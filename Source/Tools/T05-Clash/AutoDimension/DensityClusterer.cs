using System;
using System.Collections.Generic;
using System.Linq;
using LemoineTools.Tools.Clash.AutoDimension.Resolvers;
using Vec2 = LemoineTools.Tools.Clash.AutoDimension.Core.Vec2;

namespace LemoineTools.Tools.Clash.AutoDimension
{
    /// <summary>
    /// Finds <i>oversaturated</i> areas — pockets of clashes packed tighter than their value
    /// texts can ever fit individually — so the chainer can collapse each pocket into one
    /// chained string per axis (split by nearest reference) instead of a forest of solo
    /// dimensions whose witness lines cross everything.
    ///
    /// A pocket is a connected cluster under single-link distance ≤ the nominal value-text
    /// width: if each member sits within one text-width of the next, their texts cannot sit
    /// side by side, so independent strings are unplaceable by construction. Clusters smaller
    /// than the member threshold are left to the normal run grouping.
    /// Revit-free and deterministic (all ordering by source key).
    /// </summary>
    public static class DensityClusterer
    {
        public sealed class Result
        {
            /// <summary>sourceKey → dense-cluster id, only for members of oversaturated clusters.</summary>
            public Dictionary<string, string> ClusterByKey { get; } =
                new Dictionary<string, string>(StringComparer.Ordinal);

            /// <summary>Count of oversaturated clusters found.</summary>
            public int ClusterCount { get; set; }

            /// <summary>One log line per cluster: member count + extent, for the run log.</summary>
            public List<string> Summaries { get; } = new List<string>();
        }

        /// <summary>
        /// Clusters <paramref name="sources"/> with single-link distance ≤ <paramref name="linkFt"/>
        /// (the nominal text width, model ft); clusters of ≥ <paramref name="minCount"/> members
        /// are flagged dense.
        /// </summary>
        public static Result Build(IReadOnlyList<SourceLine> sources, double linkFt, int minCount)
        {
            var result = new Result();
            if (sources == null || sources.Count == 0 || linkFt <= 1e-9 || minCount < 2) return result;

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

            double link2 = linkFt * linkFt;
            for (int i = 0; i < pts.Count; i++)
            {
                for (int j = i + 1; j < pts.Count; j++)
                {
                    Vec2 d = pts[j].p - pts[i].p;
                    if (d.X * d.X + d.Y * d.Y <= link2) Union(i, j);
                }
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
                if (kv.Value.Count < minCount) continue;
                string id = "c" + seq.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
                seq++;
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (var m in kv.Value)
                {
                    result.ClusterByKey[pts[m].key] = id;
                    var p = pts[m].p;
                    if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
                }
                result.Summaries.Add(
                    $"Dense area {id}: {kv.Value.Count} clash(es) within {maxX - minX:0.#}×{maxY - minY:0.#} ft "
                  + "— chained per axis, split by nearest reference.");
            }
            result.ClusterCount = seq;
            return result;
        }
    }
}
