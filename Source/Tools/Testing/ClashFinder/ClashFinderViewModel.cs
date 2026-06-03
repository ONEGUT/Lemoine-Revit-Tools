using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.Testing
{
    /// <summary>
    /// Clash Finder &amp; Dimension wizard: pick saved definition(s) + views, detect clashes
    /// and place coloured ES-tagged markers. A checkbox controls whether the (discovery-only)
    /// dimension pass runs afterward. The actual work happens in <see cref="ClashFinderEventHandler"/>.
    /// </summary>
    public class ClashFinderViewModel : ILemoineTool
    {
        public string Title    => "Clash Finder & Dimension";
        public string RunLabel => "Find & Mark Clashes →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Definitions", required: true),
            new StepDefinition("S2", "Select Views",       required: true),
            new StepDefinition("S3", "Options & Run",      required: false),
        };

        public event EventHandler? ValidationChanged;
        private void Fire() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly ClashFinderEventHandler? _handler;
        private readonly ExternalEvent?           _event;
        private readonly AutoDimension.SlabPickEventHandler? _slabPickHandler;
        private readonly ExternalEvent?                      _slabPickEvent;

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
        private bool _runDimensionPass = false;
        private double _storeyMarginMm = 600.0;   // sub-floor depth still counted as a level's storey (slabs/structure)
        private double _roundSizeMm    = 0.0;     // round marker diameter; 0 = auto-fit to the clash

        // Dimension-pass destination + chaining, seeded from the shared auto-dimension config.
        private const string GridDisplay   = "To Grid";
        private const string SlabDisplay   = "To Slab Edge";
        private const string ManualDisplay = "To Picked Edge";
        private string _dimTargetType =
            string.Equals(AutoDimension.AutoDimensionConfig.Instance.TargetType, "SlabEdge", StringComparison.OrdinalIgnoreCase) ? "SlabEdge"
          : string.Equals(AutoDimension.AutoDimensionConfig.Instance.TargetType, "ManualDatum", StringComparison.OrdinalIgnoreCase) ? "ManualDatum"
          : "Grid";
        private bool   _chainAligned     = AutoDimension.AutoDimensionConfig.Instance.ChainAligned;
        private double _chainGapMm       = AutoDimension.AutoDimensionConfig.Instance.ChainMaxGapMm;
        private double _chainCollinearMm = AutoDimension.AutoDimensionConfig.Instance.ChainCollinearToleranceMm;

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
                case "S3": return BuildOptionsStep();
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

        // ── S3 — Options & Run ────────────────────────────────────────────────
        private FrameworkElement BuildOptionsStep()
        {
            var outer = new StackPanel();

            var toggles = new LemoineToggleSwitches();
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "clear", Label = "Clear previous clash markers before running",
                                 Desc = "Removes earlier Lemoine clash markers in the selected views first.", DefaultOn = _clearPrevious },
                new ToggleItem { Id = "allDocs", Label = "Scan all documents (ignore saved source filter)",
                                 Desc = "Overrides each definition's saved source documents and scans every loaded model.", DefaultOn = _showAllDocuments },
                new ToggleItem { Id = "dimPass", Label = "Place dimensions after marking",
                                 Desc = "Runs the auto-dimension engine on the clash markers, dimensioning each out to the chosen destination below.", DefaultOn = _runDimensionPass },
                new ToggleItem { Id = "chain", Label = "Chain aligned clash dimensions",
                                 Desc = "Merge collinear clashes that sit close together into one multi-segment string instead of separate dimensions.", DefaultOn = _chainAligned },
            });
            toggles.StateChanged += state =>
            {
                state.TryGetValue("clear",   out _clearPrevious);
                state.TryGetValue("allDocs", out _showAllDocuments);
                state.TryGetValue("dimPass", out _runDimensionPass);
                state.TryGetValue("chain",   out _chainAligned);
                Fire();
            };
            outer.Children.Add(toggles);

            AddDivider(outer);
            AddStepperRow(outer,
                "Storey depth margin (mm)",
                "Clashes within this depth below a level still count as that level's storey, so slabs and structure hanging just under the floor are marked on its plan. Increase if penetrations are missed; 0 cuts exactly at the level.",
                _storeyMarginMm, min: 0, max: 3000, step: 50, decimals: 0,
                v => { _storeyMarginMm = v; Fire(); });

            AddDivider(outer);
            AddStepperRow(outer,
                "Round marker size (mm, 0 = auto-fit)",
                "Diameter of the round drawn at each clash. 0 fits the round to the clash; set a value to force every round to a fixed size — e.g. the opening needed for the pipe.",
                _roundSizeMm, min: 0, max: 1000, step: 25, decimals: 0,
                v => { _roundSizeMm = v; Fire(); });

            AddDivider(outer);
            AddLabel(outer, "Dimension destination (used when the dimension pass is on).");
            var destPicker = new LemoineSingleSelect { Label = "Destination" };
            destPicker.Items = new List<string> { GridDisplay, SlabDisplay, ManualDisplay };
            destPicker.SelectedItem = _dimTargetType == "SlabEdge" ? SlabDisplay
                                    : _dimTargetType == "ManualDatum" ? ManualDisplay : GridDisplay;
            destPicker.SelectionChanged += sel =>
            {
                _dimTargetType = sel == SlabDisplay ? "SlabEdge"
                               : sel == ManualDisplay ? "ManualDatum" : "Grid";
                Fire();
            };
            outer.Children.Add(destPicker);
            if (_dimTargetType == "ManualDatum")
                AddDim(outer, "Manual: you'll pick one datum edge per view when the dimension pass starts.");

            // Up-front slab pick — used in slab-edge mode; applies to every selected view.
            AddDivider(outer);
            AddLabel(outer, "Slab to dimension to (slab-edge mode). Pick a host floor, or a floor inside a Revit link.");
            var slabStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            slabStatus.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            slabStatus.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            slabStatus.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            Action refreshSlab = () => slabStatus.Text = _pickedSlab == null
                ? "No slab picked — scans all floors (nearest edge per axis)."
                : $"Slab: {_pickedSlabName}";
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
            outer.Children.Add(slabRow);
            outer.Children.Add(slabStatus);

            AddDivider(outer);
            AddStepperRow(outer,
                "Chain max gap (mm)",
                "Clashes farther apart than this along a run are not chained into the same string.",
                _chainGapMm, min: 0, max: 10000, step: 100, decimals: 0,
                v => { _chainGapMm = v; Fire(); });
            AddStepperRow(outer,
                "Chain alignment tolerance (mm)",
                "How far a clash may sit off the shared baseline and still count as in line for chaining.",
                _chainCollinearMm, min: 0, max: 2000, step: 25, decimals: 0,
                v => { _chainCollinearMm = v; Fire(); });

            AddDivider(outer);
            AddDim(outer, $"{_selectedDefDisplays.Count} definition(s) · {_selectedViewNames.Count} view(s) selected.");

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
                    var bits = new List<string>();
                    if (_clearPrevious)    bits.Add("clear");
                    if (_showAllDocuments) bits.Add("all docs");
                    bits.Add(_runDimensionPass
                        ? $"dim → {(_dimTargetType == "SlabEdge" ? "slab edge" : _dimTargetType == "ManualDatum" ? "picked edge" : "grid")}{(_chainAligned ? " · chained" : "")}"
                        : "dim pass off");
                    bits.Add(_roundSizeMm > 0 ? $"round {_roundSizeMm:F0} mm" : "round auto");
                    return string.Join(" · ", bits);
                default: return "—";
            }
        }

        // ── Run ───────────────────────────────────────────────────────────────
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            if (_handler == null || _event == null) return;

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
            _handler.DimChainAligned    = _chainAligned;
            _handler.DimChainMaxGapMm   = _chainGapMm;
            _handler.DimChainCollinearMm = _chainCollinearMm;
            _handler.SlabScopes = _pickedSlab != null
                ? new List<AutoDimension.Resolvers.SlabScope> { _pickedSlab }
                : new List<AutoDimension.Resolvers.SlabScope>();
            _handler.PushLog          = pushLog;
            _handler.OnProgress       = onProgress;
            _handler.OnComplete       = onComplete;

            _event.Raise();
        }

        // Raises the slab-pick external event; the picked floor (host or linked) comes back on
        // Revit's main thread and is marshalled to this window's dispatcher.
        private void StartSlabPick(System.Windows.Threading.Dispatcher disp, Action refresh, bool inLinks)
        {
            if (_slabPickHandler == null || _slabPickEvent == null) return;
            _slabPickHandler.InLinks = inLinks;
            _slabPickHandler.OnPicked = (scope, name) =>
                disp.BeginInvoke(new Action(() =>
                {
                    if (scope == null) return;   // cancelled / not a floor — keep the prior choice
                    _pickedSlab = scope;
                    _pickedSlabName = name;
                    refresh();
                    Fire();
                }));
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
