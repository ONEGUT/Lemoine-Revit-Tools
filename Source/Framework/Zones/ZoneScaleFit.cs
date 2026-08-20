using System;
using System.Collections.Generic;

namespace LemoineTools.Framework.Zones
{
    // =========================================================================
    // ZoneScaleFit — "what is the largest scale at which this area still fits
    // the drawing area of this title block?"
    //
    // Revit-free by design. This project cannot be built on Linux, so a pure
    // function that can be run against real numbers outside Revit is the only
    // part of the placement chain that can actually be checked before it ships.
    //
    // Units and sign conventions, stated once because getting them backwards is
    // silent rather than loud:
    //
    //   View.Scale is a DENOMINATOR. 1/8" = 1'-0" is Scale 96, and one model
    //   foot occupies 1/96 of a paper foot. So:
    //
    //       paperFeet = modelFeet / Scale
    //
    //   A LARGER drawing therefore has a SMALLER denominator. "The largest scale
    //   that fits" means the smallest denominator that fits, which is why the
    //   ladder is walked in ascending order and the first fit wins.
    // =========================================================================
    public static class ZoneScaleFit
    {
        /// <summary>
        /// Standard imperial architectural scales, as <c>View.Scale</c> denominators, largest
        /// drawing first.
        ///
        /// Hardcoded deliberately: Revit exposes no API to enumerate its predefined view-scale
        /// list, so this ladder is the only way to offer the standard set. A user-defined
        /// custom scale cannot be listed at all — a view scale is just an integer.
        /// </summary>
        public static readonly int[] ImperialArchitectural =
        {
            1,    // 12" = 1'-0"
            2,    //  6" = 1'-0"
            4,    //  3" = 1'-0"
            8,    //  1 1/2" = 1'-0"
            12,   //  1" = 1'-0"
            16,   //  3/4" = 1'-0"
            24,   //  1/2" = 1'-0"
            32,   //  3/8" = 1'-0"
            48,   //  1/4" = 1'-0"
            64,   //  3/16" = 1'-0"
            96,   //  1/8" = 1'-0"
            128,  //  3/32" = 1'-0"
            192,  //  1/16" = 1'-0"
            384,  //  1/32" = 1'-0"
        };

        /// <summary>Imperial engineering scales (1" = N'-0"), largest drawing first.</summary>
        public static readonly int[] ImperialEngineering =
        {
            120,  // 1" = 10'-0"
            240,  // 1" = 20'-0"
            360,  // 1" = 30'-0"
            480,  // 1" = 40'-0"
            600,  // 1" = 50'-0"
            720,  // 1" = 60'-0"
            1200, // 1" = 100'-0"
            2400, // 1" = 200'-0"
        };

        /// <summary>Common metric scales as denominators, largest drawing first.</summary>
        public static readonly int[] Metric =
        {
            1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 5000,
        };

        /// <summary>Architectural plus engineering, sorted, de-duplicated. The default ladder.</summary>
        public static readonly int[] DefaultLadder = BuildDefault();

        private static int[] BuildDefault()
        {
            var set = new SortedSet<int>();
            foreach (int s in ImperialArchitectural) set.Add(s);
            foreach (int s in ImperialEngineering)   set.Add(s);
            var arr = new int[set.Count];
            set.CopyTo(arr);
            return arr;
        }

        /// <summary>Outcome of a fit solve.</summary>
        public sealed class Result
        {
            /// <summary>The chosen denominator. Never zero — see <see cref="Fits"/>.</summary>
            public int Scale { get; set; }
            /// <summary>False when even the smallest drawing on the ladder overflows.</summary>
            public bool Fits { get; set; }
            /// <summary>Paper feet left over on each axis at <see cref="Scale"/>. Negative means overflow.</summary>
            public double SlackXFt { get; set; }
            public double SlackYFt { get; set; }
            /// <summary>Drawing footprint at <see cref="Scale"/>, in paper feet.</summary>
            public double PaperWidthFt  { get; set; }
            public double PaperHeightFt { get; set; }
            /// <summary>
            /// True when rotating the view 90° would allow a larger scale than <see cref="Scale"/>.
            /// Informational only — nothing here rotates anything.
            /// </summary>
            public bool RotationWouldImprove { get; set; }
            /// <summary>The scale rotation would allow. 0 when it would not help.</summary>
            public int RotatedScale { get; set; }
        }

        /// <summary>
        /// Largest scale on the ladder at which <paramref name="extentsWidthFt"/> ×
        /// <paramref name="extentsDepthFt"/> of model fits <paramref name="areaWidthFt"/> ×
        /// <paramref name="areaHeightFt"/> of paper.
        ///
        /// When nothing fits, the smallest drawing on the ladder is returned with
        /// <see cref="Result.Fits"/> false and negative slack, rather than a null or a throw —
        /// the caller reports the overflow and still has a usable number to show.
        /// </summary>
        public static Result Solve(double extentsWidthFt, double extentsDepthFt,
                                   double areaWidthFt,   double areaHeightFt,
                                   int[]? ladder = null)
        {
            int[] rungs = (ladder != null && ladder.Length > 0) ? ladder : DefaultLadder;

            // A degenerate extent is a real condition (an area whose extents were never solved),
            // not something to divide by. Treat it as "fits at the largest scale" and let the
            // caller's own validation object to the zero size.
            if (extentsWidthFt <= 0) extentsWidthFt = 0;
            if (extentsDepthFt <= 0) extentsDepthFt = 0;
            if (areaWidthFt  <= 0) areaWidthFt  = 1e-6;
            if (areaHeightFt <= 0) areaHeightFt = 1e-6;

            int chosen = 0;
            for (int i = 0; i < rungs.Length; i++)
            {
                int s = rungs[i];
                if (s <= 0) continue;
                if (extentsWidthFt / s <= areaWidthFt && extentsDepthFt / s <= areaHeightFt)
                {
                    chosen = s;
                    break;
                }
            }

            bool fits = chosen > 0;
            if (!fits) chosen = LargestRung(rungs);   // smallest drawing available

            var r = new Result
            {
                Scale         = chosen,
                Fits          = fits,
                PaperWidthFt  = extentsWidthFt / chosen,
                PaperHeightFt = extentsDepthFt / chosen,
            };
            r.SlackXFt = areaWidthFt  - r.PaperWidthFt;
            r.SlackYFt = areaHeightFt - r.PaperHeightFt;

            // Would turning it 90° do better? Reported, never applied.
            int rotated = FirstFit(extentsDepthFt, extentsWidthFt, areaWidthFt, areaHeightFt, rungs);
            if (rotated > 0 && (!fits || rotated < chosen))
            {
                r.RotationWouldImprove = true;
                r.RotatedScale         = rotated;
            }

            return r;
        }

        /// <summary>True when the extents fit the area at exactly this scale.</summary>
        public static bool FitsAt(double extentsWidthFt, double extentsDepthFt,
                                  double areaWidthFt, double areaHeightFt, int scale)
        {
            if (scale <= 0) return false;
            if (areaWidthFt <= 0 || areaHeightFt <= 0) return false;
            return extentsWidthFt / scale <= areaWidthFt &&
                   extentsDepthFt / scale <= areaHeightFt;
        }

        private static int FirstFit(double w, double d, double areaW, double areaH, int[] rungs)
        {
            for (int i = 0; i < rungs.Length; i++)
            {
                int s = rungs[i];
                if (s > 0 && w / s <= areaW && d / s <= areaH) return s;
            }
            return 0;
        }

        private static int LargestRung(int[] rungs)
        {
            int max = 1;
            foreach (int s in rungs) if (s > max) max = s;
            return max;
        }

        /// <summary>
        /// The next rung down (a smaller drawing) after <paramref name="scale"/>, or 0 when
        /// already at the bottom. Used by the group solver to step down when a packed
        /// arrangement does not fit at the scale the union alone suggested.
        /// </summary>
        public static int NextSmaller(int scale, int[]? ladder = null)
        {
            int[] rungs = (ladder != null && ladder.Length > 0) ? ladder : DefaultLadder;
            int best = 0;
            foreach (int s in rungs)
                if (s > scale && (best == 0 || s < best)) best = s;
            return best;
        }

        /// <summary>
        /// Human-readable label for a denominator ("1/8\" = 1'-0\"", "1\" = 20'-0\""), falling
        /// back to a bare ratio for a value off the standard ladder. Display only.
        /// </summary>
        public static string Label(int scale)
        {
            if (scale <= 0) return "—";
            switch (scale)
            {
                case 1:   return "12\" = 1'-0\"";
                case 2:   return "6\" = 1'-0\"";
                case 4:   return "3\" = 1'-0\"";
                case 8:   return "1 1/2\" = 1'-0\"";
                case 12:  return "1\" = 1'-0\"";
                case 16:  return "3/4\" = 1'-0\"";
                case 24:  return "1/2\" = 1'-0\"";
                case 32:  return "3/8\" = 1'-0\"";
                case 48:  return "1/4\" = 1'-0\"";
                case 64:  return "3/16\" = 1'-0\"";
                case 96:  return "1/8\" = 1'-0\"";
                case 128: return "3/32\" = 1'-0\"";
                case 192: return "1/16\" = 1'-0\"";
                case 384: return "1/32\" = 1'-0\"";
            }
            // Engineering scales are exactly the multiples of 12 above 1"=1'-0".
            if (scale % 12 == 0) return "1\" = " + (scale / 12) + "'-0\"";
            return "1 : " + scale;
        }
    }
}
