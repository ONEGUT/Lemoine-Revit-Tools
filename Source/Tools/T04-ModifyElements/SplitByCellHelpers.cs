using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;

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

            // A pinned element would fail at the eventual doc.Delete of the original; a grouped
            // element cannot be copied/deleted independently of its group. Report both clearly
            // instead of letting a raw Revit exception surface from deep inside the split.
            if (el.Pinned)
                throw new InvalidOperationException("Element is pinned — unpin to split.");
            if (el.GroupId != null && el.GroupId != ElementId.InvalidElementId)
                throw new InvalidOperationException("Element is a member of a group — ungroup to split.");

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
                    catch (Exception ex)
                    {
                        LemoineLog.Swallowed($"SplitByCell: cell boolean intersect failed on element {el.Id.Value}", ex);
                        continue;
                    }

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

            // Integer cell counts (see the solid path) so repeated += drift can't drop the trailing
            // column/row; the epsilon stops an exact span edge from spawning a zero-width sliver.
            int nx = Math.Max(1, (int)Math.Ceiling((maxX - startX) / cellX - 1e-9));
            int ny = Math.Max(1, (int)Math.Ceiling((maxY - startY) / cellY - 1e-9));

            var cellsX = new List<(double Min, double Max)>(nx);
            for (int i = 0; i < nx; i++) { double x = startX + i * cellX; cellsX.Add((x, x + cellX)); }

            var cellsY = new List<(double Min, double Max)>(ny);
            for (int j = 0; j < ny; j++) { double y = startY + j * cellY; cellsY.Add((y, y + cellY)); }

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

            // Phase 2: create replacement FilledRegions. A single degenerate cell (e.g. a sliver from
            // a grid line coincident with the region edge) must not abort the whole split — log it and
            // skip, keeping the good cells.
            var newIds = new List<ElementId>();
            foreach (var loops in pending)
            {
                try
                {
                    FilledRegion newFr = FilledRegion.Create(doc, fr.GetTypeId(), fr.OwnerViewId, loops);
                    newIds.Add(newFr.Id);
                }
                catch (Exception ex)
                {
                    LemoineLog.Swallowed($"SplitByCell: skipped a degenerate cell of filled region {fr.Id.Value}", ex);
                }
            }

            // If nothing valid was produced, leave the original intact rather than deleting it.
            if (newIds.Count == 0) return (0, CellSplitStatus.NoCellsIntersected);

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

        // ── Generalized plane-based split (Level / Grid / Reference Plane cutters) ─────────
        //
        // Split by Cell partitions a sheet element with an axis-aligned box grid. Level/Grid/
        // Reference-Plane cutters instead supply an arbitrary list of cutting planes (a
        // horizontal plane per level; a vertical plane per grid line or reference plane — see
        // SplitElementsShared.TryBuildGridPlane / TryBuildReferencePlanePlane / the level-plane
        // builder). SplitSolidByPlanes / SplitPolygonByPlanes generalize the same
        // boolean-intersect (solid) / Sutherland-Hodgman-clip (FilledRegion sketch) machinery
        // to any such plane set via an iterative half-space (BSP) partition: each cutting plane
        // bisects every current piece into a "positive side" and "negative side" fragment, so
        // planes of any orientation — not just axis-aligned — are supported, and passing the
        // same plane list twice (once per axis) reproduces an ordinary rectangular grid too.

        /// <summary>
        /// The shared entry point for the Level / Grid / Reference-Plane cutters against a
        /// footprint ("sheet") target — Floor, Ceiling, FootPrintRoof, a Floor-instance
        /// Structural Foundation slab, or FilledRegion. Mirrors <see cref="SplitElement"/>'s
        /// solid-intersect-then-recreate shape, but for an arbitrary plane set instead of a
        /// cell grid.
        /// </summary>
        internal static void SplitSheetElementByPlanes(
            Document doc, Element el, List<(XYZ Normal, XYZ Origin)> planes,
            string cutterLabel, SplitStats stats)
        {
            if (el is FilledRegion fr)
            {
                SplitFilledRegionByPlanes(doc, fr, planes, cutterLabel, stats);
                return;
            }

            if (!IsSupportedForRecreation(el))
            {
                stats.Skip($"{el.Category?.Name} {el.Id}: no applicable {cutterLabel} split strategy for this element type");
                return;
            }

            Solid? solid = GetPrimarySolid(el);
            if (solid == null || solid.Volume < 1e-9)
            { stats.Skip($"{el.Category?.Name} {el.Id}: no solid geometry"); return; }

            BoundingBoxXYZ bb = el.get_BoundingBox(null);
            if (bb == null)
            { stats.Skip($"{el.Category?.Name} {el.Id}: no bounding box"); return; }

            List<Solid> pieces;
            try { pieces = SplitSolidByPlanes(solid, planes, bb); }
            catch (Exception ex)
            { stats.Fail(el.Id.ToString(), $"{cutterLabel} split failed: {ex.Message}"); return; }

            if (pieces.Count < 2)
            {
                // No supplied plane actually crossed this element's solid — the common case for
                // a flat floor/ceiling/roof against a Level cutter (§1.8): the element sits AT
                // an elevation rather than spanning across one. Not an error; report and move on.
                stats.Skip($"{el.Category?.Name} {el.Id}: does not cross any selected {cutterLabel}");
                return;
            }

            var pendingLoops = new List<IList<CurveLoop>>();
            foreach (var piece in pieces)
            {
                IList<CurveLoop>? loops = ExtractTopFaceLoops(piece);
                if (loops == null || loops.Count == 0) continue;
                pendingLoops.Add(loops);
            }

            if (pendingLoops.Count == 0)
            {
                stats.Skip($"{el.Category?.Name} {el.Id}: split pieces have no horizontal top face " +
                           "(sloped/non-horizontal geometry is not yet supported)");
                return;
            }

            var newIds = new List<ElementId>();
            try
            {
                foreach (var loops in pendingLoops)
                {
                    ElementId newId = RecreateElement(doc, el, loops);
                    if (newId == null || newId == ElementId.InvalidElementId)
                        throw new InvalidOperationException("Element recreation failed.");
                    newIds.Add(newId);
                }
            }
            catch (Exception ex)
            {
                foreach (var nid in newIds)
                    try { doc.Delete(nid); } catch (Exception __lex) { LemoineLog.Swallowed("SplitElements: clean up partial sheet split", __lex); }
                stats.Fail(el.Id.ToString(), $"{cutterLabel} split recreation failed: {ex.Message}");
                return;
            }

            doc.Delete(el.Id);
            stats.Split(newIds.Count, $"{el.Category?.Name} {el.Id} → {newIds.Count} {cutterLabel} segment(s)");
        }

        /// <summary>
        /// Splits a solid into disjoint pieces via an iterative half-space (BSP) partition:
        /// each cutting plane bisects every current piece into a boolean-intersect ("positive
        /// side") and boolean-difference ("negative side") fragment. <paramref name="bb"/> is
        /// the ORIGINAL element's bounding box (not recomputed per piece) — since every piece
        /// is a subset of the original solid, one generously-margined box safely bounds the
        /// half-space solid built for every cut.
        /// </summary>
        internal static List<Solid> SplitSolidByPlanes(
            Solid solid, List<(XYZ Normal, XYZ Origin)> planes, BoundingBoxXYZ bb)
        {
            var pieces = new List<Solid> { solid };
            foreach (var (normal, origin) in planes)
            {
                var next = new List<Solid>();
                foreach (var piece in pieces)
                {
                    Solid? halfSpace = BuildHalfSpaceSolid(normal, origin, bb);
                    if (halfSpace == null) { next.Add(piece); continue; }

                    Solid? posSide = null, negSide = null;
                    try { posSide = BooleanOperationsUtils.ExecuteBooleanOperation(piece, halfSpace, BooleanOperationsType.Intersect); }
                    catch (Exception ex) { LemoineLog.Swallowed("SplitElements: half-space intersect failed", ex); }
                    try { negSide = BooleanOperationsUtils.ExecuteBooleanOperation(piece, halfSpace, BooleanOperationsType.Difference); }
                    catch (Exception ex) { LemoineLog.Swallowed("SplitElements: half-space difference failed", ex); }

                    bool posOk = posSide != null && posSide.Volume > 1e-9;
                    bool negOk = negSide != null && negSide.Volume > 1e-9;

                    if (posOk) next.Add(posSide!);
                    if (negOk) next.Add(negSide!);
                    if (!posOk && !negOk)
                        next.Add(piece); // plane didn't actually cross this piece — keep it whole
                }
                pieces = next;
            }
            return pieces;
        }

        // Builds a large box solid occupying the positive-normal half-space of the plane
        // through `origin`, sized from `bb` (see SplitSolidByPlanes) rather than the piece
        // being cut, so the same box safely covers every fragment regardless of orientation.
        // The loop winds CCW as viewed from +normal (u × v = normal by construction), matching
        // the extrusion-direction convention MakeCellSolid uses for the Z axis.
        private static Solid? BuildHalfSpaceSolid(XYZ normal, XYZ origin, BoundingBoxXYZ bb)
        {
            try
            {
                XYZ n = normal.Normalize();
                XYZ refAxis = Math.Abs(n.DotProduct(XYZ.BasisZ)) < 0.99 ? XYZ.BasisZ : XYZ.BasisX;
                XYZ u = n.CrossProduct(refAxis).Normalize();
                XYZ v = n.CrossProduct(u).Normalize();

                XYZ center = (bb.Min + bb.Max) * 0.5;
                double diag = bb.Max.DistanceTo(bb.Min) + 10.0;
                // Project the element's bbox center onto the cutting plane so the half-space
                // box is centered near the geometry regardless of where `origin` itself sits.
                XYZ planeCenter = center - n.DotProduct(center - origin) * n;

                double half = diag;
                XYZ p0 = planeCenter - u * half - v * half;
                XYZ p1 = planeCenter + u * half - v * half;
                XYZ p2 = planeCenter + u * half + v * half;
                XYZ p3 = planeCenter - u * half + v * half;

                var loop = new CurveLoop();
                loop.Append(Line.CreateBound(p0, p1));
                loop.Append(Line.CreateBound(p1, p2));
                loop.Append(Line.CreateBound(p2, p3));
                loop.Append(Line.CreateBound(p3, p0));

                return GeometryCreationUtilities.CreateExtrusionGeometry(
                    new List<CurveLoop> { loop }, n, diag * 2);
            }
            catch (Exception ex) { LemoineLog.Swallowed("SplitElements: build half-space cutting solid", ex); return null; }
        }

        // ── FilledRegion plane-based split (2D BSP, mirrors SplitSolidByPlanes) ─────────────

        private static void SplitFilledRegionByPlanes(
            Document doc, FilledRegion fr, List<(XYZ Normal, XYZ Origin)> planes,
            string cutterLabel, SplitStats stats)
        {
            IList<CurveLoop> boundaries = fr.GetBoundaries();
            if (boundaries == null || boundaries.Count == 0)
            { stats.Skip($"Filled Region {fr.Id}: no boundary geometry"); return; }

            List<XYZ>? outer = LoopToPoints(boundaries[0]);
            if (outer == null)
            { stats.Skip($"Filled Region {fr.Id}: boundary has non-linear curves — not supported"); return; }

            var holes = new List<List<XYZ>>();
            for (int hi = 1; hi < boundaries.Count; hi++)
            {
                var hole = LoopToPoints(boundaries[hi]);
                if (hole != null) holes.Add(hole);
            }

            double z = outer[0].Z;

            List<(List<XYZ> Outer, List<List<XYZ>> Holes)> pieces;
            try { pieces = SplitPolygonByPlanes(outer, holes, planes, z); }
            catch (Exception ex)
            { stats.Fail(fr.Id.ToString(), $"{cutterLabel} split failed: {ex.Message}"); return; }

            if (pieces.Count < 2)
            { stats.Skip($"Filled Region {fr.Id}: does not cross any selected {cutterLabel}"); return; }

            var newIds = new List<ElementId>();
            foreach (var (pOuter, pHoles) in pieces)
            {
                CurveLoop? outerLoop = PointsToLoop(pOuter);
                if (outerLoop == null) continue;

                var cellLoops = new List<CurveLoop> { outerLoop };
                foreach (var h in pHoles)
                {
                    CurveLoop? holeLoop = PointsToLoop(h);
                    if (holeLoop != null) cellLoops.Add(holeLoop);
                }

                try
                {
                    FilledRegion newFr = FilledRegion.Create(doc, fr.GetTypeId(), fr.OwnerViewId, cellLoops);
                    newIds.Add(newFr.Id);
                }
                catch (Exception ex)
                {
                    LemoineLog.Swallowed($"SplitElements: skipped a degenerate {cutterLabel} piece of filled region {fr.Id.Value}", ex);
                }
            }

            if (newIds.Count == 0)
            { stats.Fail(fr.Id.ToString(), "All split pieces failed to recreate."); return; }

            doc.Delete(fr.Id);
            stats.Split(newIds.Count, $"Filled Region {fr.Id} → {newIds.Count} {cutterLabel} segment(s)");
        }

        private static List<XYZ>? LoopToPoints(CurveLoop loop)
        {
            var pts = new List<XYZ>();
            foreach (Curve c in loop)
            {
                if (!(c is Line)) return null;
                pts.Add(c.GetEndPoint(0));
            }
            return pts.Count >= 3 ? pts : null;
        }

        /// <summary>
        /// Splits a 2D polygon (with holes) into pieces via the same BSP idea as
        /// <see cref="SplitSolidByPlanes"/>, projected into the FilledRegion's own sketch
        /// plane at elevation <paramref name="z"/>. A cutting plane with no XY component
        /// (a Level's horizontal plane) cannot cross a flat sketch and is skipped per-piece
        /// rather than treated as an error — SplitTargets already hides the Level cutter from
        /// FilledRegion for this reason, so this is a defensive no-op, not the primary guard.
        /// </summary>
        internal static List<(List<XYZ> Outer, List<List<XYZ>> Holes)> SplitPolygonByPlanes(
            List<XYZ> outer, List<List<XYZ>> holes, List<(XYZ Normal, XYZ Origin)> planes, double z)
        {
            var pieces = new List<(List<XYZ> Outer, List<List<XYZ>> Holes)> { (outer, holes) };

            foreach (var (normal3, origin3) in planes)
            {
                XYZ n2 = new XYZ(normal3.X, normal3.Y, 0);
                if (n2.GetLength() < 1e-9) continue;
                n2 = n2.Normalize();
                XYZ p2 = new XYZ(origin3.X, origin3.Y, z);

                var next = new List<(List<XYZ> Outer, List<List<XYZ>> Holes)>();
                foreach (var (pOuter, pHoles) in pieces)
                {
                    var posOuter = SHClip(pOuter, pt => n2.DotProduct(pt - p2) >= 0, (a, b) => IntersectAtPlane2D(a, b, n2, p2));
                    var negOuter = SHClip(pOuter, pt => n2.DotProduct(pt - p2) <= 0, (a, b) => IntersectAtPlane2D(a, b, n2, p2));

                    bool posOk = posOuter.Count >= 3;
                    bool negOk = negOuter.Count >= 3;

                    if (posOk) next.Add((posOuter, ClipHolesToHalfPlane(pHoles, n2, p2, keepPositive: true)));
                    if (negOk) next.Add((negOuter, ClipHolesToHalfPlane(pHoles, n2, p2, keepPositive: false)));
                    if (!posOk && !negOk)
                        next.Add((pOuter, pHoles)); // plane missed this piece entirely — keep it whole
                }
                pieces = next;
            }
            return pieces;
        }

        private static List<List<XYZ>> ClipHolesToHalfPlane(
            List<List<XYZ>> holes, XYZ n2, XYZ p2, bool keepPositive)
        {
            var result = new List<List<XYZ>>();
            foreach (var hole in holes)
            {
                var clipped = keepPositive
                    ? SHClip(hole, pt => n2.DotProduct(pt - p2) >= 0, (a, b) => IntersectAtPlane2D(a, b, n2, p2))
                    : SHClip(hole, pt => n2.DotProduct(pt - p2) <= 0, (a, b) => IntersectAtPlane2D(a, b, n2, p2));
                if (clipped.Count >= 3) result.Add(clipped);
            }
            return result;
        }

        private static XYZ IntersectAtPlane2D(XYZ a, XYZ b, XYZ n2, XYZ p2)
        {
            double da = n2.DotProduct(a - p2);
            double db = n2.DotProduct(b - p2);
            double denom = da - db;
            if (Math.Abs(denom) < 1e-9) return a;
            double t = da / denom;
            return a + t * (b - a);
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
                    $"Only footprint roofs can be split this way; extrusion roofs are not supported.");

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
            catch (Exception __lex) { LemoineLog.Swallowed("SplitByCell: read project base point", __lex); }

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
                else if (bic == BuiltInCategory.OST_StructuralFoundation)
                {
                    // Only Floor-instance slabs are supported for recreation-based splitting —
                    // isolated footings and wall foundations are FamilyInstances and would throw
                    // NotSupportedException in SplitElement. Filter them out here so the picker
                    // never offers an element the engine can't act on.
                    ids = new FilteredElementCollector(doc, view.Id)
                        .OfCategoryId(new ElementId(bic))
                        .WhereElementIsNotElementType()
                        .Where(e => e is Floor)
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
