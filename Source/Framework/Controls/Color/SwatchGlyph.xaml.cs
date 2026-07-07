using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LemoineTools.Framework.Controls
{
    /// <summary>
    /// Pure visual atom — renders the swatch glyph used in legend blocks,
    /// palette tiles, swatch pickers, and the live preview.
    ///
    /// Shape × Fill × Color matrix:
    ///   Kind  ∈ { "square", "circle", "tri", "line", "dash" }
    ///   Fill  ∈ { "solid", "hatch", "dots" }   (line / dash ignore Fill)
    ///   Color : any SolidColorBrush — sets stroke + solid fill + hatch lines + dots
    ///
    /// Sizing:
    ///   GlyphWidth × GlyphHeight (logical px). For circle / triangle the glyph is
    ///   rendered square at GlyphHeight, leaving any remaining width transparent.
    /// </summary>
    public partial class SwatchGlyph : UserControl
    {
        // ── Dependency properties ──────────────────────────────────────────
        public static readonly DependencyProperty KindProperty =
            DependencyProperty.Register(nameof(Kind), typeof(string), typeof(SwatchGlyph),
                new FrameworkPropertyMetadata("square", FrameworkPropertyMetadataOptions.AffectsRender, OnAnyChanged));

        public static readonly DependencyProperty FillProperty =
            DependencyProperty.Register(nameof(Fill), typeof(string), typeof(SwatchGlyph),
                new FrameworkPropertyMetadata("solid", FrameworkPropertyMetadataOptions.AffectsRender, OnAnyChanged));

        public static readonly DependencyProperty SwatchColorProperty =
            DependencyProperty.Register(nameof(SwatchColor), typeof(Color), typeof(SwatchGlyph),
                new FrameworkPropertyMetadata(Color.FromRgb(0x1a, 0x1a, 0x1a),
                    FrameworkPropertyMetadataOptions.AffectsRender, OnAnyChanged));

        public static readonly DependencyProperty GlyphWidthProperty =
            DependencyProperty.Register(nameof(GlyphWidth), typeof(double), typeof(SwatchGlyph),
                new FrameworkPropertyMetadata(22.0, FrameworkPropertyMetadataOptions.AffectsRender, OnAnyChanged));

        public static readonly DependencyProperty GlyphHeightProperty =
            DependencyProperty.Register(nameof(GlyphHeight), typeof(double), typeof(SwatchGlyph),
                new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender, OnAnyChanged));

        public string Kind        { get => (string)GetValue(KindProperty);        set => SetValue(KindProperty, value); }
        public string Fill        { get => (string)GetValue(FillProperty);        set => SetValue(FillProperty, value); }
        public Color  SwatchColor { get => (Color)GetValue(SwatchColorProperty);  set => SetValue(SwatchColorProperty, value); }
        public double GlyphWidth  { get => (double)GetValue(GlyphWidthProperty);  set => SetValue(GlyphWidthProperty, value); }
        public double GlyphHeight { get => (double)GetValue(GlyphHeightProperty); set => SetValue(GlyphHeightProperty, value); }

        public SwatchGlyph()
        {
            InitializeComponent();
            Loaded += (s, e) => Redraw();
        }

        private static void OnAnyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SwatchGlyph g) g.Redraw();
        }

        // ── Render ─────────────────────────────────────────────────────────
        private void Redraw()
        {
            _root.Children.Clear();

            string kind = (Kind ?? "square").ToLowerInvariant();
            string fill = (Fill ?? "solid").ToLowerInvariant();
            double w = GlyphWidth, h = GlyphHeight;
            var stroke = new SolidColorBrush(SwatchColor);
            stroke.Freeze();
            Width  = w;
            Height = h;

            switch (kind)
            {
                case "line":  RenderLine(w, h, stroke, dashed: false); break;
                case "dash":  RenderLine(w, h, stroke, dashed: true);  break;
                case "circle":
                    RenderCircleOrTri(w, h, stroke, fill, asCircle: true);
                    break;
                case "tri":
                case "triangle":
                    RenderCircleOrTri(w, h, stroke, fill, asCircle: false);
                    break;
                default:      RenderSquare(w, h, stroke, fill); break;
            }
        }

        private void RenderLine(double w, double h, SolidColorBrush stroke, bool dashed)
        {
            var line = new Line
            {
                X1 = 0, Y1 = h / 2.0, X2 = w, Y2 = h / 2.0,
                Stroke = stroke,
                StrokeThickness = 2.0,
                SnapsToDevicePixels = true,
            };
            if (dashed) line.StrokeDashArray = new DoubleCollection { 2.5, 2.0 };
            _root.Children.Add(line);
        }

        private void RenderSquare(double w, double h, SolidColorBrush stroke, string fill)
        {
            var border = new Border
            {
                Width = w, Height = h,
                BorderBrush = stroke,
                BorderThickness = new Thickness(1.4),
                Background = fill == "solid" ? (Brush)stroke : Brushes.Transparent,
                ClipToBounds = true,
                SnapsToDevicePixels = true,
            };
            if (fill == "hatch") border.Child = MakeHatchOverlay(stroke, w, h);
            else if (fill == "dots") border.Child = MakeDotsOverlay(stroke, w, h);
            _root.Children.Add(border);
        }

        private void RenderCircleOrTri(double w, double h, SolidColorBrush stroke, string fill, bool asCircle)
        {
            // Render glyph at h × h, centered horizontally within the w slot.
            double size = h;
            var host = new Grid
            {
                Width = size, Height = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                ClipToBounds = true,
                SnapsToDevicePixels = true,
            };

            if (asCircle)
            {
                var ellipse = new Ellipse
                {
                    Width = size, Height = size,
                    Stroke = stroke,
                    StrokeThickness = 1.4,
                    Fill = fill == "solid" ? (Brush)stroke : Brushes.Transparent,
                };
                host.Children.Add(ellipse);
                if (fill == "hatch") host.Children.Add(MakeHatchOverlayClipped(stroke, size, asCircle: true));
                else if (fill == "dots") host.Children.Add(MakeDotsOverlayClipped(stroke, size, asCircle: true));
            }
            else
            {
                var poly = new Polygon
                {
                    Stroke = stroke,
                    StrokeThickness = 1.4,
                    Fill = fill == "solid" ? (Brush)stroke : Brushes.Transparent,
                    Points = new PointCollection
                    {
                        new Point(size / 2.0, 1),
                        new Point(size - 1, size - 1),
                        new Point(1, size - 1),
                    },
                };
                host.Children.Add(poly);
                if (fill == "hatch") host.Children.Add(MakeHatchOverlayClipped(stroke, size, asCircle: false));
                else if (fill == "dots") host.Children.Add(MakeDotsOverlayClipped(stroke, size, asCircle: false));
            }
            _root.Children.Add(host);
        }

        // ── Pattern overlays ───────────────────────────────────────────────
        private static UIElement MakeHatchOverlay(SolidColorBrush stroke, double w, double h)
        {
            var brush = MakeHatchBrush(stroke);
            return new Rectangle { Fill = brush, Width = double.NaN, Height = double.NaN };
        }

        private static UIElement MakeHatchOverlayClipped(SolidColorBrush stroke, double size, bool asCircle)
        {
            var brush = MakeHatchBrush(stroke);
            var rect = new Rectangle { Fill = brush };
            if (asCircle)
                rect.Clip = new EllipseGeometry(new Point(size / 2.0, size / 2.0), size / 2.0, size / 2.0);
            else
            {
                var gp = new PathGeometry();
                var fig = new PathFigure { StartPoint = new Point(size / 2.0, 1), IsClosed = true };
                fig.Segments.Add(new LineSegment(new Point(size - 1, size - 1), false));
                fig.Segments.Add(new LineSegment(new Point(1, size - 1), false));
                gp.Figures.Add(fig);
                rect.Clip = gp;
            }
            return rect;
        }

        private static UIElement MakeDotsOverlay(SolidColorBrush stroke, double w, double h)
        {
            var brush = MakeDotsBrush(stroke);
            return new Rectangle { Fill = brush };
        }

        private static UIElement MakeDotsOverlayClipped(SolidColorBrush stroke, double size, bool asCircle)
        {
            var brush = MakeDotsBrush(stroke);
            var rect = new Rectangle { Fill = brush };
            if (asCircle)
                rect.Clip = new EllipseGeometry(new Point(size / 2.0, size / 2.0), size / 2.0, size / 2.0);
            else
            {
                var gp = new PathGeometry();
                var fig = new PathFigure { StartPoint = new Point(size / 2.0, 1), IsClosed = true };
                fig.Segments.Add(new LineSegment(new Point(size - 1, size - 1), false));
                fig.Segments.Add(new LineSegment(new Point(1, size - 1), false));
                gp.Figures.Add(fig);
                rect.Clip = gp;
            }
            return rect;
        }

        private static DrawingBrush MakeHatchBrush(SolidColorBrush stroke)
        {
            // 45° diagonal stripes — 1px line, 4px spacing.
            var pen = new Pen(stroke, 1.0);
            pen.Freeze();
            var geom = new GeometryGroup();
            geom.Children.Add(new LineGeometry(new Point(-1, 5), new Point(5, -1)));
            geom.Children.Add(new LineGeometry(new Point(0, 0),  new Point(5,  5)));
            var drawing = new GeometryDrawing { Geometry = geom, Pen = pen };
            drawing.Freeze();
            var brush = new DrawingBrush
            {
                Drawing = drawing,
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 5, 5),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None,
            };
            brush.Freeze();
            return brush;
        }

        private static DrawingBrush MakeDotsBrush(SolidColorBrush stroke)
        {
            // 4px tile, 1px dot.
            var dot = new EllipseGeometry(new Point(2, 2), 0.9, 0.9);
            var drawing = new GeometryDrawing { Geometry = dot, Brush = stroke };
            drawing.Freeze();
            var brush = new DrawingBrush
            {
                Drawing = drawing,
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 4, 4),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None,
            };
            brush.Freeze();
            return brush;
        }

        // ── Convenience: convert hex string "#RRGGBB" → Color ───────────────
        public static Color ColorFromHex(string hex, Color fallback)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch { return fallback; }
        }
    }
}
