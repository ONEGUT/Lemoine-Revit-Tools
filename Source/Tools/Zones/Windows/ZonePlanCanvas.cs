using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LemoineTools.Framework;

using WpfPoint = System.Windows.Point;

namespace LemoineTools.Tools.Zones.Windows
{
    // =========================================================================
    // ZonePlanCanvas — draws a ZoneCanvasScene, fitted and centred, and reports
    // what the user clicked or hovered.
    //
    // Two decisions worth stating, because both have a tempting wrong version:
    //
    //  1. The geometry is PRE-TRANSFORMED into device pixels rather than drawn
    //     in world units under a RenderTransform. A ScaleTransform would scale
    //     stroke thickness and glyphs with it, so a 2px building outline would
    //     render 6px thick on a small floor and hairline on a large one, and
    //     the 6-9.5px labels would be unreadable at both ends. Pre-transforming
    //     means every thickness and font size in the scene is already the final
    //     device value. The cost is a rebuild on resize, which at a few dozen
    //     elements is nothing.
    //
    //  2. It is a Canvas of Shapes, not a DrawingVisual. Hit-testing comes for
    //     free and each shape carries its zone id, which is the whole
    //     interaction model. DrawingVisual would mean hand-rolling hit-testing
    //     for no gain at this element count.
    //
    // The canvas is READ-ONLY: it selects, it never edits geometry. Dragging
    // extents was considered for this window and deliberately excluded.
    // =========================================================================
    public sealed class ZonePlanCanvas : Grid
    {
        private readonly Canvas _surface = new Canvas();
        private readonly Canvas _overlay = new Canvas();

        private ZoneCanvasScene? _scene;

        private Border?    _readout;
        private TextBlock? _readoutName;
        private TextBlock? _readoutDims;

        /// <summary>Raised when the user clicks a mark carrying a hit id.</summary>
        public event Action<string>? ItemClicked;

        public ZonePlanCanvas()
        {
            ClipToBounds = true;

            // Small in-canvas type (6-9.5px Consolas) turns to mush without these.
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            UseLayoutRounding = true;

            // The overlay carries the hover readout only. It must never intercept the mouse,
            // or the readout would sit between the cursor and the shape it describes and the
            // shape would stop receiving MouseMove.
            _overlay.IsHitTestVisible = false;

            Children.Add(_surface);
            Children.Add(_overlay);

            SizeChanged += (s, e) => Redraw();
            MouseLeave  += (s, e) => HideReadout();
        }

        /// <summary>
        /// Replaces the drawing. Pass null to clear.
        ///
        /// Selection is NOT a parameter: the window has already expressed it in each item's
        /// style, so there is one place that decides what "selected" looks like.
        /// </summary>
        public void SetScene(ZoneCanvasScene? scene)
        {
            _scene = scene;
            Redraw();
        }

        // ── Fit ──────────────────────────────────────────────────────────────

        private double _scale = 1, _offX, _offY;

        private double MapX(double x) => _offX + (x - (_scene?.MinX ?? 0)) * _scale;

        // Model Y runs up, screen Y runs down — flipped here, once, so nothing downstream
        // has to remember to.
        private double MapY(double y) => _offY + ((_scene?.MaxY ?? 0) - y) * _scale;

        private void Redraw()
        {
            _surface.Children.Clear();
            _overlay.Children.Clear();
            _readout = null;

            var scene = _scene;
            if (scene == null || !scene.HasBounds) return;

            double inset = scene.InsetPx;
            double vw = ActualWidth  - inset * 2;
            double vh = ActualHeight - inset * 2;
            if (vw <= 1 || vh <= 1) return;

            double ww = scene.WidthWorld, wh = scene.HeightWorld;
            if (ww <= 0 || wh <= 0) return;

            _scale = Math.Min(vw / ww, vh / wh);
            _offX  = inset + (vw - ww * _scale) / 2.0;
            _offY  = inset + (vh - wh * _scale) / 2.0;

            foreach (var item in scene.Items)
            {
                try { AddItem(item); }
                catch (Exception ex)
                {
                    // One malformed mark must not blank the whole drawing — the rest of the
                    // plan is still worth showing, and the reason is in diagnostics.
                    DiagnosticsLog.Swallowed("ZonePlanCanvas: draw item", ex);
                }
            }
        }

        // ── Item rendering ───────────────────────────────────────────────────

        private void AddItem(ZoneCanvasItem item)
        {
            switch (item)
            {
                case ZoneCanvasPoly p:   AddPoly(p);   break;
                case ZoneCanvasRect r:   AddRect(r);   break;
                case ZoneCanvasLine l:   AddLine(l);   break;
                case ZoneCanvasCross c:  AddCross(c);  break;
                case ZoneCanvasHandle h: AddHandle(h); break;
                case ZoneCanvasText t:   AddText(t);   break;
            }
        }

        private void ApplyStyle(Shape shape, ZoneCanvasStyle st)
        {
            shape.Fill   = ZoneCanvasStyle.Resolve(st.Fill,   st.FillAlpha);
            shape.Stroke = ZoneCanvasStyle.Resolve(st.Stroke, st.StrokeAlpha);

            if (shape.Stroke != null && st.Thickness > 0)
            {
                shape.StrokeThickness = st.Thickness;

                if (st.Dash != null && st.Dash.Length > 0)
                {
                    // WPF expresses StrokeDashArray in MULTIPLES OF STROKE THICKNESS; the design's
                    // patterns are absolute device px. Divide, or a 1.2px matchline draws its
                    // 7-3-2-3 pattern at 8.4-3.6-2.4-3.6 and stops meeting its neighbour.
                    var dashes = new DoubleCollection();
                    foreach (double d in st.Dash) dashes.Add(d / st.Thickness);
                    shape.StrokeDashArray = dashes;
                    shape.StrokeDashCap   = PenLineCap.Flat;
                }
            }

            shape.StrokeLineJoin = PenLineJoin.Round;
        }

        private void WireHit(Shape shape, ZoneCanvasItem item)
        {
            if (string.IsNullOrEmpty(item.HitId)) return;

            string id = item.HitId!;
            shape.Tag    = id;
            shape.Cursor = Cursors.Hand;

            shape.MouseLeftButtonUp += (s, e) =>
            {
                try { ItemClicked?.Invoke(id); }
                catch (Exception ex) { DiagnosticsLog.Error("ZonePlanCanvas: selection", ex); }
                e.Handled = true;
            };

            shape.MouseMove  += (s, e) => ShowReadout(item, e.GetPosition(this));
            shape.MouseLeave += (s, e) => HideReadout();
        }

        private void AddPoly(ZoneCanvasPoly p)
        {
            var geo = new PathGeometry { FillRule = FillRule.EvenOdd };

            foreach (var ring in p.Rings)
            {
                if (ring.Count < 2) continue;

                var fig = new PathFigure
                {
                    StartPoint = new WpfPoint(MapX(ring[0].X), MapY(ring[0].Y)),
                    IsClosed   = true,
                    IsFilled   = true,
                };
                for (int i = 1; i < ring.Count; i++)
                    fig.Segments.Add(new LineSegment(new WpfPoint(MapX(ring[i].X), MapY(ring[i].Y)), true));

                geo.Figures.Add(fig);
            }

            if (geo.Figures.Count == 0) return;

            var path = new Path { Data = geo };
            ApplyStyle(path, p.Style);
            WireHit(path, p);
            _surface.Children.Add(path);
        }

        private void AddRect(ZoneCanvasRect r)
        {
            double x0 = MapX(r.MinX), x1 = MapX(r.MaxX);
            double y0 = MapY(r.MaxY), y1 = MapY(r.MinY);   // MaxY maps to the smaller screen y

            var rect = new Rectangle
            {
                Width  = Math.Max(0, x1 - x0),
                Height = Math.Max(0, y1 - y0),
            };
            ApplyStyle(rect, r.Style);
            WireHit(rect, r);

            Canvas.SetLeft(rect, x0);
            Canvas.SetTop(rect, y0);
            _surface.Children.Add(rect);
        }

        private void AddLine(ZoneCanvasLine l)
        {
            var line = new Line
            {
                X1 = MapX(l.X0), Y1 = MapY(l.Y0),
                X2 = MapX(l.X1), Y2 = MapY(l.Y1),
            };
            ApplyStyle(line, l.Style);
            WireHit(line, l);
            _surface.Children.Add(line);
        }

        private void AddCross(ZoneCanvasCross c)
        {
            double cx = MapX(c.X), cy = MapY(c.Y);
            double a  = c.ArmPx;

            var geo = new GeometryGroup();
            geo.Children.Add(new LineGeometry(new WpfPoint(cx - a, cy), new WpfPoint(cx + a, cy)));
            geo.Children.Add(new LineGeometry(new WpfPoint(cx, cy - a), new WpfPoint(cx, cy + a)));

            var path = new Path { Data = geo };
            ApplyStyle(path, c.Style);
            _surface.Children.Add(path);
        }

        private void AddHandle(ZoneCanvasHandle h)
        {
            double s = h.SizePx;
            var rect = new Rectangle { Width = s, Height = s };
            ApplyStyle(rect, h.Style);

            Canvas.SetLeft(rect, MapX(h.X) - s / 2.0);
            Canvas.SetTop(rect,  MapY(h.Y) - s / 2.0);
            _surface.Children.Add(rect);
        }

        private void AddText(ZoneCanvasText t)
        {
            var tb = new TextBlock
            {
                Text       = t.Text ?? "",
                FontSize   = t.FontSizePx,
                FontWeight = t.Bold ? FontWeights.Bold : FontWeights.Normal,
                Foreground = ZoneCanvasStyle.Resolve(t.Ink, 1.0),
                // A label must never eat a click meant for the area underneath it.
                IsHitTestVisible = false,
            };
            if (t.Mono) tb.FontFamily = AppSettings.Instance.ActiveTheme.MonoFont;

            TextOptions.SetTextFormattingMode(tb, TextFormattingMode.Display);

            Canvas.SetLeft(tb, MapX(t.X) + t.OffsetXPx);
            Canvas.SetTop(tb,  MapY(t.Y) + t.OffsetYPx);
            _surface.Children.Add(tb);
        }

        // ── Hover readout ────────────────────────────────────────────────────
        //
        // An overlay Canvas rather than a Popup: a Popup would need StaysOpen=true to be safe
        // at all (StaysOpen=false installs a ComponentDispatcher message hook that corrupts
        // Revit's message loop), and it would still need dismissing by hand. An overlay in
        // container space needs neither, and the fit-scaling never enters the maths because
        // the position comes straight from the cursor.

        private void ShowReadout(ZoneCanvasItem item, WpfPoint at)
        {
            if (string.IsNullOrEmpty(item.HoverName) && string.IsNullOrEmpty(item.HoverDims))
                return;

            if (_readout == null) BuildReadout();
            if (_readout == null || _readoutName == null || _readoutDims == null) return;

            _readoutName.Text = item.HoverName ?? "";
            _readoutDims.Text = item.HoverDims ?? "";
            _readout.Visibility = Visibility.Visible;

            // Centre against the realised height once there is one; the first frame after the
            // readout appears falls back to its desired size.
            double h = _readout.ActualHeight;
            if (h <= 0)
            {
                _readout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                h = _readout.DesiredSize.Height;
            }

            Canvas.SetLeft(_readout, at.X + AppSettings.Instance.S(12));
            Canvas.SetTop(_readout,  at.Y - h / 2.0);
        }

        private void HideReadout()
        {
            if (_readout != null) _readout.Visibility = Visibility.Collapsed;
        }

        private void BuildReadout()
        {
            _readoutName = new TextBlock { FontWeight = FontWeights.SemiBold };
            _readoutName.FontFamily = AppSettings.Instance.ActiveTheme.MonoFont;
            _readoutName.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            _readoutName.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");

            _readoutDims = new TextBlock { Margin = new Thickness(AppSettings.Instance.S(7), 0, 0, 0) };
            _readoutDims.FontFamily = AppSettings.Instance.ActiveTheme.MonoFont;
            _readoutDims.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            _readoutDims.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_readoutName);
            row.Children.Add(_readoutDims);

            _readout = new Border
            {
                CornerRadius    = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                Padding         = AppSettings.Instance.Th(8, 3, 8, 3),
                Child           = row,
                Effect          = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 8, ShadowDepth = 2, Direction = 270, Opacity = 0.45,
                    Color = Colors.Black,
                },
            };
            _readout.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            _readout.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");

            _overlay.Children.Add(_readout);
        }
    }
}
