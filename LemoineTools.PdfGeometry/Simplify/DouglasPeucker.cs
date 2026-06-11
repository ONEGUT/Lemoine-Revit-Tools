using System;
using System.Collections.Generic;
using LemoineTools.PdfGeometry.Primitives;

namespace LemoineTools.PdfGeometry.Simplify
{
    /// <summary>Douglas-Peucker polyline simplification (open chains and closed rings).</summary>
    public static class DouglasPeucker
    {
        public static List<Pt2> SimplifyOpen(IReadOnlyList<Pt2> pts, double tol)
        {
            if (pts.Count <= 2) return new List<Pt2>(pts);
            var keep = new bool[pts.Count];
            keep[0] = keep[pts.Count - 1] = true;
            SimplifyRange(pts, 0, pts.Count - 1, tol, keep);
            var outPts = new List<Pt2>();
            for (int i = 0; i < pts.Count; i++) if (keep[i]) outPts.Add(pts[i]);
            return outPts;
        }

        /// <summary>
        /// Simplifies a closed ring (no repeated end vertex) by anchoring at the
        /// two mutually farthest vertices and simplifying the two halves — the
        /// standard trick to avoid DP's dependence on an arbitrary start vertex.
        /// </summary>
        public static List<Pt2> SimplifyClosed(IReadOnlyList<Pt2> ring, double tol)
        {
            if (ring.Count <= 4) return new List<Pt2>(ring);

            // Anchor 1: farthest vertex from vertex 0; anchor 2: farthest from anchor 1.
            int a1 = FarthestFrom(ring, 0);
            int a2 = FarthestFrom(ring, a1);
            int lo = Math.Min(a1, a2), hi = Math.Max(a1, a2);

            var half1 = Slice(ring, lo, hi);
            var half2 = Slice(ring, hi, lo + ring.Count); // wraps

            var s1 = SimplifyOpen(half1, tol);
            var s2 = SimplifyOpen(half2, tol);

            var outRing = new List<Pt2>(s1);
            for (int i = 1; i < s2.Count - 1; i++) outRing.Add(s2[i]); // skip shared anchors
            return outRing;
        }

        private static int FarthestFrom(IReadOnlyList<Pt2> pts, int idx)
        {
            int best = idx; double bestD = -1;
            for (int i = 0; i < pts.Count; i++)
            {
                double d = pts[idx].DistanceSqTo(pts[i]);
                if (d > bestD) { bestD = d; best = i; }
            }
            return best;
        }

        private static List<Pt2> Slice(IReadOnlyList<Pt2> ring, int from, int toInclusive)
        {
            var outPts = new List<Pt2>(toInclusive - from + 1);
            for (int i = from; i <= toInclusive; i++) outPts.Add(ring[i % ring.Count]);
            return outPts;
        }

        private static void SimplifyRange(IReadOnlyList<Pt2> pts, int first, int last, double tol, bool[] keep)
        {
            // Iterative stack to dodge deep recursion on long raster contours.
            var stack = new Stack<(int first, int last)>();
            stack.Push((first, last));
            while (stack.Count > 0)
            {
                var (f, l) = stack.Pop();
                if (l - f < 2) continue;

                var seg = new Seg2(pts[f], pts[l]);
                double maxD = -1; int maxI = -1;
                for (int i = f + 1; i < l; i++)
                {
                    double d = seg.DistanceTo(pts[i]);
                    if (d > maxD) { maxD = d; maxI = i; }
                }
                if (maxD > tol && maxI > 0)
                {
                    keep[maxI] = true;
                    stack.Push((f, maxI));
                    stack.Push((maxI, l));
                }
            }
        }
    }
}
