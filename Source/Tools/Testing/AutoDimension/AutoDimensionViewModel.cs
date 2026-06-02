using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.Testing.AutoDimension
{
    /// <summary>
    /// Auto Dimension wizard: pick plan views, choose a destination type (Grid or Slab Edge),
    /// then place collision-aware dimensions from the Clash Finder's tagged cross-lines out to
    /// the resolved target. The work happens in <see cref="AutoDimensionEventHandler"/>; this
    /// view-model only gathers inputs and raises the external event.
    /// </summary>
    public class AutoDimensionViewModel : ILemoineTool
    {
        public string Title    => "Auto Dimension";
        public string RunLabel => "Place Dimensions →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Views",     required: true),
            new StepDefinition("S2", "Target & Options", required: false),
        };

        public event EventHandler? ValidationChanged;
        private void Fire() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly AutoDimensionEventHandler? _handler;
        private readonly ExternalEvent?             _event;

        // ── Data ──────────────────────────────────────────────────────────────
        private readonly List<View> _allViews;
        private readonly Dictionary<string, ElementId> _viewNameToId = new Dictionary<string, ElementId>();

        // ── State ─────────────────────────────────────────────────────────────
        private List<string> _selectedViewNames = new List<string>();
        private string _targetType  = "Grid";          // "Grid" | "SlabEdge"
        private bool   _includeLinks = true;

        private const string GridDisplay = "To Grid";
        private const string SlabDisplay = "To Slab Edge";

        public AutoDimensionViewModel(
            AutoDimensionEventHandler? handler,
            ExternalEvent?             externalEvent,
            List<View>                 allViews)
        {
            _handler  = handler;
            _event    = externalEvent;
            _allViews = allViews ?? new List<View>();

            var cfg = AutoDimensionConfig.Instance;
            _targetType   = string.Equals(cfg.TargetType, "SlabEdge", StringComparison.OrdinalIgnoreCase) ? "SlabEdge" : "Grid";
            _includeLinks = cfg.IncludeLinks;

            foreach (var v in _allViews)
                if (!_viewNameToId.ContainsKey(v.Name))
                    _viewNameToId[v.Name] = v.Id;
        }

        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildViewsStep();
                case "S2": return BuildOptionsStep();
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

        // ── S1 — Select Views ─────────────────────────────────────────────────
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

        // ── S2 — Target & Options ─────────────────────────────────────────────
        private FrameworkElement BuildOptionsStep()
        {
            var outer = new StackPanel();

            AddLabel(outer, "Choose what each dimension measures out to.");

            var picker = new LemoineSingleSelect { Label = "Destination" };
            picker.Items = new List<string> { GridDisplay, SlabDisplay };
            picker.SelectedItem = _targetType == "SlabEdge" ? SlabDisplay : GridDisplay;
            picker.SelectionChanged += sel =>
            {
                _targetType = sel == SlabDisplay ? "SlabEdge" : "Grid";
                Fire();
            };
            outer.Children.Add(picker);

            AddDivider(outer);

            var toggles = new LemoineToggleSwitches();
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem
                {
                    Id = "links", Label = "Include linked models as targets",
                    Desc = "Resolve grids / slab edges that live in loaded Revit links (coordination models).",
                    DefaultOn = _includeLinks,
                },
            });
            toggles.StateChanged += state =>
            {
                state.TryGetValue("links", out _includeLinks);
                Fire();
            };
            outer.Children.Add(toggles);

            AddDivider(outer);
            AddDim(outer, $"{_selectedViewNames.Count} view(s) selected · {(_targetType == "SlabEdge" ? "slab edge" : "grid")} targets.");

            return WrapInScroll(outer);
        }

        // ── Validation / summaries ────────────────────────────────────────────
        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedViewNames.Count > 0;
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedViewNames.Count == 0 ? "—" : $"{_selectedViewNames.Count} view(s)";
                case "S2": return $"{(_targetType == "SlabEdge" ? "slab edge" : "grid")}{(_includeLinks ? " · links" : "")}";
                default:   return "—";
            }
        }

        // ── Run ───────────────────────────────────────────────────────────────
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            if (_handler == null || _event == null) return;

            var cfg = AutoDimensionConfig.Instance;
            cfg.TargetType   = _targetType;
            cfg.IncludeLinks = _includeLinks;
            cfg.Save();

            _handler.ViewIds = _selectedViewNames
                .Where(n => _viewNameToId.ContainsKey(n))
                .Select(n => _viewNameToId[n])
                .ToList();
            _handler.Config     = cfg;
            _handler.PushLog    = pushLog;
            _handler.OnProgress = onProgress;
            _handler.OnComplete = onComplete;

            _event.Raise();
        }

        // ── UI helpers (mirror ClashFinderViewModel — resource refs only) ──────
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
    }
}
