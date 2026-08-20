using System;
using System.Collections.Generic;

namespace LemoineTools.Framework.Zones
{
    // =========================================================================
    // ZoneGroupSolver — where each area in a group lands on ONE sheet.
    //
    // A group of one area is the ordinary case (a sheet per area). A group of
    // several is a composite sheet: one level split across two or more views.
    //
    // The rule that makes composites work, and the reason this is not a
    // rectangle-packing problem:
    //
    //   A GROUP IS SOLVED AS ONE UNIT, AT ONE SHARED SCALE, PRESERVING THE
    //   AREAS' TRUE WORLD-RELATIVE POSITIONS.
    //
    // Union the areas' world extents, fit THAT to the drawing area to get one
    // scale, place the union, then offset each area by its real world position
    // divided by that scale. Three things follow for free:
    //
    //   • Matchlines meet exactly. The east edge of Area A and the west edge of
    //     Area B are the same world line, so at a shared scale with true offsets
    //     they coincide on paper. A packer positions by rectangle, not geometry,
    //     and cannot promise this.
    //   • Views cannot overlap, because the areas do not overlap in the world.
    //   • The shared scale is guaranteed, which a split floor plan needs in order
    //     to read as one drawing.
    //
    // Packed composition exists for scope boxes that deliberately overlap for
    // matchline context, where true continuity would draw the overlap band twice.
    // It breaks matchline alignment, so it is never the default.
    //
    // Revit-free by design — see ZoneScaleFit for why that matters here.
    //
    // Coordinates: world values are feet; sheet values are ABSOLUTE sheet feet,
    // the same space Viewport.SetBoxCenter consumes. paperFeet = modelFeet/Scale.
    // =========================================================================
    public static class ZoneGroupSolver
    {
        /// <summary>One area entering the solve. World feet.</summary>
        public sealed class AreaInput
        {
            public string AreaId  { get; set; } = "";
            public string Label   { get; set; } = "";
            public double MinX    { get; set; }
            public double MinY    { get; set; }
            public double MaxX    { get; set; }
            public double MaxY    { get; set; }
            /// <summary>The area's world anchor — the point pinned to the sheet.</summary>
            public double AnchorX { get; set; }
            public double AnchorY { get; set; }

            public double WidthFt => MaxX - MinX;
            public double DepthFt => MaxY - MinY;
        }

        /// <summary>The usable drawing area, in absolute sheet feet (title block minus margins).</summary>
        public sealed class DrawingArea
        {
            public double MinX { get; set; }
            public double MinY { get; set; }
            public double MaxX { get; set; }
            public double MaxY { get; set; }

            public double WidthFt  => MaxX - MinX;
            public double HeightFt => MaxY - MinY;
            public double CentreX  => (MinX + MaxX) / 2.0;
            public double CentreY  => (MinY + MaxY) / 2.0;

            public static DrawingArea FromSize(double widthFt, double heightFt,
                                               double marginL, double marginR,
                                               double marginB, double marginT)
                => new DrawingArea
                {
                    MinX = marginL,
                    MinY = marginB,
                    MaxX = Math.Max(marginL, widthFt  - marginR),
                    MaxY = Math.Max(marginB, heightFt - marginT),
                };
        }

        /// <summary>Where one area ended up, in absolute sheet feet.</summary>
        public sealed class Placed
        {
            public string AreaId { get; set; } = "";
            public string Label  { get; set; } = "";
            /// <summary>The area's footprint on the sheet.</summary>
            public double FootMinX { get; set; }
            public double FootMinY { get; set; }
            public double FootMaxX { get; set; }
            public double FootMaxY { get; set; }
            /// <summary>Sheet coordinate the area's world anchor must occupy — the stored value.</summary>
            public double AnchorSheetX { get; set; }
            public double AnchorSheetY { get; set; }
            /// <summary>Echoed back so a caller writing a placement record has the pair in hand.</summary>
            public double AnchorWorldX { get; set; }
            public double AnchorWorldY { get; set; }

            public double WidthFt  => FootMaxX - FootMinX;
            public double HeightFt => FootMaxY - FootMinY;
        }

        /// <summary>Two areas whose footprints collide on the sheet.</summary>
        public sealed class Overlap
        {
            public string AreaIdA { get; set; } = "";
            public string AreaIdB { get; set; } = "";
            public string LabelA  { get; set; } = "";
            public string LabelB  { get; set; } = "";
            /// <summary>Size of the collision in paper feet — how badly they intersect.</summary>
            public double OverlapWidthFt  { get; set; }
            public double OverlapHeightFt { get; set; }
        }

        public sealed class Result
        {
            public int  Scale { get; set; }
            /// <summary>False when the arrangement overflows the drawing area at every scale tried.</summary>
            public bool Fits  { get; set; }
            /// <summary>True when the group carried no areas — a configuration error, not a layout one.</summary>
            public bool IsEmpty { get; set; }
            public List<Placed>  Items    { get; set; } = new List<Placed>();
            /// <summary>Empty on a well-formed Continuous group. Never suppressed.</summary>
            public List<Overlap> Overlaps { get; set; } = new List<Overlap>();
            /// <summary>Paper feet left over around the arrangement. Negative means overflow.</summary>
            public double SlackXFt { get; set; }
            public double SlackYFt { get; set; }
            /// <summary>Which composition actually produced this result.</summary>
            public string Composition { get; set; } = ZoneComposition.Continuous;
        }

        /// <summary>
        /// Solves a group. <paramref name="fixedScale"/> of 0 means "solve the scale"; any other
        /// value is used verbatim and <see cref="Result.Fits"/> reports whether it worked.
        /// </summary>
        public static Result Solve(IReadOnlyList<AreaInput>? areas,
                                   DrawingArea area,
                                   string? composition = ZoneComposition.Continuous,
                                   double gapPaperFt = 0.0,
                                   int fixedScale = 0,
                                   int[]? ladder = null)
        {
            var result = new Result
            {
                Composition = composition == ZoneComposition.Packed
                    ? ZoneComposition.Packed
                    : ZoneComposition.Continuous,
            };

            if (areas == null || areas.Count == 0)
            {
                // A group with no areas cannot be laid out. Say so explicitly rather than
                // returning a plausible-looking empty success.
                result.IsEmpty = true;
                result.Fits    = false;
                result.Scale   = fixedScale > 0 ? fixedScale : 96;
                return result;
            }

            if (area == null) area = DrawingArea.FromSize(1, 1, 0, 0, 0, 0);

            result = result.Composition == ZoneComposition.Packed
                ? SolvePacked(areas, area, gapPaperFt, fixedScale, ladder)
                : SolveContinuous(areas, area, fixedScale, ladder);

            result.Overlaps = FindOverlaps(result.Items);
            return result;
        }

        // ── Continuous: true world-relative geometry ─────────────────────────
        private static Result SolveContinuous(IReadOnlyList<AreaInput> areas, DrawingArea area,
                                              int fixedScale, int[]? ladder)
        {
            Bounds u = Union(areas);

            int scale = fixedScale > 0
                ? fixedScale
                : ZoneScaleFit.Solve(u.Width, u.Depth, area.WidthFt, area.HeightFt, ladder).Scale;
            if (scale <= 0) scale = 96;

            double paperW = u.Width / scale;
            double paperH = u.Depth / scale;

            // Centre the union in the drawing area. Every area then sits at its true offset
            // from the union's corner, so the group reads as one continuous drawing.
            double originX = area.CentreX - paperW / 2.0;
            double originY = area.CentreY - paperH / 2.0;

            var res = new Result
            {
                Scale       = scale,
                Composition = ZoneComposition.Continuous,
                SlackXFt    = area.WidthFt  - paperW,
                SlackYFt    = area.HeightFt - paperH,
                Fits        = paperW <= area.WidthFt + Eps && paperH <= area.HeightFt + Eps,
            };

            foreach (var a in areas)
            {
                if (a == null) continue;
                double fx = originX + (a.MinX - u.MinX) / scale;
                double fy = originY + (a.MinY - u.MinY) / scale;
                res.Items.Add(Place(a, fx, fy, scale));
            }
            return res;
        }

        // ── Packed: reading order, gap-separated ─────────────────────────────
        private static Result SolvePacked(IReadOnlyList<AreaInput> areas, DrawingArea area,
                                          double gap, int fixedScale, int[]? ladder)
        {
            if (gap < 0) gap = 0;

            // Packing depends on the scale and the scale depends on the packing, so start from
            // the scale the union alone suggests and step DOWN the ladder until the packed
            // arrangement fits. Bounded by the ladder, so it always terminates.
            Bounds u = Union(areas);
            int scale = fixedScale > 0
                ? fixedScale
                : ZoneScaleFit.Solve(u.Width, u.Depth, area.WidthFt, area.HeightFt, ladder).Scale;
            if (scale <= 0) scale = 96;

            Result attempt = PackAt(areas, area, gap, scale);
            if (fixedScale <= 0)
            {
                int guard = 0;
                while (!attempt.Fits && guard++ < 32)
                {
                    int next = ZoneScaleFit.NextSmaller(scale, ladder);
                    if (next <= 0) break;               // bottom of the ladder — report the overflow
                    scale   = next;
                    attempt = PackAt(areas, area, gap, scale);
                }
            }
            return attempt;
        }

        /// <summary>
        /// Row-packs the areas at one scale, in plan reading order (north to south, then west to
        /// east) so the sheet layout still corresponds to the building rather than to rectangle
        /// sizes.
        /// </summary>
        private static Result PackAt(IReadOnlyList<AreaInput> areas, DrawingArea area,
                                     double gap, int scale)
        {
            var ordered = new List<AreaInput>();
            foreach (var a in areas) if (a != null) ordered.Add(a);
            ordered.Sort((p, q) =>
            {
                int byNorth = q.MaxY.CompareTo(p.MaxY);      // northernmost first
                if (byNorth != 0) return byNorth;
                return p.MinX.CompareTo(q.MinX);             // then westernmost
            });

            var rows       = new List<List<AreaInput>>();
            var rowWidths  = new List<double>();
            var rowHeights = new List<double>();
            var current    = new List<AreaInput>();
            double curW = 0, curH = 0;

            foreach (var a in ordered)
            {
                double w = a.WidthFt / scale;
                double h = a.DepthFt / scale;
                double prospective = current.Count == 0 ? w : curW + gap + w;
                if (current.Count > 0 && prospective > area.WidthFt + Eps)
                {
                    rows.Add(current); rowWidths.Add(curW); rowHeights.Add(curH);
                    current = new List<AreaInput>(); curW = 0; curH = 0;
                    prospective = w;
                }
                current.Add(a);
                curW = prospective;
                curH = Math.Max(curH, h);
            }
            if (current.Count > 0) { rows.Add(current); rowWidths.Add(curW); rowHeights.Add(curH); }

            double blockH = 0;
            foreach (double h in rowHeights) blockH += h;
            blockH += gap * Math.Max(0, rows.Count - 1);

            double maxRowW = 0;
            foreach (double w in rowWidths) if (w > maxRowW) maxRowW = w;

            var res = new Result
            {
                Scale       = scale,
                Composition = ZoneComposition.Packed,
                SlackXFt    = area.WidthFt  - maxRowW,
                SlackYFt    = area.HeightFt - blockH,
                Fits        = maxRowW <= area.WidthFt + Eps && blockH <= area.HeightFt + Eps,
            };

            // Block centred vertically; each row centred horizontally; items sit on their row's
            // top edge so a row of mixed depths still aligns along the top, as a plan set reads.
            double cursorTop = area.CentreY + blockH / 2.0;
            for (int r = 0; r < rows.Count; r++)
            {
                double cursorX = area.CentreX - rowWidths[r] / 2.0;
                foreach (var a in rows[r])
                {
                    double w = a.WidthFt / scale;
                    double h = a.DepthFt / scale;
                    res.Items.Add(Place(a, cursorX, cursorTop - h, scale));
                    cursorX += w + gap;
                }
                cursorTop -= rowHeights[r] + gap;
            }
            return res;
        }

        // ── Shared ───────────────────────────────────────────────────────────

        private const double Eps = 1e-9;

        /// <summary>
        /// Turns a footprint origin into a placement. The anchor keeps its position WITHIN the
        /// area's own extents, so wherever the footprint lands the anchored world point lands
        /// with it — which is what makes the stored pair reusable on any sheet of that size.
        /// </summary>
        private static Placed Place(AreaInput a, double footMinX, double footMinY, int scale)
            => new Placed
            {
                AreaId   = a.AreaId,
                Label    = a.Label,
                FootMinX = footMinX,
                FootMinY = footMinY,
                FootMaxX = footMinX + a.WidthFt / scale,
                FootMaxY = footMinY + a.DepthFt / scale,
                AnchorSheetX = footMinX + (a.AnchorX - a.MinX) / scale,
                AnchorSheetY = footMinY + (a.AnchorY - a.MinY) / scale,
                AnchorWorldX = a.AnchorX,
                AnchorWorldY = a.AnchorY,
            };

        private struct Bounds
        {
            public double MinX, MinY, MaxX, MaxY;
            public double Width => MaxX - MinX;
            public double Depth => MaxY - MinY;
        }

        private static Bounds Union(IReadOnlyList<AreaInput> areas)
        {
            var b = new Bounds
            {
                MinX = double.MaxValue, MinY = double.MaxValue,
                MaxX = double.MinValue, MaxY = double.MinValue,
            };
            bool any = false;
            foreach (var a in areas)
            {
                if (a == null) continue;
                any = true;
                if (a.MinX < b.MinX) b.MinX = a.MinX;
                if (a.MinY < b.MinY) b.MinY = a.MinY;
                if (a.MaxX > b.MaxX) b.MaxX = a.MaxX;
                if (a.MaxY > b.MaxY) b.MaxY = a.MaxY;
            }
            if (!any) { b.MinX = b.MinY = b.MaxX = b.MaxY = 0; }
            return b;
        }

        /// <summary>
        /// Every pair of colliding footprints. On a Continuous group this should be empty — a
        /// hit means the source areas overlap in the world, which is a planning error worth
        /// surfacing at review time rather than as two drawings printed on top of each other.
        /// </summary>
        public static List<Overlap> FindOverlaps(IReadOnlyList<Placed>? items)
        {
            var list = new List<Overlap>();
            if (items == null) return list;

            // A shared edge is not an overlap — adjacent scope boxes normally touch exactly.
            const double touchTol = 1e-6;

            for (int i = 0; i < items.Count; i++)
            for (int j = i + 1; j < items.Count; j++)
            {
                var a = items[i];
                var b = items[j];
                if (a == null || b == null) continue;

                double ox = Math.Min(a.FootMaxX, b.FootMaxX) - Math.Max(a.FootMinX, b.FootMinX);
                double oy = Math.Min(a.FootMaxY, b.FootMaxY) - Math.Max(a.FootMinY, b.FootMinY);
                if (ox <= touchTol || oy <= touchTol) continue;

                list.Add(new Overlap
                {
                    AreaIdA = a.AreaId, AreaIdB = b.AreaId,
                    LabelA  = a.Label,  LabelB  = b.Label,
                    OverlapWidthFt = ox, OverlapHeightFt = oy,
                });
            }
            return list;
        }
    }
}
