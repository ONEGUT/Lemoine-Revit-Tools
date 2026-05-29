using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.ModifyElements
{
    /// <summary>
    /// Shared split engine used by SplitByLevelEventHandler and SplitByGridEventHandler.
    ///
    /// Two dispatch strategies:
    ///
    ///   PARAMETER — copies the element N times, sets Base/Top level constraint
    ///   parameters on each copy, then deletes the original.
    ///   Works for: Walls, Structural Columns, Architectural Columns.
    ///
    ///   CURVE — finds intersection points on the element's LocationCurve,
    ///   copies it once per extra segment, then assigns each copy a sub-segment
    ///   of the original curve. Connectors are disconnected first so Revit
    ///   accepts the curve change.
    ///   Works for: MEP curves (ducts, pipes, conduit, cable tray), Structural Framing.
    ///
    /// Every element-level failure is caught, logged, and skipped.
    /// A failed element never aborts the enclosing transaction.
    /// </summary>
    public static class SplitElementsShared
    {
        // ── Supported category lists ──────────────────────────────────────────

        /// <summary>
        /// Defines the Revit element categories that the level-split operation supports,
        /// together with a human-readable label and a short note describing the split strategy.
        /// Used to populate the UI category selector and to validate incoming elements at runtime.
        /// </summary>
        public static readonly IReadOnlyList<(BuiltInCategory Cat, string Label, string Note)>
            LevelSplitCategories = new[]
        {
            (BuiltInCategory.OST_Walls,
                "Walls",
                "One segment per level span — base/top constraint"),
            (BuiltInCategory.OST_StructuralColumns,
                "Structural Columns",
                "One segment per level span — base/top constraint"),
            (BuiltInCategory.OST_Columns,
                "Architectural Columns",
                "One segment per level span — base/top constraint"),
            (BuiltInCategory.OST_StructuralFraming,
                "Structural Framing",
                "Splits vertical/diagonal members at level — curve-based"),
            (BuiltInCategory.OST_DuctCurves,
                "Ducts",
                "Splits riser ducts at level elevation — curve-based"),
            (BuiltInCategory.OST_PipeCurves,
                "Pipes",
                "Splits riser pipes at level elevation — curve-based"),
            (BuiltInCategory.OST_Conduit,
                "Conduit",
                "Splits vertical conduit at level — curve-based"),
            (BuiltInCategory.OST_CableTray,
                "Cable Trays",
                "Splits vertical cable tray at level — curve-based"),
        };

        /// <summary>
        /// Defines the Revit element categories that the grid-split operation supports,
        /// together with a human-readable label and a short note describing the split strategy.
        /// Used to populate the UI category selector and to validate incoming elements at runtime.
        /// </summary>
        public static readonly IReadOnlyList<(BuiltInCategory Cat, string Label, string Note)>
            GridSplitCategories = new[]
        {
            (BuiltInCategory.OST_Walls,
                "Walls",
                "Split at grid plane — curve-based"),
            (BuiltInCategory.OST_StructuralFraming,
                "Structural Framing",
                "Split at grid plane intersection — curve-based"),
            (BuiltInCategory.OST_DuctCurves,
                "Ducts",
                "Split at grid plane intersection — curve-based"),
            (BuiltInCategory.OST_PipeCurves,
                "Pipes",
                "Split at grid plane intersection — curve-based"),
            (BuiltInCategory.OST_Conduit,
                "Conduit",
                "Split at grid plane intersection — curve-based"),
            (BuiltInCategory.OST_CableTray,
                "Cable Trays",
                "Split at grid plane intersection — curve-based"),
        };

        // ── Level constraint parameters ───────────────────────────────────────

        private static readonly BuiltInParameter WallBase    = BuiltInParameter.WALL_BASE_CONSTRAINT;
        private static readonly BuiltInParameter WallTop     = BuiltInParameter.WALL_HEIGHT_TYPE;
        private static readonly BuiltInParameter WallBaseOff = BuiltInParameter.WALL_BASE_OFFSET;
        private static readonly BuiltInParameter WallTopOff  = BuiltInParameter.WALL_TOP_OFFSET;

        private static readonly BuiltInParameter ColBase    = BuiltInParameter.FAMILY_BASE_LEVEL_PARAM;
        private static readonly BuiltInParameter ColTop     = BuiltInParameter.FAMILY_TOP_LEVEL_PARAM;
        private static readonly BuiltInParameter ColBaseOff = BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM;
        private static readonly BuiltInParameter ColTopOff  = BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM;

        // ═════════════════════════════════════════════════════════════════════
        //  Public entry points
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Splits each element in <paramref name="elements"/> at every boundary defined by
        /// <paramref name="levels"/>, using the appropriate strategy for the element's category.
        /// Walls and columns use the PARAMETER strategy (copied segments with adjusted level
        /// constraints); MEP curves and structural framing use the CURVE strategy (copied
        /// segments with reassigned <see cref="LocationCurve"/>).
        /// </summary>
        /// <param name="doc">The active Revit document. Must be inside an open transaction.</param>
        /// <param name="elements">The elements to split.</param>
        /// <param name="levels">
        /// The set of levels whose elevations act as split planes. Elements whose extents
        /// do not span any of these levels are skipped without error.
        /// </param>
        /// <returns>
        /// A <see cref="SplitStats"/> object summarising how many elements were split,
        /// skipped, or failed.
        /// </returns>
        public static SplitStats SplitByLevel(
            Document             doc,
            IEnumerable<Element> elements,
            List<Level>          levels)
        {
            var stats = new SplitStats();

            foreach (Element el in elements)
            {
                try
                {
                    if (el?.Category?.Id == null) { stats.Skip("No category"); continue; }
                    long bic = el.Category.Id.Value;

                    if (bic == (long)BuiltInCategory.OST_Walls)
                        SplitWallByLevel(doc, (Wall)el, levels, stats);

                    else if (bic == (long)BuiltInCategory.OST_StructuralColumns ||
                             bic == (long)BuiltInCategory.OST_Columns)
                        SplitColumnByLevel(doc, el, levels, stats);

                    else if (bic == (long)BuiltInCategory.OST_StructuralFraming ||
                             bic == (long)BuiltInCategory.OST_DuctCurves        ||
                             bic == (long)BuiltInCategory.OST_PipeCurves        ||
                             bic == (long)BuiltInCategory.OST_Conduit           ||
                             bic == (long)BuiltInCategory.OST_CableTray)
                        SplitCurveByLevel(doc, el, levels, stats);

                    else
                        stats.Skip($"Unsupported category: {el.Category.Name}");
                }
                catch (Exception ex)
                {
                    stats.Fail(el?.Id?.ToString() ?? "?", ex.Message);
                }
            }

            return stats;
        }

        /// <summary>
        /// Splits each element in <paramref name="elements"/> at the vertical plane of each
        /// grid line in <paramref name="grids"/>, using the CURVE strategy (copies the element
        /// and reassigns each copy's <see cref="LocationCurve"/> to its sub-segment).
        /// </summary>
        /// <param name="doc">The active Revit document. Must be inside an open transaction.</param>
        /// <param name="elements">The elements to split.</param>
        /// <param name="grids">
        /// The grid lines whose perpendicular vertical planes act as cutting surfaces.
        /// Null entries are silently ignored. If no valid grid planes can be derived,
        /// all elements are recorded as failed and the method returns immediately.
        /// </param>
        /// <returns>
        /// A <see cref="SplitStats"/> object summarising how many elements were split,
        /// skipped, or failed.
        /// </returns>
        public static SplitStats SplitByGrid(
            Document             doc,
            IEnumerable<Element> elements,
            List<Grid?>          grids)
        {
            var stats = new SplitStats();

            var planes = grids
                .Where(g => g != null)
                .Select(g => TryBuildGridPlane(g!))
                .Where(p => p.HasValue)
                .Select(p => p!.Value)
                .ToList();

            if (!planes.Any())
            {
                stats.Fail("grids", "No valid grid planes could be computed.");
                return stats;
            }

            foreach (Element el in elements)
            {
                try
                {
                    if (el?.Category?.Id == null) { stats.Skip("No category"); continue; }
                    long bic = el.Category.Id.Value;

                    if (bic == (long)BuiltInCategory.OST_Walls)
                        SplitWallByGrid(doc, (Wall)el, planes, stats);

                    else if (bic == (long)BuiltInCategory.OST_StructuralFraming ||
                             bic == (long)BuiltInCategory.OST_DuctCurves        ||
                             bic == (long)BuiltInCategory.OST_PipeCurves        ||
                             bic == (long)BuiltInCategory.OST_Conduit           ||
                             bic == (long)BuiltInCategory.OST_CableTray)
                        SplitCurveByGrid(doc, el, planes, stats);

                    else
                        stats.Skip($"Unsupported category: {el.Category.Name}");
                }
                catch (Exception ex)
                {
                    stats.Fail(el?.Id?.ToString() ?? "?", ex.Message);
                }
            }

            return stats;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Level split implementations
        // ═════════════════════════════════════════════════════════════════════

        private static void SplitWallByLevel(
            Document doc, Wall wall, List<Level> levels, SplitStats stats)
        {
            Level? baseLevel = doc.GetElement(
                wall.get_Parameter(WallBase)?.AsElementId()) as Level;
            Level? topLevel = doc.GetElement(
                wall.get_Parameter(WallTop)?.AsElementId()) as Level;

            if (baseLevel == null || topLevel == null)
            { stats.Skip($"Wall {wall.Id}: not level-constrained"); return; }

            var spans = BuildLevelSpans(levels, baseLevel, topLevel);
            if (spans.Count < 2)
            { stats.Skip($"Wall {wall.Id}: no intermediate levels in range"); return; }

            double baseOff = wall.get_Parameter(WallBaseOff)?.AsDouble() ?? 0.0;
            double topOff  = wall.get_Parameter(WallTopOff)?.AsDouble()  ?? 0.0;

            var copies = CopyTimes(doc, wall.Id, spans.Count - 1);
            if (copies == null) { stats.Fail(wall.Id.ToString(), "Copy failed"); return; }

            DisallowWallJoins(doc, wall);
            foreach (var cid in copies)
            {
                var cwall = doc.GetElement(cid) as Wall;
                if (cwall != null) DisallowWallJoins(doc, cwall);
            }

            for (int k = 0; k < copies.Count; k++)
            {
                var seg = doc.GetElement(copies[k]) as Wall;
                if (seg == null) continue;
                try
                {
                    seg.get_Parameter(WallBase)   ?.Set(spans[k].Id);
                    seg.get_Parameter(WallTop)    ?.Set(spans[k + 1].Id);
                    seg.get_Parameter(WallBaseOff)?.Set(k == 0                    ? baseOff : 0.0);
                    seg.get_Parameter(WallTopOff) ?.Set(k == copies.Count - 1    ? topOff  : 0.0);
                }
                catch (Exception ex) { stats.Fail($"Wall {wall.Id} seg {k}", ex.Message); }
            }

            doc.Delete(wall.Id);
            stats.Split($"Wall {wall.Id} → {copies.Count} segments");
        }

        private static void SplitColumnByLevel(
            Document doc, Element col, List<Level> levels, SplitStats stats)
        {
            Level? baseLevel = doc.GetElement(
                col.get_Parameter(ColBase)?.AsElementId()) as Level;
            Level? topLevel = doc.GetElement(
                col.get_Parameter(ColTop)?.AsElementId()) as Level;

            if (baseLevel == null || topLevel == null)
            { stats.Skip($"Column {col.Id}: not level-constrained"); return; }

            var spans = BuildLevelSpans(levels, baseLevel, topLevel);
            if (spans.Count < 2)
            { stats.Skip($"Column {col.Id}: no intermediate levels in range"); return; }

            double baseOff = col.get_Parameter(ColBaseOff)?.AsDouble() ?? 0.0;
            double topOff  = col.get_Parameter(ColTopOff)?.AsDouble()  ?? 0.0;

            var copies = CopyTimes(doc, col.Id, spans.Count - 1);
            if (copies == null) { stats.Fail(col.Id.ToString(), "Copy failed"); return; }

            for (int k = 0; k < copies.Count; k++)
            {
                var seg = doc.GetElement(copies[k]);
                if (seg == null) continue;
                try
                {
                    seg.get_Parameter(ColBase)   ?.Set(spans[k].Id);
                    seg.get_Parameter(ColTop)    ?.Set(spans[k + 1].Id);
                    seg.get_Parameter(ColBaseOff)?.Set(k == 0                 ? baseOff : 0.0);
                    seg.get_Parameter(ColTopOff) ?.Set(k == copies.Count - 1 ? topOff  : 0.0);
                }
                catch (Exception ex) { stats.Fail($"Column {col.Id} seg {k}", ex.Message); }
            }

            doc.Delete(col.Id);
            stats.Split($"Column {col.Id} → {copies.Count} segments");
        }

        private static void SplitCurveByLevel(
            Document doc, Element el, List<Level> levels, SplitStats stats)
        {
            if (!(el.Location is LocationCurve lc) || !(lc.Curve is Line line))
            { stats.Skip($"{el.Id}: no linear LocationCurve"); return; }

            XYZ A = line.GetEndPoint(0);
            XYZ B = line.GetEndPoint(1);

            if (Math.Abs(B.Z - A.Z) < 0.01)
            { stats.Skip($"{el.Id}: curve is essentially horizontal — no level split needed"); return; }

            double lo = Math.Min(A.Z, B.Z);
            double hi = Math.Max(A.Z, B.Z);

            var splitPts = levels
                .Select(l => l.Elevation)
                .Where(z => z > lo + 1e-4 && z < hi - 1e-4)
                .OrderBy(z => z)
                .Select(z => PointOnLineAtZ(A, B, z))
                .Where(p => p != null)
                .ToList();

            if (!splitPts.Any())
            { stats.Skip($"{el.Id}: no levels fall within curve Z range [{lo:F2}, {hi:F2}]"); return; }

            SplitCurveElement(doc, el, A, B, splitPts, "level", stats);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Grid split implementations
        // ═════════════════════════════════════════════════════════════════════

        private static void SplitWallByGrid(
            Document doc, Wall wall,
            List<(XYZ Normal, XYZ Origin)> planes, SplitStats stats)
        {
            if (!(wall.Location is LocationCurve wlc) || !(wlc.Curve is Line wallLine))
            { stats.Skip($"Wall {wall.Id}: no linear LocationCurve"); return; }

            XYZ A = wallLine.GetEndPoint(0);
            XYZ B = wallLine.GetEndPoint(1);
            XYZ dir = B - A;

            var splitPts = planes
                .Select(p => LinePlaneIntersect(A, B, p.Normal, p.Origin))
                .Where(p => p != null)
                .OrderBy(p => dir.DotProduct(p - A))
                .ToList();

            if (!splitPts.Any())
            { stats.Skip($"Wall {wall.Id}: no grid intersections along wall"); return; }

            SplitCurveElement(doc, wall, A, B, splitPts, "grid", stats);
        }

        private static void SplitCurveByGrid(
            Document doc, Element el,
            List<(XYZ Normal, XYZ Origin)> planes, SplitStats stats)
        {
            if (!(el.Location is LocationCurve lc) || !(lc.Curve is Line line))
            { stats.Skip($"{el.Id}: no linear LocationCurve"); return; }

            XYZ A = line.GetEndPoint(0);
            XYZ B = line.GetEndPoint(1);
            XYZ dir = B - A;

            var splitPts = planes
                .Select(p => LinePlaneIntersect(A, B, p.Normal, p.Origin))
                .Where(p => p != null)
                .OrderBy(p => dir.DotProduct(p - A))
                .ToList();

            if (!splitPts.Any())
            { stats.Skip($"{el.Id}: no grid plane intersections along element"); return; }

            SplitCurveElement(doc, el, A, B, splitPts, "grid", stats);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Core curve split
        // ═════════════════════════════════════════════════════════════════════

        private static void SplitCurveElement(
            Document    doc,
            Element     el,
            XYZ         A,
            XYZ         B,
            List<XYZ?>  rawSplitPts,
            string      splitKind,
            SplitStats  stats)
        {
            var pts = DeduplicatePoints(rawSplitPts, 0.01);
            if (!pts.Any()) { stats.Skip($"{el.Id}: no split points after dedup"); return; }

            var seq = new List<XYZ> { A };
            seq.AddRange(pts);
            seq.Add(B);

            int segCount = seq.Count - 1;
            if (segCount < 2) { stats.Skip($"{el.Id}: only one segment after dedup"); return; }

            var segIds = new List<ElementId> { el.Id };
            for (int i = 1; i < segCount; i++)
            {
                try
                {
                    ICollection<ElementId> copied =
                        ElementTransformUtils.CopyElement(doc, el.Id, XYZ.Zero);
                    if (copied == null || !copied.Any())
                        throw new InvalidOperationException("CopyElement returned empty.");
                    segIds.Add(copied.First());
                }
                catch (Exception ex)
                {
                    stats.Fail(el.Id.ToString(), $"copy #{i} failed: {ex.Message}");
                    return;
                }
            }

            int success = 0;
            for (int i = 0; i < segIds.Count; i++)
            {
                Element seg = doc.GetElement(segIds[i]);
                if (seg == null) continue;

                XYZ segA = seq[i];
                XYZ segB = seq[i + 1];

                if (segA.DistanceTo(segB) < 0.01)
                {
                    if (i > 0) { try { doc.Delete(segIds[i]); } catch (Exception __lex) { LemoineLog.Swallowed("SplitElements: delete partial segment", __lex); } }
                    stats.Fail(el.Id.ToString(), $"seg {i}: degenerate length, removed.");
                    continue;
                }

                try
                {
                    DisconnectAllConnectors(seg);
                    if (seg is Wall wSeg)
                        DisallowWallJoins(doc, wSeg);
                    var segLc = seg.Location as LocationCurve;
                    if (segLc == null) throw new InvalidOperationException("No LocationCurve on copy.");
                    segLc.Curve = Line.CreateBound(segA, segB);
                    success++;
                }
                catch (Exception ex)
                {
                    stats.Fail(el.Id.ToString(), $"seg {i} curve set failed: {ex.Message}");
                }
            }

            if (success > 0)
                stats.Split($"{el.Category?.Name} {el.Id} → {success} {splitKind} segment(s)");
            else
                stats.Fail(el.Id.ToString(), "All segment curve assignments failed.");
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Geometry helpers (public for use by event handlers)
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns the point that lies on the line segment AB at elevation <paramref name="Z"/>.
        /// Returns <see langword="null"/> if the segment is essentially horizontal (ΔZ &lt; 1e-6),
        /// or if <paramref name="Z"/> falls outside the interior of the segment (i.e. the
        /// intersection would be at or beyond an endpoint).
        /// </summary>
        /// <param name="A">Start point of the line segment.</param>
        /// <param name="B">End point of the line segment.</param>
        /// <param name="Z">Target elevation (Revit internal units, feet).</param>
        /// <returns>The interpolated <see cref="XYZ"/> at elevation Z, or <see langword="null"/>.</returns>
        public static XYZ? PointOnLineAtZ(XYZ A, XYZ B, double Z)
        {
            double dZ = B.Z - A.Z;
            if (Math.Abs(dZ) < 1e-6) return null;
            double t = (Z - A.Z) / dZ;
            if (t <= 1e-4 || t >= 1.0 - 1e-4) return null;
            return new XYZ(A.X + t * (B.X - A.X),
                           A.Y + t * (B.Y - A.Y),
                           Z);
        }

        /// <summary>
        /// Computes the intersection of the line segment AB with an infinite plane defined by
        /// a normal vector and an origin point.  Returns <see langword="null"/> if the segment
        /// is parallel to the plane (denominator &lt; 1e-9) or the intersection falls at or
        /// beyond an endpoint of the segment.
        /// </summary>
        /// <param name="A">Start point of the line segment.</param>
        /// <param name="B">End point of the line segment.</param>
        /// <param name="planeNormal">Unit normal of the cutting plane.</param>
        /// <param name="planeOrigin">Any point that lies on the cutting plane.</param>
        /// <returns>The intersection <see cref="XYZ"/>, or <see langword="null"/>.</returns>
        public static XYZ? LinePlaneIntersect(
            XYZ A, XYZ B, XYZ planeNormal, XYZ planeOrigin)
        {
            XYZ AB = B - A;
            double denom = planeNormal.DotProduct(AB);
            if (Math.Abs(denom) < 1e-9) return null;
            double t = planeNormal.DotProduct(planeOrigin - A) / denom;
            if (t <= 1e-4 || t >= 1.0 - 1e-4) return null;
            return A + t * AB;
        }

        /// <summary>
        /// Derives a vertical cutting plane from a Revit <see cref="Grid"/> whose underlying
        /// curve is a straight line.  The plane normal is perpendicular to the grid direction
        /// in the horizontal (XY) plane.
        /// </summary>
        /// <param name="grid">The Revit grid to derive the plane from.</param>
        /// <returns>
        /// A tuple containing the plane's unit normal and an origin point on the plane,
        /// or <see langword="null"/> if the grid has no linear curve or the curve is
        /// degenerate (length &lt; 1e-9).
        /// </returns>
        public static (XYZ Normal, XYZ Origin)? TryBuildGridPlane(Grid grid)
        {
            try
            {
                if (!(grid.Curve is Line l)) return null;
                XYZ g0  = l.GetEndPoint(0);
                XYZ g1  = l.GetEndPoint(1);
                XYZ dir = new XYZ(g1.X - g0.X, g1.Y - g0.Y, 0.0);
                if (dir.GetLength() < 1e-9) return null;
                dir = dir.Normalize();
                return (new XYZ(-dir.Y, dir.X, 0.0), g0);
            }
            catch { return null; }
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static List<Level> BuildLevelSpans(
            List<Level> selected, Level baseLevel, Level topLevel)
        {
            double lo = Math.Min(baseLevel.Elevation, topLevel.Elevation);
            double hi = Math.Max(baseLevel.Elevation, topLevel.Elevation);

            var list = selected
                .Where(l => l.Elevation >= lo - 1e-4 && l.Elevation <= hi + 1e-4)
                .OrderBy(l => l.Elevation)
                .ToList();

            if (!list.Any(l => l.Id == baseLevel.Id)) list.Insert(0, baseLevel);
            if (!list.Any(l => l.Id == topLevel.Id))  list.Add(topLevel);

            return list.OrderBy(l => l.Elevation).ToList();
        }

        private static List<ElementId>? CopyTimes(Document doc, ElementId id, int n)
        {
            var result = new List<ElementId>();
            for (int i = 0; i < n; i++)
            {
                try
                {
                    ICollection<ElementId> c =
                        ElementTransformUtils.CopyElement(doc, id, XYZ.Zero);
                    if (c == null || !c.Any()) return null;
                    result.Add(c.First());
                }
                catch { return null; }
            }
            return result;
        }

        private static List<XYZ> DeduplicatePoints(IEnumerable<XYZ> pts, double tol)
        {
            var result = new List<XYZ>();
            foreach (XYZ p in pts)
                if (!result.Any(r => r.DistanceTo(p) < tol))
                    result.Add(p);
            return result;
        }

        private static void DisallowWallJoins(Document doc, Wall wall)
        {
            try { WallUtils.DisallowWallJoinAtEnd(wall, 0); } catch (Exception __lex) { LemoineLog.Swallowed("SplitElements: disallow wall join at end 0", __lex); }
            try { WallUtils.DisallowWallJoinAtEnd(wall, 1); } catch (Exception __lex) { LemoineLog.Swallowed("SplitElements: disallow wall join at end 1", __lex); }
        }

        private static void DisconnectAllConnectors(Element el)
        {
            try
            {
                ConnectorManager? cm = null;
                if      (el is MEPCurve      mc) cm = mc.ConnectorManager;
                else if (el is FamilyInstance fi) cm = fi.MEPModel?.ConnectorManager;
                if (cm == null) return;

                foreach (Connector c in cm.Connectors)
                {
                    try
                    {
                        foreach (Connector other in c.AllRefs.Cast<Connector>().ToList())
                            try { c.DisconnectFrom(other); } catch (Exception __lex) { LemoineLog.Swallowed("SplitElements: disconnect MEP connector", __lex); }
                    }
                    catch (Exception __lex) { LemoineLog.Swallowed("SplitElements: enumerate connector references", __lex); }
                }
            }
            catch (Exception __lex) { LemoineLog.Swallowed("SplitElements: disconnect element connectors", __lex); }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Stats accumulator
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Accumulates per-element outcomes (split, skipped, or failed) for a single
    /// <see cref="SplitElementsShared"/> operation and provides a formatted summary.
    /// Instances are created inside <see cref="SplitElementsShared.SplitByLevel"/> and
    /// <see cref="SplitElementsShared.SplitByGrid"/> and returned to callers.
    /// </summary>
    public sealed class SplitStats
    {
        /// <summary>
        /// The number of elements that were successfully split into two or more segments.
        /// </summary>
        public int SplitCount { get; private set; }

        /// <summary>
        /// The number of elements that were skipped because they had no applicable split
        /// boundaries (e.g. no levels within their vertical extent).  A skip is not an error.
        /// </summary>
        public int SkipCount  { get; private set; }

        /// <summary>
        /// The number of elements for which an error occurred during splitting.
        /// </summary>
        public int FailCount  { get; private set; }

        private readonly List<string> _log = new List<string>();

        /// <summary>
        /// Ordered log of all messages recorded during the operation.
        /// Each entry is prefixed with ✓ (split), — (skip), or ✗ (fail).
        /// </summary>
        public IReadOnlyList<string> Log => _log;

        /// <summary>
        /// Records a successful split and appends a ✓-prefixed message to <see cref="Log"/>.
        /// </summary>
        /// <param name="msg">Description of what was split (e.g. "Wall 12345 → 3 segments").</param>
        public void Split(string msg) { SplitCount++; _log.Add($"✓ {msg}"); }

        /// <summary>
        /// Records a skipped element and appends a —-prefixed message to <see cref="Log"/>.
        /// </summary>
        /// <param name="msg">Reason the element was skipped.</param>
        public void Skip(string msg)  { SkipCount++;  _log.Add($"— {msg}"); }

        /// <summary>
        /// Records a failure for a specific element and appends a ✗-prefixed message to <see cref="Log"/>.
        /// </summary>
        /// <param name="id">The element ID or identifier string to include in the log entry.</param>
        /// <param name="reason">A short description of why the element failed.</param>
        public void Fail(string id, string reason)
        {
            FailCount++;
            _log.Add($"✗ {id}: {reason}");
        }

        /// <summary>
        /// Returns a multi-line human-readable summary of the operation, including counts
        /// of split, skipped, and failed elements.
        /// </summary>
        /// <param name="opName">A label for the operation (e.g. "Split by Level") shown on the first line.</param>
        /// <returns>A formatted string suitable for display in a Revit task dialog.</returns>
        public string Summary(string opName) =>
            $"{opName} complete.\n\n" +
            $"  Split:    {SplitCount}\n" +
            $"  Skipped:  {SkipCount}\n" +
            $"  Failed:   {FailCount}";
    }
}
