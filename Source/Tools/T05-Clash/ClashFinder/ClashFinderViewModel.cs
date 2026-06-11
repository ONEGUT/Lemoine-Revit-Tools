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
        private const double MmPerFoot = 304.8;
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
        private List<string> _selectedViewNames    = new List<string>();
        private readonly Dictionary<string, ElementId> _viewNameToId = new Dictionary<string, ElementId>();

        private bool _clearPrevious    = true;
        private bool _showAllDocuments = false;
        private bool _runDimensionPass = true;   // dimensioning is the point — on by default
        private double _storeyMarginMm = 609.6;   // 2 ft: sub-floor depth still counted as a level's storey
        private double _roundSizeMm    = 0.0;     // round marker oversize; 0 = exact element size

        // Dimension-pass destination + chaining, seeded from the shared auto-dimension config.
        private const string GridDisplay   = "To Grid";
        private const string SlabDisplay   = "To Slab Edge";
        private const string ManualDisplay = "To Picked Edge";
        private string _dimTargetType =
            string.Equals(AutoDimension.AutoDimensionConfig.Instance.TargetType, "SlabEdge", StringComparison.OrdinalIgnoreCase) ? "SlabEdge"
          : string.Equals(AutoDimension.AutoDimensionConfig.Instance.TargetType, "ManualDatum", StringComparison.OrdinalIgnoreCase) ? "ManualDatum"
          : "Grid";
        private bool   _chainAligned  = AutoDimension.AutoDimensionConfig.Instance.ChainAligned;
        private bool   _denseCallouts = AutoDimension.AutoDimensionConfig.Instance.DenseCalloutsEnabled;
        private double _runGapFt      = AutoDimension.AutoDimensionConfig.Instance.RunGapFt;
        private double _runCrossFt    = AutoDimension.AutoDimensionConfig.Instance.RunCrossToleranceFt;

        public ClashFinderViewModel(
            ClashFinderEventHandler? handler,
            ExternalEvent?           externalEvent,
            List<View>               allViews,
            List<ClashDefinition>    definitions,
            AutoDimension.SlabPickEventHandler? slabPickHandler = null,
            ExternalEvent?                      slabPickEvent   = null)
        {
            _handler          = handler;
            _event            = externalEvent;
            _slabPickHandler  = slabPickHandler;
            _slabPickEvent    = slabPickEvent;
            _allViews    = allViews ?? new List<View>();
            _definitions = definitions ?? new List<ClashDefinition>();

            var used = new HashSet<string>();
            foreach (var def in _definitions)
            {
                string baseName = string.IsNullOrWhiteSpace(def.Name) ? "(unnamed)" : def.Name;
                string display  = baseName;
                int n = 2;
                while (!used.Add(display)) display = $"{baseName} ({n++})";
                _defDisplayToDef[display] = def;
            }

            foreach (var v in _allViews)
                if (!_viewNameToId.ContainsKey(v.Name))
                    _viewNameToId[v.Name] = v.Id;
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
            var outer = new StackPanel();
            var tabs  = new LemoineMultiSelectTabs();
            tabs.SelectionChanged += selected =>
            {
                _selectedViewNames = new List<string>(selected);
                Fire();
            };
            tabs.SetGroups(BuildViewGroups(), _selectedViewNames);
            outer.Children.Add(tabs);
            return WrapInScroll(outer);
        }

        private Dictionary<string, List<string>> BuildViewGroups()
        {
            var groups = new Dictionary<string, List<string>>
            {
                ["Floor Plans"]             = new List<string>(),
                ["Reflected Ceiling Plans"] = new List<string>(),
            };

            foreach (var v in _allViews)
            {
                if (!_viewNameToId.ContainsKey(v.Name)) _viewNameToId[v.Name] = v.Id;
                if (v.ViewType == ViewType.CeilingPlan)
                    groups["Reflected Ceiling Plans"].Add(v.Name);
                else
                    groups["Floor Plans"].Add(v.Name);
            }

            foreach (var k in groups.Keys.Where(k => groups[k].Count == 0).ToList())
                groups.Remove(k);

            if (groups.Count == 0) groups["(No plan views)"] = new List<string>();
            return groups;
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
                new ToggleItem { Id = "allDocs", Label = "Scan all documents (ignore saved source filter)",
                                 Desc = "Overrides each definition's saved source documents and scans every loaded model.", DefaultOn = _showAllDocuments },
            });
            toggles.StateChanged += state =>
            {
                state.TryGetValue("clear",   out _clearPrevious);
                state.TryGetValue("allDocs", out _showAllDocuments);
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
            AddStepperRow(outer,
                "Storey depth margin (ft)",
                "Clashes within this depth below a level still count as that level's storey, so slabs and structure hanging just under the floor are marked on its plan. Increase if penetrations are missed; 0 cuts exactly at the level.",
                _storeyMarginMm / MmPerFoot, min: 0, max: 12, step: 0.5, decimals: 2,
                v => { _storeyMarginMm = v * MmPerFoot; Fire(); });

            return WrapInScroll(outer);
        }

        // ── S4 — Per-run Dimensioning ─────────────────────────────────────────
        private FrameworkElement BuildDimensioningStep()
        {
            var outer = new StackPanel();

            AddDim(outer, "Per-run overrides — these start from the saved defaults "
                        + "(Settings → Dimensions) and apply to this run only; the defaults are never changed here.");
            AddDivider(outer);

            var toggles = new LemoineToggleSwitches();
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "dimPass", Label = "Place dimensions after marking",
                                 Desc = "Runs the auto-dimension engine on the clash markers, dimensioning each out to the chosen destination below.", DefaultOn = _runDimensionPass },
                new ToggleItem { Id = "chain", Label = "Chain aligned clash dimensions",
                                 Desc = "Merge collinear clashes that sit close together into one multi-segment string instead of separate dimensions.", DefaultOn = _chainAligned },
                new ToggleItem { Id = "callouts", Label = "Enlarged callouts for extreme density",
                                 Desc = "Areas too dense even for chained strings get a plan callout at a computed larger scale; those clashes are dimensioned in the callout instead of this view.", DefaultOn = _denseCallouts },
            });
            toggles.StateChanged += state =>
            {
                state.TryGetValue("dimPass",  out _runDimensionPass);
                state.TryGetValue("chain",    out _chainAligned);
                state.TryGetValue("callouts", out _denseCallouts);
                Fire();
            };
            outer.Children.Add(toggles);

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

            AddDivider(outer);
            AddStepperRow(outer,
                "Group reach along run (ft)",
                "Clashes within this distance of each other ALONG a shared line group into one run (one chained dimension). Farther apart starts a new run. Near-misses are reported in the run log.",
                _runGapFt, min: 0, max: 40, step: 0.5, decimals: 2,
                v => { _runGapFt = v; Fire(); });
            AddStepperRow(outer,
                "Line tolerance (ft)",
                "How far a clash may sit OFF the run's line and still join it. Also the across-run snap: members within this share one dimension.",
                _runCrossFt, min: 0, max: 8, step: 0.25, decimals: 2,
                v => { _runCrossFt = v; Fire(); });

            return WrapInScroll(outer);
        }

        // ── Validation / summaries ────────────────────────────────────────────
        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedDefDisplays.Count > 0;
                case "S2": return _selectedViewNames.Count > 0;
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedDefDisplays.Count == 0 ? "—" : $"{_selectedDefDisplays.Count} definition(s)";
                case "S2": return _selectedViewNames.Count   == 0 ? "—" : $"{_selectedViewNames.Count} view(s)";
                case "S3":
                {
                    var bits = new List<string>();
                    if (_clearPrevious)    bits.Add("clear");
                    if (_showAllDocuments) bits.Add("all docs");
                    bits.Add(_roundSizeMm > 0 ? $"+{_roundSizeMm / MmPerInch:0.##} in" : "exact size");
                    return string.Join(" · ", bits);
                }
                case "S4":
                    return _runDimensionPass
                        ? $"dim → {(_dimTargetType == "SlabEdge" ? "slab edge" : _dimTargetType == "ManualDatum" ? "picked edge" : "grid")}{(_chainAligned ? $" · grouped ({_runGapFt:0.#} ft / {_runCrossFt:0.##} ft)" : "")}"
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
            ["views"]  = _selectedViewNames.Count   > 0 ? $"{_selectedViewNames.Count} view(s)"        : "—",
            ["marker"] = _roundSizeMm > 0 ? $"+{_roundSizeMm / MmPerInch:0.##} in oversize" : "exact element size",
            ["dim"]    = _runDimensionPass
                ? $"{(_dimTargetType == "SlabEdge" ? "slab edge" : _dimTargetType == "ManualDatum" ? "picked edge" : "grid")}"
                  + (_chainAligned ? $" · grouped, reach {_runGapFt:0.#} ft / line tol {_runCrossFt:0.##} ft" : " · ungrouped")
                : "off",
        };

        public IList<string>? ReviewChips
        {
            get
            {
                var chips = new List<string>();
                if (_clearPrevious)    chips.Add("clear previous");
                if (_showAllDocuments) chips.Add("all documents");
                if (_runDimensionPass && _dimTargetType == "SlabEdge")
                    chips.Add(_pickedSlab != null ? $"slab: {_pickedSlabName}" : "slab: clashed element");
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
            _handler.ViewIds = _selectedViewNames
                .Where(n => _viewNameToId.ContainsKey(n))
                .Select(n => _viewNameToId[n])
                .ToList();
            _handler.ClearPrevious    = _clearPrevious;
            _handler.ShowAllDocuments = _showAllDocuments;
            _handler.RunDimensionPass = _runDimensionPass;
            _handler.DimTargetType    = _dimTargetType;
            _handler.StoreyMarginMm   = _storeyMarginMm;
            _handler.RoundSizeMm        = _roundSizeMm;
            _handler.DimChainAligned  = _chainAligned;
            _handler.DimDenseCallouts = _denseCallouts;
            _handler.DimRunGapFt      = _runGapFt;
            _handler.DimRunCrossFt    = _runCrossFt;
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
