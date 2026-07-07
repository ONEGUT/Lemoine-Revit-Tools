using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace LemoineTools.Tools.Dimensioning.AutoDimension.Core
{
    /// <summary>
    /// Deterministic multi-pass arranger, working CLUSTER BY CLUSTER. Pass structure:
    ///   1. For each cluster (ordered by id) the group's dimensions are greedy-placed and
    ///      re-passed until the group's score plateaus — every candidate is scored against the
    ///      clusters already placed AND the group's own members, so the group is optimized
    ///      jointly inside its working region instead of each dimension fending for itself
    ///      across the whole view. Within a group, corridors place shortest-span first
    ///      (ASME Y14.5: shortest nearest the object).
    ///   2. Repair passes — the worst-scoring dimensions VIEW-WIDE are re-placed one at a time
    ///      with a full side × row × column-end search against the entire frozen layout,
    ///      accepted only on improvement. This unsticks what greedy ordering can't.
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

            var sw = Stopwatch.StartNew();

            // One group per cluster, ordered by cluster id — deterministic. Unclustered
            // dimensions ("" id) form their own leading group.
            var groups = dims
                .GroupBy(d => d.ClusterId ?? "")
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.ToList())
                .ToList();

            var settled = new List<PlannedDimension>(dims.Count);   // clusters already placed
            foreach (var group in groups)
            {
                // Placement priority within the group: shortest span first per corridor
                // (drafting: shortest dimension nearest the object — that ordering is what
                // keeps long strings' lines outside short strings' witness drops).
                RowPlanner.SortForPlacement(group);

                double prevSoft = double.MaxValue;
                for (int iter = 0; iter < _cfg.MaxIterations; iter++)
                {
                    var placed = new List<PlannedDimension>(settled.Count + group.Count);
                    placed.AddRange(settled);

                    foreach (var d in group)
                    {
                        PlaceOne(d, staticObstacles, placed);
                        placed.Add(d);
                    }

                    // Converged: the group has no hard violations and its soft has plateaued.
                    var total = ScoreGroup(group, staticObstacles, placed);
                    if (total.Hard <= 1e-6 && Math.Abs(prevSoft - total.Soft) <= _cfg.PlateauEpsilon)
                        break;
                    prevSoft = total.Soft;

                    if (sw.ElapsedMilliseconds > _cfg.TimeCapMs) break;
                }

                settled.AddRange(group);
                if (sw.ElapsedMilliseconds > _cfg.TimeCapMs) break;
            }

            Repair(dims, staticObstacles, sw);

            if (_cfg.AlignSharedRows)
                RowPlanner.SnapSharedRows(dims, _cfg, _scorer, staticObstacles);
        }

        /// <summary>Sum of one group's members' scores against <paramref name="all"/>.</summary>
        private LayoutScore ScoreGroup(
            List<PlannedDimension> group, IReadOnlyList<Box2> obstacles,
            IReadOnlyList<PlannedDimension> all)
        {
            double hard = 0, soft = 0;
            foreach (var d in group)
            {
                var s = _scorer.Score(d, obstacles, all);
                hard += s.Hard;
                soft += s.Soft;
            }
            return new LayoutScore(hard, soft);
        }

        /// <summary>
        /// Worst-first repair: each pass ranks every dimension by its local score against the
        /// WHOLE layout (greedy only ever saw the ones placed before it), then re-places the
        /// offenders with a full side × row × column-end search, keeping a move only when its
        /// local score improves. The pairwise score terms are symmetric, so
        /// improving one dimension with everyone else frozen never worsens the total. Stops
        /// when a pass changes nothing, passes run out, or the time cap hits — deterministic.
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

                            foreach (int dir in HasMovedTags(d) ? BothDirs : ForwardDir)
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
            // both column ends are tried too, so a busy right side flips the column left exactly
            // like a busy top flips the string below. Tags always stack on the dimension's own
            // side (above an above-line string, below a below one).
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

                    foreach (int dir in HasMovedTags(d) ? BothDirs : ForwardDir)
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

        private static readonly int[] BothDirs   = { 1, -1 };
        private static readonly int[] ForwardDir = { 1 };

        /// <summary>True when any segment's text moved out of line (the column direction/stack
        /// side only matter then).</summary>
        private static bool HasMovedTags(PlannedDimension d)
        {
            foreach (var seg in d.Segments)
                if (TagColumnPlanner.IsMoved(seg.TextState)) return true;
            return false;
        }

        private static DimSide Flip(DimSide s) =>
            s == DimSide.Positive ? DimSide.Negative : DimSide.Positive;

        /// <summary>
        /// Marks each segment's text state. FIXED RULE: any tag wider than its segment's
        /// crossbar (cramped) is pulled off the dimension into the tag column, exactly like a
        /// chained dimension's relocated values — never left overhanging inline. The moved
        /// state follows the dimension's side so the commit layer stacks the tags on the
        /// SAME side as the value text: Staggered (above) for an above-line/positive string,
        /// Flipped (below) for a below-line/negative one. Fitting text stays inline.
        /// </summary>
        private void ResolveSegments(PlannedDimension d)
        {
            SegmentTextState movedState = d.Side == DimSide.Positive
                ? SegmentTextState.Staggered
                : SegmentTextState.Flipped;

            foreach (var seg in d.Segments)
                seg.TextState = seg.IsCramped ? movedState : SegmentTextState.Inline;
        }

        /// <summary>Count of segments whose value tag was pulled off the dimension (for the report).</summary>
        public static int MovedTagCount(IEnumerable<PlannedDimension> dims) =>
            dims.Sum(d => d.Segments.Count(s => TagColumnPlanner.IsMoved(s.TextState)));
    }
}
