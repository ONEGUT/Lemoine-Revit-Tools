using System;
using System.Collections.Generic;
using System.Windows.Media;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Zones.Windows
{
    // =========================================================================
    // ZoneCanvasScene — a drawing described in WORLD units, with no idea how
    // big the pane it lands in is.
    //
    // Plan mode and Sheet mode are the same problem twice: a set of outlines,
    // rectangles, lines and labels inside a bounding box, uniformly scaled to
    // fit a viewport with the aspect ratio preserved. So there is ONE scene
    // type and ONE renderer (ZonePlanCanvas) rather than two canvases whose
    // fit behaviour could drift apart.
    //
    // World units are model feet in plan mode and sheet inches in sheet mode.
    // The renderer neither knows nor cares which.
    //
    // COLOUR: every ink resolves through ThemePalette (see Resolve below) —
    // never a hex literal. The renderer resolves at build time rather than via
    // SetResourceReference because several of these marks need a fractional
    // ALPHA of a theme colour (area fill at 7%, key-plan fill at 45%) and an
    // opacity variant cannot be expressed as a resource key. That is safe
    // because the window rebuilds the whole canvas on ThemeChanged, so a
    // resolved brush never outlives the palette it came from.
    // =========================================================================

    /// <summary>A named colour role, resolved against the live palette at render time.</summary>
    public enum ZoneCanvasInk
    {
        None,
        Text,       // primary text
        Sub,        // dim/section text, building outline stroke
        Dim,        // secondary text, scope box, unselected anchor
        Border,
        Surface,    // building fill, title block panel
        Page,       // sheet paper
        Raised,
        Accent,     // areas, selection, handles
        Green,      // matchlines, resolved values, slack, "fits"
        Red,        // problems, overflow
    }

    /// <summary>Stroke and fill for one mark. Thickness and dashes are in DEVICE pixels.</summary>
    public struct ZoneCanvasStyle
    {
        public ZoneCanvasInk Fill;
        public double        FillAlpha;      // 0..1, 0 = no fill

        public ZoneCanvasInk Stroke;
        public double        StrokeAlpha;    // 0..1
        public double        Thickness;      // device px

        /// <summary>Absolute dash pattern in device px, or null for a solid line.</summary>
        public double[]? Dash;

        public static ZoneCanvasStyle Solid(ZoneCanvasInk stroke, double thickness, double alpha = 1.0)
            => new ZoneCanvasStyle { Stroke = stroke, StrokeAlpha = alpha, Thickness = thickness };

        public static ZoneCanvasStyle Dashed(ZoneCanvasInk stroke, double thickness, double[] dash, double alpha = 1.0)
            => new ZoneCanvasStyle { Stroke = stroke, StrokeAlpha = alpha, Thickness = thickness, Dash = dash };

        public static ZoneCanvasStyle Filled(ZoneCanvasInk fill, double fillAlpha,
                                             ZoneCanvasInk stroke, double strokeAlpha, double thickness)
            => new ZoneCanvasStyle
            {
                Fill = fill, FillAlpha = fillAlpha,
                Stroke = stroke, StrokeAlpha = strokeAlpha, Thickness = thickness,
            };

        /// <summary>Resolves an ink against the active palette. Never returns null for a used role.</summary>
        public static Brush? Resolve(ZoneCanvasInk ink, double alpha)
        {
            if (ink == ZoneCanvasInk.None || alpha <= 0) return null;

            var p = AppSettings.Instance.ActiveTheme;
            SolidColorBrush? src = ink switch
            {
                ZoneCanvasInk.Text    => p.Text,
                ZoneCanvasInk.Sub     => p.TextSub,
                ZoneCanvasInk.Dim     => p.TextDim,
                ZoneCanvasInk.Border  => p.Border,
                ZoneCanvasInk.Surface => p.Surface,
                ZoneCanvasInk.Page    => p.PageBg,
                ZoneCanvasInk.Raised  => p.Raised,
                ZoneCanvasInk.Accent  => p.Accent,
                ZoneCanvasInk.Green   => p.Green,
                ZoneCanvasInk.Red     => p.Red,
                _                     => null,
            };
            if (src == null) return null;

            if (alpha >= 1.0) return src;

            var c = src.Color;
            var b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * alpha), c.R, c.G, c.B));
            b.Freeze();   // shared across this window's visuals; frozen so it is thread-safe by construction
            return b;
        }
    }

    // ── Items ────────────────────────────────────────────────────────────────

    /// <summary>Base for everything the canvas can draw.</summary>
    public abstract class ZoneCanvasItem
    {
        public ZoneCanvasStyle Style;

        /// <summary>Non-null makes this mark clickable and hoverable, reporting this id.</summary>
        public string? HitId;

        /// <summary>Shown in the hover readout beside the name. Only read when HitId is set.</summary>
        public string? HoverName;
        public string? HoverDims;
    }

    /// <summary>
    /// A closed shape, possibly with holes. The building outline is ONE polygon carrying one
    /// ring — an L-shaped floor is a single closed path, so its interior reads as one slab
    /// rather than as overlapping rectangles.
    /// </summary>
    public sealed class ZoneCanvasPoly : ZoneCanvasItem
    {
        public List<List<ZoneCanvasPoint>> Rings { get; } = new List<List<ZoneCanvasPoint>>();
    }

    /// <summary>An axis-aligned rectangle in world units.</summary>
    public sealed class ZoneCanvasRect : ZoneCanvasItem
    {
        public double MinX, MinY, MaxX, MaxY;
    }

    /// <summary>A straight segment in world units.</summary>
    public sealed class ZoneCanvasLine : ZoneCanvasItem
    {
        public double X0, Y0, X1, Y1;
    }

    /// <summary>
    /// A small cross centred on a world point, with arms measured in DEVICE px so an anchor
    /// stays the same size whether the floor is 40ft or 400ft across.
    /// </summary>
    public sealed class ZoneCanvasCross : ZoneCanvasItem
    {
        public double X, Y;
        public double ArmPx = 3;
    }

    /// <summary>A selection handle: a filled square of a fixed device size, centred on a world point.</summary>
    public sealed class ZoneCanvasHandle : ZoneCanvasItem
    {
        public double X, Y;
        public double SizePx = 5;
    }

    /// <summary>
    /// A text label anchored to a world point and offset by device px, so labels keep their
    /// designed inset from a shape's corner at every zoom.
    /// </summary>
    public sealed class ZoneCanvasText : ZoneCanvasItem
    {
        public double X, Y;
        public double OffsetXPx, OffsetYPx;
        public string Text = "";
        public double FontSizePx = 7.5;
        public bool   Bold;
        public ZoneCanvasInk Ink = ZoneCanvasInk.Text;

        /// <summary>Monospaced (Consolas) — true for every data/dimension label, per the design.</summary>
        public bool Mono = true;
    }

    // ── Scene ────────────────────────────────────────────────────────────────

    /// <summary>A point in world units. Mirrors ZoneGeometrySnapshot.PlanPoint, kept separate so the
    /// renderer has no dependency on the capture DTO.</summary>
    public struct ZoneCanvasPoint
    {
        public double X;
        public double Y;

        public ZoneCanvasPoint(double x, double y) { X = x; Y = y; }
    }

    /// <summary>
    /// One complete drawing: a world bounding box plus the marks inside it, in paint order.
    /// </summary>
    public sealed class ZoneCanvasScene
    {
        public double MinX = double.MaxValue, MinY = double.MaxValue;
        public double MaxX = double.MinValue, MaxY = double.MinValue;

        public List<ZoneCanvasItem> Items { get; } = new List<ZoneCanvasItem>();

        /// <summary>Inset from the pane edge to the drawing, in device px.</summary>
        public double InsetPx = 14;

        public bool HasBounds => MaxX > MinX && MaxY > MinY;

        public double WidthWorld  => HasBounds ? MaxX - MinX : 0;
        public double HeightWorld => HasBounds ? MaxY - MinY : 0;

        /// <summary>Grows the fitted box to include a world point.</summary>
        public void Include(double x, double y)
        {
            if (x < MinX) MinX = x;
            if (y < MinY) MinY = y;
            if (x > MaxX) MaxX = x;
            if (y > MaxY) MaxY = y;
        }

        public void IncludeRect(double minX, double minY, double maxX, double maxY)
        {
            Include(minX, minY);
            Include(maxX, maxY);
        }

        public void Add(ZoneCanvasItem item) => Items.Add(item);
    }
}
