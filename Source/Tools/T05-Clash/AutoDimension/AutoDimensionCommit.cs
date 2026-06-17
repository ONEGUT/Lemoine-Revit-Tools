using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Clash.AutoDimension.Resolvers;

namespace LemoineTools.Tools.Clash.AutoDimension
{
    /// <summary>Counts reported back from a commit.</summary>
    public struct CommitResult
    {
        public int Placed;
        public int DeletedPrior;
        public int StaleDeleted;   // prior owned dims whose target is not re-created this run
        public int Failures;
    }

    /// <summary>
    /// Dumb consumer of an approved <see cref="Core.DimensionPlan"/>. Inside the caller's single
    /// transaction it (1) deletes all prior engine-owned dimensions in the view — clear-then-
    /// replace, reporting stale targets — then (2) places the freshly computed dimensions and
    /// stamps each with the owner entity. Never touches a dimension the engine does not own.
    /// </summary>
    public static class AutoDimensionCommit
    {
        public static CommitResult Commit(
            Document doc, View view, EngineOutput output, Action<string, string> log)
        {
            log = log ?? ((a, b) => { });
            var result = new CommitResult();
            var plan = output.Plan;

            // ── 1. Clear-then-replace: delete prior owned dimensions ──────────
            var newTargetKeys = new HashSet<string>(plan.Dimensions.Select(d => d.TargetKey));
            List<Dimension> prior;
            try
            {
                prior = new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(Dimension)).WhereElementIsNotElementType()
                    .Cast<Dimension>()
                    .Where(AutoDimOwnerSchema.IsOwned)
                    .OrderBy(d => d.Id.IntegerValue)
                    .ToList();
            }
            catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionCommit: collect prior owned dims", ex); prior = new List<Dimension>(); }

            foreach (var d in prior)
            {
                bool stale = !newTargetKeys.Contains(AutoDimOwnerSchema.ReadTargetKey(d));
                try
                {
                    if (doc.Delete(d.Id).Count > 0)
                    {
                        result.DeletedPrior++;
                        if (stale) result.StaleDeleted++;
                    }
                }
                catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionCommit: delete prior owned dim", ex); }
            }
            if (result.DeletedPrior > 0)
                log($"Cleared {result.DeletedPrior} prior auto-dimension(s) ({result.StaleDeleted} stale).", "info");

            // ── 2. Place the new plan ─────────────────────────────────────────
            if (plan.Dimensions.Count > 0)
                log($"Placing {plan.Dimensions.Count} dimension(s) in '{view.Name}'…", "info");

            int processed = 0;
            // Realized text-box footprints (view-2D) of every moved tag placed so far, so a later
            // tag can be slid sideways until it clears earlier ones — cross-dimension clash avoidance.
            var placedTags = new List<Core.Box2>();
            // Sibling dimension geometry (view-2D). This run's own new dims are NOT in the obstacle
            // set — that was collected before any were placed — so moved tags would otherwise land on
            // other strings' dimension/witness LINES. Collect every dim's line + witnesses once so the
            // column placer can slide a tag clear of them, not just clear of boxes.
            var staticLines = new List<OwnedSeg>();
            foreach (var d in plan.Dimensions)
            {
                var an = d.Anatomy ?? (d.Anatomy = Core.DimAnatomy.Build(d, output.CoreConfig));
                staticLines.Add(new OwnedSeg(d, an.DimLine));
                foreach (var w in an.Witnesses) staticLines.Add(new OwnedSeg(d, w));
            }
            foreach (var pd in plan.Dimensions)
            {
                if (++processed % 200 == 0)
                    log($"  …{processed}/{plan.Dimensions.Count} dimension(s) committed", "info");

                // Axis/perp are per-dimension now that X and Y strings coexist in one plan.
                Core.Vec2 axis = pd.AxisDir.Normalized();
                Core.Vec2 perp = axis.Perp();

                if (!output.Refs.TryGetValue(pd.SourceKey, out var bundle) || bundle?.Ordered == null || bundle.Ordered.Count < 2)
                {
                    result.Failures++;
                    log($"Source {pd.SourceKey} → {pd.TargetKey}: missing reference bundle ({bundle?.Ordered?.Count ?? 0} ref) — skipped.", "fail");
                    continue;
                }

                double sign = pd.Side == Core.DimSide.Positive ? 1.0 : -1.0;
                Core.Vec2 off = perp * (sign * pd.OffsetFt);
                XYZ p0 = output.Projection.From2D(pd.SourcePoint + off);
                XYZ p1 = output.Projection.From2D(pd.TargetPoint + off);

                // Revit rejects any curve below ShortCurveTolerance — guard against that exact bound
                // (the old 1e-4 ft guard let sub-tolerance lines through, throwing on CreateBound).
                double lenFt   = p0.DistanceTo(p1);
                double shortFt = doc.Application.ShortCurveTolerance;
                if (lenFt < shortFt)
                {
                    result.Failures++;
                    log($"Source {pd.SourceKey} → {pd.TargetKey}: dimension too short ({lenFt * 304.8:0.#} mm < {shortFt * 304.8:0.##} mm tol) — skipped.", "fail");
                    continue;
                }

                try
                {
                    var line = Line.CreateBound(p0, p1);
                    var refArray = new ReferenceArray();
                    foreach (var r in bundle.Ordered) refArray.Append(r);   // sources… then target → native chain

                    Dimension? dim = output.DimTypeId != ElementId.InvalidElementId
                        ? doc.Create.NewDimension(view, line, refArray, (DimensionType)doc.GetElement(output.DimTypeId))
                        : doc.Create.NewDimension(view, line, refArray);

                    if (dim == null)
                    {
                        result.Failures++;
                        log($"Source {pd.SourceKey} → {pd.TargetKey}: NewDimension returned null ({bundle.Ordered.Count} refs) — the target edge may not be visible/dimensionable in this view (e.g. a linked element not shown by the host view).", "fail");
                        continue;
                    }

                    if (!AutoDimOwnerSchema.Stamp(dim, plan.RunId, pd.TargetKey))
                    {
                        // Placed but not reclaimable next run — surface it.
                        result.Failures++;
                        log($"Source {pd.SourceKey}: placed but owner stamp failed (won't be reclaimed next run).", "fail");
                        continue;
                    }

                    ApplyTextStates(dim, pd, output.Projection, axis, perp, output.CoreConfig,
                                    placedTags, output.Obstacles, staticLines, log);
                    result.Placed++;
                }
                catch (Exception ex)
                {
                    result.Failures++;
                    LemoineLog.Error("AutoDimensionCommit: place dimension", ex);
                    log($"Source {pd.SourceKey} → {pd.TargetKey} ({bundle.Ordered.Count} refs, {lenFt * 304.8:0} mm, axis {(Math.Abs(axis.X) >= Math.Abs(axis.Y) ? "x" : "y")}): {ex.Message}", "fail");
                }
            }

            return result;
        }

        /// <summary>
        /// Realizes the layout's per-segment text decisions on the placed Revit dimension. A run of
        /// adjacent moved tags is relocated as a single aligned COLUMN just past the group's end in
        /// the planned direction (PlannedDimension.TagColumnDir: +axis, or mirrored to -axis when
        /// the layout found that side clearer):
        /// each tag is shifted to the side, then stacked perpendicular (UP when the dimension line is
        /// on the positive/above side, DOWN when Flipped/below) so crowded values pile straight on top
        /// of one another while their arc leaders swing out to the side. The nearest anchor sits lowest
        /// and each farther one a row higher, so the leaders nest and never cross; each tag also clears
        /// every tag already placed this run (cross-dimension) AND every pre-existing annotation
        /// (the engine's obstacle set). The column geometry (base/step/along multipliers) comes from
        /// <see cref="Core.LayoutConfig"/>, shared with the plan-time <see cref="Core.TagColumnPlanner"/>
        /// so what the scorer saw is what gets built. Every move is independently guarded.
        /// NOTE: the heights are Windows-tunable — verify the text clears the arc on a real plot.
        /// </summary>
        private const int    MaxColumnSteps   = 24;   // perpendicular clash-avoidance tries before giving up
        private const int    MaxAlongSteps    = 8;    // along-column nudges tried at each level before bumping out
        private const double GlyphWidthFactor = 0.6;  // average glyph advance as a fraction of text height

        /// <summary>
        /// Width (model ft) of a tag's actual displayed value text. Uses the real Revit ValueString
        /// (e.g. "0' - 11 5/8\"") so the column boxes and front-edge alignment match what's drawn,
        /// rather than the plan-time decimal estimate which badly under-counted imperial glyphs.
        /// Falls back to the planned estimate if the value string isn't available yet.
        /// </summary>
        private static double TagWidthFt(string? valueString, double fallbackFt, double th)
        {
            if (string.IsNullOrEmpty(valueString)) return fallbackFt;
            int chars = Math.Max(3, valueString.Length);
            return chars * th * GlyphWidthFactor;
        }

        private static void ApplyTextStates(
            Dimension dim, Core.PlannedDimension pd, Resolvers.ViewProjection projection,
            Core.Vec2 axis, Core.Vec2 perp, Core.LayoutConfig cfg, List<Core.Box2> placedTags,
            IReadOnlyList<Core.Box2> obstacles, IReadOnlyList<OwnedSeg> lines, Action<string, string> log)
        {
            double th = cfg.TextHeightFt;
            if (th <= 0) return;

            // World perpendicular (outward, on the offset side) and axial (the reading direction).
            XYZ worldPerp, worldAxis;
            try
            {
                XYZ origin = projection.From2D(new Core.Vec2(0, 0));
                worldPerp = projection.From2D(perp) - origin;
                worldAxis = projection.From2D(axis) - origin;
                if (worldPerp.GetLength() < 1e-9 || worldAxis.GetLength() < 1e-9) return;
                worldPerp = worldPerp.Normalize();
                worldAxis = worldAxis.Normalize();
            }
            catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionCommit: tag directions", ex); return; }

            // Tags stack on the dimension's own side: above for a positive-side string,
            // below for a negative-side (flipped) one — matching the value text's side.
            double sign = pd.Side == Core.DimSide.Positive ? 1.0 : -1.0;

            // Multi-segment chain → per-segment text; single span → the whole-dimension text.
            DimensionSegmentArray? segs = null;
            try { segs = dim.Segments; }
            catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionCommit: read segments", ex); }

            if (segs != null && segs.Size > 0)
            {
                // Walk segments, gathering maximal runs of consecutive moved tags. A non-moved
                // (inline/fitting) segment breaks the run, so a spread-out dimension yields several
                // small columns instead of one. Each run is then placed as an aligned column.
                int n = Math.Min(segs.Size, pd.Segments.Count);
                var run = new List<ColumnTag>();
                for (int k = 0; k <= n; k++)
                {
                    if (k < n && IsMoved(pd.Segments[k].TextState))
                    {
                        DimensionSegment? seg = null;
                        XYZ? def = null;
                        string? val = null;
                        try { seg = segs.get_Item(k); def = seg?.TextPosition; val = seg?.ValueString; }
                        catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionCommit: read segment text", ex); }
                        if (seg != null && def != null)
                        {
                            DimensionSegment captured = seg;
                            double w = TagWidthFt(val, pd.Segments[k].TextWidthFt, th);
                            run.Add(new ColumnTag(def, pd.Segments[k], w, pos =>
                            {
                                try { captured.TextPosition = pos; }
                                catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionCommit: nudge segment text", ex); }
                            }));
                            continue;
                        }
                    }
                    if (run.Count > 0)
                    {
                        PlaceColumn(run, worldPerp, worldAxis, sign, pd.TagColumnDir, th, cfg, projection, axis, perp, placedTags, obstacles, lines, pd);
                        run.Clear();
                    }
                }
            }
            else if (pd.Segments.Count > 0 && IsMoved(pd.Segments[0].TextState))
            {
                try
                {
                    XYZ? def = dim.TextPosition;
                    if (def != null)
                    {
                        double w = TagWidthFt(dim.ValueString, pd.Segments[0].TextWidthFt, th);
                        var single = new List<ColumnTag>
                        {
                            new ColumnTag(def, pd.Segments[0], w, pos =>
                            {
                                try { dim.TextPosition = pos; }
                                catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionCommit: nudge dimension text", ex); }
                            }),
                        };
                        PlaceColumn(single, worldPerp, worldAxis, sign, pd.TagColumnDir, th, cfg, projection, axis, perp, placedTags, obstacles, lines, pd);
                    }
                }
                catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionCommit: nudge dimension text", ex); }
            }
        }

        /// <summary>One moved tag: its natural (segment-centre) position, the planned segment it
        /// belongs to, and a setter that writes the chosen world position back to Revit.</summary>
        private sealed class ColumnTag
        {
            public readonly XYZ DefaultPos;
            public readonly Core.PlannedSegment Ps;
            public readonly double Width;          // actual displayed-text width (model ft)
            public readonly Action<XYZ> SetPos;
            public ColumnTag(XYZ defaultPos, Core.PlannedSegment ps, double width, Action<XYZ> setPos)
            { DefaultPos = defaultPos; Ps = ps; Width = width; SetPos = setPos; }
        }

        /// <summary>
        /// Places a run of adjacent moved tags as one aligned column. Every tag's FRONT (arc-side)
        /// edge lands on a common line — wider tags extend further back (+axis) rather than spreading
        /// their centres — and that front line sits just past the group's furthest dimension edge (a
        /// moved segment's far witness), so the whole column clears the dimension. Tags then stack
        /// perpendicular from a base clearance, nearest anchor lowest, each subsequent one a row higher
        /// (bumped further if it still hits an already-placed tag). The perpendicular sign follows the
        /// dimension side, so an above run climbs up and a Flipped/below run mirrors downward.
        /// </summary>
        private static void PlaceColumn(
            List<ColumnTag> run, XYZ worldPerp, XYZ worldAxis, double sign, int colDir, double th,
            Core.LayoutConfig cfg, Resolvers.ViewProjection projection,
            Core.Vec2 axis, Core.Vec2 perp, List<Core.Box2> placedTags, IReadOnlyList<Core.Box2> obstacles,
            IReadOnlyList<OwnedSeg> lines, Core.PlannedDimension self)
        {
            if (run.Count == 0 || th <= 0) return;

            double dir = colDir < 0 ? -1.0 : 1.0;                  // planned column direction
            double Axial(XYZ p) => projection.To2D(p).Dot(axis);   // reading-direction coordinate

            // Reference point on the dimension line (any tag centre works — the line is straight along
            // worldAxis), plus the group's furthest dimension edge IN THE COLUMN DIRECTION: the far
            // witness of the dir-most moved segment = its centre + dir * half its measured span.
            ColumnTag refTag = run[0];
            double refAxial  = Axial(refTag.DefaultPos);
            double groupEdge = dir > 0 ? double.MinValue : double.MaxValue;
            foreach (var t in run)
            {
                double edge = Axial(t.DefaultPos) + dir * t.Ps.LengthFt * 0.5;
                if (dir > 0 ? edge > groupEdge : edge < groupEdge) groupEdge = edge;
            }
            // Every tag's front edge aligns here, offset just past the dimension's furthest edge.
            double frontLine = groupEdge + dir * cfg.TagColumnAlongHeights * th;

            // Nearest anchor to the column lowest → farthest highest, so the arcs nest without
            // crossing; mirrored when the column hangs off the other (-axis) end.
            var ordered = (dir > 0
                ? run.OrderByDescending(t => Axial(t.DefaultPos))
                : run.OrderBy(t => Axial(t.DefaultPos))).ToList();

            // Below-line tags need an extra downward push: Revit anchors dimension value text near
            // its baseline, so a below tag at the same magnitude as an above tag renders hard against
            // the line (it appears to move only sideways). ⚠ tunable — verify on a Windows plot.
            double baseLevel = cfg.TagColumnBaseHeights * th;
            if (sign < 0) baseLevel += th;
            double step  = cfg.TagColumnStepHeights * th;

            double level = baseLevel;
            foreach (var t in ordered)
            {
                double halfAlong = Math.Max(t.Width, th) * 0.5;
                double halfPerp  = th * 0.5;
                double halfX = Math.Abs(axis.X) * halfAlong + Math.Abs(perp.X) * halfPerp;
                double halfY = Math.Abs(axis.Y) * halfAlong + Math.Abs(perp.Y) * halfPerp;

                // Centre this tag so its front (arc-side) edge sits on frontLine: push the centre
                // away by the tag's own half-width in the column direction. All front edges then
                // line up regardless of text length.
                double along = (frontLine - refAxial) + dir * halfAlong;
                XYZ baseOnLine = refTag.DefaultPos + worldAxis * along;

                // Keep the tag as near the line as possible and slide it ALONG the column to clear
                // already-placed tags, pre-existing annotations, AND sibling dimension/witness LINES —
                // bumping perpendicular only when no along-position at the current level is clear.
                // Moving the dragged text (along) is preferred over stacking it further out, so it
                // stops covering other strings' lines.
                XYZ pos = baseOnLine + worldPerp * (sign * level);
                Core.Box2 box = Core.Box2.FromCenter(projection.To2D(pos), halfX, halfY);
                bool clear = false;
                for (int p = 0; p < MaxColumnSteps && !clear; p++)
                {
                    double lvl = level + p * step;
                    for (int a = 0; a <= MaxAlongSteps && !clear; a++)
                    {
                        XYZ cand = baseOnLine + worldAxis * (dir * a * step) + worldPerp * (sign * lvl);
                        Core.Box2 cbox = Core.Box2.FromCenter(projection.To2D(cand), halfX, halfY);
                        if (!ClashesAny(cbox, placedTags, obstacles, lines, self))
                        {
                            pos = cand; box = cbox; clear = true;
                        }
                    }
                }
                t.SetPos(pos);   // ⚠ leader appearance depends on the DimensionType — verify on Windows
                placedTags.Add(box);
                level += step;    // next tag starts a row higher (the search still guarantees clearance)
            }
        }

        /// <summary>One sibling dimension's view-2D line (dimension line or a witness) plus the
        /// dimension it belongs to, so a tag never tries to avoid its OWN dimension's lines.</summary>
        private readonly struct OwnedSeg
        {
            public readonly Core.PlannedDimension Owner;
            public readonly Core.Seg2 Seg;
            public OwnedSeg(Core.PlannedDimension owner, Core.Seg2 seg) { Owner = owner; Seg = seg; }
        }

        /// <summary>True when a candidate tag box overlaps any already-placed tag, any pre-existing
        /// annotation obstacle, or any sibling dimension/witness line (its own dimension's lines are
        /// skipped — the tag is legitimately offset off its own line).</summary>
        private static bool ClashesAny(
            Core.Box2 box, List<Core.Box2> placedTags, IReadOnlyList<Core.Box2> obstacles,
            IReadOnlyList<OwnedSeg> lines, Core.PlannedDimension self)
        {
            for (int i = 0; i < placedTags.Count; i++)
                if (box.Intersects(placedTags[i])) return true;
            if (obstacles != null)
                for (int i = 0; i < obstacles.Count; i++)
                    if (box.Intersects(obstacles[i])) return true;
            if (lines != null)
                for (int i = 0; i < lines.Count; i++)
                {
                    if (ReferenceEquals(lines[i].Owner, self)) continue;
                    if (SegIntersectsBox(lines[i].Seg, box)) return true;
                }
            return false;
        }

        /// <summary>True when the segment touches the box: an endpoint inside, or the segment
        /// crossing a box edge. Mirrors the scorer's test so commit and plan agree.</summary>
        private static bool SegIntersectsBox(Core.Seg2 s, Core.Box2 b)
        {
            if (PointInBox(s.A, b) || PointInBox(s.B, b)) return true;
            var c00 = new Core.Vec2(b.MinX, b.MinY);
            var c10 = new Core.Vec2(b.MaxX, b.MinY);
            var c11 = new Core.Vec2(b.MaxX, b.MaxY);
            var c01 = new Core.Vec2(b.MinX, b.MaxY);
            return s.Crosses(new Core.Seg2(c00, c10)) || s.Crosses(new Core.Seg2(c10, c11))
                || s.Crosses(new Core.Seg2(c11, c01)) || s.Crosses(new Core.Seg2(c01, c00));
        }

        private static bool PointInBox(Core.Vec2 p, Core.Box2 b) =>
            p.X > b.MinX && p.X < b.MaxX && p.Y > b.MinY && p.Y < b.MaxY;

        private static bool IsMoved(Core.SegmentTextState s) =>
            s == Core.SegmentTextState.LeaderOut ||
            s == Core.SegmentTextState.Staggered ||
            s == Core.SegmentTextState.Flipped;
    }
}
