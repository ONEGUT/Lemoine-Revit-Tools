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

            // FilledRegion is a 2D element — no solid geometry; clip its boundary curves directly.
            if (el is FilledRegion fr2d)
                return SplitFilledRegion(doc, fr2d, cellX, cellY, gridOrigin);

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

            // ── Phase 1: compute all cell shapes BEFORE touching the document ────
            // RecreateElement modifies the document, which invalidates the elementSolid
            // reference held by Revit's geometry engine. All boolean intersections must
            // be completed before any element is created.
            var pendingLoops = new List<IList<CurveLoop>>();

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

                    IList<CurveLoop>? loops = ExtractTopFaceLoops(intersection);
                    if (loops == null || loops.Count == 0) continue;

                    pendingLoops.Add(loops);
                }
            }

            if (pendingLoops.Count == 0) return (0, CellSplitStatus.NoCellsIntersected);

            // ── Phase 2: create replacement elements (document modification) ──────
            // RecreateElement propagates Revit API exceptions so the caller's transaction
            // rolls back; InvalidElementId means unsupported/misconfigured element type.
            foreach (var loops in pendingLoops)
            {
                ElementId newId = RecreateElement(doc, el, loops);
                if (newId == null || newId == ElementId.InvalidElementId)
                    throw new InvalidOperationException("Cell element recreation failed.");
                newIds.Add(newId);
            }

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

        // Use the top face (normal = +Z) so GetEdgesAsCurveLoops returns CCW loops when
        // viewed from above — the orientation Floor.Create and Ceiling.Create require.
        // The bottom face (normal = -Z) returns CW loops, which those APIs reject.
        private static IList<CurveLoop>? ExtractTopFaceLoops(Solid solid)
        {
            PlanarFace? topFace  = null;
            double      highestZ = double.MinValue;

            foreach (Face face in solid.Faces)
            {
                if (!(face is PlanarFace pf)) continue;
                if (pf.FaceNormal.Z < 1.0 - 1e-6) continue; // only upward-facing
                if (pf.Origin.Z > highestZ)
                {
                    highestZ = pf.Origin.Z;
                    topFace  = pf;
                }
            }

            return topFace?.GetEdgesAsCurveLoops();
        }

        // ── FilledRegion 2D split ─────────────────────────────────────────────

        // FilledRegion has no 3D solid; clip its sketch boundary directly in XY using
        // Sutherland-Hodgman against each cell rectangle. Only line-segment boundaries
        // are supported — arcs/splines cause that region to be skipped gracefully.
        private static (int CellCount, CellSplitStatus Status) SplitFilledRegion(
            Document      doc,
            FilledRegion  fr,
            double        cellX,
            double        cellY,
            XYZ?          gridOrigin)
        {
            IList<CurveLoop> boundaries = fr.GetBoundaries();
            if (boundaries == null || boundaries.Count == 0)
                return (0, CellSplitStatus.NoGeometry);

            CurveLoop outerLoop = boundaries[0];

            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            double z    = 0;
            bool   first = true;

            foreach (Curve c in outerLoop)
            {
                XYZ p = c.GetEndPoint(0);
                if (first) { z = p.Z; first = false; }
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }

            double ox     = gridOrigin?.X ?? minX;
            double oy     = gridOrigin?.Y ?? minY;
            double startX = Math.Floor((minX - ox) / cellX) * cellX + ox;
            double startY = Math.Floor((minY - oy) / cellY) * cellY + oy;

            var cellsX = new List<(double Min, double Max)>();
            for (double x = startX; x < maxX; x += cellX)
                cellsX.Add((x, x + cellX));

            var cellsY = new List<(double Min, double Max)>();
            for (double y = startY; y < maxY; y += cellY)
                cellsY.Add((y, y + cellY));

            if (cellsX.Count == 1 && cellsY.Count == 1)
                return (0, CellSplitStatus.FitsInOneCell);

            // Phase 1: clip outer boundary + any holes against each cell rectangle.
            var pending = new List<IList<CurveLoop>>();

            foreach (var (xMin, xMax) in cellsX)
            {
                foreach (var (yMin, yMax) in cellsY)
                {
                    List<XYZ>? clippedOuter = ClipLoopToRect(outerLoop, xMin, xMax, yMin, yMax, z);
                    if (clippedOuter == null || clippedOuter.Count < 3) continue;

                    CurveLoop? outer = PointsToLoop(clippedOuter);
                    if (outer == null) continue;

                    var cellLoops = new List<CurveLoop> { outer };

                    // Clip holes (inner loops, index 1+) against the same cell rectangle.
                    for (int hi = 1; hi < boundaries.Count; hi++)
                    {
                        List<XYZ>? clippedHole = ClipLoopToRect(boundaries[hi], xMin, xMax, yMin, yMax, z);
                        if (clippedHole == null || clippedHole.Count < 3) continue;
                        CurveLoop? hole = PointsToLoop(clippedHole);
                        if (hole != null) cellLoops.Add(hole);
                    }

                    pending.Add(cellLoops);
                }
            }

            if (pending.Count == 0) return (0, CellSplitStatus.NoCellsIntersected);

            // Phase 2: create replacement FilledRegions.
            var newIds = new List<ElementId>();
            foreach (var loops in pending)
            {
                FilledRegion newFr = FilledRegion.Create(doc, fr.GetTypeId(), fr.OwnerViewId, loops);
                newIds.Add(newFr.Id);
            }

            doc.Delete(fr.Id);
            return (newIds.Count, CellSplitStatus.Split);
        }

        // Sutherland-Hodgman clip of a CurveLoop against an axis-aligned rectangle.
        // Returns null if the loop has non-linear curves (unsupported).
        private static List<XYZ>? ClipLoopToRect(
            CurveLoop loop,
            double xMin, double xMax, double yMin, double yMax, double z)
        {
            var polygon = new List<XYZ>();
            foreach (Curve c in loop)
            {
                if (!(c is Line)) return null;
                polygon.Add(c.GetEndPoint(0));
            }

            if (polygon.Count < 3) return null;

            // Clip against each of the four half-planes.
            var pts = SHClip(polygon, p => p.X >= xMin, (a, b) => IntersectAtX(a, b, xMin, z));
            if (pts.Count < 3) return null;
            pts = SHClip(pts,     p => p.X <= xMax, (a, b) => IntersectAtX(a, b, xMax, z));
            if (pts.Count < 3) return null;
            pts = SHClip(pts,     p => p.Y >= yMin, (a, b) => IntersectAtY(a, b, yMin, z));
            if (pts.Count < 3) return null;
            pts = SHClip(pts,     p => p.Y <= yMax, (a, b) => IntersectAtY(a, b, yMax, z));
            return pts.Count >= 3 ? pts : null;
        }

        private static List<XYZ> SHClip(
            List<XYZ>       polygon,
            Func<XYZ, bool> inside,
            Func<XYZ, XYZ, XYZ> intersect)
        {
            var output = new List<XYZ>();
            int n = polygon.Count;
            for (int i = 0; i < n; i++)
            {
                XYZ curr = polygon[i];
                XYZ prev = polygon[(i + n - 1) % n];
                bool currIn = inside(curr);
                bool prevIn = inside(prev);
                if (currIn)
                {
                    if (!prevIn) output.Add(intersect(prev, curr));
                    output.Add(curr);
                }
                else if (prevIn)
                {
                    output.Add(intersect(prev, curr));
                }
            }
            return output;
        }

        private static XYZ IntersectAtX(XYZ a, XYZ b, double x, double z)
        {
            double t = (x - a.X) / (b.X - a.X);
            return new XYZ(x, a.Y + t * (b.Y - a.Y), z);
        }

        private static XYZ IntersectAtY(XYZ a, XYZ b, double y, double z)
        {
            double t = (y - a.Y) / (b.Y - a.Y);
            return new XYZ(a.X + t * (b.X - a.X), y, z);
        }

        private static CurveLoop? PointsToLoop(List<XYZ> pts)
        {
            try
            {
                // Remove duplicate adjacent points.
                var clean = new List<XYZ>();
                for (int i = 0; i < pts.Count; i++)
                {
                    if (pts[i].DistanceTo(pts[(i + 1) % pts.Count]) > 1e-6)
                        clean.Add(pts[i]);
                }
                if (clean.Count < 3) return null;

                var loop = new CurveLoop();
                for (int i = 0; i < clean.Count; i++)
                    loop.Append(Line.CreateBound(clean[i], clean[(i + 1) % clean.Count]));
                return loop;
            }
            catch { return null; }
        }

        // ── Element recreation ────────────────────────────────────────────────

        // Floor, Ceiling, FilledRegion; OST_StructuralFoundation slabs are Floor instances.
        // RoofBase (FootPrintRoof only — ExtrusionRoof is rejected inside CreateRoof).
        private static bool IsSupportedForRecreation(Element el) =>
            el is Floor || el is Ceiling || el is FilledRegion || el is RoofBase;

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

            if (source is RoofBase roof)
                return CreateRoof(doc, roof, loops);

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

        private static ElementId CreateRoof(
            Document doc, RoofBase source, IList<CurveLoop> loops)
        {
            if (!(source is FootPrintRoof))
                throw new NotSupportedException(
                    $"Only footprint roofs can be cell-split; extrusion roofs are not supported.");

            Level? level = doc.GetElement(
                source.get_Parameter(BuiltInParameter.ROOF_BASE_LEVEL_PARAM)?.AsElementId()
                ?? ElementId.InvalidElementId) as Level;

            if (level == null)
                throw new InvalidOperationException($"Roof {source.Id}: host level not found.");

            RoofType? rt = doc.GetElement(source.GetTypeId()) as RoofType;
            if (rt == null)
                throw new InvalidOperationException($"Roof {source.Id}: roof type not found.");

            var footprint = new CurveArray();
            foreach (Curve c in loops[0])
                footprint.Append(c);

            ModelCurveArray unused;
            FootPrintRoof newRoof = doc.Create.NewFootPrintRoof(footprint, level, rt, out unused);

            // Preserve vertical positioning
            double baseOffset = source.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM)?.AsDouble() ?? 0.0;
            newRoof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM)?.Set(baseOffset);

            // Preserve cutoff level if set
            ElementId? cutoffId = source.get_Parameter(BuiltInParameter.ROOF_UPTO_LEVEL_PARAM)?.AsElementId();
            if (cutoffId != null && cutoffId != ElementId.InvalidElementId)
            {
                newRoof.get_Parameter(BuiltInParameter.ROOF_UPTO_LEVEL_PARAM)?.Set(cutoffId);
                double cutoffOffset = source.get_Parameter(BuiltInParameter.ROOF_UPTO_LEVEL_OFFSET_PARAM)?.AsDouble() ?? 0.0;
                newRoof.get_Parameter(BuiltInParameter.ROOF_UPTO_LEVEL_OFFSET_PARAM)?.Set(cutoffOffset);
            }

            return newRoof.Id;
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
