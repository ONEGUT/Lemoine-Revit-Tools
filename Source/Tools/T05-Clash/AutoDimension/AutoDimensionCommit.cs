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
                        log($"Source {pd.SourceKey} → {pd.TargetKey}: NewDimension returned null ({bundle.Ordered.Count} refs).", "fail");
                        continue;
                    }

                    if (!AutoDimOwnerSchema.Stamp(dim, plan.RunId, pd.TargetKey))
                    {
                        // Placed but not reclaimable next run — surface it.
                        result.Failures++;
                        log($"Source {pd.SourceKey}: placed but owner stamp failed (won't be reclaimed next run).", "fail");
                        continue;
                    }

                    ApplyTextStates(dim, pd, output.Projection, axis, perp, output.CoreConfig, placedTags, log);
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
        /// adjacent moved tags is relocated as a single aligned COLUMN just past the group's far end:
        /// each tag is shifted to the side, then stacked perpendicular (UP when the dimension line is
        /// on the positive/above side, DOWN when Flipped/below) so crowded values pile straight on top
        /// of one another while their arc leaders swing out to the side. The nearest anchor sits lowest
        /// and each farther one a row higher, so the leaders nest and never cross; each tag also clears
        /// every tag already placed this run (cross-dimension). Every move is independently guarded.
        /// NOTE: these heights are Windows-tunable — verify the text clears the arc on a real plot.
        /// </summary>
        private const double ColumnBasePerpHeights = 2.2;  // first clearance above/below the dimension arc
        private const double ColumnStepHeights     = 1.4;  // perpendicular step between stacked tags
        private const double AlongBaseHeights      = 0.75; // column offset past the group's far text edge
        private const int    MaxColumnSteps        = 24;   // perpendicular clash-avoidance tries before giving up

        private static void ApplyTextStates(
            Dimension dim, Core.PlannedDimension pd, Resolvers.ViewProjection projection,
            Core.Vec2 axis, Core.Vec2 perp, Core.LayoutConfig cfg, List<Core.Box2> placedTags,
            Action<string, string> log)
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
                        try { seg = segs.get_Item(k); def = seg?.TextPosition; }
                        catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionCommit: read segment text", ex); }
                        if (seg != null && def != null)
                        {
                            DimensionSegment captured = seg;
                            run.Add(new ColumnTag(def, pd.Segments[k], pos =>
                            {
                                try { captured.TextPosition = pos; }
                                catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionCommit: nudge segment text", ex); }
                            }));
                            continue;
                        }
                    }
                    if (run.Count > 0)
                    {
                        PlaceColumn(run, worldPerp, worldAxis, sign, th, projection, axis, perp, placedTags);
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
                        var single = new List<ColumnTag>
                        {
                            new ColumnTag(def, pd.Segments[0], pos =>
                            {
                                try { dim.TextPosition = pos; }
                                catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionCommit: nudge dimension text", ex); }
                            }),
                        };
                        PlaceColumn(single, worldPerp, worldAxis, sign, th, projection, axis, perp, placedTags);
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
            public readonly Action<XYZ> SetPos;
            public ColumnTag(XYZ defaultPos, Core.PlannedSegment ps, Action<XYZ> setPos)
            { DefaultPos = defaultPos; Ps = ps; SetPos = setPos; }
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
            List<ColumnTag> run, XYZ worldPerp, XYZ worldAxis, double sign, double th,
            Resolvers.ViewProjection projection, Core.Vec2 axis, Core.Vec2 perp, List<Core.Box2> placedTags)
        {
            if (run.Count == 0 || th <= 0) return;

            double Axial(XYZ p) => projection.To2D(p).Dot(axis);   // reading-direction coordinate

            // Reference point on the dimension line (any tag centre works — the line is straight along
            // worldAxis), plus the group's furthest dimension edge: the far witness of the +axis-most
            // moved segment = its centre + half its measured span.
            ColumnTag refTag = run[0];
            double refAxial  = Axial(refTag.DefaultPos);
            double groupFarEdge = double.MinValue;
            foreach (var t in run)
            {
                double edge = Axial(t.DefaultPos) + t.Ps.LengthFt * 0.5;
                if (edge > groupFarEdge) groupFarEdge = edge;
            }
            // Every tag's front edge aligns here, offset just past the dimension's furthest edge.
            double frontLine = groupFarEdge + AlongBaseHeights * th;

            // Nearest anchor (max axial) lowest → farthest highest, so the arcs nest without crossing.
            var ordered = run.OrderByDescending(t => Axial(t.DefaultPos)).ToList();

            double level = ColumnBasePerpHeights * th;
            double step  = ColumnStepHeights * th;
            foreach (var t in ordered)
            {
                double halfAlong = Math.Max(t.Ps.TextWidthFt, th) * 0.5;
                double halfPerp  = th * 0.5;
                double halfX = Math.Abs(axis.X) * halfAlong + Math.Abs(perp.X) * halfPerp;
                double halfY = Math.Abs(axis.Y) * halfAlong + Math.Abs(perp.Y) * halfPerp;

                // Centre this tag so its front (arc-side, -axis) edge sits on frontLine: push the centre
                // back by the tag's own half-width. All front edges then line up regardless of text length.
                double along = (frontLine - refAxial) + halfAlong;
                XYZ baseOnLine = refTag.DefaultPos + worldAxis * along;

                XYZ pos = baseOnLine;
                Core.Box2 box = default;
                for (int s = 0; s < MaxColumnSteps; s++)
                {
                    pos = baseOnLine + worldPerp * (sign * level);
                    box = Core.Box2.FromCenter(projection.To2D(pos), halfX, halfY);
                    bool clash = false;
                    for (int i = 0; i < placedTags.Count; i++)
                        if (box.Intersects(placedTags[i])) { clash = true; break; }
                    if (!clash) break;
                    level += step;
                }
                t.SetPos(pos);   // ⚠ leader appearance depends on the DimensionType — verify on Windows
                placedTags.Add(box);
                level += step;    // next tag at least one row higher (monotonic → leaders never cross)
            }
        }

        private static bool IsMoved(Core.SegmentTextState s) =>
            s == Core.SegmentTextState.LeaderOut ||
            s == Core.SegmentTextState.Staggered ||
            s == Core.SegmentTextState.Flipped;
    }
}
