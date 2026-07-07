using System;
using System.Collections.Generic;

namespace LemoineTools.Tools.Dimensioning.AutoDimension.Core
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

    /// <summary>Per-constraint tallies for one dimension's score — filled by
    /// <see cref="LayoutScorer.Score"/> when supplied, consumed by the layout snapshot so a
    /// bad placement names exactly which rule(s) it lost to.</summary>
    public sealed class ScoreDetail
    {
        public double BandVsObstacles;     // line-band overlap area with static annotations
        public double TextVsObstacles;     // value-text overlap area with static annotations
        public double BandVsBands;         // line-band overlap area with other strings
        public double TextVsTextOrBand;    // value-text overlap area with other text/bands
        public int    LineCrossesLine;     // dim line × dim line crossings
        public int    LineCrossesWitness;  // dim line × witness crossings (either direction)
        public int    WitnessThroughText;  // witness slicing through value text
        public int    LeaderCrossings;     // leader × leader
        public int    LeaderLineCrossings; // leader × dim/witness line
        public bool   OffCrop;
        public double CrampedPenalty;
        public double LeaderSlack;
        public double SpacingDeviation;
        public double StaggerPenalty;
        public double RegionOverflow;      // line/text area outside the cluster's working region

        public override string ToString()
        {
            var parts = new List<string>();
            void Add(string label, double v) { if (v > 1e-9) parts.Add($"{label} {v:0.##}"); }
            Add("band×obstacle", BandVsObstacles);
            Add("text×obstacle", TextVsObstacles);
            Add("band×band", BandVsBands);
            Add("text×text/band", TextVsTextOrBand);
            if (LineCrossesLine > 0)     parts.Add($"line×line ×{LineCrossesLine}");
            if (LineCrossesWitness > 0)  parts.Add($"line×witness ×{LineCrossesWitness}");
            if (WitnessThroughText > 0)  parts.Add($"witness×text ×{WitnessThroughText}");
            if (LeaderCrossings > 0)     parts.Add($"leader×leader ×{LeaderCrossings}");
            if (LeaderLineCrossings > 0) parts.Add($"leader×line ×{LeaderLineCrossings}");
            if (OffCrop) parts.Add("off-crop");
            Add("cramped", CrampedPenalty);
            Add("leader-slack", LeaderSlack);
            Add("spacing-dev", SpacingDeviation);
            Add("stagger", StaggerPenalty);
            Add("region-overflow", RegionOverflow);
            return string.Join("; ", parts);
        }
    }

    /// <summary>
    /// Scores a candidate dimension placement against the obstacle set and other dimensions,
    /// using the full drawn anatomy (<see cref="DimAnatomy"/>) rather than one fat band.
    /// HARD constraints must reach zero: text or line band overlapping anything, a dimension
    /// line crossing another dimension line or a witness line (ASME Y14.5 §1.7.2 — witness
    /// lines may cross each other, dimension lines never cross), a witness slicing through
    /// value text, off-crop. SOFT penalties are minimised: cramped text, leadering, leader
    /// crossings and slack, uneven lane spacing. Revit-free and deterministic.
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

        /// <summary>Scores one dimension given static obstacles and the other dimensions to
        /// respect (the already-placed set during greedy passes; everyone else during repair).
        /// Pass a <paramref name="detail"/> to additionally collect per-constraint tallies
        /// (snapshot/diagnostics path — same single scoring code path either way).</summary>
        public LayoutScore Score(
            PlannedDimension d,
            IReadOnlyList<Box2> obstacles,
            IReadOnlyList<PlannedDimension> placed,
            ScoreDetail? detail = null)
        {
            double hard = 0;
            double soft = 0;

            DimAnatomy an = d.Anatomy ?? (d.Anatomy = DimAnatomy.Build(d, _cfg));

            // ── HARD: static obstacles (existing annotations, markers, source lines) ──
            // The line band and every value text must stay clear. Witness lines are NOT
            // penalised here — extension lines may cross object lines (ASME Y14.5).
            for (int i = 0; i < obstacles.Count; i++)
            {
                Box2 ob = obstacles[i];
                if (!d.PaperBounds.Intersects(ob)) continue;
                double a1 = an.LineBand.OverlapArea(ob);
                hard += a1 * _cfg.OverlapWeight;
                if (detail != null) detail.BandVsObstacles += a1;
                foreach (var tb in an.TextBoxes)
                {
                    double a2 = tb.OverlapArea(ob);
                    hard += a2 * _cfg.OverlapWeight;
                    if (detail != null) detail.TextVsObstacles += a2;
                }
            }

            // ── vs other dimensions ────────────────────────────────────────────
            for (int i = 0; i < placed.Count; i++)
            {
                var p = placed[i];
                if (ReferenceEquals(p, d)) continue;
                var pa = p.Anatomy;
                if (pa == null) continue;
                if (!d.PaperBounds.Intersects(p.PaperBounds)) continue;

                // Line bands must not share area (string-on-string).
                double bb = an.LineBand.OverlapArea(pa.LineBand);
                hard += bb * _cfg.OverlapWeight;
                if (detail != null) detail.BandVsBands += bb;

                // Dimension lines never cross each other or any witness line (hard).
                if (an.DimLine.Crosses(pa.DimLine))
                {
                    hard += _cfg.CrossingWeight;
                    if (detail != null) detail.LineCrossesLine++;
                }
                foreach (var w in pa.Witnesses)
                    if (an.DimLine.Crosses(w))
                    {
                        hard += _cfg.CrossingWeight;
                        if (detail != null) detail.LineCrossesWitness++;
                    }
                foreach (var w in an.Witnesses)
                    if (pa.DimLine.Crosses(w))
                    {
                        hard += _cfg.CrossingWeight;
                        if (detail != null) detail.LineCrossesWitness++;
                    }

                // Value text is sacrosanct: no other text, line band, or witness over it.
                foreach (var tb in an.TextBoxes)
                {
                    double tband = tb.OverlapArea(pa.LineBand);
                    hard += tband * _cfg.OverlapWeight;
                    if (detail != null) detail.TextVsTextOrBand += tband;
                    foreach (var otb in pa.TextBoxes)
                    {
                        double tt = tb.OverlapArea(otb);
                        hard += tt * _cfg.OverlapWeight;
                        if (detail != null) detail.TextVsTextOrBand += tt;
                    }
                    foreach (var w in pa.Witnesses)
                        if (SegIntersectsBox(w, tb))
                        {
                            hard += _cfg.WitnessCrossWeight;
                            if (detail != null) detail.WitnessThroughText++;
                        }
                }
                foreach (var otb in pa.TextBoxes)
                {
                    double bt = otb.OverlapArea(an.LineBand);
                    hard += bt * _cfg.OverlapWeight;
                    if (detail != null) detail.TextVsTextOrBand += bt;
                    foreach (var w in an.Witnesses)
                        if (SegIntersectsBox(w, otb))
                        {
                            hard += _cfg.WitnessCrossWeight;
                            if (detail != null) detail.WitnessThroughText++;
                        }
                }

                // Leaders: never cross another leader (soft-high) nor lines (soft).
                foreach (var l in an.Leaders)
                {
                    foreach (var ol in pa.Leaders)
                        if (l.Crosses(ol))
                        {
                            soft += _cfg.LeaderCrossWeight;
                            if (detail != null) detail.LeaderCrossings++;
                        }
                    if (l.Crosses(pa.DimLine))
                    {
                        soft += _cfg.LeaderLineCrossWeight;
                        if (detail != null) detail.LeaderLineCrossings++;
                    }
                    foreach (var w in pa.Witnesses)
                        if (l.Crosses(w))
                        {
                            soft += _cfg.LeaderLineCrossWeight;
                            if (detail != null) detail.LeaderLineCrossings++;
                        }
                }
                foreach (var ol in pa.Leaders)
                {
                    if (ol.Crosses(an.DimLine))
                    {
                        soft += _cfg.LeaderLineCrossWeight;
                        if (detail != null) detail.LeaderLineCrossings++;
                    }
                    foreach (var w in an.Witnesses)
                        if (ol.Crosses(w))
                        {
                            soft += _cfg.LeaderLineCrossWeight;
                            if (detail != null) detail.LeaderLineCrossings++;
                        }
                }

                // Stagger stacked values: texts on adjacent rows shouldn't form a column.
                if (_cfg.StaggerStackedText)
                {
                    double sp = StaggerPenalty(an, pa, d, p);
                    soft += sp;
                    if (detail != null) detail.StaggerPenalty += sp;
                }
            }

            // ── HARD: off-crop ────────────────────────────────────────────────
            if (_crop.HasValue && !_crop.Value.Contains(d.PaperBounds))
            {
                hard += _cfg.OffCropWeight;
                if (detail != null) detail.OffCrop = true;
            }

            // ── SOFT: stay inside the cluster's working region ────────────────
            // Each cluster's strings/tags should fill the empty space the region carved out
            // for them (clashes → targets, ballooned to the neighbours) — spilling into the
            // next group's space costs per square foot.
            if (d.HasRegion)
            {
                double outside = Math.Max(0, an.LineBand.Area - an.LineBand.OverlapArea(d.Region));
                foreach (var tb in an.TextBoxes)
                    outside += Math.Max(0, tb.Area - tb.OverlapArea(d.Region));
                double rp = outside * _cfg.RegionWeight;
                soft += rp;
                if (detail != null) detail.RegionOverflow += rp;
            }

            // ── SOFT: cramped segment text / leadering ────────────────────────
            for (int i = 0; i < d.Segments.Count; i++)
            {
                var seg = d.Segments[i];
                if (seg.TextState == SegmentTextState.LeaderOut)
                {
                    soft += _cfg.LeaderWeight;          // leadered → no cramped penalty
                    if (detail != null) detail.CrampedPenalty += _cfg.LeaderWeight;
                    continue;
                }
                if (seg.IsCramped)
                {
                    double overflow = seg.TextWidthFt - seg.LengthFt;
                    // Moved tags (staggered above / flipped below) borrow neighbour space → half penalty.
                    bool moved = seg.TextState == SegmentTextState.Staggered
                              || seg.TextState == SegmentTextState.Flipped;
                    double factor = moved ? 0.5 : 1.0;
                    double cp = overflow * _cfg.CrampedWeight * factor;
                    soft += cp;
                    if (detail != null) detail.CrampedPenalty += cp;
                }
            }

            // ── SOFT: leader slack — keep moved tags near their segment ───────
            double minLeader = _cfg.TextHeightFt * 2.0;
            foreach (var l in an.Leaders)
            {
                double slack = Math.Max(0, l.Length - minLeader) * _cfg.LeaderSlackWeight;
                soft += slack;
                if (detail != null) detail.LeaderSlack += slack;
            }

            // ── SOFT: uneven spacing — penalise offsets off the first-offset/spacing cadence ──
            double dev = Math.Abs(d.OffsetFt - SnapToGrid(d.OffsetFt));
            soft += dev * _cfg.UnevenSpacingWeight;
            if (detail != null) detail.SpacingDeviation += dev * _cfg.UnevenSpacingWeight;

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
            // Pairwise terms counted twice above; halving is not necessary for convergence
            // (monotone), but keep totals comparable run-to-run.
            return new LayoutScore(hard, soft);
        }

        /// <summary>Names the hard constraints this dimension still violates — for the run
        /// report when a dense layout is unsatisfiable.</summary>
        public string DescribeHardViolations(
            PlannedDimension d, IReadOnlyList<Box2> obstacles, IReadOnlyList<PlannedDimension> others)
        {
            var reasons = new List<string>();
            var an = d.Anatomy;
            if (an == null) return "";

            for (int i = 0; i < obstacles.Count; i++)
            {
                if (!d.PaperBounds.Intersects(obstacles[i])) continue;
                if (an.LineBand.OverlapArea(obstacles[i]) > 0) { reasons.Add("line over existing annotation"); break; }
            }
            foreach (var p in others)
            {
                if (ReferenceEquals(p, d) || p.Anatomy == null) continue;
                var pa = p.Anatomy;
                if (!d.PaperBounds.Intersects(p.PaperBounds)) continue;
                if (an.LineBand.OverlapArea(pa.LineBand) > 0 && !reasons.Contains("string on string"))
                    reasons.Add("string on string");
                if (an.DimLine.Crosses(pa.DimLine) && !reasons.Contains("dimension lines cross"))
                    reasons.Add("dimension lines cross");
                foreach (var w in pa.Witnesses)
                    if (an.DimLine.Crosses(w)) { if (!reasons.Contains("line crosses a witness")) reasons.Add("line crosses a witness"); break; }
            }
            return string.Join(", ", reasons);
        }

        /// <summary>Soft penalty when two same-axis dimensions on adjacent rows have value
        /// texts that overlap along the axis — ASME's staggering convention: offset stacked
        /// values sideways so they don't pile into one unreadable column.</summary>
        private double StaggerPenalty(DimAnatomy an, DimAnatomy pa, PlannedDimension d, PlannedDimension p)
        {
            Vec2 da = d.AxisDir.Normalized();
            Vec2 pb = p.AxisDir.Normalized();
            if (Math.Abs(da.Dot(pb)) < 0.9) return 0;            // different axes — no stacking
            bool xAxis = Math.Abs(da.X) >= Math.Abs(da.Y);

            double lo = _cfg.StringSpacingFt * 0.25;             // same row → handled by overlap terms
            double hi = _cfg.StringSpacingFt * 1.75;             // beyond the adjacent row → unrelated
            double pen = 0;

            foreach (var tb in an.TextBoxes)
            {
                foreach (var ob in pa.TextBoxes)
                {
                    double alongOverlap = xAxis
                        ? Math.Min(tb.MaxX, ob.MaxX) - Math.Max(tb.MinX, ob.MinX)
                        : Math.Min(tb.MaxY, ob.MaxY) - Math.Max(tb.MinY, ob.MinY);
                    if (alongOverlap <= 0) continue;

                    double perpDist = xAxis
                        ? Math.Abs((tb.MinY + tb.MaxY) - (ob.MinY + ob.MaxY)) * 0.5
                        : Math.Abs((tb.MinX + tb.MaxX) - (ob.MinX + ob.MaxX)) * 0.5;
                    if (perpDist < lo || perpDist > hi) continue;

                    double minWidth = Math.Max(1e-9, Math.Min(
                        xAxis ? tb.Width : tb.Height, xAxis ? ob.Width : ob.Height));
                    pen += _cfg.StaggerWeight * Math.Min(1.0, alongOverlap / minWidth);
                }
            }
            return pen;
        }

        private double SnapToGrid(double offset)
        {
            if (_cfg.StringSpacingFt <= 1e-9) return _cfg.FirstOffsetFt;
            double n = (offset - _cfg.FirstOffsetFt) / _cfg.StringSpacingFt;
            double k = Math.Round(n);
            return _cfg.FirstOffsetFt + k * _cfg.StringSpacingFt;
        }

        /// <summary>True when the segment touches the box: an endpoint inside, or the segment
        /// crossing one of the box edges.</summary>
        private static bool SegIntersectsBox(Seg2 s, Box2 b)
        {
            if (Inside(s.A, b) || Inside(s.B, b)) return true;
            var c00 = new Vec2(b.MinX, b.MinY);
            var c10 = new Vec2(b.MaxX, b.MinY);
            var c11 = new Vec2(b.MaxX, b.MaxY);
            var c01 = new Vec2(b.MinX, b.MaxY);
            return s.Crosses(new Seg2(c00, c10)) || s.Crosses(new Seg2(c10, c11))
                || s.Crosses(new Seg2(c11, c01)) || s.Crosses(new Seg2(c01, c00));
        }

        private static bool Inside(Vec2 p, Box2 b) =>
            p.X > b.MinX && p.X < b.MaxX && p.Y > b.MinY && p.Y < b.MaxY;
    }
}
