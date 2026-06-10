using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace LemoineTools.Tools.Clash.AutoDimension.Core
{
    /// <summary>
    /// Deterministic multi-pass arranger. Pass structure:
    ///   1. Greedy placement in standards-true order — corridors of competing same-axis
    ///      dimensions place shortest-span first, so short strings take the inner rows and
    ///      long ones step outward (ASME Y14.5: shortest nearest the object). Re-runs whole
    ///      passes until the soft score plateaus or a cap is hit.
    ///   2. Repair passes — the worst-scoring dimensions are re-placed one at a time with a
    ///      full side × row × text-state search against the entire frozen layout, accepted
    ///      only on improvement. This unsticks the dense cases greedy ordering can't solve.
    ///   3. Shared-row snap — near-level, span-disjoint pairs align onto one line
    ///      (<see cref="RowPlanner.SnapSharedRows"/>).
    /// No parallelism and no hashset iteration — identical input yields an identical plan.
    /// </summary>
    public sealed class GreedyLayoutEngine
    {
        private readonly LayoutConfig _cfg;
        private readonly LayoutScorer _scorer;

        public GreedyLayoutEngine(LayoutConfig cfg, LayoutScorer scorer)
        {
            _cfg    = cfg;
            _scorer = scorer;
        }

        /// <summary>Arranges the dimensions in place, mutating their side/offset/segment states.</summary>
        public void Arrange(List<PlannedDimension> dims, IReadOnlyList<Box2> staticObstacles)
        {
            if (dims == null || dims.Count == 0) return;

            // Placement priority: within each corridor the shortest span goes first and claims
            // the inner row (drafting: shortest dimension nearest the object — that ordering is
            // what keeps long strings' lines outside short strings' witness drops).
            RowPlanner.SortForPlacement(dims);

            var sw = Stopwatch.StartNew();
            double prevSoft = double.MaxValue;

            for (int iter = 0; iter < _cfg.MaxIterations; iter++)
            {
                var placed = new List<PlannedDimension>(dims.Count);

                foreach (var d in dims)
                {
                    PlaceOne(d, staticObstacles, placed);
                    placed.Add(d);
                }

                var total = _scorer.ScoreAll(dims, staticObstacles);

                // Converged: no hard violations and soft has plateaued.
                if (total.Hard <= 1e-6 && Math.Abs(prevSoft - total.Soft) <= _cfg.PlateauEpsilon)
                    break;
                prevSoft = total.Soft;

                if (sw.ElapsedMilliseconds > _cfg.TimeCapMs) break;
            }

            Repair(dims, staticObstacles, sw);

            if (_cfg.AlignSharedRows)
                RowPlanner.SnapSharedRows(dims, _cfg, _scorer, staticObstacles);
        }

        /// <summary>
        /// Worst-first repair: each pass ranks every dimension by its local score against the
        /// WHOLE layout (greedy only ever saw the ones placed before it), then re-places the
        /// offenders with a full side × row search, keeping a move only when its local score
        /// improves. The pairwise score terms are symmetric, so improving one dimension with
        /// everyone else frozen never worsens the total. Stops when a pass changes nothing,
        /// passes run out, or the time cap hits — deterministic throughout.
        /// </summary>
        private void Repair(List<PlannedDimension> dims, IReadOnlyList<Box2> obstacles, Stopwatch sw)
        {
            for (int pass = 0; pass < _cfg.MaxRepairPasses; pass++)
            {
                if (sw.ElapsedMilliseconds > _cfg.TimeCapMs) return;

                var ranked = dims
                    .Select(d => (d, s: _scorer.Score(d, obstacles, dims)))
                    .Where(t => t.s.Hard > 1e-6 || t.s.Soft > 1e-6)
                    .OrderByDescending(t => t.s.Hard)
                    .ThenByDescending(t => t.s.Soft)
                    .ThenBy(t => t.d.SourceKey, StringComparer.Ordinal)
                    .Select(t => t.d)
                    .ToList();

                bool changed = false;
                foreach (var d in ranked)
                {
                    if (sw.ElapsedMilliseconds > _cfg.TimeCapMs) return;

                    DimSide origSide   = d.Side;
                    double  origOffset = d.OffsetFt;
                    int     origDir    = d.TagColumnDir;
                    var     best       = _scorer.Score(d, obstacles, dims);
                    DimSide bestSide   = origSide;
                    double  bestOffset = origOffset;
                    int     bestDir    = origDir;

                    foreach (var side in new[] { origSide, Flip(origSide) })
                    {
                        for (int step = 0; step < _cfg.MaxOffsetSteps; step++)
                        {
                            double offset = _cfg.FirstOffsetFt + step * _cfg.StringSpacingFt;

                            d.Side     = side;
                            d.OffsetFt = offset;
                            ResolveSegments(d);

                            foreach (int dir in HasMovedTags(d) ? BothColumnDirs : ForwardColumnDir)
                            {
                                if (side == origSide && Math.Abs(offset - origOffset) <= 1e-9 && dir == origDir)
                                    continue;   // current placement is already `best`

                                d.TagColumnDir = dir;
                                DimGeometry.RecomputeBounds(d, _cfg);
                                var s = _scorer.Score(d, obstacles, dims);

                                bool better = s.Hard < best.Hard - 1e-9
                                           || (Math.Abs(s.Hard - best.Hard) <= 1e-9 && s.Soft < best.Soft - 1e-9);
                                if (better) { best = s; bestSide = side; bestOffset = offset; bestDir = dir; }
                            }
                        }
                    }

                    d.Side         = bestSide;
                    d.OffsetFt     = bestOffset;
                    d.TagColumnDir = bestDir;
                    ResolveSegments(d);
                    DimGeometry.RecomputeBounds(d, _cfg);
                    if (bestSide != origSide || Math.Abs(bestOffset - origOffset) > 1e-9 || bestDir != origDir)
                        changed = true;
                }

                if (!changed) return;
            }
        }

        /// <summary>Chooses side + offset + tag-column direction, then per-segment text states.</summary>
        private void PlaceOne(
            PlannedDimension d,
            IReadOnlyList<Box2> obstacles,
            IReadOnlyList<PlannedDimension> placed)
        {
            // Step the offset outward, but at EACH (closest-possible) offset try both sides and
            // take whichever clears. Dragging the string to the open/lower side keeps it near the
            // clash instead of marching one stack upward — so a dense run de-stacks across both
            // sides rather than always piling toward the top. When segments moved their tags out,
            // both column directions are tried too, so a busy right side flips the column left
            // exactly like a busy top flips the string below.
            DimSide original  = d.Side;
            DimSide bestSide   = original;
            int     bestDir    = 1;
            double  bestOffset = _cfg.FirstOffsetFt;
            double  bestHard   = double.MaxValue;

            for (int step = 0; step < _cfg.MaxOffsetSteps; step++)
            {
                double offset = _cfg.FirstOffsetFt + step * _cfg.StringSpacingFt;

                foreach (var side in new[] { original, Flip(original) })
                {
                    d.Side     = side;
                    d.OffsetFt = offset;
                    ResolveSegments(d);               // pick segment states at this side/offset

                    foreach (int dir in HasMovedTags(d) ? BothColumnDirs : ForwardColumnDir)
                    {
                        d.TagColumnDir = dir;
                        DimGeometry.RecomputeBounds(d, _cfg);
                        double hard = _scorer.Score(d, obstacles, placed).Hard;

                        // Lower hard wins; ties prefer the closer offset, then the original side.
                        // Iteration order (original side, +axis column first) settles remaining
                        // ties — deterministic: identical input yields an identical plan.
                        bool better = hard < bestHard - 1e-9;
                        bool tie    = Math.Abs(hard - bestHard) <= 1e-9;
                        bool closer = offset < bestOffset - 1e-9;
                        bool sameOffset      = Math.Abs(offset - bestOffset) <= 1e-9;
                        bool prefersOriginal = side == original && bestSide != original;
                        if (better || (tie && (closer || (sameOffset && prefersOriginal))))
                        {
                            bestHard = hard; bestOffset = offset; bestSide = side; bestDir = dir;
                        }
                    }
                }

                if (bestHard <= 1e-6) break;          // a side cleared at this offset → don't march further out
            }

            d.Side         = bestSide;
            d.OffsetFt     = bestOffset;
            d.TagColumnDir = bestDir;
            ResolveSegments(d);
            DimGeometry.RecomputeBounds(d, _cfg);
        }

        private static readonly int[] BothColumnDirs   = { 1, -1 };
        private static readonly int[] ForwardColumnDir = { 1 };

        /// <summary>True when any segment's text moved out of line (the column direction only
        /// matters then).</summary>
        private static bool HasMovedTags(PlannedDimension d)
        {
            foreach (var seg in d.Segments)
                if (TagColumnPlanner.IsMoved(seg.TextState)) return true;
            return false;
        }

        private static DimSide Flip(DimSide s) =>
            s == DimSide.Positive ? DimSide.Negative : DimSide.Positive;

        /// <summary>
        /// Marks each segment's text state. A cramped segment (text wider than its span) is
        /// moved out of line ONLY when its text actually overlaps an adjacent segment's text —
        /// a cramped tag whose overflow lands in empty space (a large/fitting neighbour) is
        /// left inline. The moved state follows the dimension's side so the commit layer stacks
        /// the tags into a column on the correct side: Staggered (above) for the positive side,
        /// Flipped (below) for the negative side. Leader-out is chosen instead when its fixed
        /// penalty beats the moved (half-overflow) penalty.
        /// </summary>
        private void ResolveSegments(PlannedDimension d)
        {
            var segs = d.Segments;

            // Which side the dimension line sits on decides up (Staggered) vs down (Flipped).
            SegmentTextState movedState = d.Side == DimSide.Positive
                ? SegmentTextState.Staggered
                : SegmentTextState.Flipped;

            for (int i = 0; i < segs.Count; i++)
            {
                var seg = segs[i];
                if (!seg.IsCramped)
                {
                    seg.TextState = SegmentTextState.Inline;
                    continue;
                }

                // Cramped, but only a real text–text overlap with a neighbour warrants moving;
                // overflow into a wide/empty neighbour reads fine inline and must not move.
                bool clash =
                    (i > 0              && AdjacentTextOverlap(seg, segs[i - 1])) ||
                    (i < segs.Count - 1 && AdjacentTextOverlap(seg, segs[i + 1]));

                if (!clash)
                {
                    seg.TextState = SegmentTextState.Inline;
                    continue;
                }

                double overflow  = seg.TextWidthFt - seg.LengthFt;
                double movedCost  = overflow * _cfg.CrampedWeight * 0.5;
                double leaderCost = _cfg.LeaderWeight;
                seg.TextState = leaderCost < movedCost ? SegmentTextState.LeaderOut : movedState;
            }
        }

        /// <summary>
        /// True when two adjacent segments' value texts overlap: half each text width together
        /// exceeds the centre-to-centre gap (half each segment length).
        /// </summary>
        private static bool AdjacentTextOverlap(PlannedSegment a, PlannedSegment b)
        {
            double halfText = (a.TextWidthFt + b.TextWidthFt) * 0.5;
            double gap      = (a.LengthFt + b.LengthFt) * 0.5;
            return halfText > gap;
        }

        /// <summary>Count of segments the layout left leadered (for the report).</summary>
        public static int LeaderedCount(IEnumerable<PlannedDimension> dims) =>
            dims.Sum(d => d.Segments.Count(s => s.TextState == SegmentTextState.LeaderOut));
    }
}
