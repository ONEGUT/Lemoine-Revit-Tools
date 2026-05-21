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
    public class SplitByCellViewModel : ILemoineTool
    {
        public string Title    => "Split Elements by Cell";
        public string RunLabel => "Split in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Categories", required: true),
            new StepDefinition("S2", "Cell Size",         required: true),
            new StepDefinition("S3", "Review & Run",      required: false),
        };

        // ── State ─────────────────────────────────────────────────────────────
        private List<string> _selectedCats    = new List<string>();
        private double       _cellX           = 10.0;
        private double       _cellY           = 10.0;
        private bool         _useProjectOrigin = false;

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly SplitByCellEventHandler _handler;
        private readonly ExternalEvent           _event;

        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public SplitByCellViewModel(
            SplitByCellEventHandler handler,
            ExternalEvent           externalEvent)
        {
            _handler = handler;
            _event   = externalEvent;
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
            var catLabels = SplitByCellHelpers.SupportedCategories
                .Select(bic =>
                {
                    switch (bic)
                    {
                        case BuiltInCategory.OST_Floors:               return "Floors";
                        case BuiltInCategory.OST_Ceilings:             return "Ceilings";
                        case BuiltInCategory.OST_Roofs:                return "Roofs";
                        case BuiltInCategory.OST_StructuralFoundation: return "Structural Foundations";
                        case BuiltInCategory.OST_FilledRegion:         return "Filled Regions";
                        default:                                        return bic.ToString().Replace("OST_", "");
                    }
                })
                .ToList();

            var groups = new Dictionary<string, List<string>>
            {
                { "Categories", catLabels }
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
            var outer = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

            // Cell size inputs (X = width, Y = height) via LemoineNumberRange
            var sizeRange = new LemoineNumberRange
            {
                MinLabel = "Cell X width (ft)",
                MaxLabel = "Cell Y height (ft)",
                AbsMin   = 0.1,
                AbsMax   = 1000,
                Step     = 0.5,
            };
            sizeRange.SetValues(_cellX, _cellY);
            sizeRange.RangeChanged += (min, max) =>
            {
                if (min.HasValue && min.Value > 0) _cellX = min.Value;
                if (max.HasValue && max.Value > 0) _cellY = max.Value;
                OnValidationChanged();
            };
            outer.Children.Add(sizeRange);

            // Grid origin toggle
            var toggle = new LemoineToggleSwitches();
            toggle.SetItems(new List<ToggleItem>
            {
                new ToggleItem
                {
                    Id        = "projOrigin",
                    Label     = "Align grid to project origin",
                    Desc      = "When on, the cell grid snaps to the project base point so cells are consistent across the model. When off, each element's own bounding box is used.",
                    DefaultOn = false,
                },
            });
            toggle.StateChanged += state =>
            {
                _useProjectOrigin = state.TryGetValue("projOrigin", out bool v) && v;
                OnValidationChanged();
            };
            outer.Children.Add(toggle);

            return outer;
        }

        private FrameworkElement BuildReview()
        {
            var outer = new StackPanel();
            var grid  = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddCard(grid, "Categories",
                () => _selectedCats.Count == 0 ? "—" : string.Join(", ", _selectedCats),
                0, 0);
            AddCard(grid, "Cell Size",
                () => $"{_cellX:F2} ft × {_cellY:F2} ft",
                0, 1);
            AddCard(grid, "Grid Origin",
                () => _useProjectOrigin ? "Project base point" : "Per-element bounding box",
                1, 0);
            AddCard(grid, "Scope",
                () => "Active view",
                1, 1);

            outer.Children.Add(grid);

            var note = new TextBlock
            {
                Text         = "Sketch-based elements in the active view will be split into a regular grid of cells. " +
                               "Elements that fit within a single cell are skipped. Each element runs in its own transaction — " +
                               "the entire operation collapses to a single Ctrl+Z undo step.",
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
            if (stepId == "S2") return _cellX > 0 && _cellY > 0;
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1")
                return _selectedCats.Count == 0 ? "—" : string.Join(", ", _selectedCats);
            if (stepId == "S2")
                return $"{_cellX:F2} ft × {_cellY:F2} ft";
            if (stepId == "S3")
                return "Ready to run";
            return "—";
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _handler.SelectedCategoryLabels = new List<string>(_selectedCats);
            _handler.CellX                  = _cellX;
            _handler.CellY                  = _cellY;
            _handler.UseProjectOrigin       = _useProjectOrigin;
            _handler.OnLog                  = pushLog;
            _handler.OnProgress             = onProgress;
            _handler.OnComplete             = onComplete;

            _event.Raise();
        }
    }
}
