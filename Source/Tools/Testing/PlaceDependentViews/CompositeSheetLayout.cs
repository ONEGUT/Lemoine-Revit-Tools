using System;
using System.Collections.Generic;
using System.Linq;

namespace LemoineTools.Tools.Testing.PlaceDependentViews
{
    /// <summary>
    /// Revit-free layout math for one "composite" sheet: a source view anchored at the
    /// top-center or left-center of the drawing area, with its sub views (callouts,
    /// sections, elevations — mixed scales and shapes) arranged in the leftover band as
    /// aligned rows (below a top-anchored source) or aligned columns (right of a
    /// left-anchored source).
    ///
    /// Same conventions as <see cref="SheetLayoutPacker"/>: all sizes in sheet feet,
    /// returned centers relative to the drawing-area bottom-left (+X right, +Y up).
    ///
    /// The anchor is chosen automatically: a candidate that fits always wins; when both
    /// fit, the one whose leftover band is larger (a wide source leaves a deeper bottom
    /// band, a tall source a wider right band); when neither fits, the least-bad overflow.
    /// </summary>
    public static class CompositeSheetLayout
    {
        public sealed class Result
        {
            /// <summary>Center of the source view.</summary>
            public SheetLayoutPacker.Placement Parent { get; }
            /// <summary>One placement per child rect, in input order.</summary>
            public IReadOnlyList<SheetLayoutPacker.Placement> Children { get; }
            /// <summary>True if the source or the child block exceeds the drawing area.</summary>
            public bool Overflow { get; }
            /// <summary>True: source top-center with rows below. False: source left-center with columns to the right.</summary>
            public bool ParentOnTop { get; }

            public Result(SheetLayoutPacker.Placement parent,
                          IReadOnlyList<SheetLayoutPacker.Placement> children,
                          bool overflow, bool parentOnTop)
            {
                Parent      = parent;
                Children    = children;
                Overflow    = overflow;
                ParentOnTop = parentOnTop;
            }
        }

        public static Result Pack(
            SheetLayoutPacker.Rect parent,
            IReadOnlyList<SheetLayoutPacker.Rect> children,
            double areaW,
            double areaH,
            double gap)
        {
            if (gap < 0)    gap   = 0;
            if (areaW <= 0) areaW = 1e-6;
            if (areaH <= 0) areaH = 1e-6;
            children = children ?? new List<SheetLayoutPacker.Rect>();

            var (top,  topBad)  = PackTopBand(parent, children, areaW, areaH, gap);
            var (left, leftBad) = PackLeftBand(parent, children, areaW, areaH, gap);

            bool topFits = !top.Overflow, leftFits = !left.Overflow;
            if (topFits != leftFits) return topFits ? top : left;

            if (topFits)
            {
                // Both fit — prefer the anchor that leaves the larger working band, which
                // naturally sends a wide source to the top and a tall source to the left.
                double topBandArea  = areaW * (areaH - parent.H - gap);
                double leftBandArea = areaH * (areaW - parent.W - gap);
                return topBandArea >= leftBandArea ? top : left;
            }

            return topBad <= leftBad ? top : left;
        }

        // ── Source top-center, children in aligned rows below ────────────────
        private static (Result, double) PackTopBand(
            SheetLayoutPacker.Rect parent, IReadOnlyList<SheetLayoutPacker.Rect> children,
            double areaW, double areaH, double gap)
        {
            const double eps = 1e-9;
            var parentPlace = new SheetLayoutPacker.Placement(areaW / 2.0, areaH - parent.H / 2.0);
            double bandH = areaH - parent.H - gap;

            // Tallest-first so the items sharing a row have similar heights and read aligned.
            var order = Enumerable.Range(0, children.Count)
                .OrderByDescending(i => children[i].H)
                .ThenByDescending(i => children[i].W)
                .ToList();

            var rows       = new List<List<int>>();
            var rowWidths  = new List<double>();
            var rowHeights = new List<double>();
            var current    = new List<int>();
            double curW = 0, curH = 0;

            foreach (int idx in order)
            {
                double w = children[idx].W;
                double prospective = current.Count == 0 ? w : curW + gap + w;
                if (current.Count > 0 && prospective > areaW + eps)
                {
                    rows.Add(current); rowWidths.Add(curW); rowHeights.Add(curH);
                    current = new List<int>(); curW = 0; curH = 0;
                    prospective = w;
                }
                current.Add(idx);
                curW = prospective;
                curH = Math.Max(curH, children[idx].H);
            }
            if (current.Count > 0) { rows.Add(current); rowWidths.Add(curW); rowHeights.Add(curH); }

            double blockH = rowHeights.Sum() + gap * Math.Max(0, rows.Count - 1);

            // Center the row block vertically in the band (band bottom = area bottom),
            // each row horizontally, each item vertically within its row.
            double topY = (Math.Max(bandH, 0) + blockH) / 2.0;

            var placements = new SheetLayoutPacker.Placement[children.Count];
            double cursorTop = topY;
            for (int r = 0; r < rows.Count; r++)
            {
                double rowMidY = cursorTop - rowHeights[r] / 2.0;
                double cursorX = (areaW - rowWidths[r]) / 2.0;
                foreach (int idx in rows[r])
                {
                    double w = children[idx].W;
                    placements[idx] = new SheetLayoutPacker.Placement(cursorX + w / 2.0, rowMidY);
                    cursorX += w + gap;
                }
                cursorTop -= rowHeights[r] + gap;
            }

            double maxRowW  = rowWidths.Count > 0 ? rowWidths.Max() : 0;
            bool   overflow = parent.W > areaW + eps || parent.H > areaH + eps;
            if (children.Count > 0)
                overflow |= bandH <= eps || blockH > bandH + eps || maxRowW > areaW + eps;

            double badness = Math.Max(parent.W / areaW, parent.H / areaH);
            if (children.Count > 0)
                badness = Math.Max(badness,
                    Math.Max(blockH / Math.Max(bandH, 1e-6), maxRowW / areaW));

            return (new Result(parentPlace, placements, overflow, parentOnTop: true), badness);
        }

        // ── Source left-center, children in aligned columns to the right ─────
        private static (Result, double) PackLeftBand(
            SheetLayoutPacker.Rect parent, IReadOnlyList<SheetLayoutPacker.Rect> children,
            double areaW, double areaH, double gap)
        {
            const double eps = 1e-9;
            var parentPlace = new SheetLayoutPacker.Placement(parent.W / 2.0, areaH / 2.0);
            double bandW  = areaW - parent.W - gap;
            double bandX0 = parent.W + gap;

            // Widest-first so the items sharing a column have similar widths and read aligned.
            var order = Enumerable.Range(0, children.Count)
                .OrderByDescending(i => children[i].W)
                .ThenByDescending(i => children[i].H)
                .ToList();

            var cols       = new List<List<int>>();
            var colWidths  = new List<double>();
            var colHeights = new List<double>();
            var current    = new List<int>();
            double curW = 0, curH = 0;

            foreach (int idx in order)
            {
                double h = children[idx].H;
                double prospective = current.Count == 0 ? h : curH + gap + h;
                if (current.Count > 0 && prospective > areaH + eps)
                {
                    cols.Add(current); colWidths.Add(curW); colHeights.Add(curH);
                    current = new List<int>(); curW = 0; curH = 0;
                    prospective = h;
                }
                current.Add(idx);
                curH = prospective;
                curW = Math.Max(curW, children[idx].W);
            }
            if (current.Count > 0) { cols.Add(current); colWidths.Add(curW); colHeights.Add(curH); }

            double blockW = colWidths.Sum() + gap * Math.Max(0, cols.Count - 1);

            // Center the column block horizontally in the band, each column vertically
            // in the full area height, each item horizontally within its column.
            double leftX = bandX0 + (Math.Max(bandW, 0) - blockW) / 2.0;

            var placements = new SheetLayoutPacker.Placement[children.Count];
            double cursorLeft = leftX;
            for (int c = 0; c < cols.Count; c++)
            {
                double colMidX   = cursorLeft + colWidths[c] / 2.0;
                double cursorTop = (areaH + colHeights[c]) / 2.0;
                foreach (int idx in cols[c])
                {
                    double h = children[idx].H;
                    placements[idx] = new SheetLayoutPacker.Placement(colMidX, cursorTop - h / 2.0);
                    cursorTop -= h + gap;
                }
                cursorLeft += colWidths[c] + gap;
            }

            double maxColH  = colHeights.Count > 0 ? colHeights.Max() : 0;
            bool   overflow = parent.W > areaW + eps || parent.H > areaH + eps;
            if (children.Count > 0)
                overflow |= bandW <= eps || blockW > bandW + eps || maxColH > areaH + eps;

            double badness = Math.Max(parent.W / areaW, parent.H / areaH);
            if (children.Count > 0)
                badness = Math.Max(badness,
                    Math.Max(blockW / Math.Max(bandW, 1e-6), maxColH / areaH));

            return (new Result(parentPlace, placements, overflow, parentOnTop: false), badness);
        }
    }
}
