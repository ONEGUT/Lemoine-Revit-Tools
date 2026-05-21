using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LemoineTools.Tools.Testing.LegendCreator;

namespace LemoineTools.Lemoine.Controls
{
    /// <summary>
    /// Top ribbon of the Legend Creator. Two rows:
    ///   Row 1 — [LEGEND ▸]   [TITLE input]   [SUBTITLE input]
    ///   Row 2 — [LAYOUT ▸]   SWATCH W×H   FONT pt   GAP px
    /// </summary>
    public partial class LemoineLegendLayoutBar : UserControl
    {
        public event EventHandler? Changed;

        private LegendLayoutConfig _layout = new LegendLayoutConfig();
        public LegendLayoutConfig Layout
        {
            get => _layout;
            set
            {
                _layout = value ?? new LegendLayoutConfig();
                if (IsLoaded) BuildAll();
            }
        }

        public LemoineLegendLayoutBar()
        {
            InitializeComponent();
            Loaded += (s, e) => BuildAll();
        }

        private void BuildAll()
        {
            _outer.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            _outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            _root.Children.Clear();
            _root.Children.Add(BuildTopRow());
            var sep = new Border { Height = 1 };
            sep.SetResourceReference(Border.BackgroundProperty, "LemoineBorderMid");
            _root.Children.Add(sep);
            _root.Children.Add(BuildBottomRow());
        }

        private UIElement BuildTopRow()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());

            // Brand
            var brand = MakeBrand("LEGEND");
            Grid.SetColumn(brand, 0);
            grid.Children.Add(brand);

            // TITLE cell
            var titleEdit = new LemoineInlineEdit
            {
                Text = _layout.Title ?? "",
                Bold = true,
                FontSizeKey = "LemoineFS_LG",
                Placeholder = "Filter Legend",
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 140,
            };
            titleEdit.TextCommitted += (s, t) => { _layout.Title = t; Changed?.Invoke(this, EventArgs.Empty); };
            var titleCell = MakeCell("TITLE", titleEdit);
            Grid.SetColumn(titleCell, 1);
            grid.Children.Add(titleCell);

            // SUBTITLE cell
            var subEdit = new LemoineInlineEdit
            {
                Text = _layout.Subtitle ?? "",
                FontSizeKey = "LemoineFS_MD",
                Placeholder = "(optional)",
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 80,
            };
            subEdit.TextCommitted += (s, t) => { _layout.Subtitle = t; Changed?.Invoke(this, EventArgs.Empty); };
            var subCell = MakeCell("SUBTITLE", subEdit);
            Grid.SetColumn(subCell, 2);
            grid.Children.Add(subCell);

            return grid;
        }

        private UIElement BuildBottomRow()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());

            var brand = MakeBrand("LAYOUT");
            Grid.SetColumn(brand, 0);
            grid.Children.Add(brand);

            // SWATCH W × H
            var wStep = new LemoineNumberStepper { Value = _layout.SwatchW, MinValue = 10, MaxValue = 48, Step = 2 };
            wStep.ValueChanged += (s, v) => { _layout.SwatchW = v; Changed?.Invoke(this, EventArgs.Empty); };
            var times = new TextBlock { Text = " × ", VerticalAlignment = VerticalAlignment.Center };
            times.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            times.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            times.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            var hStep = new LemoineNumberStepper { Value = _layout.SwatchH, MinValue = 6, MaxValue = 28, Step = 1 };
            hStep.ValueChanged += (s, v) => { _layout.SwatchH = v; Changed?.Invoke(this, EventArgs.Empty); };
            var swatchInner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            swatchInner.Children.Add(wStep);
            swatchInner.Children.Add(times);
            swatchInner.Children.Add(hStep);
            swatchInner.Children.Add(MakeUnit("px"));
            var swatchCell = MakeCell("SWATCH", swatchInner);
            Grid.SetColumn(swatchCell, 1);
            grid.Children.Add(swatchCell);

            // FONT
            var fontStep = new LemoineNumberStepper { Value = _layout.FontPt, MinValue = 7, MaxValue = 16, Step = 1 };
            fontStep.ValueChanged += (s, v) => { _layout.FontPt = v; Changed?.Invoke(this, EventArgs.Empty); };
            var fontInner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            fontInner.Children.Add(fontStep);
            fontInner.Children.Add(MakeUnit("pt"));
            var fontCell = MakeCell("FONT", fontInner);
            Grid.SetColumn(fontCell, 2);
            grid.Children.Add(fontCell);

            // GAP
            var gapStep = new LemoineNumberStepper { Value = _layout.Gap, MinValue = 0, MaxValue = 20, Step = 1 };
            gapStep.ValueChanged += (s, v) => { _layout.Gap = v; Changed?.Invoke(this, EventArgs.Empty); };
            var gapInner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            gapInner.Children.Add(gapStep);
            gapInner.Children.Add(MakeUnit("px"));
            var gapCell = MakeCell("GAP", gapInner);
            Grid.SetColumn(gapCell, 3);
            grid.Children.Add(gapCell);

            return grid;
        }

        // ── Helpers ─────────────────────────────────────────────────────────
        private static Border MakeBrand(string text)
        {
            var tb = new TextBlock
            {
                Text = text + " ▸",
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            var b = new Border
            {
                Padding = new Thickness(12, 6, 12, 6),
                BorderThickness = new Thickness(0, 0, 1, 0),
                MinWidth = 96,
                Child = tb,
            };
            b.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
            b.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            return b;
        }

        /// <summary>Cell = right-bordered Border containing a horizontal stack [label, content].</summary>
        private static Border MakeCell(string label, UIElement content)
        {
            var inner = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var lbl = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            inner.Children.Add(lbl);
            inner.Children.Add(content);

            var cell = new Border
            {
                Padding = new Thickness(12, 6, 12, 6),
                BorderThickness = new Thickness(0, 0, 1, 0),
                Child = inner,
            };
            cell.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
            return cell;
        }

        private static TextBlock MakeUnit(string text)
        {
            var tb = new TextBlock { Text = " " + text, VerticalAlignment = VerticalAlignment.Center };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return tb;
        }
    }
}
