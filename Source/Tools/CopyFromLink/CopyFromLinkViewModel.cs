using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;
using LemoineTools.Tools.AutoFilters;

namespace LemoineTools.Tools.CopyFromLink
{
    /// <summary>
    /// Copy Elements from Link — pick a loaded link, pick the model categories to pull, then tick the
    /// exact "Family: Type" entries found within those categories. The run copies each matched element
    /// verbatim into the host (link transform applied). An optional change-detection step stamps the
    /// outputs so a re-run reconciles instead of duplicating.
    /// </summary>
    public class CopyFromLinkViewModel : IStepFlowTool, IReviewableTool, IRunResult, IStepAware, IToolCleanup
    {
        public string Title    => AppStrings.T("copy.fromLink.title");
        public string RunLabel => AppStrings.T("copy.fromLink.runLabel");
        public string? ResultNoun => "elements";
        public IReadOnlyList<ResultChip>? ResultChips => null;

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("source",  AppStrings.T("copy.fromLink.steps.source"), required: true),
            new StepDefinition("types",   AppStrings.T("copy.fromLink.steps.types"),         required: true),
            new StepDefinition("changes", AppStrings.T("copy.fromLink.steps.changes"),         required: false),
            new StepDefinition("run",     AppStrings.T("copy.fromLink.steps.run"),             required: false),
        };

        // ── Injected ───────────────────────────────────────────────────────────
        private readonly CopyFromLinkScanHandler? _scanHandler;
        private readonly ExternalEvent?           _scanEvent;
        private readonly CopyFromLinkRunHandler?  _runHandler;
        private readonly ExternalEvent?           _runEvent;
        private readonly List<CopyFromLinkLinkInfo> _links;

        // ── State ───────────────────────────────────────────────────────────────
        private readonly CopyFromLinkSpec _spec = new CopyFromLinkSpec();
        private readonly Dictionary<string, CopyFromLinkLinkInfo> _linkByName = new Dictionary<string, CopyFromLinkLinkInfo>();
        private HashSet<string> _selectedTypeKeys = new HashSet<string>(StringComparer.Ordinal);

        // Change detection
        private bool _deletePrevious = CopyFromLinkSettings.Instance.DeletePrevious;
        private bool _onlyChanged    = CopyFromLinkSettings.Instance.OnlyChanged;
        private bool _deleteOrphans  = CopyFromLinkSettings.Instance.DeleteOrphans;

        // Scan
        private bool _scanning, _scanDone;
        private string? _scanError;
        private CopyFromLinkScanResult _scan = new CopyFromLinkScanResult();

        // Live UI handles
        private StackPanel? _worksetContainer, _typesContainer;
        private Dispatcher? _disp;
        private Action<string>? _refreshStep;

        public event EventHandler? ValidationChanged;
        private void Changed() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public void OnWindowClosed()
        {
            if (_scanHandler != null) { _scanHandler.OnScanned = null; _scanHandler.OnError = null; }
            if (_runHandler != null) { _runHandler.PushLog = null; _runHandler.OnProgress = null; _runHandler.OnComplete = null; }
        }

        public CopyFromLinkViewModel(
            CopyFromLinkScanHandler? scanHandler, ExternalEvent? scanEvent,
            CopyFromLinkRunHandler?  runHandler,  ExternalEvent?  runEvent,
            List<CopyFromLinkLinkInfo>? links)
        {
            _scanHandler = scanHandler; _scanEvent = scanEvent;
            _runHandler  = runHandler;  _runEvent  = runEvent;
            _links       = links ?? new List<CopyFromLinkLinkInfo>();

            foreach (var l in _links) _linkByName[l.Name] = l;
            if (_links.Count > 0) { _spec.LinkInstId = _links[0].LinkInstId; _spec.LinkInstUid = _links[0].LinkInstUid; }
        }

        // ═══════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "source":  return BuildSourceStep();
                case "types":   return BuildTypesStep();
                case "changes": return BuildChangesStep();
                default:        return null;   // "run" rendered by the framework (IReviewableTool)
            }
        }

        // ── Step 1: Source link, categories, worksets ────────────────────────────
        private FrameworkElement BuildSourceStep()
        {
            _disp = Dispatcher.CurrentDispatcher;
            var outer = new StackPanel();

            if (_links.Count == 0)
            {
                outer.Children.Add(Dim(AppStrings.T("copy.fromLink.labels.noLinks")));
                return outer;
            }

            outer.Children.Add(Label(AppStrings.T("copy.fromLink.labels.sourceLink")));
            var linkSelect = new SingleSelect
            {
                Items        = _links.Select(l => l.Name).ToList(),
                SelectedItem = _links.FirstOrDefault(l => l.LinkInstId == _spec.LinkInstId)?.Name ?? _links[0].Name,
            };
            linkSelect.SelectionChanged += name =>
            {
                if (name != null && _linkByName.TryGetValue(name, out var info))
                {
                    _spec.LinkInstId  = info.LinkInstId;
                    _spec.LinkInstUid = info.LinkInstUid;
                }
                _spec.ExcludedWorksetIds.Clear();
                ResetScan();
                RebuildWorksets();
                Changed();
            };
            outer.Children.Add(linkSelect);

            Divider(outer);

            outer.Children.Add(Label(AppStrings.T("copy.fromLink.labels.catsToCopy")));
            var catTabs = new MultiSelectTabs();
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

            outer.Children.Add(Label(AppStrings.T("copy.fromLink.labels.worksets")));
            _worksetContainer = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
            outer.Children.Add(_worksetContainer);
            RebuildWorksets();

            return outer;
        }

        private void RebuildWorksets()
        {
            if (_worksetContainer == null) return;
            _worksetContainer.Children.Clear();

            var link = _links.FirstOrDefault(l => l.LinkInstId == _spec.LinkInstId);
            var ws   = link?.Worksets ?? new List<CopyFromLinkWorksetInfo>();
            if (ws.Count == 0) { _worksetContainer.Children.Add(Dim(AppStrings.T("copy.fromLink.labels.noWorksets"))); return; }

            var excluded = new HashSet<int>(_spec.ExcludedWorksetIds);
            var tabs = new MultiSelectTabs();
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

        // ── Step 2: Families/types (scan-driven) ─────────────────────────────────
        private FrameworkElement BuildTypesStep()
        {
            _disp = Dispatcher.CurrentDispatcher;
            var outer = new StackPanel();
            outer.Children.Add(Label(AppStrings.T("copy.fromLink.labels.families")));
            outer.Children.Add(Dim(AppStrings.T("copy.fromLink.labels.familiesHelp")));

            _typesContainer = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            outer.Children.Add(_typesContainer);
            RebuildTypes();
            return outer;
        }

        // ── IStepAware ───────────────────────────────────────────────────────────
        public void SetContentRefreshCallback(Action<string> rebuildStepContent) => _refreshStep = rebuildStepContent;

        public void OnStepActivated(string stepId)
        {
            if (stepId != "types") return;
            _disp = Dispatcher.CurrentDispatcher;
            if (_spec.Categories.Count == 0) { _scanDone = false; RebuildTypes(); return; }
            if (!_scanDone && !_scanning) TriggerScan();
            else                          RebuildTypes();
        }

        private void RebuildTypes()
        {
            if (_typesContainer == null) return;
            _typesContainer.Children.Clear();

            if (_scanning)          { _typesContainer.Children.Add(Dim(AppStrings.T("copy.fromLink.labels.scanning"))); return; }
            if (_scanError != null) { _typesContainer.Children.Add(Dim(AppStrings.T("copy.fromLink.labels.scanError", _scanError))); return; }
            if (!_scanDone)
            {
                _typesContainer.Children.Add(Dim(_spec.Categories.Count == 0
                    ? AppStrings.T("copy.fromLink.labels.pickCatFirst")
                    : AppStrings.T("copy.fromLink.labels.confirmToScan")));
                return;
            }
            if (_scan.TypesByCategory.Count == 0)
            {
                _typesContainer.Children.Add(Dim(AppStrings.T("copy.fromLink.labels.noTypes", _scan.ElementCount)));
                return;
            }

            var allKeys = new HashSet<string>(_scan.TypesByCategory.Values.SelectMany(v => v), StringComparer.Ordinal);

            // First populate defaults to all; later rescans keep the user's picks intersected with
            // what's still available (an empty selection re-defaults to all).
            if (_selectedTypeKeys.Count == 0) _selectedTypeKeys = new HashSet<string>(allKeys, StringComparer.Ordinal);
            else _selectedTypeKeys.IntersectWith(allKeys);
            if (_selectedTypeKeys.Count == 0) _selectedTypeKeys = new HashSet<string>(allKeys, StringComparer.Ordinal);

            _typesContainer.Children.Add(Dim(AppStrings.T("copy.fromLink.labels.typesSummary", _scan.ElementCount, allKeys.Count, _scan.TypesByCategory.Count)));

            var tabs = new MultiSelectTabs();
            tabs.SelectionChanged += sel =>
            {
                _selectedTypeKeys = new HashSet<string>(sel, StringComparer.Ordinal);
                Changed();
            };
            var groups = _scan.TypesByCategory.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
            tabs.SetGroups(groups, _selectedTypeKeys.ToList());
            _typesContainer.Children.Add(tabs);
        }

        private void ResetScan()
        {
            _scanDone = false;
            _scanning = false;
            _scanError = null;
            _scan = new CopyFromLinkScanResult();
            _selectedTypeKeys.Clear();
        }

        private void TriggerScan()
        {
            if (_scanHandler == null || _scanEvent == null) return;
            if (_spec.Categories.Count == 0) { _scanDone = false; RebuildTypes(); return; }

            _scanning = true; _scanDone = false; _scanError = null;
            RebuildTypes();

            _scanHandler.ScanRequested = true;
            _scanHandler.Spec = CloneSpec();
            _scanHandler.OnScanned = result => _disp?.BeginInvoke((Action)(() =>
            {
                _scanning = false; _scanDone = true; _scan = result ?? new CopyFromLinkScanResult();
                RebuildTypes(); Changed();
            }));
            _scanHandler.OnError = err => _disp?.BeginInvoke((Action)(() =>
            {
                _scanning = false; _scanError = err;
                RebuildTypes();
            }));
            _scanEvent.Raise();
        }

        private CopyFromLinkSpec CloneSpec() => new CopyFromLinkSpec
        {
            LinkInstId         = _spec.LinkInstId,
            LinkInstUid        = _spec.LinkInstUid,
            Categories         = new List<string>(_spec.Categories),
            ExcludedWorksetIds = new List<int>(_spec.ExcludedWorksetIds),
        };

        // ── Step 3: Change Detection ─────────────────────────────────────────────
        private FrameworkElement BuildChangesStep()
        {
            var outer = new StackPanel();
            outer.Children.Add(Dim("By default each copy is a finished element — once created it is detached "
                + "from this tool and a later run never deletes it. Turn on delete-previous to keep outputs "
                + "stamped so a re-run on the same source elements replaces them instead of adding duplicates."));

            var reconcileHost = new StackPanel();
            void PopulateReconcile()
            {
                reconcileHost.Children.Clear();
                if (!_deletePrevious) return;
                var sub = new ToggleSwitches { Margin = new Thickness(0, 8, 0, 0) };
                sub.SetItems(new List<ToggleItem>
                {
                    new ToggleItem { Id = "only", Label = AppStrings.T("copy.fromLink.labels.onlyLabel"),
                        Desc = AppStrings.T("copy.fromLink.labels.onlyDesc"), DefaultOn = _onlyChanged },
                    new ToggleItem { Id = "orph", Label = AppStrings.T("copy.fromLink.labels.orphLabel"),
                        Desc = AppStrings.T("copy.fromLink.labels.orphDesc"), DefaultOn = _deleteOrphans },
                });
                sub.StateChanged += st =>
                {
                    if (st.TryGetValue("only", out var a)) _onlyChanged   = a;
                    if (st.TryGetValue("orph", out var b)) _deleteOrphans = b;
                    Changed();
                };
                reconcileHost.Children.Add(sub);
            }

            var toggles = new ToggleSwitches { Margin = new Thickness(0, 8, 0, 0) };
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "prev", Label = AppStrings.T("copy.fromLink.labels.prevLabel"),
                    Desc = AppStrings.T("copy.fromLink.labels.prevDesc"), DefaultOn = _deletePrevious },
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
            ("link", AppStrings.T("copy.fromLink.review.itemLink")), ("cats", AppStrings.T("copy.fromLink.review.itemCats")), ("types", AppStrings.T("copy.fromLink.review.itemTypes")), ("changes", AppStrings.T("copy.fromLink.review.itemChanges")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["link"]    = _links.FirstOrDefault(l => l.LinkInstId == _spec.LinkInstId)?.Name ?? "—",
            ["cats"]    = _spec.Categories.Count == 0 ? "—" : AppStrings.T("copy.fromLink.review.catsValue", _spec.Categories.Count),
            ["types"]   = _selectedTypeKeys.Count == 0 ? AppStrings.T("copy.fromLink.review.typesNone") : AppStrings.T("copy.fromLink.review.typesValue", _selectedTypeKeys.Count),
            ["changes"] = ChangesSummary(),
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => null;
        public string?        ReviewWarning => _selectedTypeKeys.Count == 0 ? AppStrings.T("copy.fromLink.review.warnNoTypes") : null;

        private string ChangesSummary() => !_deletePrevious
            ? AppStrings.T("copy.fromLink.review.changesKeep")
            : _onlyChanged ? AppStrings.T("copy.fromLink.review.changesOnlyChanged") : AppStrings.T("copy.fromLink.review.changesReCopyAll");

        // ── Validation / Summary ─────────────────────────────────────────────────
        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "source": return _spec.Categories.Count > 0;
                case "types":  return _selectedTypeKeys.Count > 0;
                default:       return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "source":
                    return _spec.Categories.Count == 0 ? "—"
                        : AppStrings.T("copy.fromLink.summaries.source", (_links.FirstOrDefault(l => l.LinkInstId == _spec.LinkInstId)?.Name ?? "link"), _spec.Categories.Count);
                case "types":
                    return _selectedTypeKeys.Count == 0 ? "—" : AppStrings.T("copy.fromLink.summaries.typeCount", _selectedTypeKeys.Count);
                case "changes":
                    return ChangesSummary();
                case "run": return AppStrings.T("copy.fromLink.summaries.run");
                default:    return "—";
            }
        }

        // ── Run ──────────────────────────────────────────────────────────────────
        public void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null) { pushLog(AppStrings.T("copy.fromLink.log.handlerMissing"), "fail"); onComplete(0, 1, 0); return; }

            SaveSettings();

            _spec.SelectedTypeKeys = _selectedTypeKeys.ToList();
            _runHandler.Spec           = _spec;
            _runHandler.DeletePrevious = _deletePrevious;
            _runHandler.OnlyChanged    = _deletePrevious && _onlyChanged;
            _runHandler.DeleteOrphans  = _deletePrevious && _deleteOrphans;
            _runHandler.PushLog    = pushLog;
            _runHandler.OnProgress = onProgress;
            _runHandler.OnComplete = onComplete;

            pushLog(AppStrings.T("copy.fromLink.log.raising"), "info");
            _runEvent.Raise();
        }

        private void SaveSettings()
        {
            var s = CopyFromLinkSettings.Instance;
            s.DeletePrevious = _deletePrevious;
            s.OnlyChanged    = _onlyChanged;
            s.DeleteOrphans  = _deleteOrphans;
            s.Save();
        }

        // ── Category grouping (mirrors Copy Linear) ──────────────────────────────
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

        // ── Small WPF helpers (theme via resource refs only) ──────────────────────
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
    }
}
