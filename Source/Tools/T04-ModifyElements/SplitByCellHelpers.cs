using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace LemoineTools.Tools.ModifyElements
{
    internal enum CellSplitStatus { Split, FitsInOneCell, NoGeometry, NoCellsIntersected }

    /// <summary>
    /// Geometry helpers and element-recreation logic for SplitByCellEventHandler.
    /// </summary>
    internal static class SplitByCellHelpers
    {
        internal static readonly BuiltInCategory[] SupportedCategories =
        {
            BuiltInCategory.OST_Floors,
            BuiltInCategory.OST_Ceilings,
            BuiltInCategory.OST_Roofs,
            BuiltInCategory.OST_StructuralFoundation,
            BuiltInCategory.OST_FilledRegion,
        };

        // ── Per-element split ─────────────────────────────────────────────────

        /// <summary>
        /// Splits one element into grid cells.
        /// Returns the number of replacement cells and a status code.
        /// Throws if any cell recreation fails so the caller's transaction rolls back cleanly,
        /// leaving the original element intact with no partial cells in the model.
        /// </summary>
        internal static (int CellCount, CellSplitStatus Status) SplitElement(
            Document doc,
            Element  el,
            double   cellX,
            double   cellY,
            XYZ?     gridOrigin)
        {
            // Fail fast for element types we don't know how to recreate.
            if (!IsSupportedForRecreation(el))
                throw new NotSupportedException(
                    $"'{el.Category?.Name ?? el.GetType().Name}' elements are not yet supported for cell splitting.");

            Solid? elementSolid = GetPrimarySolid(el);
            if (elementSolid == null || elementSolid.Volume < 1e-9)
                return (0, CellSplitStatus.NoGeometry);

            BoundingBoxXYZ bb = el.get_BoundingBox(null);
            if (bb == null) return (0, CellSplitStatus.NoGeometry);

            double ox = gridOrigin?.X ?? bb.Min.X;
            double oy = gridOrigin?.Y ?? bb.Min.Y;

            double startX = Math.Floor((bb.Min.X - ox) / cellX) * cellX + ox;
            double startY = Math.Floor((bb.Min.Y - oy) / cellY) * cellY + oy;

            var cellsX = new List<(double Min, double Max)>();
            for (double x = startX; x < bb.Max.X; x += cellX)
                cellsX.Add((x, x + cellX));

            var cellsY = new List<(double Min, double Max)>();
            for (double y = startY; y < bb.Max.Y; y += cellY)
                cellsY.Add((y, y + cellY));

            if (cellsX.Count == 1 && cellsY.Count == 1)
                return (0, CellSplitStatus.FitsInOneCell);

            double zMid    = (bb.Min.Z + bb.Max.Z) * 0.5;
            double halfH   = Math.Max((bb.Max.Z - bb.Min.Z) * 0.5, 0.5) + 1.0;
            double zBottom = zMid - halfH;
            double zTop    = zMid + halfH;

            var newIds = new List<ElementId>();

            foreach (var (xMin, xMax) in cellsX)
            {
                foreach (var (yMin, yMax) in cellsY)
                {
                    Solid? cellSolid = MakeCellSolid(xMin, xMax, yMin, yMax, zBottom, zTop);
                    if (cellSolid == null) continue;

                    Solid intersection;
                    try
                    {
                        intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
                            elementSolid, cellSolid,
                            BooleanOperationsType.Intersect);
                    }
                    catch { continue; }

                    if (intersection == null || intersection.Volume < 1e-9)
                        continue;

                    IList<CurveLoop>? loops = ExtractBottomFaceLoops(intersection);
                    if (loops == null || loops.Count == 0) continue;

                    // RecreateElement propagates Revit API exceptions so the caller's transaction
                    // rolls back; InvalidElementId means unsupported/misconfigured element type.
                    ElementId newId = RecreateElement(doc, el, loops);
                    if (newId == null || newId == ElementId.InvalidElementId)
                        throw new InvalidOperationException(
                            $"Cell ({xMin:F2}–{xMax:F2}, {yMin:F2}–{yMax:F2}): element recreation failed.");
                    newIds.Add(newId);
                }
            }

            if (newIds.Count == 0) return (0, CellSplitStatus.NoCellsIntersected);

            doc.Delete(el.Id);
            return (newIds.Count, CellSplitStatus.Split);
        }

        // ── Geometry ──────────────────────────────────────────────────────────

        internal static Solid? GetPrimarySolid(Element el)
        {
            var opts = new Options
            {
                DetailLevel              = ViewDetailLevel.Fine,
                ComputeReferences        = false,
                IncludeNonVisibleObjects = false,
            };

            GeometryElement geo = el.get_Geometry(opts);
            if (geo == null) return null;

            Solid? best = null;
            foreach (GeometryObject obj in geo)
            {
                if (obj is Solid s)
                {
                    if (s.Volume > (best?.Volume ?? 0)) best = s;
                }
                else if (obj is GeometryInstance gi)
                {
                    foreach (GeometryObject inner in gi.GetInstanceGeometry())
                        if (inner is Solid si && si.Volume > (best?.Volume ?? 0))
                            best = si;
                }
            }

            return best;
        }

        private static Solid? MakeCellSolid(
            double xMin, double xMax,
            double yMin, double yMax,
            double zBottom, double zTop)
        {
            try
            {
                var loop = new CurveLoop();
                loop.Append(Line.CreateBound(new XYZ(xMin, yMin, zBottom), new XYZ(xMax, yMin, zBottom)));
                loop.Append(Line.CreateBound(new XYZ(xMax, yMin, zBottom), new XYZ(xMax, yMax, zBottom)));
                loop.Append(Line.CreateBound(new XYZ(xMax, yMax, zBottom), new XYZ(xMin, yMax, zBottom)));
                loop.Append(Line.CreateBound(new XYZ(xMin, yMax, zBottom), new XYZ(xMin, yMin, zBottom)));

                return GeometryCreationUtilities.CreateExtrusionGeometry(
                    new List<CurveLoop> { loop },
                    XYZ.BasisZ,
                    zTop - zBottom);
            }
            catch { return null; }
        }

        private static IList<CurveLoop>? ExtractBottomFaceLoops(Solid solid)
        {
            PlanarFace? bottomFace = null;
            double lowestZ = double.MaxValue;

            foreach (Face face in solid.Faces)
            {
                if (!(face is PlanarFace pf)) continue;
                if (Math.Abs(Math.Abs(pf.FaceNormal.Z) - 1.0) > 1e-6) continue;
                if (pf.Origin.Z < lowestZ)
                {
                    lowestZ    = pf.Origin.Z;
                    bottomFace = pf;
                }
            }

            return bottomFace?.GetEdgesAsCurveLoops();
        }

        // ── Element recreation ────────────────────────────────────────────────

        // Floor, Ceiling, FilledRegion; OST_StructuralFoundation slabs are Floor instances.
        private static bool IsSupportedForRecreation(Element el) =>
            el is Floor || el is Ceiling || el is FilledRegion;

        // No outer try/catch: Revit API exceptions propagate to SplitElement's caller so
        // the per-element transaction can roll back atomically.
        private static ElementId RecreateElement(
            Document doc, Element source, IList<CurveLoop> loops)
        {
            if (source is Floor floor)
                return CreateFloor(doc, floor, loops);

            if (source is Ceiling ceiling)
                return CreateCeiling(doc, ceiling, loops);

            if (source is FilledRegion fr)
                return CreateFilledRegion(doc, fr, loops);

            // Should not be reachable — IsSupportedForRecreation guards this path.
            throw new NotSupportedException(
                $"'{source.Category?.Name ?? source.GetType().Name}' elements are not supported for cell splitting.");
        }

        private static ElementId CreateFloor(
            Document doc, Floor source, IList<CurveLoop> loops)
        {
            Level? level = doc.GetElement(
                source.get_Parameter(BuiltInParameter.LEVEL_PARAM)?.AsElementId()
                ?? ElementId.InvalidElementId) as Level
                ?? doc.GetElement(source.LevelId) as Level;

            if (level == null) return ElementId.InvalidElementId;

            FloorType? ft = doc.GetElement(source.GetTypeId()) as FloorType;
            if (ft == null) return ElementId.InvalidElementId;

            bool isStructural = source.get_Parameter(
                BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() == 1;

            Floor newFloor = Floor.Create(doc, loops, ft.Id, level.Id, isStructural, null, 0.0);

            double offset = source.get_Parameter(
                BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0.0;
            newFloor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(offset);

            return newFloor.Id;
        }

        private static ElementId CreateCeiling(
            Document doc, Ceiling source, IList<CurveLoop> loops)
        {
            Level? level = doc.GetElement(source.LevelId) as Level;
            if (level == null) return ElementId.InvalidElementId;

            CeilingType? ct = doc.GetElement(source.GetTypeId()) as CeilingType;
            if (ct == null) return ElementId.InvalidElementId;

            Ceiling newCeiling = Ceiling.Create(doc, loops, ct.Id, level.Id);

            double offset = source.get_Parameter(
                BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0.0;
            newCeiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)?.Set(offset);

            return newCeiling.Id;
        }

        private static ElementId CreateFilledRegion(
            Document doc, FilledRegion source, IList<CurveLoop> loops)
        {
            FilledRegion newRegion = FilledRegion.Create(
                doc, source.GetTypeId(), source.OwnerViewId, loops);
            return newRegion.Id;
        }

        // ── Project base point ────────────────────────────────────────────────

        /// <summary>
        /// Returns the project base point XY location, or null if not found.
        /// Callers should log the absence and fall back to XYZ.Zero.
        /// </summary>
        internal static XYZ? GetProjectBasePoint(Document doc)
        {
            try
            {
                var bp = new FilteredElementCollector(doc)
                    .OfClass(typeof(BasePoint))
                    .Cast<BasePoint>()
                    .FirstOrDefault(p => !p.IsShared);

                if (bp != null)
                {
                    return new XYZ(
                        bp.get_Parameter(BuiltInParameter.BASEPOINT_EASTWEST_PARAM)?.AsDouble()  ?? 0,
                        bp.get_Parameter(BuiltInParameter.BASEPOINT_NORTHSOUTH_PARAM)?.AsDouble() ?? 0,
                        0);
                }
            }
            catch { }

            return null;
        }

        // ── Category map builder ──────────────────────────────────────────────

        /// <summary>
        /// Builds a map from BuiltInCategory to element IDs visible in the given view.
        /// Keyed by BIC enum (not Category.Name) so matching works in non-English Revit.
        /// </summary>
        internal static Dictionary<BuiltInCategory, List<ElementId>> BuildCategoryMap(
            Document doc, View view)
        {
            var map = new Dictionary<BuiltInCategory, List<ElementId>>();

            foreach (BuiltInCategory bic in SupportedCategories)
            {
                List<ElementId> ids;

                if (bic == BuiltInCategory.OST_FilledRegion)
                {
                    ids = new FilteredElementCollector(doc, view.Id)
                        .OfClass(typeof(FilledRegion))
                        .WhereElementIsNotElementType()
                        .Select(e => e.Id)
                        .ToList();
                }
                else
                {
                    ids = new FilteredElementCollector(doc, view.Id)
                        .OfCategoryId(new ElementId(bic))
                        .WhereElementIsNotElementType()
                        .Select(e => e.Id)
                        .ToList();
                }

                if (ids.Count == 0) continue;

                if (!map.ContainsKey(bic))
                    map[bic] = new List<ElementId>();
                map[bic].AddRange(ids);
            }

            return map;
        }
    }
}
