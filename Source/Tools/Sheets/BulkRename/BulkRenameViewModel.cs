using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;

namespace LemoineTools.Tools.LinkViews.BulkRename
{
    /// <summary>
    /// Bulk-renames sheets or views using one of four operations
    /// (Find&amp;Replace, Prefix/Suffix, Sequential numbering, Token pattern).
    /// Sheets can rewrite Number or Name; views — which have no sheet number —
    /// rewrite Name only. A live preview (shared with the run handler through
    /// <see cref="BulkRenameEngine.Plan"/>) shows exactly what will be written.
    /// </summary>
    public class BulkRenameViewModel : IStepFlowTool, IReviewableTool, IStepAware, IToolCleanup
    {
        // ── Identity ──────────────────────────────────────────────────
        public string Title    => AppStrings.T("linkviews.bulkRename.title");
        public string RunLabel => AppStrings.T("linkviews.bulkRename.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", AppStrings.T("linkviews.bulkRename.steps.S1"),          required: true),
            new StepDefinition("S2", AppStrings.T("linkviews.bulkRename.steps.S2"),    required: true),
            new StepDefinition("S3", AppStrings.T("linkviews.bulkRename.steps.S3"), required: true),
            new StepDefinition("S4", AppStrings.T("linkviews.bulkRename.steps.S4"),    required: false),
        };

        // ── Entry types passed from the Command (main thread) ─────────
        public sealed class SheetEntry
        {
            public ElementId Id     { get; set; } = ElementId.InvalidElementId;
            public string    Number { get; set; } = "";
            public string    Name   { get; set; } = "";
        }

        public sealed class ViewEntry
        {
            public ElementId Id        { get; set; } = ElementId.InvalidElementId;
            public string    Name      { get; set; } = "";
            public string    TypeLabel { get; set; } = "";
        }

        // ── Mode labels (display ↔ enum) ──────────────────────────────
        private static readonly (string Label, RenameMode Mode)[] Modes =
        {
            (AppStrings.T("linkviews.bulkRename.labels.modeFindReplace"),      RenameMode.FindReplace),
            (AppStrings.T("linkviews.bulkRename.labels.modePrefixSuffix"), RenameMode.PrefixSuffix),
            (AppStrings.T("linkviews.bulkRename.labels.modeSequential"), RenameMode.Sequential),
            (AppStrings.T("linkviews.bulkRename.labels.modeToken"),       RenameMode.Token),
        };
        private static string LabelFor(RenameMode m) => Modes.First(x => x.Mode == m).Label;
        private static RenameMode ModeFromLabel(string? l) =>
            Modes.FirstOrDefault(x => x.Label == l).Label == null
                ? RenameMode.FindReplace
                : Modes.First(x => x.Label == l).Mode;

        private static readonly string FieldNumber = AppStrings.T("linkviews.bulkRename.labels.fieldSheetNumber");
        private static readonly string FieldName   = AppStrings.T("linkviews.bulkRename.labels.fieldSheetName");

        // ── State ──────────────────────────────────────────────────────
        private readonly List<SheetEntry>  _sheets;
        private readonly List<ViewEntry>   _views;
        private readonly BrowserTree _browserTree;

        private RenameTarget _target = RenameTarget.Sheets;
        private RenameField  _field  = RenameField.Name;
        private readonly RenameConfig _config = new RenameConfig();

        private readonly HashSet<long> _selectedSheetIds = new HashSet<long>();
        private readonly HashSet<long> _selectedViewIds  = new HashSet<long>();

        // Live S3 sub-panels (rebuilt in place when inputs change)
        private StackPanel? _modeHost;
        private StackPanel? _previewList;
        private TextBlock?  _previewCount;

        // ── ExternalEvent wiring ───────────────────────────────────────
        private readonly BulkRenameRunHandler? _runHandler;
        private readonly ExternalEvent?        _runEvent;

        public event EventHandler? ValidationChanged;

        // Null the callbacks parked on the static handler so this VM isn't retained after close.
        public void OnWindowClosed()
        {
            if (_runHandler == null) return;
            _runHandler.PushLog    = null;
            _runHandler.OnProgress = null;
            _runHandler.OnComplete = null;
        }

        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public BulkRenameViewModel(
            BulkRenameRunHandler? runHandler, ExternalEvent? runEvent,
            List<SheetEntry>? sheets, List<ViewEntry>? views,
            BrowserTree? browserTree = null)
        {
            _runHandler  = runHandler;
            _runEvent    = runEvent;
            _sheets      = sheets ?? new List<SheetEntry>();
            _views       = views  ?? new List<ViewEntry>();
            _browserTree = browserTree ?? new BrowserTree();
        }

        // ═══════════════════════════════════════════════════════════════
        // IStepAware — S2 depends on target; S3 depends on selection/target/field
        // ═══════════════════════════════════════════════════════════════
        private Action<string>? _refreshStep;
        public void SetContentRefreshCallback(Action<string> rebuildStepContent) => _refreshStep = rebuildStepContent;

        public void OnStepActivated(string stepId)
        {
            if (stepId == "S2" || stepId == "S3") _refreshStep?.Invoke(stepId);
        }

        // ═══════════════════════════════════════════════════════════════
        // GetStepContent
        // ═══════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "S1") return BuildTargetPicker();
            if (stepId == "S2") return BuildItemPicker();
            if (stepId == "S3") return BuildOperation();
            return null; // S4 rendered by the framework (IReviewableTool)
        }

        // ── S1: Target ─────────────────────────────────────────────────
        private FrameworkElement BuildTargetPicker()
        {
            var outer = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            outer.Children.Add(SectionHeader(AppStrings.T("linkviews.bulkRename.labels.headerTarget")));

            var select = new SingleSelect
            {
                Width = 200,
                Items = new[] { AppStrings.T("linkviews.bulkRename.labels.targetSheets"), AppStrings.T("linkviews.bulkRename.labels.targetViews") },
                SelectedItem = _target == RenameTarget.Sheets ? AppStrings.T("linkviews.bulkRename.labels.targetSheets") : AppStrings.T("linkviews.bulkRename.labels.targetViews"),
                AccessibleName = AppStrings.T("linkviews.bulkRename.labels.a11yTarget"),
            };
            select.SelectionChanged += v =>
            {
                if (string.IsNullOrEmpty(v)) return;
                _target = v == AppStrings.T("linkviews.bulkRename.labels.targetViews") ? RenameTarget.Views : RenameTarget.Sheets;
                if (_target == RenameTarget.Views) _field = RenameField.Name;
                _refreshStep?.Invoke("S2");
                _refreshStep?.Invoke("S3");
                OnValidationChanged();
            };
            outer.Children.Add(select);

            var hint = BodyHint(AppStrings.T("linkviews.bulkRename.labels.hintTarget"));
            outer.Children.Add(hint);
            return outer;
        }

        // ── S2: Select items ───────────────────────────────────────────
        private FrameworkElement BuildItemPicker()
        {
            var picker = new BrowserTreePicker
            {
                Height         = 300,
                AccessibleName = AppStrings.T("linkviews.bulkRename.labels.itemsPicker"),
            };
            // Subscribe BEFORE SetTree — the single SelectionChanged fired at the end
            // of SetTree is what re-seeds the mirror sets on step rebuild.
            picker.SelectionChanged += ids =>
            {
                var set = _target == RenameTarget.Sheets ? _selectedSheetIds : _selectedViewIds;
                set.Clear();
                foreach (var id in ids) set.Add(id);
                OnValidationChanged();
            };

            var eligible = _target == RenameTarget.Sheets
                ? _sheets.Select(s => s.Id.Value)
                : _views.Select(v => v.Id.Value);
            // Copy: SetTree's end-of-setup SelectionChanged clears+refills this same set.
            picker.SetTree(_browserTree, eligible, SelectedIds().ToList());

            var outer = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            outer.Children.Add(picker);
            return outer;
        }

        // ── S3: Field & Operation ──────────────────────────────────────
        private FrameworkElement BuildOperation()
        {
            var outer = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };

            // Field selector (sheets only)
            if (_target == RenameTarget.Sheets)
            {
                outer.Children.Add(SectionHeader(AppStrings.T("linkviews.bulkRename.labels.headerField")));
                var fieldSelect = new SingleSelect
                {
                    Width = 200,
                    Items = new[] { FieldName, FieldNumber },
                    SelectedItem = _field == RenameField.Number ? FieldNumber : FieldName,
                    AccessibleName = AppStrings.T("linkviews.bulkRename.labels.a11yField"),
                };
                fieldSelect.SelectionChanged += v =>
                {
                    _field = v == FieldNumber ? RenameField.Number : RenameField.Name;
                    RebuildPreview();
                    OnValidationChanged();
                };
                outer.Children.Add(fieldSelect);
            }
            else
            {
                outer.Children.Add(BodyHint(AppStrings.T("linkviews.bulkRename.labels.renamingView")));
            }

            // Mode selector
            outer.Children.Add(SectionHeader(AppStrings.T("linkviews.bulkRename.labels.headerOperation"), topMargin: 14));
            var modeSelect = new SingleSelect
            {
                Width = 220,
                Items = Modes.Select(m => m.Label).ToArray(),
                SelectedItem = LabelFor(_config.Mode),
                AccessibleName = AppStrings.T("linkviews.bulkRename.labels.a11yOperation"),
            };
            modeSelect.SelectionChanged += v =>
            {
                _config.Mode = ModeFromLabel(v);
                BuildModeInputs();
                RebuildPreview();
                OnValidationChanged();
            };
            outer.Children.Add(modeSelect);

            // Mode inputs (swapped in place)
            _modeHost = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            outer.Children.Add(_modeHost);
            BuildModeInputs();

            // Preview
            var sep = new System.Windows.Shapes.Rectangle { Height = 1, Margin = new Thickness(0, 14, 0, 10) };
            sep.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "LemoineBorder");
            outer.Children.Add(sep);

            outer.Children.Add(SectionHeader(AppStrings.T("linkviews.bulkRename.labels.headerPreview")));
            _previewCount = BodyHint("");
            outer.Children.Add(_previewCount);

            var previewBorder = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
                Padding         = new Thickness(10, 6, 10, 6),
                Margin          = new Thickness(0, 4, 0, 0),
            };
            previewBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            previewBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _previewList = new StackPanel();
            previewBorder.Child = _previewList;
            outer.Children.Add(previewBorder);

            RebuildPreview();
            return outer;
        }

        // ── S3: mode-specific inputs ───────────────────────────────────
        private void BuildModeInputs()
        {
            if (_modeHost == null) return;
            _modeHost.Children.Clear();

            switch (_config.Mode)
            {
                case RenameMode.FindReplace:
                {
                    var find = new TextField { Label = AppStrings.T("linkviews.bulkRename.labels.findLabel"), Placeholder = AppStrings.T("linkviews.bulkRename.labels.findPlaceholder"), Text = _config.Find };
                    find.TextChanged += t => { _config.Find = t; RebuildPreview(); OnValidationChanged(); };
                    _modeHost.Children.Add(find);

                    var repl = new TextField { Label = AppStrings.T("linkviews.bulkRename.labels.replaceLabel"), Placeholder = AppStrings.T("linkviews.bulkRename.labels.replacePlaceholder"), Text = _config.Replace, Margin = new Thickness(0, 8, 0, 0) };
                    repl.TextChanged += t => { _config.Replace = t; RebuildPreview(); OnValidationChanged(); };
                    _modeHost.Children.Add(repl);

                    var toggles = new ToggleSwitches { AccessibleName = AppStrings.T("linkviews.bulkRename.labels.findOptions"), Margin = new Thickness(0, 8, 0, 0) };
                    toggles.SetItems(new List<ToggleItem>
                    {
                        new ToggleItem { Id = "case",  Label = AppStrings.T("linkviews.bulkRename.labels.caseSensitive"),          DefaultOn = _config.CaseSensitive },
                        new ToggleItem { Id = "whole", Label = AppStrings.T("linkviews.bulkRename.labels.wholeField"),   DefaultOn = _config.WholeField },
                    });
                    toggles.StateChanged += d =>
                    {
                        if (d.TryGetValue("case",  out var c)) _config.CaseSensitive = c;
                        if (d.TryGetValue("whole", out var w)) _config.WholeField   = w;
                        RebuildPreview();
                        OnValidationChanged();
                    };
                    _modeHost.Children.Add(toggles);
                    break;
                }

                case RenameMode.PrefixSuffix:
                {
                    var prefix = new TextField { Label = AppStrings.T("linkviews.bulkRename.labels.prefixLabel"), Placeholder = AppStrings.T("linkviews.bulkRename.labels.prefixPlaceholder"), Text = _config.Prefix };
                    prefix.TextChanged += t => { _config.Prefix = t; RebuildPreview(); OnValidationChanged(); };
                    _modeHost.Children.Add(prefix);

                    var suffix = new TextField { Label = AppStrings.T("linkviews.bulkRename.labels.suffixLabel"), Placeholder = AppStrings.T("linkviews.bulkRename.labels.suffixPlaceholder"), Text = _config.Suffix, Margin = new Thickness(0, 8, 0, 0) };
                    suffix.TextChanged += t => { _config.Suffix = t; RebuildPreview(); OnValidationChanged(); };
                    _modeHost.Children.Add(suffix);
                    break;
                }

                case RenameMode.Sequential:
                {
                    var tokens = new TokenInput(FieldTokens(includeSeq: true), "{Seq}") { Text = _config.SeqPattern };
                    tokens.TextChanged += (s, e) => { _config.SeqPattern = tokens.Text; RebuildPreview(); OnValidationChanged(); };
                    _modeHost.Children.Add(tokens);
                    _modeHost.Children.Add(BuildSeqSteppers());
                    break;
                }

                case RenameMode.Token:
                {
                    var tokens = new TokenInput(FieldTokens(includeSeq: true), DefaultTokenPattern()) { Text = _config.TokenPattern };
                    tokens.TextChanged += (s, e) => { _config.TokenPattern = tokens.Text; RebuildPreview(); OnValidationChanged(); };
                    _modeHost.Children.Add(tokens);
                    _modeHost.Children.Add(BuildSeqSteppers());
                    break;
                }
            }
        }

        private FrameworkElement BuildSeqSteppers()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

            row.Children.Add(LabeledStepper(AppStrings.T("linkviews.bulkRename.labels.seqStart"), _config.SeqStart, 0, 100000, v => _config.SeqStart = v));
            row.Children.Add(LabeledStepper(AppStrings.T("linkviews.bulkRename.labels.seqIncrement"), _config.SeqIncrement, 1, 1000, v => _config.SeqIncrement = v));
            row.Children.Add(LabeledStepper(AppStrings.T("linkviews.bulkRename.labels.seqPad"), _config.SeqPad, 0, 8, v => _config.SeqPad = v));
            return row;
        }

        private FrameworkElement LabeledStepper(string label, int value, int min, int max, Action<int> onSet)
        {
            var col = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
            var cap = SectionHeader(label.ToUpperInvariant());
            cap.Margin = new Thickness(0, 0, 0, 4);
            col.Children.Add(cap);

            var stepper = new InlineStepper
            {
                MinValue = min, MaxValue = max, Step = 1, Decimals = 0, Value = value,
            };
            stepper.ValueChanged += (s, v) => { onSet((int)v); RebuildPreview(); OnValidationChanged(); };
            col.Children.Add(stepper);
            return col;
        }

        // ── Token vocabulary (mode-aware) ──────────────────────────────
        private (string Label, string Token)[] FieldTokens(bool includeSeq)
        {
            var list = _target == RenameTarget.Sheets
                ? new List<(string, string)> { (AppStrings.T("linkviews.bulkRename.labels.fieldSheetNumber"), "{SheetNumber}"), (AppStrings.T("linkviews.bulkRename.labels.fieldSheetName"), "{SheetName}") }
                : new List<(string, string)> { (AppStrings.T("linkviews.bulkRename.labels.fieldViewName"), "{ViewName}"), (AppStrings.T("linkviews.bulkRename.labels.tokViewType"), "{ViewType}") };
            if (includeSeq) list.Add((AppStrings.T("linkviews.bulkRename.labels.tokSeq"), "{Seq}"));
            return list.ToArray();
        }

        private string DefaultTokenPattern() =>
            _target == RenameTarget.Sheets ? "{SheetNumber} - {SheetName}" : "{ViewName}";

        // ═══════════════════════════════════════════════════════════════
        // Planning (shared with the run handler via BulkRenameEngine.Plan)
        // ═══════════════════════════════════════════════════════════════
        private bool EnforceUnique() =>
            _target == RenameTarget.Views || (_target == RenameTarget.Sheets && _field == RenameField.Number);

        private HashSet<long> SelectedIds() =>
            _target == RenameTarget.Sheets ? _selectedSheetIds : _selectedViewIds;

        /// <summary>Ordered (oldValue, tokens, ElementId) for the current selection.</summary>
        private List<(string oldValue, Dictionary<string, string> tokens, object? tag)> OrderedEntries()
        {
            var sel = SelectedIds();
            var result = new List<(string, Dictionary<string, string>, object?)>();

            if (_target == RenameTarget.Sheets)
            {
                foreach (var s in _sheets.OrderBy(s => s.Number, StringComparer.OrdinalIgnoreCase))
                {
                    if (!sel.Contains(s.Id.Value)) continue;
                    string oldValue = _field == RenameField.Number ? s.Number : s.Name;
                    var tokens = new Dictionary<string, string> { ["SheetNumber"] = s.Number, ["SheetName"] = s.Name };
                    result.Add((oldValue, tokens, s.Id));
                }
            }
            else
            {
                foreach (var v in _views.OrderBy(v => v.TypeLabel).ThenBy(v => v.Name))
                {
                    if (!sel.Contains(v.Id.Value)) continue;
                    var tokens = new Dictionary<string, string> { ["ViewName"] = v.Name, ["ViewType"] = v.TypeLabel };
                    result.Add((v.Name, tokens, v.Id));
                }
            }
            return result;
        }

        private IEnumerable<string> ExistingValuesNotSelected()
        {
            if (!EnforceUnique()) return Enumerable.Empty<string>();
            var sel = SelectedIds();
            if (_target == RenameTarget.Sheets) // Number
                return _sheets.Where(s => !sel.Contains(s.Id.Value)).Select(s => s.Number);
            return _views.Where(v => !sel.Contains(v.Id.Value)).Select(v => v.Name); // View Name
        }

        private List<RenamePlanItem> BuildPlan()
            => BulkRenameEngine.Plan(_config, OrderedEntries(), ExistingValuesNotSelected(), EnforceUnique());

        private void RebuildPreview()
        {
            if (_previewList == null || _previewCount == null) return;
            _previewList.Children.Clear();

            var plan = BuildPlan();
            int changes    = plan.Count(p => p.Status == RenameStatus.Change);
            int collisions = plan.Count(p => p.Status == RenameStatus.Collision);
            int empties    = plan.Count(p => p.Status == RenameStatus.Empty);

            _previewCount.Text = plan.Count == 0
                ? AppStrings.T("linkviews.bulkRename.labels.previewNone")
                : AppStrings.T("linkviews.bulkRename.labels.previewCounts", changes, collisions, empties);

            if (plan.Count == 0)
            {
                _previewList.Children.Add(MonoLine(AppStrings.T("linkviews.bulkRename.labels.previewDash"), "LemoineTextDim"));
                return;
            }

            foreach (var p in plan.Take(12))
            {
                string line = AppStrings.T("linkviews.bulkRename.labels.previewLine", p.OldValue, p.NewValue);
                string suffix =
                    p.Status == RenameStatus.Collision ? AppStrings.T("linkviews.bulkRename.labels.sfxCollides") :
                    p.Status == RenameStatus.Empty     ? AppStrings.T("linkviews.bulkRename.labels.sfxEmpty") :
                    p.Status == RenameStatus.Unchanged ? AppStrings.T("linkviews.bulkRename.labels.sfxNoChange") : "";
                string colour = p.Status == RenameStatus.Collision || p.Status == RenameStatus.Empty
                    ? "LemoineRed"
                    : p.Status == RenameStatus.Unchanged ? "LemoineTextDim" : "LemoineText";
                _previewList.Children.Add(MonoLine(line + suffix, colour));
            }

            if (plan.Count > 12)
                _previewList.Children.Add(MonoLine(AppStrings.T("linkviews.bulkRename.labels.previewMore", plan.Count - 12), "LemoineTextDim"));
        }

        // ═══════════════════════════════════════════════════════════════
        // Validation / summary
        // ═══════════════════════════════════════════════════════════════
        private bool ModeConfigured()
        {
            switch (_config.Mode)
            {
                case RenameMode.FindReplace:  return !string.IsNullOrEmpty(_config.Find);
                case RenameMode.PrefixSuffix: return !string.IsNullOrEmpty(_config.Prefix) || !string.IsNullOrEmpty(_config.Suffix);
                case RenameMode.Sequential:   return !string.IsNullOrWhiteSpace(_config.SeqPattern);
                case RenameMode.Token:        return !string.IsNullOrWhiteSpace(_config.TokenPattern);
            }
            return false;
        }

        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return true;
            if (stepId == "S2") return SelectedIds().Count > 0;
            if (stepId == "S3") return ModeConfigured() && BuildPlan().Any(p => p.Status == RenameStatus.Change);
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1") return _target == RenameTarget.Sheets ? AppStrings.T("linkviews.bulkRename.labels.targetSheets") : AppStrings.T("linkviews.bulkRename.labels.targetViews");
            if (stepId == "S2")
            {
                int n = SelectedIds().Count;
                return n > 0 ? AppStrings.T("linkviews.bulkRename.summaries.itemCount", n) : "—";
            }
            if (stepId == "S3")
            {
                if (!ModeConfigured()) return LabelFor(_config.Mode);
                int c = BuildPlan().Count(p => p.Status == RenameStatus.Change);
                return AppStrings.T("linkviews.bulkRename.summaries.s3", LabelFor(_config.Mode), c);
            }
            if (stepId == "S4") return AppStrings.T("linkviews.bulkRename.summaries.S4");
            return "—";
        }

        // ═══════════════════════════════════════════════════════════════
        // IReviewableTool
        // ═══════════════════════════════════════════════════════════════
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("target",  AppStrings.T("linkviews.bulkRename.review.itemTarget")),
            ("field",   AppStrings.T("linkviews.bulkRename.review.itemField")),
            ("mode",    AppStrings.T("linkviews.bulkRename.review.itemMode")),
            ("count",   AppStrings.T("linkviews.bulkRename.review.itemCount")),
            ("changes", AppStrings.T("linkviews.bulkRename.review.itemChanges")),
            ("skips",   AppStrings.T("linkviews.bulkRename.review.itemSkips")),
        };

        public IDictionary<string, string> ReviewValues
        {
            get
            {
                var plan = BuildPlan();
                int changes = plan.Count(p => p.Status == RenameStatus.Change);
                int skips   = plan.Count(p => p.Status != RenameStatus.Change);
                string field = _target == RenameTarget.Views
                    ? AppStrings.T("linkviews.bulkRename.labels.fieldViewName")
                    : (_field == RenameField.Number ? AppStrings.T("linkviews.bulkRename.labels.fieldSheetNumber") : AppStrings.T("linkviews.bulkRename.labels.fieldSheetName"));

                return new Dictionary<string, string>
                {
                    ["target"]  = _target == RenameTarget.Sheets ? AppStrings.T("linkviews.bulkRename.labels.targetSheets") : AppStrings.T("linkviews.bulkRename.labels.targetViews"),
                    ["field"]   = field,
                    ["mode"]    = LabelFor(_config.Mode),
                    ["count"]   = SelectedIds().Count > 0 ? AppStrings.T("linkviews.bulkRename.review.countValue", SelectedIds().Count) : "—",
                    ["changes"] = changes.ToString(),
                    ["skips"]   = skips.ToString(),
                };
            }
        }

        public IList<string>? ReviewChips => null;

        public string? ReviewNote =>
            AppStrings.T("linkviews.bulkRename.review.note");

        public string? ReviewWarning
        {
            get
            {
                var plan = BuildPlan();
                int collisions = plan.Count(p => p.Status == RenameStatus.Collision);
                int empties    = plan.Count(p => p.Status == RenameStatus.Empty);
                if (collisions == 0 && empties == 0) return null;
                var parts = new List<string>();
                if (collisions > 0) parts.Add(AppStrings.T("linkviews.bulkRename.review.warnCollide", collisions));
                if (empties    > 0) parts.Add(AppStrings.T("linkviews.bulkRename.review.warnEmpty", empties));
                return string.Join("; ", parts) + AppStrings.T("linkviews.bulkRename.review.warnSuffix");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Run
        // ═══════════════════════════════════════════════════════════════
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _runHandler!.Target          = _target;
            _runHandler.Field            = _field;
            _runHandler.OrderedIds       = OrderedEntries().Select(e => (ElementId)e.tag!).ToList();
            _runHandler.Config           = _config;
            _runHandler.PushLog          = pushLog;
            _runHandler.OnProgress       = onProgress;
            _runHandler.OnComplete       = onComplete;

            pushLog(AppStrings.T("linkviews.bulkRename.log.raising"), "info");
            _runEvent!.Raise();
        }

        // ── Small themed helpers ───────────────────────────────────────
        private static TextBlock SectionHeader(string text, double topMargin = 0)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, topMargin, 0, 6) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static TextBlock BodyHint(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static TextBlock MonoLine(string text, string foregroundKey)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(0, 1, 0, 1) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, foregroundKey);
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            return tb;
        }
    }
}
