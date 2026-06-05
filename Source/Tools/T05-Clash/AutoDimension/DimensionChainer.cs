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

        /// <summary>Identity of the physical run this clash belongs to (from
        /// <see cref="ClashRunGrouper"/>). Isolated clashes get a unique solo id.</summary>
        public string RunId { get; set; } = "";

        /// <summary>The run's principal (long) axis. Measurement axes parallel to this chain
        /// along the run; axes perpendicular to it collapse to one representative dimension.</summary>
        public Core.Vec2 RunLongAxis { get; set; } = new Core.Vec2(1, 0);
    }

    /// <summary>
    /// Turns resolved clash→target pairs into the planned dimensions the layout/commit consume,
    /// grouped by the physical run each clash belongs to (see <see cref="ClashRunGrouper"/>).
    /// For each run, a measurement axis <b>along</b> the run chains every member to one shared edge
    /// (sources + target as ordered references → a native Revit chained dimension); a measurement
    /// axis <b>across</b> the run collapses to a single representative dimension at the run's median
    /// offset, so slightly off-line members are absorbed rather than dimensioned on top of one
    /// another. An isolated clash (solo run) yields one plain two-reference dimension per axis.
    /// Deterministic: every grouping and ordering is sorted, never hash-ordered.
    /// </summary>
    public static class DimensionChainer
    {
        public sealed class Result
        {
            public List<Core.PlannedDimension> Dims { get; } = new List<Core.PlannedDimension>();
            public Dictionary<string, PlannedRefBundle> Refs { get; } = new Dictionary<string, PlannedRefBundle>();
        }

        public static Result Build(IReadOnlyList<ResolvedItem> items, Core.LayoutConfig cfg)
        {
            var result = new Result();
            if (items == null || items.Count == 0) return result;

            // Group by run, then by measurement axis. Each (run, axis) group becomes either a
            // chained string (axis runs along the run) or one representative dimension (axis runs
            // across the run). Deterministic ordering throughout.
            var byKey = items
                .GroupBy(it => (it.RunId, AxisTag(it.Axis)))
                .OrderBy(g => g.Key.Item1, StringComparer.Ordinal).ThenBy(g => g.Key.Item2, StringComparer.Ordinal);

            foreach (var g in byKey)
            {
                var group = g.ToList();
                Core.Vec2 axis = group[0].Axis.Normalized();
                Core.Vec2 perp = axis.Perp();
                Core.Vec2 longAxis = group[0].RunLongAxis.Normalized();

                // Along the run when the measurement axis is more parallel to the run's long axis
                // than to its cross axis; otherwise across.
                bool along = Math.Abs(axis.Dot(longAxis)) >= Math.Abs(axis.Dot(longAxis.Perp()));

                // One shared target for the whole group (majority vote) — adjacent members that the
                // resolver pinned to slightly different faces are pulled onto the same edge, which is
                // what lets near-coincident clashes collapse instead of stacking.
                var target = ChooseTarget(group);

                if (along && group.Count >= 2)
                {
                    EmitChain(group, axis, perp, target, cfg, result);
                }
                else
                {
                    // Across the run (or a solo member): one dimension whose value is the run's
                    // median distance along the measurement axis, anchored on the member closest to
                    // that median — so a clash a hair off the line never sets the dimension.
                    var rep = Representative(group, axis);
                    EmitSingle(rep, axis, target, cfg, result);
                }
            }

            return result;
        }

        /// <summary>The group's shared target: the most common <see cref="ResolvedItem.TargetKey"/>
        /// (ties broken lexicographically), returned as its first member's reference/point/key.</summary>
        private static (Reference Ref, Core.Vec2 Point, string Key) ChooseTarget(List<ResolvedItem> group)
        {
            var winner = group
                .GroupBy(it => it.TargetKey, StringComparer.Ordinal)
                .OrderByDescending(grp => grp.Count())
                .ThenBy(grp => grp.Key, StringComparer.Ordinal)
                .First()
                .OrderBy(it => it.SourceKey, StringComparer.Ordinal)
                .First();
            return (winner.TargetRef, winner.Target2d, winner.TargetKey);
        }

        /// <summary>The member whose coordinate along <paramref name="measureAxis"/> is closest to
        /// the group's median, so an off-line outlier never sets the dimension value. Ties broken by
        /// source key.</summary>
        private static ResolvedItem Representative(List<ResolvedItem> group, Core.Vec2 measureAxis)
        {
            var coords = group.Select(it => it.Source2d.Dot(measureAxis)).OrderBy(v => v).ToList();
            double median = coords[coords.Count / 2];
            return group
                .OrderBy(it => Math.Abs(it.Source2d.Dot(measureAxis) - median))
                .ThenBy(it => it.SourceKey, StringComparer.Ordinal)
                .First();
        }

        private static void EmitSingle(
            ResolvedItem it, Core.Vec2 axis, (Reference Ref, Core.Vec2 Point, string Key) target,
            Core.LayoutConfig cfg, Result result)
        {
            double srcA = it.Source2d.Dot(axis);
            double tgtA = target.Point.Dot(axis);
            double len  = Math.Abs(tgtA - srcA);

            string key = $"{it.SourceKey}|{AxisTag(axis)}";
            var dim = new Core.PlannedDimension
            {
                SourceKey   = key,
                TargetKey   = $"{target.Key}|{AxisTag(axis)}",
                TargetType  = it.TargetType,
                SourcePoint = it.Source2d,
                TargetPoint = target.Point,
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
            var ordered = new List<(Reference r, double a)> { (it.SourceRef, srcA), (target.Ref, tgtA) }
                .OrderBy(t => t.a).Select(t => t.r).ToList();
            result.Refs[key] = new PlannedRefBundle { Ordered = ordered };
        }

        private static void EmitChain(
            List<ResolvedItem> run, Core.Vec2 axis, Core.Vec2 perp,
            (Reference Ref, Core.Vec2 Point, string Key) target, Core.LayoutConfig cfg, Result result)
        {
            // The whole run shares one target line → constant target axial.
            double tgtA = target.Point.Dot(axis);
            Reference targetRef = target.Ref;
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

            if (deduped.Count < 2) { EmitSingle(run[0], axis, target, cfg, result); return; }

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
                TargetKey   = $"{target.Key}|chain|{AxisTag(axis)}",
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
