using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace LemoineTools.Lemoine
{
    /// <summary>
    /// Reusable cursor-following drag ghost — a transparent, click-through Popup that
    /// mirrors the dragged item beside the cursor for the duration of a DragDrop. One
    /// instance per active drag: <see cref="Begin"/> shows it, <see cref="Update"/>
    /// repositions it (call from <c>QueryContinueDrag</c>), <see cref="End"/> tears it down.
    /// Positioned in screen space via Win32 so it tracks the cursor across the whole window,
    /// not just the source control's bounds. Revit-free so every layer can use it.
    ///
    /// ⚠ The Popup keeps <c>StaysOpen</c> at its default (true). Never set it false here:
    /// StaysOpen=false installs a ComponentDispatcher.ThreadFilterMessage hook that corrupts
    /// Revit's message loop and crashes Revit (see CLAUDE.md "Revit Crash Constraints").
    /// </summary>
    public sealed class LemoineDragGhost
    {
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint pt);
        [DllImport("user32.dll")] private static extern int  GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int  SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        private const int GWL_EXSTYLE       = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private struct NativePoint { public int X; public int Y; }

        private Popup?            _popup;
        private FrameworkElement? _anchor;
        private Point             _clickOffset;

        /// <summary>
        /// Shows the ghost at the cursor. <paramref name="anchor"/> must be a live
        /// visual-tree element — it supplies the device→logical DPI transform.
        /// <paramref name="content"/> is the pill to display (see <see cref="BuildPill"/>);
        /// <paramref name="clickOffset"/> is the cursor-to-pill offset in logical units.
        /// </summary>
        public void Begin(FrameworkElement anchor, FrameworkElement content, Point clickOffset)
        {
            End();
            _anchor      = anchor;
            _clickOffset = clickOffset;
            var lpt = CursorLogical();
            _popup = new Popup
            {
                AllowsTransparency = true,
                IsHitTestVisible   = false,
                Placement          = PlacementMode.AbsolutePoint,
                HorizontalOffset   = lpt.X - clickOffset.X,
                VerticalOffset     = lpt.Y - clickOffset.Y,
                Child              = content,
            };
            _popup.Opened += MakeHwndTransparent;
            _popup.IsOpen  = true;
        }

        /// <summary>Repositions the ghost to the current cursor. Safe to call when inactive.</summary>
        public void Update()
        {
            if (_popup == null) return;
            var lpt = CursorLogical();
            _popup.HorizontalOffset = lpt.X - _clickOffset.X;
            _popup.VerticalOffset   = lpt.Y - _clickOffset.Y;
        }

        /// <summary>Closes the ghost. Safe to call when inactive (idempotent).</summary>
        public void End()
        {
            if (_popup != null) { _popup.IsOpen = false; _popup = null; }
            _anchor = null;
        }

        private Point CursorLogical()
        {
            GetCursorPos(out var pt);
            // ⚠ PresentationSource.FromVisual requires the anchor to be in the live tree.
            var src = _anchor != null ? PresentationSource.FromVisual(_anchor) : null;
            if (src?.CompositionTarget != null)
            {
                var m = src.CompositionTarget.TransformFromDevice;
                return new Point(pt.X * m.M11, pt.Y * m.M22);
            }
            return new Point(pt.X, pt.Y);
        }

        private void MakeHwndTransparent(object? sender, EventArgs e)
        {
            if (_popup?.Child != null &&
                PresentationSource.FromVisual(_popup.Child) is HwndSource hs)
            {
                int ex = GetWindowLong(hs.Handle, GWL_EXSTYLE);
                SetWindowLong(hs.Handle, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT);
            }
        }

        // ── Standard pill content ────────────────────────────────────────────────
        /// <summary>
        /// Builds the standard ghost pill — a colour swatch + name on a raised surface — so
        /// every legend drag ghost looks identical. <paramref name="res"/> is any live tree
        /// element used to resolve theme resources.
        /// </summary>
        public static Border BuildPill(FrameworkElement res, Color color, string name)
        {
            Brush      BrushRes (string key, Brush fb)  => res.TryFindResource(key) as Brush      ?? fb;
            double     DoubleRes(string key, double fb) => res.TryFindResource(key) is double d   ? d : fb;
            FontFamily FontRes  (string key)            => res.TryFindResource(key) as FontFamily ?? new FontFamily("Segoe UI");

            var inner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            inner.Children.Add(new Border
            {
                Width = 14, Height = 10, CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(color),
                Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            inner.Children.Add(new TextBlock
            {
                Text = name, FontWeight = FontWeights.SemiBold,
                Foreground = BrushRes("LemoineText", Brushes.White),
                FontSize = DoubleRes("LemoineFS_SM", 12),
                FontFamily = FontRes("LemoineUiFont"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            return new Border
            {
                CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 4, 8, 4),
                Background  = BrushRes("LemoineRaised", new SolidColorBrush(Color.FromRgb(50, 50, 50))),
                BorderBrush = BrushRes("LemoineBorder", Brushes.Gray),
                Child = inner,
            };
        }
    }
}
