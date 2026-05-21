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
    public class SplitByLevelViewModel : ILemoineTool
    {
        public string Title    => "Split Elements by Level";
        public string RunLabel => "Split in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Categories", required: true),
            new StepDefinition("S2", "Select Levels",     required: true),
            new StepDefinition("S3", "Review & Run",      required: false),
        };

        // ── State ─────────────────────────────────────────────────────────────
        private List<string>                       _selectedCats        = new List<string>();
        private List<string>                       _selectedLevelNames  = new List<string>();
        private readonly Dictionary<string, Level> _levelsByName;

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly SplitByLevelEventHandler _handler;
        private readonly ExternalEvent            _event;

        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public SplitByLevelViewModel(
            SplitByLevelEventHandler handler,
            ExternalEvent            externalEvent,
            IEnumerable<Level>       allLevels)
        {
            _handler      = handler;
            _event        = externalEvent;
            _levelsByName = allLevels
                .OrderBy(l => l.Elevation)
                .GroupBy(l => l.Name)
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
                    SplitElementsShared.LevelSplitCategories.Select(c => c.Label).ToList()
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
            if (_levelsByName.Count == 0)
            {
                var msg = new TextBlock { Text = "No levels found in the document.", TextWrapping = TextWrapping.Wrap };
                msg.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                msg.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                msg.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                return msg;
            }

            var groups = new Dictionary<string, List<string>>
            {
                { "Levels", _levelsByName.Keys.ToList() }
            };

            var tabs = new LemoineMultiSelectTabs();
            tabs.SetGroups(groups);
            tabs.SelectionChanged += selected =>
            {
                _selectedLevelNames = new List<string>(selected);
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
            AddCard(grid, "Levels Selected",
                () => _selectedLevelNames.Count == 0 ? "—" : $"{_selectedLevelNames.Count} level(s)",
                0, 1);
            AddCard(grid, "Operation",
                () => "Split elements at each selected level boundary",
                1, 0);
            AddCard(grid, "Undo",
                () => "Single Ctrl+Z step",
                1, 1);

            outer.Children.Add(grid);

            var note = new TextBlock
            {
                Text         = "Elements spanning multiple selected levels will be split into one segment per span. " +
                               "Walls and columns use level constraints; MEP curves and framing use LocationCurve adjustment.",
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
            if (stepId == "S2") return _selectedLevelNames.Count > 0;
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1")
                return _selectedCats.Count == 0 ? "—" : string.Join(", ", _selectedCats);
            if (stepId == "S2")
                return _selectedLevelNames.Count == 0 ? "—"
                    : $"{_selectedLevelNames.Count} level(s) selected";
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
            _handler.SelectedBics = SplitElementsShared.LevelSplitCategories
                .Where(c => selectedBics.Contains(c.Label))
                .Select(c => c.Cat)
                .ToList();

            _handler.SelectedLevelIds = _selectedLevelNames
                .Where(n => _levelsByName.ContainsKey(n))
                .Select(n => _levelsByName[n].Id)
                .ToList();

            _handler.OnLog      = pushLog;
            _handler.OnProgress = onProgress;
            _handler.OnComplete = onComplete;

            _event.Raise();
        }
    }
}
