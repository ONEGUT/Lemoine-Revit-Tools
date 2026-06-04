using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace LemoineTools.Tools.Clash.AutoDimension
{
    /// <summary>One resolved clash→target pair on a single axis, before chaining. Revit-aware
    /// (carries the references), so it lives here rather than in the Revit-free layout core.</summary>
    public sealed class ResolvedItem
    {
        public string SourceKey { get; set; } = "";
        public Reference SourceRef { get; set; } = null!;
        public Core.Vec2 Source2d { get; set; }
        public Core.Vec2 Axis { get; set; } = new Core.Vec2(1, 0);
        public Reference TargetRef { get; set; } = null!;
        public Core.Vec2 Target2d { get; set; }
        public string TargetKey { get; set; } = "";
        public Core.TargetType TargetType { get; set; }
    }

    /// <summary>
    /// Turns resolved clash→target pairs into the planned dimensions the layout/commit consume.
    /// When chaining is on, pairs that share an axis + the same target and sit on a common
    /// baseline (collinear within tolerance) and are adjacent along the axis are merged into one
    /// multi-segment string (sources + target as ordered references → a native Revit chained
    /// dimension). Otherwise every pair becomes a two-reference dimension. Deterministic: all
    /// grouping is sorted, never hash-ordered.
    /// </summary>
    public static class DimensionChainer
    {
        public sealed class Result
        {
            public List<Core.PlannedDimension> Dims { get; } = new List<Core.PlannedDimension>();
            public Dictionary<string, PlannedRefBundle> Refs { get; } = new Dictionary<string, PlannedRefBundle>();
        }

        public static Result Build(
            IReadOnlyList<ResolvedItem> items, Core.LayoutConfig cfg,
            bool chain, double maxGapFt, double collinearTolFt, double duplicateTolFt = 0.0)
        {
            var result = new Result();
            if (items == null || items.Count == 0) return result;

            // Group by axis, then exact target, then collinear baseline bucket. Each group's
            // members share a target line, so their target axial is constant; only their source
            // axials differ. Within a group, adjacent runs (gap < maxGap) chain.
            var byKey = items
                .GroupBy(it => (AxisTag(it.Axis), it.TargetKey))
                .OrderBy(g => g.Key.Item1).ThenBy(g => g.Key.Item2);

            foreach (var g in byKey)
            {
                Core.Vec2 axis = g.First().Axis.Normalized();
                Core.Vec2 perp = axis.Perp();

                // Bucket members onto common baselines (perp coordinate within tolerance).
                var members = g.OrderBy(it => it.Source2d.Dot(perp)).ToList();
                var buckets = new List<List<ResolvedItem>>();
                foreach (var it in members)
                {
                    double p = it.Source2d.Dot(perp);
                    var bucket = buckets.FirstOrDefault(b => Math.Abs(b[0].Source2d.Dot(perp) - p) <= collinearTolFt);
                    if (bucket == null) { bucket = new List<ResolvedItem>(); buckets.Add(bucket); }
                    bucket.Add(it);
                }

                foreach (var bucket in buckets)
                {
                    // Sort along the axis, split into adjacency runs.
                    var sorted = bucket.OrderBy(it => it.Source2d.Dot(axis)).ToList();
                    int i = 0;
                    while (i < sorted.Count)
                    {
                        int j = i + 1;
                        while (chain && j < sorted.Count
                               && (sorted[j].Source2d.Dot(axis) - sorted[j - 1].Source2d.Dot(axis)) <= maxGapFt)
                            j++;

                        var run = sorted.GetRange(i, j - i);
                        if (run.Count >= 2) EmitChain(run, axis, perp, cfg, result);
                        else                EmitSingle(run[0], axis, cfg, result);
                        i = j;
                    }
                }
            }

            if (duplicateTolFt > 0) CollapseDuplicates(result, duplicateTolFt);
            return result;
        }

        /// <summary>
        /// Drops dimensions that are visually identical to one already kept: same axis and the same
        /// ordered witness-line positions (within tolerance), regardless of how far apart the two sit
        /// perpendicular to the run. Three parallel 11'-6" dimensions to the same edge become one.
        /// Operates on the final dims so legitimate chained strings (which differ in witness layout)
        /// are preserved; only true parallel copies are removed. Deterministic — keeps the first.
        /// </summary>
        private static void CollapseDuplicates(Result result, double tolFt)
        {
            var kept = new List<Core.PlannedDimension>();
            var keptSigs = new List<(string axis, List<double> axials)>();

            foreach (var d in result.Dims)
            {
                string tag = AxisTag(d.AxisDir);
                List<double> axials = WitnessAxials(d);
                bool dup = false;
                foreach (var s in keptSigs)
                    if (s.axis == tag && SameLayout(s.axials, axials, tolFt)) { dup = true; break; }

                if (dup)
                {
                    result.Refs.Remove(d.SourceKey);   // drop its references too — it won't be placed
                    continue;
                }
                kept.Add(d);
                keptSigs.Add((tag, axials));
            }

            if (kept.Count != result.Dims.Count)
            {
                result.Dims.Clear();
                result.Dims.AddRange(kept);
            }
        }

        /// <summary>Ordered along-axis witness positions of a dimension, measured from its near end.
        /// Works for both singles (two positions) and chained strings (one per reference).</summary>
        private static List<double> WitnessAxials(Core.PlannedDimension d)
        {
            Core.Vec2 axis = d.AxisDir.Normalized();
            double minA = Math.Min(d.SourcePoint.Dot(axis), d.TargetPoint.Dot(axis));
            var list = new List<double> { minA };
            double acc = minA;
            foreach (var seg in d.Segments) { acc += seg.LengthFt; list.Add(acc); }
            return list;
        }

        private static bool SameLayout(List<double> a, List<double> b, double tolFt)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (Math.Abs(a[i] - b[i]) > tolFt) return false;
            return true;
        }

        private static void EmitSingle(ResolvedItem it, Core.Vec2 axis, Core.LayoutConfig cfg, Result result)
        {
            double srcA = it.Source2d.Dot(axis);
            double tgtA = it.Target2d.Dot(axis);
            double len  = Math.Abs(tgtA - srcA);

            string key = $"{it.SourceKey}|{AxisTag(axis)}";
            var dim = new Core.PlannedDimension
            {
                SourceKey   = key,
                TargetKey   = $"{it.TargetKey}|{AxisTag(axis)}",
                TargetType  = it.TargetType,
                SourcePoint = it.Source2d,
                TargetPoint = it.Target2d,
                AxisDir     = axis,
                Side        = Core.DimSide.Positive,
                OffsetFt    = cfg.FirstOffsetFt,
                Segments    = new List<Core.PlannedSegment>
                {
                    new Core.PlannedSegment { LengthFt = len, TextWidthFt = EstimateTextWidth(len, cfg.TextHeightFt) },
                },
            };
            result.Dims.Add(dim);

            // Reference order along the line: source then target by axial.
            var ordered = new List<(Reference r, double a)> { (it.SourceRef, srcA), (it.TargetRef, tgtA) }
                .OrderBy(t => t.a).Select(t => t.r).ToList();
            result.Refs[key] = new PlannedRefBundle { Ordered = ordered };
        }

        private static void EmitChain(
            List<ResolvedItem> run, Core.Vec2 axis, Core.Vec2 perp, Core.LayoutConfig cfg, Result result)
        {
            // All members share the same target line → constant target axial; use the first's ref.
            double tgtA = run[0].Target2d.Dot(axis);
            Reference targetRef = run[0].TargetRef;
            double basePerp = run.Average(it => it.Source2d.Dot(perp));

            // Merge member sources + the single target, sorted along the axis, de-duping coincident
            // axial positions (a zero-length segment would be rejected by Revit).
            var refsByAxial = new List<(Reference r, double a)>();
            foreach (var it in run) refsByAxial.Add((it.SourceRef, it.Source2d.Dot(axis)));
            refsByAxial.Add((targetRef, tgtA));

            var deduped = new List<(Reference r, double a)>();
            foreach (var ra in refsByAxial.OrderBy(t => t.a))
                if (deduped.Count == 0 || Math.Abs(ra.a - deduped[deduped.Count - 1].a) > cfg.PrecisionFt)
                    deduped.Add(ra);

            if (deduped.Count < 2) { EmitSingle(run[0], axis, cfg, result); return; }

            double minA = deduped[0].a, maxA = deduped[deduped.Count - 1].a;
            Core.Vec2 sp = axis * minA + perp * basePerp;
            Core.Vec2 tp = axis * maxA + perp * basePerp;

            var segments = new List<Core.PlannedSegment>();
            for (int k = 1; k < deduped.Count; k++)
            {
                double len = deduped[k].a - deduped[k - 1].a;
                segments.Add(new Core.PlannedSegment { LengthFt = len, TextWidthFt = EstimateTextWidth(len, cfg.TextHeightFt) });
            }

            string key = "chain|" + AxisTag(axis) + "|" +
                         string.Join("+", run.Select(it => it.SourceKey).OrderBy(s => s, StringComparer.Ordinal));

            result.Dims.Add(new Core.PlannedDimension
            {
                SourceKey   = key,
                TargetKey   = $"{run[0].TargetKey}|chain|{AxisTag(axis)}",
                TargetType  = run[0].TargetType,
                SourcePoint = sp,
                TargetPoint = tp,
                AxisDir     = axis,
                Side        = Core.DimSide.Positive,
                OffsetFt    = cfg.FirstOffsetFt,
                Segments    = segments,
            });
            result.Refs[key] = new PlannedRefBundle { Ordered = deduped.Select(t => t.r).ToList() };
        }

        private static string AxisTag(Core.Vec2 axis) => Math.Abs(axis.X) >= Math.Abs(axis.Y) ? "x" : "y";

        /// <summary>Rough value-string width: char count × ~0.6× glyph height. Shared with the engine.</summary>
        internal static double EstimateTextWidth(double valueFt, double textHeightModelFt)
        {
            string s = valueFt.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "'";
            int chars = Math.Max(3, s.Length);
            return chars * textHeightModelFt * 0.6;
        }
    }
}
