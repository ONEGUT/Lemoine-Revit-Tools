using System;

namespace LemoineTools.PdfGeometry.Primitives
{
    /// <summary>
    /// Immutable 2D point/vector in PDF point space (1 pt = 1/72").
    /// PDF convention: origin bottom-left, X right, Y up.
    /// </summary>
    public readonly struct Pt2 : IEquatable<Pt2>
    {
        public double X { get; }
        public double Y { get; }

        public Pt2(double x, double y) { X = x; Y = y; }

        public static readonly Pt2 Zero = new Pt2(0, 0);

        public static Pt2 operator +(Pt2 a, Pt2 b) => new Pt2(a.X + b.X, a.Y + b.Y);
        public static Pt2 operator -(Pt2 a, Pt2 b) => new Pt2(a.X - b.X, a.Y - b.Y);
        public static Pt2 operator *(Pt2 a, double s) => new Pt2(a.X * s, a.Y * s);
        public static Pt2 operator *(double s, Pt2 a) => new Pt2(a.X * s, a.Y * s);
        public static Pt2 operator /(Pt2 a, double s) => new Pt2(a.X / s, a.Y / s);
        public static Pt2 operator -(Pt2 a) => new Pt2(-a.X, -a.Y);

        public double Dot(Pt2 b) => X * b.X + Y * b.Y;

        /// <summary>Z component of the 3D cross product — positive when b is CCW from this.</summary>
        public double Cross(Pt2 b) => X * b.Y - Y * b.X;

        public double Length => Math.Sqrt(X * X + Y * Y);
        public double LengthSq => X * X + Y * Y;

        public double DistanceTo(Pt2 b)
        {
            double dx = X - b.X, dy = Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public double DistanceSqTo(Pt2 b)
        {
            double dx = X - b.X, dy = Y - b.Y;
            return dx * dx + dy * dy;
        }

        public Pt2 Normalized()
        {
            double len = Length;
            return len <= double.Epsilon ? Zero : new Pt2(X / len, Y / len);
        }

        /// <summary>Perpendicular vector, 90° counter-clockwise.</summary>
        public Pt2 PerpCcw() => new Pt2(-Y, X);

        public static Pt2 Lerp(Pt2 a, Pt2 b, double t) =>
            new Pt2(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

        /// <summary>Angle of the vector in radians, range (-π, π].</summary>
        public double Angle => Math.Atan2(Y, X);

        public bool Equals(Pt2 other) => X == other.X && Y == other.Y;
        public override bool Equals(object? obj) => obj is Pt2 p && Equals(p);
        public override int GetHashCode() => X.GetHashCode() * 397 ^ Y.GetHashCode();
        public override string ToString() => $"({X:0.###}, {Y:0.###})";
    }
}
