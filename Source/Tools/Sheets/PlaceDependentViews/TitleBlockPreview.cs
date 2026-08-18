using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;

namespace LemoineTools.Tools.Sheets.PlaceDependentViews
{
    /// <summary>
    /// The selected title block drawn to scale with its four margin steppers placed on the four
    /// sides of the sheet — top above it, bottom below it, left at the left, right at the right.
    ///
    /// A column of four steppers labelled Top/Bottom/Left/Right is readable but not obvious: which
    /// edge each number pulls in has to be worked out from the label every time. Put each control
    /// on the edge it governs and there is nothing left to work out. The dashed inner rectangle is
    /// the drawing area the current margins leave, so an over-large margin is visible rather than
    /// discovered on a plot.
    ///
    /// Deliberately Revit-free: it takes a paper size in inches and gives back four numbers.
    /// </summary>
    public sealed class TitleBlockPreview : UserControl
    {
        // Render box the sheet is fitted into (device-independent px). The sheet keeps its real
        // aspect ratio inside this, so a portrait title block reads as portrait.
        private const double MaxRenderW = 300;
        private const double MaxRenderH = 190;

        private readonly Border    _sheet;
        private readonly Rectangle _drawArea;
        private readonly TextBlock _sizeLabel;
        private readonly InlineStepper _top, _bottom, _left, _right;

        private double _sheetW, _sheetH;      // paper inches; 0 = unknown
        private double _mTop = 0.5, _mBottom = 0.5, _mLeft = 0.5, _mRight = 0.5;
        private bool   _suppress;             // guards the programmatic Value writes in SetMargins

        /// <summary>Fires whenever the user changes any margin. Args: top, bottom, left, right (inches).</summary>
        public event Action<double, double, double, double>? MarginsChanged;

        public TitleBlockPreview()
        {
            // ── The sheet itself ──────────────────────────────────────────────
            _drawArea = new Rectangle
            {
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 },
                Fill            = Brushes.Transparent,
            };
            _drawArea.SetResourceReference(Shape.StrokeProperty, "LemoineAccent");

            _sheet = new Border
            {
                BorderThickness     = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Child               = _drawArea,
            };
            _sheet.SetResourceReference(Border.BorderBrushProperty, "LemoineText");
            _sheet.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");

            _sizeLabel = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(0, 6, 0, 0),
                TextWrapping        = TextWrapping.Wrap,
                TextAlignment       = TextAlignment.Center,
            };
            _sizeLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            _sizeLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            _sizeLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var centre = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            centre.Children.Add(_sheet);
            centre.Children.Add(_sizeLabel);

            // ── Steppers, one per edge ────────────────────────────────────────
            _top    = EdgeStepper(v => { _mTop    = v; Raise(); });
            _bottom = EdgeStepper(v => { _mBottom = v; Raise(); });
            _left   = EdgeStepper(v => { _mLeft   = v; Raise(); });
            _right  = EdgeStepper(v => { _mRight  = v; Raise(); });

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Add(grid, EdgeCell(AppStrings.T("testing.placeDependentViews.labels.marginTop"),    _top,    HorizontalAlignment.Center), 0, 1);
            Add(grid, EdgeCell(AppStrings.T("testing.placeDependentViews.labels.marginLeft"),   _left,   HorizontalAlignment.Left),   1, 0);
            Add(grid, centre, 1, 1);
            Add(grid, EdgeCell(AppStrings.T("testing.placeDependentViews.labels.marginRight"),  _right,  HorizontalAlignment.Right),  1, 2);
            Add(grid, EdgeCell(AppStrings.T("testing.placeDependentViews.labels.marginBottom"), _bottom, HorizontalAlignment.Center), 2, 1);

            Content = grid;
            Redraw();
        }

        /// <summary>
        /// Sets the paper size to draw. Pass a non-positive width or height when the title block
        /// family does not publish SHEET_WIDTH / SHEET_HEIGHT — the sheet is then drawn at a
        /// generic landscape ratio and SAYS the size is unknown, rather than inventing one that
        /// looks authoritative and is wrong.
        /// </summary>
        public void SetSheet(double widthIn, double heightIn)
        {
            _sheetW = widthIn  > 0 ? widthIn  : 0;
            _sheetH = heightIn > 0 ? heightIn : 0;
            Redraw();
        }

        /// <summary>Loads the four margins without raising <see cref="MarginsChanged"/> — used when
        /// a different title block is selected and its saved margins come back from the store.</summary>
        public void SetMargins(double top, double bottom, double left, double right)
        {
            _suppress = true;
            try
            {
                _mTop = top; _mBottom = bottom; _mLeft = left; _mRight = right;
                _top.Value = top; _bottom.Value = bottom; _left.Value = left; _right.Value = right;
            }
            finally { _suppress = false; }
            Redraw();
        }

        private void Raise()
        {
            if (_suppress) return;
            Redraw();
            MarginsChanged?.Invoke(_mTop, _mBottom, _mLeft, _mRight);
        }

        // ── Drawing ───────────────────────────────────────────────────────────
        private void Redraw()
        {
            bool known = _sheetW > 0 && _sheetH > 0;
            double w = known ? _sheetW : 42.0;      // generic landscape stand-in
            double h = known ? _sheetH : 30.0;

            double scale = Math.Min(MaxRenderW / w, MaxRenderH / h);
            double rw = w * scale, rh = h * scale;
            _sheet.Width  = rw;
            _sheet.Height = rh;

            // The drawing area inset uses the SAME scale as the sheet, so the dashed rectangle is a
            // true picture of what the margins leave. Clamped so margins larger than the sheet
            // collapse the rectangle to nothing visible instead of inverting it.
            double l = Clamp(_mLeft   * scale, 0, rw / 2);
            double r = Clamp(_mRight  * scale, 0, rw / 2);
            double t = Clamp(_mTop    * scale, 0, rh / 2);
            double b = Clamp(_mBottom * scale, 0, rh / 2);
            _drawArea.Margin = new Thickness(l, t, r, b);

            _sizeLabel.Text = known
                ? AppStrings.T("testing.placeDependentViews.labels.sheetSize", Trim(_sheetW), Trim(_sheetH))
                : AppStrings.T("testing.placeDependentViews.labels.sheetSizeUnknown");
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        private static string Trim(double v) => v.ToString("0.##");

        // ── Chrome helpers ────────────────────────────────────────────────────
        private InlineStepper EdgeStepper(Action<double> set)
        {
            var s = new InlineStepper
            {
                Value = 0.5, MinValue = 0.0, MaxValue = 12.0, Step = 0.125, Decimals = 3,
                ValueWidth = 52,
            };
            s.ValueChanged += (sender, v) => set(v);
            return s;
        }

        private static FrameworkElement EdgeCell(string label, InlineStepper stepper, HorizontalAlignment align)
        {
            var sp = new StackPanel
            {
                HorizontalAlignment = align,
                Margin              = new Thickness(6),
            };
            var lbl = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 2), HorizontalAlignment = align };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            stepper.HorizontalAlignment = align;
            sp.Children.Add(lbl);
            sp.Children.Add(stepper);
            return sp;
        }

        private static void Add(Grid grid, UIElement child, int row, int col)
        {
            Grid.SetRow(child, row);
            Grid.SetColumn(child, col);
            grid.Children.Add(child);
        }
    }
}
