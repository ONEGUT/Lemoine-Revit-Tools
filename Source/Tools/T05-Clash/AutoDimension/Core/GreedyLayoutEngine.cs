using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace LemoineTools.Tools.Testing.AutoDimension.Core
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

            // Deterministic processing order: side, then axial start, then source key.
            dims.Sort((a, b) =>
            {
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

        /// <summary>Chooses offset then per-segment text states for one dimension.</summary>
        private void PlaceOne(
            PlannedDimension d,
            IReadOnlyList<Box2> obstacles,
            IReadOnlyList<PlannedDimension> placed)
        {
            // 1. Step the string offset outward until hard clears; keep the best seen.
            double bestOffset = _cfg.FirstOffsetFt;
            double bestHard   = double.MaxValue;

            for (int step = 0; step < _cfg.MaxOffsetSteps; step++)
            {
                d.OffsetFt = _cfg.FirstOffsetFt + step * _cfg.StringSpacingFt;
                ResolveSegments(d);               // pick segment states at this offset
                DimGeometry.RecomputeBounds(d, _cfg);

                var sc = _scorer.Score(d, obstacles, placed);
                if (sc.Hard < bestHard)
                {
                    bestHard   = sc.Hard;
                    bestOffset = d.OffsetFt;
                }
                if (sc.Hard <= 1e-6) break;        // cleared → take this offset
            }

            // 2. If no offset cleared, try the other side at the first offset.
            if (bestHard > 1e-6)
            {
                DimSide original = d.Side;
                d.Side     = original == DimSide.Positive ? DimSide.Negative : DimSide.Positive;
                d.OffsetFt = _cfg.FirstOffsetFt;
                ResolveSegments(d);
                DimGeometry.RecomputeBounds(d, _cfg);
                var flipped = _scorer.Score(d, obstacles, placed);
                if (flipped.Hard < bestHard) return; // keep flipped side + first offset

                d.Side = original;                   // revert; fall through to best offset
            }

            d.OffsetFt = bestOffset;
            ResolveSegments(d);
            DimGeometry.RecomputeBounds(d, _cfg);
        }

        /// <summary>
        /// For each cramped segment, pick the text state with the lowest soft penalty:
        /// inline (overflow penalty), staggered (half penalty, uses neighbour space), or
        /// leader-out (no cramped penalty, fixed leader penalty). Flipped is treated as inline
        /// for sizing — it only repositions to dodge a neighbour and is selected by the offset
        /// loop, so here we choose between the size-affecting states deterministically.
        /// </summary>
        private void ResolveSegments(PlannedDimension d)
        {
            foreach (var seg in d.Segments)
            {
                if (!seg.IsCramped)
                {
                    seg.TextState = SegmentTextState.Inline;
                    continue;
                }

                double overflow = seg.TextWidthFt - seg.LengthFt;
                double inlineCost    = overflow * _cfg.CrampedWeight;
                double staggeredCost = overflow * _cfg.CrampedWeight * 0.5;
                double leaderCost    = _cfg.LeaderWeight;

                // Deterministic tie-break order: Inline < Staggered < LeaderOut.
                seg.TextState = SegmentTextState.Inline;
                double best = inlineCost;
                if (staggeredCost < best) { best = staggeredCost; seg.TextState = SegmentTextState.Staggered; }
                if (leaderCost   < best) {                        seg.TextState = SegmentTextState.LeaderOut; }
            }
        }

        /// <summary>Count of segments the layout left leadered (for the report).</summary>
        public static int LeaderedCount(IEnumerable<PlannedDimension> dims) =>
            dims.Sum(d => d.Segments.Count(s => s.TextState == SegmentTextState.LeaderOut));
    }
}
