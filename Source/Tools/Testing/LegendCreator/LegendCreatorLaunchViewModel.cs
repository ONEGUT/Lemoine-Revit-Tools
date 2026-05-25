using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Testing.LegendCreator
{
    /// <summary>
    /// ILemoineTool for the Legend Creation ribbon button step-flow.
    /// Step S1 lets the user pick which existing legend view to duplicate,
    /// then displays a summary of the current Legend Creator settings.
    /// </summary>
    public sealed class LegendCreatorLaunchViewModel : ILemoineTool
    {
        // ── ILemoineTool identity ──────────────────────────────────────────────
        public string Title    => "Legend Creation";
        public string RunLabel => "Create Legend →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Template", required: false),
        };

#pragma warning disable CS0067
        public event EventHandler? ValidationChanged;
#pragma warning restore CS0067

        // ── Data ──────────────────────────────────────────────────────────────
        private readonly List<(ElementId Id, string Name)> _legendViews;
        private readonly LegendCreatorEventHandler         _handler;
        private readonly Autodesk.Revit.UI.ExternalEvent   _event;
        private int _selectedIndex = 0;

        public LegendCreatorLaunchViewModel(
            List<(ElementId Id, string Name)> legendViews,
            LegendCreatorEventHandler         handler,
            Autodesk.Revit.UI.ExternalEvent   externalEvent)
        {
            _legendViews = legendViews;
            _handler     = handler;
            _event       = externalEvent;
        }

        // ── CardDef struct ─────────────────────────────────────────────────────
        private struct CardDef
        {
            public string Label, Val;
            public int    Row, Col;
            public CardDef(string label, string val, int row, int col)
            { Label = label; Val = val; Row = row; Col = col; }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // GetStepContent
        // ═══════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId != "S1") return null;

            var outer = new StackPanel();

            // ── Legend view picker ─────────────────────────────────────────────
            var pickerLabel = new TextBlock
            {
                Text   = "BASE LEGEND VIEW",
                Margin = new Thickness(0, 0, 0, 4),
            };
            pickerLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            pickerLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            pickerLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            outer.Children.Add(pickerLabel);

            var combo = new ComboBox
            {
                IsEditable = false,
                Margin     = new Thickness(0, 0, 0, 12),
            };
            combo.SetResourceReference(FrameworkElement.HeightProperty,   "LemoineH_Input");
            combo.SetResourceReference(ComboBox.FontFamilyProperty,        "LemoineMonoFont");
            combo.SetResourceReference(ComboBox.FontSizeProperty,          "LemoineFS_SM");

            foreach (var (_, name) in _legendViews)
                combo.Items.Add(name);

            combo.SelectedIndex = _selectedIndex;
            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedIndex >= 0)
                    _selectedIndex = combo.SelectedIndex;
            };
            outer.Children.Add(combo);

            // ── Current Legend Creator settings summary ────────────────────────
            var settings = LegendCreatorSettings.Instance;
            var layout   = settings.Layout ?? new LegendLayoutConfig();
            var rows     = settings.Rows   ?? new List<LegendRowConfig>();
            int groups   = rows.Sum(r => r.Groups?.Count ?? 0);
            int blocks   = rows.SelectMany(r => r.Groups ?? new List<LegendGroupConfig>())
                               .Sum(g => g.Blocks?.Count ?? 0);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var cards = new[]
            {
                new CardDef("Layout", string.IsNullOrWhiteSpace(layout.Title) ? "—" : layout.Title.Trim(), 0, 0),
                new CardDef("Groups", groups.ToString(),     0, 1),
                new CardDef("Blocks", blocks.ToString(),     1, 0),
                new CardDef("Rows",   rows.Count.ToString(), 1, 1),
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

                var valTb = new TextBlock
                {
                    Text         = c.Val,
                    FontWeight   = FontWeights.Medium,
                    TextWrapping = TextWrapping.Wrap,
                };
                valTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                valTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                valTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

                var sp = new StackPanel();
                sp.Children.Add(lbl);
                sp.Children.Add(valTb);
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
        public bool IsValid(string stepId) => _legendViews.Count > 0;

        public string SummaryFor(string stepId) =>
            stepId == "S1" && _selectedIndex >= 0 && _selectedIndex < _legendViews.Count
                ? _legendViews[_selectedIndex].Name
                : "—";

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _handler.TemplateLegendId =
                _selectedIndex >= 0 && _selectedIndex < _legendViews.Count
                    ? _legendViews[_selectedIndex].Id
                    : null;

            _handler.PushLog    = pushLog;
            _handler.OnProgress = onProgress;
            _handler.OnComplete = onComplete;

            pushLog("Raising Revit ExternalEvent…", "info");
            _event.Raise();
        }
    }
}
