using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
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
            long __issues0 = DiagnosticsLog.IssueCount;
            int pass = 0, fail = 0, skip = 0;

            try
            {
                RunGrids(doc, ref pass, ref fail, ref skip);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("MakeCeilingGrids: run aborted", ex);
                Log(AppStrings.T("ceilings.makeGrids.log.error", ex.Message), "fail");
                fail++;
            }
            finally
            {
                // Session-long static handler — drop the run's payload.
                LinkInstIds       = new List<ElementId>();
                ExcludedTypeNames = new List<(string, string)>();
            }

            Progress(100, pass, fail, skip);
            long __issues = DiagnosticsLog.IssuesSince(__issues0);
            if (__issues > 0) Log(AppStrings.T("ceilings.makeGrids.log.nonFatalIssues", __issues), "warn");
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

            Log(AppStrings.T("ceilings.makeGrids.log.foundLevels", levels.Count), "info");

            // Find a CeilingPlan ViewFamilyType once
            var vft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.CeilingPlan);

            if (vft == null)
            {
                Log(AppStrings.T("ceilings.makeGrids.log.noRcpType"), "fail");
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
            bool cancelledInPhase1 = false;

            foreach (var level in levels)
            {
                if (RunState.CancelRequested)
                {
                    Log(AppStrings.T("common.log.stoppedByUser", done, total), "warn");
                    cancelledInPhase1 = true;
                    break;
                }

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
                            catch (Exception __lex) { DiagnosticsLog.Swallowed($"MakeCeilingGrids: name conflict for RCP '{viewName}' — keeping Revit's generated name", __lex); }
                        }

                        if (view != null)
                            SetCeilingOnlyVisibility(doc, view);

                        tx.Commit();
                    }

                    if (view == null) { Log(AppStrings.T("ceilings.makeGrids.log.rcpFailed", level.Name), "fail"); fail++; }
                    else              { createdViews.Add((view, view.Name)); }
                }
                catch (Exception ex)
                {
                    Log(AppStrings.T("ceilings.makeGrids.log.levelFailed", level.Name, ex.Message), "fail");
                    fail++;
                }

                done++;
                Progress((int)(done * 40.0 / total), pass, fail, skip);
            }

            if (createdViews.Count == 0)
            {
                Log(AppStrings.T("ceilings.makeGrids.log.noViewsCreated"), "fail");
                fail++;
                return;
            }

            // Cancelled while creating views → bail out BEFORE deleting hide filters or
            // rebuilding the trade, so a cancel can't wipe the previous run's hide-filter set.
            // The views created so far are kept.
            if (cancelledInPhase1)
                return;

            var viewIds = createdViews.Select(cv => cv.view.Id).ToList();

            // ── Phase 1b: scope the RCPs to the selected documents (per-document hide) ──
            ApplyDocumentScope(doc, createdViews);

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

                // Mirror the created filters into the "Ceiling Grids — Hidden" trade so they
                // appear (grouped) in the rules list and the generic Create-Filters engine never
                // regenerates them. Skip the rebuild if the create loop was cancelled — cgRules
                // is then partial and would drop rules from the trade.
                if (!RunState.CancelRequested)
                    RegisterHideTrade(cgRules);

                Log(AppStrings.T("ceilings.makeGrids.log.hidTypes", cgRules.Count, viewIds.Count), "info");
            }
            else
            {
                Log(AppStrings.T("ceilings.makeGrids.log.noExclusions"), "info");
            }

            Progress(70, pass, fail, skip);

            // ── Phase 3: export each view to DWG (70–100%) ───────────────────────────
            // Resolve and create the output folder ONCE (not per view). A blank folder means
            // "create the views only, no export". If the folder can't be created, fall back to
            // create-only rather than failing every view with the same directory error.
            bool   exportEnabled = !string.IsNullOrWhiteSpace(OutputFolder);
            string outDir        = "";
            if (exportEnabled)
            {
                outDir = UseCeilingGridsSubfolder
                    ? Path.Combine(OutputFolder, "Ceiling Grids")
                    : OutputFolder;
                try { EnsureDir(outDir); }
                catch (Exception ex)
                {
                    Log(AppStrings.T("ceilings.makeGrids.log.exportDirFailed", outDir, ex.Message), "fail");
                    fail++;
                    exportEnabled = false;
                }
            }

            for (int i = 0; i < createdViews.Count; i++)
            {
                if (RunState.CancelRequested)
                {
                    Log(AppStrings.T("common.log.stoppedByUser", i, createdViews.Count), "warn");
                    break;
                }

                var (view, name) = createdViews[i];
                try
                {
                    if (exportEnabled)
                    {
                        bool overwrite = File.Exists(Path.Combine(outDir, name + ".dwg"));
                        ExportDwg(doc, view, name, outDir);
                        Log(AppStrings.T(overwrite ? "ceilings.makeGrids.log.overwrote"
                                                   : "ceilings.makeGrids.log.exported", name), "pass");
                    }
                    else
                    {
                        Log(AppStrings.T("ceilings.makeGrids.log.created", name), "pass");
                    }
                    pass++;
                }
                catch (Exception ex)
                {
                    Log(AppStrings.T("ceilings.makeGrids.log.exportFailed", name, ex.Message), "fail");
                    fail++;
                }

                Progress(70 + (int)((i + 1) * 30.0 / createdViews.Count), pass, fail, skip);
            }
        }

        // ── Scope each RCP to the selected documents ─────────────────────────────────
        //
        // The document selection (host + links) now actually controls what the created
        // ceiling plans show, not just which types the scan lists:
        //   • Every visible link NOT selected is hidden in the view. Because the RCP shows
        //     only ceilings, hiding the link removes its ceilings from the plan.
        //   • If the host is not selected, host ceilings are hidden individually — a category
        //     hide can't be used, because links displayed "By Host View" cascade the host's
        //     OST_Ceilings visibility and would lose the selected links' ceilings too.
        // With everything selected (the default) nothing is hidden — behaviour is unchanged.
        private void ApplyDocumentScope(Document doc, List<(ViewPlan view, string name)> views)
        {
            var selectedLinkIds = new HashSet<long>(LinkInstIds.Select(id => id.Value));
            var hiddenLinkViews = new Dictionary<string, int>(StringComparer.Ordinal);
            int hostHiddenViews = 0;

            using (var tx = new Transaction(doc, "Scope Ceiling Grid Views"))
            {
                ConfigureFailures(tx);
                tx.Start();

                foreach (var (view, name) in views)
                {
                    try
                    {
                        var toHide = new List<ElementId>();

                        foreach (RevitLinkInstance link in new FilteredElementCollector(doc, view.Id)
                            .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                        {
                            if (selectedLinkIds.Contains(link.Id.Value)) continue;
                            if (!link.CanBeHidden(view)) continue;
                            toHide.Add(link.Id);
                            hiddenLinkViews[link.Name] = hiddenLinkViews.TryGetValue(link.Name, out int c) ? c + 1 : 1;
                        }

                        if (!IncludeHost)
                        {
                            var hostCeilings = new FilteredElementCollector(doc, view.Id)
                                .OfCategory(BuiltInCategory.OST_Ceilings)
                                .WhereElementIsNotElementType()
                                .Where(e => e.CanBeHidden(view))
                                .Select(e => e.Id)
                                .ToList();
                            if (hostCeilings.Count > 0)
                            {
                                toHide.AddRange(hostCeilings);
                                hostHiddenViews++;
                            }
                        }

                        if (toHide.Count > 0)
                            view.HideElements(toHide);
                    }
                    catch (Exception ex)
                    {
                        Log(AppStrings.T("ceilings.makeGrids.log.scopeFailed", name, ex.Message), "warn");
                        DiagnosticsLog.Swallowed($"MakeCeilingGrids: scope view '{name}'", ex);
                    }
                }

                tx.Commit();
            }

            foreach (var kv in hiddenLinkViews)
                Log(AppStrings.T("ceilings.makeGrids.log.scopeHiddenLink", kv.Key, kv.Value), "info");
            if (hostHiddenViews > 0)
                Log(AppStrings.T("ceilings.makeGrids.log.scopeHiddenHost", hostHiddenViews), "info");
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
                    if (RunState.CancelRequested)
                    {
                        Log(AppStrings.T("common.log.stoppedByUser", cgRules.Count, excluded.Count), "warn");
                        break;
                    }

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
                            Log(AppStrings.T("ceilings.makeGrids.log.filterCreateError", filterName, ex.Message), "fail");
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
                            Log(AppStrings.T("ceilings.makeGrids.log.filterApplyError", vp.Name, ex.Message), "fail");
                            fail++;
                        }
                    }

                    cgRules.Add((ruleName, type));
                    // Hide-filter creation is an internal step; the success total counts the
                    // views exported in Phase 3, not the per-type filters created here.
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

            var hideIds = new HashSet<long>(hideFilters.Select(f => f.Id.Value));

            using (var tx = new Transaction(doc, "Delete Ceiling Grids Hide Filters"))
            {
                ConfigureFailures(tx);
                tx.Start();

                // Iterate views once, reading each view's filter list a single time, instead of
                // scanning every view for every hide filter (filters × views GetFilters calls).
                foreach (View v in allViews)
                {
                    try
                    {
                        foreach (ElementId fid in v.GetFilters().Where(id => hideIds.Contains(id.Value)).ToList())
                            v.RemoveFilter(fid);
                    }
                    catch (Exception __lex) { DiagnosticsLog.Swallowed("MakeCeilingGrids: remove filter from view (may not support filters)", __lex); }
                }

                foreach (var pfe in hideFilters)
                {
                    try   { doc.Delete(pfe.Id); }
                    catch (Exception ex)
                    {
                        Log(AppStrings.T("ceilings.makeGrids.log.filterDeleteError", pfe.Name, ex.Message), "fail");
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
                Log(AppStrings.T("ceilings.makeGrids.log.tradeRegistered", CGTradeLabel, trade.Rules.Count), "info");
            }
            catch (Exception ex)
            {
                // Non-fatal: the Revit filters were already created/applied above. Surface
                // the failure so the rules-list sync issue isn't hidden.
                DiagnosticsLog.Error("MakeCeilingGrids: register Ceiling Grids hide trade", ex);
                Log(AppStrings.T("ceilings.makeGrids.log.rulesUpdateFailed", ex.Message), "fail");
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
                        DiagnosticsLog.Swallowed("MakeCeilingGrids: read link display mode", ex);
                        continue;
                    }

                    if (mode != LinkVisibility.ByHostView)
                    {
                        notCascading++;
                        Log(AppStrings.T("ceilings.makeGrids.log.linkNotCascading", link.Name, view.Name, mode),
                            "fail");
                        DiagnosticsLog.Warn("MakeCeilingGrids",
                            $"link '{link.Name}' in view '{view.Name}' display={mode}; "
                            + "host hide filters will not cascade onto its ceilings.");
                    }
                }
            }

            if (notCascading == 0)
                DiagnosticsLog.Info("MakeCeilingGrids",
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
                try { view.SetCategoryHidden(cat.Id, !keep); } catch (Exception __lex) { DiagnosticsLog.Swallowed($"MakeCeilingGrids: set category {cat.Id.Value} visibility in view {view.Id.Value}", __lex); }
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
