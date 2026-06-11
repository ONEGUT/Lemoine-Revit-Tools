using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Tools.Clash;

namespace LemoineTools.Tools.Testing
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

        public string Title    => "Clash Finder & Elevation Tag";
        public string RunLabel => "Find & Tag Clashes →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Definitions",     required: true),
            new StepDefinition("S2", "Select Views",           required: true),
            new StepDefinition("S3", "Marker & Tag Settings",  required: false),
            new StepDefinition("S4", "Review & Run",           required: false),
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
        private List<string> _selectedViewNames   = new List<string>();
        private readonly Dictionary<string, ElementId> _viewNameToId = new Dictionary<string, ElementId>();

        private bool   _clearPrevious    = true;
        private double _roundSizeMm      = 0.0;     // round marker oversize; 0 = exact element size
        private string _anchorMode       = "Centre"; // "Top" | "Centre" | "Bottom"
        private ElementId _spotTypeId    = ElementId.InvalidElementId;

        private const string AnchorTop    = "Top of round";
        private const string AnchorCentre = "Centre of round";
        private const string AnchorBottom = "Bottom of round";

        public ClashElevationFinderViewModel(
            ClashElevationFinderEventHandler? handler,
            ExternalEvent?                    externalEvent,
            List<View>                        allViews,
            List<ClashDefinition>             definitions,
            List<(string Name, ElementId Id)> spotTypes)
        {
            _handler     = handler;
            _event       = externalEvent;
            _allViews    = allViews    ?? new List<View>();
            _definitions = definitions ?? new List<ClashDefinition>();
            _spotTypes   = spotTypes   ?? new List<(string, ElementId)>();

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

        // ── S2 — Select Views (sections + elevations) ─────────────────────────
        private FrameworkElement BuildViewsStep()
        {
            var outer = new StackPanel();
            AddLabel(outer, "Pick the section and/or elevation views to mark and tag.");
            var tabs = new LemoineMultiSelectTabs();
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
                ["Sections"]   = new List<string>(),
                ["Elevations"] = new List<string>(),
            };

            foreach (var v in _allViews)
            {
                if (!_viewNameToId.ContainsKey(v.Name)) _viewNameToId[v.Name] = v.Id;
                if (v.ViewType == ViewType.Elevation)
                    groups["Elevations"].Add(v.Name);
                else
                    groups["Sections"].Add(v.Name);
            }

            foreach (var k in groups.Keys.Where(k => groups[k].Count == 0).ToList())
                groups.Remove(k);

            if (groups.Count == 0) groups["(No section or elevation views)"] = new List<string>();
            return groups;
        }

        // ── S3 — Marker & Tag Settings ────────────────────────────────────────
        private FrameworkElement BuildSettingsStep()
        {
            var outer = new StackPanel();

            var toggles = new LemoineToggleSwitches();
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "clear", Label = "Clear previous clash markers before running",
                                 Desc = "Removes earlier Lemoine clash markers and tags in the selected views first.", DefaultOn = _clearPrevious },
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
                "Added to the Group 1 element's own cross-section. 0 draws the marker at the element's exact size (round pipe Ø, or rectangular-duct W×H); a value enlarges every marker by that much. Top/bottom tag elevations are measured at the marker's edge, so the oversize also sets how far above/below the clash centre they read.",
                _roundSizeMm / MmPerInch, min: 0, max: 40, step: 0.25, decimals: 2,
                v => { _roundSizeMm = v * MmPerInch; Fire(); });

            AddDivider(outer);
            AddLabel(outer, "Where on the round to place the elevation tag.");
            var anchorPicker = new LemoineSingleSelect { Label = "Tag position" };
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
            AddLabel(outer, "Spot elevation type used for each tag.");
            if (_spotTypes.Count == 0)
            {
                AddDim(outer, "No spot elevation type found in this project. Create one (Annotate → Spot Elevation) and re-open this tool — markers will still be placed, but without tags.");
            }
            else
            {
                var typePicker = new LemoineSingleSelect { Label = "Spot type" };
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
                    if (_clearPrevious) bits.Add("clear");
                    bits.Add($"tag {(_anchorMode == "Top" ? "top" : _anchorMode == "Bottom" ? "bottom" : "centre")}");
                    bits.Add(_roundSizeMm > 0 ? $"+{_roundSizeMm / MmPerInch:0.##} in" : "exact size");
                    return string.Join(" · ", bits);
                }
                case "S4": return "Ready to run";
                default: return "—";
            }
        }

        // ── ILemoineReviewable — framework renders the review step ────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("defs",   "Definitions"),
            ("views",  "Views"),
            ("marker", "Marker"),
            ("tag",    "Tag"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["defs"]   = _selectedDefDisplays.Count > 0 ? $"{_selectedDefDisplays.Count} definition(s)" : "—",
            ["views"]  = _selectedViewNames.Count   > 0 ? $"{_selectedViewNames.Count} view(s)"        : "—",
            ["marker"] = _roundSizeMm > 0 ? $"+{_roundSizeMm / MmPerInch:0.##} in oversize" : "exact element size",
            ["tag"]    = $"{(_anchorMode == "Top" ? "top" : _anchorMode == "Bottom" ? "bottom" : "centre")}"
                + (_spotTypes.Count > 0
                    ? $" · {_spotTypes.FirstOrDefault(t => t.Id == _spotTypeId).Name ?? _spotTypes[0].Name}"
                    : " · no spot type"),
        };

        public IList<string>? ReviewChips
        {
            get
            {
                var chips = new List<string>();
                if (_clearPrevious) chips.Add("clear previous");
                return chips.Count > 0 ? chips : null;
            }
        }

        public string? ReviewNote => "Detects clashes from the selected definitions across the chosen "
            + "sections and elevations, places coloured round markers, and tags each with a spot "
            + "elevation at the chosen position on the round.";

        public string? ReviewWarning => _spotTypes.Count == 0
            ? "No spot elevation type found — markers will be placed but cannot be tagged."
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
