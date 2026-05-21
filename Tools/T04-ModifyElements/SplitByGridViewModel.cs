using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

using WpfGrid = System.Windows.Controls.Grid;

namespace LemoineTools.Tools.ModifyElements
{
    public class SplitByGridViewModel : ILemoineTool
    {
        public string Title    => "Split Elements by Grid";
        public string RunLabel => "Split in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Categories", required: true),
            new StepDefinition("S2", "Select Grids",      required: true),
            new StepDefinition("S3", "Review & Run",      required: false),
        };

        // ── State ─────────────────────────────────────────────────────────────
        private List<string>                                    _selectedCats      = new List<string>();
        private List<string>                                    _selectedGridNames = new List<string>();
        private readonly Dictionary<string, Autodesk.Revit.DB.Grid> _gridsByName;

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly SplitByGridEventHandler _handler;
        private readonly ExternalEvent           _event;

        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public SplitByGridViewModel(
            SplitByGridEventHandler              handler,
            ExternalEvent                        externalEvent,
            IEnumerable<Autodesk.Revit.DB.Grid>  allGrids)
        {
            _handler     = handler;
            _event       = externalEvent;
            _gridsByName = allGrids
                .Where(g => g.Curve is Line)
                .GroupBy(g => g.Name)
                .ToDictionary(g => g.Key, g => g.First());
        }

        // ═════════════════════════════════════════════════════════════════════
        //  GetStepContent
        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildS1();
                case "S2": return BuildS2();
                case "S3": return BuildReview();
                default:   return null;
            }
        }

        private FrameworkElement BuildS1()
        {
            var groups = new Dictionary<string, List<string>>
            {
                {
                    "Categories",
                    SplitElementsShared.GridSplitCategories.Select(c => c.Label).ToList()
                }
            };

            var tabs = new LemoineMultiSelectTabs();
            tabs.SetGroups(groups);
            tabs.SelectionChanged += selected =>
            {
                _selectedCats = new List<string>(selected);
                OnValidationChanged();
            };
            return tabs;
        }

        private FrameworkElement BuildS2()
        {
            if (_gridsByName.Count == 0)
            {
                var msg = new TextBlock
                {
                    Text         = "No linear grids found in the document. Only linear (non-arc) grids can be used as split planes.",
                    TextWrapping = TextWrapping.Wrap,
                };
                msg.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                msg.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                msg.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                return msg;
            }

            var groups = new Dictionary<string, List<string>>
            {
                { "Grids", _gridsByName.Keys.OrderBy(n => n).ToList() }
            };

            var tabs = new LemoineMultiSelectTabs();
            tabs.SetGroups(groups);
            tabs.SelectionChanged += selected =>
            {
                _selectedGridNames = new List<string>(selected);
                OnValidationChanged();
            };
            return tabs;
        }

        private FrameworkElement BuildReview()
        {
            var outer = new StackPanel();
            var grid  = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddCard(grid, "Categories Selected",
                () => _selectedCats.Count == 0 ? "—" : string.Join(", ", _selectedCats),
                0, 0);
            AddCard(grid, "Grids Selected",
                () => _selectedGridNames.Count == 0 ? "—" : $"{_selectedGridNames.Count} grid(s)",
                0, 1);
            AddCard(grid, "Operation",
                () => "Split elements at each selected grid plane",
                1, 0);
            AddCard(grid, "Scope",
                () => "Entire document",
                1, 1);

            outer.Children.Add(grid);

            var note = new TextBlock
            {
                Text         = "Elements whose LocationCurve intersects a selected grid plane will be split at that intersection point. " +
                               "Only linear (non-arc) grids are supported as split planes.",
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, 8, 0, 0),
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            note.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            note.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            outer.Children.Add(note);

            return outer;
        }

        private void AddCard(WpfGrid grid, string label, Func<string> val, int row, int col)
        {
            var card = new Border
            {
                Margin          = new Thickness(col == 0 ? 0 : 4, row == 0 ? 0 : 4, 0, 0),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
            };
            card.SetResourceReference(Border.PaddingProperty,    "LemoineTh_CardPad");
            card.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var lbl = new TextBlock { Text = label.ToUpper(), Margin = new Thickness(0, 0, 0, 2) };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var valText = new TextBlock { FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap };
            valText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            valText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            valText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            valText.Text = val();
            ValidationChanged += (s, e) => valText.Text = val();

            var sp = new StackPanel();
            sp.Children.Add(lbl);
            sp.Children.Add(valText);
            card.Child = sp;
            WpfGrid.SetRow(card, row);
            WpfGrid.SetColumn(card, col);
            grid.Children.Add(card);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  IsValid / SummaryFor / Run
        // ═════════════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _selectedCats.Count > 0;
            if (stepId == "S2") return _selectedGridNames.Count > 0;
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1")
                return _selectedCats.Count == 0 ? "—" : string.Join(", ", _selectedCats);
            if (stepId == "S2")
                return _selectedGridNames.Count == 0 ? "—"
                    : $"{_selectedGridNames.Count} grid(s) selected";
            if (stepId == "S3")
                return "Ready to run";
            return "—";
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            var selectedBics = new HashSet<string>(_selectedCats);
            _handler.SelectedBics = SplitElementsShared.GridSplitCategories
                .Where(c => selectedBics.Contains(c.Label))
                .Select(c => c.Cat)
                .ToList();

            _handler.SelectedGridIds = _selectedGridNames
                .Where(n => _gridsByName.ContainsKey(n))
                .Select(n => _gridsByName[n].Id)
                .ToList();

            _handler.OnLog      = pushLog;
            _handler.OnProgress = onProgress;
            _handler.OnComplete = onComplete;

            _event.Raise();
        }
    }
}
