using System.Collections.Generic;

namespace LemoineTools.Tools.Clash.AutoDimension.Core
{
    /// <summary>Separated hard/soft score. The layout loop drives <see cref="Hard"/> to zero
    /// then minimises <see cref="Soft"/> to a plateau.</summary>
    public readonly struct LayoutScore
    {
        public readonly double Hard;
        public readonly double Soft;
        public LayoutScore(double hard, double soft) { Hard = hard; Soft = soft; }
        public double Total => Hard + Soft;
    }

    /// <summary>
    /// Scores a candidate dimension placement against the obstacle set and already-placed
    /// dimensions. HARD constraints must reach zero (overlap, off-crop, witness crossing);
    /// SOFT penalties (cramped text, uneven spacing, leadering) are minimised. Revit-free and
    /// deterministic — no collection-order dependence beyond the caller-supplied ordering.
    /// </summary>
    public sealed class LayoutScorer
    {
        private readonly LayoutConfig _cfg;
        private readonly Box2? _crop;

        /// <param name="crop">Optional crop boundary in paper space; null disables off-crop scoring.</param>
        public LayoutScorer(LayoutConfig cfg, Box2? crop)
        {
            _cfg  = cfg;
            _crop = crop;
        }

        /// <summary>Scores one dimension given static obstacles and previously placed dimensions.</summary>
        public LayoutScore Score(
            PlannedDimension d,
            IReadOnlyList<Box2> obstacles,
            IReadOnlyList<PlannedDimension> placed)
        {
            double hard = 0;
            double soft = 0;

            // ── HARD: overlap with obstacles ──────────────────────────────────
            for (int i = 0; i < obstacles.Count; i++)
                hard += d.PaperBounds.OverlapArea(obstacles[i]) * _cfg.OverlapWeight;

            // ── HARD: overlap with already-placed dimension strings ───────────
            for (int i = 0; i < placed.Count; i++)
            {
                if (ReferenceEquals(placed[i], d)) continue;
                hard += d.PaperBounds.OverlapArea(placed[i].PaperBounds) * _cfg.OverlapWeight;
            }

            // ── HARD: off-crop ────────────────────────────────────────────────
            if (_crop.HasValue && !_crop.Value.Contains(d.PaperBounds))
                hard += _cfg.OffCropWeight;

            // ── HARD: witness line crossing another string's text ─────────────
            // The witness drops from the source run to the dim line along the offset
            // direction; if that segment passes through a placed string's band it crosses
            // text. Approximated by testing the thin witness box against placed bounds.
            Box2 witness = WitnessBox(d);
            for (int i = 0; i < placed.Count; i++)
            {
                if (ReferenceEquals(placed[i], d)) continue;
                if (witness.Intersects(placed[i].PaperBounds))
                    hard += _cfg.WitnessCrossWeight;
            }

            // ── SOFT: cramped segment text ────────────────────────────────────
            for (int i = 0; i < d.Segments.Count; i++)
            {
                var seg = d.Segments[i];
                if (seg.TextState == SegmentTextState.LeaderOut)
                {
                    soft += _cfg.LeaderWeight;          // leadered → no cramped penalty
                    continue;
                }
                if (seg.IsCramped)
                {
                    double overflow = seg.TextWidthFt - seg.LengthFt;
                    double factor = seg.TextState == SegmentTextState.Staggered ? 0.5 : 1.0;
                    soft += overflow * _cfg.CrampedWeight * factor;
                }
            }

            // ── SOFT: uneven spacing — penalise offsets off the first-offset/spacing cadence ──
            double dev = System.Math.Abs(d.OffsetFt - SnapToGrid(d.OffsetFt));
            soft += dev * _cfg.UnevenSpacingWeight;

            return new LayoutScore(hard, soft);
        }

        /// <summary>Total hard+soft over a whole placed set (each dim vs the others + obstacles).</summary>
        public LayoutScore ScoreAll(IReadOnlyList<PlannedDimension> placed, IReadOnlyList<Box2> obstacles)
        {
            double hard = 0, soft = 0;
            for (int i = 0; i < placed.Count; i++)
            {
                var s = Score(placed[i], obstacles, placed);
                hard += s.Hard;
                soft += s.Soft;
            }
            // Pairwise overlap counted twice above; halve the dim-vs-dim contribution is not
            // necessary for convergence (monotone), but keep totals comparable run-to-run.
            return new LayoutScore(hard, soft);
        }

        private double SnapToGrid(double offset)
        {
            if (_cfg.StringSpacingFt <= 1e-9) return _cfg.FirstOffsetFt;
            double n = (offset - _cfg.FirstOffsetFt) / _cfg.StringSpacingFt;
            double k = System.Math.Round(n);
            return _cfg.FirstOffsetFt + k * _cfg.StringSpacingFt;
        }

        private Box2 WitnessBox(PlannedDimension d)
        {
            Vec2 axis = d.AxisDir.Normalized();
            Vec2 perp = axis.Perp();
            double sign = d.Side == DimSide.Positive ? 1.0 : -1.0;
            Vec2 top = d.SourcePoint + perp * (sign * d.OffsetFt);
            var box = Box2.FromPoints(d.SourcePoint, top);
            double t = _cfg.TextHeightFt * 0.25;
            return new Box2(box.MinX - t, box.MinY - t, box.MaxX + t, box.MaxY + t);
        }
    }
}
