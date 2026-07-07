using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LemoineTools.Framework.Controls
{
    /// <summary>
    /// Renders a small "eye" / "eye-slashed" glyph as WPF shapes (no font
    /// dependency, no asset). Used as the visibility toggle inside legend
    /// blocks and as the "show / hide all" toggle on group headers.
    /// </summary>
    internal static class EyeGlyph
    {
        /// <summary>
        /// Build a Grid containing an eye glyph.
        /// </summary>
        /// <param name="visible">If true, eye is solid; if false, slashed and dim.</param>
        /// <param name="size">Glyph size (logical px). Default ~16.</param>
        public static Grid Make(bool visible, double size = 16)
        {
            var host = new Grid
            {
                Width  = size,
                Height = size,
                SnapsToDevicePixels = true,
                Background = Brushes.Transparent,
            };

            string strokeKey = visible ? "LemoineText"    : "LemoineTextDim";
            string fillKey   = visible ? "LemoineText"    : "LemoineTextDim";

            // Outer almond — drawn as two arcs forming a lens
            var almond = new Path
            {
                StrokeThickness = 1.4,
                StrokeLineJoin  = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap   = PenLineCap.Round,
                Fill = null,
                SnapsToDevicePixels = true,
            };
            almond.SetResourceReference(Shape.StrokeProperty, strokeKey);

            // Lens path: two quadratic arcs meeting at the corners (in normalized
            // 16×16 coords, then scaled). Drawn as a PathGeometry.
            var s = size;
            double cx = s * 0.50, cy = s * 0.50;
            double w  = s * 0.42, h = s * 0.22;
            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(new Point(cx - w, cy), isFilled: false, isClosed: false);
                ctx.QuadraticBezierTo(new Point(cx, cy - h * 1.8), new Point(cx + w, cy), isStroked: true, isSmoothJoin: false);
                ctx.QuadraticBezierTo(new Point(cx, cy + h * 1.8), new Point(cx - w, cy), isStroked: true, isSmoothJoin: false);
            }
            geom.Freeze();
            almond.Data = geom;
            host.Children.Add(almond);

            // Pupil
            var pupil = new Ellipse
            {
                Width = s * 0.28, Height = s * 0.28,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            pupil.SetResourceReference(Shape.FillProperty, fillKey);
            host.Children.Add(pupil);

            // Diagonal slash if hidden
            if (!visible)
            {
                var slash = new Line
                {
                    X1 = s * 0.12, Y1 = s * 0.82,
                    X2 = s * 0.88, Y2 = s * 0.18,
                    StrokeThickness = 1.6,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap   = PenLineCap.Round,
                    SnapsToDevicePixels = true,
                };
                slash.SetResourceReference(Shape.StrokeProperty, "LemoineText");
                host.Children.Add(slash);
            }

            return host;
        }
    }
}
