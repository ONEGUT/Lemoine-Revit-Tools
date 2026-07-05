using System;
using System.Collections.Generic;
using System.Linq;
using LemoineTools.PdfGeometry.Primitives;

namespace LemoineTools.PdfGeometry.Graph
{
    /// <summary>Result of reconciling one traced loop against the shared edge store.</summary>
    internal sealed class LoopComposition
    {
        public bool Success;
        public string? Warning;
        public List<EdgeRef> Refs = new List<EdgeRef>();

        /// <summary>Edges created during this reconcile — rolled back by the session when the loop is abandoned.</summary>
        public List<int> CreatedEdgeIds = new List<int>();
    }

    /// <summary>
    /// Matches a freshly traced boundary loop against the existing shared edge
    /// store and composes the loop out of (a) existing edges verbatim, (b) split
    /// portions of existing edges, and (c) new edges for the unmatched runs.
    /// This is what makes adjacent regions share identical geometry instead of
    /// two near-coincident traces.
    /// </summary>
    internal static class EdgeReconciler
    {
        private sealed class Sample
        {
            public double S;        // arc-length position on the ring
            public Pt2 P;
            public int EdgeId = -1; // matched edge, -1 = unmatched
            public double T;        // arc-length param on the matched edge
        }

        private sealed class Run
        {
            public int EdgeId;
            public double SFirst, SLast;   // ring params of first/last sample
            public double TFirst, TLast;   // edge params at first/last sample
            public double T0, T1;          // snapped min/max edge params
            public bool Forward;           // ring travels the edge first→last
            public List<EdgeRef> ChainRefs = new List<EdgeRef>();
            public Pt2 StartPt, EndPt;     // exact oriented junction points of the resolved chain
        }

        public static LoopComposition Reconcile(RegionSession session, List<Pt2> ring, double tol)
        {
            var comp = new LoopComposition();
            if (ring.Count < 3) { comp.Warning = "loop has fewer than 3 vertices"; return comp; }

            double ringLen = PolylineOps.RingLength(ring);
            var ringBounds = Bounds2.FromPoints(ring).Inflate(tol);
            var candidates = session.EdgesSnapshot()
                .Where(e => e.HasFreeSide && e.Bounds.Inflate(tol).Intersects(ringBounds))
                .ToList();

            if (candidates.Count == 0) return ComposeAsNewEdges(session, ring);

            double step = Math.Max(tol * 0.5, 0.25);
            var samples = BuildSamples(ring, step);
            MatchSamples(samples, candidates, tol);

            if (samples.All(s => s.EdgeId < 0)) return ComposeAsNewEdges(session, ring);

            // Rotate so index 0 sits on a match boundary; runs then never wrap.
            int n = samples.Count;
            int start = -1;
            for (int i = 0; i < n; i++)
                if (samples[i].EdgeId != samples[(i + n - 1) % n].EdgeId) { start = i; break; }
            if (start < 0)
            {
                // Every sample matched the same single edge — a ring coincides
                // entirely with one open edge, which has no valid composition.
                comp.Warning = "traced loop coincides entirely with one existing edge";
                return comp;
            }

            var runs = BuildRuns(samples, start, n, session, tol, step);
            if (runs.Count == 0) return ComposeAsNewEdges(session, ring);

            var pieceMap = SplitMatchedEdges(session, runs, tol);
            foreach (var run in runs)
                if (!ResolveChain(run, pieceMap, tol))
                {
                    comp.Warning = $"could not resolve matched portion of edge {run.EdgeId}";
                    return comp;
                }

            ComposeWithGaps(session, ring, ringLen, runs, comp);
            if (!comp.Success) return comp;

            if (!ValidateChain(session, comp.Refs))
            {
                comp.Success = false;
                comp.Warning = "composed loop is not contiguous";
                comp.Refs.Clear();
            }
            return comp;
        }

        /// <summary>
        /// Composes the ring purely from new edges (two halves) — used when no
        /// shared geometry matches, and as the session's fallback when a
        /// reconcile fails.
        /// </summary>
        public static LoopComposition ComposeAsNewEdges(RegionSession session, List<Pt2> ring)
        {
            var comp = new LoopComposition();
            double ringLen = PolylineOps.RingLength(ring);
            var open = new List<Pt2>(ring) { ring[0] };
            var half1 = PolylineOps.SubPolyline(open, 0, ringLen / 2);
            var half2 = PolylineOps.SubPolyline(open, ringLen / 2, ringLen);
            var e1 = session.AddEdge(half1, isSplitLine: false);
            var e2 = session.AddEdge(half2, isSplitLine: false);

            // AddEdge canonicalizes endpoints; re-assert the shared midpoint and
            // closure so the two halves chain exactly even after node snapping.
            comp.Refs.Add(new EdgeRef(e1.EdgeId, false));
            comp.Refs.Add(new EdgeRef(e2.EdgeId, false));
            comp.CreatedEdgeIds.Add(e1.EdgeId);
            comp.CreatedEdgeIds.Add(e2.EdgeId);
            comp.Success = ValidateChain(session, comp.Refs);
            if (!comp.Success) comp.Warning = "new-edge composition failed to close";
            return comp;
        }

        private static List<Sample> BuildSamples(List<Pt2> ring, double step)
        {
            var samples = new List<Sample>();
            double s = 0;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                Pt2 a = ring[i], b = ring[(i + 1) % n];
                double segLen = a.DistanceTo(b);
                samples.Add(new Sample { S = s, P = a });
                for (double d = step; d < segLen; d += step)
                    samples.Add(new Sample { S = s + d, P = Pt2.Lerp(a, b, d / segLen) });
                s += segLen;
            }
            return samples;
        }

        private static void MatchSamples(List<Sample> samples, List<SharedEdge> candidates, double tol)
        {
            foreach (var smp in samples)
            {
                double bestD = tol;
                foreach (var edge in candidates)
                {
                    if (!edge.Bounds.Inflate(tol).Contains(smp.P)) continue;
                    double t = PolylineOps.ClosestParam(edge.Polyline, smp.P, out double d);
                    if (d <= bestD)
                    {
                        bestD = d;
                        smp.EdgeId = edge.EdgeId;
                        smp.T = t;
                    }
                }
            }
        }

        private static List<Run> BuildRuns(List<Sample> samples, int start, int n,
                                           RegionSession session, double tol, double step)
        {
            var runs = new List<Run>();
            Run? cur = null;
            for (int k = 0; k < n; k++)
            {
                var smp = samples[(start + k) % n];
                if (smp.EdgeId < 0) { cur = null; continue; }
                if (cur != null && cur.EdgeId == smp.EdgeId)
                {
                    cur.SLast = smp.S;
                    cur.TLast = smp.T;
                }
                else
                {
                    cur = new Run
                    {
                        EdgeId = smp.EdgeId,
                        SFirst = smp.S, SLast = smp.S,
                        TFirst = smp.T, TLast = smp.T,
                    };
                    runs.Add(cur);
                }
            }

            // Drop touch-blips: runs that cover almost none of their edge are
            // corner grazes, not genuine shared boundary. A run spanning most of
            // a short edge stays, however short in absolute terms.
            double minRun = Math.Max(tol * 2, step * 3);
            runs.RemoveAll(r =>
            {
                var edge = session.GetEdge(r.EdgeId);
                double coverage = Math.Abs(r.TLast - r.TFirst);
                return coverage < Math.Min(minRun, edge.Length * 0.8);
            });

            double snapTol = tol * 2;
            foreach (var r in runs)
            {
                var edge = session.GetEdge(r.EdgeId);
                r.Forward = r.TLast >= r.TFirst;
                r.T0 = Math.Min(r.TFirst, r.TLast);
                r.T1 = Math.Max(r.TFirst, r.TLast);
                if (r.T0 <= snapTol) r.T0 = 0;
                if (r.T1 >= edge.Length - snapTol) r.T1 = edge.Length;
            }
            return runs;
        }

        /// <summary>
        /// Splits every matched edge at the interior junction params of its runs.
        /// Returns, per original edge id, the resulting pieces with their
        /// parent-param ranges (the unsplit edge itself when no split was needed).
        /// </summary>
        private static Dictionary<int, List<(SharedEdge edge, double t0, double t1)>> SplitMatchedEdges(
            RegionSession session, List<Run> runs, double tol)
        {
            var map = new Dictionary<int, List<(SharedEdge, double, double)>>();
            foreach (var grp in runs.GroupBy(r => r.EdgeId))
            {
                var edge = session.GetEdge(grp.Key);
                var interior = new List<double>();
                foreach (var r in grp)
                {
                    if (r.T0 > 0 && r.T0 < edge.Length) interior.Add(r.T0);
                    if (r.T1 > 0 && r.T1 < edge.Length) interior.Add(r.T1);
                }

                if (interior.Count == 0)
                {
                    map[grp.Key] = new List<(SharedEdge, double, double)> { (edge, 0, edge.Length) };
                    continue;
                }

                interior.Sort();
                var merged = new List<double>();
                foreach (var t in interior)
                    if (merged.Count == 0 || t - merged[merged.Count - 1] > tol * 0.5)
                        merged.Add(t);

                map[grp.Key] = session.SplitEdge(grp.Key, merged);
            }
            return map;
        }

        /// <summary>
        /// After splitting, every run maps onto a consecutive chain of whole
        /// edges, selected by parent-param range.
        /// </summary>
        private static bool ResolveChain(Run run,
            Dictionary<int, List<(SharedEdge edge, double t0, double t1)>> pieceMap, double tol)
        {
            if (!pieceMap.TryGetValue(run.EdgeId, out var pieces)) return false;
            double eps = tol * 0.5 + 1e-9;
            var chain = pieces.Where(p => p.t0 >= run.T0 - eps && p.t1 <= run.T1 + eps)
                              .OrderBy(p => p.t0)
                              .Select(p => p.edge)
                              .ToList();
            if (chain.Count == 0) return false;

            if (run.Forward)
            {
                foreach (var e in chain) run.ChainRefs.Add(new EdgeRef(e.EdgeId, false));
                run.StartPt = chain[0].Start;
                run.EndPt = chain[chain.Count - 1].End;
            }
            else
            {
                for (int i = chain.Count - 1; i >= 0; i--) run.ChainRefs.Add(new EdgeRef(chain[i].EdgeId, true));
                run.StartPt = chain[chain.Count - 1].End;
                run.EndPt = chain[0].Start;
            }
            return true;
        }

        private static void ComposeWithGaps(RegionSession session, List<Pt2> ring, double ringLen,
                                            List<Run> runs, LoopComposition comp)
        {
            for (int k = 0; k < runs.Count; k++)
            {
                Run cur = runs[k];
                Run next = runs[(k + 1) % runs.Count];
                comp.Refs.AddRange(cur.ChainRefs);

                Pt2 a = cur.EndPt, b = next.StartPt;
                if (a.DistanceTo(b) <= 1e-9 && runs.Count > 1) continue; // runs meet at a node

                double sA = cur.SLast;
                double sB = next.SFirst;
                if (sB <= sA) sB += ringLen; // gap crosses the ring's s=0 point

                var gap = SubRing(ring, ringLen, sA, sB);
                gap[0] = a;
                gap[gap.Count - 1] = b;
                gap = PolylineOps.Dedupe(gap, 1e-9, isRing: false);
                if (gap.Count < 2) gap = new List<Pt2> { a, b };

                var edge = session.AddEdge(gap, isSplitLine: false);
                comp.Refs.Add(new EdgeRef(edge.EdgeId, false));
                comp.CreatedEdgeIds.Add(edge.EdgeId);
            }
            comp.Success = comp.Refs.Count > 0;
        }

        /// <summary>Sub-polyline of a ring between arc-length params; s1 may wrap past the ring length.</summary>
        private static List<Pt2> SubRing(List<Pt2> ring, double ringLen, double s0, double s1)
        {
            var open = new List<Pt2>(ring) { ring[0] };
            if (s1 <= ringLen)
                return PolylineOps.SubPolyline(open, s0, s1);

            var part1 = PolylineOps.SubPolyline(open, s0, ringLen);
            var part2 = PolylineOps.SubPolyline(open, 0, s1 - ringLen);
            for (int i = 0; i < part2.Count; i++)
                if (i > 0 || part1[part1.Count - 1].DistanceTo(part2[i]) > 1e-9)
                    part1.Add(part2[i]);
            return part1;
        }

        private static bool ValidateChain(RegionSession session, List<EdgeRef> refs)
        {
            if (refs.Count == 0) return false;
            const double weld = 1e-6;
            for (int i = 0; i < refs.Count; i++)
            {
                var cur = refs[i];
                var nxt = refs[(i + 1) % refs.Count];
                var curEdge = session.GetEdge(cur.EdgeId);
                var nxtEdge = session.GetEdge(nxt.EdgeId);
                Pt2 curEnd = cur.Reversed ? curEdge.Start : curEdge.End;
                Pt2 nxtStart = nxt.Reversed ? nxtEdge.End : nxtEdge.Start;
                if (curEnd.DistanceTo(nxtStart) > weld) return false;
            }
            return true;
        }
    }
}
