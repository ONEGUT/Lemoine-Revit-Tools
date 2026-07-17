using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Framework;

namespace LemoineTools.Tools.ModifyElements
{
    internal enum CellSplitStatus { Split, FitsInOneCell, NoGeometry, NoCellsIntersected, Sloped }

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
        /// Returns the number of replacement cells created, the number of cells that were
        /// dropped (boolean-intersect or recreation failures), and a status code.
        /// A cell that can't be recreated is skipped and logged — the element still splits
        /// from the cells that succeeded, and the original is deleted only once at least one
        /// replacement cell exists. If NO cell survives, the original is left intact.
        /// </summary>
        internal static (int CellCount, int DroppedCells, CellSplitStatus Status) SplitElement(
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
                return (0, 0, CellSplitStatus.NoGeometry);

            // A sloped/non-planar element has no horizontal top face, so cell extraction (which
            // reads the intersection's up-facing face) can never return a loop — every cell would
            // silently drop.
            //
            // For a sloped FOOTPRINT ROOF we recover: cut a perfectly-flat proxy of the same
            // footprint (which DOES have a horizontal top face), then re-slope each resulting cell
            // roof back onto the original roof surface. Any other sloped element is still reported
            // honestly as "sloped, not supported".
            bool                             reslopeRoof  = false;
            List<UpFace>?                    slopeFaces   = null;
            if (!HasHorizontalTopFace(elementSolid))
            {
                if (el is FootPrintRoof)
                {
                    slopeFaces   = CaptureUpwardFaces(elementSolid);
                    Solid? proxy = BuildFlatProxySolid((FootPrintRoof)el, elementSolid);
                    if (proxy == null || proxy.Volume < 1e-9 || slopeFaces.Count == 0)
                        return (0, 0, CellSplitStatus.Sloped);
                    elementSolid = proxy;   // cut the flat proxy; re-slope the results below
                    reslopeRoof  = true;
                }
                else
                {
                    return (0, 0, CellSplitStatus.Sloped);
                }
            }

            BoundingBoxXYZ bb = el.get_BoundingBox(null);
            if (bb == null) return (0, 0, CellSplitStatus.NoGeometry);

            double ox = gridOrigin?.X ?? bb.Min.X;
            double oy = gridOrigin?.Y ?? bb.Min.Y;

            double startX = Math.Floor((bb.Min.X - ox) / cellX) * cellX + ox;
            double startY = Math.Floor((bb.Min.Y - oy) / cellY) * cellY + oy;

            // Use an integer cell count rather than accumulating `x += cellX`: repeated addition
            // drifts by a float epsilon over many cells and can drop the trailing column/row when
            // the running value lands just past bb.Max. The small epsilon stops an exact span edge
            // from spawning a zero-width sliver cell.
            int nx = Math.Max(1, (int)Math.Ceiling((bb.Max.X - startX) / cellX - 1e-9));
            int ny = Math.Max(1, (int)Math.Ceiling((bb.Max.Y - startY) / cellY - 1e-9));

            var cellsX = new List<(double Min, double Max)>(nx);
            for (int i = 0; i < nx; i++)
            {
                double x = startX + i * cellX;
                cellsX.Add((x, x + cellX));
            }

            var cellsY = new List<(double Min, double Max)>(ny);
            for (int j = 0; j < ny; j++)
            {
                double y = startY + j * cellY;
                cellsY.Add((y, y + cellY));
            }

            if (cellsX.Count == 1 && cellsY.Count == 1)
                return (0, 0, CellSplitStatus.FitsInOneCell);

            double zMid    = (bb.Min.Z + bb.Max.Z) * 0.5;
            double halfH   = Math.Max((bb.Max.Z - bb.Min.Z) * 0.5, 0.5) + 1.0;
            double zBottom = zMid - halfH;
            double zTop    = zMid + halfH;

            var newIds = new List<ElementId>();
            int dropped = 0;   // cells lost to a boolean-intersect or recreation failure

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
                    catch (Exception ex)
                    {
                        // A cell that can't be intersected is a real dropped cell — count it so
                        // the run log can tell the user cells were lost, not just diagnostics.log.
                        dropped++;
                        DiagnosticsLog.Swallowed($"SplitByCell: cell boolean intersect failed on element {el.Id.Value}", ex);
                        continue;
                    }

                    // An empty/zero-volume intersection is a cell the element doesn't reach
                    // (a corner of the grid outside the slab) — expected, not a dropped cell.
                    if (intersection == null || intersection.Volume < 1e-9)
                        continue;

                    IList<CurveLoop>? loops = ExtractTopFaceLoops(intersection);
                    if (loops == null || loops.Count == 0) continue;

                    pendingLoops.Add(loops);
                }
            }

            if (pendingLoops.Count == 0) return (0, dropped, CellSplitStatus.NoCellsIntersected);

            // ── Phase 2: create replacement elements (document modification) ──────
            // Skip-and-log a cell that can't be recreated (e.g. a sliver from a grid line
            // landing on the slab edge) rather than throwing away the whole element — an
            // element that splits 95% cleanly should not fail 100%. The original is deleted
            // only if at least one replacement cell was actually created.
            foreach (var loops in pendingLoops)
            {
                ElementId newId;
                try
                {
                    newId = RecreateElement(doc, el, loops);
                }
                catch (Exception ex)
                {
                    dropped++;
                    DiagnosticsLog.Swallowed($"SplitByCell: cell recreation threw on element {el.Id.Value}", ex);
                    continue;
                }

                if (newId == null || newId == ElementId.InvalidElementId)
                {
                    dropped++;
                    DiagnosticsLog.Swallowed(
                        $"SplitByCell: cell recreation returned InvalidElementId on element {el.Id.Value}",
                        new InvalidOperationException("RecreateElement returned InvalidElementId."));
                    continue;
                }

                newIds.Add(newId);
            }

            // Every cell failed to recreate — leave the original intact rather than deleting it.
            if (newIds.Count == 0) return (0, dropped, CellSplitStatus.NoCellsIntersected);

            // Sloped-roof path: the cells were cut from a FLAT proxy, so the new roofs are flat.
            // Re-slope each one back onto the original roof surface before removing the original.
            if (reslopeRoof && slopeFaces != null)
            {
                doc.Regenerate(); // slab-shape vertices only exist after the roofs regenerate
                foreach (ElementId id in newIds)
                    ReslopeRoofToFaces(doc, id, slopeFaces);
            }

            doc.Delete(el.Id);
            return (newIds.Count, dropped, CellSplitStatus.Split);
        }

        // ── Sloped-roof re-slope support ──────────────────────────────────────

        // An upward-facing planar face of the original roof solid, with its outer outline
        // projected to XY so a cell vertex can be matched to the face it sits under.
        internal sealed class UpFace
        {
            public XYZ        Normal = XYZ.BasisZ;
            public XYZ        Origin = XYZ.Zero;
            public List<XYZ>  PolyXY = new List<XYZ>();
        }

        // Captures every upward-facing (FaceNormal.Z > 0) planar face of the roof solid, keeping
        // its plane and its outer loop in plan — used to sample the original roof height under
        // each cell vertex when re-sloping.
        private static List<UpFace> CaptureUpwardFaces(Solid solid)
        {
            var faces = new List<UpFace>();
            foreach (Face f in solid.Faces)
            {
                if (!(f is PlanarFace pf) || pf.FaceNormal.Z <= 1e-3) continue;

                var poly = new List<XYZ>();
                IList<CurveLoop> loops = pf.GetEdgesAsCurveLoops();
                if (loops.Count > 0)
                    foreach (Curve c in loops[0])
                        poly.Add(c.GetEndPoint(0));

                if (poly.Count < 3) continue;
                faces.Add(new UpFace { Normal = pf.FaceNormal.Normalize(), Origin = pf.Origin, PolyXY = poly });
            }
            return faces;
        }

        // Builds a perfectly-flat solid over the roof's footprint (same plan outline, negligible
        // thickness) so the standard cell extraction — which needs a horizontal top face — works.
        private static Solid? BuildFlatProxySolid(FootPrintRoof roof, Solid roofSolid)
        {
            // Footprint outline: prefer the largest upward face's plan outline (covers a
            // single-slope roof exactly). Flatten it to a single Z.
            List<XYZ>? outline = null;
            double     bestArea = 0;
            foreach (var up in CaptureUpwardFaces(roofSolid))
            {
                double area = PolygonAreaXY(up.PolyXY);
                if (area > bestArea) { bestArea = area; outline = up.PolyXY; }
            }
            if (outline == null || outline.Count < 3) return null;

            BoundingBoxXYZ? bb = roof.get_BoundingBox(null);
            double z = bb?.Min.Z ?? outline[0].Z;

            try
            {
                var loop = new CurveLoop();
                for (int i = 0; i < outline.Count; i++)
                {
                    XYZ a = new XYZ(outline[i].X,                       outline[i].Y,                       z);
                    XYZ b = new XYZ(outline[(i + 1) % outline.Count].X, outline[(i + 1) % outline.Count].Y, z);
                    if (a.DistanceTo(b) < 1e-7) continue;
                    loop.Append(Line.CreateBound(a, b));
                }

                Solid? s = TryExtrude2(loop, XYZ.BasisZ, 0.5);
                return s;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"SplitByCell: flat proxy build failed for roof {roof.Id.Value}", ex);
                return null;
            }
        }

        // CreateExtrusionGeometry rejects a loop wound against the extrusion direction; try the
        // loop as given and, on failure, reversed.
        private static Solid? TryExtrude2(CurveLoop loop, XYZ dir, double dist)
        {
            try { return GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, dir, dist); }
            catch { }
            try
            {
                var pts = new List<XYZ>();
                foreach (Curve c in loop) pts.Add(c.GetEndPoint(0));
                pts.Reverse();
                var rl = new CurveLoop();
                for (int i = 0; i < pts.Count; i++)
                {
                    if (pts[i].DistanceTo(pts[(i + 1) % pts.Count]) < 1e-7) continue;
                    rl.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
                }
                return GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { rl }, dir, dist);
            }
            catch { return null; }
        }

        // Re-slopes one flat roof onto the original roof surface by offsetting each of its
        // slab-shape vertices to the original height sampled under that vertex.
        private static void ReslopeRoofToFaces(Document doc, ElementId roofId, List<UpFace> faces)
        {
            try
            {
                if (!(doc.GetElement(roofId) is RoofBase roof)) return;
                SlabShapeEditor sse = roof.SlabShapeEditor;
                if (sse == null) return;

                // ResetSlabShape initialises the editor's vertices/creases from the (flat)
                // boundary — it's the reliable way to populate SlabShapeVertices before editing.
                try { sse.ResetSlabShape(); }
                catch (Exception ex) { DiagnosticsLog.Swallowed($"SplitByCell: reset slab shape on roof {roofId.Value}", ex); }

                foreach (SlabShapeVertex v in sse.SlabShapeVertices)
                {
                    XYZ p = v.Position;
                    double? targetZ = SampleFaceZ(faces, p.X, p.Y);
                    if (targetZ == null) continue;
                    double offset = targetZ.Value - p.Z;
                    if (Math.Abs(offset) < 1e-6) continue;
                    try { sse.ModifySubElement(v, offset); }
                    catch (Exception ex) { DiagnosticsLog.Swallowed($"SplitByCell: re-slope vertex on roof {roofId.Value}", ex); }
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"SplitByCell: re-slope roof {roofId.Value} failed", ex);
            }
        }

        // Height of the original roof surface under (x,y): the plane of whichever up-face contains
        // the point in plan; falls back to the single face when there is exactly one.
        private static double? SampleFaceZ(List<UpFace> faces, double x, double y)
        {
            foreach (var f in faces)
                if (PointInPolygonXY(f.PolyXY, x, y))
                    return PlaneZAt(f.Normal, f.Origin, x, y);

            if (faces.Count == 1)
                return PlaneZAt(faces[0].Normal, faces[0].Origin, x, y);

            return null;
        }

        private static double PlaneZAt(XYZ n, XYZ o, double x, double y)
        {
            if (Math.Abs(n.Z) < 1e-9) return o.Z;
            return o.Z - (n.X * (x - o.X) + n.Y * (y - o.Y)) / n.Z;
        }

        private static bool PointInPolygonXY(List<XYZ> poly, double x, double y)
        {
            bool inside = false;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = poly[i].X, yi = poly[i].Y;
                double xj = poly[j].X, yj = poly[j].Y;
                bool intersects = ((yi > y) != (yj > y)) &&
                                  (x < (xj - xi) * (y - yi) / (yj - yi + 1e-30) + xi);
                if (intersects) inside = !inside;
            }
            return inside;
        }

        private static double PolygonAreaXY(List<XYZ> poly)
        {
            double a = 0;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
                a += (poly[j].X + poly[i].X) * (poly[j].Y - poly[i].Y);
            return Math.Abs(a) * 0.5;
        }

        // True when the element's solid has at least one horizontal (up-facing) planar face —
        // the surface cell splitting reads to rebuild each cell. A sloped roof / ramp / sloped
        // floor has none, so it can never cell-split with the current top-face extraction.
        private static bool HasHorizontalTopFace(Solid solid)
        {
            foreach (Face face in solid.Faces)
            {
                if (face is PlanarFace pf && pf.FaceNormal.Z >= 1.0 - 1e-6)
                    return true;
            }
            return false;
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

        // Intersects a solid with the half-space on one side of a plane. `side` is +1 for the
        // plane-normal side, -1 for the opposite side. Used by the area-element plane split to
        // slice a slab region in two. Returns null on failure (caller treats null as empty).
        // Only meaningful for VERTICAL planes (horizontal normal), which is all the area split
        // ever passes — the extruded half-space box below assumes a horizontal normal.
        internal static Solid? HalfSpaceIntersect(Solid region, XYZ normal, XYZ origin, int side)
        {
            try
            {
                XYZ n   = normal.Normalize();
                XYZ dir = side >= 0 ? n : n.Negate();

                // In-plane horizontal axis (the plane is vertical, so this spans its width).
                XYZ u = dir.CrossProduct(XYZ.BasisZ);
                if (u.GetLength() < 1e-9) return null; // not a vertical plane — unsupported here
                u = u.Normalize();

                const double L = 1.0e4; // 10 000 ft: far larger than any real slab
                const double H = 1.0e4;

                // Rectangle lying IN the cutting plane (spans u and Z), wound so its normal points
                // along +dir, then extruded along dir to sweep out the half-space box.
                XYZ o  = origin;
                XYZ p0 = o - u * L - XYZ.BasisZ * H;
                XYZ p1 = o - u * L + XYZ.BasisZ * H;
                XYZ p2 = o + u * L + XYZ.BasisZ * H;
                XYZ p3 = o + u * L - XYZ.BasisZ * H;

                Solid? box = TryExtrude(new[] { p0, p1, p2, p3 }, dir, L);
                if (box == null) return null;

                return BooleanOperationsUtils.ExecuteBooleanOperation(
                    region, box, BooleanOperationsType.Intersect);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("SplitByCell: half-space intersect failed", ex);
                return null;
            }
        }

        // Extrudes a 4-point planar rectangle along `dir`. CreateExtrusionGeometry rejects a loop
        // wound against the extrusion direction, so try the given order and, on failure, the
        // reverse before giving up.
        private static Solid? TryExtrude(XYZ[] pts, XYZ dir, double dist)
        {
            foreach (var order in new[] { pts, new[] { pts[0], pts[3], pts[2], pts[1] } })
            {
                try
                {
                    var loop = new CurveLoop();
                    for (int i = 0; i < order.Length; i++)
                        loop.Append(Line.CreateBound(order[i], order[(i + 1) % order.Length]));
                    return GeometryCreationUtilities.CreateExtrusionGeometry(
                        new List<CurveLoop> { loop }, dir, dist);
                }
                catch { /* try the reversed winding */ }
            }
            return null;
        }

        // Use the top face (normal = +Z) so GetEdgesAsCurveLoops returns CCW loops when
        // viewed from above — the orientation Floor.Create and Ceiling.Create require.
        // The bottom face (normal = -Z) returns CW loops, which those APIs reject.
        internal static IList<CurveLoop>? ExtractTopFaceLoops(Solid solid)
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
        private static (int CellCount, int DroppedCells, CellSplitStatus Status) SplitFilledRegion(
            Document      doc,
            FilledRegion  fr,
            double        cellX,
            double        cellY,
            XYZ?          gridOrigin)
        {
            IList<CurveLoop> boundaries = fr.GetBoundaries();
            if (boundaries == null || boundaries.Count == 0)
                return (0, 0, CellSplitStatus.NoGeometry);

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

            // Integer cell counts (see the solid path) so repeated += drift can't drop the trailing
            // column/row; the epsilon stops an exact span edge from spawning a zero-width sliver.
            int nx = Math.Max(1, (int)Math.Ceiling((maxX - startX) / cellX - 1e-9));
            int ny = Math.Max(1, (int)Math.Ceiling((maxY - startY) / cellY - 1e-9));

            var cellsX = new List<(double Min, double Max)>(nx);
            for (int i = 0; i < nx; i++) { double x = startX + i * cellX; cellsX.Add((x, x + cellX)); }

            var cellsY = new List<(double Min, double Max)>(ny);
            for (int j = 0; j < ny; j++) { double y = startY + j * cellY; cellsY.Add((y, y + cellY)); }

            if (cellsX.Count == 1 && cellsY.Count == 1)
                return (0, 0, CellSplitStatus.FitsInOneCell);

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

            if (pending.Count == 0) return (0, 0, CellSplitStatus.NoCellsIntersected);

            // Phase 2: create replacement FilledRegions. A single degenerate cell (e.g. a sliver from
            // a grid line coincident with the region edge) must not abort the whole split — log it and
            // skip, keeping the good cells.
            var newIds = new List<ElementId>();
            int dropped = 0;
            foreach (var loops in pending)
            {
                try
                {
                    FilledRegion newFr = FilledRegion.Create(doc, fr.GetTypeId(), fr.OwnerViewId, loops);
                    newIds.Add(newFr.Id);
                }
                catch (Exception ex)
                {
                    dropped++;
                    DiagnosticsLog.Swallowed($"SplitByCell: skipped a degenerate cell of filled region {fr.Id.Value}", ex);
                }
            }

            // If nothing valid was produced, leave the original intact rather than deleting it.
            if (newIds.Count == 0) return (0, dropped, CellSplitStatus.NoCellsIntersected);

            doc.Delete(fr.Id);
            return (newIds.Count, dropped, CellSplitStatus.Split);
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
            double dx = b.X - a.X;
            // Edge parallel to the clip line (coincident with a grid line): no real crossing, so
            // return the boundary point without dividing by ~0 (which produced NaN → garbage cells).
            if (Math.Abs(dx) < 1e-9) return new XYZ(x, a.Y, z);
            double t = (x - a.X) / dx;
            return new XYZ(x, a.Y + t * (b.Y - a.Y), z);
        }

        private static XYZ IntersectAtY(XYZ a, XYZ b, double y, double z)
        {
            double dy = b.Y - a.Y;
            if (Math.Abs(dy) < 1e-9) return new XYZ(a.X, y, z);
            double t = (y - a.Y) / dy;
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
        internal static bool IsSupportedForRecreation(Element el) =>
            el is Floor || el is Ceiling || el is FilledRegion || el is RoofBase;

        // No outer try/catch: Revit API exceptions propagate to SplitElement's caller so
        // the per-element transaction can roll back atomically.
        internal static ElementId RecreateElement(
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
                // BasePoint.Position is the point's INTERNAL-coordinate location — the frame the
                // cell grid math runs in. The BASEPOINT_EASTWEST/NORTHSOUTH parameters are the
                // point's SHARED-coordinate readout, which diverges from internal on any project
                // with shared coordinates set (rotated/offset site), so using them anchored the
                // grid to a meaningless point. Snap only X/Y; the grid is 2D in plan.
                BasePoint? bp = BasePoint.GetProjectBasePoint(doc);
                if (bp != null)
                {
                    XYZ pos = bp.Position;
                    return new XYZ(pos.X, pos.Y, 0);
                }
            }
            catch (Exception __lex) { DiagnosticsLog.Swallowed("SplitByCell: read project base point", __lex); }

            return null;
        }

        // ── Category map builder ──────────────────────────────────────────────

        /// <summary>
        /// Builds a map from BuiltInCategory to element IDs visible in the given view.
        /// Keyed by BIC enum (not Category.Name) so matching works in non-English Revit.
        /// FilledRegion uses a class filter, not a category filter, because category filters
        /// miss FilledRegion elements — so this is the single source of truth for both the
        /// pre-open count strip and the run, keeping the two in agreement.
        /// </summary>
        /// <param name="only">If supplied, only these categories are collected (the rest are
        /// skipped) — lets the run collect just the selected categories instead of all five.</param>
        internal static Dictionary<BuiltInCategory, List<ElementId>> BuildCategoryMap(
            Document doc, View view, ICollection<BuiltInCategory>? only = null)
        {
            var map = new Dictionary<BuiltInCategory, List<ElementId>>();

            IEnumerable<BuiltInCategory> cats = only != null
                ? SupportedCategories.Where(only.Contains)
                : SupportedCategories;

            foreach (BuiltInCategory bic in cats)
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
