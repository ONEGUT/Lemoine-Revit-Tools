using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace LemoineTools.Framework.Zones
{
    // =========================================================================
    // ZonePlacementService — turns a layout into stored placements, and reads a
    // placement back off a sheet the user positioned by hand.
    //
    // Two ways to author a placement, both supported deliberately:
    //
    //   SOLVE   — centre the group in the drawing area at the fitted scale.
    //             Deterministic, needs no sheet to exist yet, and is what makes
    //             a zone usable the moment it is defined.
    //   CAPTURE — read the anchor pair back off a viewport the user placed and
    //             nudged until it looked right. This is what makes the location
    //             genuinely theirs rather than merely centred.
    //
    // Captured beats solved: a re-solve never silently overwrites a captured
    // placement, because that would discard a deliberate decision in favour of a
    // default. Overwriting one is always an explicit act.
    //
    // The rule that governs everything here:
    //
    //   A PLACEMENT IS A PAIR — a world anchor AND the sheet coordinate it
    //   occupies. Refreshing half of it moves the drawing. Measured on the
    //   solver's Python port: an area whose extents grow 10 ft, re-anchored from
    //   its new centre but kept at its stored sheet coordinate, shifts the
    //   building 5/8" on a 1/8" plot, silently.
    // =========================================================================
    public static class ZonePlacementService
    {
        /// <summary>Outcome of solving one layout's placements.</summary>
        public sealed class SolveReport
        {
            public int Solved   { get; set; }
            public int Skipped  { get; set; }
            public int Overflow { get; set; }
            public int Kept     { get; set; }
            public List<string> Problems { get; } = new List<string>();
        }

        /// <summary>
        /// Solves and stores placements for every group in a layout.
        ///
        /// <paramref name="overwriteCaptured"/> defaults to false so a captured placement
        /// survives a re-solve. Mutates the library only — no Revit writes, no transaction.
        /// </summary>
        public static SolveReport SolveLayout(Document? doc, ZoneLibrary? library,
                                              ZoneSheetSet? layout,
                                              bool overwriteCaptured = false,
                                              Action<string, string>? log = null)
        {
            var report = new SolveReport();
            if (doc == null || library == null || layout == null) return report;

            void Say(string m, string t)
            {
                try { log?.Invoke(m, t); } catch (Exception ex) { DiagnosticsLog.Swallowed("ZonePlacementService: log", ex); }
            }

            var areaResult = ZoneTitleBlocks.Resolve(doc, layout, null, log);
            if (!areaResult.Ok || areaResult.Area == null)
            {
                report.Problems.Add($"No drawing area for layout '{layout.Name}'.");
                return report;
            }

            ZoneTitleBlocks.CheckRecordedSize(layout, areaResult.SheetWidthFt, areaResult.SheetHeightFt, log);

            // Record the size this layout was solved against, so a later title block change is
            // detectable rather than merely wrong.
            layout.SheetWidthFt  = areaResult.SheetWidthFt;
            layout.SheetHeightFt = areaResult.SheetHeightFt;

            var groups = layout.Groups ?? new List<ZoneSheetGroup>();
            if (groups.Count == 0)
            {
                Say($"Layout '{layout.Name}' has no groups — nothing to place.", "warn");
                return report;
            }

            foreach (var group in groups)
            {
                if (group == null) continue;

                var inputs = new List<ZoneGroupSolver.AreaInput>();
                foreach (string areaId in group.AreaIds ?? new List<string>())
                {
                    var area = library.Area(areaId);
                    if (area == null)
                    {
                        report.Problems.Add($"Group '{GroupLabel(group)}' references an area that no longer exists.");
                        Say($"Layout '{layout.Name}', group '{GroupLabel(group)}': an area in this group " +
                            "no longer exists in the library.", "warn");
                        continue;
                    }
                    if (!area.HasExtents)
                    {
                        report.Skipped++;
                        report.Problems.Add($"Area '{area.Name}' has no resolved extents.");
                        Say($"Area '{area.Name}': no extents resolved yet — it cannot be placed. " +
                            "Adopt or solve its extents first.", "warn");
                        continue;
                    }

                    inputs.Add(new ZoneGroupSolver.AreaInput
                    {
                        AreaId  = area.Id,
                        Label   = area.Name,
                        MinX    = area.MinX, MinY = area.MinY,
                        MaxX    = area.MaxX, MaxY = area.MaxY,
                        AnchorX = area.HasAnchor ? area.AnchorX : (area.MinX + area.MaxX) / 2.0,
                        AnchorY = area.HasAnchor ? area.AnchorY : (area.MinY + area.MaxY) / 2.0,
                    });
                }

                if (inputs.Count == 0) continue;

                var solved = ZoneGroupSolver.Solve(
                    inputs, areaResult.Area, layout.Composition,
                    layout.GapPaperFt, group.ScaleOverride);

                if (!solved.Fits)
                {
                    report.Overflow++;
                    Say($"Layout '{layout.Name}', group '{GroupLabel(group)}': the areas do not fit the " +
                        $"drawing area even at {ZoneScaleFit.Label(solved.Scale)} " +
                        $"(short by {Math.Max(0, -solved.SlackXFt) * 12:0.#}\" × " +
                        $"{Math.Max(0, -solved.SlackYFt) * 12:0.#}\").", "warn");
                }

                // Overlaps are always reported. On a Continuous group a hit means the source
                // areas overlap in the world — a planning error that must surface here rather
                // than as two drawings printed on top of each other.
                foreach (var o in solved.Overlaps)
                {
                    report.Problems.Add($"'{o.LabelA}' and '{o.LabelB}' overlap on the sheet.");
                    Say($"Layout '{layout.Name}', group '{GroupLabel(group)}': '{o.LabelA}' and " +
                        $"'{o.LabelB}' overlap by {o.OverlapWidthFt * 12:0.#}\" × " +
                        $"{o.OverlapHeightFt * 12:0.#}\" on the sheet.", "warn");
                }

                bool composite = inputs.Count > 1;
                string groupKey = composite ? group.Id : "";

                foreach (var placed in solved.Items)
                {
                    var existing = library.Placement(placed.AreaId, layout.TitleBlockTypeName, groupKey);
                    if (existing != null &&
                        existing.Source == ZonePlacementSource.Captured &&
                        !overwriteCaptured)
                    {
                        report.Kept++;
                        Say($"Area '{placed.Label}': keeping the placement captured from sheet " +
                            $"{existing.CapturedFromSheetNumber} rather than replacing it with a solved one.", "info");
                        continue;
                    }

                    library.SetPlacement(new ZoneSheetPlacement
                    {
                        AreaId             = placed.AreaId,
                        TitleBlockTypeName = layout.TitleBlockTypeName,
                        GroupId            = groupKey,
                        SheetWidthFt       = areaResult.SheetWidthFt,
                        SheetHeightFt      = areaResult.SheetHeightFt,
                        AnchorWorldX       = placed.AnchorWorldX,
                        AnchorWorldY       = placed.AnchorWorldY,
                        AnchorSheetX       = placed.AnchorSheetX,
                        AnchorSheetY       = placed.AnchorSheetY,
                        Scale              = solved.Scale,
                        Source             = ZonePlacementSource.Solved,
                    });
                    report.Solved++;
                }
            }

            Say($"Layout '{layout.Name}': {report.Solved} placement(s) solved, {report.Kept} captured one(s) kept, " +
                $"{report.Skipped} skipped, {report.Overflow} group(s) overflowing.",
                report.Overflow + report.Skipped > 0 ? "warn" : "info");

            return report;
        }

        /// <summary>
        /// Reads a placement back off a viewport the user positioned by hand.
        ///
        /// The viewport's geometry is only valid after a regeneration following its creation or
        /// its last SetBoxCenter — the caller must have regenerated. This reads the box centre
        /// it is given; it never infers a position by comparing two centres, because
        /// GetBoxCenter tracks the view title and a centre difference can be a title change
        /// rather than a move.
        /// </summary>
        public static ZoneSheetPlacement? CaptureFromViewport(Document? doc, ZoneLibrary? library,
                                                              Viewport? viewport, ZoneArea? area,
                                                              string titleBlockTypeName, string groupId,
                                                              Action<string, string>? log = null)
        {
            if (doc == null || library == null || viewport == null || area == null) return null;

            void Say(string m, string t)
            {
                try { log?.Invoke(m, t); } catch (Exception ex) { DiagnosticsLog.Swallowed("ZonePlacementService: log", ex); }
            }

            try
            {
                if (!(doc.GetElement(viewport.ViewId) is View view))
                {
                    Say("Could not read the view behind that viewport.", "fail");
                    return null;
                }

                int scale = view.Scale;
                if (scale <= 0)
                {
                    Say($"'{view.Name}' has no usable scale — a placement cannot be captured from it.", "fail");
                    return null;
                }

                BoundingBoxXYZ cb = view.CropBox;
                if (cb?.Transform == null)
                {
                    Say($"'{view.Name}' has no crop box — a placement cannot be captured from it.", "fail");
                    return null;
                }

                ReadAnnotationCrop(view, out bool annoActive,
                                   out double top, out double bottom, out double left, out double right, Say);

                // Where the view's own model-crop centre sits on the sheet right now.
                XYZ anchorSheet = SheetAnchorMath.SourceAnchorOnSheet(
                    viewport.GetBoxCenter(), cb.Min, cb.Max,
                    annoActive, top, bottom, left, right,
                    scale, viewport.Rotation);

                // The world point that corresponds to. Captured from the VIEW, so the stored
                // pair describes this drawing exactly — not the area's nominal anchor, which
                // may differ if the crop was adjusted.
                XYZ anchorWorld = SheetAnchorMath.AnchorWorld(cb.Transform, cb.Min, cb.Max);

                string sheetNumber = "";
                try
                {
                    if (doc.GetElement(viewport.SheetId) is ViewSheet sh) sheetNumber = sh.SheetNumber ?? "";
                }
                catch (Exception ex) { DiagnosticsLog.Swallowed("ZonePlacementService: read sheet number", ex); }

                var placement = new ZoneSheetPlacement
                {
                    AreaId                  = area.Id,
                    TitleBlockTypeName      = titleBlockTypeName ?? "",
                    GroupId                 = groupId ?? "",
                    AnchorWorldX            = anchorWorld.X,
                    AnchorWorldY            = anchorWorld.Y,
                    AnchorSheetX            = anchorSheet.X,
                    AnchorSheetY            = anchorSheet.Y,
                    Scale                   = scale,
                    Source                  = ZonePlacementSource.Captured,
                    CapturedFromSheetNumber = sheetNumber,
                    CapturedUtcTicks        = DateTime.UtcNow.Ticks,
                };

                library.SetPlacement(placement);
                Say($"Captured the placement of '{area.Name}' from sheet {sheetNumber} " +
                    $"at {ZoneScaleFit.Label(scale)}.", "pass");
                return placement;
            }
            catch (Exception ex)
            {
                Say("Could not capture that placement.", "fail");
                DiagnosticsLog.Error("ZonePlacementService: capture from viewport", ex);
                return null;
            }
        }

        /// <summary>
        /// The sheet point to give <c>Viewport.SetBoxCenter</c> so a stored placement lands
        /// exactly where it was authored.
        ///
        /// The placement's OWN world anchor is used, never one recomputed from the area's
        /// current extents — see the note at the top of this file.
        /// </summary>
        public static XYZ? BoxCentreFor(Document? doc, ZoneSheetPlacement? placement,
                                        Viewport? viewport, Action<string, string>? log = null)
        {
            if (doc == null || placement == null || viewport == null) return null;

            void Say(string m, string t)
            {
                try { log?.Invoke(m, t); } catch (Exception ex) { DiagnosticsLog.Swallowed("ZonePlacementService: log", ex); }
            }

            try
            {
                if (!(doc.GetElement(viewport.ViewId) is View view)) return null;

                int scale = view.Scale;
                if (scale <= 0)
                {
                    Say($"'{view.Name}' has no usable scale — it cannot be positioned from a stored placement.", "warn");
                    return null;
                }

                BoundingBoxXYZ cb = view.CropBox;
                if (cb?.Transform == null)
                {
                    Say($"'{view.Name}' has no crop box — it cannot be positioned from a stored placement.", "warn");
                    return null;
                }

                if (scale != placement.Scale)
                    Say($"'{view.Name}' is at {ZoneScaleFit.Label(scale)} but its placement was authored at " +
                        $"{ZoneScaleFit.Label(placement.Scale)}. It will register on the stored anchor, " +
                        "but the drawing will not be the size the placement assumed.", "warn");

                ReadAnnotationCrop(view, out bool annoActive,
                                   out double top, out double bottom, out double left, out double right, Say);

                return SheetAnchorMath.BoxCentreForAnchor(
                    new XYZ(placement.AnchorWorldX, placement.AnchorWorldY, 0),
                    new XYZ(placement.AnchorSheetX, placement.AnchorSheetY, 0),
                    cb.Transform, cb.Min, cb.Max,
                    annoActive, top, bottom, left, right,
                    scale, viewport.Rotation);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZonePlacementService: compute box centre", ex);
                return null;
            }
        }

        /// <summary>
        /// Reads a view's annotation-crop state (offsets are model feet). A failure is reported
        /// rather than silently treated as "no annotation crop" — that silence would reintroduce
        /// exactly the mis-placement the footprint maths exists to remove.
        /// </summary>
        private static void ReadAnnotationCrop(View v, out bool active,
                                               out double top, out double bottom,
                                               out double left, out double right,
                                               Action<string, string> say)
        {
            active = false; top = bottom = left = right = 0;
            try
            {
                var p = v.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                if (p == null || p.AsInteger() != 1) return;
                ViewCropRegionShapeManager sm = v.GetCropRegionShapeManager();
                top    = sm.TopAnnotationCropOffset;
                bottom = sm.BottomAnnotationCropOffset;
                left   = sm.LeftAnnotationCropOffset;
                right  = sm.RightAnnotationCropOffset;
                active = true;
            }
            catch (Exception ex)
            {
                active = false; top = bottom = left = right = 0;
                say($"Could not read the annotation crop on '{v.Name}' — its placement may be slightly off.", "warn");
                DiagnosticsLog.Swallowed($"ZonePlacementService: read annotation crop on view {v.Id}", ex);
            }
        }

        private static string GroupLabel(ZoneSheetGroup g)
            => string.IsNullOrEmpty(g?.Suffix) ? (g?.Id ?? "") : g.Suffix;
    }
}
