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
            foreach (var pd in plan.Dimensions)
            {
                // Axis/perp are per-dimension now that X and Y strings coexist in one plan.
                Core.Vec2 axis = pd.AxisDir.Normalized();
                Core.Vec2 perp = axis.Perp();

                if (!output.Refs.TryGetValue(pd.SourceKey, out var bundle) || bundle?.Ordered == null || bundle.Ordered.Count < 2)
                {
                    result.Failures++;
                    log($"Source {pd.SourceKey}: missing reference bundle — skipped.", "fail");
                    continue;
                }

                double sign = pd.Side == Core.DimSide.Positive ? 1.0 : -1.0;
                Core.Vec2 off = perp * (sign * pd.OffsetFt);
                XYZ p0 = output.Projection.From2D(pd.SourcePoint + off);
                XYZ p1 = output.Projection.From2D(pd.TargetPoint + off);

                if (p0.DistanceTo(p1) < 1e-4)
                {
                    result.Failures++;
                    log($"Source {pd.SourceKey}: degenerate dimension line — skipped.", "fail");
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
                        log($"Source {pd.SourceKey}: NewDimension returned null.", "fail");
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
                    log($"Source {pd.SourceKey}: {ex.Message}", "fail");
                }
            }

            return result;
        }

        /// <summary>
        /// Realizes the layout's per-segment text decisions on the placed Revit dimension:
        /// cramped segments flagged LeaderOut / Staggered have their text dragged outward along
        /// the string's perpendicular so dense runs stay readable (the "drag compacted strings off
        /// to the side" behaviour). Each move is independently guarded — a Revit rejection on one
        /// segment never loses the dimension or the rest of the run.
        /// </summary>
        private static void ApplyTextStates(
            Dimension dim, Core.PlannedDimension pd, Resolvers.ViewProjection projection,
            Core.Vec2 perp, Core.LayoutConfig cfg, Action<string, string> log)
        {
            // World direction of the string's perpendicular (drag axis for text).
            XYZ worldPerp;
            try
            {
                worldPerp = (projection.From2D(perp) - projection.From2D(new Core.Vec2(0, 0)));
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
                for (int k = 0; k < n; k++)
                {
                    double drag = DragFor(pd.Segments[k].TextState, cfg, k);
                    if (drag <= 0) continue;
                    try
                    {
                        var seg = segs.get_Item(k);
                        if (seg?.TextPosition == null) continue;
                        seg.TextPosition = seg.TextPosition + worldPerp * (sign * drag);   // ⚠ leader appearance depends on the DimensionType
                    }
                    catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionCommit: drag segment text", ex); }
                }
            }
            else if (pd.Segments.Count > 0)
            {
                double drag = DragFor(pd.Segments[0].TextState, cfg, 0);
                if (drag > 0)
                {
                    try
                    {
                        if (dim.TextPosition != null)
                            dim.TextPosition = dim.TextPosition + worldPerp * (sign * drag);   // ⚠ verify on Windows
                    }
                    catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionCommit: drag dimension text", ex); }
                }
            }
        }

        /// <summary>Outward drag distance (model ft) for a segment's text state; 0 = leave inline.</summary>
        private static double DragFor(Core.SegmentTextState state, Core.LayoutConfig cfg, int index)
        {
            switch (state)
            {
                case Core.SegmentTextState.LeaderOut:  return cfg.TextHeightFt * 4.0;
                case Core.SegmentTextState.Staggered:  return cfg.TextHeightFt * ((index % 2 == 0) ? 1.6 : 2.8);
                default:                               return 0.0;
            }
        }
    }
}
