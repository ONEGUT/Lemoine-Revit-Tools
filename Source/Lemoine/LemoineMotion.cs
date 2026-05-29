using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

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
        private static readonly CubicEase EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

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
            var from  = (el.GetValue(prop) as SolidColorBrush)?.Color ?? Colors.Transparent;
            var brush = new SolidColorBrush(from);
            el.SetValue(prop, brush);
            brush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(to, Hover) { EasingFunction = EaseOut });
        }

        private static void RestoreTo(FrameworkElement el, DependencyProperty prop, string? key, Color to)
        {
            var from  = (el.GetValue(prop) as SolidColorBrush)?.Color ?? Colors.Transparent;
            var brush = new SolidColorBrush(from);
            el.SetValue(prop, brush);
            var anim = new ColorAnimation(to, Hover) { EasingFunction = EaseOut };
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
    }
}
