using System;
using System.Collections.Generic;
using System.Linq;
using LemoineTools.Tools.Clash.AutoDimension.Resolvers;
using Vec2 = LemoineTools.Tools.Clash.AutoDimension.Core.Vec2;

namespace LemoineTools.Tools.Clash.AutoDimension
{
    /// <summary>
    /// Groups clash source points into physical <i>runs</i> before any axis resolution.
    /// A run is a maximal set of clashes that travel together along one straight line — a pipe
    /// rack, a bank of penetrations — found by seeding on the nearest pair, fitting a principal
    /// axis, and growing only with points that stay on that line (perpendicular ≤ cross tolerance)
    /// and adjacent along it (gap ≤ run gap). One run per clash.
    ///
    /// The run gives the chainer a real notion of membership: dimensions measured <b>along</b> the
    /// run chain across every member to the edge; dimensions measured <b>across</b> the run collapse
    /// to a single representative (slightly off-line members are absorbed, not dimensioned twice).
    /// This replaces the old per-axis fixed-tolerance bucketing + post-hoc duplicate collapse, which
    /// both over-grouped (chaining unrelated clashes) and under-grouped (stacking near-coincident
    /// ones). Revit-free and deterministic — all ordering is by source key, never hash order.
    /// </summary>
    public static class ClashRunGrouper
    {
        /// <summary>Run identity + the principal (long) axis and its perpendicular (cross) axis.</summary>
        public sealed class RunInfo
        {
            public string RunId { get; set; } = "";
            public Vec2 LongAxis { get; set; } = new Vec2(1, 0);
            public Vec2 CrossAxis { get; set; } = new Vec2(0, 1);
        }

        /// <summary>
        /// Clusters <paramref name="sources"/> into runs. Tolerances are in feet (model space).
        /// Returns a map keyed by <see cref="SourceLine.SourceKey"/>; any source not present is an
        /// isolated run of one (the caller treats a missing key as a solo run).
        /// </summary>
        public static Dictionary<string, RunInfo> Build(
            IReadOnlyList<SourceLine> sources, double crossTolFt, double gapFt)
        {
            var map = new Dictionary<string, RunInfo>(StringComparer.Ordinal);
            if (sources == null || sources.Count == 0) return map;

            // Deterministic working set: (key, 2D anchor), ordered by source key.
            var pts = sources
                .Where(s => s != null && !string.IsNullOrEmpty(s.SourceKey))
                .OrderBy(s => s.SourceKey, StringComparer.Ordinal)
                .Select(s => (key: s.SourceKey, p: s.Anchor2d))
                .ToList();
            if (pts.Count == 0) return map;

            var used = new bool[pts.Count];
            int runSeq = 0;

            for (int i = 0; i < pts.Count; i++)
            {
                if (used[i]) continue;

                var members = new List<int> { i };
                used[i] = true;

                // Seed the run direction from the nearest unused neighbour within the gap.
                int seed2 = NearestWithin(pts, used, i, gapFt);
                Vec2 longAxis = new Vec2(1, 0);
                if (seed2 >= 0)
                {
                    used[seed2] = true;
                    members.Add(seed2);
                    Vec2 dir = (pts[seed2].p - pts[i].p).Normalized();
                    if (dir.Length > 1e-9) longAxis = dir;
                }

                // Grow: add the best collinear, adjacent, unused point until none qualifies.
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    Vec2 centroid = Centroid(pts, members);
                    if (members.Count >= 2) longAxis = PrincipalAxis(pts, members, centroid, longAxis);
                    Vec2 cross = longAxis.Perp();

                    double minA = double.MaxValue, maxA = double.MinValue;
                    foreach (var m in members)
                    {
                        double a = (pts[m].p - centroid).Dot(longAxis);
                        if (a < minA) minA = a;
                        if (a > maxA) maxA = a;
                    }

                    int best = -1;
                    double bestGap = double.MaxValue, bestPerp = double.MaxValue;
                    string bestKey = "";
                    for (int j = 0; j < pts.Count; j++)
                    {
                        if (used[j]) continue;
                        Vec2 d = pts[j].p - centroid;
                        double perp = Math.Abs(d.Dot(cross));
                        if (perp > crossTolFt) continue;
                        double a = d.Dot(longAxis);
                        double gap = a < minA ? (minA - a) : a > maxA ? (a - maxA) : 0.0;
                        if (gap > gapFt) continue;

                        // Prefer the smallest along-axis gap, then the smallest perpendicular
                        // deviation, then the lowest source key — fully deterministic.
                        bool better = gap < bestGap - 1e-12
                                   || (Math.Abs(gap - bestGap) <= 1e-12 && perp < bestPerp - 1e-12)
                                   || (Math.Abs(gap - bestGap) <= 1e-12 && Math.Abs(perp - bestPerp) <= 1e-12
                                       && string.CompareOrdinal(pts[j].key, bestKey) < 0);
                        if (best < 0 || better)
                        {
                            best = j; bestGap = gap; bestPerp = perp; bestKey = pts[j].key;
                        }
                    }

                    if (best >= 0)
                    {
                        used[best] = true;
                        members.Add(best);
                        grew = true;
                    }
                }

                var info = new RunInfo
                {
                    RunId = "run" + runSeq.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                    LongAxis = longAxis,
                    CrossAxis = longAxis.Perp(),
                };
                runSeq++;
                foreach (var m in members) map[pts[m].key] = info;
            }

            return map;
        }

        /// <summary>Index of the nearest unused point to <paramref name="from"/> within
        /// <paramref name="gapFt"/>, or -1. Ties broken by lowest source key.</summary>
        private static int NearestWithin(
            List<(string key, Vec2 p)> pts, bool[] used, int from, double gapFt)
        {
            int best = -1;
            double bestDist = double.MaxValue;
            for (int j = 0; j < pts.Count; j++)
            {
                if (used[j] || j == from) continue;
                double dist = (pts[j].p - pts[from].p).Length;
                if (dist > gapFt) continue;
                if (dist < bestDist - 1e-12
                    || (Math.Abs(dist - bestDist) <= 1e-12 && best >= 0
                        && string.CompareOrdinal(pts[j].key, pts[best].key) < 0))
                {
                    best = j; bestDist = dist;
                }
            }
            return best;
        }

        private static Vec2 Centroid(List<(string key, Vec2 p)> pts, List<int> members)
        {
            Vec2 sum = new Vec2(0, 0);
            foreach (var m in members) sum += pts[m].p;
            return members.Count == 0 ? sum : sum * (1.0 / members.Count);
        }

        /// <summary>Principal axis (largest-variance direction) of the member points via the
        /// closed-form 2×2 covariance eigenvector. Falls back to <paramref name="prev"/> when the
        /// spread is degenerate, and keeps the sign aligned with <paramref name="prev"/> so the axis
        /// never flips between grow iterations.</summary>
        private static Vec2 PrincipalAxis(
            List<(string key, Vec2 p)> pts, List<int> members, Vec2 centroid, Vec2 prev)
        {
            double sxx = 0, syy = 0, sxy = 0;
            foreach (var m in members)
            {
                Vec2 d = pts[m].p - centroid;
                sxx += d.X * d.X;
                syy += d.Y * d.Y;
                sxy += d.X * d.Y;
            }
            if (sxx + syy < 1e-12) return prev;

            double theta = 0.5 * Math.Atan2(2.0 * sxy, sxx - syy);
            Vec2 axis = new Vec2(Math.Cos(theta), Math.Sin(theta)).Normalized();
            if (axis.Length < 1e-9) return prev;
            if (axis.Dot(prev) < 0) axis = axis * -1.0;   // keep direction stable
            return axis;
        }
    }
}
