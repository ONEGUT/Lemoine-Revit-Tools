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
    /// Clash Finder &amp; Elevation Tag wizard: pick saved definition(s) + section/elevation views,
    /// detect clashes and place coloured round markers, then tag each round with a spot elevation at
    /// its top, centre, or bottom. The actual work happens in <see cref="ClashElevationFinderEventHandler"/>.
    /// </summary>
    public class ClashElevationFinderViewModel : ILemoineTool
    {
        public string Title    => "Clash Finder & Elevation Tag";
        public string RunLabel => "Find & Tag Clashes →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Definitions", required: true),
            new StepDefinition("S2", "Select Views",       required: true),
            new StepDefinition("S3", "Options & Run",      required: false),
        };

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
        private bool   _showAllDocuments = false;
        private double _roundSizeMm      = 0.0;     // round marker diameter; 0 = auto-fit to the clash
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

        // ── S3 — Options & Run ────────────────────────────────────────────────
        private FrameworkElement BuildOptionsStep()
        {
            var outer = new StackPanel();

            var toggles = new LemoineToggleSwitches();
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "clear", Label = "Clear previous clash markers before running",
                                 Desc = "Removes earlier Lemoine clash markers and tags in the selected views first.", DefaultOn = _clearPrevious },
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

            AddDivider(outer);
            AddStepperRow(outer,
                "Round marker size (mm, 0 = match Group 1 element)",
                "Diameter of the round drawn at each clash. 0 matches the Group 1 element's own size (e.g. the pipe diameter); set a value to force every round to a fixed size. Top/bottom tag elevations are measured at this round's edge, so the size sets how far above/below the clash centre they read.",
                _roundSizeMm, min: 0, max: 1000, step: 25, decimals: 0,
                v => { _roundSizeMm = v; Fire(); });

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
                    bits.Add($"tag {(_anchorMode == "Top" ? "top" : _anchorMode == "Bottom" ? "bottom" : "centre")}");
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
            _handler.AnchorMode       = _anchorMode;
            _handler.SpotTypeId       = _spotTypeId;
            _handler.RoundSizeMm      = _roundSizeMm;
            _handler.PushLog          = pushLog;
            _handler.OnProgress       = onProgress;
            _handler.OnComplete       = onComplete;

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
