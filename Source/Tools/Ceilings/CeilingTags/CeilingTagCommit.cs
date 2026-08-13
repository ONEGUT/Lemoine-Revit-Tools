using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
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
    /// The ordering discipline here is the performance fix. Each <c>IndependentTag.Create</c>
    /// is a write; a geometry read between two writes forces Revit to regenerate the model to
    /// answer it. Because the engine already resolved every reference and point, this loop is
    /// writes only, and the single regeneration happens once at commit.
    ///
    /// This is also the seam where a future room source would diverge: a room tag is created
    /// through <c>Creation.Document.NewRoomTag(LinkElementId, UV, viewId)</c> and returns a
    /// <c>RoomTag</c>, so it needs its own emitter rather than a branch inside this one.
    /// </summary>
    public static class CeilingTagCommit
    {
        public static CommitResult Place(
            Document doc,
            IReadOnlyList<ViewTagPlan> plans,
            ElementId tagTypeId,
            bool replaceExisting,
            Action<string, string> log)
        {
            var result = new CommitResult();
            if (plans.Count == 0) return result;

            int totalTags = plans.Sum(p => p.Plan.Placements.Count);
            var progress = new RunProgressReporter(log, totalTags, AppStrings.T("ceilings.tags.noun"));

            var ceilingTagCatId = new ElementId(BuiltInCategory.OST_CeilingTags);

            using (var tx = new Transaction(doc, "Tag Ceilings"))
            {
                ConfigureFailures(tx);
                tx.Start();

                foreach (ViewTagPlan vtp in plans)
                {
                    if (RunState.CancelRequested) break;

                    // ── Replace: clear this view's existing ceiling tags first ────
                    if (replaceExisting)
                    {
                        foreach (ElementId staleId in new FilteredElementCollector(doc, vtp.ViewId)
                                     .OfClass(typeof(IndependentTag))
                                     .Cast<IndependentTag>()
                                     .Where(t => t.Category?.Id == ceilingTagCatId)
                                     .Select(t => t.Id)
                                     .ToList())
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

            return result;
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
