using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace LemoineTools.Tools.ModifyElements
{
    public class SplitByReferencePlaneEventHandler : IExternalEventHandler
    {
        public List<BuiltInCategory>      SelectedBics        { get; set; } = new List<BuiltInCategory>();
        public List<ElementId>            SelectedRefPlaneIds { get; set; } = new List<ElementId>();
        public List<ElementId>?           PreSelectedIds      { get; set; }  // E
        public ElementId?                 ActiveViewId        { get; set; }  // B
        public Action<string, string>?    OnLog               { get; set; }
        public Action<int, int, int, int>? OnProgress          { get; set; }
        public Action<int, int, int>?      OnComplete          { get; set; }

        public string GetName() => "SplitByReferencePlane";

        public void Execute(UIApplication app)
        {
            var pushLog    = OnLog      ?? ((m, s) => { });
            var onProgress = OnProgress ?? ((p, a, b, c) => { });
            var onComplete = OnComplete ?? ((a, b, c) => { });

            try
            {
                Document doc = app.ActiveUIDocument.Document;

                var refPlanes = SelectedRefPlaneIds
                    .Select(id => doc.GetElement(id) as ReferencePlane)
                    .Where(r => r != null)
                    .ToList();

                if (!refPlanes.Any())
                {
                    pushLog("No valid reference planes found.", "fail");
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
                    pushLog($"Operating on {elements.Count} pre-selected element(s).", "info");
                }
                else
                {
                    View? view = ActiveViewId != null ? doc.GetElement(ActiveViewId) as View : null;
                    elements = new List<Element>();
                    foreach (var bic in SelectedBics)
                    {
                        var coll = (view != null && !view.IsTemplate)
                            ? new FilteredElementCollector(doc, view.Id)
                            : new FilteredElementCollector(doc);
                        elements.AddRange(coll
                            .OfCategoryId(new ElementId(bic))
                            .WhereElementIsNotElementType()
                            .ToList());
                    }
                    pushLog($"Found {elements.Count} elements across {SelectedBics.Count} category(ies).", "info");
                }

                pushLog($"Splitting at {refPlanes.Count} reference plane(s)...", "info");

                SplitStats stats;
                using (var tx = new Transaction(doc, "Split Elements by Reference Plane"))
                {
                    var fho = tx.GetFailureHandlingOptions();
                    fho.SetClearAfterRollback(true);
                    tx.SetFailureHandlingOptions(fho);
                    tx.Start();

                    stats = SplitElementsShared.SplitByReferencePlane(doc, elements, refPlanes);

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
