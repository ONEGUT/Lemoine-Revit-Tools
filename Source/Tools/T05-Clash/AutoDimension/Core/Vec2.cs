using System;

namespace LemoineTools.Tools.Testing.AutoDimension.Core
{
    /// <summary>
    /// Minimal 2D vector of doubles. Revit-free so the layout core is unit-testable on
    /// plain fixtures. All geometry inside the abstract layout model is expressed in these.
    /// </summary>
    public readonly struct Vec2
    {
        public readonly double X;
        public readonly double Y;

        public Vec2(double x, double y) { X = x; Y = y; }

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator *(Vec2 a, double s) => new Vec2(a.X * s, a.Y * s);

        public double Dot(Vec2 o) => X * o.X + Y * o.Y;
        public double Length => Math.Sqrt(X * X + Y * Y);

        public Vec2 Normalized()
        {
            double len = Length;
            return len < 1e-9 ? new Vec2(0, 0) : new Vec2(X / len, Y / len);
        }

        /// <summary>90° counter-clockwise perpendicular (used to offset a dim string sideways).</summary>
        public Vec2 Perp() => new Vec2(-Y, X);

        public override string ToString() => $"({X:0.####},{Y:0.####})";
    }
}
