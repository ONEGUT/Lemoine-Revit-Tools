using System;

namespace LemoineTools.PdfGeometry.Primitives
{
    /// <summary>A 2D line segment in PDF point space.</summary>
    public readonly struct Seg2
    {
        public Pt2 A { get; }
        public Pt2 B { get; }

        public Seg2(Pt2 a, Pt2 b) { A = a; B = b; }

        public double Length => A.DistanceTo(B);
        public Pt2 Direction => (B - A).Normalized();
        public Pt2 Mid => Pt2.Lerp(A, B, 0.5);

        public Bounds2 Bounds => Bounds2.FromPoints(A, B);

        /// <summary>Closest point on the segment to <paramref name="p"/>, with its parameter t in [0,1].</summary>
        public Pt2 ClosestPoint(Pt2 p, out double t)
        {
            Pt2 ab = B - A;
            double lenSq = ab.LengthSq;
            if (lenSq <= double.Epsilon) { t = 0; return A; }
            t = (p - A).Dot(ab) / lenSq;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            return Pt2.Lerp(A, B, t);
        }

        public double DistanceTo(Pt2 p) => ClosestPoint(p, out _).DistanceTo(p);

        /// <summary>
        /// Proper or touching intersection of two segments. Returns true with the
        /// intersection point when the segments cross (including endpoint touches
        /// within <paramref name="eps"/>); collinear overlaps return false — the
        /// caller (endpoint clustering) handles those separately.
        /// </summary>
        public static bool Intersect(Seg2 s1, Seg2 s2, double eps, out Pt2 point, out double t1, out double t2)
        {
            point = default; t1 = 0; t2 = 0;
            Pt2 r = s1.B - s1.A;
            Pt2 s = s2.B - s2.A;
            double denom = r.Cross(s);
            if (Math.Abs(denom) <= double.Epsilon) return false; // parallel or collinear

            Pt2 qp = s2.A - s1.A;
            t1 = qp.Cross(s) / denom;
            t2 = qp.Cross(r) / denom;

            double tol1 = s1.Length <= double.Epsilon ? 0 : eps / s1.Length;
            double tol2 = s2.Length <= double.Epsilon ? 0 : eps / s2.Length;
            if (t1 < -tol1 || t1 > 1 + tol1 || t2 < -tol2 || t2 > 1 + tol2) return false;

            t1 = Math.Max(0, Math.Min(1, t1));
            t2 = Math.Max(0, Math.Min(1, t2));
            point = Pt2.Lerp(s1.A, s1.B, t1);
            return true;
        }

        public override string ToString() => $"{A} → {B}";
    }
}
