using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace LemoineTools.Lemoine
{
    /// <summary>
    /// Shared, theme-aware motion helpers ("rich & snappy"). Revit-free so every
    /// layer can call it. Centralises hover, press, and reveal animations so every
    /// interactive element animates identically. Durations come from
    /// <see cref="LemoineSettings"/> (AnimFast / AnimMed / AnimPress) so they stay
    /// in one place and scale with the global motion tokens.
    ///
    /// Theme-awareness: hover animates a *local* SolidColorBrush, then re-binds the
    /// element to its theme resource on leave-complete, so a live theme switch keeps
    /// updating resting elements.
    /// </summary>
    public static class LemoineMotion
    {
        // MUST be frozen: each tool window runs on its own STA thread, and a non-frozen
        // Freezable (CubicEase) is thread-affine — the second window to animate through this
        // shared static would throw "calling thread cannot access this object because a
        // different thread owns it" and crash Revit. Frozen Freezables are thread-safe.
        private static readonly CubicEase EaseOut = FreezeEase(new CubicEase { EasingMode = EasingMode.EaseOut });

        private static CubicEase FreezeEase(CubicEase e) { e.Freeze(); return e; }

        private static Duration Hover => new Duration(TimeSpan.FromMilliseconds(LemoineSettings.Instance.AnimFast));
        private static Duration Press => new Duration(TimeSpan.FromMilliseconds(LemoineSettings.Instance.AnimPress));
        private static Duration Reveal => new Duration(TimeSpan.FromMilliseconds(LemoineSettings.Instance.AnimMed));

        // ── Hover ─────────────────────────────────────────────────────────────
        /// <summary>
        /// Wires animated hover on a <see cref="Border"/>. <paramref name="normalBgKey"/>
        /// may be null for a transparent resting background. Optionally animates the
        /// border brush too. On leave the dynamic-resource binding is restored.
        /// </summary>
        public static void WireHover(
            Border  b,
            string? normalBgKey, string hoverBgKey,
            string? normalBorderKey = null, string? hoverBorderKey = null)
        {
            if (b == null) return;

            b.MouseEnter += (s, e) =>
            {
                AnimateTo(b, Border.BackgroundProperty, ColorOf(b, hoverBgKey));
                if (hoverBorderKey != null)
                    AnimateTo(b, Border.BorderBrushProperty, ColorOf(b, hoverBorderKey));
            };
            b.MouseLeave += (s, e) =>
            {
                RestoreTo(b, Border.BackgroundProperty, normalBgKey, ColorOf(b, normalBgKey));
                if (hoverBorderKey != null)
                    RestoreTo(b, Border.BorderBrushProperty, normalBorderKey, ColorOf(b, normalBorderKey));
            };
        }

        /// <summary>
        /// Hover for a clickable <see cref="Panel"/> row (e.g. a StackPanel holding a
        /// checkbox + label): fades the panel background in on hover and out on leave.
        /// Forces a transparent resting background so the whole row stays hit-testable.
        /// </summary>
        public static void WireHover(Panel p, string? normalBgKey, string hoverBgKey)
        {
            if (p == null) return;
            if (p.Background == null) p.Background = Brushes.Transparent; // full-width hit area
            p.MouseEnter += (s, e) => AnimateTo(p, Panel.BackgroundProperty, ColorOf(p, hoverBgKey));
            p.MouseLeave += (s, e) => RestoreTo(p, Panel.BackgroundProperty, normalBgKey, ColorOf(p, normalBgKey));
        }

        /// <summary>
        /// Hover for a persistent toggle tab/pill whose active/inactive resting style is
        /// managed elsewhere (re-styled in place rather than rebuilt). Animates a hover
        /// background + border only while <paramref name="isActive"/> is false, so it never
        /// fights the active highlight and never leaves an inactive tab stuck lit.
        /// </summary>
        public static void WireToggleHover(
            Border b, Func<bool> isActive,
            string hoverBgKey = "LemoineAccentDim", string hoverBorderKey = "LemoineAccent")
        {
            if (b == null || isActive == null) return;
            b.MouseEnter += (s, e) =>
            {
                if (isActive()) return;
                AnimateTo(b, Border.BackgroundProperty,  ColorOf(b, hoverBgKey));
                AnimateTo(b, Border.BorderBrushProperty, ColorOf(b, hoverBorderKey));
            };
            b.MouseLeave += (s, e) =>
            {
                if (isActive()) return;
                RestoreTo(b, Border.BackgroundProperty,  null, Colors.Transparent);
                RestoreTo(b, Border.BorderBrushProperty, null, Colors.Transparent);
            };
        }

        // ── Press (tap-down scale) ──────────────────────────────────────────────
        /// <summary>
        /// Adds a snappy 0.97 press-scale to any element. Uses a preview handler so a
        /// Button's Click still fires. Safe to call once per element.
        /// </summary>
        public static void WirePress(FrameworkElement el)
        {
            if (el == null) return;
            if (!(el.RenderTransform is ScaleTransform))
            {
                el.RenderTransform       = new ScaleTransform(1, 1);
                el.RenderTransformOrigin = new Point(0.5, 0.5);
            }
            var st = (ScaleTransform)el.RenderTransform;

            el.PreviewMouseLeftButtonDown += (s, e) => ScaleTo(st, 0.97);
            el.PreviewMouseLeftButtonUp   += (s, e) => ScaleTo(st, 1.0);
            el.MouseLeave                 += (s, e) => ScaleTo(st, 1.0);
        }

        // ── Text-link hover (foreground brighten) ───────────────────────────────
        /// <summary>
        /// Hover affordance for clickable text links (e.g. "Clear", "+ New Set", a set
        /// name) that aren't real buttons: the foreground fades to the accent colour and
        /// back. <paramref name="normalForegroundKey"/> is the resting text resource the
        /// link rebinds to on leave.
        /// </summary>
        public static void WireTextHover(TextBlock tb, string normalForegroundKey, string hoverForegroundKey = "LemoineAccent")
        {
            if (tb == null) return;
            tb.MouseEnter += (s, e) => AnimateTo(tb, TextBlock.ForegroundProperty, ColorOf(tb, hoverForegroundKey));
            tb.MouseLeave += (s, e) =>
                RestoreTo(tb, TextBlock.ForegroundProperty, normalForegroundKey, ColorOf(tb, normalForegroundKey));
        }

        // ── Swatch hover (accent ring + soft shadow lift) ───────────────────────
        /// <summary>
        /// Hover affordance for clickable colour tiles: the border fades to the accent
        /// colour, a soft drop-shadow lifts the tile, and it scales up slightly — a clear
        /// "this is clickable" cue without a press. <paramref name="normalBorderKey"/> is
        /// the resting border resource the tile rebinds to on leave.
        /// Do not combine with <see cref="WirePress"/> on the same element — both own the
        /// RenderTransform.
        /// </summary>
        public static void WireSwatchHover(Border b, string normalBorderKey, double scale = 1.06)
        {
            if (b == null) return;

            // Centre-origin scale transform for the lift.
            var st = new ScaleTransform(1, 1);
            b.RenderTransform       = st;
            b.RenderTransformOrigin = new Point(0.5, 0.5);

            // Soft neutral shadow — created lazily on first hover, NOT at construction, so a
            // step that builds many swatches doesn't allocate dozens of shader effects up
            // front (cheaper to build, and avoids GPU-effect pressure under Revit's host).
            DropShadowEffect? shadow = null;

            b.MouseEnter += (s, e) =>
            {
                if (shadow == null)
                {
                    shadow = new DropShadowEffect
                    {
                        BlurRadius = 8, ShadowDepth = 2, Direction = 270, Color = Colors.Black, Opacity = 0,
                    };
                    b.Effect = shadow;
                }
                AnimateTo(b, Border.BorderBrushProperty, ColorOf(b, "LemoineAccent"));
                shadow.BeginAnimation(DropShadowEffect.OpacityProperty,
                    new DoubleAnimation(0.35, Hover) { EasingFunction = EaseOut });
                LiftTo(st, scale);
            };
            b.MouseLeave += (s, e) =>
            {
                RestoreTo(b, Border.BorderBrushProperty, normalBorderKey, ColorOf(b, normalBorderKey));
                shadow?.BeginAnimation(DropShadowEffect.OpacityProperty,
                    new DoubleAnimation(0, Hover) { EasingFunction = EaseOut });
                LiftTo(st, 1.0);
            };
        }

        // ── Reveal (fade + slight upward slide) ─────────────────────────────────
        /// <summary>
        /// Fades an element in from below — used when a step's content expands.
        /// Replaces any existing RenderTransform with a TranslateTransform, so do not
        /// combine with <see cref="WirePress"/> on the same element.
        /// </summary>
        public static void FadeSlideIn(FrameworkElement el, double fromY = 8)
        {
            if (el == null) return;
            var tt = new TranslateTransform(0, fromY);
            el.RenderTransform = tt;
            el.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, Reveal) { EasingFunction = EaseOut });
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(fromY, 0, Reveal) { EasingFunction = EaseOut });
        }

        // ── internals ───────────────────────────────────────────────────────────
        private static Color ColorOf(FrameworkElement el, string? key)
            => key != null && el.TryFindResource(key) is SolidColorBrush br
                ? br.Color : Colors.Transparent;

        private static void AnimateTo(FrameworkElement el, DependencyProperty prop, Color to)
        {
            var cur = (el.GetValue(prop) as SolidColorBrush)?.Color ?? Colors.Transparent;
            // A fully-transparent rest colour is #00FFFFFF (white). Animating its RGB toward
            // the target flashes white mid-transition, so start from the *target* hue at zero
            // alpha — only the alpha animates, and the intermediate colour stays on-theme.
            var from  = cur.A == 0 ? Color.FromArgb(0, to.R, to.G, to.B) : cur;
            var brush = new SolidColorBrush(from);
            el.SetValue(prop, brush);
            brush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(to, Hover) { EasingFunction = EaseOut });
        }

        private static void RestoreTo(FrameworkElement el, DependencyProperty prop, string? key, Color to)
        {
            var cur  = (el.GetValue(prop) as SolidColorBrush)?.Color ?? Colors.Transparent;
            // Fade out to the same hue at zero alpha (not white) so RGB never interpolates
            // toward #FFFFFF on the way back to a transparent rest state.
            var dest = to.A == 0 ? Color.FromArgb(0, cur.R, cur.G, cur.B) : to;
            var brush = new SolidColorBrush(cur);
            el.SetValue(prop, brush);
            var anim = new ColorAnimation(dest, Hover) { EasingFunction = EaseOut };
            // Re-bind to the theme resource once we're back at rest so live theme
            // switches keep updating the element (the local brush stops following it).
            // Guard on IsMouseOver so a stale leave-completion can't clobber a hover
            // that re-entered before this animation finished.
            anim.Completed += (s, e) =>
            {
                if (el.IsMouseOver) return;
                if (key != null) el.SetResourceReference(prop, key);
                else             el.SetValue(prop, Brushes.Transparent);
            };
            brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }

        private static void ScaleTo(ScaleTransform st, double to)
        {
            var a = new DoubleAnimation(to, Press) { EasingFunction = EaseOut };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        // Hover-paced scale (smoother than the snappier press scale) for the swatch lift.
        private static void LiftTo(ScaleTransform st, double to)
        {
            var a = new DoubleAnimation(to, Hover) { EasingFunction = EaseOut };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
    }
}
