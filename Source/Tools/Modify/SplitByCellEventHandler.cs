using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;

namespace LemoineTools.Tools.ModifyElements
{
    public class SplitByCellEventHandler : IExternalEventHandler
    {
        public List<string>               SelectedCategoryLabels { get; set; } = new List<string>();
        public double                     CellX                  { get; set; } = 10.0;
        public double                     CellY                  { get; set; } = 10.0;
        public bool                       UseProjectOrigin       { get; set; } = false;
        public List<ElementId>?           PreSelectedIds         { get; set; }  // E
        // View to scope the category collection to — captured when the tool was OPENED, not
        // read live from Revit at Run time, so switching to a sheet/other view before clicking
        // Run can't silently empty the selection. Null falls back to the current active view.
        public ElementId?                 ActiveViewId           { get; set; }
        public Action<string, string>?     OnLog                  { get; set; }
        public Action<int, int, int, int>? OnProgress            { get; set; }
        public Action<int, int, int>?      OnComplete            { get; set; }

        public string GetName() => "SplitByCell";

        public void Execute(UIApplication app)
        {
            var pushLog    = OnLog      ?? ((m, s) => { });
            var onProgress = OnProgress ?? ((p, a, b, c) => { });
            var onComplete = OnComplete ?? ((a, b, c) => { });

            try
            {
                Document doc  = app.ActiveUIDocument.Document;
                // Prefer the view captured at open time (ActiveViewId); fall back to the live
                // active view only when none was captured.
                View? view = ActiveViewId != null
                    ? doc.GetElement(ActiveViewId) as View
                    : app.ActiveUIDocument.ActiveView;

                if (view == null || view.IsTemplate)
                {
                    pushLog(AppStrings.T("modify.splitByCell.log.noActiveView"), "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                // Revit internal units are feet; CellX/CellY are already in feet
                double cellXft = CellX;
                double cellYft = CellY;

                List<ElementId> targetIds;
                if (PreSelectedIds != null && PreSelectedIds.Count > 0)
                {
                    targetIds = new List<ElementId>(PreSelectedIds);
                    pushLog(AppStrings.T("modify.splitByCell.log.preSelected", targetIds.Count), "info");
                }
                else
                {
                    var labelToBic   = SplitByCellViewModel.CatLabels
                        .ToDictionary(kvp => kvp.Value, kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);
                    var selectedBics = new HashSet<BuiltInCategory>(
                        SelectedCategoryLabels
                            .Where(l => labelToBic.ContainsKey(l))
                            .Select(l => labelToBic[l]));
                    // Collect only the selected categories, and name the view being searched so a
                    // "nothing found" result is diagnosable (wrong view active at open time, etc.).
                    var categoryMap  = SplitByCellHelpers.BuildCategoryMap(doc, view, selectedBics);
                    targetIds = categoryMap
                        .Where(kvp => selectedBics.Contains(kvp.Key))
                        .SelectMany(kvp => kvp.Value)
                        .ToList();
                    pushLog(AppStrings.T("modify.splitByCell.log.usingView", view.Name), "info");
                }

                if (!targetIds.Any())
                {
                    pushLog(AppStrings.T("modify.splitByCell.log.noElements"), "info");
                    onComplete(0, 0, 0);
                    return;
                }

                pushLog(AppStrings.T("modify.splitByCell.log.foundElements", targetIds.Count), "info");

                XYZ? gridOrigin = null;
                if (UseProjectOrigin)
                {
                    XYZ? bp = SplitByCellHelpers.GetProjectBasePoint(doc);
                    if (bp == null)
                        pushLog(AppStrings.T("modify.splitByCell.log.noBasePoint"), "info");
                    gridOrigin = bp ?? XYZ.Zero;
                }

                int created = 0;
                int skipped = 0;
                int failed  = 0;

                var progress = new RunProgressReporter(pushLog, targetIds.Count, "elements");

                using (var tg = new TransactionGroup(doc, "Split Elements by Cell"))
                {
                    tg.Start();

                    foreach (ElementId id in targetIds)
                    {
                        // Abandon mid-run: stop processing more elements but let tg.Assimilate()
                        // (below) still run so every per-element split already committed survives.
                        if (RunState.CancelRequested)
                        {
                            pushLog(AppStrings.T("common.log.stoppedByUser", progress.Done, targetIds.Count), "warn");
                            break;
                        }

                        Element el = doc.GetElement(id);
                        if (el == null)
                        {
                            // Pre-selected ids can go stale (the elements were split/deleted since
                            // the tool was opened). Say so, don't skip silently.
                            skipped++;
                            pushLog(AppStrings.T("modify.splitByCell.log.gone", id.Value), "info");
                            progress.Tick();
                            onProgress(progress.Percent, created, failed, skipped);
                            continue;
                        }

                        // A successful SplitElement DELETES the original element, and any
                        // property access on a deleted element throws InvalidObjectException
                        // ("The referenced object is not valid…"). Capture the identity used
                        // in log lines up front — logging el.Category after the split threw,
                        // and that throw rolled back the entire (successful) split.
                        string catName = el.Category?.Name ?? string.Empty;
                        ElementId elId  = el.Id;

                        // A grouped element can't be deleted/recreated in place — the split would
                        // throw a raw Revit error. Skip-and-log with a clear reason instead.
                        if (el.GroupId != null && el.GroupId != ElementId.InvalidElementId)
                        {
                            skipped++;
                            pushLog(AppStrings.T("modify.splitByCell.log.inGroup", catName, elId), "info");
                            progress.Tick();
                            onProgress(progress.Percent, created, failed, skipped);
                            continue;
                        }

                        // In a workshared model, an element owned by another user can't be modified.
                        if (doc.IsWorkshared)
                        {
                            var co = WorksharingUtils.GetCheckoutStatus(doc, elId);
                            if (co == CheckoutStatus.OwnedByOtherUser)
                            {
                                skipped++;
                                pushLog(AppStrings.T("modify.splitByCell.log.owned", catName, elId), "info");
                                progress.Tick();
                                onProgress(progress.Percent, created, failed, skipped);
                                continue;
                            }
                        }

                        using (var tx = new Transaction(doc, $"Split {el.Name}"))
                        {
                            var fho = tx.GetFailureHandlingOptions();
                            fho.SetClearAfterRollback(true);
                            tx.SetFailureHandlingOptions(fho);
                            tx.Start();

                            try
                            {
                                var (n, droppedCells, cellStatus) = SplitByCellHelpers.SplitElement(
                                    doc, el, cellXft, cellYft, gridOrigin);

                                if (cellStatus == CellSplitStatus.Split)
                                {
                                    // Commit BEFORE counting/logging so the created tally only
                                    // ever reflects cells that actually persisted, and a log
                                    // failure can never roll back a completed split.
                                    tx.Commit();
                                    created += n;
                                    // Surface dropped cells (recreation/intersect failures) in the
                                    // run log — otherwise the loss lives only in diagnostics.log.
                                    if (droppedCells > 0)
                                        pushLog(AppStrings.T("modify.splitByCell.log.cellOkDropped", catName, elId, n, droppedCells), "warn");
                                    else
                                        pushLog(AppStrings.T("modify.splitByCell.log.cellOk", catName, elId, n), "pass");
                                }
                                else if (cellStatus == CellSplitStatus.FitsInOneCell)
                                {
                                    tx.RollBack();
                                    skipped++;
                                    pushLog(AppStrings.T("modify.splitByCell.log.fitsOneCell", catName, elId), "info");
                                }
                                else if (cellStatus == CellSplitStatus.NoGeometry)
                                {
                                    tx.RollBack();
                                    skipped++;
                                    pushLog(AppStrings.T("modify.splitByCell.log.noSolid", catName, elId), "info");
                                }
                                else if (cellStatus == CellSplitStatus.Sloped)
                                {
                                    tx.RollBack();
                                    skipped++;
                                    pushLog(AppStrings.T("modify.splitByCell.log.sloped", catName, elId), "info");
                                }
                                else // NoCellsIntersected
                                {
                                    tx.RollBack();
                                    failed++;
                                    pushLog(AppStrings.T("modify.splitByCell.log.boolFail", catName, elId, droppedCells), "fail");
                                }
                            }
                            catch (Exception ex)
                            {
                                // The transaction may already be committed (a throw after
                                // tx.Commit()) — only roll back one that is still open.
                                if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                                failed++;
                                pushLog(AppStrings.T("modify.splitByCell.log.cellError", catName, elId, ex.Message), "fail");
                            }
                        }

                        progress.Tick();
                        onProgress(progress.Percent, created, failed, skipped);
                    }

                    tg.Assimilate();
                }

                pushLog(AppStrings.T("modify.splitByCell.log.done", created, skipped, failed),
                        failed > 0 ? "fail" : "pass");
                onProgress(100, created, failed, skipped);
                onComplete(created, failed, skipped);
            }
            catch (Exception ex)
            {
                pushLog(AppStrings.T("modify.splitByCell.log.error", ex.Message), "fail");
                onComplete(0, 1, 0);
            }
            finally
            {
                // Session-long static handler — drop the run's payload.
                SelectedCategoryLabels = new List<string>();
                PreSelectedIds         = null;
            }
        }
    }
}
