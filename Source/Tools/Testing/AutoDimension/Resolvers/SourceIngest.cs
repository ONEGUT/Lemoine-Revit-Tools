using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Testing.AutoDimension.Resolvers
{
    /// <summary>
    /// Step 1 of the pipeline: find the Clash Finder's tagged cross-line markers in a view and
    /// validate that each yields a usable endpoint reference. Lines carrying no reference are
    /// reported as unresolved sources (never silently skipped). Read-only — no transaction.
    /// </summary>
    public static class SourceIngest
    {
        /// <summary>
        /// Collects valid source lines from <paramref name="view"/>, appending any line that
        /// cannot produce an endpoint reference to <paramref name="unresolved"/>. Results are
        /// returned in a deterministic order (by ElementId).
        /// </summary>
        public static List<SourceLine> Collect(
            Document doc, View view, ViewProjection projection,
            List<Core.UnresolvedTarget> unresolved)
        {
            var result = new List<SourceLine>();
            if (doc == null || view == null) return result;

            List<DetailCurve> curves;
            try
            {
                curves = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_Lines)
                    .WhereElementIsNotElementType()
                    .Where(ClashTagSchema.IsOurs)
                    .OfType<DetailCurve>()
                    .OrderBy(dc => dc.Id.IntegerValue)
                    .ToList();
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("SourceIngest: collect tagged cross lines", ex);
                return result;
            }

            foreach (var dc in curves)
            {
                string key = dc.Id.IntegerValue.ToString();
                Curve? gc = null;
                try { gc = dc.GeometryCurve; }
                catch (Exception ex) { LemoineLog.Swallowed("SourceIngest: read geometry curve", ex); }

                Reference? sref = null;
                XYZ? anchor = null;
                if (gc != null)
                {
                    try
                    {
                        sref   = gc.GetEndPointReference(1);
                        anchor = gc.GetEndPoint(1);
                    }
                    catch (Exception ex) { LemoineLog.Swallowed("SourceIngest: endpoint reference", ex); }
                }

                if (sref == null || anchor == null)
                {
                    unresolved.Add(new Core.UnresolvedTarget
                    {
                        SourceKey = key,
                        Reason    = "source cross-line carries no usable endpoint reference",
                    });
                    continue;
                }

                result.Add(new SourceLine
                {
                    SourceKey = key,
                    Id        = dc.Id,
                    SourceRef = sref,
                    Anchor3d  = anchor,
                    Anchor2d  = projection.To2D(anchor),
                });
            }

            return result;
        }
    }
}
