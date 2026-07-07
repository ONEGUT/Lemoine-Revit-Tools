using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;

namespace LemoineTools.Tools.ModifyElements
{
    public class SplitByGridEventHandler : IExternalEventHandler
    {
        public List<string>               SelectedCategoryNames { get; set; } = new List<string>();
        public List<ElementId>            SelectedGridIds       { get; set; } = new List<ElementId>();
        public List<ElementId>?           PreSelectedIds        { get; set; }
        public ElementId?                 ActiveViewId          { get; set; }

        // Refresh mode: set IsRefreshRequest = true before raising to collect fresh grids.
        public bool                       IsRefreshRequest      { get; set; } = false;
        public Action<IEnumerable<Grid>>? OnRefreshed           { get; set; }

        public Action<string, string>?     OnLog      { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "SplitByGrid";

        public void Execute(UIApplication app)
        {
            Document doc = app.ActiveUIDocument.Document;

            if (IsRefreshRequest)
            {
                IsRefreshRequest = false;
                var freshGrids = new FilteredElementCollector(doc)
                    .OfClass(typeof(Grid))
                    .Cast<Grid>()
                    .Where(g => g.Curve is Line)
                    .OrderBy(g => g.Name)
                    .ToList();
                var cb = OnRefreshed;
                OnRefreshed = null;
                cb?.Invoke(freshGrids);
                return;
            }

            var pushLog    = OnLog      ?? ((m, s) => { });
            var onProgress = OnProgress ?? ((p, a, b, c) => { });
            var onComplete = OnComplete ?? ((a, b, c) => { });

            try
            {
                var grids = SelectedGridIds
                    .Select(id => doc.GetElement(id) as Grid)
                    .Where(g => g != null && g.Curve is Line)
                    .ToList();

                if (!grids.Any())
                {
                    pushLog(AppStrings.T("modify.splitByGrid.log.noValid"), "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                List<Element> elements;
                if (PreSelectedIds != null && PreSelectedIds.Count > 0)
                {
                    elements = PreSelectedIds
                        .Select(id => doc.GetElement(id))
                        .Where(e => e != null)
                        .ToList()!;
                    pushLog(AppStrings.T("modify.splitByGrid.log.preSelected", elements.Count), "info");
                }
                else
                {
                    View? view = ActiveViewId != null ? doc.GetElement(ActiveViewId) as View : null;
                    elements = CollectByName(doc, view, SelectedCategoryNames);
                    pushLog(AppStrings.T("modify.splitByGrid.log.foundCats", elements.Count, SelectedCategoryNames.Count), "info");
                }

                pushLog(AppStrings.T("modify.splitByGrid.log.splitting", elements.Count, grids.Count), "info");

                var progress = new RunProgressReporter(pushLog, elements.Count, "elements");

                SplitStats stats;
                using (var tx = new Transaction(doc, "Split Elements by Grid"))
                {
                    var fho = tx.GetFailureHandlingOptions();
                    fho.SetClearAfterRollback(true);
                    tx.SetFailureHandlingOptions(fho);
                    tx.Start();

                    stats = SplitElementsShared.SplitByGrid(doc, elements, grids, progress);

                    tx.Commit();
                }

                foreach (var entry in stats.Log)
                {
                    string status = entry.StartsWith("✓") ? "pass"
                                  : entry.StartsWith("✗") ? "fail"
                                  : "info";
                    pushLog(entry, status);
                }

                if (RunState.CancelRequested)
                {
                    int processed = stats.SplitCount + stats.SkipCount + stats.FailCount;
                    pushLog(AppStrings.T("common.log.stoppedByUser", processed, elements.Count), "warn");
                }

                pushLog(AppStrings.T("modify.splitByGrid.log.done", stats.SegmentsCreated, stats.SplitCount, stats.SkipCount, stats.FailCount),
                        stats.FailCount > 0 ? "fail" : "pass");
                onProgress(100, stats.SegmentsCreated, stats.FailCount, stats.SkipCount);
                onComplete(stats.SegmentsCreated, stats.FailCount, stats.SkipCount);
            }
            catch (Exception ex)
            {
                pushLog(AppStrings.T("modify.splitByGrid.log.error", ex.Message), "fail");
                onComplete(0, 1, 0);
            }
            finally
            {
                // Session-long static handler — drop the run's payload.
                SelectedCategoryNames = new List<string>();
                SelectedGridIds       = new List<ElementId>();
                PreSelectedIds        = null;
            }
        }

        private static List<Element> CollectByName(Document doc, View? view, List<string> names)
        {
            var selectedSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            var coll = (view != null && !view.IsTemplate)
                ? new FilteredElementCollector(doc, view.Id)
                : new FilteredElementCollector(doc);
            return coll
                .WhereElementIsNotElementType()
                .Where(e => e.Category?.Name != null && selectedSet.Contains(e.Category.Name))
                .ToList();
        }
    }
}
