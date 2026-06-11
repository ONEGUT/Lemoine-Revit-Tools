using System;
using System.Collections.Generic;
using System.Linq;

namespace LemoineTools.Tools.Clash.AutoDimension.Core
{
    /// <summary>
    /// Plans the moved-tag columns in the abstract layout space — the same column geometry
    /// <c>AutoDimensionCommit.PlaceColumn</c> realizes (front edges aligned just past the
    /// group's far end, nearest anchor lowest, one row per tag) — so the scorer can SEE the
    /// relocated value texts and their leaders before anything is committed. Commit remains
    /// the source of truth for the final positions (it re-measures with the realized
    /// <c>ValueString</c> and bumps off real obstacles); this planner exists so a layout that
    /// piles tags onto a neighbour scores badly at plan time instead of surprising at commit.
    /// Revit-free and deterministic.
    /// </summary>
    public static class TagColumnPlanner
    {
        /// <summary>Sets <see cref="PlannedSegment.TagPos"/> (view-2D centre of the planned
        /// tag) for every moved segment at the dimension's current side/offset/text states,
        /// and clears it on inline segments.</summary>
        public static void PlanColumns(PlannedDimension d, LayoutConfig cfg)
        {
            foreach (var seg in d.Segments) seg.TagPos = null;

            double th = cfg.TextHeightFt;
            if (th <= 0 || d.Segments.Count == 0) return;

            Vec2 axis = d.AxisDir.Normalized();
            Vec2 perp = axis.Perp();
            double sign = d.Side == DimSide.Positive ? 1.0 : -1.0;
            double dir  = d.TagColumnDir < 0 ? -1.0 : 1.0;
            // Stack direction: +1 climbs away from the line on its offset side; -1 mirrors the
            // column to the other side of the line ("dragged below the dimension") — the escape
            // when witnesses from scattered cluster anchors foul the above-line column zone.
            double stack = d.TagStackDir < 0 ? -1.0 : 1.0;
            double lineLevel = d.SourcePoint.Dot(perp) + sign * d.OffsetFt;

            double[] bounds = DimGeometry.SegmentBoundaries(d, axis);

            // Maximal runs of consecutive moved segments — an inline segment breaks the run,
            // mirroring the walk in AutoDimensionCommit.ApplyTextStates.
            var run = new List<(PlannedSegment seg, double centreA)>();
            for (int k = 0; k <= d.Segments.Count; k++)
            {
                if (k < d.Segments.Count && IsMoved(d.Segments[k].TextState))
                {
                    run.Add((d.Segments[k], (bounds[k] + bounds[k + 1]) * 0.5));
                    continue;
                }
                if (run.Count > 0)
                {
                    PlanColumn(run, axis, perp, sign * stack, dir, lineLevel, th, cfg);
                    run.Clear();
                }
            }
        }

        private static void PlanColumn(
            List<(PlannedSegment seg, double centreA)> run,
            Vec2 axis, Vec2 perp, double sign, double dir, double lineLevel, double th, LayoutConfig cfg)
        {
            // Front line just past the group's furthest dimension edge IN THE COLUMN DIRECTION
            // (the dir-most moved segment's far witness), exactly as commit aligns the realized
            // tags. dir=+1 hangs the column off the +axis end, dir=-1 mirrors it to the other side.
            double edge = dir > 0 ? double.MinValue : double.MaxValue;
            foreach (var t in run)
            {
                double e = t.centreA + dir * t.seg.LengthFt * 0.5;
                if (dir > 0 ? e > edge : e < edge) edge = e;
            }
            double frontLine = edge + dir * cfg.TagColumnAlongHeights * th;

            // Nearest anchor to the column lowest, each farther one a row higher → leaders nest.
            var ordered = dir > 0
                ? run.OrderByDescending(t => t.centreA).ToList()
                : run.OrderBy(t => t.centreA).ToList();

            double level = cfg.TagColumnBaseHeights * th;
            double step  = cfg.TagColumnStepHeights * th;
            foreach (var t in ordered)
            {
                double halfAlong = Math.Max(t.seg.TextWidthFt, th) * 0.5;
                double centreA   = frontLine + dir * halfAlong;
                t.seg.TagPos = axis * centreA + perp * (lineLevel + sign * level);
                level += step;
            }
        }

        internal static bool IsMoved(SegmentTextState s) =>
            s == SegmentTextState.LeaderOut ||
            s == SegmentTextState.Staggered ||
            s == SegmentTextState.Flipped;
    }
}
