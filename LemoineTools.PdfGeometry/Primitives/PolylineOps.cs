using System;
using System.Collections.Generic;

namespace LemoineTools.PdfGeometry.Primitives
{
    /// <summary>
    /// Pure helpers over open polylines (vertex lists) and closed rings
    /// (vertex lists where the last→first segment is implied; first vertex
    /// is NOT repeated at the end).
    /// </summary>
    public static class PolylineOps
    {
        public static double Length(IReadOnlyList<Pt2> pts)
        {
            double len = 0;
            for (int i = 1; i < pts.Count; i++) len += pts[i - 1].DistanceTo(pts[i]);
            return len;
        }

        public static double RingLength(IReadOnlyList<Pt2> ring)
        {
            if (ring.Count < 2) return 0;
            return Length(ring) + ring[ring.Count - 1].DistanceTo(ring[0]);
        }

        /// <summary>Point at arc-length s along an open polyline; s clamped to [0, length].</summary>
        public static Pt2 PointAt(IReadOnlyList<Pt2> pts, double s)
        {
            if (pts.Count == 0) return Pt2.Zero;
            if (s <= 0) return pts[0];
            for (int i = 1; i < pts.Count; i++)
            {
                double seg = pts[i - 1].DistanceTo(pts[i]);
                if (s <= seg && seg > double.Epsilon)
                    return Pt2.Lerp(pts[i - 1], pts[i], s / seg);
                s -= seg;
            }
            return pts[pts.Count - 1];
        }

        /// <summary>
        /// Closest point on an open polyline to <paramref name="p"/>, returning its
        /// arc-length parameter and the distance.
        /// </summary>
        public static double ClosestParam(IReadOnlyList<Pt2> pts, Pt2 p, out double distance)
        {
            double bestS = 0, bestD = double.MaxValue, acc = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                var seg = new Seg2(pts[i - 1], pts[i]);
                Pt2 cp = seg.ClosestPoint(p, out double t);
                double d = cp.DistanceTo(p);
                if (d < bestD)
                {
                    bestD = d;
                    bestS = acc + t * seg.Length;
                }
                acc += seg.Length;
            }
            distance = bestD;
            return bestS;
        }

        /// <summary>
        /// Extracts the sub-polyline of an open polyline between arc-length
        /// parameters s0 &lt; s1, including exact interpolated end points.
        /// </summary>
        public static List<Pt2> SubPolyline(IReadOnlyList<Pt2> pts, double s0, double s1)
        {
            double total = Length(pts);
            if (s0 < 0) s0 = 0;
            if (s1 > total) s1 = total;
            var outPts = new List<Pt2> { PointAt(pts, s0) };
            double acc = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                double seg = pts[i - 1].DistanceTo(pts[i]);
                double vertS = acc + seg;
                if (vertS > s0 && vertS < s1)
                {
                    var v = pts[i];
                    if (outPts[outPts.Count - 1].DistanceTo(v) > 1e-9) outPts.Add(v);
                }
                acc = vertS;
                if (acc >= s1) break;
            }
            var end = PointAt(pts, s1);
            if (outPts[outPts.Count - 1].DistanceTo(end) > 1e-9) outPts.Add(end);
            if (outPts.Count == 1) outPts.Add(end); // degenerate guard: always ≥ 2 points
            return outPts;
        }

        /// <summary>Signed area of a closed ring (shoelace). Positive = counter-clockwise in PDF space (Y up).</summary>
        public static double SignedArea(IReadOnlyList<Pt2> ring)
        {
            double a = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                Pt2 p = ring[i];
                Pt2 q = ring[(i + 1) % ring.Count];
                a += p.X * q.Y - q.X * p.Y;
            }
            return a * 0.5;
        }

        public static double Area(IReadOnlyList<Pt2> ring) => Math.Abs(SignedArea(ring));

        /// <summary>Even-odd point-in-polygon test on a closed ring.</summary>
        public static bool ContainsPoint(IReadOnlyList<Pt2> ring, Pt2 p)
        {
            bool inside = false;
            int n = ring.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Pt2 a = ring[i], b = ring[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) &&
                    p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>
        /// True when the closed ring is a simple polygon: no two non-adjacent
        /// segments intersect. O(n²) — fine at post-simplification vertex counts.
        /// </summary>
        public static bool IsSimpleRing(IReadOnlyList<Pt2> ring, double eps)
        {
            int n = ring.Count;
            if (n < 3) return false;
            for (int i = 0; i < n; i++)
            {
                var si = new Seg2(ring[i], ring[(i + 1) % n]);
                for (int j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1) continue; // adjacent across the wrap
                    var sj = new Seg2(ring[j], ring[(j + 1) % n]);
                    if (Seg2.Intersect(si, sj, -eps, out _, out _, out _)) return false;
                }
            }
            return true;
        }

        public static List<Pt2> Reversed(IReadOnlyList<Pt2> pts)
        {
            var r = new List<Pt2>(pts.Count);
            for (int i = pts.Count - 1; i >= 0; i--) r.Add(pts[i]);
            return r;
        }

        /// <summary>Removes consecutive duplicate vertices (within eps) and, for rings, a duplicated closing vertex.</summary>
        public static List<Pt2> Dedupe(IReadOnlyList<Pt2> pts, double eps, bool isRing)
        {
            var outPts = new List<Pt2>(pts.Count);
            foreach (var p in pts)
                if (outPts.Count == 0 || outPts[outPts.Count - 1].DistanceTo(p) > eps)
                    outPts.Add(p);
            if (isRing && outPts.Count > 1 && outPts[0].DistanceTo(outPts[outPts.Count - 1]) <= eps)
                outPts.RemoveAt(outPts.Count - 1);
            return outPts;
        }

        /// <summary>
        /// True when any segment of the open polyline passes through the interior
        /// of the polygon defined by <paramref name="ring"/> (midpoint sampling).
        /// Used to invalidate traced faces cut by a new split line.
        /// </summary>
        public static bool PolylineCutsRing(IReadOnlyList<Pt2> polyline, IReadOnlyList<Pt2> ring)
        {
            for (int i = 1; i < polyline.Count; i++)
            {
                // Sample a few interior points per segment — robust against
                // segments whose endpoints both lie outside (or on) the ring.
                for (int k = 1; k <= 3; k++)
                {
                    var p = Pt2.Lerp(polyline[i - 1], polyline[i], k / 4.0);
                    if (ContainsPoint(ring, p)) return true;
                }
            }
            return false;
        }
    }
}
