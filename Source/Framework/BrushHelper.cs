using System.Windows.Media;

namespace LemoineTools.Framework
{
    /// <summary>
    /// Shared colour-conversion helpers used across all Lemoine controls.
    /// Centralised here so no window or control needs its own private duplicate.
    /// </summary>
    internal static class BrushHelper
    {
        /// <summary>
        /// Converts a CSS-style hex colour string (e.g. <c>"#4f8fc4"</c> or <c>"4f8fc4"</c>)
        /// to a <see cref="Color"/>. Returns <paramref name="fallback"/> if <paramref name="hex"/>
        /// is null, empty, or cannot be parsed.
        /// </summary>
        public static Color ColorFromHex(string? hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            try { return (Color)ColorConverter.ConvertFromString(hex!); }
            catch { return fallback; }
        }

        /// <summary>
        /// Converts a CSS-style hex colour string to a frozen <see cref="SolidColorBrush"/>.
        /// Returns a brush over <paramref name="fallback"/> if the string cannot be parsed.
        /// </summary>
        public static SolidColorBrush BrushFromHex(string? hex, Color fallback)
        {
            var b = new SolidColorBrush(ColorFromHex(hex, fallback));
            b.Freeze();
            return b;
        }
    }
}
