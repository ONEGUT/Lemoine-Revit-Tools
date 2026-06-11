using System;

namespace LemoineTools.Tools.Clash.AutoDimension.Core
{
    /// <summary>
    /// Axis-aligned 2D bounding box of doubles. Used for paper-space collision tests in the
    /// layout core. Revit-free.
    /// </summary>
    public readonly struct Box2
    {
        public readonly double MinX;
        public readonly double MinY;
        public readonly double MaxX;
        public readonly double MaxY;

        public Box2(double minX, double minY, double maxX, double maxY)
        {
            MinX = Math.Min(minX, maxX);
            MinY = Math.Min(minY, maxY);
            MaxX = Math.Max(minX, maxX);
            MaxY = Math.Max(minY, maxY);
        }

        public double Width  => MaxX - MinX;
        public double Height => MaxY - MinY;

        public static Box2 FromCenter(Vec2 c, double halfW, double halfH) =>
            new Box2(c.X - halfW, c.Y - halfH, c.X + halfW, c.Y + halfH);

        public static Box2 FromPoints(Vec2 a, Vec2 b) =>
            new Box2(a.X, a.Y, b.X, b.Y);

        /// <summary>True if the two boxes share any area (touching edges do not count as overlap).</summary>
        public bool Intersects(Box2 o) =>
            MinX < o.MaxX && MaxX > o.MinX && MinY < o.MaxY && MaxY > o.MinY;

        /// <summary>Overlap area (0 when disjoint). Used to size the soft/hard penalty.</summary>
        public double OverlapArea(Box2 o)
        {
            double ox = Math.Min(MaxX, o.MaxX) - Math.Max(MinX, o.MinX);
            double oy = Math.Min(MaxY, o.MaxY) - Math.Max(MinY, o.MinY);
            if (ox <= 0 || oy <= 0) return 0;
            return ox * oy;
        }

        public bool Contains(Box2 o) =>
            o.MinX >= MinX && o.MaxX <= MaxX && o.MinY >= MinY && o.MaxY <= MaxY;

        public bool Contains(Vec2 p) =>
            p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;

        public Box2 Union(Box2 o) =>
            new Box2(Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY),
                     Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY));

        public override string ToString() => $"[{MinX:0.##},{MinY:0.##} → {MaxX:0.##},{MaxY:0.##}]";
    }
}
