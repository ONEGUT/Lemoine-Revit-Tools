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
    //   highlight → the area's extents rectangle, TRIMMED to the building
    //               outline (and optionally to the matchlines), so the part of a
    //               scope box hanging off the building is not drawn. A trim can
    //               legitimately split the highlight into SEVERAL pieces — an
    //               area spanning a courtyard is two — and each piece becomes
    //               its own FilledRegion.
    //   matchlines → optional, read from a linked model by category and drawn as
    //               detail lines; the highlight is cut back to the side of each
    //               one the area sits on.
    //
    // FILL GRAPHICS ARE ONE SETTING FOR THE WHOLE RUN. The pattern and colour
    // are written onto a filled region type this tool OWNS (reused by name), so
    // every key plan in every run shares one appearance and no project type is
    // mutated behind the user's back. Re-running with a different colour
    // therefore restyles the key plans made earlier too, which is the point of
    // it being a single override.
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
        /// <summary>
        /// Drafting fill pattern for the highlight, by NAME. Empty means the solid fill.
        /// A name, never an id — the same cross-document discipline the rest of the zone
        /// model follows.
        /// </summary>
        public string FillPatternName { get; set; } = "";

        /// <summary>Highlight colour as "#RRGGBB". Defaults to the house 80,80,80 grey.</summary>
        public string FillColorHex { get; set; } = DefaultFillHex;

        /// <summary>View scale for every legend created, so they fit the sheet they are for.</summary>
        public int LegendScale { get; set; } = 96;

        /// <summary>Trim the highlight back to the building outline.</summary>
        public bool TrimToOutline { get; set; } = true;

        /// <summary>Link instance to read matchlines from; invalid = none, or the host when asked.</summary>
        public ElementId MatchlineLinkId { get; set; } = ElementId.InvalidElementId;
        /// <summary>Read matchlines at all — from the host when no link is chosen.</summary>
        public bool UseMatchlines { get; set; }
        /// <summary>Draw the matchlines on the legend as detail lines.</summary>
        public bool ShowMatchlines { get; set; } = true;
        /// <summary>Cut the highlight back to the matchline side the area sits on.</summary>
        public bool TrimToMatchlines { get; set; } = true;

        /// <summary>The RGB the user asked for as the default: a mid grey that reads under linework.</summary>
        public const string DefaultFillHex = "#505050";   // 80, 80, 80

        /// <summary>Name of the filled region type this tool creates and owns.</summary>
        public const string OwnedFillTypeName = "Lemoine - Key Plan Fill";

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

                // ── Matchlines, once, shared by every key plan in this run ────
                var matchlines = new ZoneMatchlines.Result();
                if (UseMatchlines)
                {
                    Document mlDoc = doc;
                    Transform mlTf = Transform.Identity;
                    if (MatchlineLinkId != ElementId.InvalidElementId &&
                        doc.GetElement(MatchlineLinkId) is RevitLinkInstance mli)
                    {
                        var mld = mli.GetLinkDocument();
                        if (mld == null)
                            Log("The matchline link could not be read — matchlines fall back to the host document.", "warn");
                        else { mlDoc = mld; mlTf = mli.GetTotalTransform(); }
                    }
                    matchlines = ZoneMatchlines.Collect(mlDoc, mlTf, levelName, Log);
                }

                // The fill type is created ONCE per run, inside this transaction, and carries
                // the run's single pattern + colour override.
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

                    // Creating or restyling a type is a write, so it belongs inside the
                    // transaction — ResolveFillType above only found an existing one.
                    fillId = EnsureOwnedFillType(doc, fillId);

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
                            if (BuildOne(doc, seed, name, area, outline, matchlines, cx, cy, fillId))
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
                              ZoneSlabOutline.Result outline, ZoneMatchlines.Result matchlines,
                              double cx, double cy, ElementId fillId)
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

            // The scale is what decides whether the key plan fits the corner of the sheet it
            // is for, so it is set explicitly rather than inherited from whatever the seed was.
            if (LegendScale > 0)
            {
                try { legend.Scale = LegendScale; }
                catch (Exception ex)
                {
                    Log($"'{name}': Revit refused the scale 1:{LegendScale} — the legend kept the seed's scale.", "warn");
                    DiagnosticsLog.Swallowed($"ZoneKeyPlan: set scale on '{name}'", ex);
                }
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

            // Matchlines, drawn before the highlight so the fill reads on top of nothing.
            if (ShowMatchlines && matchlines.Segments.Count > 0)
            {
                foreach (var seg in matchlines.Segments)
                {
                    try
                    {
                        var mline = Line.CreateBound(Shift(seg.A.X, seg.A.Y), Shift(seg.B.X, seg.B.Y));
                        doc.Create.NewDetailCurve(legend, mline);
                        lines++;
                    }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Swallowed($"ZoneKeyPlan: matchline on '{name}'", ex);
                    }
                }
            }

            // ── The highlight ────────────────────────────────────────────────
            // The rectangle is what the library knows exactly; the trimming is what makes it
            // describe the building rather than the scope box. Both can yield SEVERAL pieces,
            // and each piece becomes its own filled region.
            int filledPieces = 0;
            if (fillId != ElementId.InvalidElementId && area.HasExtents)
            {
                var pieces = new List<List<XYZ>>
                {
                    ZonePolygonOps.Rect(area.MinX, area.MinY, area.MaxX, area.MaxY),
                };

                if (TrimToOutline && outline.Ok)
                {
                    pieces = ZonePolygonOps.IntersectWith(
                        pieces[0],
                        outline.Rings.Select(r => (IReadOnlyList<XYZ>)r.Points),
                        (m, t) => Log($"'{name}': {m}", t));
                }
                else if (TrimToOutline && !outline.Ok)
                {
                    Log($"'{name}': there is no building outline to trim the highlight to, " +
                        "so the full scope box rectangle is drawn.", "warn");
                }

                if (TrimToMatchlines && matchlines.Segments.Count > 0 && pieces.Count > 0)
                {
                    // The area's anchor says which side of each matchline to keep. It is the
                    // extents centre unless the area carries a real anchor.
                    var keep = new XYZ(
                        area.HasAnchor ? area.AnchorX : (area.MinX + area.MaxX) / 2.0,
                        area.HasAnchor ? area.AnchorY : (area.MinY + area.MaxY) / 2.0, 0);

                    pieces = ZonePolygonOps.TrimToMatchlineSide(
                        pieces.Select(p => (IReadOnlyList<XYZ>)p).ToList(),
                        matchlines.Segments.Select(sg => (sg.A, sg.B)),
                        keep,
                        (m, t) => Log($"'{name}': {m}", t));
                }

                // NOTE: FilledRegion.IsRegionCreationEnabledInView EXISTS on the type but is
                // marked `assembly` (internal) in RevitAPI.dll, so it cannot be called from
                // here. There is no public pre-check, so this attempts the create and reports
                // the refusal — which is the honest shape anyway: a view that rejects filled
                // regions tells us so by refusing.
                foreach (var piece in pieces)
                {
                    if (piece.Count < 3) continue;
                    try
                    {
                        var loop = new CurveLoop();
                        for (int i = 0; i < piece.Count; i++)
                        {
                            var a = piece[i];
                            var b = piece[(i + 1) % piece.Count];
                            if (a.DistanceTo(b) < ZonePolygonOps.WeldToleranceFt) continue;
                            loop.Append(Line.CreateBound(Shift(a.X, a.Y), Shift(b.X, b.Y)));
                        }

                        FilledRegion.Create(doc, fillId, legend.Id, new List<CurveLoop> { loop });
                        filledPieces++;
                    }
                    catch (Exception ex)
                    {
                        Log($"'{name}': one highlight piece could not be drawn — {ex.Message}.", "warn");
                        DiagnosticsLog.Swallowed($"ZoneKeyPlan: filled region on '{name}'", ex);
                    }
                }

                if (pieces.Count > 0 && filledPieces == 0)
                    Log($"'{name}': the area highlight could not be drawn. The outline is still " +
                        "there; a legend that refuses filled regions needs the highlight drawn by hand.", "warn");
            }
            else if (!area.HasExtents)
            {
                Log($"'{name}': '{area.Name}' has no resolved extents, so nothing is highlighted.", "warn");
            }

            bool filled = filledPieces > 0;

            // An empty key plan looks like a working one, so a zero result is stated.
            if (lines == 0 && !filled)
            {
                Log($"'{name}': created but EMPTY — no outline and no highlight could be drawn.", "fail");
                return false;
            }

            Log($"Created '{name}' — {lines} line segment(s)" +
                (filled
                    ? (filledPieces == 1 ? " and the area highlight." : $" and {filledPieces} highlight pieces.")
                    : ", no highlight."),
                filled ? "pass" : "warn");
            return true;
        }

        /// <summary>
        /// The tool's own filled region type if it already exists, else any type to clone from.
        /// Read-only — the actual create/restyle happens in EnsureOwnedFillType, inside the
        /// transaction.
        /// </summary>
        private ElementId ResolveFillType(Document doc)
        {
            try
            {
                var types = new FilteredElementCollector(doc)
                    .OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().ToList();

                var owned = types.FirstOrDefault(
                    t => string.Equals(t.Name, OwnedFillTypeName, StringComparison.OrdinalIgnoreCase));
                if (owned != null) return owned.Id;

                if (types.Count == 0)
                {
                    // There is no API to create a FilledRegionType from nothing — it can only be
                    // duplicated from one that exists. A project with none cannot be helped here,
                    // and saying so beats a create that fails with no explanation.
                    Log("This project has no filled region type at all, so one cannot be created " +
                        "for the highlight (Revit only allows duplicating an existing type).", "warn");
                    return ElementId.InvalidElementId;
                }

                return types.OrderBy(t => t.Name, NaturalOrderComparer.OrdinalIgnoreCase).First().Id;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("ZoneKeyPlan: resolve filled region type", ex);
                return ElementId.InvalidElementId;
            }
        }

        /// <summary>
        /// Returns the tool-owned filled region type, creating it by duplication the first time
        /// and writing this run's pattern + colour onto it either way.
        ///
        /// A type the TOOL owns, never a project one: restyling a type the project uses
        /// elsewhere would silently change drawings nobody asked about. Reused by name, so the
        /// setting really is a single override across every key plan.
        ///
        /// Must be called inside an open transaction.
        /// </summary>
        private ElementId EnsureOwnedFillType(Document doc, ElementId seedTypeId)
        {
            if (seedTypeId == ElementId.InvalidElementId) return ElementId.InvalidElementId;

            FilledRegionType? type = null;
            try
            {
                type = doc.GetElement(seedTypeId) as FilledRegionType;
                if (type == null) return ElementId.InvalidElementId;

                if (!string.Equals(type.Name, OwnedFillTypeName, StringComparison.OrdinalIgnoreCase))
                {
                    var dup = type.Duplicate(OwnedFillTypeName) as FilledRegionType;
                    if (dup == null)
                    {
                        Log($"'{OwnedFillTypeName}' could not be created; the highlight uses " +
                            $"'{type.Name}' as it is, so its pattern and colour are whatever that type carries.", "warn");
                        return type.Id;
                    }
                    type = dup;
                    Log($"Created the filled region type '{OwnedFillTypeName}' for the highlight.", "info");
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneKeyPlan: create owned filled region type", ex);
                Log($"The highlight's own fill type could not be created ({ex.Message}); " +
                    "an existing type is used untouched.", "warn");
                return seedTypeId;
            }

            ApplyFillGraphics(doc, type);
            return type.Id;
        }

        /// <summary>
        /// Writes the run's single pattern + colour override onto the tool's fill type.
        ///
        /// The FOREGROUND pattern is what a filled region draws with, so that is what carries
        /// the chosen pattern and colour. IsMasking is forced off — a masking region paints
        /// over the outline underneath it, which is the opposite of what a key plan wants.
        /// </summary>
        private void ApplyFillGraphics(Document doc, FilledRegionType type)
        {
            ElementId patternId = ResolveFillPattern(doc);
            var colour = ParseColour(FillColorHex);

            try
            {
                if (patternId != ElementId.InvalidElementId) type.ForegroundPatternId = patternId;
                type.ForegroundPatternColor = colour;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneKeyPlan: apply fill pattern/colour", ex);
                Log($"The highlight's pattern or colour could not be applied ({ex.Message}); " +
                    "the fill type keeps what it had.", "warn");
                return;
            }

            try { type.IsMasking = false; }
            catch (Exception ex) { DiagnosticsLog.Swallowed("ZoneKeyPlan: clear IsMasking", ex); }

            Log($"Highlight fill: {(string.IsNullOrEmpty(FillPatternName) ? "solid" : FillPatternName)} " +
                $"at {FillColorHex}, applied to '{type.Name}' for every key plan in this run.", "info");
        }

        /// <summary>
        /// The chosen DRAFTING fill pattern by name, falling back to the solid fill.
        /// Drafting, not Model: a filled region in a legend is annotation, and a model pattern
        /// would scale with the view rather than the paper.
        /// </summary>
        private ElementId ResolveFillPattern(Document doc)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(FillPatternName))
                {
                    var named = FillPatternElement.GetFillPatternElementByName(
                        doc, FillPatternTarget.Drafting, FillPatternName);
                    if (named != null) return named.Id;

                    Log($"No drafting fill pattern named '{FillPatternName}' is in this project — " +
                        "the highlight falls back to a solid fill.", "warn");
                }

                var solid = new FilteredElementCollector(doc)
                    .OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                    .FirstOrDefault(f =>
                    {
                        try
                        {
                            var fp = f.GetFillPattern();
                            return fp != null && fp.IsSolidFill && fp.Target == FillPatternTarget.Drafting;
                        }
                        catch (Exception ex)
                        {
                            DiagnosticsLog.Swallowed($"ZoneKeyPlan: read fill pattern '{f.Name}'", ex);
                            return false;
                        }
                    });

                if (solid != null) return solid.Id;

                Log("This project has no solid drafting fill pattern, so the highlight keeps " +
                    "whatever pattern the fill type already carried.", "warn");
                return ElementId.InvalidElementId;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneKeyPlan: resolve fill pattern", ex);
                return ElementId.InvalidElementId;
            }
        }

        /// <summary>
        /// "#RRGGBB" to a Revit colour, falling back to the documented 80,80,80 default rather
        /// than to something arbitrary, so a malformed value still produces the intended look.
        /// </summary>
        private static Color ParseColour(string? hex)
        {
            byte r = 80, g = 80, b = 80;
            try
            {
                string h = (hex ?? "").Trim().TrimStart('#');
                if (h.Length == 6)
                {
                    r = Convert.ToByte(h.Substring(0, 2), 16);
                    g = Convert.ToByte(h.Substring(2, 2), 16);
                    b = Convert.ToByte(h.Substring(4, 2), 16);
                }
                else if (!string.IsNullOrEmpty(h))
                {
                    DiagnosticsLog.Warn("ZoneKeyPlan", $"Fill colour '{hex}' is not #RRGGBB; using 80,80,80.");
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"ZoneKeyPlan: parse fill colour '{hex}'", ex);
            }
            return new Color(r, g, b);
        }
    }
}
