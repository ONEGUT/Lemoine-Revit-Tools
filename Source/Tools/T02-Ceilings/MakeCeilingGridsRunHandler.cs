using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.AutoFilters;

namespace LemoineTools.Tools.Ceilings
{
    public sealed class MakeCeilingGridsRunHandler : IExternalEventHandler
    {
        // ── Trade this tool registers its hide-filters/rules under (mirrors the
        //    Ceiling Heatmap's CH trade pattern) ─────────────────────────────────
        private const string CGTradeId    = "CG";
        private const string CGTradeLabel = "Ceiling Grids — Hidden";
        private const string CGTradeColor = "#7F7F7F";

        // ── Inputs ────────────────────────────────────────────────────────────
        public bool                   IncludeHost   { get; set; } = true;
        public List<ElementId>        LinkInstIds   { get; set; } = new List<ElementId>();

        /// <summary>
        /// Family/Type name pairs the user unchecked. One ParameterFilterElement (and one
        /// "Ceiling Grids — Hidden" trade rule) is created per pair, matching ceilings by
        /// Type Name, with the filter's visibility turned off in each RCP view. Host view
        /// filters cascade onto links shown "By Host View", so this hides host and linked
        /// ceilings alike — the same mechanism the Ceiling Heatmap uses to color them.
        /// </summary>
        public List<(string Family, string Type)> ExcludedTypeNames { get; set; } = new List<(string, string)>();

        public string                 OutputFolder             { get; set; } = "";
        public bool                   UseCeilingGridsSubfolder { get; set; } = false;

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.Ceilings.MakeCeilingGridsRunHandler";

        public void Execute(UIApplication app)
        {
            var doc  = app.ActiveUIDocument.Document;
            long __issues0 = LemoineLog.IssueCount;
            int pass = 0, fail = 0, skip = 0;

            try
            {
                RunGrids(doc, ref pass, ref fail, ref skip);
            }
            catch (Exception ex)
            {
                LemoineLog.Error("MakeCeilingGrids: run aborted", ex);
                Log($"Error: {ex.Message}", "fail");
                fail++;
            }

            Progress(100, pass, fail, skip);
            long __issues = LemoineLog.IssuesSince(__issues0);
            if (__issues > 0) Log($"{__issues} non-fatal issue(s) recorded — see diagnostics log.", "warn");
            Complete(pass, fail, skip);
        }

        // ─────────────────────────────────────────────────────────────────────────
        private void RunGrids(Document doc, ref int pass, ref int fail, ref int skip)
        {
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            Log($"Found {levels.Count} level(s).", "info");

            // Find a CeilingPlan ViewFamilyType once
            var vft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.CeilingPlan);

            if (vft == null)
            {
                Log("No Ceiling Plan view family type found in this project.", "fail");
                fail++;
                return;
            }

            // Distinct ceiling types to hide, by (Family, Type) name.
            var excluded = ExcludedTypeNames
                .Where(p => !string.IsNullOrWhiteSpace(p.Type))
                .GroupBy(p => $"{p.Family}|{p.Type}", StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();

            // ── Phase 1: find/create the RCP views + ceiling-only visibility (0–40%) ──
            var createdViews = new List<(ViewPlan view, string name)>();
            int total = Math.Max(1, levels.Count);
            int done  = 0;

            foreach (var level in levels)
            {
                try
                {
                    string viewName = Sanitize(level.Name) + "_CeilingGrid";

                    ViewPlan? view = null;
                    using (var tx = new Transaction(doc, $"Create RCP: {viewName}"))
                    {
                        ConfigureFailures(tx);
                        tx.Start();

                        view = FindRcp(doc, viewName);
                        if (view == null)
                        {
                            view = ViewPlan.Create(doc, vft.Id, level.Id);
                            try   { view.Name = viewName; }
                            catch (Exception __lex) { LemoineLog.Swallowed($"MakeCeilingGrids: name conflict for RCP '{viewName}' — keeping Revit's generated name", __lex); }
                        }

                        if (view != null)
                            SetCeilingOnlyVisibility(doc, view);

                        tx.Commit();
                    }

                    if (view == null) { Log($"Could not create RCP for {level.Name}.", "fail"); fail++; }
                    else              { createdViews.Add((view, view.Name)); }
                }
                catch (Exception ex)
                {
                    Log($"Level {level.Name} failed: {ex.Message}", "fail");
                    fail++;
                }

                done++;
                Progress((int)(done * 40.0 / total), pass, fail, skip);
            }

            if (createdViews.Count == 0)
            {
                Log("No ceiling plan views were created — nothing to do.", "fail");
                fail++;
                return;
            }

            var viewIds = createdViews.Select(cv => cv.view.Id).ToList();

            // ── Phase 2: hide excluded types via filters + trade rules (40–70%) ──────
            if (excluded.Count > 0)
            {
                // Diagnostic: host view filters only cascade onto a link displayed
                // "By Host View". Warn about any link that won't be hidden (same
                // accommodation the Ceiling Heatmap makes).
                ReportLinkDisplayModes(doc, viewIds);

                // Remove any prior "CG_" hide filters so the set reflects this run.
                DeleteHideFilters(doc, ref fail);

                var cgRules = CreateAndApplyHideFilters(doc, viewIds, excluded, ref pass, ref fail);

                // Mirror the created filters into the "Ceiling Grids — Hidden" trade so
                // they appear (grouped) in the rules list and the generic Create-Filters
                // engine never regenerates them.
                RegisterHideTrade(cgRules);

                Log($"Hid {cgRules.Count} ceiling type(s) across {viewIds.Count} view(s).", "info");
            }
            else
            {
                Log("No ceiling types excluded — all ceilings shown.", "info");
            }

            Progress(70, pass, fail, skip);

            // ── Phase 3: export each view to DWG (70–100%) ───────────────────────────
            int exported = 0;
            for (int i = 0; i < createdViews.Count; i++)
            {
                var (view, name) = createdViews[i];
                try
                {
                    if (!string.IsNullOrWhiteSpace(OutputFolder))
                    {
                        string outDir = UseCeilingGridsSubfolder
                            ? EnsureDir(Path.Combine(OutputFolder, "Ceiling Grids"))
                            : OutputFolder;
                        EnsureDir(outDir);
                        ExportDwg(doc, view, name, outDir);
                        Log($"Exported: {name}.dwg", "pass");
                        exported++;
                    }
                    else
                    {
                        Log($"Created: {name}", "pass");
                    }
                    pass++;
                }
                catch (Exception ex)
                {
                    Log($"Export of {name} failed: {ex.Message}", "fail");
                    fail++;
                }

                Progress(70 + (int)((i + 1) * 30.0 / createdViews.Count), pass, fail, skip);
            }
        }

        // ── Create one filter per excluded type and apply (hidden) to every view ──────
        //
        // Mirrors CeilingHeatmapEventHandler: reuse-by-name, per-view try/catch, one
        // ParameterFilterElement per rule. The only difference is SetFilterVisibility is
        // false here (hide) where the heatmap sets it true (show + color).
        private List<(string ruleName, string typeName)> CreateAndApplyHideFilters(
            Document doc, List<ElementId> viewIds,
            List<(string Family, string Type)> excluded,
            ref int pass, ref int fail)
        {
            var cgRules = new List<(string ruleName, string typeName)>();

            var ceilingCatId = new ElementId(BuiltInCategory.OST_Ceilings);
            // Type Name is a BuiltInParameter, so its id is valid even with no host
            // ceilings, and it matches elements inside links (link-safe), exactly like
            // the heatmap's height-offset parameter.
            var typeParamId  = new ElementId(BuiltInParameter.ALL_MODEL_TYPE_NAME);

            var existingFilters = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .ToDictionary(f => f.Name);

            using (var tx = new Transaction(doc, "Ceiling Grids — Hide Filters"))
            {
                ConfigureFailures(tx);
                tx.Start();

                foreach (var (family, type) in excluded)
                {
                    // Rule name carries the family for readability; the filter still
                    // matches on Type Name (one discriminating parameter per rule, as in
                    // the AutoFilters rule model).
                    string ruleName   = $"{family} — {type}";
                    string filterName = AutoFiltersSettings.MakeFilterName(CGTradeId, ruleName);

                    ParameterFilterElement? pfe;
                    if (!existingFilters.TryGetValue(filterName, out pfe))
                    {
                        try
                        {
                            FilterRule rule = ParameterFilterRuleFactory.CreateEqualsRule(typeParamId, type);
                            var epf = new ElementParameterFilter(rule);
                            pfe = ParameterFilterElement.Create(
                                doc, filterName, new List<ElementId> { ceilingCatId }, epf);
                            existingFilters[filterName] = pfe;
                        }
                        catch (Exception ex)
                        {
                            Log($"Error creating hide filter '{filterName}': {ex.Message}", "fail");
                            fail++;
                            continue;
                        }
                    }

                    foreach (ElementId viewId in viewIds)
                    {
                        var vp = doc.GetElement(viewId) as ViewPlan;
                        if (vp == null) continue;
                        try
                        {
                            if (!vp.GetFilters().Any(id => id.Value == pfe.Id.Value))
                                vp.AddFilter(pfe.Id);
                            vp.SetFilterVisibility(pfe.Id, false); // hide matched ceilings
                        }
                        catch (Exception ex)
                        {
                            Log($"Error applying hide filter to view: {ex.Message}", "fail");
                        }
                    }

                    cgRules.Add((ruleName, type));
                    pass++;
                }

                tx.Commit();
            }

            return cgRules;
        }

        // ── Delete prior "CG_" hide filters (removing them from all views first) ──────
        private void DeleteHideFilters(Document doc, ref int fail)
        {
            var hideFilters = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .Where(f => f.Name.StartsWith(CGTradeId + "_"))
                .ToList();

            if (hideFilters.Count == 0) return;

            var allViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate)
                .ToList();

            using (var tx = new Transaction(doc, "Delete Ceiling Grids Hide Filters"))
            {
                ConfigureFailures(tx);
                tx.Start();

                foreach (var pfe in hideFilters)
                {
                    foreach (View v in allViews)
                    {
                        try
                        {
                            if (v.GetFilters().Contains(pfe.Id))
                                v.RemoveFilter(pfe.Id);
                        }
                        catch (Exception __lex) { LemoineLog.Swallowed("MakeCeilingGrids: remove filter from view (may not support filters)", __lex); }
                    }

                    try   { doc.Delete(pfe.Id); }
                    catch (Exception ex)
                    {
                        Log($"Could not delete filter '{pfe.Name}': {ex.Message}", "fail");
                        fail++;
                    }
                }

                tx.Commit();
            }
        }

        // ── Mirror created filters into the "Ceiling Grids — Hidden" trade ────────────
        // Rebuilt every run so the rules always match the current exclusions. Each rule is
        // linked to its Revit filter by the shared name convention, hides its matches
        // (Visible = false), and the trade is ExternallyManaged so the generic
        // Create-Filters engine never regenerates these rules. Mirrors
        // CeilingHeatmapEventHandler.RegisterCeilingHeatmapTrade.
        private void RegisterHideTrade(List<(string ruleName, string typeName)> cgRules)
        {
            try
            {
                var settings = AutoFiltersSettings.Instance;

                var trade = settings.Trades.FirstOrDefault(
                    t => string.Equals(t.Id, CGTradeId, StringComparison.OrdinalIgnoreCase));
                if (trade == null)
                {
                    trade = new FilterTradeConfig { Id = CGTradeId };
                    settings.Trades.Add(trade);
                }

                trade.Label             = CGTradeLabel;
                trade.Color             = CGTradeColor;
                trade.ExternallyManaged = true;

                trade.Rules.Clear();
                foreach (var (ruleName, typeName) in cgRules)
                {
                    var rule = FilterRuleConfig.NewBlank();
                    rule.Name              = ruleName;
                    rule.Enabled           = true;
                    rule.Parameter         = "Type Name";
                    rule.BuiltInCategories = new List<string> { "OST_Ceilings" };
                    rule.MatchType         = "equals";
                    rule.Match             = new List<string> { typeName };
                    rule.Visible           = false; // hide matched ceilings
                    rule.FilterOn          = true;
                    rule.OverrideCut       = false;
                    rule.OverrideSurf      = false;
                    rule.OverrideLine      = false;
                    rule.Notes             = "Auto-generated by Make Ceiling Grids (hides this ceiling type). " +
                                             "Managed by the Make Ceiling Grids tool.";
                    trade.Rules.Add(rule);
                }

                settings.Save();
                Log($"Registered '{CGTradeLabel}' trade with {trade.Rules.Count} rule(s).", "info");
            }
            catch (Exception ex)
            {
                // Non-fatal: the Revit filters were already created/applied above. Surface
                // the failure so the rules-list sync issue isn't hidden.
                LemoineLog.Error("MakeCeilingGrids: register Ceiling Grids hide trade", ex);
                Log($"Filters applied, but could not update the rules list: {ex.Message}", "fail");
            }
        }

        /// <summary>
        /// Diagnostic only — host view filters cascade onto a link's elements only when the
        /// link is displayed "By Host View" in that view. For every view × visible link,
        /// log the link's display mode and warn about any link whose ceilings won't be
        /// hidden. Does NOT change the link's display settings. Mirrors the Ceiling
        /// Heatmap's ReportLinkDisplayModes.
        /// </summary>
        private void ReportLinkDisplayModes(Document doc, List<ElementId> viewIds)
        {
            int notCascading = 0;

            foreach (ElementId viewId in viewIds)
            {
                if (!(doc.GetElement(viewId) is View view)) continue;

                foreach (RevitLinkInstance link in new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .Where(li => li.GetLinkDocument() != null))
                {
                    LinkVisibility mode;
                    try
                    {
                        RevitLinkGraphicsSettings? gs = view.GetLinkOverrides(link.Id);
                        mode = gs?.LinkVisibilityType ?? LinkVisibility.ByHostView;
                    }
                    catch (Exception ex)
                    {
                        LemoineLog.Swallowed("MakeCeilingGrids: read link display mode", ex);
                        continue;
                    }

                    if (mode != LinkVisibility.ByHostView)
                    {
                        notCascading++;
                        Log($"Link \"{link.Name}\" in view \"{view.Name}\" is displayed "
                            + $"\"{mode}\", not \"By Host View\" — its excluded ceilings will not be "
                            + "hidden. Set it to \"By Host View\" in Visibility/Graphics to include them.",
                            "fail");
                        LemoineLog.Warn("MakeCeilingGrids",
                            $"link '{link.Name}' in view '{view.Name}' display={mode}; "
                            + "host hide filters will not cascade onto its ceilings.");
                    }
                }
            }

            if (notCascading == 0)
                LemoineLog.Info("MakeCeilingGrids",
                    "all visible links display By Host View — hide filters will cascade onto linked ceilings.");
        }

        // ── View helpers ───────────────────────────────────────────────────────
        private static ViewPlan? FindRcp(Document doc, string name)
            => new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .FirstOrDefault(v =>
                    v.Name == name && !v.IsTemplate &&
                    (doc.GetElement(v.GetTypeId()) as ViewFamilyType)?.ViewFamily == ViewFamily.CeilingPlan);

        private static void SetCeilingOnlyVisibility(Document doc, View view)
        {
            view.AreAnnotationCategoriesHidden      = true;
            view.AreAnalyticalModelCategoriesHidden = true;
            view.AreImportCategoriesHidden          = true;

            // Model categories: hide everything except Ceilings
            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat.CategoryType != CategoryType.Model) continue;
                if (!cat.get_AllowsVisibilityControl(view)) continue;
                bool keep = cat.Id.Value == (int)BuiltInCategory.OST_Ceilings;
                try { view.SetCategoryHidden(cat.Id, !keep); } catch (Exception __lex) { LemoineLog.Swallowed($"MakeCeilingGrids: set category {cat.Id.Value} visibility in view {view.Id.Value}", __lex); }
            }
        }

        // ── DWG export ─────────────────────────────────────────────────────────
        private static void ExportDwg(Document doc, View view, string baseName, string outDir)
        {
            var opts = new DWGExportOptions
            {
                FileVersion = ACADVersion.R2018,
                MergedViews = true,
            };
            doc.Export(outDir, baseName, new List<ElementId> { view.Id }, opts);
        }

        private static string Sanitize(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Join("_", name.Split(invalid)).Trim();
        }

        private static string EnsureDir(string path)
        {
            Directory.CreateDirectory(path);
            return path;
        }

        private static void ConfigureFailures(Transaction tx)
        {
            var opts = tx.GetFailureHandlingOptions();
            opts.SetClearAfterRollback(true);
            opts.SetDelayedMiniWarnings(true);
            tx.SetFailureHandlingOptions(opts);
        }

        private void Log(string text, string status) => PushLog?.Invoke(text, status);
        private void Progress(int pct, int p, int f, int s) => OnProgress?.Invoke(pct, p, f, s);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
