using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Tools.AutoFilters;

using WpfTextBox = System.Windows.Controls.TextBox;

namespace LemoineTools.Tools.CopyLinear
{
    /// <summary>
    /// Copy Linear Elements — one tool, two modes. Pulls linear runs (pipes, ducts, conduit,
    /// cable tray, framing…) out of a picked link, filters them by system / size / phase, then
    /// either splits each into standard-length segments or replaces it with a family placed at
    /// intervals. An optional "only changed" re-run reconciles via stamped host outputs.
    /// </summary>
    public class CopyLinearViewModel : ILemoineTool, ILemoineReviewable, ILemoineRunResult, IStepAware
    {
        public string Title    => "Copy Linear Elements";
        public string RunLabel => "Run in Revit →";
        public string? ResultNoun => _mode == "Replace" ? "instances" : "segments";
        public IReadOnlyList<ResultChip>? ResultChips => null;

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("source",    "Source",            required: true),
            new StepDefinition("filters",   "Parameter Filters", required: false),
            new StepDefinition("operation", "Operation",         required: true),
            new StepDefinition("changes",   "Change Detection",  required: false),
            new StepDefinition("run",       "Review & Run",      required: false),
        };

        // ── Injected ───────────────────────────────────────────────────────────
        private readonly CopyLinearScanHandler? _scanHandler;
        private readonly ExternalEvent?         _scanEvent;
        private readonly CopyLinearRunHandler?  _runHandler;
        private readonly ExternalEvent?         _runEvent;
        private readonly List<CopyLinearDocInfo>    _docs;
        private readonly List<CopyLinearFamilyInfo> _families;

        // ── State ───────────────────────────────────────────────────────────────
        private readonly CopyLinearSourceSpec _spec = new CopyLinearSourceSpec();
        private readonly Dictionary<string, long>   _docDisplayToId = new Dictionary<string, long>();
        private readonly Dictionary<string, CopyLinearFamilyInfo> _familyByKey = new Dictionary<string, CopyLinearFamilyInfo>();

        private string _mode = CopyLinearSettings.Instance.Mode;

        // Split params
        private double _segLenFeet = CopyLinearSettings.Instance.SegmentLengthFeet;
        private double _gapInches  = CopyLinearSettings.Instance.GapInches;
        private bool   _keepRemainder = CopyLinearSettings.Instance.KeepRemainder;

        // Replace params
        private double _intervalFeet = CopyLinearSettings.Instance.IntervalFeet;
        private double _extraSpacingInches = CopyLinearSettings.Instance.ExtraSpacingInches;
        private bool   _alignToSource = CopyLinearSettings.Instance.AlignToSource;
        private string _lengthParam = CopyLinearSettings.Instance.LengthParamName ?? "";
        private string _familyKey   = CopyLinearSettings.Instance.FamilyKey ?? "";

        // Manual placement override (Replace mode, shown when align-to-source is off) —
        // offsets are in each placement's own source-run frame, never global axes.
        private double _manualXInches = CopyLinearSettings.Instance.ManualOffsetXInches;
        private double _manualYInches = CopyLinearSettings.Instance.ManualOffsetYInches;
        private double _manualZInches = CopyLinearSettings.Instance.ManualOffsetZInches;
        private double _manualRotDeg  = CopyLinearSettings.Instance.ManualRotationDegrees;

        // Change detection
        private bool _deletePrevious = CopyLinearSettings.Instance.DeletePrevious;
        private bool _onlyChanged   = CopyLinearSettings.Instance.OnlyChanged;
        private bool _deleteOrphans = CopyLinearSettings.Instance.DeleteOrphans;

        // Scan
        private bool _scanning, _scanDone;
        private string? _scanError;
        private CopyLinearScanResult _scan = new CopyLinearScanResult();
        // Parameters the user has added a filter row for, in display order (one row each).
        private readonly List<string> _activeFilterParams = new List<string>();

        // Live UI handles
        private StackPanel? _worksetContainer, _filterContainer, _operationBody;
        private Dispatcher? _disp;
        private Action<string>? _refreshStep;   // IStepAware content-refresh callback

        public event EventHandler? ValidationChanged;
        private void Changed() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public CopyLinearViewModel(
            CopyLinearScanHandler? scanHandler, ExternalEvent? scanEvent,
            CopyLinearRunHandler?  runHandler,  ExternalEvent?  runEvent,
            List<CopyLinearDocInfo>?    docs,
            List<CopyLinearFamilyInfo>? families)
        {
            _scanHandler = scanHandler; _scanEvent = scanEvent;
            _runHandler  = runHandler;  _runEvent  = runEvent;
            _docs        = docs ?? new List<CopyLinearDocInfo>();
            _families    = families ?? new List<CopyLinearFamilyInfo>();

            foreach (var f in _families) _familyByKey[f.Key] = f;

            // Default source = first document (host).
            if (_docs.Count > 0) _spec.LinkInstId = _docs[0].LinkInstId;
        }

        // ═══════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "source":    return BuildSourceStep();
                case "filters":   return BuildFiltersStep();
                case "operation": return BuildOperationStep();
                case "changes":   return BuildChangesStep();
                default:          return null;   // "run" rendered by framework (ILemoineReviewable)
            }
        }

        // ── Step 1: Source & Filter ─────────────────────────────────────────────
        private FrameworkElement BuildSourceStep()
        {
            _disp = Dispatcher.CurrentDispatcher;
            var outer = new StackPanel();

            if (_docs.Count == 0)
            {
                outer.Children.Add(Dim("No documents available. Open the host project in Revit."));
                return outer;
            }

            // Source document (single).
            outer.Children.Add(Label("Source document"));
            _docDisplayToId.Clear();
            foreach (var d in _docs) _docDisplayToId[d.Name] = d.LinkInstId;
            var docSelect = new LemoineSingleSelect
            {
                Items        = _docs.Select(d => d.Name).ToList(),
                SelectedItem = _docs.FirstOrDefault(d => d.LinkInstId == _spec.LinkInstId)?.Name ?? _docs[0].Name,
            };
            docSelect.SelectionChanged += disp =>
            {
                if (disp != null && _docDisplayToId.TryGetValue(disp, out var id)) _spec.LinkInstId = id;
                _spec.ExcludedWorksetIds.Clear();
                ResetScan();
                RebuildWorksets();
                RebuildFilters();
                Changed();
            };
            outer.Children.Add(docSelect);

            Divider(outer);

            // Categories — all filterable Revit categories, grouped by discipline (same pattern as ClashGroupEditor).
            outer.Children.Add(Label("Categories to copy"));
            var catTabs = new LemoineMultiSelectTabs();
            catTabs.SelectionChanged += sel =>
            {
                var map = AutoFiltersSettings.KnownCategoryMap;
                _spec.Categories = sel.Select(s => map.TryGetValue(s, out var o) ? o : null)
                                      .Where(o => o != null).Cast<string>().ToList();
                ResetScan();
                Changed();
            };
            var catGroups = BuildCategoryGroups();
            var allCatDisplayNames = new HashSet<string>(catGroups.Values.SelectMany(v => v), StringComparer.Ordinal);
            var selectedCatLabels = AutoFiltersSettings.KnownCategoryMap
                .Where(kv => _spec.Categories.Contains(kv.Value) && allCatDisplayNames.Contains(kv.Key))
                .Select(kv => kv.Key).ToList();
            catTabs.SetGroups(catGroups, selectedCatLabels);
            outer.Children.Add(catTabs);

            Divider(outer);

            // Worksets (per chosen doc).
            outer.Children.Add(Label("Worksets (unchecked = excluded)"));
            _worksetContainer = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
            outer.Children.Add(_worksetContainer);
            RebuildWorksets();

            return outer;
        }

        // ── Step 2: Parameter Filters ───────────────────────────────────────────
        // Scanning the source is the result of confirming step 1 — OnStepActivated("filters")
        // kicks the scan, and this content renders the discovered parameters once it returns.
        private FrameworkElement BuildFiltersStep()
        {
            _disp = Dispatcher.CurrentDispatcher;
            var outer = new StackPanel();
            outer.Children.Add(Label("Parameter filters"));
            outer.Children.Add(Dim("Narrow the copied elements by any parameter found on the scanned source. "
                + "Add a filter, choose its parameter, then tick the values to keep. No filters = copy every matched element."));

            _filterContainer = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            outer.Children.Add(_filterContainer);
            RebuildFilters();
            return outer;
        }

        // ── IStepAware ───────────────────────────────────────────────────────────
        public void SetContentRefreshCallback(Action<string> rebuildStepContent) => _refreshStep = rebuildStepContent;

        public void OnStepActivated(string stepId)
        {
            if (stepId != "filters") return;
            _disp = Dispatcher.CurrentDispatcher;
            // Confirming the Source step lands here — scan now if we have categories and no fresh result.
            if (_spec.Categories.Count == 0) { _scanDone = false; RebuildFilters(); return; }
            if (!_scanDone && !_scanning) TriggerScan();
            else                          RebuildFilters();
        }

        private void RebuildWorksets()
        {
            if (_worksetContainer == null) return;
            _worksetContainer.Children.Clear();

            var doc = _docs.FirstOrDefault(d => d.LinkInstId == _spec.LinkInstId);
            var ws  = doc?.Worksets ?? new List<CopyLinearWorksetInfo>();
            if (ws.Count == 0) { _worksetContainer.Children.Add(Dim("No user worksets in this document.")); return; }

            var excluded = new HashSet<int>(_spec.ExcludedWorksetIds);
            var tabs = new LemoineMultiSelectTabs();
            var byName = ws.ToDictionary(w => w.Name, w => w.Id);
            tabs.SelectionChanged += sel =>
            {
                var includedIds = new HashSet<int>(sel.Where(byName.ContainsKey).Select(n => byName[n]));
                _spec.ExcludedWorksetIds = ws.Where(w => !includedIds.Contains(w.Id)).Select(w => w.Id).ToList();
                ResetScan();
                Changed();
            };
            var included = ws.Where(w => !excluded.Contains(w.Id)).Select(w => w.Name).ToList();
            tabs.SetGroups(new Dictionary<string, List<string>> { { "Worksets", ws.Select(w => w.Name).ToList() } }, included);
            _worksetContainer.Children.Add(tabs);
        }

        private void RebuildFilters()
        {
            if (_filterContainer == null) return;
            _filterContainer.Children.Clear();

            if (_scanning)        { _filterContainer.Children.Add(Dim("Scanning source…")); return; }
            if (_scanError != null) { _filterContainer.Children.Add(Dim($"Scan error: {_scanError}")); return; }
            if (!_scanDone)
            {
                _filterContainer.Children.Add(Dim(_spec.Categories.Count == 0
                    ? "Pick at least one category in the Source step, then return here."
                    : "Confirm the Source step to scan for parameters."));
                return;
            }
            if (_scan.ParameterValues.Count == 0)
            {
                _filterContainer.Children.Add(Dim($"{_scan.ElementCount} element(s) found — no parameters with 2+ distinct values to filter on."));
                return;
            }

            _filterContainer.Children.Add(Dim($"{_scan.ElementCount} element(s) · {_scan.ParameterValues.Count} filterable parameter(s)."));

            // Prune filter state for params no longer present in this scan.
            _activeFilterParams.RemoveAll(p => !_scan.ParameterValues.ContainsKey(p));
            foreach (var k in _spec.ParamFilters.Keys.Where(k => !_scan.ParameterValues.ContainsKey(k)).ToList())
                _spec.ParamFilters.Remove(k);

            foreach (var paramName in _activeFilterParams.ToList())
                _filterContainer.Children.Add(BuildFilterRow(paramName));

            // "Add filter" — only while some parameter is still unused (one row per parameter).
            var used = new HashSet<string>(_activeFilterParams, StringComparer.Ordinal);
            if (!_scan.ParameterValues.Keys.Any(k => !used.Contains(k))) return;

            var addBtn = LemoineControlStyles.BuildButton("+ Add filter");
            addBtn.Margin = new Thickness(0, 8, 0, 0);
            addBtn.Click += (s, e) =>
            {
                var firstAvail = _scan.ParameterValues.Keys
                    .Where(k => !_activeFilterParams.Contains(k))
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (firstAvail == null) return;
                _activeFilterParams.Add(firstAvail);
                RebuildFilters();
                Changed();
            };
            _filterContainer.Children.Add(addBtn);
        }

        // One filter = a bordered card holding the parameter dropdown AND the value checkbox list
        // together (same visual). The checkbox list renders immediately from the dropdown's current
        // selection — it never waits on a SelectionChanged that may not fire for the default item.
        private FrameworkElement BuildFilterRow(string initialParam)
        {
            string currentParam = initialParam;

            var card = new Border { Padding = new Thickness(10), Margin = new Thickness(0, 6, 0, 0) };
            card.SetResourceReference(Border.BorderBrushProperty,   "LemoineBorder");
            card.SetResourceReference(Border.CornerRadiusProperty,  "LemoineRadius_Card");
            card.BorderThickness = new Thickness(1);
            var inner = new StackPanel();
            card.Child = inner;

            // Header: parameter dropdown + remove. The dropdown lists every scanned parameter
            // except those already used by OTHER rows, so each parameter appears at most once.
            var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var others  = new HashSet<string>(_activeFilterParams.Where(p => p != currentParam), StringComparer.Ordinal);
            var options = _scan.ParameterValues.Keys
                .Where(k => !others.Contains(k))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var picker = new LemoineSingleSelect { Label = "Parameter", Items = options };
            picker.SelectedItem = currentParam;   // safe before the handler is wired
            Grid.SetColumn(picker, 0);
            header.Children.Add(picker);

            var removeBtn = new Button
            {
                Content = "×", Padding = new Thickness(8, 1, 8, 1),
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, 0, 0),
            };
            removeBtn.SetResourceReference(Button.FontSizeProperty,   "LemoineFS_MD");
            removeBtn.SetResourceReference(Button.ForegroundProperty, "LemoineTextDim");
            removeBtn.SetResourceReference(Button.FontFamilyProperty, "LemoineUiFont");
            removeBtn.Click += (s, e) =>
            {
                _activeFilterParams.Remove(currentParam);
                _spec.ParamFilters.Remove(currentParam);
                RebuildFilters();
                Changed();
            };
            Grid.SetColumn(removeBtn, 1);
            header.Children.Add(removeBtn);
            inner.Children.Add(header);

            // Value checkbox list — rebuilt in place whenever the dropdown changes parameter.
            var valueHost = new StackPanel();
            inner.Children.Add(valueHost);
            LoadFilterValues(valueHost, currentParam);

            picker.SelectionChanged += param =>
            {
                if (string.IsNullOrEmpty(param) || param == currentParam) return;
                // Move this row to a different parameter: drop the old key, claim the new one.
                int idx = _activeFilterParams.IndexOf(currentParam);
                _spec.ParamFilters.Remove(currentParam);
                currentParam = param!;
                if (idx >= 0) _activeFilterParams[idx] = currentParam;
                RebuildFilters();   // re-render so every row's dropdown excludes the new pick
                Changed();
            };

            return card;
        }

        // Builds the value checkbox list for one parameter into the supplied host panel.
        private void LoadFilterValues(StackPanel host, string paramName)
        {
            host.Children.Clear();
            var values   = _scan.ParameterValues.TryGetValue(paramName, out var v)   ? v   : new List<string>();
            var selected = _spec.ParamFilters.TryGetValue(paramName,    out var sel) ? sel : new List<string>();

            var tabs = new LemoineMultiSelectTabs();
            tabs.SelectionChanged += picked => { _spec.ParamFilters[paramName] = picked.ToList(); Changed(); };
            var initial = selected.Where(values.Contains).ToList();
            _spec.ParamFilters[paramName] = initial;
            tabs.SetGroups(new Dictionary<string, List<string>> { { paramName, values } }, initial);
            host.Children.Add(tabs);
        }

        private void ResetScan()
        {
            _scanDone = false;
            _scanning = false;
            _scanError = null;
            _activeFilterParams.Clear();
            _spec.ParamFilters.Clear();
        }

        private void TriggerScan()
        {
            if (_scanHandler == null || _scanEvent == null) return;
            if (_spec.Categories.Count == 0) { _scanDone = false; RebuildFilters(); return; }

            _scanning = true; _scanDone = false; _scanError = null;
            RebuildFilters();   // show "Scanning…" in the live container

            _scanHandler.Spec = CloneSpecForScan();
            _scanHandler.OnScanned = result => _disp?.BeginInvoke((Action)(() =>
            {
                _scanning = false; _scanDone = true; _scan = result ?? new CopyLinearScanResult();
                RebuildFilters(); Changed();
            }));
            _scanHandler.OnError = err => _disp?.BeginInvoke((Action)(() =>
            {
                _scanning = false; _scanError = err;
                RebuildFilters();
            }));
            _scanEvent.Raise();
        }

        private CopyLinearSourceSpec CloneSpecForScan() => new CopyLinearSourceSpec
        {
            LinkInstId         = _spec.LinkInstId,
            Categories         = new List<string>(_spec.Categories),
            ExcludedWorksetIds = new List<int>(_spec.ExcludedWorksetIds),
        };

        // ── Step 2: Operation ───────────────────────────────────────────────────
        private FrameworkElement BuildOperationStep()
        {
            var outer = new StackPanel();
            outer.Children.Add(Label("Operation"));
            var modeSelect = new LemoineSingleSelect
            {
                Items        = new List<string> { "Split into segments", "Replace with family" },
                SelectedItem = _mode == "Replace" ? "Replace with family" : "Split into segments",
            };
            modeSelect.SelectionChanged += v =>
            {
                _mode = v == "Replace with family" ? "Replace" : "Split";
                PopulateOperationBody();
                Changed();
            };
            outer.Children.Add(modeSelect);

            Divider(outer);
            _operationBody = new StackPanel();
            outer.Children.Add(_operationBody);
            PopulateOperationBody();
            return outer;
        }

        private void PopulateOperationBody()
        {
            if (_operationBody == null) return;
            _operationBody.Children.Clear();
            if (_mode == "Replace") BuildReplaceBody(_operationBody);
            else                    BuildSplitBody(_operationBody);
        }

        private void BuildSplitBody(StackPanel body)
        {
            body.Children.Add(Label("Segment length (feet)"));
            body.Children.Add(Stepper(_segLenFeet, 0.5, 1000, 1, 1, v => _segLenFeet = v));

            body.Children.Add(Label("Gap between pieces (inches)"));
            body.Children.Add(Stepper(_gapInches, 0, 48, 0.25, 2, v => _gapInches = v));

            var toggles = new LemoineToggleSwitches { Margin = new Thickness(0, 10, 0, 0) };
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "rem", Label = "Keep final remainder piece",
                    Desc = "Off = drop a trailing piece shorter than the segment length.", DefaultOn = _keepRemainder },
            });
            toggles.StateChanged += st => { if (st.TryGetValue("rem", out var on)) { _keepRemainder = on; Changed(); } };
            body.Children.Add(toggles);
        }

        private void BuildReplaceBody(StackPanel body)
        {
            body.Children.Add(Label("Family type to place"));
            if (_families.Count == 0)
                body.Children.Add(Dim("No placeable family types found in the host model."));
            else
            {
                if (!_familyByKey.ContainsKey(_familyKey)) _familyKey = _families[0].Key;
                var famSearch = new LemoineSearchAutocomplete
                {
                    Items          = _families.Select(f => f.Key).ToList(),
                    Placeholder    = "Search family types…",
                    MaxSuggestions = 12,
                    Value          = _familyKey,
                };
                // Fires per keystroke with the raw text — only an exact item is a valid pick,
                // so a partial query simply invalidates the step until one is chosen.
                famSearch.SelectionChanged += v =>
                {
                    _familyKey = v != null && _familyByKey.ContainsKey(v) ? v : "";
                    Changed();
                };
                body.Children.Add(famSearch);
            }

            body.Children.Add(Label("Placement interval (feet)"));
            body.Children.Add(Stepper(_intervalFeet, 0.25, 1000, 1, 2, v => _intervalFeet = v));

            body.Children.Add(Label("Extra spacing (inches)"));
            body.Children.Add(Stepper(_extraSpacingInches, 0, 120, 0.25, 2, v => _extraSpacingInches = v));

            body.Children.Add(Label("Length parameter to set (optional)"));
            var box = TextBox(_lengthParam);
            box.TextChanged += (s, e) => { _lengthParam = ((WpfTextBox)s).Text; };
            body.Children.Add(box);

            // Every instance is always rotated to its run direction; the toggle only governs
            // the box-based alignment pass on top of that. Unchecking expands the manual
            // placement override below, which takes control instead.
            var manualHost = new StackPanel();
            void PopulateManual()
            {
                manualHost.Children.Clear();
                if (_alignToSource) return;
                manualHost.Children.Add(Dim("Manual placement override — applied to every instance relative to "
                    + "its own source element's run (X = along the run, Y = sideways, Z = up), instead of the "
                    + "automatic alignment."));
                manualHost.Children.Add(Label("Move X — along run (inches)"));
                manualHost.Children.Add(Stepper(_manualXInches, -1200, 1200, 1, 2, v => _manualXInches = v));
                manualHost.Children.Add(Label("Move Y — side (inches)"));
                manualHost.Children.Add(Stepper(_manualYInches, -1200, 1200, 1, 2, v => _manualYInches = v));
                manualHost.Children.Add(Label("Move Z — up (inches)"));
                manualHost.Children.Add(Stepper(_manualZInches, -1200, 1200, 1, 2, v => _manualZInches = v));
                manualHost.Children.Add(Label("Rotation about placement point (degrees)"));
                manualHost.Children.Add(Stepper(_manualRotDeg, -360, 360, 5, 1, v => _manualRotDeg = v));
            }

            var toggles = new LemoineToggleSwitches { Margin = new Thickness(0, 10, 0, 0) };
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "align", Label = "Align each instance to its source element",
                    Desc = "After rotating to the run, each placement is corrected relative to its own source element's box — fixes Z/side offsets. Line-based families are skipped. Off = set the offsets yourself below.", DefaultOn = _alignToSource },
            });
            toggles.StateChanged += st =>
            {
                if (!st.TryGetValue("align", out var al)) return;
                _alignToSource = al;
                PopulateManual();
                Changed();
            };
            body.Children.Add(toggles);
            body.Children.Add(manualHost);
            PopulateManual();
        }

        // ── Step 3: Change Detection ────────────────────────────────────────────
        private FrameworkElement BuildChangesStep()
        {
            var outer = new StackPanel();
            outer.Children.Add(Dim("By default each run's outputs are finished elements — once created they are "
                + "detached from this tool and a later run never deletes them. Turn on delete-previous to keep "
                + "outputs stamped so a re-run on the same source elements replaces them instead of adding duplicates."));

            // The reconciliation toggles only mean anything in delete-previous mode — they
            // expand beneath the master toggle when it is on.
            var reconcileHost = new StackPanel();
            void PopulateReconcile()
            {
                reconcileHost.Children.Clear();
                if (!_deletePrevious) return;
                var sub = new LemoineToggleSwitches { Margin = new Thickness(0, 8, 0, 0) };
                sub.SetItems(new List<ToggleItem>
                {
                    new ToggleItem { Id = "only", Label = "Only process elements changed since last run",
                        Desc = "On = leave unchanged runs untouched and rebuild only new/moved/resized ones. Off = rebuild every matched run.", DefaultOn = _onlyChanged },
                    new ToggleItem { Id = "orph", Label = "Delete outputs whose source no longer matches",
                        Desc = "Removes stamped segments/instances when their source run is gone or no longer passes the filters.", DefaultOn = _deleteOrphans },
                });
                sub.StateChanged += st =>
                {
                    if (st.TryGetValue("only", out var a)) _onlyChanged   = a;
                    if (st.TryGetValue("orph", out var b)) _deleteOrphans = b;
                    Changed();
                };
                reconcileHost.Children.Add(sub);
            }

            var toggles = new LemoineToggleSwitches { Margin = new Thickness(0, 8, 0, 0) };
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "prev", Label = "Delete previous run outputs",
                    Desc = "On = outputs from earlier runs of the same source elements are deleted and rebuilt, and this run's outputs stay linked for the next one. Off = nothing from previous runs is touched and this run's outputs are left as plain elements.", DefaultOn = _deletePrevious },
            });
            toggles.StateChanged += st =>
            {
                if (!st.TryGetValue("prev", out var p)) return;
                _deletePrevious = p;
                PopulateReconcile();
                Changed();
            };
            outer.Children.Add(toggles);
            outer.Children.Add(reconcileHost);
            PopulateReconcile();
            return outer;
        }

        // ── Review ──────────────────────────────────────────────────────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("doc", "Source"), ("cats", "Categories"), ("filters", "Filters"),
            ("mode", "Operation"), ("params", "Settings"), ("changes", "Change Detection"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["doc"]     = _docs.FirstOrDefault(d => d.LinkInstId == _spec.LinkInstId)?.Name ?? "—",
            ["cats"]    = _spec.Categories.Count == 0 ? "—" : $"{_spec.Categories.Count} category(ies)",
            ["filters"] = FilterSummary(),
            ["mode"]    = _mode == "Replace" ? "Replace with family" : "Split into segments",
            ["params"]  = _mode == "Replace"
                ? $"every {_intervalFeet:0.##} ft" + (_extraSpacingInches > 0 ? $" +{_extraSpacingInches:0.##}\" " : "")
                    + (_alignToSource ? " · aligned to source" : HasManualOverride ? " · manual override" : "")
                : $"{_segLenFeet:0.##} ft" + (_gapInches > 0 ? $", {_gapInches:0.##}\" gap" : ""),
            ["changes"] = ChangesSummary(),
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => null;
        public string?        ReviewWarning => _mode == "Replace" && string.IsNullOrEmpty(_familyKey)
            ? "Pick a family type for Replace mode." : null;

        private string FilterSummary()
        {
            int active = _spec.ParamFilters.Count(kv => kv.Value?.Count > 0);
            return active == 0 ? "All" : $"{active} param filter(s)";
        }

        private bool HasManualOverride =>
            Math.Abs(_manualXInches) > 1e-9 || Math.Abs(_manualYInches) > 1e-9
            || Math.Abs(_manualZInches) > 1e-9 || Math.Abs(_manualRotDeg) > 1e-9;

        private string ChangesSummary() => !_deletePrevious
            ? "Keep previous outputs"
            : _onlyChanged ? "Delete previous · only changed" : "Delete previous · rebuild all";

        // ── Validation / Summary ────────────────────────────────────────────────
        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "source":    return _spec.Categories.Count > 0;
                case "operation": return _mode == "Replace"
                                       ? (_familyByKey.ContainsKey(_familyKey) && _intervalFeet > 0)
                                       : _segLenFeet > 0;
                default:          return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "source":
                    return _spec.Categories.Count == 0 ? "—"
                        : $"{(_docs.FirstOrDefault(d => d.LinkInstId == _spec.LinkInstId)?.Name ?? "source")} · {_spec.Categories.Count} cat";
                case "filters":
                    return FilterSummary();
                case "operation":
                    return _mode == "Replace" ? $"Replace · every {_intervalFeet:0.##} ft" : $"Split · {_segLenFeet:0.##} ft";
                case "changes":
                    return ChangesSummary();
                case "run": return "Ready to run";
                default:    return "—";
            }
        }

        // ── Run ──────────────────────────────────────────────────────────────────
        public void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null) { pushLog("Run handler not registered.", "fail"); onComplete(0, 1, 0); return; }

            SaveSettings();

            _runHandler.Spec              = _spec;
            _runHandler.Mode              = _mode;
            _runHandler.SegmentLengthFeet = _segLenFeet;
            _runHandler.GapFeet           = _gapInches / 12.0;
            _runHandler.KeepRemainder     = _keepRemainder;
            _runHandler.IntervalFeet      = _intervalFeet;
            _runHandler.ExtraSpacingFeet  = _extraSpacingInches / 12.0;
            _runHandler.AlignToSource     = _alignToSource;
            _runHandler.LengthParamName   = _lengthParam;
            _runHandler.SymbolId          = _familyByKey.TryGetValue(_familyKey, out var f) ? f.SymbolId : 0L;
            // Manual override only takes control when auto-align is off.
            _runHandler.ManualOffsetAlongFeet = _alignToSource ? 0.0 : _manualXInches / 12.0;
            _runHandler.ManualOffsetSideFeet  = _alignToSource ? 0.0 : _manualYInches / 12.0;
            _runHandler.ManualOffsetUpFeet    = _alignToSource ? 0.0 : _manualZInches / 12.0;
            _runHandler.ManualRotationRad     = _alignToSource ? 0.0 : _manualRotDeg * Math.PI / 180.0;
            _runHandler.DeletePrevious    = _deletePrevious;
            _runHandler.OnlyChanged       = _deletePrevious && _onlyChanged;
            _runHandler.DeleteOrphans     = _deletePrevious && _deleteOrphans;
            _runHandler.PushLog    = pushLog;
            _runHandler.OnProgress = onProgress;
            _runHandler.OnComplete = onComplete;

            pushLog("Raising Revit ExternalEvent…", "info");
            _runEvent.Raise();
        }

        private void SaveSettings()
        {
            var s = CopyLinearSettings.Instance;
            s.Mode = _mode;
            s.SegmentLengthFeet = _segLenFeet; s.GapInches = _gapInches; s.KeepRemainder = _keepRemainder;
            s.IntervalFeet = _intervalFeet; s.ExtraSpacingInches = _extraSpacingInches;
            s.AlignToSource = _alignToSource;
            s.ManualOffsetXInches = _manualXInches; s.ManualOffsetYInches = _manualYInches;
            s.ManualOffsetZInches = _manualZInches; s.ManualRotationDegrees = _manualRotDeg;
            s.LengthParamName = _lengthParam; s.FamilyKey = _familyKey;
            s.DeletePrevious = _deletePrevious;
            s.OnlyChanged = _onlyChanged; s.DeleteOrphans = _deleteOrphans;
            s.Save();
        }

        // ── Category grouping (mirrors ClashGroupEditor) ──────────────────────────
        private static Dictionary<string, List<string>> BuildCategoryGroups()
        {
            var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var kv in AutoFiltersSettings.KnownCategoryMap)
            {
                string disc = DisciplineOf(kv.Value);
                if (!groups.ContainsKey(disc)) groups[disc] = new List<string>();
                groups[disc].Add(kv.Key);
            }
            foreach (var k in groups.Keys.ToList())
                groups[k].Sort(StringComparer.OrdinalIgnoreCase);
            return groups;
        }

        private static string DisciplineOf(string ost)
        {
            bool C(params string[] needles) => needles.Any(n => ost.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
            if (C("Duct", "MechanicalEquipment", "FlexDuct"))                                      return "Mechanical";
            if (C("Pipe", "Plumbing", "Sprinkler", "FlexPipe", "FabricationPipe",
                  "FabricationHangers", "FabricationContainment"))                                  return "Piping";
            if (C("Cable", "Conduit", "Electrical", "Lighting", "Communication",
                  "FireAlarm", "Security", "Data", "Telephone", "NurseCall"))                       return "Electrical";
            if (C("Structural", "Rebar", "Reinforcement", "Fabric"))                                return "Structural";
            return "Architectural / Other";
        }

        // ── Small WPF helpers (theme via resource refs only) ─────────────────────
        private static TextBlock Label(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 6, 0, 4) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static TextBlock Dim(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static void Divider(StackPanel parent)
        {
            var sep = new System.Windows.Shapes.Rectangle { Height = 1, Margin = new Thickness(0, 8, 0, 8) };
            sep.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "LemoineBorder");
            parent.Children.Add(sep);
        }

        private static LemoineInlineStepper Stepper(double value, double min, double max, double step, int decimals, Action<double> onChange)
        {
            var st = new LemoineInlineStepper { MinValue = min, MaxValue = max, Step = step, Decimals = decimals, Value = value };
            st.ValueChanged += (s, v) => onChange(v);
            return st;
        }

        private static WpfTextBox TextBox(string text)
        {
            var box = new WpfTextBox { Text = text ?? "", Padding = new Thickness(8, 4, 8, 4), BorderThickness = new Thickness(1) };
            box.SetResourceReference(WpfTextBox.MinHeightProperty,   "LemoineH_Input");
            box.SetResourceReference(WpfTextBox.BackgroundProperty,  "LemoineSelectBg");
            box.SetResourceReference(WpfTextBox.ForegroundProperty,  "LemoineText");
            box.SetResourceReference(WpfTextBox.BorderBrushProperty, "LemoineBorderMid");
            box.SetResourceReference(WpfTextBox.FontFamilyProperty,  "LemoineUiFont");
            box.SetResourceReference(WpfTextBox.FontSizeProperty,    "LemoineFS_MD");
            return box;
        }
    }
}
