using System;
using System.Collections.Generic;
using LemoineTools.PdfGeometry.Primitives;

namespace LemoineTools.PdfGeometry.Raster
{
    /// <summary>
    /// Moore-neighborhood boundary tracing over a filled-region mask.
    /// Produces the outer contour and one contour per interior hole, as
    /// pixel-center polylines (pixel space: origin top-left, Y down).
    /// </summary>
    public static class MooreContourTracer
    {
        // Moore neighborhood in clockwise order (pixel space, Y down): W, NW, N, NE, E, SE, S, SW.
        private static readonly int[] NbrX = { -1, -1, 0, 1, 1, 1, 0, -1 };
        private static readonly int[] NbrY = { 0, -1, -1, -1, 0, 1, 1, 1 };

        public sealed class ContourSet
        {
            public List<Pt2> Outer { get; }
            public List<List<Pt2>> Holes { get; }
            public ContourSet(List<Pt2> outer, List<List<Pt2>> holes) { Outer = outer; Holes = holes; }
        }

        /// <summary>
        /// Traces the outer contour and all hole contours of the (single,
        /// connected) region produced by a flood fill. The search window is the
        /// fill's bounding box, passed in to avoid a full-page scan.
        /// </summary>
        public static ContourSet Trace(BitGrid region, int minX, int minY, int maxX, int maxY)
        {
            var outer = TraceComponent(region.GetSafe, FindStart(region, minX, minY, maxX, maxY)
                ?? throw new InvalidOperationException("Contour trace called on an empty region."));

            var holes = TraceHoles(region, minX, minY, maxX, maxY);
            return new ContourSet(outer, holes);
        }

        private static (int x, int y)? FindStart(BitGrid grid, int minX, int minY, int maxX, int maxY)
        {
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                    if (grid[x, y]) return (x, y);
            return null;
        }

        /// <summary>
        /// Standard Moore tracing with Jacob's stopping criterion: terminate on
        /// re-entering the start pixel from the same backtrack cell.
        /// </summary>
        private static List<Pt2> TraceComponent(Func<int, int, bool> isSet, (int x, int y) start)
        {
            var contour = new List<Pt2> { new Pt2(start.x, start.y) };

            // Start pixel is the topmost-leftmost ⇒ its W neighbor is clear.
            var c = start;
            var b = (x: start.x - 1, y: start.y);
            var startBacktrack = b;
            bool firstMove = true;
            long cap = 0;

            while (true)
            {
                // Index of the backtrack cell in c's neighborhood.
                int bi = NeighborIndex(c, b);
                bool moved = false;
                for (int k = 1; k <= 8; k++)
                {
                    int i = (bi + k) % 8;
                    int nx = c.x + NbrX[i], ny = c.y + NbrY[i];
                    if (isSet(nx, ny))
                    {
                        int prev = (bi + k - 1) % 8;
                        b = (c.x + NbrX[prev], c.y + NbrY[prev]);
                        c = (nx, ny);
                        moved = true;
                        break;
                    }
                }
                if (!moved) return contour; // isolated single pixel

                if (!firstMove && c == start && b == startBacktrack) return contour;
                firstMove = false;
                contour.Add(new Pt2(c.x, c.y));

                if (++cap > 8L * 1024 * 1024)
                    throw new InvalidOperationException("Contour trace exceeded iteration cap — corrupt region mask.");
            }
        }

        private static int NeighborIndex((int x, int y) c, (int x, int y) n)
        {
            int dx = n.x - c.x, dy = n.y - c.y;
            for (int i = 0; i < 8; i++)
                if (NbrX[i] == dx && NbrY[i] == dy) return i;
            return 0; // backtrack drifted out of the 8-neighborhood (cannot happen mid-trace); restart scan at W
        }

        /// <summary>
        /// Holes = 4-connected components of non-region pixels inside the
        /// region's bounding box that cannot reach the (inflated) box border.
        /// Each component is Moore-traced; its outer contour is the hole ring.
        /// Note the component includes the hole's own barrier linework, so the
        /// ring lands on the outside of e.g. a column outline — which is exactly
        /// where the floor sketch's inner loop belongs.
        /// </summary>
        private static List<List<Pt2>> TraceHoles(BitGrid region, int minX, int minY, int maxX, int maxY)
        {
            int x0 = Math.Max(0, minX - 1), y0 = Math.Max(0, minY - 1);
            int x1 = Math.Min(region.Width - 1, maxX + 1), y1 = Math.Min(region.Height - 1, maxY + 1);
            int w = x1 - x0 + 1, h = y1 - y0 + 1;

            // Flood "outside" over non-region pixels from the window border.
            var outside = new BitGrid(w, h);
            var stack = new Stack<(int x, int y)>();
            for (int x = 0; x < w; x++) { stack.Push((x, 0)); stack.Push((x, h - 1)); }
            for (int y = 0; y < h; y++) { stack.Push((0, y)); stack.Push((w - 1, y)); }
            while (stack.Count > 0)
            {
                var (px, py) = stack.Pop();
                if (px < 0 || px >= w || py < 0 || py >= h) continue;
                if (outside[px, py] || region[px + x0, py + y0]) continue;
                outside[px, py] = true;
                stack.Push((px - 1, py)); stack.Push((px + 1, py));
                stack.Push((px, py - 1)); stack.Push((px, py + 1));
            }

            // Remaining non-region, non-outside pixels are hole pixels.
            var holes = new List<List<Pt2>>();
            var labeled = new BitGrid(w, h);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (labeled[x, y] || outside[x, y] || region[x + x0, y + y0]) continue;

                    // Collect this hole component (4-connected).
                    var component = new BitGrid(w, h);
                    stack.Push((x, y));
                    while (stack.Count > 0)
                    {
                        var (px, py) = stack.Pop();
                        if (px < 0 || px >= w || py < 0 || py >= h) continue;
                        if (labeled[px, py] || outside[px, py] || region[px + x0, py + y0]) continue;
                        labeled[px, py] = true;
                        component[px, py] = true;
                        stack.Push((px - 1, py)); stack.Push((px + 1, py));
                        stack.Push((px, py - 1)); stack.Push((px, py + 1));
                    }

                    // Topmost-leftmost pixel of the component starts the trace.
                    (int x, int y)? cstart = null;
                    for (int yy = 0; yy < h && cstart == null; yy++)
                        for (int xx = 0; xx < w; xx++)
                            if (component[xx, yy]) { cstart = (xx, yy); break; }
                    if (cstart == null) continue;

                    var ring = TraceComponent(component.GetSafe, cstart.Value);
                    // Shift back to full-grid pixel coordinates.
                    for (int i = 0; i < ring.Count; i++)
                        ring[i] = new Pt2(ring[i].X + x0, ring[i].Y + y0);
                    holes.Add(ring);
                }
            }
            return holes;
        }
    }
}
