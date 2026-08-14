using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Tools.Ceilings.CeilingTags.TagCore;

namespace LemoineTools.Tools.Ceilings.CeilingTags
{
    /// <summary>Tally of one commit pass.</summary>
    public struct CommitResult
    {
        public int Placed;
        public int Deleted;
        public int Failed;
    }

    /// <summary>
    /// The mutation side: creates every planned tag with no geometry reads interleaved.
    ///
    /// The ordering discipline here is the performance fix, and there are two separate
    /// regeneration traps it exists to avoid:
    ///
    /// 1. <b>Reads between writes.</b> Each <c>IndependentTag.Create</c> is a write; a geometry
    ///    read between two writes forces Revit to regenerate the model to answer it. The engine
    ///    already resolved every reference and point, and the stale-tag collection below is
    ///    hoisted ahead of the first write, so this loop is writes only.
    /// 2. <b>Writing into a CLOSED view.</b> Revit maintains a computed view for each OPEN view;
    ///    a tag created in a closed one makes Revit compute that view, and the create re-dirties
    ///    it so the next tag recomputes it again. Every target view is therefore opened before
    ///    the transaction starts (Revit refuses to change the active view inside one) and closed
    ///    again afterwards.
    ///
    /// This is also the seam where a future room source would diverge: a room tag is created
    /// through <c>Creation.Document.NewRoomTag(LinkElementId, UV, viewId)</c> and returns a
    /// <c>RoomTag</c>, so it needs its own emitter rather than a branch inside this one.
    /// </summary>
    public static class CeilingTagCommit
    {
        public static CommitResult Place(
            UIDocument uidoc,
            IReadOnlyList<ViewTagPlan> plans,
            ElementId tagTypeId,
            bool replaceExisting,
            Action<string, string> log)
        {
            var result = new CommitResult();
            if (plans.Count == 0) return result;

            Document doc = uidoc.Document;
            int totalTags = plans.Sum(p => p.Plan.Placements.Count);
            var progress = new RunProgressReporter(log, totalTags, AppStrings.T("ceilings.tags.noun"));

            var ceilingTagCatId = new ElementId(BuiltInCategory.OST_CeilingTags);

            // ── Read phase: every view's stale tags, collected before ANY write ──────
            // A view-scoped collector run AFTER a write forces a full regeneration to answer
            // it, so collecting per view inside the write loop cost one extra regeneration
            // per view beyond the first. All the reads happen here, together.
            var staleByView = new Dictionary<ElementId, List<ElementId>>();
            if (replaceExisting)
            {
                foreach (ViewTagPlan vtp in plans)
                {
                    if (staleByView.ContainsKey(vtp.ViewId)) continue;
                    staleByView[vtp.ViewId] = new FilteredElementCollector(doc, vtp.ViewId)
                        .OfClass(typeof(IndependentTag))
                        .Cast<IndependentTag>()
                        .Where(t => t.Category?.Id == ceilingTagCatId)
                        .Select(t => t.Id)
                        .ToList();
                }
            }

            // ── Open the target views BEFORE the transaction ─────────────────────────
            // Only views that actually receive tags: opening a view is not free either, and
            // every open view pins native graphics RAM until it is closed again.
            var beforeViews = PickerViewGuard.Snapshot(uidoc);
            try
            {
                PickerViewGuard.OpenViews(
                    uidoc,
                    plans.Where(p => p.Plan.Placements.Count > 0).Select(p => p.ViewId),
                    beforeViews, log);

                PlaceAll(doc, plans, staleByView, tagTypeId, totalTags, progress, log, ref result);
            }
            finally
            {
                // Restores the user's original active view and closes only what this run
                // opened. Outside the transaction — Revit refuses both inside one.
                PickerViewGuard.CloseOpenedViews(uidoc, beforeViews, log);
            }

            return result;
        }

        private static void PlaceAll(
            Document doc,
            IReadOnlyList<ViewTagPlan> plans,
            Dictionary<ElementId, List<ElementId>> staleByView,
            ElementId tagTypeId,
            int totalTags,
            RunProgressReporter progress,
            Action<string, string> log,
            ref CommitResult result)
        {
            using (var tx = new Transaction(doc, "Tag Ceilings"))
            {
                ConfigureFailures(tx);
                tx.Start();

                foreach (ViewTagPlan vtp in plans)
                {
                    if (RunState.CancelRequested) break;

                    // ── Replace: clear this view's existing ceiling tags first ────
                    if (staleByView.TryGetValue(vtp.ViewId, out List<ElementId>? stale) && stale != null)
                    {
                        foreach (ElementId staleId in stale)
                        {
                            try { doc.Delete(staleId); result.Deleted++; }
                            catch (Exception ex)
                            {
                                DiagnosticsLog.Swallowed("CeilingTags: delete existing tag (protected or already gone)", ex);
                            }
                        }
                    }

                    foreach (TagPlacement p in vtp.Plan.Placements)
                    {
                        if (RunState.CancelRequested)
                        {
                            log(AppStrings.T("common.log.stoppedByUser", result.Placed, totalTags), "warn");
                            break;
                        }

                        // Explicit null tests rather than `src?.TagRef == null` so nullable flow
                        // analysis narrows both for the dereferences below.
                        if (!vtp.Refs.TryGetValue(p.RegionId, out CeilingSourceRef? src)
                            || src == null || src.TagRef == null)
                        {
                            // A placement with no reference cannot be created — say so rather
                            // than dropping it silently.
                            log(AppStrings.T("ceilings.tags.log.noRefForRegion", p.RegionId), "warn");
                            result.Failed++;
                            continue;
                        }

                        var pt = new XYZ(p.Point.X, p.Point.Y, src.ZWorld);

                        try
                        {
                            // The type-id overload applies the chosen ceiling tag directly, so
                            // there is no default-tag dependency and no ChangeTypeId second pass
                            // (which would be another write per tag).
                            IndependentTag.Create(
                                doc, tagTypeId, vtp.ViewId, src.TagRef,
                                false, TagOrientation.Horizontal, pt);
                            result.Placed++;
                        }
                        catch (Exception ex)
                        {
                            log(AppStrings.T(
                                src.Linked ? "ceilings.tags.log.tagLinkedFailed"
                                           : "ceilings.tags.log.tagHostFailed",
                                src.ElementId, ex.Message), "fail");
                            result.Failed++;
                        }

                        progress.Tick();
                    }
                }

                // Commit whatever was placed — a cancelled run keeps its work, per the
                // cooperative-cancellation contract.
                tx.Commit();
            }
        }

        private static void ConfigureFailures(Transaction tx)
        {
            var opts = tx.GetFailureHandlingOptions();
            opts.SetClearAfterRollback(true);
            opts.SetDelayedMiniWarnings(true);
            tx.SetFailureHandlingOptions(opts);
        }
    }
}
