using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.Clash
{
    /// <summary>
    /// Clash Finder &amp; Dimension wizard: pick saved definition(s) + views, detect clashes
    /// and place coloured ES-tagged markers, then (optionally) dimension them out to grids or a
    /// slab edge. Five steps: definitions · views · marker settings · per-run dimensioning ·
    /// review &amp; run. The actual work happens in <see cref="ClashFinderEventHandler"/>.
    /// Distances are edited in imperial units (oversize in inches, tolerances in feet) but stored
    /// internally in millimetres — the engine divides by 304.8.
    /// </summary>
    public class ClashFinderViewModel : ILemoineTool, ILemoineReviewable, IWindowActivatable, ILemoineRunResult, ILemoineToolCleanup
    {
        // Run strip: live "markers" label during the run, full chip breakdown on completion.
        public string? ResultNoun => "markers";
        private System.Collections.Generic.IReadOnlyList<LemoineTools.Lemoine.ResultChip>? _resultChips;
        public System.Collections.Generic.IReadOnlyList<LemoineTools.Lemoine.ResultChip>? ResultChips => _resultChips;

        // Null the callbacks parked on the static handlers so this VM isn't retained after close
        // — and so a late slab pick can't marshal into this window's terminated dispatcher.
        public void OnWindowClosed()
        {
            if (_slabPickHandler != null) _slabPickHandler.OnPicked = null;
            _activateWindow = null;
            if (_handler == null) return;
            _handler.PushLog       = null;
            _handler.OnProgress    = null;
            _handler.OnComplete    = null;
            _handler.OnResultChips = null;
        }

        public string Title    => "Clash Finder & Dimension";
        public string RunLabel => "Find & Mark Clashes →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Definitions",   required: true),
            new StepDefinition("S2", "Select Views",         required: true),
            new StepDefinition("S3", "Marker Settings",      required: false),
            new StepDefinition("S4", "Dimensioning",         required: false),
            new StepDefinition("S5", "Review & Run",         required: false),
        };

        // ── Unit conversion (UI imperial ↔ stored mm) ─────────────────────────
        private const double MmPerInch = 25.4;

        public event EventHandler? ValidationChanged;
        private void Fire() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly ClashFinderEventHandler? _handler;
        private readonly ExternalEvent?           _event;
        private readonly AutoDimension.SlabPickEventHandler? _slabPickHandler;
        private readonly ExternalEvent?                      _slabPickEvent;

        // Brings the wizard window back to front after a slab pick (set by StepFlowWindow).
        private Action? _activateWindow;
        public void SetActivateCallback(Action activateWindow) => _activateWindow = activateWindow;

        // Up-front slab pick (slab-edge mode): chosen before running.
        private AutoDimension.Resolvers.SlabScope? _pickedSlab;
        private string _pickedSlabName = "";

        // ── Data ──────────────────────────────────────────────────────────────
        private readonly List<View> _allViews;
        private readonly List<ClashDefinition> _definitions;
        private readonly Dictionary<string, ClashDefinition> _defDisplayToDef = new Dictionary<string, ClashDefinition>();

        // ── State ─────────────────────────────────────────────────────────────
        private List<string> _selectedDefDisplays  = new List<string>();
        private List<long>   _selectedViewIds      = new List<long>();
        private readonly LemoineBrowserTree _browserTree;

        private bool _clearPrevious     = true;
        private bool _runDimensionPass  = true;   // dimensioning is the point — on by default
        private bool _adoptUserCallouts = true;   // user-drawn callouts become pre-defined clash groups
        private double _roundSizeMm     = 0.0;    // round marker oversize; 0 = exact element size

        // Finest scale (1:N) a callout may auto-enlarge to — run override of the saved default.
        private static readonly int[] CalloutScaleOptions = { 12, 16, 24, 32, 48 };
        private int _maxCalloutScale = AutoDimension.AutoDimensionConfig.Instance.MaxCalloutScale;

        // Dimension-pass destination, seeded from the shared auto-dimension config. Chaining,
        // callouts, grouping tolerances, and the storey margin are settings-only (Settings →
        // Dimensions) — the run always uses the saved values.
        private const string GridDisplay   = "To Grid";
        private const string SlabDisplay   = "To Slab Edge";
        private const string ManualDisplay = "To Picked Edge";
        private string _dimTargetType =
            string.Equals(AutoDimension.AutoDimensionConfig.Instance.TargetType, "SlabEdge", StringComparison.OrdinalIgnoreCase) ? "SlabEdge"
          : string.Equals(AutoDimension.AutoDimensionConfig.Instance.TargetType, "ManualDatum", StringComparison.OrdinalIgnoreCase) ? "ManualDatum"
          : "Grid";

        public ClashFinderViewModel(
            ClashFinderEventHandler? handler,
            ExternalEvent?           externalEvent,
            List<View>               allViews,
            List<ClashDefinition>    definitions,
            AutoDimension.SlabPickEventHandler? slabPickHandler = null,
            ExternalEvent?                      slabPickEvent   = null,
            LemoineBrowserTree?                 browserTree     = null)
        {
            _handler          = handler;
            _event            = externalEvent;
            _slabPickHandler  = slabPickHandler;
            _slabPickEvent    = slabPickEvent;
            _allViews    = allViews ?? new List<View>();
            _definitions = definitions ?? new List<ClashDefinition>();
            _browserTree = browserTree ?? new LemoineBrowserTree();

            var used = new HashSet<string>();
            foreach (var def in _definitions)
            {
                string baseName = string.IsNullOrWhiteSpace(def.Name) ? "(unnamed)" : def.Name;
                string display  = baseName;
                int n = 2;
                while (!used.Add(display)) display = $"{baseName} ({n++})";
                _defDisplayToDef[display] = def;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildDefinitionsStep();
                case "S2": return BuildViewsStep();
                case "S3": return BuildMarkerStep();
                case "S4": return BuildDimensioningStep();
                case "S5": return null;   // framework renders the review (ILemoineReviewable)
                default:   return null;
            }
        }

        private static ScrollViewer WrapInScroll(FrameworkElement content, double maxHeight = 700)
        {
            var sv = new ScrollViewer
            {
                Content                       = content,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight                     = maxHeight,
            };
            LemoineControlStyles.WireBubblingScroll(sv);
            return sv;
        }

        // ── S1 — Select Definitions ───────────────────────────────────────────
        private FrameworkElement BuildDefinitionsStep()
        {
            var outer = new StackPanel();

            if (_defDisplayToDef.Count == 0)
            {
                AddDim(outer, "No clash definitions saved yet. Open “Clash Definitions” to create one first.");
                return WrapInScroll(outer);
            }

            AddLabel(outer, "Pick one or more saved clash definitions to run.");

            var groups = new Dictionary<string, List<string>>
            {
                ["Saved Definitions"] = _defDisplayToDef.Keys.ToList(),
            };

            var tabs = new LemoineMultiSelectTabs();
            tabs.SelectionChanged += selected =>
            {
                _selectedDefDisplays = new List<string>(selected);
                Fire();
            };
            tabs.SetGroups(groups, _selectedDefDisplays);
            outer.Children.Add(tabs);

            return WrapInScroll(outer);
        }

        // ── S2 — Select Views ─────────────────────────────────────────────────
        private FrameworkElement BuildViewsStep()
        {
            var outer  = new StackPanel();
            var picker = new LemoineBrowserTreePicker
            {
                Height         = 300,
                AccessibleName = "Views to scan",
            };
            // Subscribe BEFORE SetTree — its end-of-setup SelectionChanged seeds the mirror list.
            picker.SelectionChanged += ids =>
            {
                _selectedViewIds = ids.ToList();
                Fire();
            };
            picker.SetTree(_browserTree,
                _allViews.Select(v => v.Id.Value),
                _selectedViewIds.ToList());
            outer.Children.Add(picker);
            return WrapInScroll(outer);
        }

        // ── S3 — Marker Settings ──────────────────────────────────────────────
        private FrameworkElement BuildMarkerStep()
        {
            var outer = new StackPanel();

            var toggles = new LemoineToggleSwitches();
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "clear", Label = "Clear previous clash markers before running",
                                 Desc = "Removes earlier Lemoine clash markers in the selected views first.", DefaultOn = _clearPrevious },
            });
            toggles.StateChanged += state =>
            {
                state.TryGetValue("clear", out _clearPrevious);
                Fire();
            };
            outer.Children.Add(toggles);

            AddDivider(outer);
            AddStepperRow(outer,
                "Marker oversize (in, 0 = exact element size)",
                "Added to the Group 1 element's own cross-section. 0 draws the marker at the element's exact size (round pipe Ø, or rectangular-duct W×H); a value enlarges every marker by that much — e.g. 2 in makes a 4 in pipe a 6 in round and adds 2 in to a duct's width and height.",
                _roundSizeMm / MmPerInch, min: 0, max: 40, step: 0.25, decimals: 2,
                v => { _roundSizeMm = v * MmPerInch; Fire(); });

            AddDivider(outer);
            AddDim(outer, "The storey depth margin (how far below a level a clash still counts as "
                        + "that level's storey) lives in Settings → Dimensions.");

            return WrapInScroll(outer);
        }

        // ── S4 — Per-run Dimensioning ─────────────────────────────────────────
        private FrameworkElement BuildDimensioningStep()
        {
            var outer = new StackPanel();

            AddDim(outer, "Chaining, callouts, grouping distances, and layout spacing are saved "
                        + "defaults (Settings → Dimensions) and apply to every run.");
            AddDivider(outer);

            var toggles = new LemoineToggleSwitches();
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "dimPass", Label = "Place dimensions after marking",
                                 Desc = "Runs the auto-dimension engine on the clash markers, dimensioning each out to the chosen destination below.", DefaultOn = _runDimensionPass },
                new ToggleItem { Id = "userCallouts", Label = "Use my drawn callouts as clash groups",
                                 Desc = "Any callout you drew on a selected view becomes its own pre-defined group: the clashes inside it mark and dimension only there — never clustered with markers outside it — and the tool grows its crop to the room and sets a legible scale, like an automatic dense-area callout.", DefaultOn = _adoptUserCallouts },
            });
            toggles.StateChanged += state =>
            {
                state.TryGetValue("dimPass", out _runDimensionPass);
                state.TryGetValue("userCallouts", out _adoptUserCallouts);
                Fire();
            };
            outer.Children.Add(toggles);

            AddDivider(outer);
            AddLabel(outer, "Finest callout scale — a dense area won't enlarge past this "
                          + "(a bigger denominator = a smaller, less-zoomed callout).");
            var calloutScalePicker = new LemoineSingleSelect { Label = "Finest callout scale" };
            calloutScalePicker.Items        = CalloutScaleOptions.Select(s => $"1:{s}").ToList();
            calloutScalePicker.SelectedItem = $"1:{_maxCalloutScale}";
            calloutScalePicker.SelectionChanged += sel =>
            {
                if (int.TryParse(sel.Replace("1:", ""), out int v) && v > 0) _maxCalloutScale = v;
                Fire();
            };
            outer.Children.Add(calloutScalePicker);

            AddDivider(outer);
            AddLabel(outer, "Dimension destination (used when the dimension pass is on).");
            var destPicker = new LemoineSingleSelect { Label = "Destination" };
            destPicker.Items = new List<string> { GridDisplay, SlabDisplay, ManualDisplay };
            destPicker.SelectedItem = _dimTargetType == "SlabEdge" ? SlabDisplay
                                    : _dimTargetType == "ManualDatum" ? ManualDisplay : GridDisplay;
            outer.Children.Add(destPicker);

            // Manual-datum note — shown only for that destination.
            var manualSection = new StackPanel();
            AddDim(manualSection, "Manual: you'll pick one datum edge per view when the dimension pass starts.");
            outer.Children.Add(manualSection);

            // Slab-edge config — shown only for that destination. Default targets each clash's own
            // Group 2 element; the buttons override every clash with one specific floor.
            var slabSection = new StackPanel();
            AddDivider(slabSection);
            AddLabel(slabSection, "Slab edge mode dimensions each clash to the EDGE of the exact element it hit "
                          + "(the Group 2 element — slab, wall, etc.). Optionally override with one specific floor for every clash.");
            var slabStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            slabStatus.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            slabStatus.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            slabStatus.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            Action refreshSlab = () => slabStatus.Text = _pickedSlab == null
                ? "Default: dimensions to each clash's own Group 2 element edge (legacy markers fall back to nearest floor edge)."
                : $"Override — every clash dimensions to: {_pickedSlabName}";
            refreshSlab();

            var slabRow = new StackPanel { Orientation = Orientation.Horizontal };
            var pickBtn = LemoineControlStyles.BuildButton("Pick host slab…", LemoineControlStyles.LemoineButtonVariant.Primary);
            pickBtn.Margin = new Thickness(0, 0, 8, 0);
            pickBtn.Click += (s, e) => StartSlabPick(((Button)s!).Dispatcher, refreshSlab, inLinks: false);
            var pickLinkBtn = LemoineControlStyles.BuildButton("Pick linked slab…", LemoineControlStyles.LemoineButtonVariant.Primary);
            pickLinkBtn.Margin = new Thickness(0, 0, 8, 0);
            pickLinkBtn.Click += (s, e) => StartSlabPick(((Button)s!).Dispatcher, refreshSlab, inLinks: true);
            var clearBtn = LemoineControlStyles.BuildButton("Clear", LemoineControlStyles.LemoineButtonVariant.Ghost);
            clearBtn.Click += (s, e) => { _pickedSlab = null; _pickedSlabName = ""; refreshSlab(); Fire(); };
            slabRow.Children.Add(pickBtn);
            slabRow.Children.Add(pickLinkBtn);
            slabRow.Children.Add(clearBtn);
            slabSection.Children.Add(slabRow);
            slabSection.Children.Add(slabStatus);
            outer.Children.Add(slabSection);

            // Reveal only the config relevant to the chosen destination (Grid shows neither), and keep
            // it in sync when the destination changes — the step content is built once, so toggle here.
            Action syncDestSections = () =>
            {
                manualSection.Visibility = _dimTargetType == "ManualDatum"
                    ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                slabSection.Visibility = _dimTargetType == "SlabEdge"
                    ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            };
            destPicker.SelectionChanged += sel =>
            {
                _dimTargetType = sel == SlabDisplay ? "SlabEdge"
                               : sel == ManualDisplay ? "ManualDatum" : "Grid";
                syncDestSections();
                Fire();
            };
            syncDestSections();

            return WrapInScroll(outer);
        }

        // ── Validation / summaries ────────────────────────────────────────────
        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedDefDisplays.Count > 0;
                case "S2": return _selectedViewIds.Count > 0;
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedDefDisplays.Count == 0 ? "—" : $"{_selectedDefDisplays.Count} definition(s)";
                case "S2": return _selectedViewIds.Count     == 0 ? "—" : $"{_selectedViewIds.Count} view(s)";
                case "S3":
                {
                    var bits = new List<string>();
                    if (_clearPrevious) bits.Add("clear");
                    bits.Add(_roundSizeMm > 0 ? $"+{_roundSizeMm / MmPerInch:0.##} in" : "exact size");
                    return string.Join(" · ", bits);
                }
                case "S4":
                    return _runDimensionPass
                        ? $"dim → {(_dimTargetType == "SlabEdge" ? "slab edge" : _dimTargetType == "ManualDatum" ? "picked edge" : "grid")}"
                          + (_adoptUserCallouts ? " · my callouts" : "")
                        : "dim pass off";
                case "S5": return "Ready to run";
                default: return "—";
            }
        }

        // ── ILemoineReviewable — framework renders the review step ────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("defs",   "Definitions"),
            ("views",  "Views"),
            ("marker", "Marker"),
            ("dim",    "Dimensioning"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["defs"]   = _selectedDefDisplays.Count > 0 ? $"{_selectedDefDisplays.Count} definition(s)" : "—",
            ["views"]  = _selectedViewIds.Count     > 0 ? $"{_selectedViewIds.Count} view(s)"          : "—",
            ["marker"] = _roundSizeMm > 0 ? $"+{_roundSizeMm / MmPerInch:0.##} in oversize" : "exact element size",
            ["dim"]    = _runDimensionPass
                ? $"{(_dimTargetType == "SlabEdge" ? "slab edge" : _dimTargetType == "ManualDatum" ? "picked edge" : "grid")}"
                : "off",
        };

        public IList<string>? ReviewChips
        {
            get
            {
                var chips = new List<string>();
                if (_clearPrevious) chips.Add("clear previous");
                if (_runDimensionPass && _adoptUserCallouts) chips.Add("my callouts = groups");
                if (_runDimensionPass && _dimTargetType == "SlabEdge")
                    chips.Add(_pickedSlab != null ? $"slab: {_pickedSlabName}" : "slab: clashed element");
                if (_runDimensionPass) chips.Add($"callouts ≤ 1:{_maxCalloutScale}");
                return chips.Count > 0 ? chips : null;
            }
        }

        public string? ReviewNote => "Detects clashes from the selected definitions across the chosen "
            + "views, places coloured ES-tagged markers, and (if the dimension pass is on) dimensions "
            + "each run out to the chosen destination.";

        public string? ReviewWarning => _runDimensionPass && _dimTargetType == "ManualDatum"
            ? "Manual datum: you'll be prompted to pick one edge per view once the run starts."
            : null;

        // ── Run ───────────────────────────────────────────────────────────────
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            if (_handler == null || _event == null) return;

            _resultChips = null;   // clear any breakdown from a previous run

            _handler.Definitions = _selectedDefDisplays
                .Where(d => _defDisplayToDef.ContainsKey(d))
                .Select(d => _defDisplayToDef[d])
                .ToList();
            _handler.ViewIds = _selectedViewIds
                .Select(id => new ElementId(id))
                .ToList();
            _handler.ClearPrevious     = _clearPrevious;
            _handler.RunDimensionPass  = _runDimensionPass;
            _handler.AdoptUserCallouts = _adoptUserCallouts;
            _handler.DimTargetType     = _dimTargetType;
            _handler.MaxCalloutScale   = _maxCalloutScale;
            _handler.RoundSizeMm      = _roundSizeMm;
            _handler.SlabScopes = _pickedSlab != null
                ? new List<AutoDimension.Resolvers.SlabScope> { _pickedSlab }
                : new List<AutoDimension.Resolvers.SlabScope>();
            _handler.PushLog          = pushLog;
            _handler.OnProgress       = onProgress;
            _handler.OnComplete       = onComplete;
            _handler.OnResultChips    = chips => _resultChips = chips;

            _event.Raise();
        }

        // Raises the slab-pick external event; the picked floor (host or linked) comes back on
        // Revit's main thread and is marshalled to this window's dispatcher. The pick brings Revit's
        // main window forward, so we re-activate the wizard once it resolves (or is cancelled).
        private void StartSlabPick(System.Windows.Threading.Dispatcher disp, Action refresh, bool inLinks)
        {
            if (_slabPickHandler == null || _slabPickEvent == null) return;
            _slabPickHandler.InLinks = inLinks;
            _slabPickHandler.OnPicked = (scope, name) =>
            {
                // The pick resolves on Revit's main thread and can outlive this window — an
                // unguarded BeginInvoke on a terminated dispatcher hard-crashes Revit.
                if (disp.HasShutdownStarted || disp.HasShutdownFinished) return;
                disp.BeginInvoke(new Action(() =>
                {
                    if (scope != null)   // null = cancelled / not a floor — keep the prior choice
                    {
                        _pickedSlab = scope;
                        _pickedSlabName = name;
                        refresh();
                        Fire();
                    }
                    _activateWindow?.Invoke();   // pull the wizard back in front of Revit
                }));
            };
            _slabPickEvent.Raise();
        }

        // ── UI helpers ────────────────────────────────────────────────────────
        private static void AddLabel(StackPanel parent, string text)
        {
            var lbl = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            parent.Children.Add(lbl);
        }

        private static void AddDim(StackPanel parent, string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            parent.Children.Add(tb);
        }

        private static void AddDivider(StackPanel parent)
        {
            var sep = new System.Windows.Shapes.Rectangle { Height = 1, Margin = new Thickness(0, 8, 0, 8) };
            sep.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "LemoineBorder");
            parent.Children.Add(sep);
        }

        private static void AddStepperRow(StackPanel parent, string label, string hint,
            double value, double min, double max, double step, int decimals, Action<double> onChange)
        {
            AddLabel(parent, label);

            var stepper = new LemoineInlineStepper
            {
                Value               = value,
                MinValue            = min,
                MaxValue            = max,
                Step                = step,
                Decimals            = decimals,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin              = new Thickness(0, 0, 0, 2),
                ToolTip             = hint,
            };
            stepper.ValueChanged += (s, v) => onChange(v);
            parent.Children.Add(stepper);

            AddDim(parent, hint);
        }
    }
}
