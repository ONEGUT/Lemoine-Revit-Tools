using System;
using System.Collections.Generic;

namespace LemoineTools.Tools.Ceilings.CeilingTags.TagCore
{
    /// <summary>
    /// A boolean occupancy grid over a world-feet rectangle, plus the operations the tag
    /// planner needs: scanline polygon fill (even-odd, so holes need no winding rules),
    /// occluder subtraction, island labelling, and a clearance (distance-to-edge) field.
    ///
    /// Everything downstream works on cells rather than polygons because the hard parts —
    /// "what is left of this ceiling once lower ceilings cover it", "does the centroid
    /// actually land ON the remaining shape", "where is the middle of this corridor" — are
    /// all trivial on a raster and genuinely difficult on arbitrary polygon boolean ops.
    /// The cell size is the accuracy floor; at 6 in a tag can be off-centre by at most ~3 in,
    /// which is invisible at any RCP scale.
    /// </summary>
    public sealed class PolyRaster
    {
        public int    Width    { get; }
        public int    Height   { get; }
        public double CellSize { get; }
        public double OriginX  { get; }
        public double OriginY  { get; }

        /// <summary>Row-major occupancy, <c>index = y * Width + x</c>.</summary>
        private readonly bool[] _cells;

        public PolyRaster(double minX, double minY, double maxX, double maxY, double cellSize)
        {
            if (cellSize <= 0) throw new ArgumentOutOfRangeException(nameof(cellSize));

            CellSize = cellSize;

            // The origin backs off a FULL CELL on each axis, and the extent gets 3 extra cells
            // (one for the partial cell at the max edge, one for the empty ring on each side),
            // so a filled cell can never sit on the grid border.
            //
            // This is load-bearing, not defensive padding. A region touching the border breaks
            // both consumers of this grid: a border cell has no empty neighbour, so its
            // clearance is measured to the FAR side of the region (doubling the apparent width
            // and quartering the elongation), and Zhang-Suen thinning skips the border ring
            // entirely, so the whole edge survives as skeleton — producing a line of tags
            // strung along the ceiling's edge instead of one at its centre.
            OriginX = minX - cellSize;
            OriginY = minY - cellSize;

            Width  = Math.Max(1, (int)Math.Ceiling((maxX - minX) / cellSize) + 3);
            Height = Math.Max(1, (int)Math.Ceiling((maxY - minY) / cellSize) + 3);
            _cells = new bool[Width * Height];
        }

        /// <summary>Clones another raster's geometry with an empty cell array. Used by
        /// <see cref="IslandSubset"/> — an island must land on exactly the parent's grid, so
        /// the dimensions are copied rather than recomputed from world bounds.</summary>
        private PolyRaster(PolyRaster src)
        {
            CellSize = src.CellSize;
            OriginX  = src.OriginX;
            OriginY  = src.OriginY;
            Width    = src.Width;
            Height   = src.Height;
            _cells   = new bool[Width * Height];
        }

        public bool this[int x, int y]
        {
            get => x >= 0 && y >= 0 && x < Width && y < Height && _cells[y * Width + x];
            set { if (x >= 0 && y >= 0 && x < Width && y < Height) _cells[y * Width + x] = value; }
        }

        public int Count
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _cells.Length; i++) if (_cells[i]) n++;
                return n;
            }
        }

        /// <summary>Area of the filled cells, in square feet.</summary>
        public double AreaFt2 => Count * CellSize * CellSize;

        /// <summary>World point at the centre of a cell.</summary>
        public Pt2 CellCentre(int x, int y)
            => new Pt2(OriginX + (x + 0.5) * CellSize, OriginY + (y + 0.5) * CellSize);

        /// <summary>Cell containing a world point (may be outside the grid — callers clamp/test).</summary>
        public void CellAt(Pt2 p, out int x, out int y)
        {
            x = (int)Math.Floor((p.X - OriginX) / CellSize);
            y = (int)Math.Floor((p.Y - OriginY) / CellSize);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Rasterization
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Even-odd scanline fill of a set of loops. All loops go in together — outers and
        /// holes alike — because even-odd parity subtracts a loop nested inside another with
        /// no orientation bookkeeping at all. <paramref name="value"/> false erases instead of
        /// filling, which is how occluders are subtracted.
        /// </summary>
        public void FillLoops(IEnumerable<Loop2> loops, bool value = true)
        {
            // Flatten to edges once; the scanline below walks them per row.
            var ex0 = new List<double>();
            var ey0 = new List<double>();
            var ex1 = new List<double>();
            var ey1 = new List<double>();

            foreach (Loop2 loop in loops)
            {
                var pts = loop.Points;
                if (pts.Count < 3) continue;   // not an area — nothing to fill
                for (int i = 0; i < pts.Count; i++)
                {
                    Pt2 a = pts[i];
                    Pt2 b = pts[(i + 1) % pts.Count];
                    if (Math.Abs(a.Y - b.Y) < 1e-12) continue;   // horizontal edges never cross a scanline
                    ex0.Add(a.X); ey0.Add(a.Y); ex1.Add(b.X); ey1.Add(b.Y);
                }
            }
            if (ex0.Count == 0) return;

            var xs = new List<double>();
            for (int gy = 0; gy < Height; gy++)
            {
                double sy = OriginY + (gy + 0.5) * CellSize;   // sample at the cell centre row
                xs.Clear();

                for (int e = 0; e < ex0.Count; e++)
                {
                    double y0 = ey0[e], y1 = ey1[e];
                    // Half-open test: a vertex exactly on the scanline counts for one edge only,
                    // so parity can't break at a shared vertex.
                    if ((sy >= y0 && sy < y1) || (sy >= y1 && sy < y0))
                    {
                        double t = (sy - y0) / (y1 - y0);
                        xs.Add(ex0[e] + t * (ex1[e] - ex0[e]));
                    }
                }
                if (xs.Count < 2) continue;
                xs.Sort();

                for (int i = 0; i + 1 < xs.Count; i += 2)
                {
                    double xa = xs[i], xb = xs[i + 1];
                    int gxa = (int)Math.Floor((xa - OriginX) / CellSize - 0.5) + 1;
                    int gxb = (int)Math.Floor((xb - OriginX) / CellSize - 0.5);
                    if (gxa < 0) gxa = 0;
                    if (gxb >= Width) gxb = Width - 1;
                    for (int gx = gxa; gx <= gxb; gx++)
                        _cells[gy * Width + gx] = value;
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Islands
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 4-connected connected-component labelling. Returns a label array (0 = empty,
        /// 1..N = island id) and sets <paramref name="count"/>. Islands matter because a
        /// ceiling cut in two by an occluder is two separate places that each need a tag —
        /// tagging their combined centroid would put the tag in the gap between them.
        /// </summary>
        public int[] LabelIslands(out int count)
        {
            var labels = new int[_cells.Length];
            var stack  = new Stack<int>();
            count = 0;

            for (int start = 0; start < _cells.Length; start++)
            {
                if (!_cells[start] || labels[start] != 0) continue;

                count++;
                labels[start] = count;
                stack.Push(start);

                while (stack.Count > 0)
                {
                    int idx = stack.Pop();
                    int x = idx % Width, y = idx / Width;

                    if (x > 0)          TryPush(labels, stack, idx - 1,     count);
                    if (x < Width - 1)  TryPush(labels, stack, idx + 1,     count);
                    if (y > 0)          TryPush(labels, stack, idx - Width, count);
                    if (y < Height - 1) TryPush(labels, stack, idx + Width, count);
                }
            }
            return labels;
        }

        private void TryPush(int[] labels, Stack<int> stack, int idx, int label)
        {
            if (!_cells[idx] || labels[idx] != 0) return;
            labels[idx] = label;
            stack.Push(idx);
        }

        /// <summary>A new raster holding only the cells of one island label, on exactly this
        /// raster's grid.</summary>
        public PolyRaster IslandSubset(int[] labels, int label)
        {
            var sub = new PolyRaster(this);
            for (int i = 0; i < _cells.Length; i++)
                if (labels[i] == label) sub._cells[i] = true;
            return sub;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Clearance field
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Chamfer distance transform: for every filled cell, the approximate distance IN CELLS
        /// to the nearest empty cell. Empty cells are 0. Uses the standard 3-4 two-pass
        /// weighting, divided back to cell units — accurate to a few percent, which is far
        /// finer than the cell size itself.
        ///
        /// Used for two things: the widest point of a region (is this a corridor or a blob?)
        /// and picking a tag point that sits well inside rather than hard against an edge.
        /// </summary>
        public double[] ClearanceField()
        {
            const int W1 = 3, W2 = 4;                 // orthogonal / diagonal chamfer weights
            const int INF = int.MaxValue / 4;

            var d = new int[_cells.Length];
            for (int i = 0; i < d.Length; i++) d[i] = _cells[i] ? INF : 0;

            // Forward pass — top-left to bottom-right.
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width;  x++)
            {
                int i = y * Width + x;
                if (d[i] == 0) continue;
                int best = d[i];
                if (x > 0)                 best = Math.Min(best, d[i - 1]         + W1);
                if (y > 0)                 best = Math.Min(best, d[i - Width]     + W1);
                if (x > 0 && y > 0)        best = Math.Min(best, d[i - Width - 1] + W2);
                if (x < Width - 1 && y > 0)best = Math.Min(best, d[i - Width + 1] + W2);
                d[i] = best;
            }

            // Backward pass — bottom-right to top-left.
            for (int y = Height - 1; y >= 0; y--)
            for (int x = Width - 1;  x >= 0; x--)
            {
                int i = y * Width + x;
                if (d[i] == 0) continue;
                int best = d[i];
                if (x < Width - 1)                  best = Math.Min(best, d[i + 1]         + W1);
                if (y < Height - 1)                 best = Math.Min(best, d[i + Width]     + W1);
                if (x < Width - 1 && y < Height - 1)best = Math.Min(best, d[i + Width + 1] + W2);
                if (x > 0 && y < Height - 1)        best = Math.Min(best, d[i + Width - 1] + W2);
                d[i] = best;
            }

            var outD = new double[d.Length];
            for (int i = 0; i < d.Length; i++) outD[i] = d[i] >= INF ? 0.0 : d[i] / (double)W1;
            return outD;
        }

        /// <summary>True when the world point falls on a filled cell.</summary>
        public bool Contains(Pt2 p)
        {
            CellAt(p, out int x, out int y);
            return this[x, y];
        }

        /// <summary>
        /// True when the region encloses empty space — a donut, a frame left behind by a
        /// ceiling sitting inside this one, or a corridor looping a room. Flood-fills the
        /// background from cell (0,0), which the constructor's padding guarantees is empty;
        /// any empty cell the flood cannot reach is enclosed.
        /// </summary>
        public bool HasEnclosedHole()
        {
            var seen = new bool[_cells.Length];
            var stack = new Stack<int>();
            if (_cells[0]) return false;   // padding invariant broken; report no hole rather than lie
            seen[0] = true; stack.Push(0);

            while (stack.Count > 0)
            {
                int idx = stack.Pop();
                int x = idx % Width, y = idx / Width;

                if (x > 0)          Flood(seen, stack, idx - 1);
                if (x < Width - 1)  Flood(seen, stack, idx + 1);
                if (y > 0)          Flood(seen, stack, idx - Width);
                if (y < Height - 1) Flood(seen, stack, idx + Width);
            }

            for (int i = 0; i < _cells.Length; i++)
                if (!_cells[i] && !seen[i]) return true;
            return false;
        }

        private void Flood(bool[] seen, Stack<int> stack, int idx)
        {
            if (_cells[idx] || seen[idx]) return;
            seen[idx] = true;
            stack.Push(idx);
        }

        /// <summary>
        /// Centre of the region's topmost band: the horizontal middle of the highest filled
        /// row, pushed down half that band's thickness so the point sits inside the band rather
        /// than on its edge. This is where a ring- or frame-shaped ceiling gets its single tag.
        /// </summary>
        public Pt2? TopCentre()
        {
            for (int y = Height - 1; y >= 0; y--)
            {
                int minX = -1, maxX = -1, count = 0;
                for (int x = 0; x < Width; x++)
                {
                    if (!_cells[y * Width + x]) continue;
                    if (minX < 0) minX = x;
                    maxX = x; count++;
                }
                if (count == 0) continue;

                int xc = (minX + maxX) / 2;
                if (!_cells[y * Width + xc])
                {
                    // The top row is split (two arms of a U) — take the median filled cell so
                    // the point lands on solid ceiling rather than in the gap between them.
                    int seen = 0, target = count / 2;
                    for (int x = minX; x <= maxX; x++)
                        if (_cells[y * Width + x] && seen++ == target) { xc = x; break; }
                }

                int thickness = 0;
                for (int yy = y; yy >= 0 && _cells[yy * Width + xc]; yy--) thickness++;

                return CellCentre(xc, y - thickness / 2);
            }
            return null;
        }

        /// <summary>Centroid of the filled cells in world feet, or null when nothing is filled.</summary>
        public Pt2? Centroid()
        {
            double sx = 0, sy = 0; int n = 0;
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width;  x++)
                if (_cells[y * Width + x]) { sx += x + 0.5; sy += y + 0.5; n++; }

            if (n == 0) return null;
            return new Pt2(OriginX + (sx / n) * CellSize, OriginY + (sy / n) * CellSize);
        }

        /// <summary>
        /// The filled cell nearest <paramref name="target"/> whose clearance is at least
        /// <paramref name="minClearance"/> cells, falling back to the nearest filled cell of
        /// any clearance. This is what guarantees a tag point is ON the region: the planner
        /// never emits a raw computed point, only a real interior cell centre.
        /// </summary>
        public Pt2? NearestInterior(Pt2 target, double[] clearance, double minClearance)
        {
            CellAt(target, out int tx, out int ty);

            double bestScore = double.MaxValue; int bestIdx = -1;
            double fbScore   = double.MaxValue; int fbIdx   = -1;

            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width;  x++)
            {
                int i = y * Width + x;
                if (!_cells[i]) continue;

                double dx = x - tx, dy = y - ty;
                double dist2 = dx * dx + dy * dy;

                if (dist2 < fbScore) { fbScore = dist2; fbIdx = i; }
                if (clearance[i] >= minClearance && dist2 < bestScore) { bestScore = dist2; bestIdx = i; }
            }

            int pick = bestIdx >= 0 ? bestIdx : fbIdx;
            if (pick < 0) return null;
            return CellCentre(pick % Width, pick / Width);
        }
    }
}
