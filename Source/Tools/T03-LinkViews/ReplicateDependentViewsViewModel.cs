using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

using WpfGrid     = System.Windows.Controls.Grid;
using WpfPoint    = System.Windows.Point;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfTextBox  = System.Windows.Controls.TextBox;

namespace LemoineTools.Tools.LinkViews
{
    // ── Data types populated by Command on main thread ─────────────────

    /// <summary>
    /// Snapshot describing a single existing dependent view on a source view,
    /// captured on the Revit main thread for later use by the ViewModel and run handler.
    /// </summary>
    public sealed class DepEntry
    {
        /// <summary>The element id of the existing dependent view in Revit.</summary>
        public ElementId DepViewId  { get; set; } = null!;

        /// <summary>The trailing suffix appended to the parent view name (for example "N", "S", or a quadrant label).</summary>
        public string    Suffix     { get; set; } = null!;

        /// <summary>True when the dependent view has an active crop region whose extents should be replicated.</summary>
        public bool      HasCrop    { get; set; }

        /// <summary>The minimum corner of the dependent's crop, expressed in world (model) coordinates, or <c>null</c> if no crop is set.</summary>
        public XYZ?      WorldMin   { get; set; }

        /// <summary>The maximum corner of the dependent's crop, expressed in world (model) coordinates, or <c>null</c> if no crop is set.</summary>
        public XYZ?      WorldMax   { get; set; }

        /// <summary>The element id of the scope box assigned to the dependent view, or invalid/null when none is assigned.</summary>
        public ElementId ScopeBoxId { get; set; } = null!;
    }

    /// <summary>
    /// Snapshot describing a candidate source view together with its existing dependent views.
    /// One <see cref="SourceViewEntry"/> is created for every view in the project that already
    /// has at least one dependent, so the user can pick one to replicate.
    /// </summary>
    public sealed class SourceViewEntry
    {
        /// <summary>The element id of the source view in Revit.</summary>
        public ElementId      ViewId    { get; set; } = null!;

        /// <summary>The display name of the source view.</summary>
        public string         Name      { get; set; } = null!;

        /// <summary>The Revit <see cref="ViewType"/> of the source view (Floor Plan, Ceiling Plan, etc.).</summary>
        public ViewType       ViewType  { get; set; }

        /// <summary>A human-readable label for the view type, used in group headings and the review summary.</summary>
        public string         TypeLabel { get; set; } = null!;

        /// <summary>The collection of existing dependent views that will be replicated onto each chosen target.</summary>
        public List<DepEntry> Deps      { get; set; } = null!;

        /// <summary>The world-space X basis vector of the source view, used to detect orientation mismatches with target views.</summary>
        public XYZ            BasisX    { get; set; } = null!;
    }

    /// <summary>
    /// Snapshot describing a candidate target view onto which the source's dependent views can be replicated.
    /// </summary>
    public sealed class TargetViewEntry
    {
        /// <summary>The element id of the target view in Revit.</summary>
        public ElementId ViewId             { get; set; } = null!;

        /// <summary>The display name of the target view.</summary>
        public string    Name               { get; set; } = null!;

        /// <summary>The Revit <see cref="ViewType"/> of the target view.</summary>
        public ViewType  ViewType           { get; set; }

        /// <summary>The name of the level the target view is associated with, or an empty string if no level is set.</summary>
        public string    LevelName          { get; set; } = "";

        /// <summary>The number of dependent views that already exist on the target, surfaced as a warning in the UI.</summary>
        public int       ExistingDepCount   { get; set; }

        /// <summary>
        /// True when the target view's orientation does not match the source view's orientation, indicating
        /// that the replicated crop regions may not align correctly. Recomputed when step S3 is built.
        /// </summary>
        public bool      OrientationWarning { get; set; }

        /// <summary>
        /// Gets a human-readable label for <see cref="ViewType"/> (for example "Floor Plan" or "Ceiling Plan"),
        /// falling back to the raw enum name for unmapped types.
        /// </summary>
        public string TypeLabel
        {
            get
            {
                switch (ViewType)
                {
                    case ViewType.FloorPlan:   return "Floor Plan";
                    case ViewType.CeilingPlan: return "Ceiling Plan";
                    case ViewType.Elevation:   return "Elevation";
                    case ViewType.Section:     return "Section";
                    case ViewType.Detail:      return "Detail";
                    case ViewType.AreaPlan:    return "Area Plan";
                    default:                   return ViewType.ToString();
                }
            }
        }
    }

    // ── ViewModel ─────────────────────────────────────────────────────

    /// <summary>
    /// ViewModel driving the Replicate Dependent Views step-flow window. Lets the user pick
    /// a source view, preview its existing dependents, choose target views, configure a naming
    /// scheme, and trigger the run handler that creates dependents on each target inside a
    /// Revit transaction.
    /// </summary>
    public class ReplicateDependentViewsViewModel : ILemoineTool, ILemoineReviewable
    {
        // ── Identity ──────────────────────────────────────────────────
        /// <summary>Gets the title displayed in the step-flow window header.</summary>
        public string Title    => "Replicate Dependent Views";

        /// <summary>Gets the label displayed on the final run button.</summary>
        public string RunLabel => "Replicate in Revit →";

        /// <summary>
        /// Gets the ordered set of steps shown in the step-flow window. S1 (source) and S3 (targets)
        /// are required; the remaining steps are optional preview, naming, and review screens.
        /// </summary>
        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Source View",       required: true),
            new StepDefinition("S2", "Dependent Preview", required: false),
            new StepDefinition("S3", "Target Views",      required: true),
            new StepDefinition("S4", "View Naming",       required: false),
            new StepDefinition("S5", "Review & Run",      required: false),
        };

        // ── State ──────────────────────────────────────────────────────
        private readonly List<SourceViewEntry> _allSources;
        private readonly List<TargetViewEntry> _allTargets;

        private readonly Dictionary<string, SourceViewEntry> _sourceByKey =
            new Dictionary<string, SourceViewEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, TargetViewEntry> _targetByKey =
            new Dictionary<string, TargetViewEntry>(StringComparer.Ordinal);

        private string?      _selectedSourceKey;
        private List<string> _selectedTargetKeys = new List<string>();

        // S4 naming state — Front/Center/End, each with optional inline custom text
        private string _namingFront        = "Host Level";
        private string _namingFrontCustom  = "";
        private string _namingCenter       = "Source View Name";
        private string _namingCenterCustom = "";
        private string _namingEnd          = "None";
        private string _namingEndCustom    = "";

        /// <summary>
        /// Optional lookup from target view element id (as <see cref="ElementId.Value"/>) to the view's
        /// world X basis vector, populated by the command. When provided, step S3 uses it to flag
        /// targets whose orientation differs from the source's.
        /// </summary>
        public Dictionary<long, XYZ>? TargetBasisXMap { get; set; }

        // ── ExternalEvent wiring ───────────────────────────────────────
        private readonly ReplicateDependentViewsRunHandler _runHandler;
        private readonly ExternalEvent                     _runEvent;

        /// <summary>
        /// Raised whenever a user choice changes the validity of one or more steps;
        /// the host window subscribes to this to recompute the Next/Run button states.
        /// </summary>
        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        /// <summary>
        /// Initializes a new <see cref="ReplicateDependentViewsViewModel"/>.
        /// </summary>
        /// <param name="runHandler">The Revit <see cref="IExternalEventHandler"/> that performs the actual replication inside a transaction.</param>
        /// <param name="runEvent">The <see cref="ExternalEvent"/> bound to <paramref name="runHandler"/>, raised when the user clicks the run button.</param>
        /// <param name="allSources">All views in the project that already have at least one dependent and can therefore be chosen as a source.</param>
        /// <param name="allTargets">All views in the project that are eligible to receive replicated dependents.</param>
        public ReplicateDependentViewsViewModel(
            ReplicateDependentViewsRunHandler runHandler, ExternalEvent runEvent,
            List<SourceViewEntry> allSources, List<TargetViewEntry> allTargets)
        {
            _runHandler = runHandler;
            _runEvent   = runEvent;
            _allSources = allSources ?? new List<SourceViewEntry>();
            _allTargets = allTargets ?? new List<TargetViewEntry>();

            foreach (var s in _allSources)
            {
                int depCount = s.Deps?.Count ?? 0;
                string key = $"{s.TypeLabel}  ·  {s.Name}  ({depCount} dep{(depCount != 1 ? "s" : "")})";
                _sourceByKey[key] = s;
            }

            foreach (var t in _allTargets)
            {
                string key = t.ExistingDepCount > 0
                    ? $"{t.Name}  ⚠ {t.ExistingDepCount} existing dep(s)"
                    : t.Name;
                if (_targetByKey.ContainsKey(key)) key += $"  [{t.ViewId.Value}]";
                _targetByKey[key] = t;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // GetStepContent
        // ═══════════════════════════════════════════════════════════════
        /// <summary>
        /// Builds and returns the WPF content for a given step id. Steps that depend on
        /// earlier choices (S2, S3, S5) are wrapped in a live container so they rebuild
        /// themselves whenever validation state changes.
        /// </summary>
        /// <param name="stepId">The step identifier — one of "S1" through "S5".</param>
        /// <returns>The rendered step content, or <c>null</c> when <paramref name="stepId"/> is not recognized.</returns>
        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "S1") return BuildS1();
            if (stepId == "S2") return LiveStep(() => _selectedSourceKey, BuildS2Content);
            if (stepId == "S3") return LiveStep(() => _selectedSourceKey, BuildS3Content);
            if (stepId == "S4") return BuildS4Naming();
            if (stepId == "S5") return null; // framework renders review (ILemoineReviewable)
            return null;
        }

        private FrameworkElement LiveStep(Func<string> keyFunc, Func<FrameworkElement> builder)
        {
            var host = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
            string lastKey = "\0";
            void Refresh()
            {
                string k = keyFunc() ?? "";
                if (k == lastKey) return;
                lastKey      = k;
                host.Content = builder();
            }
            Refresh();
            ValidationChanged += (_, __) => Refresh();
            return host;
        }

        // ── S1: Source View ────────────────────────────────────────────
        private FrameworkElement BuildS1()
        {
            if (_allSources.Count == 0)
                return Message("No views with existing dependent views were found in this project.");

            var groups = _sourceByKey.Keys
                .GroupBy(k => _sourceByKey[k].TypeLabel)
                .ToDictionary(g => g.Key, g => g.OrderBy(k => k).ToList());

            var preSelected = _selectedSourceKey != null
                ? new List<string> { _selectedSourceKey }
                : new List<string>();

            var tabs = new LemoineMultiSelectTabs();
            tabs.SelectionChanged += selected =>
            {
                var list  = selected.ToList();
                string newKey = list.FirstOrDefault(k => k != _selectedSourceKey)
                             ?? list.LastOrDefault();
                if (newKey == _selectedSourceKey) return;
                _selectedSourceKey  = newKey;
                _selectedTargetKeys = new List<string>();
                OnValidationChanged();
            };
            tabs.SetGroups(groups, preSelected);
            return tabs;
        }

        // ── S2: Dependent Preview (live) ───────────────────────────────
        private FrameworkElement BuildS2Content()
        {
            var source = SelectedSource;
            if (source == null) return Message("Select a source view in Step 1.");

            var panel = new StackPanel();

            var header = new TextBlock
            {
                Text = $"Dependent views on \"{source.Name}\":",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            };
            header.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            header.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            header.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            panel.Children.Add(header);

            if (source.Deps == null || source.Deps.Count == 0)
            {
                panel.Children.Add(Message("No dependent views found on this source."));
                return panel;
            }

            var sv = new ScrollViewer { MaxHeight = 260,
                                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var sp = new StackPanel();
            foreach (var dep in source.Deps)
            {
                var row  = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                var crop = new TextBlock
                {
                    Text = dep.HasCrop ? "(crop)" : "(no crop)",
                    FontStyle = FontStyles.Italic, VerticalAlignment = VerticalAlignment.Center,
                };
                crop.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                crop.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                crop.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                DockPanel.SetDock(crop, Dock.Right);

                var name = new TextBlock
                {
                    Text = $"— {source.Name} - {dep.Suffix}",
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                name.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                name.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                name.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

                row.Children.Add(crop);
                row.Children.Add(name);
                sp.Children.Add(row);
            }
            sv.Content = sp;
            panel.Children.Add(sv);
            return panel;
        }

        // ── S3: Target Views — grouped by view type, nothing pre-selected ──
        private FrameworkElement BuildS3Content()
        {
            var source = SelectedSource;
            if (source == null) return Message("Select a source view in Step 1.");

            // Show floor plans, ceiling plans, and 3D views regardless of source type
            var allowedTypes = new HashSet<ViewType>
            {
                ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.ThreeD
            };
            var compatibleKvs = _targetByKey
                .Where(kv => allowedTypes.Contains(kv.Value.ViewType)
                          && kv.Value.ViewId != source.ViewId)
                .ToList();

            // Recompute orientation warnings
            foreach (var kv in compatibleKvs)
            {
                var t = kv.Value;
                t.OrientationWarning = false;
                if (source.BasisX != null && TargetBasisXMap != null
                    && TargetBasisXMap.TryGetValue(t.ViewId.Value, out var tBasis)
                    && tBasis != null)
                    t.OrientationWarning = source.BasisX.CrossProduct(tBasis).GetLength() > 0.01;
            }

            if (compatibleKvs.Count == 0)
                return Message($"No other {source.TypeLabel} views found in this project.");

            int warnCount = compatibleKvs.Count(kv => kv.Value.OrientationWarning);
            var outer = new StackPanel();

            if (warnCount > 0)
            {
                var warn = new Border { BorderThickness = new Thickness(1),
                                        CornerRadius = new CornerRadius(3),
                                        Margin = new Thickness(0, 0, 0, 8) };
                warn.SetResourceReference(Border.PaddingProperty,    "LemoineTh_CardPad");
                warn.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                warn.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                var warnTb = new TextBlock
                {
                    Text = $"⚠  {warnCount} target(s) have a different view orientation " +
                           "and may not receive correct crop regions.",
                    TextWrapping = TextWrapping.Wrap,
                };
                warnTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                warnTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                warnTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                warn.Child = warnTb;
                outer.Children.Add(warn);
            }

            // Group by view type label (same type since source-filtered, but makes the
            // tab header clear). Within each group, sort by name.
            var byType = compatibleKvs
                .OrderBy(kv => kv.Key)
                .GroupBy(kv => kv.Value.TypeLabel)
                .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());

            // Preserve prior selections that are still compatible; default = nothing selected
            var preSelected = _selectedTargetKeys.Count > 0
                ? _selectedTargetKeys.Where(k => compatibleKvs.Any(kv => kv.Key == k)).ToList()
                : new List<string>();

            var tabs = new LemoineMultiSelectTabs();
            tabs.SelectionChanged += selected =>
            {
                _selectedTargetKeys = new List<string>(selected);
                OnValidationChanged();
            };
            tabs.SetGroups(byType, preSelected);
            outer.Children.Add(tabs);
            return outer;
        }

        // ── S4: View Naming ────────────────────────────────────────────
        // Naming token options for RDV
        private static readonly string[] RdvNamingOptions =
        {
            "None", "Host Level", "Source View Name", "Target View Name",
            "View Type", "Dep Suffix", "Custom"
        };

        private FrameworkElement BuildS4Naming()
        {
            var outer = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };

            // Preview declared early so closures can capture it
            var previewText = new TextBlock { TextWrapping = TextWrapping.Wrap };
            previewText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            previewText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            previewText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            void UpdatePreview()
            {
                var source  = SelectedSource;
                string srcName    = source?.Name ?? "Corridor Plan";
                string levelEx    = "Level 2";
                string typeEx     = "Floor Plan";
                string targetEx   = "Area Plan - L2";
                string suffixEx   = source?.Deps?.FirstOrDefault()?.Suffix ?? "N";

                var firstTarget = _selectedTargetKeys
                    .Where(k => _targetByKey.ContainsKey(k))
                    .Select(k => _targetByKey[k]).FirstOrDefault();
                if (firstTarget != null)
                {
                    levelEx  = string.IsNullOrEmpty(firstTarget.LevelName) ? "No Level" : firstTarget.LevelName;
                    typeEx   = firstTarget.TypeLabel;
                    targetEx = firstTarget.Name;
                }

                string? ResolveSlot(string slot, string custom)
                {
                    switch (slot)
                    {
                        case "Host Level":        return levelEx;
                        case "Source View Name":  return srcName;
                        case "Target View Name":  return targetEx;
                        case "View Type":         return typeEx;
                        case "Dep Suffix":        return suffixEx;
                        case "Custom":            return string.IsNullOrWhiteSpace(custom) ? "(custom)" : custom.Trim();
                        default:                  return null;
                    }
                }

                var parts = new[] { _namingFront, _namingCenter, _namingEnd }
                    .Select((slot, i) => ResolveSlot(slot,
                        i == 0 ? _namingFrontCustom :
                        i == 1 ? _namingCenterCustom : _namingEndCustom))
                    .Where(s => !string.IsNullOrEmpty(s)).ToList();
                parts.Add(suffixEx);
                previewText.Text = parts.Count > 1
                    ? string.Join(" - ", parts)
                    : "(no name components set)";
            }

            // Helper: one slot row — [label] [combo] [textbox shown only when Custom]
            void AddSlotRow(string label,
                            string curVal,    Action<string> setVal,
                            string curCustom, Action<string> setCustom)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal,
                                           Margin = new Thickness(0, 0, 0, 8) };

                var lbl = new TextBlock { Text = label, Width = 58,
                                          VerticalAlignment = VerticalAlignment.Center };
                lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                row.Children.Add(lbl);

                var cb = new WpfComboBox { Width = 150 };
                cb.SetResourceReference(FrameworkElement.HeightProperty,  "LemoineH_Input");
                cb.SetResourceReference(WpfComboBox.FontSizeProperty,     "LemoineFS_SM");
                cb.SetResourceReference(WpfComboBox.FontFamilyProperty,   "LemoineUiFont");
                cb.SetResourceReference(WpfComboBox.ForegroundProperty,   "LemoineText");
                foreach (string opt in RdvNamingOptions) cb.Items.Add(opt);
                cb.SelectedItem = curVal;
                row.Children.Add(cb);

                var tb = new WpfTextBox
                {
                    Text       = curCustom,
                    Width      = 130,
                    Margin     = new Thickness(6, 0, 0, 0),
                    Visibility = curVal == "Custom" ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed,
                };
                tb.SetResourceReference(FrameworkElement.HeightProperty,                   "LemoineH_Input");
                tb.SetResourceReference(System.Windows.Controls.Control.PaddingProperty,   "LemoineTh_InputPad");
                tb.SetResourceReference(WpfTextBox.ForegroundProperty,   "LemoineText");
                tb.SetResourceReference(WpfTextBox.BackgroundProperty,   "LemoineSelectBg");
                tb.SetResourceReference(WpfTextBox.FontSizeProperty,     "LemoineFS_SM");
                tb.SetResourceReference(WpfTextBox.FontFamilyProperty,   "LemoineMonoFont");
                tb.SetResourceReference(WpfTextBox.BorderBrushProperty,  "LemoineBorder");
                row.Children.Add(tb);

                cb.SelectionChanged += (s, e) =>
                {
                    if (!(cb.SelectedItem is string v)) return;
                    setVal(v);
                    tb.Visibility = v == "Custom" ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    UpdatePreview();
                    OnValidationChanged();
                };
                tb.TextChanged += (s, e) => { setCustom(tb.Text); UpdatePreview(); };

                outer.Children.Add(row);
            }

            AddSlotRow("Front",  _namingFront,  v => _namingFront  = v, _namingFrontCustom,  v => _namingFrontCustom  = v);
            AddSlotRow("Center", _namingCenter, v => _namingCenter = v, _namingCenterCustom, v => _namingCenterCustom = v);
            AddSlotRow("End",    _namingEnd,    v => _namingEnd    = v, _namingEndCustom,    v => _namingEndCustom    = v);

            var sep = new System.Windows.Shapes.Rectangle { Height = 1, Margin = new Thickness(0, 4, 0, 10) };
            sep.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "LemoineBorder");
            outer.Children.Add(sep);

            var previewHeader = new TextBlock { Text = "PREVIEW  (suffix always appended last)", Margin = new Thickness(0, 0, 0, 4) };
            previewHeader.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            previewHeader.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            previewHeader.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(previewHeader);

            var previewBorder = new Border
            {
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                Padding = new Thickness(10, 6, 10, 6),
            };
            previewBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            previewBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            previewBorder.Child = previewText;
            outer.Children.Add(previewBorder);

            UpdatePreview();
            ValidationChanged += (s, e) => UpdatePreview();
            return outer;
        }

                // ── S5: Review & Run ───────────────────────────────────────────

        // ── ILemoineReviewable (P3) — framework renders the review step ───
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("source",  "Source View"),
            ("deps",    "Dependents to Copy"),
            ("targets", "Target Views"),
            ("create",  "Views to Create"),
        };

        public IDictionary<string, string> ReviewValues
        {
            get
            {
                int deps = SelectedSource?.Deps?.Count ?? 0;
                int tgts = _selectedTargetKeys.Count;
                return new Dictionary<string, string>
                {
                    ["source"]  = SelectedSource?.Name ?? "—",
                    ["deps"]    = deps.ToString(),
                    ["targets"] = tgts > 0 ? $"{tgts} view(s)" : "—",
                    ["create"]  = deps > 0 && tgts > 0 ? $"≤ {deps * tgts}" : "—",
                };
            }
        }

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => "Creates one dependent view per target for each dependent on the " +
            "source, transferring crop region and view range. Targets with existing dependents of the same name " +
            "are skipped and logged.";
        public string?        ReviewWarning => null;

        // ── Helpers ────────────────────────────────────────────────────
        private SourceViewEntry? SelectedSource =>
            _selectedSourceKey != null && _sourceByKey.TryGetValue(_selectedSourceKey, out var s)
            ? s : null;

        private static FrameworkElement Message(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap,
                                     FontStyle = FontStyles.Italic };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        // ═══════════════════════════════════════════════════════════════
        // IsValid / SummaryFor / Run
        // ═══════════════════════════════════════════════════════════════
        /// <summary>
        /// Reports whether the user's selections satisfy the requirements for the given step.
        /// </summary>
        /// <param name="stepId">The step identifier — one of "S1" through "S5".</param>
        /// <returns>
        /// <c>true</c> when the step has either been satisfied or is optional;
        /// <c>false</c> when a required selection is missing (a source view on S1, or one or more targets on S3).
        /// </returns>
        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return SelectedSource != null;
            if (stepId == "S2") return true;
            if (stepId == "S3") return _selectedTargetKeys.Count > 0;
            if (stepId == "S4") return true;
            return true;
        }

        /// <summary>
        /// Returns the short status line shown next to a step in the step-flow window header.
        /// </summary>
        /// <param name="stepId">The step identifier — one of "S1" through "S5".</param>
        /// <returns>A concise summary of the current selection for that step, or "—" when nothing is selected.</returns>
        public string SummaryFor(string stepId)
        {
            if (stepId == "S1") return SelectedSource?.Name ?? "—";
            if (stepId == "S2")
            {
                int d = SelectedSource?.Deps?.Count ?? 0;
                return d > 0 ? $"{d} dependent(s)" : "No deps";
            }
            if (stepId == "S3") return _selectedTargetKeys.Count > 0
                ? $"{_selectedTargetKeys.Count} target(s)" : "—";
            if (stepId == "S4")
            {
                var set = new[] { _namingFront, _namingCenter, _namingEnd }
                    .Where(s => s != "None").ToList();
                return set.Count > 0 ? string.Join(" / ", set) : "Defaults";
            }
            if (stepId == "S5") return "Ready to run";
            return "—";
        }

        /// <summary>
        /// Hands the user's selections to the run handler and raises the bound
        /// <see cref="ExternalEvent"/> so that Revit performs the replication on its main thread.
        /// If no source is selected, the run is reported as completed with one failure and no
        /// Revit work is performed.
        /// </summary>
        /// <param name="pushLog">Callback for pushing log lines back to the host window. The second argument is the log level (for example "info", "warn", or "error").</param>
        /// <param name="onProgress">Progress callback invoked with (currentTarget, totalTargets, currentDep, totalDeps).</param>
        /// <param name="onComplete">Completion callback invoked with (created, skipped, failed) counts when the run finishes.</param>
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            var source = SelectedSource;
            if (source == null) { onComplete(0, 1, 0); return; }

            var targetEntries = _selectedTargetKeys
                .Where(k => _targetByKey.ContainsKey(k))
                .Select(k => _targetByKey[k])
                .ToList();

            _runHandler.SourceEntry   = source;
            _runHandler.TargetEntries = targetEntries;
            _runHandler.NamingFront        = _namingFront;
            _runHandler.NamingFrontCustom  = _namingFrontCustom;
            _runHandler.NamingCenter       = _namingCenter;
            _runHandler.NamingCenterCustom = _namingCenterCustom;
            _runHandler.NamingEnd          = _namingEnd;
            _runHandler.NamingEndCustom    = _namingEndCustom;
            _runHandler.PushLog       = pushLog;
            _runHandler.OnProgress    = onProgress;
            _runHandler.OnComplete    = onComplete;

            pushLog("Raising Revit ExternalEvent…", "info");
            _runEvent.Raise();
        }
    }
}
