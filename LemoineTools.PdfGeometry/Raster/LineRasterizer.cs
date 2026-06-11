using System;
using System.Collections.Generic;
using LemoineTools.PdfGeometry.Primitives;

namespace LemoineTools.PdfGeometry.Raster
{
    /// <summary>
    /// Stamps vector polylines into a barrier <see cref="BitGrid"/> so flood
    /// fills stop at user split lines. The exact vector polyline is kept in
    /// the edge graph; this raster footprint exists only to act as a dam.
    /// </summary>
    public static class LineRasterizer
    {
        /// <summary>
        /// Rasterizes a polyline (pixel coordinates) into <paramref name="grid"/>
        /// with a square brush of <paramref name="strokePx"/> pixels (2–3 recommended).
        /// </summary>
        public static void StampPolyline(BitGrid grid, IReadOnlyList<Pt2> polylinePx, int strokePx)
        {
            if (strokePx < 1) strokePx = 1;
            for (int i = 1; i < polylinePx.Count; i++)
                StampSegment(grid, polylinePx[i - 1], polylinePx[i], strokePx);
            if (polylinePx.Count == 1)
                StampBrush(grid, (int)Math.Round(polylinePx[0].X), (int)Math.Round(polylinePx[0].Y), strokePx);
        }

        private static void StampSegment(BitGrid grid, Pt2 a, Pt2 b, int strokePx)
        {
            // Bresenham over the rounded endpoints, brush-stamped at every step.
            int x0 = (int)Math.Round(a.X), y0 = (int)Math.Round(a.Y);
            int x1 = (int)Math.Round(b.X), y1 = (int)Math.Round(b.Y);
            int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                StampBrush(grid, x0, y0, strokePx);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        private static void StampBrush(BitGrid grid, int cx, int cy, int strokePx)
        {
            int lo = -(strokePx - 1) / 2;
            int hi = strokePx / 2;
            for (int oy = lo; oy <= hi; oy++)
                for (int ox = lo; ox <= hi; ox++)
                {
                    int x = cx + ox, y = cy + oy;
                    if (grid.InBounds(x, y)) grid[x, y] = true;
                }
        }
    }
}
