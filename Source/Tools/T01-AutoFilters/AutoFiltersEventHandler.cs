using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Helpers;
using RevitColor = Autodesk.Revit.DB.Color;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.AutoFilters
{
    /// <summary>
    /// Executes the Auto Filters filter-creation cycle on the Revit main thread.
    ///
    /// Set all properties then call Raise(). Revit calls Execute() at the next idle moment.
    /// </summary>
    public class AutoFiltersEventHandler : IExternalEventHandler
    {
        // ── Set by caller before Raise() ──────────────────────────────────────
        public IList<string> SelectedLinkTitles   { get; set; } = new List<string>();
        public IList<string> SelectedDisciplines  { get; set; } = new List<string>();

        /// <summary>
        /// When true, only create/update ParameterFilterElement definitions — do not apply
        /// filters to any view and do not set graphic overrides. OverwriteFilterDefinition
        /// is treated as true in this mode.
        /// </summary>
        public bool CreateOnly { get; set; } = false;

        /// <summary>
        /// When true, graphic overrides are NOT re-applied to filters that already exist on the view.
        /// Use this to preserve manually-adjusted overrides from a previous run.
        /// </summary>
        public bool KeepExistingOverrides { get; set; } = false;

        /// <summary>
        /// When true, existing ParameterFilterElements with matching names are deleted and recreated
        /// so that changes to categories or match keywords take effect.
        /// When false (default), existing elements are reused without updating their definition.
        /// </summary>
        public bool OverwriteFilterDefinition { get; set; } = false;

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?        PushLog    { get; set; }
        public Action<int, int, int, int>?    OnProgress { get; set; }
        /// <summary>Invoked on Revit's main thread with (pass, fail, skip, removed).</summary>
        public Action<int, int, int, int>?    OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.AutoFilters.AutoFiltersEventHandler";

        public void Execute(UIApplication app)
        {
            var doc  = app.ActiveUIDocument.Document;
            var view = doc.ActiveView;
            int pass = 0, fail = 0, skip = 0, removed = 0;

            try
            {
                Run(app, doc, view, ref pass, ref fail, ref skip, ref removed);
            }
            catch (Exception ex)
            {
                LemoineLog.Error("AutoFilters: run aborted", ex); Log($"Error: {ex.Message}", "fail");
                fail++;
            }

            Progress(100, pass, fail, skip);
            Complete(pass, fail, skip, removed);
        }

        // ── Core logic ────────────────────────────────────────────────────────
        //
        // V3 schema: Trade → Rule.
        // Each Rule carries its own BuiltInCategories list, Parameter name,
        // explicit match keywords, matchType, and graphic overrides.
        //
        // Filter name pattern: {Trade.Id}_{Rule.Name.Replace(" ","_").ToUpper()}
        // One ParameterFilterElement per enabled Rule (unless matchType == "all" where
        // a HasValue rule is used to match all elements with any value for the parameter).
        //
        private void Run(UIApplication app, Document doc, View view,
            ref int pass, ref int fail, ref int skip, ref int removed)
        {
            bool createOnly   = CreateOnly;
            bool overwriteDef = createOnly || OverwriteFilterDefinition;

            if (!createOnly && !view.AreGraphicsOverridesAllowed())
            {
                Log($"Active view '{view.Name}' does not support graphic overrides.", "fail");
                fail++; return;
            }

            // ── Collect source documents ──────────────────────────────────────
            var sourceDocs = new List<Document> { doc };
            var selectedLinkSet = new HashSet<string>(SelectedLinkTitles, StringComparer.OrdinalIgnoreCase);
            foreach (RevitLinkInstance li in
                new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document ld = li.GetLinkDocument();
                if (ld == null) continue;
                string title = ld.Title ?? li.Name;
                if (selectedLinkSet.Contains(title) && !sourceDocs.Any(d => d.Equals(ld)))
                    sourceDocs.Add(ld);
            }
            Log($"Scanning {sourceDocs.Count} document(s)…", "info");

            // ── Load V2 config ────────────────────────────────────────────────
            var trades = AutoFiltersSettings.Instance.Trades ?? new List<FilterTradeConfig>();
            var selectedTradeSet = new HashSet<string>(SelectedDisciplines, StringComparer.OrdinalIgnoreCase);

            // BIP map — parameter display name → BuiltInParameter for fast resolution
            // Works even when no elements of that category exist in the model.
            var bipMap = new Dictionary<string, BuiltInParameter>(StringComparer.Ordinal)
            {
                ["System Classification"] = BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM,
                ["Fabrication Service"]   = BuiltInParameter.FABRICATION_SERVICE_NAME,
                ["Type Name"]             = BuiltInParameter.ALL_MODEL_TYPE_NAME,
                ["Family Name"]           = BuiltInParameter.ELEM_FAMILY_PARAM,
                ["Structural Material"]   = BuiltInParameter.STRUCTURAL_MATERIAL_PARAM,
            };

            Progress(5, pass, fail, skip);

            // ── Count total enabled rules to size progress bar ────────────────
            int totalRules = trades.Sum(t =>
                (selectedTradeSet.Count == 0 || selectedTradeSet.Contains(t.Label))
                    ? t.Rules.Count(r => r.Enabled && (r.MatchType == "all" || r.Match.Count > 0))
                    : 0);
            if (totalRules == 0)
            {
                Log("No enabled rules with match keywords found for selected trades.", "fail");
                fail++; return;
            }
            int rulesDone = 0;

            // ── Collect existing filters and view filter ids ───────────────────
            ElementId solidFillId = GetSolidFillId(doc);
            ElementId solidLineId = GetSolidLineId();

            // Pattern lookup maps — built once, used per-rule in ApplyRuleOverride
            var fillPatternMap = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .GroupBy(fp => fp.Name, System.StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, System.StringComparer.OrdinalIgnoreCase);
            var linePatternMap = new FilteredElementCollector(doc)
                .OfClass(typeof(LinePatternElement))
                .Cast<LinePatternElement>()
                .GroupBy(lp => lp.Name, System.StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, System.StringComparer.OrdinalIgnoreCase);

            var existingFilters = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .ToDictionary(f => f.Name);

            var existingViewFilterIds = createOnly
                ? new HashSet<long>()
                : new HashSet<long>(view.GetFilters().Select(id => id.Value));

            int reused = 0;

            // Build the expected filter name set from current settings (used for orphan cleanup)
            var expectedFilterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (createOnly)
            {
                foreach (var t in trades)
                {
                    foreach (var r in t.Rules)
                    {
                        if (!r.Enabled || (r.MatchType != "all" && r.Match.Count == 0)) continue;
                        expectedFilterNames.Add(t.Id + "_" + r.Name.Trim().Replace(" ", "_").ToUpperInvariant());
                    }
                }
            }

            string txName = createOnly ? "Auto Filters — Create" : "Auto Filters — Create & Color";
            using (var tx = new Transaction(doc, txName))
            {
                ConfigureFailures(tx);
                tx.Start();

                // Remove orphaned filters: in the saved manifest but no longer in the expected set
                if (createOnly)
                {
                    var prevCreated = AutoFiltersSettings.Instance.CreatedFilterNames;
                    if (prevCreated != null)
                    {
                        foreach (var orphanName in prevCreated)
                        {
                            if (!expectedFilterNames.Contains(orphanName)
                                && existingFilters.TryGetValue(orphanName, out var orphan))
                            {
                                try
                                {
                                    doc.Delete(orphan.Id);
                                    existingFilters.Remove(orphanName);
                                    removed++;
                                    Log($"Removed '{orphanName}'.", "info");
                                }
                                catch (Exception ex)
                                {
                                    Log($"Could not remove '{orphanName}': {ex.Message}", "fail");
                                }
                            }
                        }
                    }
                }

                var matMap = new FilteredElementCollector(doc)
                    .OfClass(typeof(Material)).Cast<Material>()
                    .GroupBy(m => m.Name)
                    .ToDictionary(g => g.Key, g => g.First().Id);

                foreach (var trade in trades)
                {
                    if (selectedTradeSet.Count > 0 && !selectedTradeSet.Contains(trade.Label))
                    {
                        skip++;
                        continue;
                    }

                    foreach (var rule in trade.Rules)
                    {
                        ProcessRule(doc, view, trade, rule, bipMap, matMap,
                            solidFillId, solidLineId, fillPatternMap, linePatternMap,
                            existingFilters, existingViewFilterIds,
                            createOnly, overwriteDef,
                            ref pass, ref fail, ref skip, ref reused, ref rulesDone, totalRules);
                    }
                }

                tx.Commit();
            }

            // Persist updated manifest so future runs know which filters we own
            if (createOnly)
            {
                AutoFiltersSettings.Instance.CreatedFilterNames = expectedFilterNames.ToList();
                AutoFiltersSettings.Instance.Save();
            }

            string removeMsg = removed > 0 ? $", {removed} removed" : "";
            Log($"Complete — {pass} created, {reused} reused, {fail} failed, {skip} skipped{removeMsg}.", "pass");
            pass += reused;
        }

        private void ProcessRule(
            Document doc, View view,
            FilterTradeConfig trade, FilterRuleConfig rule,
            Dictionary<string, BuiltInParameter> bipMap,
            Dictionary<string, ElementId> matMap,
            ElementId solidFillId, ElementId solidLineId,
            Dictionary<string, ElementId> fillPatternMap,
            Dictionary<string, ElementId> linePatternMap,
            Dictionary<string, ParameterFilterElement> existingFilters,
            HashSet<long> existingViewFilterIds,
            bool createOnly, bool overwriteDef,
            ref int pass, ref int fail, ref int skip, ref int reused,
            ref int rulesDone, int totalRules)
        {
            // V3: each rule owns its own BuiltInCategories and Parameter
            var catIds = new List<ElementId>();
            foreach (var bicStr in rule.BuiltInCategories ?? new List<string>())
            {
                if (Enum.TryParse<BuiltInCategory>(bicStr, false, out var bic))
                    catIds.Add(new ElementId((long)(int)bic));
            }

            if (catIds.Count == 0)
            {
                Log($"[{trade.Id}/{rule.Name}] No BuiltInCategory resolved — skipped.", "info");
                skip++;
                if (rule.Enabled && (rule.MatchType == "all" || rule.Match.Count > 0)) rulesDone++;
                return;
            }

            ElementId? paramId = ResolveParamId(doc, rule.Parameter, catIds, bipMap);
            if (paramId == null)
            {
                Log($"[{trade.Id}/{rule.Name}] Could not resolve parameter '{rule.Parameter}'.", "fail");
                fail++;
                if (rule.Enabled && (rule.MatchType == "all" || rule.Match.Count > 0)) rulesDone++;
                return;
            }

            bool isFab       = rule.Parameter == "Fabrication Service";
            bool isStructMat = rule.Parameter == "Structural Material";
            string matchType = (rule.MatchType ?? "contains").ToLowerInvariant();
            bool hasKeywords = rule.Match.Count > 0;

            if (!rule.Enabled || (!hasKeywords && matchType != "all"))
            {
                skip++;
                return;
            }

            string filterName = trade.Id + "_"
                + rule.Name.Trim().Replace(" ", "_").ToUpperInvariant();

            try
            {
                ParameterFilterElement? pfe = null;

                // Delete and recreate if definition has changed (or in CreateOnly mode)
                if (overwriteDef && existingFilters.TryGetValue(filterName, out var oldPfe))
                {
                    doc.Delete(oldPfe.Id);
                    existingFilters.Remove(filterName);
                    existingViewFilterIds.Remove(oldPfe.Id.Value);
                }

                if (!overwriteDef && existingFilters.TryGetValue(filterName, out pfe))
                {
                    reused++;

                    // If keeping existing overrides, don't re-apply them
                    if (KeepExistingOverrides)
                    {
                        rulesDone++;
                        Progress(5 + (int)(rulesDone * 90.0 / totalRules), pass, fail, skip);
                        return;
                    }
                }
                else if (pfe == null)
                {
                    // Build element filter based on matchType
                    ElementFilter? elementFilter = BuildElementFilter(
                        paramId!, rule, isFab, isStructMat, matMap, doc, matchType);

                    if (elementFilter == null)
                    {
                        Log($"Skip '{filterName}': could not build filter rule.", "info");
                        skip++; rulesDone++;
                        return;
                    }

                    pfe = ParameterFilterElement.Create(doc, filterName, catIds, elementFilter);
                    existingFilters[filterName] = pfe;
                    pass++;
                }

                if (!createOnly)
                {
                    if (!existingViewFilterIds.Contains(pfe!.Id.Value))
                    {
                        view.AddFilter(pfe.Id);
                        existingViewFilterIds.Add(pfe.Id.Value);
                    }

                    // Apply graphic overrides (colors, line style, halftone, transparency)
                    ApplyRuleOverride(view, pfe.Id, rule, solidFillId, solidLineId, fillPatternMap, linePatternMap);
                }
            }
            catch (Exception ex)
            {
                Log($"Error '{filterName}': {ex.Message}", "fail");
                fail++;
            }

            rulesDone++;
            Progress(5 + (int)(rulesDone * 90.0 / totalRules), pass, fail, skip);
        }

        // ── Build an ElementFilter from a rule config ─────────────────────────
        private static ElementFilter? BuildElementFilter(
            ElementId paramId, FilterRuleConfig ruleConf,
            bool isFab, bool isStructMat,
            Dictionary<string, ElementId> matMap, Document doc,
            string matchType)
        {
            if (matchType == "all")
            {
                // Matches every element that has this parameter (any value)
                var hasValueRule = ParameterFilterRuleFactory.CreateHasValueParameterRule(paramId);
                return new ElementParameterFilter(hasValueRule);
            }

            if (ruleConf.Match.Count == 1)
            {
                var singleRule = BuildRuleForKeyword(
                    paramId, ruleConf.Match[0], isFab, isStructMat, matMap, doc, matchType);
                if (singleRule == null) return null;
                return new ElementParameterFilter(singleRule);
            }

            var subFilters = ruleConf.Match
                .Select(kw => BuildRuleForKeyword(paramId, kw, isFab, isStructMat, matMap, doc, matchType))
                .Where(r => r != null)
                .Select(r => (ElementFilter)new ElementParameterFilter(r!))
                .ToList();

            if (subFilters.Count == 0) return null;
            return subFilters.Count == 1 ? subFilters[0] : new LogicalOrFilter(subFilters);
        }

        // ── Build a single Revit FilterRule for one keyword ───────────────────
        private static FilterRule? BuildRuleForKeyword(
            ElementId paramId, string keyword,
            bool isFab, bool isStructMat,
            Dictionary<string, ElementId> matMap, Document doc,
            string matchType)
        {
            if (isStructMat)
            {
                if (matMap.TryGetValue(keyword, out ElementId mid))
                    return ParameterFilterRuleFactory.CreateEqualsRule(paramId, mid);
                return null;
            }

            switch (matchType)
            {
                case "equals":
                    return ParameterFilterRuleFactory.CreateEqualsRule(paramId, keyword);
                default: // "contains"
                    return ParameterFilterRuleFactory.CreateContainsRule(paramId, keyword);
            }
        }

        // ── Apply graphic override from a FilterRuleConfig ────────────────────
        //
        // rule.Visible controls whether matched elements are shown or hidden in the view.
        // This is the ONLY call to SetFilterVisibility — there is no separate FilterOn call,
        // which previously caused a race condition where the visibility was set twice.
        //
        private static void ApplyRuleOverride(View view, ElementId filterId,
            FilterRuleConfig rule,
            ElementId solidFillId, ElementId solidLineId,
            Dictionary<string, ElementId> fillPatternMap,
            Dictionary<string, ElementId> linePatternMap)
        {
            var ogs = new OverrideGraphicSettings();

            RevitColor cutColor  = HexToRevitColor(rule.CutColor)  ?? new RevitColor(128, 128, 128);
            RevitColor surfColor = HexToRevitColor(rule.SurfColor) ?? cutColor;
            RevitColor lineColor = HexToRevitColor(rule.LineColor) ?? cutColor;

            if (rule.OverrideSurf)
            {
                // Resolve the surface fill pattern by name; fall back to solid fill.
                ElementId surfFillId = ResolvePatternId(rule.SurfPattern, fillPatternMap, solidFillId);
                if (surfFillId != ElementId.InvalidElementId)
                {
                    ogs.SetSurfaceForegroundPatternId(surfFillId);
                    ogs.SetSurfaceForegroundPatternColor(surfColor);
                    ogs.SetSurfaceForegroundPatternVisible(true);
                }
            }

            if (rule.OverrideCut)
            {
                // Resolve the cut fill pattern by name; fall back to solid fill.
                ElementId cutFillId = ResolvePatternId(rule.CutPattern, fillPatternMap, solidFillId);
                if (cutFillId != ElementId.InvalidElementId)
                {
                    ogs.SetCutForegroundPatternId(cutFillId);
                    ogs.SetCutForegroundPatternColor(cutColor);
                    ogs.SetCutForegroundPatternVisible(true);
                }
            }

            if (rule.OverrideLine)
            {
                ogs.SetProjectionLineColor(lineColor);
                ogs.SetProjectionLineWeight(rule.LineWeight);
                ogs.SetCutLineColor(lineColor);
                ogs.SetCutLineWeight(rule.LineWeight);

                // Apply named line pattern; "Solid" or empty uses the built-in solid pattern.
                ElementId linePatId = ResolveLinePatternId(rule.LinePattern, linePatternMap, solidLineId);
                if (linePatId != ElementId.InvalidElementId)
                {
                    ogs.SetProjectionLinePatternId(linePatId);
                    ogs.SetCutLinePatternId(linePatId);
                }
            }

            if (rule.Halftone)
                ogs.SetHalftone(true);

            if (rule.Transparency > 0)
                ogs.SetSurfaceTransparency(Math.Max(0, Math.Min(100, rule.Transparency)));

            view.SetFilterOverrides(filterId, ogs);

            // Single visibility call: rule.Visible = true means show elements, false means hide.
            view.SetFilterVisibility(filterId, rule.Visible);
        }

        /// <summary>
        /// Resolves a fill pattern name to its <see cref="ElementId"/>.
        /// Returns <paramref name="fallbackId"/> when the name is blank or not found.
        /// </summary>
        private static ElementId ResolvePatternId(
            string? name, Dictionary<string, ElementId> map, ElementId fallbackId)
        {
            if (!string.IsNullOrWhiteSpace(name) &&
                map.TryGetValue(name, out ElementId id))
                return id;
            return fallbackId;
        }

        /// <summary>
        /// Resolves a line pattern name to its <see cref="ElementId"/>.
        /// "Solid" or blank returns the built-in solid line pattern id.
        /// </summary>
        private static ElementId ResolveLinePatternId(
            string? name, Dictionary<string, ElementId> map, ElementId solidLineId)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                string.Equals(name, "Solid", System.StringComparison.OrdinalIgnoreCase))
                return solidLineId;
            if (map.TryGetValue(name, out ElementId id))
                return id;
            return solidLineId;
        }

        private static RevitColor? HexToRevitColor(string hex)
        {
            try
            {
                hex = (hex ?? "").TrimStart('#');
                if (hex.Length == 6 && int.TryParse(hex,
                    System.Globalization.NumberStyles.HexNumber, null, out int v))
                    return new RevitColor((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
            }
            catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters: parse colour hex", __lex); }
            return null;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static ElementId? ResolveParamId(Document doc, string paramName,
            IList<ElementId> catIds, Dictionary<string, BuiltInParameter> bipMap)
        {
            // 1. BIP map first — works even when no elements exist in the model
            if (bipMap.TryGetValue(paramName, out var bip))
            {
                try { return new ElementId((long)(int)bip); } catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters: resolve built-in parameter id", __lex); }
            }

            // 2. Scan live elements in the category
            foreach (var catId in catIds)
            {
                var el = new FilteredElementCollector(doc)
                    .OfCategoryId(catId).WhereElementIsNotElementType().FirstElement();
                if (el == null) continue;
                var p = el.LookupParameter(paramName);
                if (p != null) return p.Id;
                foreach (Parameter pp in el.Parameters)
                {
                    try { if (pp.Definition.Name == paramName) return pp.Id; } catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters: match project parameter name", __lex); }
                }
            }

            // 3. Try matching a BuiltInParameter by name as a last resort
            if (Enum.TryParse<BuiltInParameter>(
                    paramName.Replace(" ", "_").ToUpperInvariant(), out var fallbackBip))
            {
                try { return new ElementId((long)(int)fallbackBip); } catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters: resolve fallback parameter id", __lex); }
            }

            return null;
        }

        private static string? ReadParamValue(Element el, string paramName,
            Dictionary<string, BuiltInParameter> bipMap)
        {
            if (paramName == "Fabrication Service")
            {
                try
                {
                    var sn = el.GetType().GetProperty("ServiceName")?.GetValue(el) as string;
                    if (!string.IsNullOrEmpty(sn)) return sn;
                }
                catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters: read fabrication ServiceName", __lex); }
            }
            if (paramName == "Type Name")
            {
                try
                {
                    var t = el.Document.GetElement(el.GetTypeId());
                    if (t != null && !string.IsNullOrEmpty(t.Name)) return t.Name;
                }
                catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters: read element type name", __lex); }
                try
                {
                    var p = el.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_NAME);
                    if (p != null) return p.AsString() ?? p.AsValueString();
                }
                catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters: read type-name parameter", __lex); }
                return null;
            }
            if (paramName == "Structural Material")
            {
                try
                {
                    var p = el.LookupParameter(paramName);
                    if (p != null)
                    {
                        var mid = p.AsElementId();
                        if (mid != null && mid != ElementId.InvalidElementId)
                        {
                            var mat = el.Document.GetElement(mid) as Material;
                            if (mat != null) return mat.Name;
                        }
                    }
                }
                catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters: read material name", __lex); }
                return null;
            }
            try
            {
                var p = el.LookupParameter(paramName);
                if (p != null)
                {
                    string s = p.AsValueString();
                    if (!string.IsNullOrEmpty(s)) return s;
                    s = p.AsString();
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters: read parameter value", __lex); }
            if (bipMap.TryGetValue(paramName, out var bipFallback))
            {
                try
                {
                    var p = el.get_Parameter(bipFallback);
                    if (p != null) return p.AsValueString() ?? p.AsString();
                }
                catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters: read fallback parameter value", __lex); }
            }
            return null;
        }

        private static void ConfigureFailures(Transaction tx)
        {
            var opts = tx.GetFailureHandlingOptions();
            opts.SetClearAfterRollback(true);
            opts.SetDelayedMiniWarnings(true);
            tx.SetFailureHandlingOptions(opts);
        }

        private static ElementId GetSolidFillId(Document doc)
        {
            foreach (FillPatternElement fp in
                new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>())
            {
                if (fp.GetFillPattern().IsSolidFill) return fp.Id;
            }
            return ElementId.InvalidElementId;
        }

        private static ElementId GetSolidLineId()
        {
            try { return LinePatternElement.GetSolidPatternId(); }
            catch { return ElementId.InvalidElementId; }
        }

        private void Log(string text, string status) => PushLog?.Invoke(text, status);
        private void Progress(int pct, int p, int f, int s) => OnProgress?.Invoke(pct, p, f, s);
        private void Complete(int p, int f, int s, int r = 0) => OnComplete?.Invoke(p, f, s, r);
    }
}
