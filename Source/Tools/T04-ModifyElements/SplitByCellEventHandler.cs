using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

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
                    pushLog("No active view or view is a template.", "fail");
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
                    pushLog($"Operating on {targetIds.Count} pre-selected element(s).", "info");
                }
                else
                {
                    var categoryMap = SplitByCellHelpers.BuildCategoryMap(doc, view);
                    var selectedSet = new HashSet<string>(SelectedCategoryLabels, StringComparer.OrdinalIgnoreCase);
                    targetIds = categoryMap
                        .Where(kvp => selectedSet.Contains(kvp.Key))
                        .SelectMany(kvp => kvp.Value)
                        .ToList();
                }

                if (!targetIds.Any())
                {
                    pushLog("No elements found for the selected categories in the active view.", "info");
                    onComplete(0, 0, 0);
                    return;
                }

                pushLog($"Found {targetIds.Count} element(s) to process.", "info");

                XYZ? gridOrigin = UseProjectOrigin
                    ? SplitByCellHelpers.GetProjectBasePoint(doc)
                    : null;

                int created = 0;
                int skipped = 0;
                int failed  = 0;

                using (var tg = new TransactionGroup(doc, "Split Elements by Cell"))
                {
                    tg.Start();

                    foreach (ElementId id in targetIds)
                    {
                        Element el = doc.GetElement(id);
                        if (el == null) { skipped++; continue; }

                        using (var tx = new Transaction(doc, $"Split {el.Name}"))
                        {
                            var fho = tx.GetFailureHandlingOptions();
                            fho.SetClearAfterRollback(true);
                            tx.SetFailureHandlingOptions(fho);
                            tx.Start();

                            try
                            {
                                int n = SplitByCellHelpers.SplitElement(
                                    doc, el, cellXft, cellYft, gridOrigin);

                                if (n > 0)
                                {
                                    created += n;
                                    pushLog($"✓ {el.Category?.Name} {el.Id} → {n} cell(s)", "pass");
                                    tx.Commit();
                                }
                                else
                                {
                                    tx.RollBack();
                                    skipped++;
                                    pushLog($"— {el.Category?.Name} {el.Id}: fits in one cell, skipped", "info");
                                }
                            }
                            catch (Exception ex)
                            {
                                tx.RollBack();
                                failed++;
                                pushLog($"✗ {el.Category?.Name} {el.Id}: {ex.Message}", "fail");
                            }
                        }
                    }

                    tg.Assimilate();
                }

                onProgress(100, created, failed, skipped);
                onComplete(created, failed, skipped);
            }
            catch (Exception ex)
            {
                pushLog($"Error: {ex.Message}", "fail");
                onComplete(0, 1, 0);
            }
        }
    }
}
