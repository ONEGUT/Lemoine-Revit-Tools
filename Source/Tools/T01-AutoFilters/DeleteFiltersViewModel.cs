using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.AutoFilters
{
    /// <summary>
    /// ViewModel for removing selected filters from the active view.
    /// The list of applied filters is captured on the main thread in the launch
    /// command and passed into this ViewModel for display.
    /// </summary>
    public class DeleteFiltersViewModel : ILemoineTool
    {
        // ── ILemoineTool identity ──────────────────────────────────────────────
        public string Title    => "Remove Filters from View";
        public string RunLabel => "Remove Selected →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Filters", required: true),
            new StepDefinition("S2", "Review & Run",   required: false),
        };

        // ── State ──────────────────────────────────────────────────────────────
        private readonly string                 _viewName;
        private readonly IReadOnlyList<string>  _allFilterNames;
        private List<string>                    _selectedNames = new List<string>();

        // ── Validation change notification ─────────────────────────────────────
        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── ExternalEvent wiring ────────────────────────────────────────────────
        private readonly DeleteFiltersEventHandler          _handler;
        private readonly Autodesk.Revit.UI.ExternalEvent    _event;

        public DeleteFiltersViewModel(
            DeleteFiltersEventHandler handler,
            Autodesk.Revit.UI.ExternalEvent externalEvent,
            IReadOnlyList<string> filterNames,
            string viewName)
        {
            _handler        = handler;
            _event          = externalEvent;
            _allFilterNames = filterNames ?? new List<string>();
            _viewName       = viewName    ?? "Active View";
        }

        // ═══════════════════════════════════════════════════════════════════════
        // GetStepContent
        // ═══════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "S1")
            {
                if (_allFilterNames.Count == 0)
                {
                    // FIX: empty-state text uses LemoineText + italic, not LemoineTextDim
                    var none = new TextBlock
                    {
                        Text         = "No filters are currently applied to the active view.",
                        TextWrapping = TextWrapping.Wrap,
                        FontStyle    = FontStyles.Italic,
                    };
                    none.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    none.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                    none.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    return none;
                }

                // Group by the trade that owns the matching rule; unmatched → "Others".
                var groups = AutoFiltersSettings.GroupFilterNamesByTrade(_allFilterNames);

                var tabs = new LemoineMultiSelectTabs();
                tabs.SetGroups(groups);
                tabs.SelectionChanged += selected =>
                {
                    _selectedNames = new List<string>(selected);
                    OnValidationChanged();
                };

                return tabs;
            }

            if (stepId == "S2")
                return BuildReviewPanel();

            return null;
        }

        // ── CardDef struct ───────────────────────────────────────────────────────
        private struct CardDef
        {
            public string       Label;
            public Func<string> Val;
            public int          Row;
            public int          Col;
            public CardDef(string label, Func<string> val, int row, int col)
            { Label = label; Val = val; Row = row; Col = col; }
        }

        private FrameworkElement BuildReviewPanel()
        {
            var outer = new StackPanel();

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var cards = new[]
            {
                new CardDef("View",             () => _viewName,                                                               0, 0),
                new CardDef("Filters to Remove",() => $"{_selectedNames.Count} selected",                                     0, 1),
                new CardDef("Remaining",        () => $"{_allFilterNames.Count - _selectedNames.Count} will stay",            1, 0),
                new CardDef("Action",           () => "Detach from view (not deleted)",                                       1, 1),
            };

            foreach (var c in cards)
            {
                var card = new Border
                {
                    Margin          = new Thickness(c.Col == 0 ? 0 : 4, c.Row == 0 ? 0 : 4, 0, 0),
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(3),
                };
                card.SetResourceReference(Border.PaddingProperty,    "LemoineTh_CardPad");
                card.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

                var lbl = new TextBlock { Text = c.Label.ToUpper(), Margin = new Thickness(0, 0, 0, 2) };
                lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

                var captured = c.Val;
                var valText = new TextBlock { FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap };
                valText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                valText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                valText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                valText.Text = captured();
                ValidationChanged += (s, e) => valText.Text = captured();

                var sp = new StackPanel();
                sp.Children.Add(lbl);
                sp.Children.Add(valText);
                card.Child = sp;

                Grid.SetRow(card, c.Row);
                Grid.SetColumn(card, c.Col);
                grid.Children.Add(card);
            }

            outer.Children.Add(grid);

            // FIX: descriptive text uses LemoineText + italic, not LemoineTextDim
            var desc = new TextBlock
            {
                Text         = "Selected filters will be detached from the active view only — " +
                               "they will NOT be deleted from the project and can be re-applied later.",
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, 8, 0, 0),
            };
            desc.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            desc.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            desc.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(desc);

            return outer;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // IsValid / SummaryFor / Run
        // ═══════════════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _selectedNames.Count > 0;
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1") return $"{_selectedNames.Count} filter(s) selected";
            if (stepId == "S2") return "Ready to run";
            return "—";
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _handler.SelectedFilterNames = new List<string>(_selectedNames);
            _handler.PushLog             = pushLog;
            _handler.OnProgress          = onProgress;
            _handler.OnComplete          = onComplete;

            pushLog($"Removing {_selectedNames.Count} filter(s) from view…", "info");
            _event.Raise();
        }
    }
}
