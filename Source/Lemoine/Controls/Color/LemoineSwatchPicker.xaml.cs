using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LemoineTools.Lemoine.Controls
{
    /// <summary>
    /// Compact "shape + fill" picker. Two rows:
    ///   • SHAPE — 5 buttons (square / circle / triangle / line / dash)
    ///   • FILL  — 3 buttons (solid / hatch / dots)
    ///
    /// Designed to be hosted inside a Popup. Fires <see cref="SelectionChanged"/>
    /// whenever Kind or Fill changes. The host owns positioning + open/close.
    /// </summary>
    public partial class LemoineSwatchPicker : UserControl
    {
        // ── Bound state ────────────────────────────────────────────────────
        public static readonly DependencyProperty KindProperty =
            DependencyProperty.Register(nameof(Kind), typeof(string), typeof(LemoineSwatchPicker),
                new PropertyMetadata("square", OnAnyChanged));

        public static readonly DependencyProperty FillProperty =
            DependencyProperty.Register(nameof(Fill), typeof(string), typeof(LemoineSwatchPicker),
                new PropertyMetadata("solid", OnAnyChanged));

        public static readonly DependencyProperty SwatchColorProperty =
            DependencyProperty.Register(nameof(SwatchColor), typeof(Color), typeof(LemoineSwatchPicker),
                new PropertyMetadata(LemoineTheme.FallbackGrey, OnAnyChanged));

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(LemoineSwatchPicker),
                new PropertyMetadata("SYMBOL", OnAnyChanged));

        public static readonly DependencyProperty AllowColorPickProperty =
            DependencyProperty.Register(nameof(AllowColorPick), typeof(bool), typeof(LemoineSwatchPicker),
                new PropertyMetadata(false, OnAnyChanged));

        public string Kind        { get => (string)GetValue(KindProperty);        set => SetValue(KindProperty, value); }
        public string Fill        { get => (string)GetValue(FillProperty);        set => SetValue(FillProperty, value); }
        public Color  SwatchColor { get => (Color)GetValue(SwatchColorProperty);  set => SetValue(SwatchColorProperty, value); }
        public string Title       { get => (string)GetValue(TitleProperty);       set => SetValue(TitleProperty, value); }
        public bool   AllowColorPick { get => (bool)GetValue(AllowColorPickProperty); set => SetValue(AllowColorPickProperty, value); }

        /// <summary>Raised after Kind or Fill changes via a user click.</summary>
        public event EventHandler<SwatchSelectionChangedEventArgs>? SelectionChanged;

        /// <summary>Raised when the user clicks the COLOR tile (only when AllowColorPick=true).</summary>
        public event EventHandler? ColorRequested;

        private static readonly string[] _kinds = { "square", "circle", "tri", "line", "dash" };
        private static readonly string[] _fills = { "solid",  "hatch",  "dots"  };

        public LemoineSwatchPicker()
        {
            InitializeComponent();
            Loaded += (s, e) => Build();
        }

        private static void OnAnyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LemoineSwatchPicker p && p.IsLoaded) p.Build();
        }

        private void Build()
        {
            _outer.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            _outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _inner.Children.Clear();

            // SHAPE header
            _inner.Children.Add(MakeHeader($"{Title?.ToUpperInvariant()} · SHAPE"));

            // SHAPE row
            var shapeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            foreach (var k in _kinds)
            {
                var btn = MakeButton(active: Kind == k);
                var glyph = new LemoineSwatchGlyph
                {
                    Kind = k, Fill = Fill, SwatchColor = SwatchColor,
                    GlyphWidth = 22, GlyphHeight = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                };
                btn.Child = glyph;
                btn.Width = 36; btn.Height = 28;
                string captured = k;
                btn.MouseLeftButtonUp += (s, e) =>
                {
                    Kind = captured;
                    SelectionChanged?.Invoke(this, new SwatchSelectionChangedEventArgs(captured, Fill));
                };
                shapeRow.Children.Add(btn);
            }
            _inner.Children.Add(shapeRow);

            // FILL header
            _inner.Children.Add(MakeHeader("FILL"));

            // FILL row
            var fillRow = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var f in _fills)
            {
                var btn = MakeButton(active: Fill == f);
                btn.Padding = new Thickness(6, 3, 6, 3);
                btn.Height = double.NaN;
                btn.Width  = double.NaN;
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment   = VerticalAlignment.Center,
                };
                var preview = new LemoineSwatchGlyph
                {
                    Kind = "square", Fill = f, SwatchColor = SwatchColor,
                    GlyphWidth = 18, GlyphHeight = 12, Margin = new Thickness(0, 0, 5, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var lbl = new TextBlock
                {
                    Text = f,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                row.Children.Add(preview);
                row.Children.Add(lbl);
                btn.Child = row;
                string captured = f;
                btn.MouseLeftButtonUp += (s, e) =>
                {
                    Fill = captured;
                    SelectionChanged?.Invoke(this, new SwatchSelectionChangedEventArgs(Kind, captured));
                };
                fillRow.Children.Add(btn);
            }
            _inner.Children.Add(fillRow);

            // ── COLOR section — only when AllowColorPick is true ──────────
            if (AllowColorPick)
            {
                var sep = new Border
                {
                    Height = 1, Margin = new Thickness(0, 10, 0, 8),
                };
                sep.SetResourceReference(Border.BackgroundProperty, "LemoineBorderMid");
                _inner.Children.Add(sep);

                _inner.Children.Add(MakeHeader("COLOR"));

                var colorBtn = MakeButton(active: false);
                colorBtn.Padding = new Thickness(6, 3, 6, 3);
                colorBtn.Height = double.NaN;
                colorBtn.Width  = double.NaN;
                colorBtn.Margin = new Thickness(0, 0, 0, 0);

                var colorRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var chip = new Border
                {
                    Width = 22, Height = 14,
                    BorderThickness = new Thickness(1),
                    Background = new SolidColorBrush(SwatchColor),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    SnapsToDevicePixels = true,
                };
                chip.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
                colorRow.Children.Add(chip);
                var colorLbl = new TextBlock { Text = "Pick color…", VerticalAlignment = VerticalAlignment.Center };
                colorLbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                colorLbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                colorLbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                colorRow.Children.Add(colorLbl);
                colorBtn.Child = colorRow;
                colorBtn.MouseLeftButtonUp += (s, e) => ColorRequested?.Invoke(this, System.EventArgs.Empty);

                _inner.Children.Add(colorBtn);
            }
        }

        private static TextBlock MakeHeader(string text)
        {
            var tb = new TextBlock
            {
                Text   = text,
                Margin = new Thickness(0, 0, 0, 6),
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return tb;
        }

        private static Border MakeButton(bool active)
        {
            var b = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
                Margin          = new Thickness(0, 0, 4, 0),
                Padding         = new Thickness(0),
                Cursor          = Cursors.Hand,
            };
            if (active)
            {
                b.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                b.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
            }
            else
            {
                b.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                b.SetResourceReference(Border.BackgroundProperty,  "LemoineBg");
            }
            return b;
        }
    }

    public sealed class SwatchSelectionChangedEventArgs : EventArgs
    {
        public string Kind { get; }
        public string Fill { get; }
        public SwatchSelectionChangedEventArgs(string kind, string fill) { Kind = kind; Fill = fill; }
    }
}
