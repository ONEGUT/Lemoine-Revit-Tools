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
    // ZoneKeyPlanRunHandler — one key plan legend per area.
    //
    // A key plan is the locator diagram on a sheet: the building outline with
    // THIS sheet's area highlighted.
    //
    // WHY A LEGEND, AND WHY A SEED:
    //
    //   Legends are the only Revit view type placeable on MORE THAN ONE SHEET.
    //   A drafting view, like any other view, can sit on exactly one — the same
    //   constraint the whole zone chain works around. So one legend per AREA
    //   serves every sheet that documents that area.
    //
    //   But there is no legend-creation API at all: `ViewLegend` does not exist
    //   as a type, and nothing on Document.Create makes one. A legend can only
    //   be produced by DUPLICATING an existing legend. So this tool needs a seed
    //   legend the user picks — exactly the same shape as the Scope Box Creator,
    //   which duplicates a seed scope box for the same reason.
    //
    // THE COMPOSITION, which is what keeps this simple:
    //
    //   outline   → ONE continuous building boundary per connected mass, from
    //               ZoneSlabOutline's boolean union of the slab footprints —
    //               holes filled, shared edges between abutting slabs gone, and
    //               a sloped slab's extra faces merged. Drawn as DETAIL LINES,
    //               so no closed-loop element and no curve-joining is needed.
    //   highlight → a FilledRegion from the area's own extents rectangle, which
    //               is already closed and already known exactly.
    //
    //   The precise half comes from data the library owns; the hard half never
    //   has to be solved.
    //
    // UNVERIFIED, needs a Windows/Revit run: how a legend's own coordinate space
    // and scale behave when model-sized geometry is drawn into it. Geometry is
    // translated so the building centre lands on the legend origin, which should
    // be right, but it is reasoned rather than observed.
    // =========================================================================
    public sealed class ZoneKeyPlanRunHandler : IExternalEventHandler
    {
        // ── Inputs, set before Raise() ────────────────────────────────────────
        /// <summary>The legend duplicated for every key plan. Required — legends cannot be created.</summary>
        public ElementId SeedLegendId { get; set; } = ElementId.InvalidElementId;
        /// <summary>Link instance to read slabs from; invalid = the host document.</summary>
        public ElementId SourceLinkId { get; set; } = ElementId.InvalidElementId;
        /// <summary>Zone level whose slab supplies the outline.</summary>
        public string OutlineLevelId { get; set; } = "";
        /// <summary>Areas to make a key plan for.</summary>
        public List<string> AreaIds { get; set; } = new List<string>();
        /// <summary>Filled region type for the highlight; invalid = first available.</summary>
        public ElementId FillTypeId { get; set; } = ElementId.InvalidElementId;

        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.Zones.ZoneKeyPlanRunHandler";

        private void Log(string t, string tone)
        {
            try { PushLog?.Invoke(t, tone); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("ZoneKeyPlanRunHandler: log", ex); }
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

                if (!(doc.GetElement(SeedLegendId) is View seed) || seed.ViewType != ViewType.Legend)
                {
                    Log("Pick a seed legend first. Revit has no API to create a legend, so each " +
                        "key plan is a duplicate of one that already exists.", "fail");
                    fail++;
                    return;
                }

                var level = lib.Level(OutlineLevelId);
                if (level == null) { Log("The outline level no longer exists in the library.", "fail"); fail++; return; }

                // ── Outline, once, shared by every key plan in this run ────────
                Document srcDoc = doc;
                Transform tf = Transform.Identity;
                if (SourceLinkId != ElementId.InvalidElementId &&
                    doc.GetElement(SourceLinkId) is RevitLinkInstance li)
                {
                    var ld = li.GetLinkDocument();
                    if (ld == null)
                    {
                        Log("The selected link could not be read — the outline falls back to the host document.", "warn");
                    }
                    else { srcDoc = ld; tf = li.GetTotalTransform(); }
                }

                string levelName = !string.IsNullOrEmpty(level.HostLevelName) ? level.HostLevelName : level.Name;
                var outline = ZoneSlabOutline.Collect(srcDoc, tf, levelName, Log);

                if (!outline.Ok)
                {
                    // Fall back to the zone extents themselves — a block diagram is a legitimate
                    // key plan, but the user is told that is what they are getting.
                    Log("No slab outline was found — the key plans will show the zone extents as " +
                        "rectangles instead of the building footprint.", "warn");
                }
                else
                {
                    Log($"Outline read from {outline.From} — {outline.Rings.Count} continuous " +
                        $"boundary(ies), {outline.WidthFt:0.#}' × {outline.DepthFt:0.#}'. " +
                        "Openings are filled and internal slab edges removed.", "info");
                }

                // Centre of the drawing: the outline when there is one, else every area's union.
                double cx = outline.Ok ? outline.CentreX : 0, cy = outline.Ok ? outline.CentreY : 0;
                if (!outline.Ok)
                {
                    var areas = AreaIds.Select(id => lib.Area(id)).Where(a => a != null && a.HasExtents).ToList();
                    if (areas.Count > 0)
                    {
                        cx = (areas.Min(a => a!.MinX) + areas.Max(a => a!.MaxX)) / 2.0;
                        cy = (areas.Min(a => a!.MinY) + areas.Max(a => a!.MaxY)) / 2.0;
                    }
                }

                ElementId fillId = ResolveFillType(doc);
                if (fillId == ElementId.InvalidElementId)
                {
                    Log("No filled region type is available — the highlighted area cannot be drawn.", "warn");
                }

                var existingNames = new HashSet<string>(
                    new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                        .Where(v => !v.IsTemplate).Select(v => v.Name),
                    StringComparer.OrdinalIgnoreCase);

                using (var tx = new Transaction(doc, "Lemoine — Key Plans from Zones"))
                {
                    var opts = tx.GetFailureHandlingOptions();
                    opts.SetClearAfterRollback(true);
                    opts.SetDelayedMiniWarnings(true);
                    tx.SetFailureHandlingOptions(opts);
                    tx.Start();

                    var progress = new RunProgressReporter((m, t) => Log(m, t), AreaIds.Count, "key plans");

                    for (int i = 0; i < AreaIds.Count; i++)
                    {
                        if (RunState.CancelRequested)
                        {
                            Log(AppStrings.T("common.log.stoppedByUser", i, AreaIds.Count), "warn");
                            break;
                        }

                        var area = lib.Area(AreaIds[i]);
                        if (area == null) { skip++; progress.Tick(); continue; }

                        string name = $"Key Plan - {level.Name} - {area.Name}";
                        if (existingNames.Contains(name))
                        {
                            Log($"'{name}' already exists — skipped.", "info");
                            skip++;
                            progress.Tick();
                            OnProgress?.Invoke(progress.Percent, pass, fail, skip);
                            continue;
                        }

                        try
                        {
                            if (BuildOne(doc, seed, name, area, outline, cx, cy, fillId))
                            {
                                existingNames.Add(name);
                                pass++;
                            }
                            else fail++;
                        }
                        catch (Exception ex)
                        {
                            Log($"'{name}': {ex.Message}", "fail");
                            DiagnosticsLog.Error($"ZoneKeyPlan: build '{name}'", ex);
                            fail++;
                        }

                        progress.Tick();
                        OnProgress?.Invoke(progress.Percent, pass, fail, skip);
                    }

                    tx.Commit();
                }

                Log($"Created {pass} key plan(s), {skip} skipped, {fail} failed.", fail > 0 ? "warn" : "pass");
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneKeyPlanRunHandler: run", ex);
                Log($"Run failed: {ex.Message}", "fail");
                fail++;
            }
            finally
            {
                try { OnComplete?.Invoke(pass, fail, skip); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("ZoneKeyPlanRunHandler: OnComplete", ex); }

                AreaIds = new List<string>();
            }
        }

        private bool BuildOne(Document doc, View seed, string name, ZoneArea area,
                              ZoneSlabOutline.Result outline, double cx, double cy, ElementId fillId)
        {
            // A legend can only be produced by duplicating one — there is no creation API.
            ElementId dupId;
            try { dupId = seed.Duplicate(ViewDuplicateOption.Duplicate); }
            catch (Exception ex)
            {
                Log($"'{name}': the seed legend could not be duplicated — {ex.Message}", "fail");
                DiagnosticsLog.Error($"ZoneKeyPlan: duplicate seed for '{name}'", ex);
                return false;
            }

            if (!(doc.GetElement(dupId) is View legend))
            {
                Log($"'{name}': the duplicated legend could not be resolved.", "fail");
                return false;
            }

            try { legend.Name = name; }
            catch (Exception ex)
            {
                Log($"'{name}': Revit refused that legend name — it kept its default.", "warn");
                DiagnosticsLog.Swallowed($"ZoneKeyPlan: name legend '{name}'", ex);
            }

            // Geometry is drawn centred on the legend origin: model-sized, translated by the
            // building centre. The legend's own scale then governs the paper size.
            XYZ Shift(double x, double y) => new XYZ(x - cx, y - cy, 0);

            int lines = 0;
            foreach (var ring in outline.Rings)
            {
                for (int i = 0; i < ring.Points.Count; i++)
                {
                    var a = ring.Points[i];
                    var b = ring.Points[(i + 1) % ring.Points.Count];
                    if (a.DistanceTo(b) < 1e-6) continue;   // Revit rejects a zero-length line

                    try
                    {
                        var line = Line.CreateBound(Shift(a.X, a.Y), Shift(b.X, b.Y));
                        doc.Create.NewDetailCurve(legend, line);
                        lines++;
                    }
                    catch (Exception ex)
                    {
                        // One bad segment must not lose the whole outline.
                        DiagnosticsLog.Swallowed($"ZoneKeyPlan: detail line on '{name}'", ex);
                    }
                }
            }

            // The highlight — the half that must be exact, from extents the library owns.
            bool filled = false;
            if (fillId != ElementId.InvalidElementId && area.HasExtents)
            {
                // NOTE: FilledRegion.IsRegionCreationEnabledInView EXISTS on the type but is
                // marked `assembly` (internal) in RevitAPI.dll, so it cannot be called from
                // here. There is no public pre-check, so this attempts the create and reports
                // the refusal — which is the honest shape anyway: a view that rejects filled
                // regions tells us so by refusing.
                try
                {
                    var loop = new CurveLoop();
                    var p1 = Shift(area.MinX, area.MinY);
                    var p2 = Shift(area.MaxX, area.MinY);
                    var p3 = Shift(area.MaxX, area.MaxY);
                    var p4 = Shift(area.MinX, area.MaxY);
                    loop.Append(Line.CreateBound(p1, p2));
                    loop.Append(Line.CreateBound(p2, p3));
                    loop.Append(Line.CreateBound(p3, p4));
                    loop.Append(Line.CreateBound(p4, p1));

                    FilledRegion.Create(doc, fillId, legend.Id, new List<CurveLoop> { loop });
                    filled = true;
                }
                catch (Exception ex)
                {
                    Log($"'{name}': the area highlight could not be drawn — {ex.Message}. " +
                        "The outline is still there; a legend that refuses filled regions needs " +
                        "the highlight drawn by hand.", "warn");
                    DiagnosticsLog.Swallowed($"ZoneKeyPlan: filled region on '{name}'", ex);
                }
            }
            else if (!area.HasExtents)
            {
                Log($"'{name}': '{area.Name}' has no resolved extents, so nothing is highlighted.", "warn");
            }

            // An empty key plan looks like a working one, so a zero result is stated.
            if (lines == 0 && !filled)
            {
                Log($"'{name}': created but EMPTY — no outline and no highlight could be drawn.", "fail");
                return false;
            }

            Log($"Created '{name}' — {lines} outline segment(s)" + (filled ? " and the area highlight." : ", no highlight."),
                filled ? "pass" : "warn");
            return true;
        }

        private ElementId ResolveFillType(Document doc)
        {
            if (FillTypeId != ElementId.InvalidElementId &&
                FilledRegion.IsValidFilledRegionTypeId(doc, FillTypeId))
                return FillTypeId;

            try
            {
                var first = new FilteredElementCollector(doc)
                    .OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>()
                    .OrderBy(t => t.Name, NaturalOrderComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                return first?.Id ?? ElementId.InvalidElementId;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("ZoneKeyPlan: resolve filled region type", ex);
                return ElementId.InvalidElementId;
            }
        }
    }
}
