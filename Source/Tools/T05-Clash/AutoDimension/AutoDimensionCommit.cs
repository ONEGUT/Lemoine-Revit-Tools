using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Testing.AutoDimension.Resolvers;

namespace LemoineTools.Tools.Testing.AutoDimension
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

                    ApplyTextStates(dim, pd, output.Projection, perp, output.CoreConfig, log);
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
        /// Realizes the layout's per-segment text decisions on the placed Revit dimension. Like a
        /// drafter, each value stays on its OWN segment: a moved (cramped) segment's text is nudged
        /// straight out perpendicular by a small amount at its own along-axis position — a short
        /// straight leader, not a far side-column with a crossing arc. Adjacent moved tags alternate
        /// their nudge so they don't collide. Each move is independently guarded.
        /// </summary>
        private const double StaggerNudgeHeights = 1.2;  // perpendicular nudge for a staggered tag
        private const double LeaderNudgeHeights  = 2.2;  // perpendicular nudge for a leadered tag (now rare)
        private const double AltStepHeights      = 1.3;  // extra height on every other moved tag (anti-collision)

        private static void ApplyTextStates(
            Dimension dim, Core.PlannedDimension pd, Resolvers.ViewProjection projection,
            Core.Vec2 perp, Core.LayoutConfig cfg, Action<string, string> log)
        {
            double th = cfg.TextHeightFt;
            if (th <= 0) return;

            // World perpendicular (outward, on the dimension's offset side) to nudge text along.
            XYZ worldPerp;
            try
            {
                worldPerp = projection.From2D(perp) - projection.From2D(new Core.Vec2(0, 0));
                if (worldPerp.GetLength() < 1e-9) return;
                worldPerp = worldPerp.Normalize();
            }
            catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionCommit: perp direction", ex); return; }

            double sign = pd.Side == Core.DimSide.Positive ? 1.0 : -1.0;

            // Multi-segment chain → per-segment text; single span → the whole-dimension text.
            DimensionSegmentArray? segs = null;
            try { segs = dim.Segments; }
            catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionCommit: read segments", ex); }

            if (segs != null && segs.Size > 0)
            {
                int n = Math.Min(segs.Size, pd.Segments.Count);
                int moved = 0;
                for (int k = 0; k < n; k++)
                {
                    var state = pd.Segments[k].TextState;
                    if (!IsMoved(state)) continue;
                    double nudge = (state == Core.SegmentTextState.LeaderOut ? LeaderNudgeHeights : StaggerNudgeHeights) * th;
                    if ((moved & 1) == 1) nudge += AltStepHeights * th;   // alternate so neighbours don't overlap
                    moved++;
                    try
                    {
                        var seg = segs.get_Item(k);
                        if (seg?.TextPosition == null) continue;
                        // Keep the text on its own segment (Revit's default midpoint), just push it out.
                        seg.TextPosition = seg.TextPosition + worldPerp * (sign * nudge);   // ⚠ leader appearance depends on the DimensionType
                    }
                    catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionCommit: nudge segment text", ex); }
                }
            }
            else if (pd.Segments.Count > 0 && IsMoved(pd.Segments[0].TextState))
            {
                try
                {
                    if (dim.TextPosition != null)
                    {
                        double nudge = (pd.Segments[0].TextState == Core.SegmentTextState.LeaderOut ? LeaderNudgeHeights : StaggerNudgeHeights) * th;
                        dim.TextPosition = dim.TextPosition + worldPerp * (sign * nudge);   // ⚠ verify on Windows
                    }
                }
                catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionCommit: nudge dimension text", ex); }
            }
        }

        private static bool IsMoved(Core.SegmentTextState s) =>
            s == Core.SegmentTextState.LeaderOut || s == Core.SegmentTextState.Staggered;
    }
}
