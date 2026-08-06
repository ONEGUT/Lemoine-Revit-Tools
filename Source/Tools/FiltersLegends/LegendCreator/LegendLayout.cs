using System;
using System.Collections.Generic;

namespace LemoineTools.Tools.FiltersLegends.LegendCreator
{
    /// <summary>
    /// Process-wide cache of TextNoteType cap heights (paper inches), keyed by the type's
    /// ElementId value. Populated on the Revit main thread when the Legend Creator launches
    /// (the only place a document is available), then read by the preview — which runs on the
    /// settings window's own STA thread and cannot query Revit — to size each role's text to
    /// the real type, not the font-point fallback.
    /// </summary>
    public static class LegendTextTypeSizes
    {
        // Keyed by TextNoteType NAME, not ElementId: the legend entry that selects a style
        // is stored machine-wide and reused across projects, where an ElementId means
        // nothing but a name still resolves.
        private static Dictionary<string, double> _capInByType =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Replaces the cached map. Pass type NAME → cap height in paper inches.</summary>
        public static void Set(Dictionary<string, double>? map)
            => _capInByType = map ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Cap height (paper inches) for a role's chosen TextNoteType name, or the font-point
        /// fallback when the name is unset or not present in the current document.
        /// </summary>
        public static double CapInches(string? typeName, int fallbackPt)
            => !string.IsNullOrEmpty(typeName)
               && _capInByType.TryGetValue(typeName!, out double c) && c > 0
                ? c
                : LegendLayout.PointsToInches(fallbackPt);
    }

    // =========================================================================
    // LegendLayout — single source of truth for legend layout math.
    //
    // Everything here is computed in PAPER INCHES (origin top-left, +Y downward).
    // The WPF preview renders these values at 96 px/inch; the Revit handler
    // converts them to model feet via InchesToFeet(value, viewScale). Because both
    // sides call the SAME formulas with the SAME inputs (real per-role text cap
    // heights + the same width estimate), the preview matches the generated legend.
    //
    // This replaces the previous split where the preview used FontPt for all text
    // and a vertical "Gap", while the handler used real TextNoteType sizes and a
    // horizontal "Gap" — two layouts that could never agree.
    // =========================================================================
    public static class LegendLayout
    {
        // Vertical breathing room added to each entry beyond the taller of swatch/label.
        public const double EntryPadIn  = 0.04;
        // Gap below a group header before its first entry, beyond the header cap height.
        public const double HeaderPadIn = 0.05;
        // Gap below the title / subtitle blocks.
        public const double TitlePadIn  = 0.06;
        public const double SubPadIn    = 0.08;

        // Label width is estimated from character count: most legend labels are short
        // and a real-font measurement isn't available on the Revit thread, so a shared
        // estimate keeps preview and output column widths identical. 0.6 is conservative.
        public const double LabelAspect = 0.6;
        // Proven-safe upper bound for a TextNote width (feet at emit) — never exceeded.
        public const double MaxLabelIn  = 4.0;

        /// <summary>Estimated label width (paper inches) for a string at the given cap height.</summary>
        public static double LabelWidthIn(string? text, double capIn)
        {
            if (string.IsNullOrEmpty(text)) return capIn * 2.0;
            double w = Math.Max(text!.Length * capIn * LabelAspect, capIn * 2.0);
            return Math.Min(w, MaxLabelIn);
        }

        /// <summary>Vertical step between consecutive entries (paper inches).</summary>
        public static double EntryHeightIn(double swatchHIn, double labelCapIn)
            => Math.Max(swatchHIn, labelCapIn) + EntryPadIn;

        /// <summary>
        /// Vertical distance from a group header's band top to its first entry CENTRE:
        /// header text band, the header pad, then half the first entry's height — matching
        /// the preview, which stacks header → pad → entry rows with centred content.
        /// </summary>
        public static double HeaderAdvanceIn(double headerCapIn, double entryHIn)
            => headerCapIn + HeaderPadIn + entryHIn / 2.0;

        /// <summary>Converts a TextNoteType size (Revit internal feet, paper-space) to paper inches.</summary>
        public static double FeetToInches(double feet) => feet * 12.0;

        /// <summary>Cap height (paper inches) for a font point size, used as a fallback.</summary>
        public static double PointsToInches(double pt) => pt / 72.0;

        /// <summary>Converts a paper-inch measurement to model feet for a drafting view scale.</summary>
        public static double InchesToFeet(double inches, double viewScale)
            => inches / 12.0 * viewScale;
    }

    /// <summary>
    /// Per-role text cap heights (paper inches) for one legend. Built from each role's
    /// chosen TextNoteType size, falling back to the layout's font-point size.
    /// </summary>
    public readonly struct LegendRoleCaps
    {
        public readonly double TitleIn;
        public readonly double SubtitleIn;
        public readonly double HeaderIn;
        public readonly double LabelIn;

        public LegendRoleCaps(double titleIn, double subtitleIn, double headerIn, double labelIn)
        {
            TitleIn = titleIn; SubtitleIn = subtitleIn; HeaderIn = headerIn; LabelIn = labelIn;
        }

        /// <summary>All roles at the given font-point size — the no-document fallback.</summary>
        public static LegendRoleCaps FromFontPt(int fontPt)
        {
            double c = LegendLayout.PointsToInches(fontPt);
            return new LegendRoleCaps(c, c, c, c);
        }
    }
}
