using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Tools.AutoFilters;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Testing.CoordSet
{
    /// <summary>
    /// Executes the coordination drawing set workflow on the Revit main thread.
    /// Phases A–G run in separate transactions; phase state is checkpointed so
    /// a partial run can be resumed after a crash.
    /// </summary>
    public sealed class CoordSetRunHandler : IExternalEventHandler
    {
        // ── Inputs set before Raise() ─────────────────────────────────────────
        public CoordSetSettings?       Settings         { get; set; }
        public List<ElementId>         SelectedLevelIds { get; set; } = new List<ElementId>();
        public List<string>            SelectedTradeIds { get; set; } = new List<string>();
        public ElementId?              TitleBlockTypeId { get; set; }
        public CoordSetRunPhase        StartFromPhase   { get; set; } = CoordSetRunPhase.None;

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.Testing.CoordSetRunHandler";

        // ── Discipline mapping ────────────────────────────────────────────────
        private static readonly string[] CoreDisciplines = { "MD", "MP", "PL", "FP", "EL" };
        private const string CoordLabel = "Coordination";
        private const string RcpLabel   = "RCP";
        private const string OtherLabel = "Other";

        public void Execute(UIApplication app)
        {
            var doc  = app.ActiveUIDocument.Document;
            long __issues0 = LemoineLog.IssueCount;
            int pass = 0, fail = 0, skip = 0;
            var s = Settings ?? CoordSetSettings.Instance;

            try
            {
                var startPhase = StartFromPhase;

                // ── Phase A: ensure filters exist ──────────────────────────────
                if (startPhase <= CoordSetRunPhase.None)
                {
                    Log("Phase A — Applying filters…", "info");
                    RunPhaseA(doc, s, ref pass, ref fail, ref skip);
                    s.LastCompletedPhase = CoordSetRunPhase.A;
                    s.Save();
                    Progress(12, pass, fail, skip);
                }

                // ── Phase B: create discipline floor-plan views ────────────────
                if (startPhase <= CoordSetRunPhase.A)
                {
                    Log("Phase B — Creating discipline views…", "info");
                    RunPhaseB(doc, s, ref pass, ref fail, ref skip);
                    s.LastCompletedPhase = CoordSetRunPhase.B;
                    s.Save();
                    Progress(28, pass, fail, skip);
                }

                // ── Phase C: create legend ─────────────────────────────────────
                if (startPhase <= CoordSetRunPhase.B)
                {
                    Log("Phase C — Creating legend…", "info");
                    RunPhaseC(doc, s, ref pass, ref fail, ref skip);
                    s.LastCompletedPhase = CoordSetRunPhase.C;
                    s.Save();
                    Progress(40, pass, fail, skip);
                }

                // ── Phase D: dependent views + grid bubbles (first level) ──────
                if (startPhase <= CoordSetRunPhase.C)
                {
                    Log("Phase D — Creating dependent views on first level…", "info");
                    RunPhaseD(doc, s, ref pass, ref fail, ref skip);
                    s.LastCompletedPhase = CoordSetRunPhase.D;
                    s.Save();
                    Progress(55, pass, fail, skip);
                }

                // ── Phase E: place first-set sheets ───────────────────────────
                if (startPhase <= CoordSetRunPhase.D)
                {
                    Log("Phase E — Creating sheets and placing viewports…", "info");
                    RunPhaseE(doc, s, ref pass, ref fail, ref skip);
                    s.LastCompletedPhase = CoordSetRunPhase.E;
                    s.Save();
                    Progress(70, pass, fail, skip);
                }

                // ── Phase F: replicate to all other levels ────────────────────
                if (startPhase <= CoordSetRunPhase.E)
                {
                    Log("Phase F — Replicating to all other levels…", "info");
                    RunPhaseF(doc, s, ref pass, ref fail, ref skip);
                    s.LastCompletedPhase = CoordSetRunPhase.F;
                    s.Save();
                    Progress(88, pass, fail, skip);
                }

                // ── Phase G: cover sheet ──────────────────────────────────────
                if (startPhase <= CoordSetRunPhase.F && s.CreateCoverSheet)
                {
                    Log("Phase G — Creating cover sheet…", "info");
                    RunPhaseG(doc, s, ref pass, ref fail, ref skip);
                }

                s.LastCompletedPhase = CoordSetRunPhase.G;
                s.Save();
            }
            catch (Exception ex)
            {
                LemoineLog.Error("CoordSet: run aborted", ex);
                Log($"Error: {ex.Message}", "fail");
                fail++;
            }

            Progress(100, pass, fail, skip);
            Log($"Done — {pass} created, {skip} skipped, {fail} failed.", "pass");
            long __issues = LemoineLog.IssuesSince(__issues0);
            if (__issues > 0) Log($"{__issues} non-fatal issue(s) recorded — see diagnostics log.", "warn");
            Complete(pass, fail, skip);
        }

        // ═════════════════════════════════════════════════════════════════════
        // PHASE A — Ensure ParameterFilterElements exist for selected trades
        // ═════════════════════════════════════════════════════════════════════
        private void RunPhaseA(Document doc, CoordSetSettings s,
            ref int pass, ref int fail, ref int skip)
        {
            var fs = AutoFiltersSettings.Instance;
            var tradesToProcess = fs.Trades
                .Where(t => SelectedTradeIds.Contains(t.Id))
                .ToList();

            if (tradesToProcess.Count == 0)
            {
                Log("Phase A: no trades selected — skipping filter creation.", "info");
                skip++;
                return;
            }

            // Collect all existing filter names to avoid duplicates
            var existingNames = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .Select(pfe => pfe.Name)
                .ToHashSet();

            using (var tx = new Transaction(doc, "Coord Set Phase A — Filters"))
            {
                ConfigureFailures(tx);
                tx.Start();
                foreach (var trade in tradesToProcess)
                {
                    foreach (var rule in trade.Rules.Where(r => r.Enabled))
                    {
                        string filterName = $"{trade.Id} - {rule.Name}";
                        if (existingNames.Contains(filterName))
                        {
                            skip++;
                            continue;
                        }
                        try
                        {
                            var catIds = ResolveCategoryIds(doc, rule.BuiltInCategories);
                            if (catIds.Count == 0) { skip++; continue; }
                            var pfe = ParameterFilterElement.Create(doc, filterName, catIds);
                            Log($"Filter: {filterName}", "pass");
                            pass++;
                        }
                        catch (Exception ex)
                        {
                            Log($"Filter '{filterName}': {ex.Message}", "fail");
                            fail++;
                        }
                    }
                }
                tx.Commit();
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // PHASE B — Create discipline floor plan views per level
        // ═════════════════════════════════════════════════════════════════════
        private void RunPhaseB(Document doc, CoordSetSettings s,
            ref int pass, ref int fail, ref int skip)
        {
            var allLevels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).ToList();

            var selectedSet = new HashSet<long>(SelectedLevelIds.Select(id => id.Value));
            var levels      = allLevels.Where(l => selectedSet.Contains(l.Id.Value)).ToList();
            if (levels.Count == 0) { Log("Phase B: no levels selected.", "info"); return; }

            var vftFP  = FindVFT(doc, ViewFamily.FloorPlan);
            var vftRCP = s.CreateRCP ? FindVFT(doc, ViewFamily.CeilingPlan) : null;

            if (vftFP == null)
            {
                Log("Phase B: no FloorPlan ViewFamilyType found.", "fail");
                fail++;
                return;
            }

            // Collect all filter elements by trade ID
            var filterMap = BuildFilterMap(doc);

            // Determine which discipline labels to create
            var disciplines = new List<string>();
            if (s.CreateCoordination) disciplines.Add(CoordLabel);
            if (s.CreateMechanical)   disciplines.Add("Mechanical");
            if (s.CreatePlumbing)     disciplines.Add("Plumbing");
            if (s.CreateFire)         disciplines.Add("Fire");
            if (s.CreateElectrical)   disciplines.Add("Electrical");
            if (s.CreateOther)        disciplines.Add(OtherLabel);
            if (s.CreateRCP && vftRCP != null) disciplines.Add(RcpLabel);

            // Trade IDs assigned to each named discipline
            var disciplineTradeMap = BuildDisciplineTradeMap(s);

            using (var tx = new Transaction(doc, "Coord Set Phase B — Discipline Views"))
            {
                ConfigureFailures(tx);
                tx.Start();

                foreach (var lvl in levels)
                {
                    // Create base coordination floor plan for this level
                    ViewPlan? coordFP = null;
                    string coordName = $"{lvl.Name} - {CoordLabel} - FP";
                    try
                    {
                        coordFP = CreateOrGetPlan(doc, vftFP.Id, lvl.Id, coordName, ref pass, ref skip);
                    }
                    catch (Exception ex)
                    {
                        Log($"Coord FP '{coordName}': {ex.Message}", "fail");
                        fail++;
                        continue;
                    }

                    // Store coord view id for later phases
                    s.SetArtifact($"CoordViewId_{lvl.Id.Value}", coordFP.Id.Value);

                    // Apply all selected trade filters to coordination view
                    if (coordFP != null)
                        ApplyFiltersToView(doc, coordFP, filterMap, SelectedTradeIds, null, s);

                    // Create discipline duplicates
                    foreach (var disc in disciplines.Where(d => d != CoordLabel))
                    {
                        if (disc == RcpLabel)
                        {
                            string rcpName = $"{lvl.Name} - {RcpLabel}";
                            try
                            {
                                CreateOrGetPlan(doc, vftRCP!.Id, lvl.Id, rcpName, ref pass, ref skip);
                            }
                            catch (Exception ex) { Log($"RCP '{rcpName}': {ex.Message}", "fail"); fail++; }
                            continue;
                        }

                        string viewName = $"{lvl.Name} - {disc} - FP";
                        try
                        {
                            ViewPlan existing = FindPlan(doc, viewName, ViewFamily.FloorPlan);
                            if (existing != null) { skip++; continue; }

                            ElementId dupId = coordFP!.Duplicate(ViewDuplicateOption.Duplicate);
                            ViewPlan?  dup   = doc.GetElement(dupId) as ViewPlan;
                            if (dup == null) { fail++; continue; }
                            dup.Name = viewName;

                            // Hide non-matching trades if setting enabled
                            if (s.HideNonMatchingTrades)
                            {
                                var discTrades = disciplineTradeMap.TryGetValue(disc, out var dt)
                                    ? dt : new HashSet<string>();
                                ApplyFiltersToView(doc, dup, filterMap, SelectedTradeIds,
                                    discTrades, s);
                            }

                            Log($"View: {viewName}", "pass");
                            pass++;
                        }
                        catch (Exception ex)
                        {
                            Log($"View '{viewName}': {ex.Message}", "fail");
                            fail++;
                        }
                    }
                }

                tx.Commit();
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // PHASE C — Create legend
        // ═════════════════════════════════════════════════════════════════════
        private void RunPhaseC(Document doc, CoordSetSettings s,
            ref int pass, ref int fail, ref int skip)
        {
            CoordSetLegendEventHandler.RunInline(
                doc, doc.ActiveView,
                s.LegendGroups?.Count > 0 ? s.LegendGroups : null,
                AutoFiltersSettings.Instance,
                PushLog,
                ref pass, ref fail, ref skip);
        }

        // ═════════════════════════════════════════════════════════════════════
        // PHASE D — Dependent views on first level + grid bubbles
        // ═════════════════════════════════════════════════════════════════════
        private void RunPhaseD(Document doc, CoordSetSettings s,
            ref int pass, ref int fail, ref int skip)
        {
            var allLevels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).ToList();

            var selectedSet = new HashSet<long>(SelectedLevelIds.Select(id => id.Value));
            var levels      = allLevels.Where(l => selectedSet.Contains(l.Id.Value)).ToList();
            if (levels.Count == 0) return;

            Level firstLevel = levels.First();

            // Collect all discipline FP views for the first level
            var discViews = CollectDisciplineViews(doc, firstLevel);

            if (discViews.Count == 0)
            {
                Log("Phase D: no discipline views found for first level.", "info");
                skip++;
                return;
            }

            using (var tx = new Transaction(doc, "Coord Set Phase D — Dependent Views"))
            {
                ConfigureFailures(tx);
                tx.Start();

                foreach (var parentView in discViews)
                {
                    try
                    {
                        ElementId depId = parentView.Duplicate(ViewDuplicateOption.AsDependent);
                        View? depView   = doc.GetElement(depId) as View;
                        if (depView == null) { fail++; continue; }

                        depView.Name = $"{parentView.Name} - Sheet";

                        // Apply grid bubble settings
                        ApplyGridBubbles(doc, depView, s.GridBubbles);

                        s.SetArtifact($"DepView_{firstLevel.Id.Value}_{parentView.Id.Value}", depId.Value);
                        Log($"Dep view: {depView.Name}", "pass");
                        pass++;
                    }
                    catch (Exception ex)
                    {
                        Log($"Dep view of '{parentView.Name}': {ex.Message}", "fail");
                        fail++;
                    }
                }

                tx.Commit();
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // PHASE E — Create sheets and place viewports for first level
        // ═════════════════════════════════════════════════════════════════════
        private void RunPhaseE(Document doc, CoordSetSettings s,
            ref int pass, ref int fail, ref int skip)
        {
            if (TitleBlockTypeId == null || TitleBlockTypeId == ElementId.InvalidElementId)
            {
                Log("Phase E: no title block selected — skipping sheet creation.", "info");
                skip++;
                return;
            }

            var selectedSet = new HashSet<long>(SelectedLevelIds.Select(id => id.Value));
            var firstLevel  = new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation)
                .FirstOrDefault(l => selectedSet.Contains(l.Id.Value));

            if (firstLevel == null) return;

            var discViews = CollectDisciplineViews(doc, firstLevel);

            using (var tx = new Transaction(doc, "Coord Set Phase E — Sheets"))
            {
                ConfigureFailures(tx);
                tx.Start();

                var discGroups = discViews
                    .GroupBy(v => GetDisciplineLabel(v.Name, firstLevel.Name))
                    .ToList();

                foreach (var grp in discGroups)
                {
                    string disc    = grp.Key;
                    string prefix  = GetDisciplinePrefix(s, disc);
                    string lvlAbbr = AbbrevLevel(firstLevel.Name);
                    string sheetNum = $"{prefix}{lvlAbbr}-001";

                    try
                    {
                        ViewSheet sheet = ViewSheet.Create(doc, TitleBlockTypeId);
                        sheet.SheetNumber = sheetNum;
                        sheet.Name        = $"{firstLevel.Name} - {disc}";

                        // Place each dep view centred at a tiled position
                        double xOff = 0;
                        foreach (var parentView in grp)
                        {
                            long depViewArtId = s.GetArtifact(
                                $"DepView_{firstLevel.Id.Value}_{parentView.Id.Value}");
                            if (depViewArtId < 0) continue;

                            ElementId depId    = new ElementId(depViewArtId);
                            View?     depView  = doc.GetElement(depId) as View;
                            if (depView == null) continue;

                            XYZ center = new XYZ(0.5 + xOff, 0.5, 0);
                            try
                            {
                                var vp = Viewport.Create(doc, sheet.Id, depId, center);
                                s.SetArtifact(
                                    $"VP_Center_{firstLevel.Id.Value}_{parentView.Id.Value}",
                                    EncodePair(center.X, center.Y));
                                xOff += 1.0;
                            }
                            catch (Exception ex)
                            {
                                Log($"Viewport for '{depView.Name}': {ex.Message}", "fail");
                                fail++;
                            }
                        }

                        s.SetArtifact($"Sheet_{disc}_{firstLevel.Id.Value}", sheet.Id.Value);
                        Log($"Sheet: {sheetNum}", "pass");
                        pass++;
                    }
                    catch (Exception ex)
                    {
                        Log($"Sheet '{sheetNum}': {ex.Message}", "fail");
                        fail++;
                    }
                }

                tx.Commit();
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // PHASE F — Replicate to all other selected levels
        // ═════════════════════════════════════════════════════════════════════
        private void RunPhaseF(Document doc, CoordSetSettings s,
            ref int pass, ref int fail, ref int skip)
        {
            if (TitleBlockTypeId == null || TitleBlockTypeId == ElementId.InvalidElementId)
            { skip++; return; }

            var allLevels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).ToList();

            var selectedSet = new HashSet<long>(SelectedLevelIds.Select(id => id.Value));
            var levels      = allLevels.Where(l => selectedSet.Contains(l.Id.Value)).ToList();
            if (levels.Count <= 1) return;

            Level firstLevel = levels.First();

            // Collect parent views from first level as template
            var firstLevelDiscViews = CollectDisciplineViews(doc, firstLevel);

            foreach (var targetLevel in levels.Skip(1))
            {
                var targetDiscViews = CollectDisciplineViews(doc, targetLevel);

                using (var tx = new Transaction(doc,
                    $"Coord Set Phase F — Level {targetLevel.Name}"))
                {
                    ConfigureFailures(tx);
                    tx.Start();

                    var discGroups = targetDiscViews
                        .GroupBy(v => GetDisciplineLabel(v.Name, targetLevel.Name))
                        .ToList();

                    foreach (var grp in discGroups)
                    {
                        string disc    = grp.Key;
                        string prefix  = GetDisciplinePrefix(s, disc);
                        string lvlAbbr = AbbrevLevel(targetLevel.Name);
                        string sheetNum = $"{prefix}{lvlAbbr}-001";

                        try
                        {
                            ViewSheet sheet = ViewSheet.Create(doc, TitleBlockTypeId);
                            sheet.SheetNumber = sheetNum;
                            sheet.Name        = $"{targetLevel.Name} - {disc}";

                            double xOff = 0;
                            foreach (var parentView in grp)
                            {
                                try
                                {
                                    ElementId depId = parentView.Duplicate(ViewDuplicateOption.AsDependent);
                                    View? depView   = doc.GetElement(depId) as View;
                                    if (depView == null) { fail++; continue; }
                                    depView.Name = $"{parentView.Name} - Sheet";

                                    ApplyGridBubbles(doc, depView, s.GridBubbles);

                                    // Match first-level viewport position if available
                                    ViewPlan matchParent = firstLevelDiscViews
                                        .FirstOrDefault(v =>
                                            GetDisciplineLabel(v.Name, firstLevel.Name) == disc);

                                    XYZ center;
                                    if (matchParent != null)
                                    {
                                        long encoded = s.GetArtifact(
                                            $"VP_Center_{firstLevel.Id.Value}_{matchParent.Id.Value}");
                                        center = encoded >= 0
                                            ? DecodePair(encoded)
                                            : new XYZ(0.5 + xOff, 0.5, 0);
                                    }
                                    else
                                    {
                                        center = new XYZ(0.5 + xOff, 0.5, 0);
                                    }

                                    Viewport.Create(doc, sheet.Id, depId, center);
                                    xOff += 1.0;
                                }
                                catch (Exception ex)
                                {
                                    Log($"F dep '{parentView.Name}': {ex.Message}", "fail");
                                    fail++;
                                }
                            }

                            Log($"Sheet: {sheetNum}", "pass");
                            pass++;
                        }
                        catch (Exception ex)
                        {
                            Log($"Sheet F '{sheetNum}': {ex.Message}", "fail");
                            fail++;
                        }
                    }

                    tx.Commit();
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // PHASE G — Cover sheet
        // ═════════════════════════════════════════════════════════════════════
        private void RunPhaseG(Document doc, CoordSetSettings s,
            ref int pass, ref int fail, ref int skip)
        {
            if (TitleBlockTypeId == null || TitleBlockTypeId == ElementId.InvalidElementId)
            { skip++; return; }

            ElementId textTypeId = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType)).FirstElementId();
            if (textTypeId == ElementId.InvalidElementId) { skip++; return; }

            var tx = new Transaction(doc, "Coord Set Phase G — Cover Sheet");
            ConfigureFailures(tx);
            tx.Start();
            try
            {
                ViewSheet cover = ViewSheet.Create(doc, TitleBlockTypeId);
                cover.SheetNumber = s.CoverSheetNumber;
                cover.Name        = s.CoverSheetName;

                var opts = new TextNoteOptions { TypeId = textTypeId };
                try { opts.HorizontalAlignment = HorizontalTextAlignment.Left; } catch (Exception __lex) { LemoineLog.Swallowed("CoordSet run: set text-note alignment", __lex); }

                double noteY = 1.0;
                void AddNote(string text)
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        TextNote.Create(doc, cover.Id, new XYZ(0.5, noteY, 0), 3.0, text, opts);
                        noteY -= 0.4;
                    }
                }

                AddNote(s.ProjectName);
                AddNote(s.ProjectNumber);
                AddNote(s.ProjectDate);

                var allSheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Where(sh => sh.SheetNumber != s.CoverSheetNumber)
                    .OrderBy(sh => sh.SheetNumber).ToList();

                if (allSheets.Count > 0)
                {
                    noteY -= 0.2;
                    string index = string.Join("\n",
                        allSheets.Select(sh => $"{sh.SheetNumber}   {sh.Name}"));
                    TextNote.Create(doc, cover.Id, new XYZ(0.5, noteY, 0), 4.0, index, opts);
                }

                tx.Commit();
                Log($"Cover sheet: {s.CoverSheetNumber}", "pass");
                pass++;
            }
            catch (Exception ex)
            {
                try { tx.RollBack(); } catch (Exception __lex) { LemoineLog.Swallowed("CoordSet run: roll back transaction", __lex); }
                Log($"Cover sheet: {ex.Message}", "fail");
                fail++;
            }
            finally
            {
                tx.Dispose();
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // HELPERS
        // ═════════════════════════════════════════════════════════════════════

        private static ViewPlan CreateOrGetPlan(Document doc, ElementId vftId,
            ElementId levelId, string name, ref int pass, ref int skip)
        {
            ViewPlan existing = FindPlan(doc, name,
                (doc.GetElement(vftId) as ViewFamilyType)?.ViewFamily ?? ViewFamily.FloorPlan);
            if (existing != null) { skip++; return existing; }

            ViewPlan vp = ViewPlan.Create(doc, vftId, levelId);
            vp.Name = name;
            pass++;
            return vp;
        }

        private static ViewPlan FindPlan(Document doc, string name, ViewFamily family)
            => new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .FirstOrDefault(v => v.Name == name
                    && v.ViewType != ViewType.Legend
                    && doc.GetElement(v.GetTypeId()) is ViewFamilyType vft && vft.ViewFamily == family);

        private static ViewFamilyType FindVFT(Document doc, ViewFamily family)
            => new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(vft => vft.ViewFamily == family);

        private static List<ElementId> ResolveCategoryIds(Document doc, IEnumerable<string> ostNames)
        {
            var ids = new List<ElementId>();
            foreach (var ost in ostNames)
            {
                if (Enum.TryParse<BuiltInCategory>(ost, out var bic))
                {
                    var catId = new ElementId(bic);
                    if (!ids.Contains(catId)) ids.Add(catId);
                }
            }
            return ids;
        }

        // filterMap: tradeId → list of ParameterFilterElement ids
        private static Dictionary<string, List<ElementId>> BuildFilterMap(Document doc)
        {
            var result = new Dictionary<string, List<ElementId>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pfe in new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>())
            {
                string name = pfe.Name;
                int sep = name.IndexOf(" - ");
                if (sep < 0) continue;
                string tradeId = name.Substring(0, sep).Trim();
                if (!result.ContainsKey(tradeId))
                    result[tradeId] = new List<ElementId>();
                result[tradeId].Add(pfe.Id);
            }
            return result;
        }

        private static void ApplyFiltersToView(Document doc, View view,
            Dictionary<string, List<ElementId>> filterMap,
            IEnumerable<string> allTrades,
            HashSet<string>? visibleTrades,
            CoordSetSettings s)
        {
            bool hideNonMatching = s.HideNonMatchingTrades && visibleTrades != null;

            foreach (var trade in allTrades)
            {
                if (!filterMap.TryGetValue(trade, out var fids)) continue;
                bool show = !hideNonMatching || (visibleTrades != null && visibleTrades.Contains(trade));
                foreach (var fid in fids)
                {
                    try
                    {
                        if (!view.GetFilters().Contains(fid))
                            view.AddFilter(fid);
                        view.SetFilterVisibility(fid, show);
                    }
                    catch (Exception __lex) { LemoineLog.Swallowed($"CoordSet run: apply filter visibility to view {view.Id.Value}", __lex); }
                }
            }
        }

        private static Dictionary<string, HashSet<string>> BuildDisciplineTradeMap(CoordSetSettings s)
        {
            // Map discipline label → set of matching trade IDs from T01 defaults
            // Coordination shows all trades.
            // "Other" = any trade not in the core discipline sets.
            var coreSets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "Mechanical",  new HashSet<string> { "MD", "MP", "MG", "PT" } },
                { "Plumbing",    new HashSet<string> { "PL" } },
                { "Fire",        new HashSet<string> { "FP" } },
                { "Electrical",  new HashSet<string> { "EL" } },
                { "Coordination",new HashSet<string>(CoreDisciplines) },
            };

            var all = AutoFiltersSettings.Instance.Trades.Select(t => t.Id).ToHashSet();
            var assigned = coreSets.Values.SelectMany(v => v).ToHashSet();
            coreSets[OtherLabel] = new HashSet<string>(all.Except(assigned));

            return coreSets;
        }

        private static void ApplyGridBubbles(Document doc, View view, GridBubbleSettings bs)
        {
            if (bs == null) return;
            var grids = new FilteredElementCollector(doc)
                .OfClass(typeof(Grid)).Cast<Grid>()
                .ToList();

            foreach (var grid in grids)
            {
                try
                {
                    // Determine grid orientation by its curve direction
                    var curve = grid.Curve;
                    XYZ dir   = (curve.GetEndPoint(1) - curve.GetEndPoint(0)).Normalize();
                    bool isVertical = Math.Abs(dir.X) < 0.1; // mostly Y → vertical grid lines

                    // For ViewSpecific override, set extent type first
                    foreach (DatumEnds end in new[] { DatumEnds.End0, DatumEnds.End1 })
                    {
                        try
                        {
                            grid.SetDatumExtentType(end, view, DatumExtentType.ViewSpecific);
                        }
                        catch (Exception __lex) { LemoineLog.Swallowed($"CoordSet run: set grid {grid.Id.Value} datum extent type", __lex); }
                    }

                    // Apply visibility per end based on orientation
                    // Vertical grid lines (run top→bottom): End0 = bottom, End1 = top
                    // Horizontal grid lines (run left→right): End0 = left, End1 = right
                    bool showEnd0, showEnd1;
                    if (isVertical)
                    {
                        showEnd0 = bs.ShowBottom;
                        showEnd1 = bs.ShowTop;
                    }
                    else
                    {
                        showEnd0 = bs.ShowLeft;
                        showEnd1 = bs.ShowRight;
                    }

                    if (showEnd0) grid.ShowBubbleInView(DatumEnds.End0, view);
                    else          grid.HideBubbleInView(DatumEnds.End0, view);

                    if (showEnd1) grid.ShowBubbleInView(DatumEnds.End1, view);
                    else          grid.HideBubbleInView(DatumEnds.End1, view);
                }
                catch (Exception __lex) { LemoineLog.Swallowed($"CoordSet run: set grid {grid.Id.Value} bubble visibility", __lex); }
            }
        }

        private static List<ViewPlan> CollectDisciplineViews(Document doc, Level level)
            => new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate
                    && v.GenLevel != null
                    && v.GenLevel.Id == level.Id
                    && v.Name.StartsWith(level.Name + " - ")
                    && v.Name.EndsWith(" - FP"))
                .ToList();

        private static string GetDisciplineLabel(string viewName, string levelName)
        {
            // viewName pattern: "{levelName} - {Disc} - FP"
            string prefix = levelName + " - ";
            if (!viewName.StartsWith(prefix)) return "Other";
            string rest = viewName.Substring(prefix.Length);
            int sufIdx   = rest.LastIndexOf(" - FP");
            return sufIdx >= 0 ? rest.Substring(0, sufIdx) : rest;
        }

        private static string GetDisciplinePrefix(CoordSetSettings s, string disc)
        {
            foreach (var e in s.DisciplinePrefixes)
                if (string.Equals(e.Discipline, disc, StringComparison.OrdinalIgnoreCase))
                    return e.Prefix;
            return s.SheetNumberPrefix;
        }

        private static string AbbrevLevel(string levelName)
        {
            // Convert "Level 2" → "L2", "B1" → "B1", etc.
            string n = levelName.Replace("Level ", "L").Replace("level ", "L");
            return n.Length > 8 ? n.Substring(0, 8) : n;
        }

        // Pack two sheet-coordinate doubles into one long (each stored as mm × 1000, 32-bit signed half)
        private static long EncodePair(double x, double y)
        {
            int xi = (int)Math.Round(x * 1000.0);
            int yi = (int)Math.Round(y * 1000.0);
            return ((long)(uint)xi << 32) | (uint)yi;
        }

        private static XYZ DecodePair(long v)
        {
            int xi = (int)(uint)(v >> 32);
            int yi = (int)(uint)(v & 0xFFFFFFFFL);
            return new XYZ(xi / 1000.0, yi / 1000.0, 0);
        }

        private static void ConfigureFailures(Transaction tx)
        {
            var opts = tx.GetFailureHandlingOptions();
            opts.SetClearAfterRollback(true);
            opts.SetDelayedMiniWarnings(true);
            tx.SetFailureHandlingOptions(opts);
        }

        private void Log(string t, string s) => PushLog?.Invoke(t, s);
        private void Progress(int p, int pa, int f, int s) => OnProgress?.Invoke(p, pa, f, s);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
