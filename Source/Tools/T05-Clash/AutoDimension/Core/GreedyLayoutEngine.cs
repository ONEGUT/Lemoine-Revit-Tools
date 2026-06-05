using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace LemoineTools.Tools.Clash.AutoDimension.Core
{
    /// <summary>
    /// Tier 1 layout: a deterministic greedy arranger. For each dimension (in a stable order)
    /// it steps the string offset outward until hard constraints clear, then resolves cramped
    /// segments by choosing the operator (inline / flip / stagger / leader-out) with the lowest
    /// soft penalty. Re-runs whole passes until the soft score plateaus or a iteration/time cap
    /// is hit. No parallelism and no hashset iteration — identical input yields an identical plan.
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

            // Processing order is placement priority: each dimension takes the closest offset that
            // clears the ones placed before it, so whoever goes first claims the close spots. Longer
            // chains are placed first and keep the near-source locations; shorter strings and lone
            // spans yield and step outward to avoid them. Side/axial/key tie-break keeps it deterministic.
            dims.Sort((a, b) =>
            {
                int seg = b.Segments.Count.CompareTo(a.Segments.Count);   // more segments first
                if (seg != 0) return seg;
                int s = a.Side.CompareTo(b.Side);
                if (s != 0) return s;
                int c = DimGeometry.AxialStart(a).CompareTo(DimGeometry.AxialStart(b));
                if (c != 0) return c;
                return string.CompareOrdinal(a.SourceKey, b.SourceKey);
            });

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
        }

        /// <summary>Chooses side + offset then per-segment text states for one dimension.</summary>
        private void PlaceOne(
            PlannedDimension d,
            IReadOnlyList<Box2> obstacles,
            IReadOnlyList<PlannedDimension> placed)
        {
            // Step the offset outward, but at EACH (closest-possible) offset try both sides and
            // take whichever clears. Dragging the string to the open/lower side keeps it near the
            // clash instead of marching one stack upward — so a dense run de-stacks across both
            // sides rather than always piling toward the top.
            DimSide original  = d.Side;
            DimSide bestSide   = original;
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
                    DimGeometry.RecomputeBounds(d, _cfg);
                    double hard = _scorer.Score(d, obstacles, placed).Hard;

                    // Lower hard wins; ties prefer the closer offset, then the original side
                    // (deterministic — identical input yields an identical plan).
                    bool better = hard < bestHard - 1e-9;
                    bool tie    = Math.Abs(hard - bestHard) <= 1e-9;
                    bool closer = offset < bestOffset - 1e-9;
                    bool sameOffset      = Math.Abs(offset - bestOffset) <= 1e-9;
                    bool prefersOriginal = side == original && bestSide != original;
                    if (better || (tie && (closer || (sameOffset && prefersOriginal))))
                    {
                        bestHard = hard; bestOffset = offset; bestSide = side;
                    }
                }

                if (bestHard <= 1e-6) break;          // a side cleared at this offset → don't march further out
            }

            d.Side     = bestSide;
            d.OffsetFt = bestOffset;
            ResolveSegments(d);
            DimGeometry.RecomputeBounds(d, _cfg);
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
