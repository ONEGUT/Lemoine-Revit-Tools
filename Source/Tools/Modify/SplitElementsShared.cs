using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Framework;

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
        private static readonly BuiltInParameter WallHeight  = BuiltInParameter.WALL_USER_HEIGHT_PARAM;

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
            List<Level>          levels,
            RunProgressReporter? progress = null,
            Action<string, string>? liveLog = null)
        {
            var stats = new SplitStats(liveLog);

            foreach (Element el in elements)
            {
                // Abandon mid-run: stop processing more elements but let the caller's
                // tx.Commit() still run so every element split so far is preserved. The
                // handler logs the "Stopped by user" warn notice after this returns.
                if (RunState.CancelRequested) break;

                // Capture the id string up front: a successful split DELETES the source
                // element, and any property access on a deleted element (including .Id)
                // throws. The catch below must not touch a possibly-deleted element.
                string elId = "?";
                try
                {
                    if (el?.Category?.Id == null) { stats.Skip("No category"); continue; }
                    elId = el.Id.ToString();

                    // Property-based dispatch: works for any category.
                    // Wall is checked first because walls also have a LocationCurve.
                    if (el is Wall)
                        SplitWallByLevel(doc, (Wall)el, levels, stats);
                    else if (el.get_Parameter(ColBase) != null && el.get_Parameter(ColTop) != null)
                        SplitColumnByLevel(doc, el, levels, stats);
                    else if (el.Location is LocationCurve lc0 && lc0.Curve is Line)
                        SplitCurveByLevel(doc, el, levels, stats);
                    else
                        stats.Skip($"{el.Category.Name} {elId}: no applicable level-split strategy (not a wall, no level params, no linear curve)");
                }
                catch (Exception ex)
                {
                    stats.Fail(elId, ex.Message);
                }
                finally
                {
                    // Tick every element regardless of outcome so the 5% interval log
                    // reflects true progress through the selection.
                    progress?.Tick();
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
            List<Grid?>          grids,
            RunProgressReporter? progress = null,
            Action<string, string>? liveLog = null)
        {
            var planes = grids
                .Where(g => g != null)
                .Select(g => TryBuildGridPlane(g!))
                .Where(p => p.HasValue)
                .Select(p => p!.Value)
                .ToList();
            return SplitByPlanesCore(doc, elements, planes, "grids", progress, liveLog);
        }

        /// <summary>
        /// Splits each element in <paramref name="elements"/> at the plane of each
        /// <see cref="ReferencePlane"/> in <paramref name="refPlanes"/>, using the same
        /// CURVE strategy as the grid split.  The reference plane's own normal and origin
        /// are used directly as the cutting plane.
        /// </summary>
        public static SplitStats SplitByReferencePlane(
            Document              doc,
            IEnumerable<Element>  elements,
            List<ReferencePlane?> refPlanes,
            RunProgressReporter?  progress = null,
            Action<string, string>? liveLog = null)
        {
            var planes = refPlanes
                .Where(r => r != null)
                .Select(r => TryBuildReferencePlanePlane(r!))
                .Where(p => p.HasValue)
                .Select(p => p!.Value)
                .ToList();
            return SplitByPlanesCore(doc, elements, planes, "reference planes", progress, liveLog);
        }

        private static SplitStats SplitByPlanesCore(
            Document                       doc,
            IEnumerable<Element>           elements,
            List<(XYZ Normal, XYZ Origin)> planes,
            string                         contextLabel,
            RunProgressReporter?           progress = null,
            Action<string, string>?        liveLog  = null)
        {
            var stats = new SplitStats(liveLog);

            if (!planes.Any())
            {
                stats.Fail(contextLabel, "No valid cutting planes could be computed.");
                return stats;
            }

            foreach (Element el in elements)
            {
                // Abandon mid-run: stop processing more elements but let the caller's
                // tx.Commit() still run so every element split so far is preserved. The
                // handler logs the "Stopped by user" warn notice after this returns.
                if (RunState.CancelRequested) break;

                // Capture the id string up front (see SplitByLevel) so the catch never
                // touches a possibly-deleted element.
                string elId = "?";
                try
                {
                    if (el?.Category?.Id == null) { stats.Skip("No category"); continue; }
                    elId = el.Id.ToString();

                    // Property-based dispatch: works for any category.
                    // Wall is checked first because walls also have a LocationCurve.
                    if (el is Wall)
                        SplitWallByGrid(doc, (Wall)el, planes, stats);
                    else if (el.Location is LocationCurve lc0 && lc0.Curve is Line)
                        SplitCurveByGrid(doc, el, planes, stats);
                    else if (SplitByCellHelpers.IsSupportedForRecreation(el))
                        // Area-based elements (floors, ceilings, roofs, foundations, filled
                        // regions) have no LocationCurve — divide their footprint by the cutting
                        // planes and recreate one element per region.
                        SplitAreaElementByPlanes(doc, el, planes, stats);
                    else
                        stats.Skip($"{el.Category.Name} {elId}: no applicable plane-split strategy (not a wall, no linear curve, not an area element)");
                }
                catch (Exception ex)
                {
                    stats.Fail(elId, ex.Message);
                }
                finally
                {
                    // Tick every element regardless of outcome so the 5% interval log
                    // reflects true progress through the selection.
                    progress?.Tick();
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

            if (baseLevel == null)
            { stats.Skip($"Wall {wall.Id}: not level-constrained"); return; }

            // A wall whose Top Constraint is "Unconnected" has no top level — its extent is
            // driven by its base level/offset plus the Unconnected Height. The level-to-level
            // path can't handle it (it needs a top level), so route it to a dedicated method
            // that keeps the top segment unconnected. This was the "split by level didn't work
            // for unconnected walls" gap.
            if (topLevel == null)
            {
                SplitWallByLevelUnconnected(doc, wall, baseLevel, levels, stats);
                return;
            }

            var spans = BuildLevelSpans(levels, baseLevel, topLevel);
            if (spans.Count < 3)
            { stats.Skip($"Wall {wall.Id}: no selected levels fall between its base and top constraints"); return; }

            double baseOff = wall.get_Parameter(WallBaseOff)?.AsDouble() ?? 0.0;
            double topOff  = wall.get_Parameter(WallTopOff)?.AsDouble()  ?? 0.0;

            // Capture the id before any delete — logging wall.Id after doc.Delete(wall.Id)
            // throws InvalidObjectException and would abort the whole run.
            string wallId = wall.Id.ToString();

            var copies = CopyTimes(doc, wall.Id, spans.Count - 1);
            if (copies == null) { stats.Fail(wallId, "Copy failed"); return; }

            DisallowWallJoins(doc, wall);
            foreach (var cid in copies)
            {
                var cwall = doc.GetElement(cid) as Wall;
                if (cwall != null) DisallowWallJoins(doc, cwall);
            }

            int successes = 0;
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
                    successes++;
                }
                // Per-segment note: logged but NOT counted as a failed element (the element's
                // single overall outcome is recorded below) so the "failed" tally can't exceed
                // the number of elements processed.
                catch (Exception ex) { stats.FailNote($"Wall {wallId} seg {k}: {ex.Message}"); }
            }

            if (successes == copies.Count)
            {
                doc.Delete(wall.Id);
                stats.Split(copies.Count, $"Wall {wallId} → {copies.Count} segments");
            }
            else
            {
                foreach (var cid in copies) try { doc.Delete(cid); } catch (Exception __lex) { DiagnosticsLog.Swallowed("SplitElements: delete unused wall copy", __lex); }
                stats.Fail(wallId,
                    successes == 0
                        ? "All segment parameter assignments failed; copies removed."
                        : $"Only {successes}/{copies.Count} segments configured; copies removed.");
            }
        }

        // Splits a wall whose top is "Unconnected" (no top level). The wall spans from its
        // base level (+ base offset) up by its Unconnected Height. Every selected level that
        // falls strictly inside that span becomes a cut. Lower/middle segments are re-constrained
        // level-to-level; the TOP segment stays Unconnected, with its Unconnected Height reduced
        // to the remaining distance above the highest cut level.
        private static void SplitWallByLevelUnconnected(
            Document doc, Wall wall, Level baseLevel, List<Level> levels, SplitStats stats)
        {
            double baseOff  = wall.get_Parameter(WallBaseOff)?.AsDouble() ?? 0.0;
            double height   = wall.get_Parameter(WallHeight)?.AsDouble()  ?? 0.0;

            if (height <= 1e-4)
            { stats.Skip($"Wall {wall.Id}: unconnected height is not set"); return; }

            double baseZ = baseLevel.Elevation + baseOff;
            double topZ  = baseZ + height;

            // Selected levels strictly interior to (baseZ, topZ), low → high.
            var cutLevels = levels
                .Where(l => l.Elevation > baseZ + 1e-4 && l.Elevation < topZ - 1e-4)
                .OrderBy(l => l.Elevation)
                .ToList();

            if (cutLevels.Count == 0)
            { stats.Skip($"Wall {wall.Id}: no selected levels fall within its unconnected height span"); return; }

            // Lower bound of each segment: base level, then each cut level. There is one more
            // segment than there are cut levels; the last segment's top stays Unconnected.
            var lowerLevels = new List<Level> { baseLevel };
            lowerLevels.AddRange(cutLevels);
            int segCount = lowerLevels.Count;

            string wallId = wall.Id.ToString();

            var copies = CopyTimes(doc, wall.Id, segCount);
            if (copies == null) { stats.Fail(wallId, "Copy failed"); return; }

            DisallowWallJoins(doc, wall);
            foreach (var cid in copies)
            {
                var cwall = doc.GetElement(cid) as Wall;
                if (cwall != null) DisallowWallJoins(doc, cwall);
            }

            int successes = 0;
            for (int k = 0; k < copies.Count; k++)
            {
                var seg = doc.GetElement(copies[k]) as Wall;
                if (seg == null) continue;
                try
                {
                    Level lower = lowerLevels[k];
                    seg.get_Parameter(WallBase)?.Set(lower.Id);
                    seg.get_Parameter(WallBaseOff)?.Set(k == 0 ? baseOff : 0.0);

                    if (k < segCount - 1)
                    {
                        // Level-to-level segment: top constraint = next lower level.
                        seg.get_Parameter(WallTop)?.Set(lowerLevels[k + 1].Id);
                        seg.get_Parameter(WallTopOff)?.Set(0.0);
                    }
                    else
                    {
                        // Top segment: keep Unconnected. Setting the top constraint to
                        // InvalidElementId is how Revit models "Unconnected"; the remaining
                        // height carries the wall from the highest cut level up to the original
                        // top elevation.
                        seg.get_Parameter(WallTop)?.Set(ElementId.InvalidElementId);
                        seg.get_Parameter(WallHeight)?.Set(topZ - lower.Elevation);
                    }
                    successes++;
                }
                catch (Exception ex) { stats.FailNote($"Wall {wallId} seg {k}: {ex.Message}"); }
            }

            if (successes == copies.Count)
            {
                doc.Delete(wall.Id);
                stats.Split(copies.Count, $"Wall {wallId} → {copies.Count} segments (unconnected top)");
            }
            else
            {
                foreach (var cid in copies) try { doc.Delete(cid); } catch (Exception __lex) { DiagnosticsLog.Swallowed("SplitElements: delete unused unconnected wall copy", __lex); }
                stats.Fail(wallId,
                    successes == 0
                        ? "All segment parameter assignments failed; copies removed."
                        : $"Only {successes}/{copies.Count} segments configured; copies removed.");
            }
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
            if (spans.Count < 3)
            { stats.Skip($"Column {col.Id}: no selected levels fall between its base and top constraints"); return; }

            double baseOff = col.get_Parameter(ColBaseOff)?.AsDouble() ?? 0.0;
            double topOff  = col.get_Parameter(ColTopOff)?.AsDouble()  ?? 0.0;

            // Capture the id before any delete (see SplitWallByLevel).
            string colId = col.Id.ToString();

            var copies = CopyTimes(doc, col.Id, spans.Count - 1);
            if (copies == null) { stats.Fail(colId, "Copy failed"); return; }

            int successes = 0;
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
                    successes++;
                }
                catch (Exception ex) { stats.FailNote($"Column {colId} seg {k}: {ex.Message}"); }
            }

            if (successes == copies.Count)
            {
                doc.Delete(col.Id);
                stats.Split(copies.Count, $"Column {colId} → {copies.Count} segments");
            }
            else
            {
                foreach (var cid in copies) try { doc.Delete(cid); } catch (Exception __lex) { DiagnosticsLog.Swallowed("SplitElements: delete unused column copy", __lex); }
                stats.Fail(colId,
                    successes == 0
                        ? "All segment parameter assignments failed; copies removed."
                        : $"Only {successes}/{copies.Count} segments configured; copies removed.");
            }
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
        //  Area-element plane split (floors, ceilings, roofs, foundations)
        // ═════════════════════════════════════════════════════════════════════

        private static void SplitAreaElementByPlanes(
            Document doc, Element el,
            List<(XYZ Normal, XYZ Origin)> planes, SplitStats stats)
        {
            string  elId  = el.Id.ToString();
            string? elCat = el.Category?.Name;

            // Only VERTICAL cutting planes divide a plan footprint. A grid always yields one;
            // a reference plane counts only when its normal is horizontal.
            var vplanes = planes.Where(p => Math.Abs(p.Normal.Z) < 1e-6).ToList();
            if (vplanes.Count == 0)
            { stats.Skip($"{elCat} {elId}: no vertical cutting planes apply to area elements"); return; }

            Solid? solid = SplitByCellHelpers.GetPrimarySolid(el);
            if (solid == null || solid.Volume < 1e-9)
            { stats.Skip($"{elCat} {elId}: no solid geometry to split (filled regions use Split by Cell)"); return; }

            // Incrementally slice the solid: each plane splits every current region into its two
            // half-spaces. Empty halves are dropped; a plane that misses a region leaves it whole.
            // This produces only the non-empty arrangement cells without enumerating 2^k combos.
            var regions = new List<Solid> { solid };
            foreach (var p in vplanes)
            {
                var next = new List<Solid>();
                foreach (var region in regions)
                {
                    Solid? pos = SplitByCellHelpers.HalfSpaceIntersect(region, p.Normal, p.Origin, +1);
                    Solid? neg = SplitByCellHelpers.HalfSpaceIntersect(region, p.Normal, p.Origin, -1);
                    bool posOk = pos != null && pos.Volume > 1e-9;
                    bool negOk = neg != null && neg.Volume > 1e-9;
                    if (posOk) next.Add(pos!);
                    if (negOk) next.Add(neg!);
                    // Both halves empty means the boolean failed on this region — keep it whole
                    // rather than silently losing it.
                    if (!posOk && !negOk) next.Add(region);
                }
                regions = next;
            }

            if (regions.Count <= 1)
            { stats.Skip($"{elCat} {elId}: no selected plane crosses the element"); return; }

            // Extract each region's horizontal top-face loops — the footprint the recreate APIs need.
            var pendingLoops = new List<IList<CurveLoop>>();
            int dropped = 0;
            foreach (var region in regions)
            {
                IList<CurveLoop>? loops = SplitByCellHelpers.ExtractTopFaceLoops(region);
                if (loops == null || loops.Count == 0) { dropped++; continue; }
                pendingLoops.Add(loops);
            }

            if (pendingLoops.Count <= 1)
            { stats.Skip($"{elCat} {elId}: could not derive split footprints (element may be sloped — use Split by Cell)"); return; }

            var newIds = new List<ElementId>();
            foreach (var loops in pendingLoops)
            {
                try
                {
                    ElementId newId = SplitByCellHelpers.RecreateElement(doc, el, loops);
                    if (newId == null || newId == ElementId.InvalidElementId) { dropped++; continue; }
                    newIds.Add(newId);
                }
                catch (Exception ex)
                {
                    dropped++;
                    DiagnosticsLog.Swallowed($"SplitElements: area plane-split recreation failed on {el.Id.Value}", ex);
                }
            }

            if (newIds.Count == 0)
            { stats.Fail(elId, "no split region could be recreated; element left intact."); return; }

            doc.Delete(el.Id);
            stats.Split(newIds.Count,
                dropped > 0
                    ? $"{elCat} {elId} → {newIds.Count} region(s) ({dropped} dropped)"
                    : $"{elCat} {elId} → {newIds.Count} region(s)");
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
            // Keep only split points strictly interior to the segment. A point within
            // tolerance of an endpoint would create a degenerate sub-segment — and for
            // the FIRST segment that is destructive: the original element (segIds[0])
            // keeps its full A→B length while the copies cover the real sub-ranges,
            // leaving overlapping duplicate geometry. Excluding endpoint-coincident
            // points guarantees every consecutive pair in `seq` is a valid segment.
            const double endTol = 0.01;
            var pts = DeduplicatePoints(rawSplitPts.Where(p => p != null).Cast<XYZ>(), endTol)
                .Where(p => p.DistanceTo(A) > endTol && p.DistanceTo(B) > endTol)
                .ToList();
            if (!pts.Any()) { stats.Skip($"{el.Id}: no interior split points after dedup"); return; }

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
                    // Clean up all copies made so far before bailing (#2)
                    foreach (var cid in segIds.Skip(1))
                        try { doc.Delete(cid); } catch (Exception __lex) { DiagnosticsLog.Swallowed("SplitElements: clean up partial copies after failure", __lex); }
                    stats.Fail(el.Id.ToString(), $"copy #{i} failed: {ex.Message}");
                    return;
                }
            }

            // el is segIds[0] — the ORIGINAL element, re-curved (never deleted here), so
            // el.Id/Category stay valid. Capture them once for the log lines regardless.
            string  elId  = el.Id.ToString();
            string? elCat = el.Category?.Name;

            int success = 0;
            for (int i = 0; i < segIds.Count; i++)
            {
                Element seg = doc.GetElement(segIds[i]);
                if (seg == null) continue;

                XYZ segA = seq[i];
                XYZ segB = seq[i + 1];

                if (segA.DistanceTo(segB) < 0.01)
                {
                    // Segment 0 IS the original element. If its own sub-segment is degenerate,
                    // re-curving it would corrupt the original while the copies cover the real
                    // sub-ranges — leaving overlapping duplicate geometry. Abort the whole
                    // element instead: delete every copy and leave the original untouched.
                    if (i == 0)
                    {
                        CleanupCopies(doc, segIds);
                        stats.Fail(elId, "first segment is degenerate; element left intact.");
                        return;
                    }
                    try { doc.Delete(segIds[i]); } catch (Exception __lex) { DiagnosticsLog.Swallowed("SplitElements: delete partial segment", __lex); }
                    stats.FailNote($"{elId} seg {i}: degenerate length, removed.");
                    continue;
                }

                try
                {
                    // Validate geometry before touching connectors (#7): if CreateBound throws,
                    // the element's connectors are left intact.
                    Line newCurve = Line.CreateBound(segA, segB);
                    var segLc = seg.Location as LocationCurve;
                    if (segLc == null) throw new InvalidOperationException("No LocationCurve on copy.");
                    if (seg is Wall wSeg) DisallowWallJoins(doc, wSeg);
                    DisconnectAllConnectors(seg);
                    segLc.Curve = newCurve;
                    success++;
                }
                catch (Exception ex)
                {
                    // Segment 0 failure = original couldn't take its sub-curve. Abort the whole
                    // element (delete all copies) so the still-full-length original is never left
                    // overlapping the copies.
                    if (i == 0)
                    {
                        CleanupCopies(doc, segIds);
                        stats.Fail(elId, $"first segment curve set failed: {ex.Message}; element left intact.");
                        return;
                    }
                    // Remove orphaned copy; per-segment note (log-only, uncounted).
                    try { doc.Delete(segIds[i]); } catch (Exception __lex) { DiagnosticsLog.Swallowed("SplitElements: delete orphaned segment copy", __lex); }
                    stats.FailNote($"{elId} seg {i} curve set failed: {ex.Message}");
                }
            }

            if (success == segIds.Count)
                stats.Split(success, $"{elCat} {elId} → {success} {splitKind} segment(s)");
            else if (success > 0)
                // Some sub-segments failed and were removed — the run is incomplete and
                // can leave a gap along the original curve. Report it as such rather than
                // a clean success so the user knows to check the result.
                stats.Split(success, $"{elCat} {elId} → {success}/{segIds.Count} {splitKind} segment(s) (some failed — result may have a gap)");
            else
                stats.Fail(elId, "All segment curve assignments failed.");
        }

        // Deletes every COPY made for a curve split (segIds[1..]), leaving the original
        // (segIds[0]) untouched. Used when the original itself can't be re-curved.
        private static void CleanupCopies(Document doc, List<ElementId> segIds)
        {
            foreach (var cid in segIds.Skip(1))
                try { doc.Delete(cid); }
                catch (Exception __lex) { DiagnosticsLog.Swallowed("SplitElements: delete copies after original-segment failure", __lex); }
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
        /// <summary>
        /// Derives a cutting plane from a <see cref="ReferencePlane"/>.  The plane's own
        /// normal and origin are used directly.  Returns <see langword="null"/> if the
        /// normal is degenerate.
        /// </summary>
        public static (XYZ Normal, XYZ Origin)? TryBuildReferencePlanePlane(ReferencePlane rp)
        {
            try
            {
                Plane plane = rp.GetPlane();
                if (plane.Normal.GetLength() < 1e-9) return null;
                return (plane.Normal.Normalize(), plane.Origin);
            }
            catch { return null; }
        }

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
                    if (c == null || !c.Any())
                    {
                        foreach (var cid in result) try { doc.Delete(cid); } catch (Exception __lex) { DiagnosticsLog.Swallowed("SplitElements: delete copies after empty copy result", __lex); }
                        return null;
                    }
                    result.Add(c.First());
                }
                catch (Exception ex)
                {
                    // Surface the root cause to diagnostics — the caller only sees a generic
                    // "Copy failed" line, so without this the real error would be lost entirely.
                    DiagnosticsLog.Swallowed("SplitElements: CopyElement failed in CopyTimes", ex);
                    foreach (var cid in result) try { doc.Delete(cid); } catch (Exception __lex) { DiagnosticsLog.Swallowed("SplitElements: clean up copies after copy failure", __lex); }
                    return null;
                }
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
            try { WallUtils.DisallowWallJoinAtEnd(wall, 0); } catch (Exception __lex) { DiagnosticsLog.Swallowed("SplitElements: disallow wall join at end 0", __lex); }
            try { WallUtils.DisallowWallJoinAtEnd(wall, 1); } catch (Exception __lex) { DiagnosticsLog.Swallowed("SplitElements: disallow wall join at end 1", __lex); }
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
                            try { c.DisconnectFrom(other); } catch (Exception __lex) { DiagnosticsLog.Swallowed("SplitElements: disconnect MEP connector", __lex); }
                    }
                    catch (Exception __lex) { DiagnosticsLog.Swallowed("SplitElements: enumerate connector references", __lex); }
                }
            }
            catch (Exception __lex) { DiagnosticsLog.Swallowed("SplitElements: disconnect element connectors", __lex); }
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
        // Optional live run-log sink. When supplied, every Split/Skip/Fail line is pushed to
        // the Output log the moment it happens (status pass/info/fail) instead of the caller
        // dumping the whole Log list after the transaction commits.
        private readonly Action<string, string>? _live;

        /// <summary>Creates a stats accumulator, optionally streaming each line live to a run log.</summary>
        /// <param name="liveLog">Run-log callback (text, status); null buffers into <see cref="Log"/> only.</param>
        public SplitStats(Action<string, string>? liveLog = null) { _live = liveLog; }

        private void Emit(string line, string status)
        {
            _log.Add(line);
            _live?.Invoke(line, status);
        }

        /// <summary>
        /// The number of source elements that were successfully split into two or more segments.
        /// </summary>
        public int SplitCount { get; private set; }

        /// <summary>
        /// The tool's real deliverable: the total number of new segments created across all
        /// elements (e.g. one wall split into 4 contributes 4). This is what the run reports
        /// as "pass", so the headline count reflects what the tool produced, not how many
        /// inputs it touched.
        /// </summary>
        public int SegmentsCreated { get; private set; }

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
        /// <param name="segments">Number of new segments this element was split into.</param>
        /// <param name="msg">Description of what was split (e.g. "Wall 12345 → 3 segments").</param>
        public void Split(int segments, string msg)
        {
            SplitCount++;
            SegmentsCreated += segments;
            Emit($"✓ {msg}", "pass");
        }

        /// <summary>
        /// Records a skipped element and appends a —-prefixed message to <see cref="Log"/>.
        /// </summary>
        /// <param name="msg">Reason the element was skipped.</param>
        public void Skip(string msg)  { SkipCount++;  Emit($"— {msg}", "info"); }

        /// <summary>
        /// Records a failure for a specific element and appends a ✗-prefixed message to <see cref="Log"/>.
        /// </summary>
        /// <param name="id">The element ID or identifier string to include in the log entry.</param>
        /// <param name="reason">A short description of why the element failed.</param>
        public void Fail(string id, string reason)
        {
            FailCount++;
            Emit($"✗ {id}: {reason}", "fail");
        }

        /// <summary>
        /// Logs a sub-element failure detail (e.g. one segment of a multi-segment split) WITHOUT
        /// incrementing <see cref="FailCount"/>. The element's single overall outcome is recorded
        /// separately via <see cref="Fail"/> or <see cref="Split"/>, so the failed tally counts
        /// elements, not log lines, and never exceeds the number of elements processed.
        /// </summary>
        public void FailNote(string msg) { Emit($"✗ {msg}", "fail"); }

        /// <summary>
        /// Returns a multi-line human-readable summary of the operation, including counts
        /// of split, skipped, and failed elements.
        /// </summary>
        /// <param name="opName">A label for the operation (e.g. "Split by Level") shown on the first line.</param>
        /// <returns>A formatted string suitable for display in a Revit task dialog.</returns>
        public string Summary(string opName) =>
            $"{opName} complete.\n\n" +
            $"  Segments created: {SegmentsCreated}  (from {SplitCount} element(s))\n" +
            $"  Skipped:          {SkipCount}\n" +
            $"  Failed:           {FailCount}";
    }
}
