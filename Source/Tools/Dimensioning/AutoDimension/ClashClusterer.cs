using System;
using System.Collections.Generic;
using System.Linq;
using LemoineTools.Tools.Dimensioning.AutoDimension.Resolvers;
using Box2 = LemoineTools.Tools.Dimensioning.AutoDimension.Core.Box2;
using Vec2 = LemoineTools.Tools.Dimensioning.AutoDimension.Core.Vec2;

namespace LemoineTools.Tools.Dimensioning.AutoDimension
{
    /// <summary>
    /// Groups a view's clash sources into <i>clusters</i> — the unit the whole dimension pass
    /// works in. Clustering is single-link over PAPER-space proximity (the link distance is a
    /// sheet-inch setting × the view scale), so the same setting groups identically at 1:96 and
    /// inside a 1:16 callout. Every source belongs to exactly one cluster (singletons included).
    ///
    /// Each cluster also carries a <i>working region</i>: the tight box around its members
    /// (later grown to include their dimension targets), ballooned outward until it meets the
    /// neighbouring clusters' regions — every pair expands at the same rate, so the boundary
    /// lands halfway between them and each cluster gets an even share of the surrounding empty
    /// space to lay its dimensions out in. Revit-free and deterministic (ordering by source key).
    /// </summary>
    public static class ClashClusterer
    {
        /// <summary>One cluster: id, members, and its tight/ballooned boxes (view-2D, model ft).</summary>
        public sealed class Cluster
        {
            public string Id { get; set; } = "";
            public List<string> MemberKeys { get; } = new List<string>();
            public List<Vec2> MemberPoints { get; } = new List<Vec2>();

            /// <summary>Tight box around the member anchors (pre-balloon). Grown by the engine
            /// to include the members' resolved dimension targets before ballooning.</summary>
            public Box2 TightBox { get; set; }

            /// <summary>The working region after ballooning (see <see cref="GrowRegions"/>).</summary>
            public Box2 Region { get; set; }
        }

        public sealed class Result
        {
            /// <summary>sourceKey → cluster id, for every input source.</summary>
            public Dictionary<string, string> ClusterByKey { get; } =
                new Dictionary<string, string>(StringComparer.Ordinal);

            /// <summary>All clusters, ordered by id (deterministic).</summary>
            public List<Cluster> Clusters { get; } = new List<Cluster>();
        }

        /// <summary>Clusters <paramref name="sources"/> with single-link distance ≤
        /// <paramref name="linkFt"/> (paper-space link converted to model ft by the caller).</summary>
        public static Result Build(IReadOnlyList<SourceLine> sources, double linkFt)
        {
            var result = new Result();
            if (sources == null || sources.Count == 0) return result;

            var pts = sources
                .Where(s => s != null && !string.IsNullOrEmpty(s.SourceKey))
                .OrderBy(s => s.SourceKey, StringComparer.Ordinal)
                .Select(s => (key: s.SourceKey, p: s.Anchor2d))
                .ToList();
            if (pts.Count == 0) return result;

            // Single-link union-find over the ≤ linkFt graph. O(n²) distance checks — clash
            // counts per view are bounded (MaxClashes) and the check is cheap.
            var parent = new int[pts.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b); }

            double link2 = Math.Max(linkFt, 0) * Math.Max(linkFt, 0);
            for (int i = 0; i < pts.Count; i++)
                for (int j = i + 1; j < pts.Count; j++)
                {
                    Vec2 d = pts[j].p - pts[i].p;
                    if (d.X * d.X + d.Y * d.Y <= link2) Union(i, j);
                }

            // Deterministic cluster ids: ordered by the lowest member key (= lowest index,
            // since pts are key-sorted and Union roots at the smaller index).
            var members = new SortedDictionary<int, List<int>>();
            for (int i = 0; i < pts.Count; i++)
            {
                int root = Find(i);
                if (!members.TryGetValue(root, out var list)) members[root] = list = new List<int>();
                list.Add(i);
            }

            int seq = 0;
            foreach (var kv in members)
            {
                var cluster = new Cluster
                {
                    Id = "g" + seq.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
                };
                seq++;
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                foreach (var m in kv.Value)
                {
                    result.ClusterByKey[pts[m].key] = cluster.Id;
                    cluster.MemberKeys.Add(pts[m].key);
                    cluster.MemberPoints.Add(pts[m].p);
                    var p = pts[m].p;
                    if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
                }
                cluster.TightBox = new Box2(minX, minY, maxX, maxY);
                cluster.Region   = cluster.TightBox;
                result.Clusters.Add(cluster);
            }
            return result;
        }

        /// <summary>
        /// Balloons every cluster's tight box (already grown to its targets by the caller)
        /// outward until the regions touch: every box expands at the same rate, so two
        /// neighbours each claim half the gap between them — equal working space all round.
        /// A cluster with no near neighbour stops at <paramref name="maxPadFt"/>, so an
        /// isolated group still gets a bounded, generous canvas rather than the whole view.
        /// </summary>
        public static void GrowRegions(List<Cluster> clusters, double maxPadFt)
        {
            if (clusters == null || clusters.Count == 0) return;
            double maxPad = Math.Max(0, maxPadFt);

            for (int i = 0; i < clusters.Count; i++)
            {
                double pad = maxPad;
                for (int j = 0; j < clusters.Count; j++)
                {
                    if (j == i) continue;
                    // Uniform simultaneous expansion: the pair meets when each has grown by
                    // half the Chebyshev gap between their tight boxes.
                    double meet = clusters[i].TightBox.ChebyshevGap(clusters[j].TightBox) * 0.5;
                    if (meet < pad) pad = meet;
                }
                clusters[i].Region = clusters[i].TightBox.Expand(pad);
            }
        }
    }
}
