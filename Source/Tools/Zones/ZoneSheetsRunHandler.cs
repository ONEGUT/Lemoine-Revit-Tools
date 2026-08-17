using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Zones;

namespace LemoineTools.Tools.Zones
{
    // =========================================================================
    // ZoneSheetsRunHandler — the end of the zone chain.
    //
    //   per (level × layout × group) → one sheet
    //   per area in that group       → one viewport at its STORED placement
    //
    // A single-area group is an ordinary sheet; a multi-area group is the
    // composite: several views of one level, on one sheet, at one shared scale,
    // each landing exactly where its placement says.
    //
    // Three things this deliberately does NOT do:
    //
    //   • It never calls doc.Regenerate(). Placement comes from the stored
    //     anchor pair, not from measuring a placed viewport, and SetBoxCenter
    //     needs no regeneration — the commit recomputes once. Regenerating per
    //     sheet is the dominant cost of every other bulk sheet tool here, and
    //     the stored-placement design removes the need entirely.
    //   • It never creates views. Views come from Create Views from Zones, and
    //     are located BY NAME through the shared resolver. A missing view is
    //     reported, not silently invented with different settings.
    //   • It never moves a viewport already on a sheet. A view can be placed on
    //     exactly one sheet, so an already-placed view is reported and skipped.
    //
    // Sheet numbers are unique in Revit and the setter THROWS, so they are
    // pre-checked against the document and against earlier sheets in the same
    // batch. Sheet NAMES are not unique and need no check.
    // =========================================================================
    public sealed class ZoneSheetsRunHandler : IExternalEventHandler
    {
        // ── Inputs, set before Raise() ────────────────────────────────────────
        public List<string> LevelIds  { get; set; } = new List<string>();
        public List<string> LayoutIds { get; set; } = new List<string>();
        /// <summary>
        /// Which of each level's views to place, BY NAME — a view def belongs to a level, so
        /// its id is not shared across levels.
        /// </summary>
        public List<string> ViewNames { get; set; } = new List<string>();

        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.Zones.ZoneSheetsRunHandler";

        private void Log(string t, string tone)
        {
            try { PushLog?.Invoke(t, tone); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("ZoneSheetsRunHandler: log", ex); }
        }

        public void Execute(UIApplication app)
        {
            int pass = 0, fail = 0, skip = 0;
            try
            {
                var doc = app?.ActiveUIDocument?.Document;
                if (doc == null) { Log("No active document.", "fail"); fail++; return; }

                var lib = ZoneSettings.Instance.Library;
                RevitFailureCapture.BeginRun();

                // ── Read phase ────────────────────────────────────────────────
                var titleBlocks = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
                foreach (var sym in new FilteredElementCollector(doc)
                             .OfCategory(BuiltInCategory.OST_TitleBlocks)
                             .WhereElementIsElementType().Cast<FamilySymbol>())
                {
                    string n = ZoneTitleBlocks.NameOf(sym);
                    if (!string.IsNullOrEmpty(n) && !titleBlocks.ContainsKey(n)) titleBlocks[n] = sym.Id;
                }

                var viewsByName  = new Dictionary<string, View>(StringComparer.OrdinalIgnoreCase);
                var usedNumbers  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
                {
                    if (v.IsTemplate) continue;
                    if (v is ViewSheet sh) { usedNumbers.Add(sh.SheetNumber ?? ""); continue; }
                    if (!viewsByName.ContainsKey(v.Name)) viewsByName[v.Name] = v;
                }

                // ── Plan phase ────────────────────────────────────────────────
                var planned = new List<PlannedSheet>();
                foreach (string layoutId in LayoutIds ?? new List<string>())
                {
                    var layout = lib.SheetSet(layoutId);
                    if (layout == null) continue;

                    if (!titleBlocks.TryGetValue(layout.TitleBlockTypeName, out var tbId))
                    {
                        Log($"Layout '{layout.Name}': title block '{layout.TitleBlockTypeName}' " +
                            "is not in this document — no sheets were planned for it.", "fail");
                        fail++;
                        continue;
                    }

                    var groups = (layout.Groups ?? new List<ZoneSheetGroup>())
                                 .OrderBy(g => g.SortIndex).ToList();
                    if (groups.Count == 0)
                    {
                        Log($"Layout '{layout.Name}' has no groups — nothing to build for it.", "warn");
                        continue;
                    }

                    foreach (string levelId in LevelIds ?? new List<string>())
                    {
                        var level = lib.Level(levelId);
                        if (level == null) continue;
                        var building = lib.Building(level.BuildingId);

                        foreach (var group in groups)
                        {
                            // Only the areas of this group that actually exist on this level.
                            var areas = (group.AreaIds ?? new List<string>())
                                .Select(id => lib.Area(id))
                                .Where(a => a != null && lib.AreaAppliesTo(a, level))
                                .Select(a => a!)
                                .ToList();
                            if (areas.Count == 0) continue;

                            ZoneNamingTokens.ResolveSheetName(layout, building, level, group,
                                                              out string number, out string name,
                                                              msg => Log(msg, "warn"));

                            planned.Add(new PlannedSheet
                            {
                                Layout = layout, Level = level, Group = group, Building = building,
                                Areas = areas, TitleBlockId = tbId,
                                Number = number, Name = name,
                                GroupKey = areas.Count > 1 ? group.Id : "",
                            });
                        }
                    }
                }

                if (planned.Count == 0)
                {
                    Log("Nothing to build — no level, sheet size and group combination produced a sheet.", "warn");
                    return;
                }

                // ── Sheet-number uniqueness, before anything is created ────────
                var batch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var keep  = new List<PlannedSheet>();
                foreach (var p in planned)
                {
                    if (usedNumbers.Contains(p.Number))
                    {
                        Log($"Sheet number '{p.Number}' already exists — that sheet was skipped.", "warn");
                        skip++;
                        continue;
                    }
                    if (!batch.Add(p.Number))
                    {
                        Log($"Two planned sheets both want number '{p.Number}' — the second was skipped. " +
                            "Add {SheetSuffix} or {SheetSize} to the layout's number pattern.", "warn");
                        skip++;
                        continue;
                    }
                    keep.Add(p);
                }

                if (keep.Count == 0)
                {
                    Log("Every planned sheet collided with an existing or duplicate number — nothing was created.", "fail");
                    fail++;
                    return;
                }

                // ── Write phase — one transaction, NO regeneration ─────────────
                using (var tx = new Transaction(doc, "Lemoine — Build Sheets from Zones"))
                {
                    var opts = tx.GetFailureHandlingOptions();
                    opts.SetClearAfterRollback(true);
                    opts.SetDelayedMiniWarnings(true);
                    tx.SetFailureHandlingOptions(opts);
                    tx.Start();

                    var progress = new RunProgressReporter((m, t) => Log(m, t), keep.Count, "sheets");

                    for (int i = 0; i < keep.Count; i++)
                    {
                        if (RunState.CancelRequested)
                        {
                            Log(AppStrings.T("common.log.stoppedByUser", i, keep.Count), "warn");
                            break;   // fall through to commit — work so far is preserved
                        }

                        var p = keep[i];
                        int placed = 0, missing = 0;

                        try
                        {
                            var sheet = ViewSheet.Create(doc, p.TitleBlockId);
                            if (sheet == null)
                            {
                                Log($"'{p.Number}': Revit refused to create the sheet.", "fail");
                                fail++;
                                progress.Tick();
                                OnProgress?.Invoke(progress.Percent, pass, fail, skip);
                                continue;
                            }

                            // Number first: it is the unique one, so a refusal here means the
                            // sheet should not keep a Revit-assigned default.
                            try { sheet.SheetNumber = p.Number; }
                            catch (Exception ex)
                            {
                                Log($"Revit refused sheet number '{p.Number}' — the sheet kept its default.", "warn");
                                DiagnosticsLog.Swallowed($"ZoneSheets: set number '{p.Number}'", ex);
                            }
                            try { sheet.Name = p.Name; }
                            catch (Exception ex)
                            {
                                // Sheet names are NOT unique, so a failure here is unusual.
                                Log($"'{p.Number}': the sheet name could not be set.", "warn");
                                DiagnosticsLog.Swallowed($"ZoneSheets: set name on '{p.Number}'", ex);
                            }

                            foreach (var area in p.Areas)
                            // The level owns the view defs and the area may override fields;
                            // ResolveViewDefs combines them exactly as the view run did, so the
                            // names looked up here match the names that were created.
                            foreach (var viewDef in lib.ResolveViewDefs(p.Level, area))
                            {
                                if (ViewNames != null && ViewNames.Count > 0 &&
                                    !ViewNames.Contains(viewDef.Name ?? "", StringComparer.OrdinalIgnoreCase))
                                    continue;

                                string viewName = ZoneNamingTokens.ResolveViewName(
                                    viewDef, p.Building, p.Level, area, p.Layout, p.Group,
                                    msg => Log(msg, "warn"));

                                if (!viewsByName.TryGetValue(viewName, out var view))
                                {
                                    Log($"'{p.Number}': no view named '{viewName}' — create the views first.", "warn");
                                    missing++;
                                    continue;
                                }

                                if (!PlaceOne(doc, sheet, view, lib, area, p))
                                    missing++;
                                else placed++;
                            }

                            if (placed > 0)
                            {
                                pass++;
                                Log(missing == 0
                                        ? $"Sheet {p.Number} — {p.Name}: {placed} view(s) placed."
                                        : $"Sheet {p.Number} — {p.Name}: {placed} placed, {missing} not.",
                                    missing == 0 ? "pass" : "warn");
                            }
                            else
                            {
                                // An empty sheet is worse than no sheet — say so loudly.
                                fail++;
                                Log($"Sheet {p.Number} — {p.Name}: created but EMPTY, no view could be placed.", "fail");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"'{p.Number}': {ex.Message}", "fail");
                            DiagnosticsLog.Error($"ZoneSheets: build '{p.Number}'", ex);
                            fail++;
                        }

                        progress.Tick();
                        OnProgress?.Invoke(progress.Percent, pass, fail, skip);
                    }

                    tx.Commit();
                }

                Log($"Built {pass} sheet(s), {skip} skipped, {fail} failed.", fail > 0 ? "warn" : "pass");
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneSheetsRunHandler: run", ex);
                Log($"Run failed: {ex.Message}", "fail");
                fail++;
            }
            finally
            {
                try { OnComplete?.Invoke(pass, fail, skip); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("ZoneSheetsRunHandler: OnComplete", ex); }

                LevelIds  = new List<string>();
                LayoutIds = new List<string>();
                ViewNames = new List<string>();
            }
        }

        /// <summary>
        /// Places one view on one sheet at its stored placement.
        ///
        /// The placement's OWN world anchor is used — never one recomputed from the area's
        /// current extents. Refreshing half the pair is what silently moves drawings.
        /// </summary>
        private bool PlaceOne(Document doc, ViewSheet sheet, View view, ZoneLibrary lib,
                              ZoneArea area, PlannedSheet p)
        {
            try
            {
                // A view lives on exactly one sheet. Placing an already-placed view is not a
                // recoverable error, so it is reported rather than attempted.
                if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                {
                    var status = view.GetPlacementOnSheetStatus();
                    Log($"'{p.Number}': '{view.Name}' cannot be added " +
                        (status == ViewPlacementOnSheetStatus.CompletelyPlaced
                            ? "— it is already on another sheet."
                            : $"({status})."), "warn");
                    return false;
                }

                var vp = Viewport.Create(doc, sheet.Id, view.Id, XYZ.Zero);
                if (vp == null)
                {
                    Log($"'{p.Number}': Revit refused a viewport for '{view.Name}'.", "warn");
                    return false;
                }

                var placement = lib.Placement(area.Id, p.Layout.TitleBlockTypeName, p.GroupKey);
                if (placement == null)
                {
                    // Centred by Revit's default. Usable, but not where the zone says — and the
                    // difference is invisible on screen, so it is stated.
                    Log($"'{p.Number}': '{area.Name}' has no stored placement for this sheet size — " +
                        "the view was left where Revit put it. Solve placements in the Zone Manager.", "warn");
                    return true;
                }

                XYZ? centre = ZonePlacementService.BoxCentreFor(doc, placement, vp, Log);
                if (centre == null) return true;   // reported inside; the viewport still exists

                // SetBoxCenter is absolute and needs no regeneration — the commit recomputes
                // once. This is why the whole run costs a single model recompute.
                vp.SetBoxCenter(centre);
                return true;
            }
            catch (Exception ex)
            {
                Log($"'{p.Number}': could not place '{view.Name}' — {ex.Message}", "warn");
                DiagnosticsLog.Error($"ZoneSheets: place '{view.Name}' on '{p.Number}'", ex);
                return false;
            }
        }

        private sealed class PlannedSheet
        {
            public ZoneSheetSet Layout   = null!;
            public ZoneLevel       Level    = null!;
            public ZoneSheetGroup  Group    = null!;
            public ZoneBuilding?   Building;
            public List<ZoneArea>  Areas    = new List<ZoneArea>();
            public ElementId       TitleBlockId = ElementId.InvalidElementId;
            public string          Number   = "";
            public string          Name     = "";
            /// <summary>"" for a solo area, the group id for a composite — the placement key.</summary>
            public string          GroupKey = "";
        }
    }
}
