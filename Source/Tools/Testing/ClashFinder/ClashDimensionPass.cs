using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Testing
{
    /// <summary>Aggregate result of one discovery pass.</summary>
    public struct ClashDimPassResult
    {
        public int CrossLines;     // tagged detail curves found
        public int ValidRefs;      // curves that yielded an end-point reference
        public int FilledRegions;  // tagged filled regions found
    }

    /// <summary>
    /// Discovery-only dimension pass. For each view it re-finds the tagged clash cross lines
    /// (and filled regions) the marking engine placed, re-obtains a geometry reference for
    /// each line, and reports the counts. No dimensions are placed yet — that is a separate
    /// rework. Read-only: it never needs a transaction.
    /// </summary>
    public static class ClashDimensionPass
    {
        public static ClashDimPassResult Run(
            Document doc, IList<ElementId> viewIds, Action<string, string> log)
        {
            log = log ?? ((a, b) => { });
            var total = new ClashDimPassResult();

            foreach (var viewId in viewIds)
            {
                var view = doc.GetElement(viewId) as View;
                if (view == null) continue;

                int crossLines    = 0;
                int validRefs     = 0;
                int filledRegions = 0;

                // Tagged cross lines (detail curves).
                try
                {
                    var curves = new FilteredElementCollector(doc, viewId)
                        .OfCategory(BuiltInCategory.OST_Lines)
                        .WhereElementIsNotElementType()
                        .Where(ClashTagSchema.IsOurs)
                        .OfType<DetailCurve>();

                    foreach (var dc in curves)
                    {
                        crossLines++;
                        var gref = dc.GeometryCurve?.GetEndPointReference(1);
                        if (gref != null) validRefs++;
                    }
                }
                catch (Exception ex) { LemoineLog.Swallowed("ClashDimensionPass: collect tagged cross lines", ex); }

                // Tagged filled regions.
                try
                {
                    filledRegions = new FilteredElementCollector(doc, viewId)
                        .OfClass(typeof(FilledRegion))
                        .WhereElementIsNotElementType()
                        .Count(ClashTagSchema.IsOurs);
                }
                catch (Exception ex) { LemoineLog.Swallowed("ClashDimensionPass: collect tagged filled regions", ex); }

                log($"'{view.Name}': {crossLines} cross line(s), {validRefs} reference(s), {filledRegions} marker region(s).", "info");

                total.CrossLines    += crossLines;
                total.ValidRefs     += validRefs;
                total.FilledRegions += filledRegions;
            }

            log($"Dimension pass (discovery): {total.CrossLines} cross line(s), {total.ValidRefs} valid reference(s), {total.FilledRegions} marker region(s) across {viewIds.Count} view(s).",
                total.CrossLines > 0 ? "pass" : "info");
            return total;
        }
    }
}
