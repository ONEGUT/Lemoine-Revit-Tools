using System;

namespace LemoineTools.Tools.Dimensioning.AutoDimension.Core
{
    /// <summary>
    /// 2D line segment used to model dimension anatomy (dimension lines, witness lines,
    /// leader chords) for crossing tests. Revit-free.
    /// </summary>
    public readonly struct Seg2
    {
        public readonly Vec2 A;
        public readonly Vec2 B;

        public Seg2(Vec2 a, Vec2 b) { A = a; B = b; }

        public double Length => (B - A).Length;

        public Box2 Bounds => Box2.FromPoints(A, B);

        /// <summary>
        /// True when the two segments properly cross — each strictly straddles the other's
        /// line. Shared endpoints, touches, and collinear overlaps do NOT count: the drafting
        /// rules this feeds (dimension lines never crossing witness lines) are about genuine
        /// crossings, and witness lines legitimately touch the dimension line they serve.
        /// </summary>
        public bool Crosses(Seg2 o)
        {
            const double eps = 1e-9;
            double d1 = Cross(B - A, o.A - A);
            double d2 = Cross(B - A, o.B - A);
            double d3 = Cross(o.B - o.A, A - o.A);
            double d4 = Cross(o.B - o.A, B - o.A);
            bool straddles1 = (d1 > eps && d2 < -eps) || (d1 < -eps && d2 > eps);
            bool straddles2 = (d3 > eps && d4 < -eps) || (d3 < -eps && d4 > eps);
            return straddles1 && straddles2;
        }

        private static double Cross(Vec2 u, Vec2 v) => u.X * v.Y - u.Y * v.X;

        public override string ToString() => $"{A} → {B}";
    }
}
