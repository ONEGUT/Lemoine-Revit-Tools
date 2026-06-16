using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.Clash.AutoDimension.Refine
{
    /// <summary>
    /// Refine Dimensions wizard: pick views that ALREADY have clash markers and re-dimension them to
    /// the nearest grid or slab edge visible in each view. No clash detection, no marking, no callouts,
    /// and no view-scale change — it replaces the dimensions this tool placed before and leaves markers
    /// and other annotations alone. Three steps: views · destination · review &amp; run. The actual work
    /// happens in <see cref="RefineDimensionsEventHandler"/>.
    /// </summary>
    public class RefineDimensionsViewModel : ILemoineTool, ILemoineReviewable, ILemoineRunResult, ILemoineToolCleanup
    {
        // Run strip: live "dimensions" label during the run, full chip breakdown on completion.
        public string? ResultNoun => "dimensions";
        private IReadOnlyList<ResultChip>? _resultChips;
        public IReadOnlyList<ResultChip>? ResultChips => _resultChips;

        // Null the callbacks parked on the static handler so this VM isn't retained after close.
        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.PushLog       = null;
            _handler.OnProgress    = null;
            _handler.OnComplete    = null;
            _handler.OnResultChips = null;
        }

        public string Title    => "Refine Dimensions";
        public string RunLabel => "Refine Dimensions →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Views",   required: true),
            new StepDefinition("S2", "Destination",    required: false),
            new StepDefinition("S3", "Review & Run",   required: false),
        };

        public event EventHandler? ValidationChanged;
        private void Fire() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly RefineDimensionsEventHandler? _handler;
        private readonly ExternalEvent?                _event;

        // ── Data ──────────────────────────────────────────────────────────────
        private readonly List<View>         _allViews;
        private readonly LemoineBrowserTree _browserTree;

        // ── State ─────────────────────────────────────────────────────────────
        private List<long> _selectedViewIds = new List<long>();

        private const string GridDisplay = "To Grid";
        private const string SlabDisplay = "To Slab Edge";
        private string _dimTargetType =
            string.Equals(AutoDimensionConfig.Instance.TargetType, "Grid", StringComparison.OrdinalIgnoreCase)
              ? "Grid" : "SlabEdge";

        public RefineDimensionsViewModel(
            RefineDimensionsEventHandler? handler,
            ExternalEvent?                externalEvent,
            List<View>                    allViews,
            LemoineBrowserTree?           browserTree = null)
        {
            _handler     = handler;
            _event       = externalEvent;
            _allViews    = allViews ?? new List<View>();
            _browserTree = browserTree ?? new LemoineBrowserTree();
        }

        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildViewsStep();
                case "S2": return BuildDestinationStep();
                case "S3": return null;   // framework renders the review (ILemoineReviewable)
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
            AddDim(outer, "Pick the views to re-dimension. Each should already carry Lemoine clash markers — "
                        + "this tool dimensions existing markers and never detects or marks clashes.");

            var picker = new LemoineBrowserTreePicker
            {
                Height         = 300,
                AccessibleName = "Views to refine",
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

        // ── S2 — Destination ──────────────────────────────────────────────────
        private FrameworkElement BuildDestinationStep()
        {
            var outer = new StackPanel();

            AddLabel(outer, "Dimension destination — each clash measures to the nearest one VISIBLE in the view.");
            var destPicker = new LemoineSingleSelect { Label = "Destination" };
            destPicker.Items        = new List<string> { SlabDisplay, GridDisplay };
            destPicker.SelectedItem = _dimTargetType == "Grid" ? GridDisplay : SlabDisplay;
            destPicker.SelectionChanged += sel =>
            {
                _dimTargetType = sel == GridDisplay ? "Grid" : "SlabEdge";
                Fire();
            };
            outer.Children.Add(destPicker);

            AddDivider(outer);
            AddDim(outer, "Refine never creates callouts and never changes a view's scale. It replaces the "
                        + "dimensions this tool placed before on the selected views; clash markers and other "
                        + "annotations are left untouched. Chaining, grouping, and spacing use the saved "
                        + "Settings → Dimensions values.");
            return WrapInScroll(outer);
        }

        // ── Validation / summaries ────────────────────────────────────────────
        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedViewIds.Count > 0;
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedViewIds.Count == 0 ? "—" : $"{_selectedViewIds.Count} view(s)";
                case "S2": return _dimTargetType == "Grid" ? "nearest grid" : "nearest slab edge";
                case "S3": return "Ready to run";
                default:   return "—";
            }
        }

        // ── ILemoineReviewable — framework renders the review step ────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("views", "Views"),
            ("dest",  "Destination"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["views"] = _selectedViewIds.Count > 0 ? $"{_selectedViewIds.Count} view(s)" : "—",
            ["dest"]  = _dimTargetType == "Grid" ? "nearest visible grid" : "nearest visible slab edge",
        };

        public IList<string>? ReviewChips => new List<string> { "no callouts", "scale unchanged" };

        public string? ReviewNote => "Re-dimensions existing clash markers on the selected views to the "
            + "nearest grid or slab edge visible in each view, replacing the dimensions this tool placed "
            + "before. No clash detection, no callouts, no scale change.";

        public string? ReviewWarning => null;

        // ── Run ───────────────────────────────────────────────────────────────
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            if (_handler == null || _event == null) return;

            _resultChips = null;   // clear any breakdown from a previous run

            _handler.ViewIds       = _selectedViewIds.Select(id => new ElementId(id)).ToList();
            _handler.DimTargetType = _dimTargetType;
            _handler.PushLog       = pushLog;
            _handler.OnProgress    = onProgress;
            _handler.OnComplete    = onComplete;
            _handler.OnResultChips = chips => _resultChips = chips;

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
    }
}
