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
        /// Realizes the layout's per-segment text decisions on the placed Revit dimension. A moved
        /// (cramped) tag is pushed OUT perpendicular (to clear the leader arc) AND sideways along the
        /// dimension axis, so the value sits beside the segment centre — on the side the arc sweeps to —
        /// instead of straight above it. Before committing each tag, its realized text box is tested
        /// against every tag already placed this run and slid further along-axis until it clears, so
        /// neighbouring arced tags no longer overlap. Each move is independently guarded.
        /// NOTE: these heights are Windows-tunable — verify the text clears the arc on a real plot.
        /// </summary>
        private const double StaggerNudgeHeights = 2.2;  // perpendicular nudge for a staggered tag (clears the arc)
        private const double LeaderNudgeHeights  = 3.0;  // perpendicular nudge for a leadered tag (now rare)
        private const double AlongBaseHeights    = 0.75; // sideways offset past the text's own half-width
        private const int    MaxAlongSteps       = 12;   // clash-avoidance tries before giving up

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
                int n = Math.Min(segs.Size, pd.Segments.Count);
                for (int k = 0; k < n; k++)
                {
                    var ps = pd.Segments[k];
                    if (!IsMoved(ps.TextState)) continue;
                    try
                    {
                        var seg = segs.get_Item(k);
                        if (seg?.TextPosition == null) continue;
                        seg.TextPosition = PlaceTag(seg.TextPosition, ps, worldPerp, worldAxis, sign, th,
                                                    projection, axis, perp, placedTags);
                    }
                    catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionCommit: nudge segment text", ex); }
                }
            }
            else if (pd.Segments.Count > 0 && IsMoved(pd.Segments[0].TextState))
            {
                try
                {
                    if (dim.TextPosition != null)
                        dim.TextPosition = PlaceTag(dim.TextPosition, pd.Segments[0], worldPerp, worldAxis,
                                                    sign, th, projection, axis, perp, placedTags);
                }
                catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionCommit: nudge dimension text", ex); }
            }
        }

        /// <summary>
        /// Computes the final world text position for one moved tag: perpendicular out to clear the arc,
        /// then sideways along the axis so it sits beside the segment centre. Slides further along-axis
        /// in text-width steps until the tag's view-2D box clears every tag already in
        /// <paramref name="placedTags"/>, then records its box. Returns the chosen world position.
        /// </summary>
        private static XYZ PlaceTag(
            XYZ defaultPos, Core.PlannedSegment ps, XYZ worldPerp, XYZ worldAxis, double sign, double th,
            Resolvers.ViewProjection projection, Core.Vec2 axis, Core.Vec2 perp, List<Core.Box2> placedTags)
        {
            double perpNudge = (ps.TextState == Core.SegmentTextState.LeaderOut ? LeaderNudgeHeights : StaggerNudgeHeights) * th;
            double halfAlong = Math.Max(ps.TextWidthFt, th) * 0.5;   // along the reading direction
            double halfPerp  = th * 0.5;
            double alongBase = halfAlong + AlongBaseHeights * th;    // clear the segment centre
            double alongStep = halfAlong * 2.0 + th * 0.5;           // one text width per clash bump

            // View-2D half extents of the (axis-aligned) tag box.
            double halfX = Math.Abs(axis.X) * halfAlong + Math.Abs(perp.X) * halfPerp;
            double halfY = Math.Abs(axis.Y) * halfAlong + Math.Abs(perp.Y) * halfPerp;

            XYZ pos = defaultPos;
            Core.Box2 box = default;
            for (int t = 0; t < MaxAlongSteps; t++)
            {
                double along = alongBase + t * alongStep;
                pos = defaultPos + worldPerp * (sign * perpNudge) + worldAxis * along;
                box = Core.Box2.FromCenter(projection.To2D(pos), halfX, halfY);
                bool clash = false;
                for (int i = 0; i < placedTags.Count; i++)
                    if (box.Intersects(placedTags[i])) { clash = true; break; }
                if (!clash) break;
            }
            placedTags.Add(box);
            return pos;   // ⚠ leader appearance depends on the DimensionType — verify on Windows
        }

        private static bool IsMoved(Core.SegmentTextState s) =>
            s == Core.SegmentTextState.LeaderOut || s == Core.SegmentTextState.Staggered;
    }
}
