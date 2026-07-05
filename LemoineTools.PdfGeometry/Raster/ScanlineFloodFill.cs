using System.Collections.Generic;

namespace LemoineTools.PdfGeometry.Raster
{
    public enum FloodFailReason
    {
        None,
        SeedOnBarrier,
        SeedOutOfBounds,
        NotEnclosed,   // fill reached the page border or exceeded the size cap
    }

    public sealed class FloodFillResult
    {
        public bool Enclosed { get; }
        public FloodFailReason FailReason { get; }
        public BitGrid? Region { get; }
        public int PixelCount { get; }
        public int MinX { get; }
        public int MinY { get; }
        public int MaxX { get; }
        public int MaxY { get; }

        private FloodFillResult(bool enclosed, FloodFailReason reason, BitGrid? region,
                                int pixelCount, int minX, int minY, int maxX, int maxY)
        {
            Enclosed = enclosed; FailReason = reason; Region = region;
            PixelCount = pixelCount; MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
        }

        public static FloodFillResult Fail(FloodFailReason reason) =>
            new FloodFillResult(false, reason, null, 0, 0, 0, 0, 0);

        public static FloodFillResult Ok(BitGrid region, int count, int minX, int minY, int maxX, int maxY) =>
            new FloodFillResult(true, FloodFailReason.None, region, count, minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Stack-based scanline flood fill over a barrier mask (true = barrier).
    /// 4-connected. A fill that touches the grid border or exceeds
    /// <c>maxPixels</c> aborts as "not enclosed" — the leak guard that keeps a
    /// seed in open space from filling the whole sheet.
    /// </summary>
    public static class ScanlineFloodFill
    {
        public static FloodFillResult Fill(BitGrid barriers, int seedX, int seedY, int maxPixels)
        {
            if (!barriers.InBounds(seedX, seedY)) return FloodFillResult.Fail(FloodFailReason.SeedOutOfBounds);
            if (barriers[seedX, seedY]) return FloodFillResult.Fail(FloodFailReason.SeedOnBarrier);

            int w = barriers.Width, h = barriers.Height;
            var region = new BitGrid(w, h);
            var stack = new Stack<(int x, int y)>();
            stack.Push((seedX, seedY));

            int count = 0;
            int minX = seedX, maxX = seedX, minY = seedY, maxY = seedY;

            while (stack.Count > 0)
            {
                var (px, py) = stack.Pop();
                if (region[px, py] || barriers[px, py]) continue;

                // Expand to the full open span on this row.
                int xL = px;
                while (xL > 0 && !barriers[xL - 1, py] && !region[xL - 1, py]) xL--;
                int xR = px;
                while (xR < w - 1 && !barriers[xR + 1, py] && !region[xR + 1, py]) xR++;

                // Touching the page border means the region is not enclosed.
                if (xL == 0 || xR == w - 1 || py == 0 || py == h - 1)
                    return FloodFillResult.Fail(FloodFailReason.NotEnclosed);

                for (int x = xL; x <= xR; x++)
                {
                    region[x, py] = true;
                    count++;
                }
                if (count > maxPixels) return FloodFillResult.Fail(FloodFailReason.NotEnclosed);

                if (xL < minX) minX = xL;
                if (xR > maxX) maxX = xR;
                if (py < minY) minY = py;
                if (py > maxY) maxY = py;

                // Queue seed candidates on the rows above and below the span.
                for (int dy = -1; dy <= 1; dy += 2)
                {
                    int y = py + dy;
                    if (y < 0 || y >= h) continue;
                    bool inRun = false;
                    for (int x = xL; x <= xR; x++)
                    {
                        bool open = !barriers[x, y] && !region[x, y];
                        if (open && !inRun)
                        {
                            stack.Push((x, y));
                            inRun = true;
                        }
                        else if (!open)
                        {
                            inRun = false;
                        }
                    }
                }
            }

            return FloodFillResult.Ok(region, count, minX, minY, maxX, maxY);
        }
    }
}
