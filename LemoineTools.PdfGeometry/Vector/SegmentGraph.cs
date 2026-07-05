using System;
using System.Collections.Generic;
using System.Linq;
using LemoineTools.PdfGeometry.Primitives;

namespace LemoineTools.PdfGeometry.Vector
{
    /// <summary>
    /// Planar arrangement over a set of line segments (flattened PDF paths +
    /// user split lines): segments are split at mutual intersections, endpoints
    /// clustered within a join tolerance (closing small drafting gaps), and the
    /// resulting graph's faces extracted via half-edge traversal.
    /// </summary>
    public sealed class SegmentGraph
    {
        public sealed class FaceCycle
        {
            /// <summary>Node positions around the cycle (no repeated closing vertex).</summary>
            public List<Pt2> Ring = new List<Pt2>();

            /// <summary>Shoelace area: positive = CCW = bounded face interior; negative = island/outer boundary cycle.</summary>
            public double SignedArea;
        }

        private readonly List<Pt2> _nodes = new List<Pt2>();
        private readonly List<(int a, int b)> _edges = new List<(int, int)>();

        public IReadOnlyList<FaceCycle> Cycles { get; private set; } = new List<FaceCycle>();

        public int NodeCount => _nodes.Count;
        public int EdgeCount => _edges.Count;

        public static SegmentGraph Build(IReadOnlyList<Seg2> segments, double joinTol)
        {
            var graph = new SegmentGraph();
            var pieces = SplitAtIntersections(segments, joinTol);
            graph.ClusterAndIndex(pieces, joinTol);
            graph.Cycles = graph.ExtractCycles();
            return graph;
        }

        // ----------------------------------------------------- arrangement build

        private static List<Seg2> SplitAtIntersections(IReadOnlyList<Seg2> segments, double joinTol)
        {
            int n = segments.Count;
            var cuts = new List<double>[n];
            for (int i = 0; i < n; i++) cuts[i] = new List<double>();

            // O(n²) with bbox prefilter — acceptable at drawing-sheet segment counts.
            var bounds = segments.Select(s => s.Bounds.Inflate(joinTol)).ToArray();
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (!bounds[i].Intersects(bounds[j])) continue;
                    if (Seg2.Intersect(segments[i], segments[j], joinTol * 0.5, out _, out double t1, out double t2))
                    {
                        if (t1 > 1e-9 && t1 < 1 - 1e-9) cuts[i].Add(t1);
                        if (t2 > 1e-9 && t2 < 1 - 1e-9) cuts[j].Add(t2);
                    }
                }
            }

            var pieces = new List<Seg2>(n * 2);
            for (int i = 0; i < n; i++)
            {
                var seg = segments[i];
                if (cuts[i].Count == 0)
                {
                    pieces.Add(seg);
                    continue;
                }
                cuts[i].Sort();
                double prev = 0;
                foreach (var t in cuts[i])
                {
                    if (t - prev > 1e-9)
                        pieces.Add(new Seg2(Pt2.Lerp(seg.A, seg.B, prev), Pt2.Lerp(seg.A, seg.B, t)));
                    prev = t;
                }
                if (1 - prev > 1e-9)
                    pieces.Add(new Seg2(Pt2.Lerp(seg.A, seg.B, prev), seg.B));
            }
            return pieces;
        }

        private void ClusterAndIndex(List<Seg2> pieces, double joinTol)
        {
            // Endpoint clustering by spatial hash: an endpoint joins the first
            // node found within joinTol (deterministic in input order).
            var cellMap = new Dictionary<(int, int), List<int>>();
            double cell = Math.Max(joinTol, 1e-6);

            int NodeFor(Pt2 p)
            {
                int cx = (int)Math.Floor(p.X / cell), cy = (int)Math.Floor(p.Y / cell);
                for (int oy = -1; oy <= 1; oy++)
                    for (int ox = -1; ox <= 1; ox++)
                        if (cellMap.TryGetValue((cx + ox, cy + oy), out var ids))
                            foreach (var id in ids)
                                if (_nodes[id].DistanceTo(p) <= joinTol)
                                    return id;
                _nodes.Add(p);
                int newId = _nodes.Count - 1;
                if (!cellMap.TryGetValue((cx, cy), out var list))
                    cellMap[(cx, cy)] = list = new List<int>();
                list.Add(newId);
                return newId;
            }

            var seen = new HashSet<(int, int)>();
            foreach (var piece in pieces)
            {
                int a = NodeFor(piece.A);
                int b = NodeFor(piece.B);
                if (a == b) continue; // collapsed by clustering
                var key = a < b ? (a, b) : (b, a);
                if (seen.Add(key)) _edges.Add(key);
            }
        }

        // -------------------------------------------------------- face traversal

        /// <summary>
        /// Standard planar face extraction. Half-edge h = 2e (a→b) or 2e+1 (b→a).
        /// next(h) at the head node v is the outgoing half-edge that is the
        /// clockwise-next after h's twin in v's CCW-sorted star — which walks
        /// every face cycle with the interior on the left, so bounded faces come
        /// out CCW (positive area) and island/outer boundaries CW (negative).
        /// </summary>
        private List<FaceCycle> ExtractCycles()
        {
            int he = _edges.Count * 2;
            var star = new List<int>[_nodes.Count]; // outgoing half-edge ids per node, CCW by angle
            for (int v = 0; v < _nodes.Count; v++) star[v] = new List<int>();
            for (int e = 0; e < _edges.Count; e++)
            {
                star[_edges[e].a].Add(2 * e);
                star[_edges[e].b].Add(2 * e + 1);
            }
            for (int v = 0; v < _nodes.Count; v++)
            {
                var origin = _nodes[v];
                star[v].Sort((h1, h2) =>
                    (_nodes[Head(h1)] - origin).Angle.CompareTo((_nodes[Head(h2)] - origin).Angle));
            }

            var starIndex = new Dictionary<int, int>(he);
            for (int v = 0; v < _nodes.Count; v++)
                for (int i = 0; i < star[v].Count; i++)
                    starIndex[star[v][i]] = i;

            var cycles = new List<FaceCycle>();
            var visited = new bool[he];
            for (int h0 = 0; h0 < he; h0++)
            {
                if (visited[h0]) continue;
                var cycleNodes = new List<int>();
                int h = h0;
                int guard = 0;
                while (!visited[h])
                {
                    visited[h] = true;
                    cycleNodes.Add(Tail(h));
                    int v = Head(h);
                    int twin = h ^ 1;
                    var outs = star[v];
                    int idx = starIndex[twin];
                    h = outs[(idx - 1 + outs.Count) % outs.Count]; // clockwise-next from the twin
                    if (++guard > he + 1) break; // corrupt star — bail rather than loop forever
                }

                RemoveSpikes(cycleNodes);
                if (cycleNodes.Count < 3) continue;

                var cyc = new FaceCycle();
                foreach (var v in cycleNodes) cyc.Ring.Add(_nodes[v]);
                cyc.SignedArea = PolylineOps.SignedArea(cyc.Ring);
                if (Math.Abs(cyc.SignedArea) > 1e-9) cycles.Add(cyc);
            }
            return cycles;
        }

        private int Head(int halfEdge) =>
            (halfEdge & 1) == 0 ? _edges[halfEdge >> 1].b : _edges[halfEdge >> 1].a;

        private int Tail(int halfEdge) =>
            (halfEdge & 1) == 0 ? _edges[halfEdge >> 1].a : _edges[halfEdge >> 1].b;

        /// <summary>Collapses antenna edges (… a, b, a …) that a face walk traverses on both sides.</summary>
        private static void RemoveSpikes(List<int> cycle)
        {
            bool changed = true;
            while (changed && cycle.Count >= 3)
            {
                changed = false;
                for (int i = 0; i < cycle.Count; i++)
                {
                    int prev = cycle[(i - 1 + cycle.Count) % cycle.Count];
                    int next = cycle[(i + 1) % cycle.Count];
                    if (prev == next)
                    {
                        // Remove the spike tip and the duplicated return vertex.
                        int tipIdx = i;
                        int dupIdx = (i + 1) % cycle.Count;
                        if (dupIdx > tipIdx) { cycle.RemoveAt(dupIdx); cycle.RemoveAt(tipIdx); }
                        else { cycle.RemoveAt(tipIdx); cycle.RemoveAt(dupIdx); }
                        changed = true;
                        break;
                    }
                }
            }
        }
    }
}
