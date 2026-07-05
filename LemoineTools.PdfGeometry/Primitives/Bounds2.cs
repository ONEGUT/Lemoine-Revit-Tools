using System;
using System.Collections.Generic;

namespace LemoineTools.PdfGeometry.Primitives
{
    /// <summary>Axis-aligned 2D bounding box.</summary>
    public readonly struct Bounds2
    {
        public double MinX { get; }
        public double MinY { get; }
        public double MaxX { get; }
        public double MaxY { get; }

        public Bounds2(double minX, double minY, double maxX, double maxY)
        {
            MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
        }

        public static Bounds2 Empty => new Bounds2(
            double.PositiveInfinity, double.PositiveInfinity,
            double.NegativeInfinity, double.NegativeInfinity);

        public bool IsEmpty => MinX > MaxX || MinY > MaxY;

        public double Width  => MaxX - MinX;
        public double Height => MaxY - MinY;
        public Pt2 Center => new Pt2((MinX + MaxX) * 0.5, (MinY + MaxY) * 0.5);

        public static Bounds2 FromPoints(Pt2 a, Pt2 b) => new Bounds2(
            Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

        public static Bounds2 FromPoints(IReadOnlyList<Pt2> pts)
        {
            var b = Empty;
            for (int i = 0; i < pts.Count; i++) b = b.Expand(pts[i]);
            return b;
        }

        public Bounds2 Expand(Pt2 p) => new Bounds2(
            Math.Min(MinX, p.X), Math.Min(MinY, p.Y),
            Math.Max(MaxX, p.X), Math.Max(MaxY, p.Y));

        public Bounds2 Inflate(double d) => new Bounds2(MinX - d, MinY - d, MaxX + d, MaxY + d);

        public bool Contains(Pt2 p) =>
            p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;

        public bool Intersects(Bounds2 o) =>
            !IsEmpty && !o.IsEmpty &&
            MinX <= o.MaxX && MaxX >= o.MinX &&
            MinY <= o.MaxY && MaxY >= o.MinY;

        public override string ToString() => $"[{MinX:0.##},{MinY:0.##} … {MaxX:0.##},{MaxY:0.##}]";
    }
}
