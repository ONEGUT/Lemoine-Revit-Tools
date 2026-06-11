using System;
using System.Collections.Generic;
using System.Linq;

namespace LemoineTools.Tools.Clash.AutoDimension.Core
{
    /// <summary>
    /// Row (lane) discipline for stacked dimensions. Two standards-driven jobs:
    ///
    /// 1. <see cref="SortForPlacement"/> — placement priority. Dimensions measuring on the
    ///    same axis whose along-axis spans overlap form a <i>corridor</i>: they compete for
    ///    the same stack of rows. Within a corridor the SHORTEST span places first and takes
    ///    the inner row, longer ones step outward — the ASME Y14.5 ordering that structurally
    ///    prevents a long dimension's line crossing a short one's witness lines.
    ///
    /// 2. <see cref="SnapSharedRows"/> — "align dimensions in one line" (US National CAD
    ///    Standard): two strings on the same axis at nearly the same line level with disjoint
    ///    spans (typically opposite-direction dimensions off one run) are pulled onto one
    ///    shared line level, so they read as a single aligned dimension line. A snap is only
    ///    kept when it does not increase the moved string's hard score.
    ///
    /// Revit-free and deterministic.
    /// </summary>
    public static class RowPlanner
    {
        /// <summary>Sorts <paramref name="dims"/> into placement order: by axis, then corridor,
        /// then shortest span first, then axial start, then key.</summary>
        public static void SortForPlacement(List<PlannedDimension> dims)
        {
            if (dims == null || dims.Count <= 1) return;

            var info = new Dictionary<PlannedDimension, (string tag, double minA, double span, double corridorStart)>();

            foreach (var grp in dims.GroupBy(AxisTag).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                // Sweep the along-axis intervals: overlapping/touching intervals share a corridor.
                var ordered = grp
                    .Select(d =>
                    {
                        Vec2 axis = d.AxisDir.Normalized();
                        double a0 = d.SourcePoint.Dot(axis);
                        double a1 = d.TargetPoint.Dot(axis);
                        return (d, minA: Math.Min(a0, a1), maxA: Math.Max(a0, a1));
                    })
                    .OrderBy(t => t.minA)
                    .ThenBy(t => t.d.SourceKey, StringComparer.Ordinal)
                    .ToList();

                double corridorStart = double.NaN, corridorEnd = double.MinValue;
                foreach (var t in ordered)
                {
                    if (double.IsNaN(corridorStart) || t.minA > corridorEnd)
                    {
                        corridorStart = t.minA;
                        corridorEnd   = t.maxA;
                    }
                    else if (t.maxA > corridorEnd) corridorEnd = t.maxA;

                    info[t.d] = (grp.Key, t.minA, t.maxA - t.minA, corridorStart);
                }
            }

            dims.Sort((x, y) =>
            {
                var ix = info[x];
                var iy = info[y];
                int c = string.CompareOrdinal(ix.tag, iy.tag);
                if (c != 0) return c;
                c = ix.corridorStart.CompareTo(iy.corridorStart);
                if (c != 0) return c;
                c = ix.span.CompareTo(iy.span);                  // shortest first → inner row
                if (c != 0) return c;
                c = ix.minA.CompareTo(iy.minA);
                if (c != 0) return c;
                return string.CompareOrdinal(x.SourceKey, y.SourceKey);
            });
        }

        /// <summary>
        /// Aligns near-level, span-disjoint same-axis pairs onto one shared line level.
        /// Mutates only <see cref="PlannedDimension.OffsetFt"/> (recomputing geometry); a move
        /// is rolled back unless the moved string's hard score stays no worse.
        /// </summary>
        public static void SnapSharedRows(
            List<PlannedDimension> dims, LayoutConfig cfg, LayoutScorer scorer,
            IReadOnlyList<Box2> obstacles)
        {
            if (dims == null || dims.Count < 2) return;
            double levelTol  = cfg.StringSpacingFt * 0.75;   // close enough to want one line
            double maxGap    = cfg.StringSpacingFt * 4.0;    // along-axis nearness of the two spans
            double minOffset = cfg.FirstOffsetFt * 0.5;      // never snap a string onto its run

            // Deterministic candidate sweep: same-axis pairs ordered by key.
            var sorted = dims.OrderBy(d => d.SourceKey, StringComparer.Ordinal).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                for (int j = i + 1; j < sorted.Count; j++)
                {
                    var a = sorted[i];
                    var b = sorted[j];
                    if (!string.Equals(AxisTag(a), AxisTag(b), StringComparison.Ordinal)) continue;

                    double levelA = LineLevel(a);
                    double levelB = LineLevel(b);
                    double dLevel = Math.Abs(levelA - levelB);
                    if (dLevel <= 1e-9 || dLevel > levelTol) continue;

                    (double minA, double maxA) = Span(a);
                    (double minB, double maxB) = Span(b);
                    double gap = Math.Max(minB - maxA, minA - maxB);
                    if (gap < 0 || gap > maxGap) continue;       // spans overlap or sit too far apart

                    // Try snapping b onto a's level, else a onto b's.
                    if (!TrySnap(b, levelA, minOffset, scorer, obstacles, dims, cfg))
                        TrySnap(a, levelB, minOffset, scorer, obstacles, dims, cfg);
                }
            }
        }

        private static bool TrySnap(
            PlannedDimension d, double targetLevel, double minOffset, LayoutScorer scorer,
            IReadOnlyList<Box2> obstacles, List<PlannedDimension> dims, LayoutConfig cfg)
        {
            Vec2 perp = d.AxisDir.Normalized().Perp();
            double sign = d.Side == DimSide.Positive ? 1.0 : -1.0;
            double newOffset = sign * (targetLevel - d.SourcePoint.Dot(perp));
            if (newOffset < minOffset) return false;

            double before = scorer.Score(d, obstacles, dims).Hard;
            double saved  = d.OffsetFt;

            d.OffsetFt = newOffset;
            DimGeometry.RecomputeBounds(d, cfg);
            double after = scorer.Score(d, obstacles, dims).Hard;

            if (after > before + 1e-9)
            {
                d.OffsetFt = saved;
                DimGeometry.RecomputeBounds(d, cfg);
                return false;
            }
            return true;
        }

        internal static string AxisTag(PlannedDimension d) =>
            Math.Abs(d.AxisDir.X) >= Math.Abs(d.AxisDir.Y) ? "x" : "y";

        /// <summary>Perp coordinate of the dimension line (its absolute "row level").</summary>
        internal static double LineLevel(PlannedDimension d)
        {
            Vec2 perp = d.AxisDir.Normalized().Perp();
            double sign = d.Side == DimSide.Positive ? 1.0 : -1.0;
            return d.SourcePoint.Dot(perp) + sign * d.OffsetFt;
        }

        private static (double min, double max) Span(PlannedDimension d)
        {
            Vec2 axis = d.AxisDir.Normalized();
            double a0 = d.SourcePoint.Dot(axis);
            double a1 = d.TargetPoint.Dot(axis);
            return (Math.Min(a0, a1), Math.Max(a0, a1));
        }
    }
}
