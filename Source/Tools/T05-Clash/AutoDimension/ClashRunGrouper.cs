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
    /// rack, a bank of penetrations.
    ///
    /// Clustering is agglomerative best-fit: every clash starts as its own cluster and the
    /// best-fitting qualifying pair of clusters is merged until none qualifies. A merge
    /// qualifies when the merged set still fits one line — every member within the cross
    /// tolerance of the merged principal axis AND every along-axis gap between adjacent
    /// members within the run gap. "Best" is the lowest worst-case perpendicular residual.
    /// This replaces the previous seed-and-grow pass, which was order-dependent (clashes were
    /// claimed first-come-first-served in ElementId order) and seed-axis-sensitive (a sideways
    /// nearest neighbour pointed the run the wrong way and the true members then failed the
    /// cross test). Best-fit merging also reunifies split half-runs and attaches solo clashes
    /// to a nearby run's line — no separate repair passes needed.
    ///
    /// Near-miss merges (pairs that failed a tolerance by less than 2×) are reported so the
    /// gap / cross-tolerance knobs can be tuned from the run log instead of guessed.
    /// Revit-free and deterministic — all ordering is by source key, never hash order.
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

        /// <summary>Grouping output: the per-clash run map plus tuning diagnostics.</summary>
        public sealed class GroupResult
        {
            /// <summary>Keyed by <see cref="SourceLine.SourceKey"/>; a missing key is a solo run.</summary>
            public Dictionary<string, RunInfo> Map { get; } = new Dictionary<string, RunInfo>(StringComparer.Ordinal);

            /// <summary>Distinct runs (including solos) over the input set.</summary>
            public int RunCount { get; set; }

            /// <summary>Human-readable near-miss diagnostics: cluster pairs that failed a merge
            /// tolerance by less than 2×, with the offending distance. Capped; for the run log.</summary>
            public List<string> NearMisses { get; } = new List<string>();
        }

        /// <summary>Max near-miss lines reported (keeps the run log readable).</summary>
        private const int MaxNearMisses = 12;

        /// <summary>
        /// Clusters <paramref name="sources"/> into runs. Tolerances are in feet (model space):
        /// <paramref name="crossTolFt"/> is how far a member may sit off the run's fitted line,
        /// <paramref name="gapFt"/> the max along-line gap between adjacent members.
        /// </summary>
        public static GroupResult Build(
            IReadOnlyList<SourceLine> sources, double crossTolFt, double gapFt)
        {
            var result = new GroupResult();
            if (sources == null || sources.Count == 0) return result;

            // Deterministic working set: (key, 2D anchor), ordered by source key.
            var pts = sources
                .Where(s => s != null && !string.IsNullOrEmpty(s.SourceKey))
                .OrderBy(s => s.SourceKey, StringComparer.Ordinal)
                .Select(s => (key: s.SourceKey, p: s.Anchor2d))
                .ToList();
            if (pts.Count == 0) return result;

            // Two members that belong to one line are at most this far apart in the plane
            // (gap along the axis, up to one cross tolerance off the line on each side) —
            // box-distance pruning radius for candidate cluster pairs.
            double prune = Math.Sqrt(gapFt * gapFt + 4.0 * crossTolFt * crossTolFt) + 1e-9;

            var clusters = new List<Cluster>();
            for (int i = 0; i < pts.Count; i++)
                clusters.Add(Cluster.Solo(i, pts[i].key, pts[i].p));

            // Fit cache keyed by immutable cluster ids — a merge mints a new id, so stale
            // entries are simply never looked up again.
            var fitCache = new Dictionary<(int, int), Fit>();
            int nextId = clusters.Count;

            while (clusters.Count > 1 && gapFt > 1e-9 && crossTolFt > 1e-9)
            {
                int bestA = -1, bestB = -1;
                Fit bestFit = default;

                for (int a = 0; a < clusters.Count; a++)
                {
                    for (int b = a + 1; b < clusters.Count; b++)
                    {
                        var ca = clusters[a];
                        var cb = clusters[b];
                        if (BoxGap(ca, cb) > prune) continue;

                        var fit = GetFit(fitCache, ca, cb, pts);
                        if (fit.MaxPerp > crossTolFt || fit.MaxAdjGap > gapFt) continue;

                        // Lower worst-case off-line residual wins; ties by lowest key pair —
                        // fully deterministic.
                        bool better = bestA < 0
                                   || fit.MaxPerp < bestFit.MaxPerp - 1e-12
                                   || (Math.Abs(fit.MaxPerp - bestFit.MaxPerp) <= 1e-12
                                       && KeyPairLess(ca, cb, clusters[bestA], clusters[bestB]));
                        if (better) { bestA = a; bestB = b; bestFit = fit; }
                    }
                }

                if (bestA < 0) break;
                var merged = Cluster.Merge(clusters[bestA], clusters[bestB], bestFit, nextId++);
                clusters.RemoveAt(bestB);   // bestB > bestA — remove the later index first
                clusters.RemoveAt(bestA);
                clusters.Add(merged);
                clusters.Sort((x, y) => string.CompareOrdinal(x.KeyLow, y.KeyLow));
            }

            CollectNearMisses(result, clusters, fitCache, pts, crossTolFt, gapFt, prune);

            // Run ids in lowest-member-key order — deterministic across runs.
            clusters.Sort((x, y) => string.CompareOrdinal(x.KeyLow, y.KeyLow));
            int runSeq = 0;
            foreach (var c in clusters)
            {
                var info = new RunInfo
                {
                    RunId     = "run" + runSeq.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                    LongAxis  = c.Axis,
                    CrossAxis = c.Axis.Perp(),
                };
                runSeq++;
                foreach (var m in c.Members) result.Map[pts[m].key] = info;
            }
            result.RunCount = clusters.Count;
            return result;
        }

        // ── Cluster bookkeeping ────────────────────────────────────────────────

        private sealed class Cluster
        {
            public int Id;                       // immutable per cluster generation (fit-cache key)
            public List<int> Members = new List<int>();
            public string KeyLow = "";           // lowest member source key (identity/tie-break)
            public Vec2 Axis = new Vec2(1, 0);   // current fitted long axis (canonical sign)
            public double MinX, MinY, MaxX, MaxY;

            public static Cluster Solo(int idx, string key, Vec2 p) => new Cluster
            {
                Id = idx,
                Members = { idx },
                KeyLow = key,
                MinX = p.X, MaxX = p.X, MinY = p.Y, MaxY = p.Y,
            };

            public static Cluster Merge(Cluster a, Cluster b, Fit fit, int id)
            {
                var c = new Cluster
                {
                    Id      = id,
                    Members = a.Members.Concat(b.Members).OrderBy(i => i).ToList(),
                    KeyLow  = string.CompareOrdinal(a.KeyLow, b.KeyLow) <= 0 ? a.KeyLow : b.KeyLow,
                    Axis    = fit.Axis,
                    MinX    = Math.Min(a.MinX, b.MinX), MaxX = Math.Max(a.MaxX, b.MaxX),
                    MinY    = Math.Min(a.MinY, b.MinY), MaxY = Math.Max(a.MaxY, b.MaxY),
                };
                return c;
            }
        }

        /// <summary>Merged-line fit of two clusters' combined members.</summary>
        private readonly struct Fit
        {
            public readonly Vec2 Axis;
            public readonly double MaxPerp;     // worst member distance off the fitted line
            public readonly double MaxAdjGap;   // worst along-axis gap between adjacent members
            public Fit(Vec2 axis, double maxPerp, double maxAdjGap)
            { Axis = axis; MaxPerp = maxPerp; MaxAdjGap = maxAdjGap; }
        }

        /// <summary>Planar gap between two clusters' bounding boxes (0 when they touch/overlap).
        /// A lower bound on the true min member distance — safe for pruning.</summary>
        private static double BoxGap(Cluster a, Cluster b)
        {
            double dx = Math.Max(0, Math.Max(a.MinX - b.MaxX, b.MinX - a.MaxX));
            double dy = Math.Max(0, Math.Max(a.MinY - b.MaxY, b.MinY - a.MaxY));
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool KeyPairLess(Cluster a1, Cluster b1, Cluster a2, Cluster b2)
        {
            string lo1 = a1.KeyLow, hi1 = b1.KeyLow;
            if (string.CompareOrdinal(lo1, hi1) > 0) { var t = lo1; lo1 = hi1; hi1 = t; }
            string lo2 = a2.KeyLow, hi2 = b2.KeyLow;
            if (string.CompareOrdinal(lo2, hi2) > 0) { var t = lo2; lo2 = hi2; hi2 = t; }
            int c = string.CompareOrdinal(lo1, lo2);
            if (c != 0) return c < 0;
            return string.CompareOrdinal(hi1, hi2) < 0;
        }

        private static Fit GetFit(
            Dictionary<(int, int), Fit> cache, Cluster a, Cluster b,
            List<(string key, Vec2 p)> pts)
        {
            var key = a.Id <= b.Id ? (a.Id, b.Id) : (b.Id, a.Id);
            if (cache.TryGetValue(key, out var hit)) return hit;
            var fit = FitLine(a, b, pts);
            cache[key] = fit;
            return fit;
        }

        /// <summary>Fits one line through both clusters' members: principal axis through the
        /// centroid, worst perpendicular residual, and worst adjacent along-axis gap.</summary>
        private static Fit FitLine(Cluster a, Cluster b, List<(string key, Vec2 p)> pts)
        {
            int n = a.Members.Count + b.Members.Count;
            var members = new int[n];
            a.Members.CopyTo(members, 0);
            b.Members.CopyTo(members, a.Members.Count);

            Vec2 sum = new Vec2(0, 0);
            foreach (var m in members) sum += pts[m].p;
            Vec2 centroid = sum * (1.0 / n);

            Vec2 axis = PrincipalAxis(members, pts, centroid);

            double maxPerp = 0;
            var along = new double[n];
            Vec2 cross = axis.Perp();
            for (int i = 0; i < n; i++)
            {
                Vec2 d = pts[members[i]].p - centroid;
                double perp = Math.Abs(d.Dot(cross));
                if (perp > maxPerp) maxPerp = perp;
                along[i] = d.Dot(axis);
            }

            Array.Sort(along);
            double maxGap = 0;
            for (int i = 1; i < n; i++)
            {
                double g = along[i] - along[i - 1];
                if (g > maxGap) maxGap = g;
            }

            return new Fit(axis, maxPerp, maxGap);
        }

        /// <summary>Principal axis (largest-variance direction) via the closed-form 2×2
        /// covariance eigenvector, with a canonical sign (+X half-plane) so identical point
        /// sets always yield the identical axis.</summary>
        private static Vec2 PrincipalAxis(int[] members, List<(string key, Vec2 p)> pts, Vec2 centroid)
        {
            double sxx = 0, syy = 0, sxy = 0;
            foreach (var m in members)
            {
                Vec2 d = pts[m].p - centroid;
                sxx += d.X * d.X;
                syy += d.Y * d.Y;
                sxy += d.X * d.Y;
            }
            if (sxx + syy < 1e-12) return new Vec2(1, 0);   // coincident points — any axis works

            double theta = 0.5 * Math.Atan2(2.0 * sxy, sxx - syy);
            Vec2 axis = new Vec2(Math.Cos(theta), Math.Sin(theta)).Normalized();
            if (axis.Length < 1e-9) return new Vec2(1, 0);
            return Canonical(axis);
        }

        /// <summary>Flips the axis into the +X half-plane (ties to +Y) so the sign is stable.</summary>
        private static Vec2 Canonical(Vec2 axis)
        {
            if (axis.X < -1e-9 || (Math.Abs(axis.X) <= 1e-9 && axis.Y < 0)) return axis * -1.0;
            return axis;
        }

        /// <summary>
        /// Reports cluster pairs that *almost* merged: both tolerances within 2× their limit but
        /// at least one exceeded. Sorted by how close they came (smallest worst exceedance first)
        /// and capped at <see cref="MaxNearMisses"/>, so the log teaches what the knobs would
        /// need to be for the grouping the user expected.
        /// </summary>
        private static void CollectNearMisses(
            GroupResult result, List<Cluster> clusters, Dictionary<(int, int), Fit> fitCache,
            List<(string key, Vec2 p)> pts, double crossTolFt, double gapFt, double prune)
        {
            if (gapFt <= 1e-9 || crossTolFt <= 1e-9) return;

            var misses = new List<(double ratio, string text)>();
            for (int a = 0; a < clusters.Count; a++)
            {
                for (int b = a + 1; b < clusters.Count; b++)
                {
                    var ca = clusters[a];
                    var cb = clusters[b];
                    if (BoxGap(ca, cb) > prune * 2.0) continue;

                    var fit = GetFit(fitCache, ca, cb, pts);
                    double gapRatio  = fit.MaxAdjGap / gapFt;
                    double perpRatio = fit.MaxPerp / crossTolFt;
                    if (gapRatio <= 1.0 && perpRatio <= 1.0) continue;   // unreachable post-loop, but cheap to guard
                    if (gapRatio > 2.0 || perpRatio > 2.0) continue;     // not "near" — skip

                    var reasons = new List<string>();
                    if (gapRatio > 1.0)
                        reasons.Add($"along gap {fit.MaxAdjGap:0.0} ft > {gapFt:0.0} ft");
                    if (perpRatio > 1.0)
                        reasons.Add($"off-line {fit.MaxPerp:0.00} ft > {crossTolFt:0.00} ft");

                    string label =
                        $"Run grouping near-miss: clash {ca.KeyLow} ({ca.Members.Count}) ↔ " +
                        $"clash {cb.KeyLow} ({cb.Members.Count}) — {string.Join(", ", reasons)}.";
                    misses.Add((Math.Max(gapRatio, perpRatio), label));
                }
            }

            foreach (var m in misses
                         .OrderBy(m => m.ratio)
                         .ThenBy(m => m.text, StringComparer.Ordinal)
                         .Take(MaxNearMisses))
                result.NearMisses.Add(m.text);
        }
    }
}
