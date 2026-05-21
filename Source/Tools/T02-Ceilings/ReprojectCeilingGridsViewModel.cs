using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.Ceilings
{
    public class ReprojectCeilingGridsViewModel : ILemoineTool
    {
        // ── ILemoineTool identity ─────────────────────────────────────────────
        public string Title    => "Reproject Ceiling Grids";
        public string RunLabel => "Run in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Source Curves", required: true),
            new StepDefinition("S2", "Review & Run",  required: false),
        };

        // ── State ─────────────────────────────────────────────────────────────
        private string _mode = "all";   // "preselected" | "all"
        private readonly int _preSelectedCount;

        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── ExternalEvent wiring ───────────────────────────────────────────
        private readonly CeilingGridEventHandler _handler;
        private readonly Autodesk.Revit.UI.ExternalEvent _event;

        public ReprojectCeilingGridsViewModel(
            CeilingGridEventHandler handler,
            Autodesk.Revit.UI.ExternalEvent externalEvent,
            int preSelectedCount)
        {
            _handler          = handler;
            _event            = externalEvent;
            _preSelectedCount = preSelectedCount;
            _mode             = preSelectedCount > 0 ? "preselected" : "all";
        }

        // ═════════════════════════════════════════════════════════════════════
        // GetStepContent
        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "S1") return BuildSourceStep();
            if (stepId == "S2") return BuildReviewStep();
            return null;
        }

        private FrameworkElement BuildSourceStep()
        {
            var outer = new StackPanel();

            // Source selector
            var select = new LemoineSingleSelect { Label = "Which curves should be reprojected?" };
            var items = new List<string> { "All curves in active view" };
            if (_preSelectedCount > 0)
                items.Insert(0, $"Pre-selected curves ({_preSelectedCount} selected)");

            select.Items = items;
            select.SelectedItem = _mode == "preselected" && _preSelectedCount > 0
                ? items[0] : "All curves in active view";

            select.SelectionChanged += val =>
            {
                _mode = (val?.StartsWith("Pre-selected") == true) ? "preselected" : "all";
                OnValidationChanged();
            };
            outer.Children.Add(select);

            // Context hint
            var hint = new Border
            {
                Margin          = new Thickness(0, 10, 0, 0),
                Padding         = new Thickness(10, 8, 10, 8),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
            };
            hint.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            hint.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var hintStack = new StackPanel();

            var hintText = new TextBlock { TextWrapping = TextWrapping.Wrap };
            hintText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            hintText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            hintText.SetResourceReference(TextBlock.FontFamilyProperty,  "LemoineUiFont");
            hintText.Text = _mode == "preselected"
                ? $"{_preSelectedCount} ModelCurve(s) were selected in Revit when this command opened. Those curves will be reprojected onto the current ceiling soffit geometry."
                : "All ModelCurves visible in the active view will be reprojected. Use this when all ceiling elevations have changed.";

            // Update hint when mode changes
            ValidationChanged += (s, e) =>
                hintText.Text = _mode == "preselected"
                    ? $"{_preSelectedCount} ModelCurve(s) pre-selected. These will be reprojected onto the current ceiling soffit geometry."
                    : "All ModelCurves visible in the active view will be reprojected.";

            var warning = new TextBlock
            {
                Text        = "⚠  Original curves will be deleted and replaced — this cannot be undone individually.",
                TextWrapping= TextWrapping.Wrap,
                Margin      = new Thickness(0, 6, 0, 0),
            };
            warning.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            warning.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
            warning.SetResourceReference(TextBlock.FontFamilyProperty,  "LemoineMonoFont");

            hintStack.Children.Add(hintText);
            hintStack.Children.Add(warning);
            hint.Child = hintStack;
            outer.Children.Add(hint);

            return outer;
        }

        // Helper struct — avoids the mixed-type implicit array inference error in net48 C#8
        private struct CardDef
        {
            public string       Label;
            public Func<string> Val;
            public int          Row;
            public int          Col;
            public CardDef(string label, Func<string> val, int row, int col)
            { Label = label; Val = val; Row = row; Col = col; }
        }

        private FrameworkElement BuildReviewStep()
        {
            var outer = new StackPanel();

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Named struct avoids the mixed-type implicit array inference error in net48 C#8
            var cards = new[]
            {
                new CardDef("Source Curves",   () => _mode == "preselected" ? $"{_preSelectedCount} pre-selected" : "All in active view", 0, 0),
                new CardDef("Target Ceilings", () => "Visible in active view",    0, 1),
                new CardDef("Operation",       () => "Delete → Reproject",        1, 0),
                new CardDef("Output",          () => "Model curves (updated Z)",  1, 1),
            };

            foreach (var c in cards)
            {
                var card = new Border
                {
                    Margin          = new Thickness(c.Col == 0 ? 0 : 4, c.Row == 0 ? 0 : 4, 0, 0),
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(3),
                    Padding         = new Thickness(10, 7, 10, 7),
                };
                card.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

                var lbl = new TextBlock { Text = c.Label.ToUpper(), Margin = new Thickness(0, 0, 0, 2) };
                lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                lbl.SetResourceReference(TextBlock.FontFamilyProperty,  "LemoineUiFont");

                var capturedVal = c.Val;
                var valText = new TextBlock { Text = capturedVal(), FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis };
                valText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                valText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                valText.SetResourceReference(TextBlock.FontFamilyProperty,  "LemoineMonoFont");
                ValidationChanged += (s, e) => valText.Text = capturedVal();

                var sp = new StackPanel();
                sp.Children.Add(lbl);
                sp.Children.Add(valText);
                card.Child = sp;

                Grid.SetRow(card, c.Row);
                Grid.SetColumn(card, c.Col);
                grid.Children.Add(card);
            }

            outer.Children.Add(grid);

            var desc = new TextBlock
            {
                Text         = "Source curves will be projected vertically onto the current soffit geometry. " +
                               "New model curves are created at the updated ceiling elevations. " +
                               "Curves with no ceiling overlap are skipped and reported in the log.",
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 8, 0, 0),
            };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            desc.SetResourceReference(TextBlock.FontFamilyProperty,  "LemoineUiFont");
            outer.Children.Add(desc);

            return outer;
        }

        // ═════════════════════════════════════════════════════════════════════
        // IsValid
        // ═════════════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return true;   // always valid — "all" is a valid choice
            return true;
        }

        // ═════════════════════════════════════════════════════════════════════
        // SummaryFor
        // ═════════════════════════════════════════════════════════════════════
        public string SummaryFor(string stepId)
        {
            if (stepId == "S1")
                return _mode == "preselected"
                    ? $"{_preSelectedCount} pre-selected curves"
                    : "All curves in active view";
            if (stepId == "S2") return "Ready to run";
            return "—";
        }

        // ═════════════════════════════════════════════════════════════════════
        // Run
        // ═════════════════════════════════════════════════════════════════════
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _handler.Mode       = CeilingGridEventHandler.ToolMode.Reproject;
            _handler.ReprojectMode = _mode;
            _handler.PushLog    = pushLog;
            _handler.OnProgress = onProgress;
            _handler.OnComplete = onComplete;

            pushLog("Raising Revit ExternalEvent…", "info");
            _event.Raise();
        }
    }
}
