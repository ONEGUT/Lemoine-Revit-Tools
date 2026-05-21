using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace LemoineTools.Tools.Testing
{
    /// <summary>
    /// Places dimension strings in bulk on the Revit API thread.
    /// Set all properties before calling ExternalEvent.Raise().
    /// </summary>
    public class BatchDimensionEventHandler : IExternalEventHandler
    {
        // ── Inputs set by ViewModel before Raise ──────────────────────────────
        public List<ElementId>         ViewIds      { get; set; } = new List<ElementId>();
        public List<DimCategoryConfig> Categories   { get; set; } = new List<DimCategoryConfig>();
        public string                  DimStyleName { get; set; } = "";

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?    PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "BatchDimension";

        public void Execute(UIApplication app)
        {
            var pushLog    = PushLog    ?? ((m, s) => { });
            var onProgress = OnProgress ?? ((p, a, b, c) => { });
            var onComplete = OnComplete ?? ((a, b, c) => { });

            int pass = 0, fail = 0, skip = 0;

            try
            {
                var doc = app.ActiveUIDocument.Document;

                // Resolve DimensionType
                var dimType = new FilteredElementCollector(doc)
                    .OfClass(typeof(DimensionType))
                    .Cast<DimensionType>()
                    .FirstOrDefault(dt => dt.Name == DimStyleName);

                if (dimType == null)
                {
                    pushLog($"Dimension style not found: '{DimStyleName}'", "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                int viewCount = ViewIds.Count;

                for (int vi = 0; vi < viewCount; vi++)
                {
                    var view = doc.GetElement(ViewIds[vi]) as View;
                    if (view == null) { skip++; continue; }

                    pushLog($"Processing view: {view.Name}", "info");

                    try
                    {
                        using (var tx = new Transaction(doc, "Lemoine — Batch Dimension"))
                        {
                            var fho = tx.GetFailureHandlingOptions();
                            fho.SetClearAfterRollback(true);
                            tx.SetFailureHandlingOptions(fho);
                            tx.Start();

                            foreach (var catConfig in Categories)
                            {
                                DimensionViewByCategory(doc, view, catConfig, dimType,
                                    pushLog, ref pass, ref fail, ref skip);
                            }

                            tx.Commit();
                        }
                    }
                    catch (Exception ex)
                    {
                        pushLog($"View '{view.Name}' error: {ex.Message}", "fail");
                        fail++;
                    }

                    int pct = 10 + (int)(vi * 85.0 / viewCount);
                    onProgress(pct, pass, fail, skip);
                }

                onProgress(100, pass, fail, skip);
                onComplete(pass, fail, skip);
            }
            catch (Exception ex)
            {
                pushLog($"Batch Dimension error: {ex.Message}", "fail");
                onComplete(pass, 1, skip);
            }
        }

        // ── Per-category dimensioning ─────────────────────────────────────────

        private static void DimensionViewByCategory(
            Document         doc,
            View             view,
            DimCategoryConfig catConfig,
            DimensionType    dimType,
            Action<string, string> pushLog,
            ref int pass, ref int fail, ref int skip)
        {
            try
            {
                // Resolve BuiltInCategory from OST string
                if (!Enum.TryParse<BuiltInCategory>(catConfig.BuiltInCatOST, out var bic))
                {
                    pushLog($"Unknown category '{catConfig.BuiltInCatOST}' — skipped.", "fail");
                    skip++;
                    return;
                }

                // Collect elements visible in this view
                var elements = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(bic)
                    .WhereElementIsNotElementType()
                    .ToList();

                if (elements.Count == 0)
                {
                    pushLog($"No {catConfig.CategoryName} in view '{view.Name}' — skipped.", "info");
                    return;
                }

                // Convert offset from mm to internal feet
                double offsetFt = UnitUtils.ConvertToInternalUnits(catConfig.Offset, UnitTypeId.Millimeters);

                // Collect references and bounding box
                var refArray = new ReferenceArray();
                BoundingBoxXYZ? combinedBb = null;

                foreach (var elem in elements)
                {
                    try
                    {
                        var refs = GetReferences(doc, view, elem, catConfig);
                        foreach (var r in refs) refArray.Append(r);

                        var bb = elem.get_BoundingBox(view);
                        if (bb != null)
                        {
                            if (combinedBb == null)
                                combinedBb = bb;
                            else
                            {
                                combinedBb.Min = new XYZ(
                                    Math.Min(combinedBb.Min.X, bb.Min.X),
                                    Math.Min(combinedBb.Min.Y, bb.Min.Y),
                                    combinedBb.Min.Z);
                                combinedBb.Max = new XYZ(
                                    Math.Max(combinedBb.Max.X, bb.Max.X),
                                    Math.Max(combinedBb.Max.Y, bb.Max.Y),
                                    combinedBb.Max.Z);
                            }
                        }
                    }
                    catch { /* skip reference errors per element */ }
                }

                if (refArray.IsEmpty || combinedBb == null)
                {
                    pushLog($"No references collected for {catConfig.CategoryName} in '{view.Name}'.", "info");
                    return;
                }

                // Determine dimension line direction based on view type
                bool isFloorPlanOrRCP = view.ViewType == ViewType.FloorPlan
                                     || view.ViewType == ViewType.CeilingPlan;

                // Horizontal dimension (along X)
                try
                {
                    var lineY = combinedBb.Min.Y - offsetFt;
                    var dimLine = Line.CreateBound(
                        new XYZ(combinedBb.Min.X, lineY, 0),
                        new XYZ(combinedBb.Max.X, lineY, 0));
                    doc.Create.NewDimension(view, dimLine, refArray, dimType);
                    pass++;
                    pushLog($"✓ Dim H {catConfig.CategoryName} in '{view.Name}'", "pass");
                }
                catch (Exception ex)
                {
                    pushLog($"Dim H failed {catConfig.CategoryName}: {ex.Message}", "fail");
                    fail++;
                }

                // Vertical dimension (along Y) — floor plans and RCPs only
                if (isFloorPlanOrRCP)
                {
                    try
                    {
                        var lineX = combinedBb.Min.X - offsetFt;
                        var dimLine = Line.CreateBound(
                            new XYZ(lineX, combinedBb.Min.Y, 0),
                            new XYZ(lineX, combinedBb.Max.Y, 0));
                        doc.Create.NewDimension(view, dimLine, refArray, dimType);
                        pass++;
                        pushLog($"✓ Dim V {catConfig.CategoryName} in '{view.Name}'", "pass");
                    }
                    catch (Exception ex)
                    {
                        pushLog($"Dim V failed {catConfig.CategoryName}: {ex.Message}", "fail");
                        fail++;
                    }
                }
            }
            catch (Exception ex)
            {
                pushLog($"Category {catConfig.CategoryName} error: {ex.Message}", "fail");
                fail++;
            }
        }

        private static List<Reference> GetReferences(
            Document doc, View view, Element elem, DimCategoryConfig catConfig)
        {
            var refs = new List<Reference>();

            // Grid: use direct reference
            if (elem is Grid grid)
            {
                if (grid.IsBubbleVisibleInView(DatumEnds.End0, view))
                    refs.Add(new Reference(grid));
                return refs;
            }

            // Walls: use HostObjectUtils for face references
            if (elem is Wall wall)
            {
                try
                {
                    var extFaces = HostObjectUtils.GetSideFaces(wall, ShellLayerType.Exterior);
                    var intFaces = HostObjectUtils.GetSideFaces(wall, ShellLayerType.Interior);

                    bool useExt = catConfig.ReferencePlane.Contains("Exterior")
                               || catConfig.ReferencePlane.Contains("Center");
                    bool useInt = catConfig.ReferencePlane.Contains("Interior")
                               || catConfig.ReferencePlane.Contains("Center");

                    if (useExt) refs.AddRange(extFaces);
                    if (useInt) refs.AddRange(intFaces);
                    if (!useExt && !useInt) refs.AddRange(extFaces); // fallback
                }
                catch { }
                return refs;
            }

            // Default: use bounding box face references from geometry
            try
            {
                var geom = elem.get_Geometry(new Options { View = view });
                if (geom != null)
                {
                    foreach (GeometryObject obj in geom)
                    {
                        if (obj is Solid solid)
                        {
                            foreach (Face face in solid.Faces)
                            {
                                var r = face.Reference;
                                if (r != null) refs.Add(r);
                                if (refs.Count >= 2) break;
                            }
                        }
                        if (refs.Count >= 2) break;
                    }
                }
            }
            catch { }

            return refs;
        }
    }
}
