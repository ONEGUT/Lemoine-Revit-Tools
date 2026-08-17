using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Naming;
using LemoineTools.Framework.Zones;

namespace LemoineTools.Tools.Zones
{
    // =========================================================================
    // ZoneViewsRunHandler — creates views from zones.
    //
    // This is the first thing in the zone chain that produces output. For each
    // (cell × recipe) it creates one view carrying everything the zone knows:
    // family type, template, scale, view range, crop and name.
    //
    // Ordering that matters, all of it recorded rather than rediscovered:
    //
    //   • ViewTemplateId is assigned BEFORE any geometry. A template can reset
    //     view geometry, so setting it first lets the crop/section box override
    //     it rather than be overwritten by it.
    //   • A template whose ViewType differs from the view's THROWS, so the
    //     assignment is guarded and skipped-and-logged rather than aborting.
    //   • Names are checked for collisions BEFORE anything is created. View.Name
    //     is unique in Revit and its setter throws, so a duplicate discovered
    //     mid-run leaves a half-built set behind. The run refuses to start.
    //
    // Scale resolution, in order: the recipe's fixed scale; else the scale
    // already solved into the chosen layout's placement for that area; else the
    // recipe default, reported.
    // =========================================================================
    public sealed class ZoneViewsRunHandler : IExternalEventHandler
    {
        /// <summary>One (level, area) pair to build views for.</summary>
        public sealed class CellRef
        {
            public string LevelId { get; set; } = "";
            public string AreaId  { get; set; } = "";
        }

        // ── Inputs, set before Raise() ────────────────────────────────────────
        public List<CellRef> Cells     { get; set; } = new List<CellRef>();
        public List<string>  RecipeIds { get; set; } = new List<string>();
        /// <summary>Optional: the sheet size whose solved scale/placement should be used.</summary>
        public string LayoutId { get; set; } = "";

        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.Zones.ZoneViewsRunHandler";

        private void Log(string t, string tone)
        {
            try { PushLog?.Invoke(t, tone); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("ZoneViewsRunHandler: log", ex); }
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

                // ── Read phase: resolve everything before any write ────────────
                var levelsByName = new Dictionary<string, Level>(StringComparer.OrdinalIgnoreCase);
                foreach (var l in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
                    if (!levelsByName.ContainsKey(l.Name)) levelsByName[l.Name] = l;

                var boxesByName = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
                var boxBounds   = new Dictionary<string, (XYZ Min, XYZ Max)>(StringComparer.OrdinalIgnoreCase);
                foreach (var b in ZoneScopeBoxSync.CollectBoxes(doc))
                {
                    if (string.IsNullOrEmpty(b.Name) || boxesByName.ContainsKey(b.Name)) continue;
                    boxesByName[b.Name] = b.Id;
                    if (b.HasBounds)
                        boxBounds[b.Name] = (new XYZ(b.MinX, b.MinY, b.MinZ), new XYZ(b.MaxX, b.MaxY, b.MaxZ));
                }

                var vftByKey = new Dictionary<string, ViewFamilyType>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>())
                {
                    string key = t.Name ?? "";
                    if (!string.IsNullOrEmpty(key) && !vftByKey.ContainsKey(key)) vftByKey[key] = t;
                }

                var templatesByName = new Dictionary<string, View>(StringComparer.OrdinalIgnoreCase);
                var existingNames   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
                {
                    if (v.IsTemplate)
                    {
                        if (!templatesByName.ContainsKey(v.Name)) templatesByName[v.Name] = v;
                    }
                    else existingNames.Add(v.Name);
                }

                var layout = lib.Layout(LayoutId);

                // ── Plan phase: build every intended view, off-model ───────────
                var planned = new List<Planned>();
                foreach (var cell in Cells ?? new List<CellRef>())
                {
                    var level = lib.Level(cell.LevelId);
                    var area  = lib.Area(cell.AreaId);
                    if (level == null || area == null)
                    {
                        Log("A selected zone no longer exists in the library and was skipped.", "warn");
                        skip++;
                        continue;
                    }
                    var building = lib.Building(area.BuildingId) ?? lib.Building(level.BuildingId);

                    foreach (var rid in RecipeIds ?? new List<string>())
                    {
                        var recipe = lib.Recipe(rid);
                        if (recipe == null) continue;

                        var group   = layout != null ? lib.GroupFor(layout, area.Id) : null;
                        string gkey = (group != null && group.AreaIds.Count > 1) ? group.Id : "";

                        planned.Add(new Planned
                        {
                            Level = level, Area = area, Recipe = recipe, Building = building,
                            Name  = ResolveName(recipe, building, level, area, layout, group),
                            Scale = ResolveScale(lib, recipe, area, layout, gkey),
                        });
                    }
                }

                if (planned.Count == 0)
                {
                    Log("Nothing to create — no zone and recipe combination was selected.", "warn");
                    return;
                }

                // ── Name collision pre-check — refuse rather than half-build ───
                var collisions = ZoneNamingTokens.FindCollisions(planned.Select(p => new ZoneNamingTokens.PlannedName
                {
                    Name = p.Name, AreaId = p.Area.Id, LevelId = p.Level.Id, RecipeId = p.Recipe.Id,
                }));

                foreach (var group in collisions)
                    Log(AppStrings.T("zones.log.nameCollision", group[0].Name, group.Count), "fail");

                var clashWithExisting = planned
                    .Where(p => existingNames.Contains(p.Name))
                    .Select(p => p.Name).Distinct().ToList();
                foreach (var n in clashWithExisting)
                    Log($"A view named '{n}' already exists — it would be skipped.", "warn");

                if (collisions.Count > 0)
                {
                    Log(AppStrings.T("zones.log.nameCollisionAbort", collisions.Count), "fail");
                    fail++;
                    return;
                }

                // ── Write phase ───────────────────────────────────────────────
                using (var tx = new Transaction(doc, "Lemoine — Create Views from Zones"))
                {
                    var opts = tx.GetFailureHandlingOptions();
                    opts.SetClearAfterRollback(true);
                    opts.SetDelayedMiniWarnings(true);
                    tx.SetFailureHandlingOptions(opts);
                    tx.Start();

                    var progress = new RunProgressReporter((msg, tone) => Log(msg, tone), planned.Count, "views");

                    for (int i = 0; i < planned.Count; i++)
                    {
                        if (RunState.CancelRequested)
                        {
                            Log(AppStrings.T("common.log.stoppedByUser", i, planned.Count), "warn");
                            break;   // fall through to commit — work so far is preserved
                        }

                        var p = planned[i];
                        if (existingNames.Contains(p.Name))
                        {
                            skip++;
                            progress.Tick();
                            OnProgress?.Invoke(progress.Percent, pass, fail, skip);
                            continue;
                        }

                        try
                        {
                            View? created = CreateOne(doc, p, levelsByName, boxesByName, boxBounds,
                                                      vftByKey, templatesByName);
                            if (created == null) { fail++; }
                            else { existingNames.Add(p.Name); pass++; }
                        }
                        catch (Exception ex)
                        {
                            Log($"'{p.Name}': {ex.Message}", "fail");
                            DiagnosticsLog.Error($"ZoneViews: create '{p.Name}'", ex);
                            fail++;
                        }

                        progress.Tick();
                        OnProgress?.Invoke(progress.Percent, pass, fail, skip);
                    }

                    tx.Commit();
                }

                Log($"Created {pass} view(s), {skip} skipped, {fail} failed.", fail > 0 ? "warn" : "pass");
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneViewsRunHandler: run", ex);
                Log($"Run failed: {ex.Message}", "fail");
                fail++;
            }
            finally
            {
                try { OnComplete?.Invoke(pass, fail, skip); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("ZoneViewsRunHandler: OnComplete", ex); }

                // Static handler — clear the payload or it outlives the run.
                Cells     = new List<CellRef>();
                RecipeIds = new List<string>();
                LayoutId  = "";
            }
        }

        private sealed class Planned
        {
            public ZoneLevel      Level    = null!;
            public ZoneArea       Area     = null!;
            public ZoneViewRecipe Recipe   = null!;
            public ZoneBuilding?  Building;
            public string         Name     = "";
            public int            Scale    = 96;
        }

        private View? CreateOne(Document doc, Planned p,
                                Dictionary<string, Level> levelsByName,
                                Dictionary<string, ElementId> boxesByName,
                                Dictionary<string, (XYZ Min, XYZ Max)> boxBounds,
                                Dictionary<string, ViewFamilyType> vftByKey,
                                Dictionary<string, View> templatesByName)
        {
            // ── Resolve the view family type ──────────────────────────────────
            ViewFamilyType? vft = null;
            if (!string.IsNullOrEmpty(p.Recipe.ViewFamilyTypeName))
                vftByKey.TryGetValue(p.Recipe.ViewFamilyTypeName, out vft);

            ViewFamily wanted = FamilyFor(p.Recipe.Kind);
            if (vft == null)
                vft = vftByKey.Values.FirstOrDefault(t => t.ViewFamily == wanted);

            if (vft == null)
            {
                Log($"'{p.Name}': no view family type available for {p.Recipe.Kind}.", "fail");
                return null;
            }
            if (vft.ViewFamily != wanted)
            {
                Log($"'{p.Name}': view family type '{p.Recipe.ViewFamilyTypeName}' is not a {p.Recipe.Kind} type.", "fail");
                return null;
            }

            View view;
            string boxName = ZoneSettings.Instance.Library.ScopeBoxFor(p.Area, p.Level);

            if (p.Recipe.Kind == ZoneViewKind.ThreeD)
            {
                var v3 = View3D.CreateIsometric(doc, vft.Id);
                if (v3 == null) { Log($"'{p.Name}': Revit refused to create the 3D view.", "fail"); return null; }
                view = v3;
                SetName(view, p.Name);
                ApplyTemplate(view, p.Recipe, templatesByName);   // template BEFORE geometry

                // A 3D view cannot carry the Scope Box parameter, so the area's XY extents and
                // the level's band become a section box instead.
                if (p.Recipe.SectionBoxFromBand && p.Area.HasExtents)
                {
                    var level = ResolveLevel(p, levelsByName);
                    double baseZ = (level?.Elevation ?? 0) + p.Level.BandBaseOffsetFt;
                    double topZ  = (level?.Elevation ?? 0) + p.Level.BandTopOffsetFt;
                    if (topZ <= baseZ) topZ = baseZ + 1.0;
                    try
                    {
                        v3.SetSectionBox(new BoundingBoxXYZ
                        {
                            Min = new XYZ(p.Area.MinX, p.Area.MinY, baseZ),
                            Max = new XYZ(p.Area.MaxX, p.Area.MaxY, topZ),
                        });
                    }
                    catch (Exception ex)
                    {
                        Log($"'{p.Name}': the section box was refused — the view is uncropped.", "warn");
                        DiagnosticsLog.Swallowed($"ZoneViews: section box on '{p.Name}'", ex);
                    }
                }
            }
            else
            {
                var level = ResolveLevel(p, levelsByName);
                if (level == null)
                {
                    Log($"'{p.Name}': host level '{p.Level.HostLevelName}' does not exist in this document.", "fail");
                    return null;
                }

                var plan = ViewPlan.Create(doc, vft.Id, level.Id);
                view = plan;
                SetName(view, p.Name);
                ApplyTemplate(view, p.Recipe, templatesByName);   // template BEFORE geometry

                // Scope box drives the crop, and it is live — the view follows the box.
                if (!string.IsNullOrEmpty(boxName) && boxesByName.TryGetValue(boxName, out var boxId))
                {
                    try
                    {
                        var prm = plan.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                        if (prm == null || prm.IsReadOnly)
                            Log($"'{p.Name}': the Scope Box parameter is unavailable (a template may govern it) — the view is uncropped.", "warn");
                        else prm.Set(boxId);
                    }
                    catch (Exception ex)
                    {
                        Log($"'{p.Name}': the scope box could not be assigned — the view is uncropped.", "warn");
                        DiagnosticsLog.Swallowed($"ZoneViews: scope box on '{p.Name}'", ex);
                    }
                }
                else if (!string.IsNullOrEmpty(boxName))
                {
                    Log($"'{p.Name}': scope box '{boxName}' is not in this document — the view is uncropped.", "warn");
                }

                // View range — the "RCP and floor plans ready to use" half of a zone.
                if (ZoneViewKind.IsPlan(p.Recipe.Kind) && p.Recipe.ViewRange != null)
                    ZoneViewRangeApplier.Apply(plan, p.Recipe.ViewRange, doc, Log);
            }

            // ── Scale ─────────────────────────────────────────────────────────
            try
            {
                if (p.Scale > 0) view.Scale = p.Scale;
            }
            catch (Exception ex)
            {
                Log($"'{p.Name}': the scale could not be set (a template may govern it).", "warn");
                DiagnosticsLog.Swallowed($"ZoneViews: scale on '{p.Name}'", ex);
            }

            ApplyDisciplineAndDetail(view, p.Recipe);
            Log($"Created '{p.Name}' at {ZoneScaleFit.Label(p.Scale)}.", "pass");
            return view;
        }

        private static Level? ResolveLevel(Planned p, Dictionary<string, Level> byName)
        {
            string key = !string.IsNullOrEmpty(p.Level.HostLevelName) ? p.Level.HostLevelName : p.Level.Name;
            return byName.TryGetValue(key, out var l) ? l : null;
        }

        private void SetName(View v, string name)
        {
            // Uniqueness was pre-checked, but a transient clash inside one run still throws —
            // report it rather than losing the view to an unhandled exception.
            try { v.Name = name; }
            catch (Exception ex)
            {
                Log($"Revit refused the view name '{name}' — it kept its default name.", "warn");
                DiagnosticsLog.Swallowed($"ZoneViews: name view '{name}'", ex);
            }
        }

        private void ApplyTemplate(View view, ZoneViewRecipe recipe, Dictionary<string, View> templates)
        {
            if (string.IsNullOrEmpty(recipe.ViewTemplateName)) return;
            if (!templates.TryGetValue(recipe.ViewTemplateName, out var tpl))
            {
                Log($"'{view.Name}': view template '{recipe.ViewTemplateName}' is not in this document.", "warn");
                return;
            }

            // Assigning a template whose ViewType differs from the view's THROWS — it does not
            // silently no-op — so this is guarded and skipped rather than allowed to abort.
            try { view.ViewTemplateId = tpl.Id; }
            catch (Exception ex)
            {
                Log($"'{view.Name}': view template '{recipe.ViewTemplateName}' does not apply to this view type.", "warn");
                DiagnosticsLog.Swallowed($"ZoneViews: template on '{view.Name}'", ex);
            }
        }

        private void ApplyDisciplineAndDetail(View view, ZoneViewRecipe recipe)
        {
            if (!string.IsNullOrEmpty(recipe.Discipline) &&
                Enum.TryParse<ViewDiscipline>(recipe.Discipline, true, out var disc))
            {
                try { view.Discipline = disc; }
                catch (Exception ex) { DiagnosticsLog.Swallowed($"ZoneViews: discipline on '{view.Name}'", ex); }
            }

            if (!string.IsNullOrEmpty(recipe.DetailLevel) &&
                Enum.TryParse<ViewDetailLevel>(recipe.DetailLevel, true, out var det))
            {
                try { view.DetailLevel = det; }
                catch (Exception ex) { DiagnosticsLog.Swallowed($"ZoneViews: detail level on '{view.Name}'", ex); }
            }
        }

        private static ViewFamily FamilyFor(string kind)
        {
            switch (kind)
            {
                case ZoneViewKind.CeilingPlan: return ViewFamily.CeilingPlan;
                case ZoneViewKind.ThreeD:      return ViewFamily.ThreeDimensional;
                case ZoneViewKind.Section:     return ViewFamily.Section;
                case ZoneViewKind.AreaPlan:    return ViewFamily.AreaPlan;
                default:                       return ViewFamily.FloorPlan;
            }
        }

        /// <summary>
        /// Scale, in order: the recipe's fixed value; the scale already solved into the chosen
        /// layout's placement for this area; then the recipe default, with a note.
        /// </summary>
        private int ResolveScale(ZoneLibrary lib, ZoneViewRecipe recipe, ZoneArea area,
                                 ZoneSheetLayout? layout, string groupKey)
        {
            if (recipe.ScaleMode == ZoneScaleMode.Fixed && recipe.Scale > 0) return recipe.Scale;

            if (layout != null)
            {
                var pl = lib.Placement(area.Id, layout.TitleBlockTypeName, groupKey);
                if (pl != null && pl.Scale > 0) return pl.Scale;
            }

            return recipe.Scale > 0 ? recipe.Scale : 96;
        }

        private string ResolveName(ZoneViewRecipe recipe, ZoneBuilding? building, ZoneLevel level,
                                   ZoneArea area, ZoneSheetLayout? layout, ZoneSheetGroup? group)
        {
            var ctx = new TokenContext();
            foreach (var kv in ZoneNamingTokens.ValuesFor(
                         ZoneSettings.Instance.Library, building, level, area, recipe, layout, group))
                ctx.Computed[kv.Key] = kv.Value;

            string pattern  = string.IsNullOrWhiteSpace(recipe.NamePattern)
                ? "{Level} - {Area} - {Recipe}"
                : recipe.NamePattern;

            string resolved = TokenResolver.Resolve(pattern, ctx, msg => Log(msg, "warn"));
            string fallback = $"{level.Name} - {area.Name} - {recipe.Name}";
            return TokenResolver.GuardDegenerate(resolved, ctx, fallback, msg => Log(msg, "warn"));
        }
    }
}
