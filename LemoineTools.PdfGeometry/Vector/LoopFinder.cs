using System;
using System.Collections.Generic;
using System.Linq;
using LemoineTools.PdfGeometry.Primitives;

namespace LemoineTools.PdfGeometry.Vector
{
    public sealed class VectorLoopResult
    {
        public List<Pt2> Outer { get; }
        public List<List<Pt2>> Holes { get; }

        public VectorLoopResult(List<Pt2> outer, List<List<Pt2>> holes)
        {
            Outer = outer; Holes = holes;
        }
    }

    /// <summary>
    /// Finds the smallest arrangement face enclosing a seed point, plus its
    /// island holes (disconnected interior linework like columns), from the
    /// cycles extracted by <see cref="SegmentGraph"/>.
    /// </summary>
    public static class LoopFinder
    {
        public static VectorLoopResult? FindEnclosing(SegmentGraph graph, Pt2 seed, double minArea)
        {
            // Bounded faces are the CCW cycles; pick the smallest containing the seed.
            SegmentGraph.FaceCycle? best = null;
            foreach (var cyc in graph.Cycles)
            {
                if (cyc.SignedArea <= minArea) continue;
                if (!PolylineOps.ContainsPoint(cyc.Ring, seed)) continue;
                if (best == null || cyc.SignedArea < best.SignedArea) best = cyc;
            }
            if (best == null) return null;

            // Island boundaries are CW cycles; a hole belongs to the face that is
            // the smallest CCW cycle containing it — keep the ones whose owner is `best`.
            var holes = new List<List<Pt2>>();
            foreach (var cw in graph.Cycles)
            {
                if (cw.SignedArea >= 0) continue;
                var probe = cw.Ring[0];
                if (!PolylineOps.ContainsPoint(best.Ring, probe)) continue;
                if (PointsEquivalent(cw.Ring, best.Ring)) continue;

                bool ownedBySmaller = graph.Cycles.Any(other =>
                    other.SignedArea > 0 &&
                    other.SignedArea < best.SignedArea &&
                    PolylineOps.ContainsPoint(other.Ring, probe));
                if (ownedBySmaller) continue;

                // The hole probe vertex lies ON the island boundary, which sits on
                // best's interior; exclude islands that ARE best's own boundary
                // (covered by PointsEquivalent above).
                holes.Add(new List<Pt2>(cw.Ring));
            }

            return new VectorLoopResult(new List<Pt2>(best.Ring), holes);
        }

        private static bool PointsEquivalent(IReadOnlyList<Pt2> a, IReadOnlyList<Pt2> b)
        {
            if (a.Count != b.Count) return false;
            var setA = new HashSet<(double, double)>(a.Select(p => (Math.Round(p.X, 6), Math.Round(p.Y, 6))));
            return b.All(p => setA.Contains((Math.Round(p.X, 6), Math.Round(p.Y, 6))));
        }
    }
}
