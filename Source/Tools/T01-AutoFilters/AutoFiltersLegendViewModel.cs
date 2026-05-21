using System;
using System.Windows;
using System.Windows.Controls;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.AutoFilters
{
    /// <summary>
    /// ViewModel for the Filter Legend Creator tool.
    /// Reads all filter overrides from the active view and generates a new
    /// Legend view with colored swatches. No user inputs required beyond
    /// confirming the run.
    /// </summary>
    public class AutoFiltersLegendViewModel : ILemoineTool
    {
        // ── ILemoineTool identity ──────────────────────────────────────────────
        public string Title    => "Filter Legend Creator";
        public string RunLabel => "Create Legend →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Review & Run", required: false),
        };

        // ── Validation change notification ─────────────────────────────────────
        // Required by ILemoineTool; never raised because this tool has no inputs.
#pragma warning disable CS0067
        public event EventHandler? ValidationChanged;
#pragma warning restore CS0067

        // ── ExternalEvent wiring ────────────────────────────────────────────────
        private readonly AutoFiltersLegendEventHandler      _handler;
        private readonly Autodesk.Revit.UI.ExternalEvent    _event;

        public AutoFiltersLegendViewModel(
            AutoFiltersLegendEventHandler handler,
            Autodesk.Revit.UI.ExternalEvent externalEvent)
        {
            _handler = handler;
            _event   = externalEvent;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // GetStepContent
        // ═══════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "S1")
                return BuildReviewPanel();
            return null;
        }

        // ── CardDef struct ───────────────────────────────────────────────────────
        private struct CardDef
        {
            public string Label;
            public string Val;
            public int    Row;
            public int    Col;
            public CardDef(string label, string val, int row, int col)
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
                new CardDef("Source",      "Active view filters",       0, 0),
                new CardDef("Output",      "New Legend view",           0, 1),
                new CardDef("Grouping",    "By discipline prefix",      1, 0),
                new CardDef("Swatches",    "One per unique color",      1, 1),
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

                var valText = new TextBlock { Text = c.Val, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap };
                valText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                valText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                valText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

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
                Text         = "Reads all filter overrides from the active view and creates a new Legend " +
                               "view with colored swatches and label text, grouped by discipline. " +
                               "Requires at least one existing Legend view in the project.",
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
        public bool IsValid(string stepId) => true;

        public string SummaryFor(string stepId) =>
            stepId == "S1" ? "Ready to run" : "—";

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _handler.PushLog    = pushLog;
            _handler.OnProgress = onProgress;
            _handler.OnComplete = onComplete;

            pushLog("Raising Revit ExternalEvent…", "info");
            _event.Raise();
        }
    }
}
