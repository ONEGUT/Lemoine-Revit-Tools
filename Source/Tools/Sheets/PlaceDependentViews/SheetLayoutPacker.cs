using System;
using System.Collections.Generic;
using System.Linq;

namespace LemoineTools.Tools.Sheets.PlaceDependentViews
{
    /// <summary>
    /// Revit-free layout math for arranging a set of viewport rectangles inside a
    /// drawing area without overlap, then centering the packed block so the leftover
    /// space is split evenly against every edge.
    ///
    /// All sizes are in sheet feet. The returned centers are expressed relative to the
    /// drawing-area origin (bottom-left), with +X to the right and +Y up — matching
    /// Revit sheet coordinates. The caller adds the area's bottom-left corner to map
    /// each center into absolute sheet space.
    ///
    /// Two strategies, chosen automatically:
    ///   • Uniform sizes (the common matchline-dependent case): pick the rows×cols grid
    ///     whose block aspect ratio best matches the drawing area, then center it.
    ///   • Mixed sizes: row ("shelf") packing in input order, each row and the whole
    ///     stack centered.
    /// </summary>
    public static class SheetLayoutPacker
    {
        public readonly struct Rect
        {
            public readonly double W;
            public readonly double H;
            public Rect(double w, double h) { W = w; H = h; }
        }

        public readonly struct Placement
        {
            /// <summary>Center X relative to the drawing-area bottom-left (feet, +X right).</summary>
            public readonly double CenterX;
            /// <summary>Center Y relative to the drawing-area bottom-left (feet, +Y up).</summary>
            public readonly double CenterY;
            public Placement(double cx, double cy) { CenterX = cx; CenterY = cy; }
        }

        public sealed class Result
        {
            /// <summary>One placement per input rect, in input order.</summary>
            public IReadOnlyList<Placement> Placements { get; }
            /// <summary>True if the packed block exceeds the drawing area on either axis.</summary>
            public bool Overflow { get; }
            /// <summary>Columns used by the uniform grid path (0 for the shelf path).</summary>
            public int Columns { get; }
            /// <summary>Rows used.</summary>
            public int Rows { get; }

            public Result(IReadOnlyList<Placement> placements, bool overflow, int cols, int rows)
            {
                Placements = placements;
                Overflow   = overflow;
                Columns    = cols;
                Rows       = rows;
            }
        }

        /// <summary>
        /// Pack <paramref name="rects"/> into a <paramref name="areaW"/>×<paramref name="areaH"/>
        /// drawing area with <paramref name="gap"/> spacing between items. Returns one center per
        /// rect (relative to the area's bottom-left) plus an overflow flag.
        /// </summary>
        public static Result Pack(
            IReadOnlyList<Rect> rects,
            double areaW,
            double areaH,
            double gap,
            double uniformTolerance = 0.02)
        {
            if (rects == null || rects.Count == 0)
                return new Result(new List<Placement>(), false, 0, 0);

            if (gap < 0)   gap   = 0;
            if (areaW <= 0) areaW = 1e-6;
            if (areaH <= 0) areaH = 1e-6;

            return IsUniform(rects, uniformTolerance)
                ? PackUniform(rects, areaW, areaH, gap)
                : PackShelves(rects, areaW, areaH, gap);
        }

        // ── Uniform grid ─────────────────────────────────────────────────────
        private static Result PackUniform(IReadOnlyList<Rect> rects, double areaW, double areaH, double gap)
        {
            int n = rects.Count;
            double w = rects.Max(r => r.W);   // use the max so no item is clipped by a slightly-smaller cell
            double h = rects.Max(r => r.H);

            int    bestCols   = 1;
            double bestScore  = double.MaxValue;
            bool   bestFits   = false;
            double areaAspect = areaW / areaH;

            for (int cols = 1; cols <= n; cols++)
            {
                int    rows   = (int)Math.Ceiling(n / (double)cols);
                double blockW = cols * w + (cols - 1) * gap;
                double blockH = rows * h + (rows - 1) * gap;
                bool   fits   = blockW <= areaW + 1e-9 && blockH <= areaH + 1e-9;

                // Prefer arrangements that fit; among those, the block whose aspect best
                // matches the area (fills it most evenly). When nothing fits, minimise the
                // worst overflow ratio so we still produce the least-bad layout.
                double score = fits
                    ? Math.Abs((blockW / blockH) - areaAspect)
                    : Math.Max(blockW / areaW, blockH / areaH);

                bool better = bestFits == fits ? score < bestScore : fits; // a fitting layout always beats a non-fitting one
                if (better)
                {
                    bestFits  = fits;
                    bestScore = score;
                    bestCols  = cols;
                }
            }

            int    finalRows = (int)Math.Ceiling(n / (double)bestCols);
            double bW = bestCols * w + (bestCols - 1) * gap;
            double bH = finalRows * h + (finalRows - 1) * gap;

            double originX = (areaW - bW) / 2.0;          // block bottom-left, centers leftover space
            double topY    = (areaH + bH) / 2.0;          // top edge of the block (Y up)

            var placements = new List<Placement>(n);
            for (int i = 0; i < n; i++)
            {
                int row = i / bestCols;                    // row 0 = top, reading order
                int col = i % bestCols;
                double cx = originX + col * (w + gap) + w / 2.0;
                double cy = topY - row * (h + gap) - h / 2.0;
                placements.Add(new Placement(cx, cy));
            }

            bool overflow = bW > areaW + 1e-9 || bH > areaH + 1e-9;
            return new Result(placements, overflow, bestCols, finalRows);
        }

        // ── Mixed-size shelf packing (preserves input order) ─────────────────
        private static Result PackShelves(IReadOnlyList<Rect> rects, double areaW, double areaH, double gap)
        {
            var rows      = new List<List<int>>();
            var rowWidths = new List<double>();
            var rowHeights = new List<double>();

            var current   = new List<int>();
            double curW    = 0, curH = 0;

            for (int i = 0; i < rects.Count; i++)
            {
                double rw = rects[i].W;
                double prospective = current.Count == 0 ? rw : curW + gap + rw;
                if (current.Count > 0 && prospective > areaW + 1e-9)
                {
                    rows.Add(current); rowWidths.Add(curW); rowHeights.Add(curH);
                    current = new List<int>(); curW = 0; curH = 0;
                    prospective = rw;
                }
                current.Add(i);
                curW = prospective;
                curH = Math.Max(curH, rects[i].H);
            }
            if (current.Count > 0) { rows.Add(current); rowWidths.Add(curW); rowHeights.Add(curH); }

            double blockH = rowHeights.Sum() + gap * Math.Max(0, rows.Count - 1);
            double topY   = (areaH + blockH) / 2.0;

            var placements = new Placement[rects.Count];
            double cursorTop = topY;
            for (int r = 0; r < rows.Count; r++)
            {
                double rowH    = rowHeights[r];
                double rowW    = rowWidths[r];
                double rowMidY = cursorTop - rowH / 2.0;
                double cursorX = (areaW - rowW) / 2.0;     // center each row horizontally
                foreach (int idx in rows[r])
                {
                    double w = rects[idx].W;
                    placements[idx] = new Placement(cursorX + w / 2.0, rowMidY);
                    cursorX += w + gap;
                }
                cursorTop -= rowH + gap;
            }

            bool overflow = blockH > areaH + 1e-9 || rowWidths.Any(x => x > areaW + 1e-9);
            return new Result(placements, overflow, 0, rows.Count);
        }

        private static bool IsUniform(IReadOnlyList<Rect> rects, double tol)
        {
            double minW = rects.Min(r => r.W), maxW = rects.Max(r => r.W);
            double minH = rects.Min(r => r.H), maxH = rects.Max(r => r.H);
            return (maxW - minW) <= tol && (maxH - minH) <= tol;
        }
    }
}
