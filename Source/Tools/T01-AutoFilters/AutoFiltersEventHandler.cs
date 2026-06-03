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
                ["System Name"]           = BuiltInParameter.RBS_SYSTEM_NAME_PARAM,
                ["Fabrication Service"]   = BuiltInParameter.FABRICATION_SERVICE_NAME,
                ["Type Name"]             = BuiltInParameter.ALL_MODEL_TYPE_NAME,
                ["Family Name"]           = BuiltInParameter.ELEM_FAMILY_PARAM,
                ["Structural Material"]   = BuiltInParameter.STRUCTURAL_MATERIAL_PARAM,
                ["Mark"]                  = BuiltInParameter.ALL_MODEL_MARK,
                ["Comments"]              = BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,
            };

            Progress(5, pass, fail, skip);

            // ── Count total enabled rules to size progress bar ────────────────
            int totalRules = trades.Sum(t =>
                (!t.ExternallyManaged
                 && (selectedTradeSet.Count == 0 || selectedTradeSet.Contains(t.Label)))
                    ? t.Rules.Count(r => r.Enabled && RuleProducesFilter(r))
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
                    if (t.ExternallyManaged) continue;
                    foreach (var r in t.Rules)
                    {
                        if (!r.Enabled || !RuleProducesFilter(r)) continue;
                        expectedFilterNames.Add(AutoFiltersSettings.MakeFilterName(t.Id, r.Name));
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

                // Resolved-parameter cache: avoids re-scanning links for the same
                // (parameter × category-set) across many rules.
                var paramCache = new Dictionary<string, ParamResolve>(StringComparer.Ordinal);

                foreach (var trade in trades)
                {
                    // Externally-managed trades (e.g. Ceiling Heatmap) use non-keyword
                    // matching and are maintained by their own tool — never regenerate here.
                    if (trade.ExternallyManaged) continue;

                    if (selectedTradeSet.Count > 0 && !selectedTradeSet.Contains(trade.Label))
                    {
                        skip++;
                        continue;
                    }

                    foreach (var rule in trade.Rules)
                    {
                        ProcessRule(doc, sourceDocs, view, trade, rule, bipMap, matMap, paramCache,
                            solidFillId, solidLineId, fillPatternMap, linePatternMap,
                            existingFilters, existingViewFilterIds,
                            createOnly, overwriteDef,
                            ref pass, ref fail, ref skip, ref reused, ref rulesDone, totalRules);
                    }
                }

                tx.Commit();
            }

            // Persist updated manifest so future runs know which filters we own.
            // Record only filters that actually exist in the document now — a rule
            // whose creation failed must not be claimed as owned (it would otherwise
            // never be cleaned up as an orphan).
            if (createOnly)
            {
                AutoFiltersSettings.Instance.CreatedFilterNames =
                    expectedFilterNames.Where(existingFilters.ContainsKey).ToList();
                AutoFiltersSettings.Instance.Save();
            }

            string removeMsg = removed > 0 ? $", {removed} removed" : "";
            Log($"Complete — {pass} created, {reused} reused, {fail} failed, {skip} skipped{removeMsg}.", "pass");
            pass += reused;
        }

        // A rule yields a ParameterFilterElement when it matches the whole category,
        // uses a value predicate (has/has-no value), or has at least one keyword.
        private static bool RuleProducesFilter(FilterRuleConfig r)
        {
            string mt = (r.MatchType ?? "contains").ToLowerInvariant();
            return mt == "all" || mt == "has a value" || mt == "has no value" || r.Match.Count > 0;
        }

        private void ProcessRule(
            Document doc, IList<Document> sourceDocs, View view,
            FilterTradeConfig trade, FilterRuleConfig rule,
            Dictionary<string, BuiltInParameter> bipMap,
            Dictionary<string, ElementId> matMap,
            Dictionary<string, ParamResolve> paramCache,
            ElementId solidFillId, ElementId solidLineId,
            Dictionary<string, ElementId> fillPatternMap,
            Dictionary<string, ElementId> linePatternMap,
            Dictionary<string, ParameterFilterElement> existingFilters,
            HashSet<long> existingViewFilterIds,
            bool createOnly, bool overwriteDef,
            ref int pass, ref int fail, ref int skip, ref int reused,
            ref int rulesDone, int totalRules)
        {
            string matchType   = (rule.MatchType ?? "contains").ToLowerInvariant();
            bool   wholeCat     = matchType == "all";
            bool   valuePredicate = matchType == "has a value" || matchType == "has no value";
            bool   hasKeywords  = rule.Match.Count > 0;

            // ── Gate first: an intentionally-disabled or empty rule is a SKIP, never a
            //    failure. Resolve nothing for it (previously a disabled rule with an
            //    unresolvable parameter was wrongly counted as a failure). ──────────
            if (!rule.Enabled || (!wholeCat && !valuePredicate && !hasKeywords))
            {
                skip++;
                return;
            }

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
                skip++; rulesDone++;
                return;
            }

            // Whole-category ("all") matches every element via the always-present
            // Category parameter, so it needs no per-trade parameter resolution.
            ElementId? paramId = null;
            if (!wholeCat)
            {
                var pr = ResolveParamId(doc, sourceDocs, rule.Parameter, catIds, bipMap, paramCache);
                if (pr.Id == null)
                {
                    Log($"[{trade.Id}/{rule.Name}] Could not resolve parameter '{rule.Parameter}'.", "fail");
                    fail++; rulesDone++;
                    return;
                }
                paramId = pr.Id;
                // Tell the user when a rule's parameter cannot reliably drive a filter
                // over linked elements — the most common "filter created but does nothing".
                if (pr.Warn != null)
                    Log($"[{trade.Id}/{rule.Name}] {pr.Warn}", "info");
            }

            bool isFab       = rule.Parameter == "Fabrication Service";
            bool isStructMat = rule.Parameter == "Structural Material";

            string filterName = AutoFiltersSettings.MakeFilterName(trade.Id, rule.Name);

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
                        paramId, rule, isFab, isStructMat, matMap, doc, matchType);

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

                    // Honor the rule's "filter active in view" flag (FilterOn). A disabled
                    // filter stays attached to the view but has no effect.
                    view.SetIsFilterEnabled(pfe.Id, rule.FilterOn);

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

        // Match types whose keyword rules are negative (element must NOT match).
        // Multiple negative keywords combine with AND ("not A AND not B"); positive
        // keywords combine with OR ("A OR B").
        private static bool IsNegativeMatch(string matchType) =>
            matchType == "does not contain" || matchType == "does not equal";

        // ── Build an ElementFilter from a rule config ─────────────────────────
        private static ElementFilter? BuildElementFilter(
            ElementId? paramId, FilterRuleConfig ruleConf,
            bool isFab, bool isStructMat,
            Dictionary<string, ElementId> matMap, Document doc,
            string matchType)
        {
            // Whole category — match every element in the filter's categories.
            // Uses the Category parameter (present on every element) so no per-trade
            // parameter resolution is required and link-only categories still work.
            if (matchType == "all")
            {
                var catParam = new ElementId((long)(int)BuiltInParameter.ELEM_CATEGORY_PARAM);
                return new ElementParameterFilter(
                    ParameterFilterRuleFactory.CreateHasValueParameterRule(catParam));
            }

            if (paramId == null) return null;

            // Parameter-level predicates ignore keywords entirely.
            if (matchType == "has a value")
                return new ElementParameterFilter(
                    ParameterFilterRuleFactory.CreateHasValueParameterRule(paramId));
            if (matchType == "has no value")
                return new ElementParameterFilter(
                    ParameterFilterRuleFactory.CreateHasNoValueParameterRule(paramId));

            var subFilters = ruleConf.Match
                .Select(kw => BuildRuleForKeyword(paramId, kw, isFab, isStructMat, matMap, doc, matchType))
                .Where(r => r != null)
                .Select(r => (ElementFilter)new ElementParameterFilter(r!))
                .ToList();

            if (subFilters.Count == 0) return null;
            if (subFilters.Count == 1) return subFilters[0];
            return IsNegativeMatch(matchType)
                ? (ElementFilter)new LogicalAndFilter(subFilters)
                : new LogicalOrFilter(subFilters);
        }

        // ── Build a single Revit FilterRule for one keyword ───────────────────
        private static FilterRule? BuildRuleForKeyword(
            ElementId paramId, string keyword,
            bool isFab, bool isStructMat,
            Dictionary<string, ElementId> matMap, Document doc,
            string matchType)
        {
            // Structural Material matches an element id, so only equals/not-equals apply.
            if (isStructMat)
            {
                if (!matMap.TryGetValue(keyword, out ElementId mid)) return null;
                return matchType == "does not equal"
                    ? ParameterFilterRuleFactory.CreateNotEqualsRule(paramId, mid)
                    : ParameterFilterRuleFactory.CreateEqualsRule(paramId, mid);
            }

            switch (matchType)
            {
                case "equals":           return ParameterFilterRuleFactory.CreateEqualsRule(paramId, keyword);
                case "does not equal":   return ParameterFilterRuleFactory.CreateNotEqualsRule(paramId, keyword);
                case "does not contain": return ParameterFilterRuleFactory.CreateNotContainsRule(paramId, keyword);
                case "begins with":      return ParameterFilterRuleFactory.CreateBeginsWithRule(paramId, keyword);
                case "ends with":        return ParameterFilterRuleFactory.CreateEndsWithRule(paramId, keyword);
                default:                 return ParameterFilterRuleFactory.CreateContainsRule(paramId, keyword);
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

        /// <summary>
        /// Result of resolving a rule's parameter to a filterable id, plus whether that
        /// parameter reliably matches elements inside linked models.
        /// </summary>
        private readonly struct ParamResolve
        {
            public readonly ElementId? Id;
            public readonly bool       LinkSafe;
            public readonly string?    Warn;
            public ParamResolve(ElementId? id, bool linkSafe, string? warn)
            { Id = id; LinkSafe = linkSafe; Warn = warn; }
            public static readonly ParamResolve None = new ParamResolve(null, false, null);
        }

        // Cached wrapper around ResolveParamIdCore keyed by parameter × category-set.
        private static ParamResolve ResolveParamId(
            Document host, IList<Document> sourceDocs, string paramName,
            IList<ElementId> catIds, Dictionary<string, BuiltInParameter> bipMap,
            Dictionary<string, ParamResolve> cache)
        {
            string key = paramName + "|" +
                string.Join(",", catIds.Select(c => c.Value).OrderBy(v => v));
            if (cache.TryGetValue(key, out var hit)) return hit;
            var res = ResolveParamIdCore(host, sourceDocs, paramName, catIds, bipMap);
            cache[key] = res;
            return res;
        }

        private static ParamResolve ResolveParamIdCore(
            Document host, IList<Document> sourceDocs, string paramName,
            IList<ElementId> catIds, Dictionary<string, BuiltInParameter> bipMap)
        {
            // 1. Built-in parameter — universal id, matches host and linked elements alike.
            if (bipMap.TryGetValue(paramName, out var bip))
            {
                try { return new ParamResolve(new ElementId((long)(int)bip), true, null); }
                catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters: resolve built-in parameter id", __lex); }
            }

            // 1b. "System Type" maps to a category-specific BuiltInParameter (duct vs pipe),
            //     so pick the one matching the rule's categories. Resolvable with no elements.
            if (string.Equals(paramName, "System Type", StringComparison.OrdinalIgnoreCase))
            {
                var stBip = ResolveSystemTypeBip(catIds);
                if (stBip != null)
                    try { return new ParamResolve(new ElementId((long)(int)stBip.Value), true, null); }
                    catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters: resolve system-type parameter id", __lex); }
            }

            // 2. Host-side SHARED parameter. Shared parameters match across documents by
            //    GUID, so resolving the host-valid id here is the reliable way to filter
            //    linked elements even when the host has no elements of the category.
            foreach (SharedParameterElement spe in new FilteredElementCollector(host)
                .OfClass(typeof(SharedParameterElement)).Cast<SharedParameterElement>())
            {
                string defName;
                try { defName = spe.GetDefinition()?.Name ?? ""; }
                catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters: read host shared-parameter definition", __lex); continue; }
                if (string.Equals(defName, paramName, StringComparison.Ordinal))
                    return new ParamResolve(spe.Id, true, null);
            }

            // 3. Live elements across host + selected links (instance → type → loop).
            foreach (var src in sourceDocs)
            {
                bool isLink = !src.Equals(host);
                foreach (var catId in catIds)
                {
                    var el = new FilteredElementCollector(src)
                        .OfCategoryId(catId).WhereElementIsNotElementType().FirstElement();
                    if (el == null) continue;

                    var found = MatchParamOnElement(src, el, paramName);
                    if (found != null) return ClassifyScannedParam(found, paramName, isLink);
                }
            }

            // 4. Last resort: built-in parameter by mangled name.
            if (Enum.TryParse<BuiltInParameter>(
                    paramName.Replace(" ", "_").ToUpperInvariant(), out var fbBip))
            {
                try { return new ParamResolve(new ElementId((long)(int)fbBip), true, null); }
                catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters: resolve fallback parameter id", __lex); }
            }

            return ParamResolve.None;
        }

        // Finds a parameter by display name on an element: instance first, then the
        // element's type (#1 — many identity/MEP params live on the type), then a
        // definition-name scan of the instance's parameters.
        private static Parameter? MatchParamOnElement(Document src, Element el, string paramName)
        {
            var p = el.LookupParameter(paramName);
            if (p != null) return p;

            var typeEl = src.GetElement(el.GetTypeId());
            if (typeEl != null)
            {
                var tp = typeEl.LookupParameter(paramName);
                if (tp != null) return tp;
            }

            foreach (Parameter pp in el.Parameters)
            {
                try { if (pp.Definition?.Name == paramName) return pp; }
                catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters: match instance parameter name", __lex); }
            }
            return null;
        }

        // Classifies a parameter discovered by scanning elements and attaches a warning
        // when it cannot reliably drive a filter over linked models.
        private static ParamResolve ClassifyScannedParam(Parameter p, string paramName, bool isLink)
        {
            bool shared;
            try { shared = p.IsShared; }
            catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters: read parameter IsShared", __lex); shared = false; }

            if (shared)
                return new ParamResolve(p.Id, true, isLink
                    ? $"⚠ '{paramName}' was found only in a link — make sure the same shared parameter exists in the host so the filter binds to linked elements."
                    : null);

            return new ParamResolve(p.Id, false,
                $"⚠ '{paramName}' is a project parameter — view filters cannot match it on linked elements. Use a built-in/shared parameter or turn on 'Whole category'.");
        }

        // "System Type" is RBS_DUCT_SYSTEM_TYPE_PARAM for duct categories and
        // RBS_PIPING_SYSTEM_TYPE_PARAM for pipe categories. Choose by category so the
        // parameter resolves without any live elements in the host document.
        private static BuiltInParameter? ResolveSystemTypeBip(IList<ElementId> catIds)
        {
            bool anyPipe = false, anyDuct = false;
            foreach (var catId in catIds)
            {
                var bic = (BuiltInCategory)(int)catId.Value;
                switch (bic)
                {
                    case BuiltInCategory.OST_DuctCurves:
                    case BuiltInCategory.OST_DuctFitting:
                    case BuiltInCategory.OST_DuctAccessory:
                    case BuiltInCategory.OST_DuctTerminal:
                    case BuiltInCategory.OST_FlexDuctCurves:
                        anyDuct = true; break;
                    case BuiltInCategory.OST_PipeCurves:
                    case BuiltInCategory.OST_PipeFitting:
                    case BuiltInCategory.OST_PipeAccessory:
                    case BuiltInCategory.OST_FlexPipeCurves:
                    case BuiltInCategory.OST_Sprinklers:
                        anyPipe = true; break;
                }
            }
            if (anyDuct) return BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM;
            if (anyPipe) return BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM;
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
