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

        /// <summary>
        /// In <see cref="CreateOnly"/> mode, restricts which existing filters have their
        /// definition refreshed to the filters named in this set — every other existing filter
        /// is reused untouched, preserving rule edits made outside the Auto Filters menu (e.g.
        /// in Revit's own filter editor). <see langword="null"/> (the default) refreshes every
        /// filter, matching the historical behaviour for callers that don't track which rules
        /// changed. Has no effect outside CreateOnly mode, where <see cref="OverwriteFilterDefinition"/>
        /// alone governs refresh. Brand-new filters are always created regardless of this set.
        /// </summary>
        public HashSet<string>? ChangedFilterNames { get; set; } = null;

        /// <summary>
        /// When true, a Revit TaskDialog summarising any filter-creation failures is shown
        /// at the end of <see cref="Execute"/>. Used by the auto-create-on-close path, where
        /// the originating window has already closed and cannot surface failures itself.
        /// </summary>
        public bool ShowFailureDialog { get; set; } = false;

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?        PushLog    { get; set; }
        public Action<int, int, int, int>?    OnProgress { get; set; }
        /// <summary>Invoked on Revit's main thread with (pass, fail, skip, removed).</summary>
        public Action<int, int, int, int>?    OnComplete { get; set; }

        // Per-run accumulation of failure messages, surfaced via the TaskDialog when
        // ShowFailureDialog is set. Cleared at the start of every Execute().
        private readonly List<string> _failures = new List<string>();

        // Lazily-built once per run: every view in the document, reused across all
        // RebuildFilter calls so several rule rebuilds in one run don't each re-enumerate
        // the whole document. Filter rebuilds create no views, so this stays valid for the
        // run. Reset at the start of every Run().
        private List<View>? _allViewsCache;

        private List<View> AllViews(Document doc)
        {
            if (_allViewsCache == null)
                _allViewsCache = new FilteredElementCollector(doc)
                    .OfClass(typeof(View)).Cast<View>().ToList();
            return _allViewsCache;
        }

        public string GetName() => "LemoineTools.Tools.AutoFilters.AutoFiltersEventHandler";

        public void Execute(UIApplication app)
        {
            var doc  = app.ActiveUIDocument.Document;
            var view = doc.ActiveView;
            int pass = 0, fail = 0, skip = 0, removed = 0;
            _failures.Clear();

            try
            {
                Run(app, doc, view, ref pass, ref fail, ref skip, ref removed);
            }
            catch (Exception ex)
            {
                LemoineLog.Error("AutoFilters: run aborted", ex); Log($"Error: {ex.Message}", "fail");
                fail++;
            }
            finally
            {
                // Session-long static handler — the cache holds live View objects; drop it
                // (and the run's inputs) so they don't outlive the run.
                _allViewsCache      = null;
                SelectedLinkTitles  = new List<string>();
                SelectedDisciplines = new List<string>();
            }

            Progress(100, pass, fail, skip);
            Complete(pass, fail, skip, removed);

            if (ShowFailureDialog && fail > 0)
                ShowFailureSummary(fail);
        }

        // Surfaces a run's per-filter failures as a Revit TaskDialog. Runs on Revit's main
        // thread (inside Execute), so it is safe even though the originating window — opened
        // on its own STA thread — has already closed by the time the auto-create pass runs.
        private void ShowFailureSummary(int fail)
        {
            try
            {
                var dlg = new TaskDialog("Auto Filters")
                {
                    MainInstruction = fail == 1
                        ? "1 filter could not be created."
                        : $"{fail} filters could not be created.",
                    MainContent     = "The remaining filters were created or left unchanged.",
                    ExpandedContent = string.Join("\n", _failures),
                    CommonButtons   = TaskDialogCommonButtons.Close,
                };
                dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                    "Open diagnostics log", "Shows the full per-rule failure detail.");
                if (dlg.Show() == TaskDialogResult.CommandLink1)
                    LemoineLog.OpenInDefaultViewer();
            }
            catch (Exception ex)
            {
                // Never let a reporting popup take down the run — the failures are already
                // in the diagnostics log via Log().
                LemoineLog.Swallowed("AutoFilters: failure dialog", ex);
            }
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
            _allViewsCache = null; // rebuild the per-run view snapshot fresh

            bool createOnly   = CreateOnly;
            // createOnly no longer forces an overwrite. Existing filters are updated in place
            // (SetCategories / SetElementFilter) rather than deleted and recreated, so their
            // ElementIds — and therefore their view assignments and legend links — survive.
            bool overwriteDef = OverwriteFilterDefinition;

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
            var bipMap = BuildBipMap();

            Progress(5, pass, fail, skip);

            // ── Count total enabled rules to size progress bar ────────────────
            int totalRules = trades.Sum(t =>
                (!t.ExternallyManaged
                 && (selectedTradeSet.Count == 0 || selectedTradeSet.Contains(t.Label)))
                    ? t.Rules.Count(r => r.Enabled && AutoFiltersSettings.RuleProducesFilter(r))
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
            var expectedFilterNames = createOnly
                ? AutoFiltersSettings.ComputeExpectedFilterNames(trades)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                            if (LemoineRun.CancelRequested)
                            {
                                Log($"Stopped by user — {removed} orphan(s) removed; work so far preserved.", "warn");
                                break;
                            }

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

                bool cancelled = false;
                foreach (var trade in trades)
                {
                    if (cancelled) break;

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
                        if (LemoineRun.CancelRequested)
                        {
                            Log($"Stopped by user — {rulesDone} of {totalRules} rule(s) processed; work so far preserved.", "warn");
                            cancelled = true;
                            break;
                        }

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

            // Whole-category ("all") is a rule-less, categories-only filter, so it needs no
            // per-trade parameter resolution.
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

                // Narrow categories to the ones the parameter can actually filter.
                // ParameterFilterElement.Create requires the parameter to apply to EVERY
                // category, so a rule that lists even one incompatible category fails
                // entirely ("parameter does not apply to this filter's categories"). Creating
                // the filter for the compatible subset turns that failure into a working
                // filter. When the query reports NO compatible categories we leave catIds
                // untouched and let Create try anyway — the utility and Create occasionally
                // disagree, and falling through avoids regressing filters that work today.
                var filterableCats = NarrowToFilterable(doc, paramId!, catIds);
                if (filterableCats.Count > 0 && filterableCats.Count < catIds.Count)
                {
                    Log($"[{trade.Id}/{rule.Name}] Parameter '{rule.Parameter}' applies to {filterableCats.Count} of {catIds.Count} categories — creating filter for the compatible ones.", "info");
                    catIds = filterableCats;
                }
            }

            bool isFab       = rule.Parameter == "Fabrication Service";
            bool isStructMat = rule.Parameter == "Structural Material";

            string filterName = AutoFiltersSettings.MakeFilterName(trade.Id, rule.Name);

            // Refresh the definition of an existing filter (when the caller explicitly asked to
            // overwrite, or — in CreateOnly mode — when this rule's definition actually changed).
            // The refresh updates the element in place rather than deleting and recreating it, so
            // the filter keeps its ElementId and stays attached to any views and legends that
            // reference it. In CreateOnly mode a ChangedFilterNames set limits the refresh to the
            // rules the user changed in the menu; a filter not in the set is reused untouched, so
            // edits made to it outside the menu (e.g. in Revit's filter editor) are preserved.
            // A null ChangedFilterNames keeps the historical "refresh all" behaviour.
            bool refreshDef = overwriteDef
                || (createOnly && (ChangedFilterNames == null || ChangedFilterNames.Contains(filterName)));

            // When set, the filter already existed on this view and the caller asked to keep
            // manually-adjusted overrides: skip re-applying overrides to it. A filter that is
            // NOT yet on the view is still attached and styled (there is nothing to preserve).
            bool keepExistingForThis = false;

            try
            {
                // Whole-category rules ("all") match every element in their categories via a
                // RULE-LESS ParameterFilterElement — never a rule on the Category parameter.
                // ELEM_CATEGORY_PARAM is not a valid filter parameter for these category sets,
                // so a HasValue rule on it throws "parameter does not apply to this filter's
                // categories" and the filter is never created. A categories-only filter (the
                // 3-arg ParameterFilterElement.Create) is the canonical "everything in these
                // categories" filter and references no parameter. Every other match type
                // builds a rule-based filter as usual.
                ElementFilter? elementFilter = wholeCat
                    ? null
                    : BuildElementFilter(paramId, rule, isFab, isStructMat, matMap, doc, matchType);

                // A non-whole-category rule that yields no filter is a genuine build failure;
                // a whole-category rule legitimately has no element filter.
                bool buildFailed = !wholeCat && elementFilter == null;

                ParameterFilterElement? pfe;
                if (existingFilters.TryGetValue(filterName, out pfe))
                {
                    reused++;

                    if (refreshDef && !buildFailed)
                    {
                        // Refresh the existing filter in place where possible (preserves the
                        // ElementId, so view assignments and legend links survive). When an
                        // in-place edit can't yield the correct definition, rebuild it fresh.
                        if (wholeCat)
                        {
                            // Target is a rule-less filter. If this element still carries rules
                            // from a previous keyword definition, rebuild it so it truly matches
                            // the whole category — the rules can't be cleared in place. A filter
                            // that is already rule-less just gets its categories refreshed.
                            bool hasRules;
                            try { hasRules = pfe.GetElementFilter() != null; }
                            catch (Exception ex)
                            {
                                // Unknown state — don't risk churning a working rule-less filter.
                                LemoineLog.Swallowed("AutoFilters: read element filter", ex);
                                hasRules = false;
                            }

                            if (hasRules)
                                pfe = RebuildFilter(doc, filterName, catIds, null, pfe,
                                                    existingFilters, existingViewFilterIds);
                            else
                                pfe.SetCategories(catIds);
                        }
                        else
                        {
                            // Rule-based: update in place, but if the new categories/parameter
                            // are incompatible with the element's current state, rebuild it.
                            try
                            {
                                pfe.SetCategories(catIds);
                                pfe.SetElementFilter(elementFilter!);
                            }
                            catch (Exception ex)
                            {
                                LemoineLog.Swallowed("AutoFilters: in-place update incompatible, rebuilding", ex);
                                pfe = RebuildFilter(doc, filterName, catIds, elementFilter, pfe,
                                                    existingFilters, existingViewFilterIds);
                            }
                        }
                    }
                    else if (refreshDef)
                    {
                        // buildFailed — leave the existing definition untouched rather than
                        // break a working filter.
                        Log($"Kept '{filterName}': could not rebuild filter rule, left unchanged.", "info");
                    }
                    else if (KeepExistingOverrides)
                    {
                        // Reuse untouched. Don't return — the filter may not be on this view
                        // yet, in which case it must still be attached below. Override
                        // preservation is decided per-view in the apply block.
                        keepExistingForThis = true;
                    }
                }
                else
                {
                    if (buildFailed)
                    {
                        Log($"Skip '{filterName}': could not build filter rule.", "info");
                        skip++; rulesDone++;
                        return;
                    }

                    // Whole-category → rule-less filter (3-arg Create); otherwise rule-based.
                    pfe = elementFilter == null
                        ? ParameterFilterElement.Create(doc, filterName, catIds)
                        : ParameterFilterElement.Create(doc, filterName, catIds, elementFilter);
                    existingFilters[filterName] = pfe;
                    pass++;
                }

                if (!createOnly)
                {
                    bool wasOnView = existingViewFilterIds.Contains(pfe!.Id.Value);
                    if (!wasOnView)
                    {
                        view.AddFilter(pfe.Id);
                        existingViewFilterIds.Add(pfe.Id.Value);
                    }

                    // Preserve overrides only for a filter that was ALREADY on this view;
                    // a newly-attached one has nothing to preserve, so it is styled normally.
                    if (!(keepExistingForThis && wasOnView))
                    {
                        // Honor the rule's "filter active in view" flag (FilterOn). A disabled
                        // filter stays attached to the view but has no effect.
                        view.SetIsFilterEnabled(pfe.Id, rule.FilterOn);

                        // Apply graphic overrides (colors, line style, halftone, transparency)
                        ApplyRuleOverride(view, pfe.Id, rule, solidFillId, solidLineId, fillPatternMap, linePatternMap);
                    }
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

        // Deletes an existing filter and recreates it with the given categories and element
        // filter (null = rule-less whole-category). Used when an in-place edit can't produce
        // the correct definition (e.g. converting a keyword rule to whole-category, or an
        // incompatible category/parameter change). The recreated element has a new ElementId,
        // which would otherwise silently detach the filter from every view/template it was on
        // (the documented churn hazard) — including in the create-only pass, which skips the
        // active-view re-add block entirely. To stay transparent, capture each existing view
        // assignment (enabled state + graphic overrides) before deleting, then restore them
        // onto the new ElementId.
        private ParameterFilterElement RebuildFilter(
            Document doc, string filterName, ICollection<ElementId> catIds,
            ElementFilter? elementFilter, ParameterFilterElement old,
            Dictionary<string, ParameterFilterElement> existingFilters,
            HashSet<long> existingViewFilterIds)
        {
            bool activeHadFilter = existingViewFilterIds.Remove(old.Id.Value);
            var pfe = RebuildFilterStatic(doc, filterName, catIds, elementFilter, old,
                                          existingFilters, AllViews(doc));
            // Keep the active-view bookkeeping in sync so ProcessRule does not double-add.
            if (activeHadFilter) existingViewFilterIds.Add(pfe.Id.Value);
            return pfe;
        }

        // Assignment-preserving delete+recreate, shared by the create pass and the
        // apply-to-views refresh. Captures every view/template assignment (enabled state +
        // graphic overrides) before deleting, then restores them onto the new ElementId so the
        // churn is transparent.
        private static ParameterFilterElement RebuildFilterStatic(
            Document doc, string filterName, ICollection<ElementId> catIds,
            ElementFilter? elementFilter, ParameterFilterElement old,
            Dictionary<string, ParameterFilterElement> existingFilters,
            IReadOnlyList<View> allViews)
        {
            ElementId oldId = old.Id;

            var assignments = new List<(View View, bool Enabled, OverrideGraphicSettings Ogs)>();
            foreach (var v in allViews)
            {
                ICollection<ElementId> vFilters;
                try { vFilters = v.GetFilters(); }
                catch (Exception ex)
                {
                    LemoineLog.Swallowed($"AutoFilters: read filters on view {v.Id.Value}", ex);
                    continue;
                }
                if (!vFilters.Any(id => id.Value == oldId.Value)) continue;

                bool enabled;
                OverrideGraphicSettings ogs;
                try
                {
                    enabled = v.GetIsFilterEnabled(oldId);
                    ogs     = v.GetFilterOverrides(oldId);
                }
                catch (Exception ex)
                {
                    LemoineLog.Swallowed($"AutoFilters: read filter state on view {v.Id.Value}", ex);
                    enabled = true;
                    ogs     = new OverrideGraphicSettings();
                }
                assignments.Add((v, enabled, ogs));
            }

            doc.Delete(oldId);

            var pfe = elementFilter == null
                ? ParameterFilterElement.Create(doc, filterName, catIds)
                : ParameterFilterElement.Create(doc, filterName, catIds, elementFilter);
            existingFilters[filterName] = pfe;

            foreach (var a in assignments)
            {
                try
                {
                    a.View.AddFilter(pfe.Id);
                    a.View.SetIsFilterEnabled(pfe.Id, a.Enabled);
                    a.View.SetFilterOverrides(pfe.Id, a.Ogs);
                }
                catch (Exception ex)
                {
                    LemoineLog.Swallowed(
                        $"AutoFilters: restore filter '{filterName}' onto view {a.View.Id.Value}", ex);
                }
            }
            return pfe;
        }

        // ── Shared definition-refresh API (used by Apply Filters to Views) ─────
        //
        // Lets the standalone "Apply Filters to Views" tool rebuild a filter's DEFINITION
        // from its current owning rule before attaching it — so a filter created before a rule
        // was merged picks up ALL the merged keywords (not just the first). The definition is
        // updated in place (SetCategories / SetElementFilter) to preserve the ElementId; only an
        // incompatible change falls back to the assignment-preserving rebuild.

        /// <summary>Builds the parameter-display-name → BuiltInParameter map. Resolvable even
        /// when no element of the category exists in the model.</summary>
        internal static Dictionary<string, BuiltInParameter> BuildBipMap() =>
            new Dictionary<string, BuiltInParameter>(StringComparer.OrdinalIgnoreCase)
            {
                ["System Classification"] = BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM,
                ["System Name"]           = BuiltInParameter.RBS_SYSTEM_NAME_PARAM,
                ["Fabrication Service"]   = BuiltInParameter.FABRICATION_SERVICE_NAME,
                ["Type Name"]             = BuiltInParameter.ALL_MODEL_TYPE_NAME,
                // ALL_MODEL_FAMILY_NAME (String storage), NOT ELEM_FAMILY_PARAM (ElementId
                // storage): a String contains/equals filter rule cannot be built against an
                // ElementId parameter — the factory throws, the keyword is dropped, and the
                // filter is silently never created.
                ["Family Name"]           = BuiltInParameter.ALL_MODEL_FAMILY_NAME,
                ["Structural Material"]   = BuiltInParameter.STRUCTURAL_MATERIAL_PARAM,
                ["Mark"]                  = BuiltInParameter.ALL_MODEL_MARK,
                ["Comments"]              = BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,
            };

        /// <summary>
        /// Opaque, reusable context for a batch of <see cref="EnsureRuleFilterDefinition"/> calls.
        /// Holds the parameter cache, lookup maps, the existing-filter index and the view list so
        /// repeated refreshes in one transaction don't re-scan the document.
        /// </summary>
        internal sealed class FilterRefreshContext
        {
            internal readonly Document        Host;
            internal readonly IList<Document>  SourceDocs;
            internal readonly Dictionary<string, BuiltInParameter>          BipMap;
            internal readonly Dictionary<string, ElementId>                 MatMap;
            internal readonly Dictionary<string, ParameterFilterElement>    ExistingFilters;
            // Internal so the enclosing AutoFiltersEventHandler can read it via ctx.ParamCache
            // (a nested type's private members are not accessible to the enclosing type).
            internal readonly Dictionary<string, ParamResolve>             ParamCache =
                new Dictionary<string, ParamResolve>(StringComparer.Ordinal);
            internal readonly List<View> AllViews;

            internal FilterRefreshContext(
                Document host, IList<Document> sourceDocs,
                Dictionary<string, ElementId> matMap,
                Dictionary<string, ParameterFilterElement> existingFilters,
                List<View> allViews)
            {
                Host = host; SourceDocs = sourceDocs; MatMap = matMap;
                ExistingFilters = existingFilters; AllViews = allViews;
                BipMap = BuildBipMap();
            }
        }

        /// <summary>
        /// Builds a <see cref="FilterRefreshContext"/> for <paramref name="doc"/>, scanning the
        /// host plus the supplied <paramref name="sourceDocs"/> (loaded links) for parameter
        /// resolution. Must be called inside a transaction when its results feed
        /// <see cref="EnsureRuleFilterDefinition"/>.
        /// </summary>
        internal static FilterRefreshContext CreateRefreshContext(
            Document doc, IList<Document> sourceDocs)
        {
            var matMap = new FilteredElementCollector(doc)
                .OfClass(typeof(Material)).Cast<Material>()
                .GroupBy(m => m.Name)
                .ToDictionary(g => g.Key, g => g.First().Id);
            var existingFilters = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var allViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>().ToList();
            return new FilterRefreshContext(doc, sourceDocs, matMap, existingFilters, allViews);
        }

        /// <summary>
        /// Ensures the ParameterFilterElement for <paramref name="rule"/> exists and carries the
        /// rule's CURRENT definition (categories + parameter + keywords). Updates in place to keep
        /// the ElementId; rebuilds (assignment-preserving) only when an in-place edit can't yield
        /// the correct definition. Returns the element (existing, refreshed, or created), or
        /// <see langword="null"/> when the rule produces no filter or its parameter can't resolve.
        /// <paramref name="warn"/> carries a non-fatal advisory (e.g. link-safety) when present.
        /// </summary>
        internal static ParameterFilterElement? EnsureRuleFilterDefinition(
            FilterRefreshContext ctx, FilterTradeConfig trade, FilterRuleConfig rule, out string? warn)
        {
            warn = null;
            Document doc = ctx.Host;

            string matchType      = (rule.MatchType ?? "contains").ToLowerInvariant();
            bool   wholeCat       = matchType == "all";
            bool   valuePredicate = matchType == "has a value" || matchType == "has no value";
            bool   hasKeywords    = rule.Match.Count > 0;
            if (!rule.Enabled || (!wholeCat && !valuePredicate && !hasKeywords)) return null;

            var catIds = new List<ElementId>();
            foreach (var bicStr in rule.BuiltInCategories ?? new List<string>())
                if (Enum.TryParse<BuiltInCategory>(bicStr, false, out var bic))
                    catIds.Add(new ElementId((long)(int)bic));
            if (catIds.Count == 0) return null;

            ElementId? paramId = null;
            if (!wholeCat)
            {
                var pr = ResolveParamId(doc, ctx.SourceDocs, rule.Parameter, catIds, ctx.BipMap, ctx.ParamCache);
                if (pr.Id == null) { warn = $"Could not resolve parameter '{rule.Parameter}'."; return null; }
                paramId = pr.Id;
                warn = pr.Warn;
                var filterable = NarrowToFilterable(doc, paramId!, catIds);
                if (filterable.Count > 0 && filterable.Count < catIds.Count) catIds = filterable;
            }

            bool   isFab       = rule.Parameter == "Fabrication Service";
            bool   isStructMat = rule.Parameter == "Structural Material";
            string filterName  = AutoFiltersSettings.MakeFilterName(trade.Id, rule.Name);

            ElementFilter? elementFilter = wholeCat
                ? null
                : BuildElementFilter(paramId, rule, isFab, isStructMat, ctx.MatMap, doc, matchType);
            bool buildFailed = !wholeCat && elementFilter == null;

            ctx.ExistingFilters.TryGetValue(filterName, out var pfe);

            if (buildFailed)
            {
                // Don't break a working filter — leave the existing definition (if any) untouched.
                warn ??= "Could not build filter rule; left unchanged.";
                return pfe;
            }

            if (pfe != null)
            {
                if (wholeCat)
                {
                    bool hasRules;
                    try { hasRules = pfe.GetElementFilter() != null; }
                    catch (Exception ex)
                    {
                        LemoineLog.Swallowed("AutoFilters: read element filter (refresh)", ex);
                        hasRules = false;
                    }
                    if (hasRules)
                        pfe = RebuildFilterStatic(doc, filterName, catIds, null, pfe, ctx.ExistingFilters, ctx.AllViews);
                    else
                        pfe.SetCategories(catIds);
                }
                else
                {
                    try
                    {
                        pfe.SetCategories(catIds);
                        pfe.SetElementFilter(elementFilter!);
                    }
                    catch (Exception ex)
                    {
                        LemoineLog.Swallowed("AutoFilters: in-place refresh incompatible, rebuilding", ex);
                        pfe = RebuildFilterStatic(doc, filterName, catIds, elementFilter, pfe, ctx.ExistingFilters, ctx.AllViews);
                    }
                }
                return pfe;
            }

            pfe = elementFilter == null
                ? ParameterFilterElement.Create(doc, filterName, catIds)
                : ParameterFilterElement.Create(doc, filterName, catIds, elementFilter);
            ctx.ExistingFilters[filterName] = pfe;
            return pfe;
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
            // Note: whole-category ("all") rules never reach here — ProcessRule builds them as
            // rule-less ParameterFilterElements (no element filter), because a rule on the
            // Category parameter is rejected by Revit ("parameter does not apply to this
            // filter's categories").
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

            // String-based rules require StorageType.String. An ElementId parameter (e.g.
            // System Type, which stores a MEP system type ElementId) will throw from the
            // factory. Catch that here and return null so the caller logs a clean skip
            // instead of letting an opaque Revit exception propagate.
            try
            {
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
            catch (Exception __lex)
            {
                // Most likely cause: the parameter's StorageType doesn't support string comparison
                // (e.g. "System Type" is ElementId-based). Return null so BuildElementFilter
                // skips this keyword with a "could not build filter rule" message.
                LemoineLog.Swallowed($"AutoFilters: build string filter rule for '{keyword}'", __lex);
                return null;
            }
        }

        // ── Apply graphic override from a FilterRuleConfig ────────────────────
        //
        // rule.Visible controls whether matched elements are shown or hidden in the view.
        // This is the ONLY call to SetFilterVisibility — there is no separate FilterOn call,
        // which previously caused a race condition where the visibility was set twice.
        //
        internal static void ApplyRuleOverride(View view, ElementId filterId,
            FilterRuleConfig rule,
            ElementId solidFillId, ElementId solidLineId,
            Dictionary<string, ElementId> fillPatternMap,
            Dictionary<string, ElementId> linePatternMap)
        {
            var ogs = new OverrideGraphicSettings();

            RevitColor cutColor    = HexToRevitColor(rule.CutColor)    ?? new RevitColor(128, 128, 128);
            RevitColor surfColor   = HexToRevitColor(rule.SurfColor)   ?? cutColor;
            RevitColor lineColor   = HexToRevitColor(rule.LineColor)   ?? cutColor;
            RevitColor surfBgColor = HexToRevitColor(rule.SurfBgColor) ?? surfColor;
            RevitColor cutBgColor  = HexToRevitColor(rule.CutBgColor)  ?? cutColor;

            if (rule.OverrideSurf)
            {
                // Resolve the surface FOREGROUND fill pattern by name; fall back to solid fill.
                ElementId surfFillId = ResolvePatternId(rule.SurfPattern, fillPatternMap, solidFillId);
                if (surfFillId != ElementId.InvalidElementId)
                {
                    ogs.SetSurfaceForegroundPatternId(surfFillId);
                    ogs.SetSurfaceForegroundPatternColor(surfColor);
                    ogs.SetSurfaceForegroundPatternVisible(true);
                }
            }

            if (rule.OverrideSurfBg)
            {
                // Surface BACKGROUND is an independent layer (see CLAUDE.md foreground/background
                // note): set its own pattern id + colour + visibility.
                ElementId surfBgFillId = ResolvePatternId(rule.SurfBgPattern, fillPatternMap, solidFillId);
                if (surfBgFillId != ElementId.InvalidElementId)
                {
                    ogs.SetSurfaceBackgroundPatternId(surfBgFillId);
                    ogs.SetSurfaceBackgroundPatternColor(surfBgColor);
                    ogs.SetSurfaceBackgroundPatternVisible(true);
                }
            }

            if (rule.OverrideCut)
            {
                // Resolve the cut FOREGROUND fill pattern by name; fall back to solid fill.
                ElementId cutFillId = ResolvePatternId(rule.CutPattern, fillPatternMap, solidFillId);
                if (cutFillId != ElementId.InvalidElementId)
                {
                    ogs.SetCutForegroundPatternId(cutFillId);
                    ogs.SetCutForegroundPatternColor(cutColor);
                    ogs.SetCutForegroundPatternVisible(true);
                }
            }

            if (rule.OverrideCutBg)
            {
                // Cut BACKGROUND is an independent layer; set its own pattern id + colour + visibility.
                ElementId cutBgFillId = ResolvePatternId(rule.CutBgPattern, fillPatternMap, solidFillId);
                if (cutBgFillId != ElementId.InvalidElementId)
                {
                    ogs.SetCutBackgroundPatternId(cutBgFillId);
                    ogs.SetCutBackgroundPatternColor(cutBgColor);
                    ogs.SetCutBackgroundPatternVisible(true);
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
        internal readonly struct ParamResolve
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
                    if (found != null) return ResolveScannedToHost(host, found, paramName, isLink);
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

        // Maps a parameter discovered by scanning an element to a HOST-valid id.
        //
        // A FilterRule created in the host references its parameter by ElementId, and that
        // id must be valid in the HOST document. A built-in parameter's id is a universal
        // negative id, but a shared/project parameter has a different ElementId in every
        // document — returning the link's id produces "the referenced object is not valid,
        // possibly because it has been deleted" at ParameterFilterElement.Create.
        private static ParamResolve ResolveScannedToHost(Document host, Parameter p, string paramName, bool isLink)
        {
            // Built-in parameter — universal id, valid in host and links alike.
            try
            {
                if (p.Definition is InternalDefinition idef && idef.BuiltInParameter != BuiltInParameter.INVALID)
                    return new ParamResolve(new ElementId((long)(int)idef.BuiltInParameter), true, null);
            }
            catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters: read scanned built-in parameter", __lex); }

            bool shared;
            try { shared = p.IsShared; }
            catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters: read parameter IsShared", __lex); shared = false; }

            if (shared)
            {
                // Shared parameters bind across documents by GUID — re-resolve to the HOST's
                // SharedParameterElement so the filter references a host-valid id. If the host
                // never bound this shared parameter, a host filter cannot reference it at all.
                Guid g;
                try { g = p.GUID; }
                catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters: read scanned shared GUID", __lex); return ParamResolve.None; }

                var hostSpe = SharedParameterElement.Lookup(host, g);
                if (hostSpe != null)
                    return new ParamResolve(hostSpe.Id, true, null);

                return new ParamResolve(null, false,
                    $"⚠ '{paramName}' is a shared parameter present in a link but not bound in the host — add it to the host's shared-parameter bindings so the filter can match linked elements.");
            }

            // Non-shared project/family parameter: its id is document-specific.
            if (isLink)
                return new ParamResolve(null, false,
                    $"⚠ '{paramName}' is a project parameter that exists only in a link — host view filters cannot reference or match it on linked elements. Use a built-in/shared parameter or turn on 'Whole category'.");

            // Host project parameter: usable on host elements, but won't match linked ones.
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
                    case BuiltInCategory.OST_PlumbingFixtures:
                        anyPipe = true; break;
                }
            }
            if (anyDuct) return BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM;
            if (anyPipe) return BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM;
            return null;
        }

        // Returns the subset of catIds for which paramId is a valid filterable parameter.
        // Filterability is driven by category schema (not element presence), so this works
        // even when the host model contains no elements of the category.
        private static List<ElementId> NarrowToFilterable(
            Document doc, ElementId paramId, IList<ElementId> catIds)
        {
            var keep = new List<ElementId>();
            foreach (var cat in catIds)
            {
                try
                {
                    var common = ParameterFilterUtilities
                        .GetFilterableParametersInCommon(doc, new List<ElementId> { cat });
                    if (common.Contains(paramId)) keep.Add(cat);
                }
                catch (Exception __lex)
                {
                    // Be permissive on a query failure: keep the category so Create can
                    // still try, rather than silently dropping a possibly-valid category.
                    LemoineLog.Swallowed($"AutoFilters: filterable-parameter check for category {cat.Value}", __lex);
                    keep.Add(cat);
                }
            }
            return keep;
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
            catch (Exception ex)
            {
                LemoineLog.Swallowed("AutoFilters: resolve solid line pattern id", ex);
                return ElementId.InvalidElementId;
            }
        }

        // Routes a run message to the (optional) UI callback AND mirrors it to the
        // durable diagnostic log. The Filters window runs with PushLog == null, so
        // without this mirror every per-rule failure reason was silently discarded —
        // the user saw a "N failed" count but never *why*. "fail" maps to Warn (a
        // handled, non-fatal per-rule failure that still counts as an issue); every
        // other status is informational.
        private void Log(string text, string status)
        {
            PushLog?.Invoke(text, status);

            if (status == "fail")
            {
                _failures.Add(text);
                LemoineLog.Warn("AutoFilters", text);
            }
            else
                LemoineLog.Info("AutoFilters", text);
        }
        private void Progress(int pct, int p, int f, int s) => OnProgress?.Invoke(pct, p, f, s);
        private void Complete(int p, int f, int s, int r = 0) => OnComplete?.Invoke(p, f, s, r);
    }
}
