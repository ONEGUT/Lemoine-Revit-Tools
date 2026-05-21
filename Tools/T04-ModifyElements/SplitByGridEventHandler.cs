using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace LemoineTools.Tools.ModifyElements
{
    public class SplitByGridEventHandler : IExternalEventHandler
    {
        public List<BuiltInCategory>      SelectedBics    { get; set; } = new List<BuiltInCategory>();
        public List<ElementId>            SelectedGridIds { get; set; } = new List<ElementId>();
        public Action<string, string>?    OnLog           { get; set; }
        public Action<int, int, int, int>? OnProgress      { get; set; }
        public Action<int, int, int>?      OnComplete      { get; set; }

        public string GetName() => "SplitByGrid";

        public void Execute(UIApplication app)
        {
            var pushLog    = OnLog      ?? ((m, s) => { });
            var onProgress = OnProgress ?? ((p, a, b, c) => { });
            var onComplete = OnComplete ?? ((a, b, c) => { });

            try
            {
                Document doc = app.ActiveUIDocument.Document;

                var grids = SelectedGridIds
                    .Select(id => doc.GetElement(id) as Grid)
                    .Where(g => g != null && g.Curve is Line)
                    .ToList();

                if (!grids.Any())
                {
                    pushLog("No valid grids found.", "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                var elements = new List<Element>();
                foreach (var bic in SelectedBics)
                {
                    var found = new FilteredElementCollector(doc)
                        .OfCategoryId(new ElementId(bic))
                        .WhereElementIsNotElementType()
                        .ToList();
                    elements.AddRange(found);
                }

                pushLog($"Found {elements.Count} elements across {SelectedBics.Count} category(ies).", "info");
                pushLog($"Splitting at {grids.Count} grid plane(s)...", "info");

                SplitStats stats;
                using (var tx = new Transaction(doc, "Split Elements by Grid"))
                {
                    var fho = tx.GetFailureHandlingOptions();
                    fho.SetClearAfterRollback(true);
                    tx.SetFailureHandlingOptions(fho);
                    tx.Start();

                    stats = SplitElementsShared.SplitByGrid(doc, elements, grids);

                    tx.Commit();
                }

                foreach (var entry in stats.Log)
                {
                    string status = entry.StartsWith("✓") ? "pass"
                                  : entry.StartsWith("✗") ? "fail"
                                  : "info";
                    pushLog(entry, status);
                }

                onProgress(100, stats.SplitCount, stats.FailCount, stats.SkipCount);
                onComplete(stats.SplitCount, stats.FailCount, stats.SkipCount);
            }
            catch (Exception ex)
            {
                pushLog($"Error: {ex.Message}", "fail");
                onComplete(0, 1, 0);
            }
        }
    }
}
