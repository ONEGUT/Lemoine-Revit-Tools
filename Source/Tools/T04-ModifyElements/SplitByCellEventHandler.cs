using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.ModifyElements
{
    public class SplitByCellEventHandler : IExternalEventHandler
    {
        public List<string>               SelectedCategoryLabels { get; set; } = new List<string>();
        public double                     CellX                  { get; set; } = 10.0;
        public double                     CellY                  { get; set; } = 10.0;
        public bool                       UseProjectOrigin       { get; set; } = false;
        public List<ElementId>?           PreSelectedIds         { get; set; }  // E
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
                View     view = app.ActiveUIDocument.ActiveView;

                if (view == null || view.IsTemplate)
                {
                    pushLog(LemoineStrings.T("modify.splitByCell.log.noActiveView"), "fail");
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
                    pushLog(LemoineStrings.T("modify.splitByCell.log.preSelected", targetIds.Count), "info");
                }
                else
                {
                    var categoryMap  = SplitByCellHelpers.BuildCategoryMap(doc, view);
                    var labelToBic   = SplitByCellViewModel.CatLabels
                        .ToDictionary(kvp => kvp.Value, kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);
                    var selectedBics = new HashSet<BuiltInCategory>(
                        SelectedCategoryLabels
                            .Where(l => labelToBic.ContainsKey(l))
                            .Select(l => labelToBic[l]));
                    targetIds = categoryMap
                        .Where(kvp => selectedBics.Contains(kvp.Key))
                        .SelectMany(kvp => kvp.Value)
                        .ToList();
                }

                if (!targetIds.Any())
                {
                    pushLog(LemoineStrings.T("modify.splitByCell.log.noElements"), "info");
                    onComplete(0, 0, 0);
                    return;
                }

                pushLog(LemoineStrings.T("modify.splitByCell.log.foundElements", targetIds.Count), "info");

                XYZ? gridOrigin = null;
                if (UseProjectOrigin)
                {
                    XYZ? bp = SplitByCellHelpers.GetProjectBasePoint(doc);
                    if (bp == null)
                        pushLog(LemoineStrings.T("modify.splitByCell.log.noBasePoint"), "info");
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
                        if (LemoineRun.CancelRequested)
                        {
                            pushLog(LemoineStrings.T("common.log.stoppedByUser", progress.Done, targetIds.Count), "warn");
                            break;
                        }

                        Element el = doc.GetElement(id);
                        if (el == null)
                        {
                            skipped++;
                            progress.Tick();
                            onProgress(progress.Percent, created, failed, skipped);
                            continue;
                        }

                        using (var tx = new Transaction(doc, $"Split {el.Name}"))
                        {
                            var fho = tx.GetFailureHandlingOptions();
                            fho.SetClearAfterRollback(true);
                            tx.SetFailureHandlingOptions(fho);
                            tx.Start();

                            try
                            {
                                var (n, cellStatus) = SplitByCellHelpers.SplitElement(
                                    doc, el, cellXft, cellYft, gridOrigin);

                                if (cellStatus == CellSplitStatus.Split)
                                {
                                    created += n;
                                    pushLog(LemoineStrings.T("modify.splitByCell.log.cellOk", el.Category?.Name, el.Id, n), "pass");
                                    tx.Commit();
                                }
                                else if (cellStatus == CellSplitStatus.FitsInOneCell)
                                {
                                    tx.RollBack();
                                    skipped++;
                                    pushLog(LemoineStrings.T("modify.splitByCell.log.fitsOneCell", el.Category?.Name, el.Id), "info");
                                }
                                else if (cellStatus == CellSplitStatus.NoGeometry)
                                {
                                    tx.RollBack();
                                    skipped++;
                                    pushLog(LemoineStrings.T("modify.splitByCell.log.noSolid", el.Category?.Name, el.Id), "info");
                                }
                                else // NoCellsIntersected
                                {
                                    tx.RollBack();
                                    failed++;
                                    pushLog(LemoineStrings.T("modify.splitByCell.log.boolFail", el.Category?.Name, el.Id), "fail");
                                }
                            }
                            catch (Exception ex)
                            {
                                tx.RollBack();
                                failed++;
                                pushLog(LemoineStrings.T("modify.splitByCell.log.cellError", el.Category?.Name, el.Id, ex.Message), "fail");
                            }
                        }

                        progress.Tick();
                        onProgress(progress.Percent, created, failed, skipped);
                    }

                    tg.Assimilate();
                }

                pushLog(LemoineStrings.T("modify.splitByCell.log.done", created, skipped, failed),
                        failed > 0 ? "fail" : "pass");
                onProgress(100, created, failed, skipped);
                onComplete(created, failed, skipped);
            }
            catch (Exception ex)
            {
                pushLog(LemoineStrings.T("modify.splitByCell.log.error", ex.Message), "fail");
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
