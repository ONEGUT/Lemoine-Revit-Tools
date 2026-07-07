using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Tools.Dimensioning;

namespace LemoineTools.Tools.Dimensioning
{
    /// <summary>
    /// Clash Finder &amp; Elevation Tag wizard: pick saved definition(s) + section/elevation views,
    /// detect clashes and place coloured round markers, then tag each round with a spot elevation at
    /// its top, centre, or bottom. Four steps: definitions · views · marker &amp; tag settings ·
    /// review &amp; run. Marker oversize is edited in inches but stored internally in millimetres.
    /// The actual work happens in <see cref="ClashElevationFinderEventHandler"/>.
    /// </summary>
    public class ClashElevationFinderViewModel : ILemoineTool, ILemoineReviewable, ILemoineRunResult, ILemoineToolCleanup
    {
        // Run strip: "markers" during the run, full marker/tag/failure breakdown on completion.
        public string? ResultNoun => "markers";
        private System.Collections.Generic.IReadOnlyList<LemoineTools.Lemoine.ResultChip>? _resultChips;
        public System.Collections.Generic.IReadOnlyList<LemoineTools.Lemoine.ResultChip>? ResultChips => _resultChips;

        // Null the callbacks parked on the static handler so this VM isn't retained after close.
        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.PushLog       = null;
            _handler.OnProgress    = null;
            _handler.OnComplete    = null;
            _handler.OnResultChips = null;
        }

        public string Title    => LemoineStrings.T("clash.elevationFinder.title");
        public string RunLabel => LemoineStrings.T("clash.elevationFinder.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", LemoineStrings.T("clash.elevationFinder.steps.S1"),     required: true),
            new StepDefinition("S2", LemoineStrings.T("clash.elevationFinder.steps.S2"),           required: true),
            new StepDefinition("S3", LemoineStrings.T("clash.elevationFinder.steps.S3"),  required: false),
            new StepDefinition("S4", LemoineStrings.T("clash.elevationFinder.steps.S4"),           required: false),
        };

        private const double MmPerInch = 25.4;

        public event EventHandler? ValidationChanged;
        private void Fire() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly ClashElevationFinderEventHandler? _handler;
        private readonly ExternalEvent?                     _event;

        // ── Data ──────────────────────────────────────────────────────────────
        private readonly List<View> _allViews;
        private readonly List<ClashDefinition> _definitions;
        private readonly Dictionary<string, ClashDefinition> _defDisplayToDef = new Dictionary<string, ClashDefinition>();
        private readonly List<(string Name, ElementId Id)> _spotTypes;
        private readonly Dictionary<string, ElementId> _spotTypeByName = new Dictionary<string, ElementId>();

        // ── State ─────────────────────────────────────────────────────────────
        private List<string> _selectedDefDisplays = new List<string>();
        private List<long>   _selectedViewIds     = new List<long>();
        private readonly LemoineBrowserTree _browserTree;

        private bool   _clearPrevious    = true;
        private double _roundSizeMm      = 0.0;     // round marker oversize; 0 = exact element size
        private string _anchorMode       = "Centre"; // "Top" | "Centre" | "Bottom"
        private ElementId _spotTypeId    = ElementId.InvalidElementId;

        private static readonly string AnchorTop    = LemoineStrings.T("clash.elevationFinder.labels.anchorTop");
        private static readonly string AnchorCentre = LemoineStrings.T("clash.elevationFinder.labels.anchorCenter");
        private static readonly string AnchorBottom = LemoineStrings.T("clash.elevationFinder.labels.anchorBottom");

        public ClashElevationFinderViewModel(
            ClashElevationFinderEventHandler? handler,
            ExternalEvent?                    externalEvent,
            List<View>                        allViews,
            List<ClashDefinition>             definitions,
            List<(string Name, ElementId Id)> spotTypes,
            LemoineBrowserTree?               browserTree = null)
        {
            _handler     = handler;
            _event       = externalEvent;
            _allViews    = allViews    ?? new List<View>();
            _definitions = definitions ?? new List<ClashDefinition>();
            _spotTypes   = spotTypes   ?? new List<(string, ElementId)>();
            _browserTree = browserTree ?? new LemoineBrowserTree();

            var used = new HashSet<string>();
            foreach (var def in _definitions)
            {
                string baseName = string.IsNullOrWhiteSpace(def.Name) ? LemoineStrings.T("clash.elevationFinder.labels.unnamed") : def.Name;
                string display  = baseName;
                int n = 2;
                while (!used.Add(display)) display = $"{baseName} ({n++})";
                _defDisplayToDef[display] = def;
            }

            foreach (var t in _spotTypes)
                if (!_spotTypeByName.ContainsKey(t.Name))
                    _spotTypeByName[t.Name] = t.Id;

            // Default to the first available spot elevation type.
            if (_spotTypes.Count > 0) _spotTypeId = _spotTypes[0].Id;
        }

        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildDefinitionsStep();
                case "S2": return BuildViewsStep();
                case "S3": return BuildSettingsStep();
                case "S4": return null;   // framework renders the review (ILemoineReviewable)
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
                AddDim(outer, LemoineStrings.T("clash.elevationFinder.labels.noDefs"));
                return WrapInScroll(outer);
            }

            AddLabel(outer, LemoineStrings.T("clash.elevationFinder.labels.pickDefs"));

            var groups = new Dictionary<string, List<string>>
            {
                [LemoineStrings.T("clash.elevationFinder.labels.groupSaved")] = _defDisplayToDef.Keys.ToList(),
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

        // ── S2 — Select Views (sections + elevations) ─────────────────────────
        private FrameworkElement BuildViewsStep()
        {
            var outer = new StackPanel();
            AddLabel(outer, LemoineStrings.T("clash.elevationFinder.labels.pickViews"));
            var picker = new LemoineBrowserTreePicker
            {
                Height         = 300,
                AccessibleName = LemoineStrings.T("clash.elevationFinder.labels.pickerName"),
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

        // ── S3 — Marker & Tag Settings ────────────────────────────────────────
        private FrameworkElement BuildSettingsStep()
        {
            var outer = new StackPanel();

            var toggles = new LemoineToggleSwitches();
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "clear", Label = LemoineStrings.T("clash.elevationFinder.labels.clearLabel"),
                                 Desc = LemoineStrings.T("clash.elevationFinder.labels.clearDesc"), DefaultOn = _clearPrevious },
            });
            toggles.StateChanged += state =>
            {
                state.TryGetValue("clear", out _clearPrevious);
                Fire();
            };
            outer.Children.Add(toggles);

            AddDivider(outer);
            AddStepperRow(outer,
                LemoineStrings.T("clash.elevationFinder.labels.oversizeLabel"),
                LemoineStrings.T("clash.elevationFinder.labels.oversizeHint"),
                _roundSizeMm / MmPerInch, min: 0, max: 40, step: 0.25, decimals: 2,
                v => { _roundSizeMm = v * MmPerInch; Fire(); });

            AddDivider(outer);
            AddLabel(outer, LemoineStrings.T("clash.elevationFinder.labels.tagWhere"));
            var anchorPicker = new LemoineSingleSelect { Label = LemoineStrings.T("clash.elevationFinder.labels.tagPosLabel") };
            anchorPicker.Items = new List<string> { AnchorTop, AnchorCentre, AnchorBottom };
            anchorPicker.SelectedItem = _anchorMode == "Top" ? AnchorTop
                                      : _anchorMode == "Bottom" ? AnchorBottom : AnchorCentre;
            anchorPicker.SelectionChanged += sel =>
            {
                _anchorMode = sel == AnchorTop ? "Top"
                            : sel == AnchorBottom ? "Bottom" : "Centre";
                Fire();
            };
            outer.Children.Add(anchorPicker);

            AddDivider(outer);
            AddLabel(outer, LemoineStrings.T("clash.elevationFinder.labels.spotWhich"));
            if (_spotTypes.Count == 0)
            {
                AddDim(outer, LemoineStrings.T("clash.elevationFinder.labels.noSpotType"));
            }
            else
            {
                var typePicker = new LemoineSingleSelect { Label = LemoineStrings.T("clash.elevationFinder.labels.spotTypeLabel") };
                typePicker.Items = _spotTypes.Select(t => t.Name).ToList();
                typePicker.SelectedItem = _spotTypes.FirstOrDefault(t => t.Id == _spotTypeId).Name ?? _spotTypes[0].Name;
                typePicker.SelectionChanged += sel =>
                {
                    if (sel != null && _spotTypeByName.TryGetValue(sel, out var id)) _spotTypeId = id;
                    Fire();
                };
                outer.Children.Add(typePicker);
            }

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
                case "S1": return _selectedDefDisplays.Count == 0 ? "—" : LemoineStrings.T("clash.elevationFinder.summaries.defCount", _selectedDefDisplays.Count);
                case "S2": return _selectedViewIds.Count     == 0 ? "—" : LemoineStrings.T("clash.elevationFinder.summaries.viewCount", _selectedViewIds.Count);
                case "S3":
                {
                    var bits = new List<string>();
                    if (_clearPrevious) bits.Add(LemoineStrings.T("clash.elevationFinder.summaries.clear"));
                    bits.Add(LemoineStrings.T("clash.elevationFinder.summaries.tag", _anchorMode == "Top" ? LemoineStrings.T("clash.elevationFinder.words.top") : _anchorMode == "Bottom" ? LemoineStrings.T("clash.elevationFinder.words.bottom") : LemoineStrings.T("clash.elevationFinder.words.center")));
                    bits.Add(_roundSizeMm > 0 ? LemoineStrings.T("clash.elevationFinder.summaries.oversize", _roundSizeMm / MmPerInch) : LemoineStrings.T("clash.elevationFinder.summaries.exactSize"));
                    return string.Join(" · ", bits);
                }
                case "S4": return LemoineStrings.T("clash.elevationFinder.summaries.S4");
                default: return "—";
            }
        }

        // ── ILemoineReviewable — framework renders the review step ────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("defs",   LemoineStrings.T("clash.elevationFinder.review.itemDefs")),
            ("views",  LemoineStrings.T("clash.elevationFinder.review.itemViews")),
            ("marker", LemoineStrings.T("clash.elevationFinder.review.itemMarker")),
            ("tag",    LemoineStrings.T("clash.elevationFinder.review.itemTag")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["defs"]   = _selectedDefDisplays.Count > 0 ? LemoineStrings.T("clash.elevationFinder.review.defsValue", _selectedDefDisplays.Count) : "—",
            ["views"]  = _selectedViewIds.Count     > 0 ? LemoineStrings.T("clash.elevationFinder.review.viewsValue", _selectedViewIds.Count)          : "—",
            ["marker"] = _roundSizeMm > 0 ? LemoineStrings.T("clash.elevationFinder.review.markerOversize", _roundSizeMm / MmPerInch) : LemoineStrings.T("clash.elevationFinder.review.markerExact"),
            ["tag"]    = (_anchorMode == "Top" ? LemoineStrings.T("clash.elevationFinder.words.top") : _anchorMode == "Bottom" ? LemoineStrings.T("clash.elevationFinder.words.bottom") : LemoineStrings.T("clash.elevationFinder.words.center"))
                + (_spotTypes.Count > 0
                    ? $" · {_spotTypes.FirstOrDefault(t => t.Id == _spotTypeId).Name ?? _spotTypes[0].Name}"
                    : LemoineStrings.T("clash.elevationFinder.review.tagNoSpot")),
        };

        public IList<string>? ReviewChips
        {
            get
            {
                var chips = new List<string>();
                if (_clearPrevious) chips.Add(LemoineStrings.T("clash.elevationFinder.review.chipClear"));
                return chips.Count > 0 ? chips : null;
            }
        }

        public string? ReviewNote => LemoineStrings.T("clash.elevationFinder.review.note");

        public string? ReviewWarning => _spotTypes.Count == 0
            ? LemoineStrings.T("clash.elevationFinder.review.warnNoSpot")
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
            _handler.ClearPrevious    = _clearPrevious;
            _handler.AnchorMode       = _anchorMode;
            _handler.SpotTypeId       = _spotTypeId;
            _handler.RoundSizeMm      = _roundSizeMm;
            _handler.PushLog          = pushLog;
            _handler.OnProgress       = onProgress;
            _handler.OnComplete       = onComplete;
            _handler.OnResultChips    = chips => _resultChips = chips;

            _event.Raise();
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
