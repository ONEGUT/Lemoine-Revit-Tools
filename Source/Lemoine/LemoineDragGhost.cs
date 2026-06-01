using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LemoineTools.Lemoine
{
    /// <summary>
    /// Reusable cursor-following drag ghost. Snapshots the element being dragged into a
    /// frozen bitmap (a live view of its current appearance) and draws it on the window's
    /// <see cref="AdornerLayer"/>, centred on the cursor. One instance per active drag:
    /// <see cref="Begin"/> shows it, <see cref="Update"/> repositions it (call from
    /// <c>QueryContinueDrag</c>), <see cref="End"/> removes it. Revit-free.
    ///
    /// The adorner lives in window space (not a screen-space Popup), so it never gets nudged
    /// back on-screen near a screen edge — the ghost tracks the cursor exactly. The snapshot
    /// is a <see cref="RenderTargetBitmap"/> taken at grab time, so the source element may be
    /// dimmed (Opacity=0) afterwards without the ghost going transparent.
    /// </summary>
    public sealed class LemoineDragGhost
    {
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint pt);
        private struct NativePoint { public int X; public int Y; }

        private GhostAdorner?     _adorner;
        private AdornerLayer?     _layer;
        private UIElement?        _adorned;   // element the adorner is attached to (for positioning)
        private Size              _size;

        /// <summary>
        /// Snapshots <paramref name="source"/> and shows the ghost centred on the cursor.
        /// <paramref name="source"/> must be a live, laid-out visual.
        /// </summary>
        public void Begin(FrameworkElement source)
        {
            End();
            if (source == null || source.ActualWidth < 1 || source.ActualHeight < 1) return;

            _size = new Size(source.ActualWidth, source.ActualHeight);
            var img = Snapshot(source, _size);
            if (img == null) return;

            // Adorn the WINDOW's top-level content, not the source itself: GetAdornerLayer(source)
            // returns the nearest layer, which inside a ScrollViewer is clipped to that viewport
            // (the ghost would vanish the moment it left the palette scroll region). Starting from
            // the window content yields the window-spanning AdornerDecorator layer.
            var window = Window.GetWindow(source);
            _adorned = (window?.Content as UIElement) ?? source;
            _layer   = AdornerLayer.GetAdornerLayer(_adorned);
            if (_layer == null) { _adorned = source; _layer = AdornerLayer.GetAdornerLayer(source); }
            if (_layer == null || _adorned == null) { _adorned = null; return; }

            _adorner = new GhostAdorner(_adorned, img, _size);
            _layer.Add(_adorner);
            Update();
        }

        /// <summary>Re-centres the ghost on the current cursor. Safe to call when inactive.</summary>
        public void Update()
        {
            if (_adorner == null || _adorned == null) return;
            GetCursorPos(out var pt);
            // PointFromScreen takes physical screen pixels (what GetCursorPos returns) and maps
            // them into the adorned element's own coordinate space, DPI included.
            var local = _adorned.PointFromScreen(new Point(pt.X, pt.Y));
            _adorner.MoveTo(new Point(local.X - _size.Width / 2.0, local.Y - _size.Height / 2.0));
        }

        /// <summary>Removes the ghost. Safe to call when inactive (idempotent).</summary>
        public void End()
        {
            if (_layer != null && _adorner != null) _layer.Remove(_adorner);
            _adorner = null;
            _layer   = null;
            _adorned = null;
        }

        private static ImageSource? Snapshot(FrameworkElement el, Size size)
        {
            var src = PresentationSource.FromVisual(el);
            double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            int pw = (int)Math.Ceiling(size.Width  * sx);
            int ph = (int)Math.Ceiling(size.Height * sy);
            if (pw < 1 || ph < 1) return null;

            var rtb = new RenderTargetBitmap(pw, ph, 96 * sx, 96 * sy, PixelFormats.Pbgra32);
            rtb.Render(el);
            rtb.Freeze();
            return rtb;
        }

        // ── Adorner that paints the frozen snapshot at a movable offset ──────────
        private sealed class GhostAdorner : Adorner
        {
            private readonly ImageSource _img;
            private readonly Size        _size;
            private Point                _topLeft;

            public GhostAdorner(UIElement adorned, ImageSource img, Size size) : base(adorned)
            {
                _img             = img;
                _size            = size;
                IsHitTestVisible = false;
                Opacity          = 0.85;
            }

            public void MoveTo(Point topLeft)
            {
                _topLeft = topLeft;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext dc)
            {
                dc.DrawImage(_img, new Rect(_topLeft, _size));
            }
        }
    }
}
