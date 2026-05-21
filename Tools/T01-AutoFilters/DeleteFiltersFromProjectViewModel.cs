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
    /// ViewModel for permanently deleting ParameterFilterElements from the project.
    /// Step 1 — select which filters to delete (grouped by discipline prefix).
    /// Step 2 — Review &amp; Run (with explicit warning card).
    /// </summary>
    public class DeleteFiltersFromProjectViewModel : ILemoineTool
    {
        // ── ILemoineTool identity ──────────────────────────────────────────────
        public string Title    => "Delete Filters from Project";
        public string RunLabel => "Permanently Delete →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Filters", required: true),
            new StepDefinition("S2", "Review & Run",   required: false),
        };

        // ── State ──────────────────────────────────────────────────────────────
        private readonly IReadOnlyList<string> _allFilterNames;
        private List<string> _selectedNames = new List<string>();

        // ── Validation change notification ─────────────────────────────────────
        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── ExternalEvent wiring ────────────────────────────────────────────────
        private readonly DeleteFiltersFromProjectEventHandler   _handler;
        private readonly Autodesk.Revit.UI.ExternalEvent        _event;

        public DeleteFiltersFromProjectViewModel(
            DeleteFiltersFromProjectEventHandler handler,
            Autodesk.Revit.UI.ExternalEvent externalEvent,
            IReadOnlyList<string> allFilterNames)
        {
            _handler        = handler;
            _event          = externalEvent;
            _allFilterNames = allFilterNames ?? new List<string>();
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
                        Text         = "No ParameterFilterElements found in this project.",
                        TextWrapping = TextWrapping.Wrap,
                        FontStyle    = FontStyles.Italic,
                    };
                    none.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    none.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                    none.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    return none;
                }

                var outer = new StackPanel();

                var warn = new LemoineWarnBanner(
                    "⚠  Deleted filters are permanently removed from the project " +
                    "and cannot be recovered. They will be removed from all views.");
                outer.Children.Add(warn);

                // Multi-select grouped by discipline prefix — nothing pre-selected
                var groups = BuildFilterGroups(_allFilterNames);
                var tabs = new LemoineMultiSelectTabs();
                tabs.SetGroups(groups);
                tabs.SelectionChanged += selected =>
                {
                    _selectedNames = new List<string>(selected);
                    OnValidationChanged();
                };
                outer.Children.Add(tabs);

                return outer;
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

            var warn = new LemoineWarnBanner(
                $"⚠  This will permanently delete {_selectedNames.Count} " +
                "filter(s) from the project. This cannot be undone.");
            ValidationChanged += (s, e) =>
                warn.Message = $"⚠  This will permanently delete {_selectedNames.Count} " +
                               "filter(s) from the project. This cannot be undone.";
            outer.Children.Add(warn);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var cards = new[]
            {
                new CardDef("Filters to Delete",
                    () => $"{_selectedNames.Count} selected",
                    0, 0),
                new CardDef("Remaining in Project",
                    () => $"{_allFilterNames.Count - _selectedNames.Count} will remain",
                    0, 1),
                new CardDef("Scope",
                    () => "Entire project",
                    1, 0),
                new CardDef("Action",
                    () => "Permanent deletion (no undo)",
                    1, 1),
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
            if (stepId == "S2") return "Ready to run — action is permanent";
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

            pushLog($"Permanently deleting {_selectedNames.Count} filter(s) from project…", "info");
            _event.Raise();
        }

        // ── Helpers ─────────────────────────────────────────────────────────────
        private static Dictionary<string, List<string>> BuildFilterGroups(
            IReadOnlyList<string> names)
        {
            var groups = new Dictionary<string, List<string>>();
            foreach (var name in names)
            {
                int sep  = name.IndexOf(" - ");
                string g = sep >= 0 ? name.Substring(0, sep).Trim() : "Other";
                if (!groups.ContainsKey(g)) groups[g] = new List<string>();
                groups[g].Add(name);
            }
            return groups;
        }
    }
}
