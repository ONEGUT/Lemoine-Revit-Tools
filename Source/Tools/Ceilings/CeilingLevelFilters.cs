using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Framework;
using LemoineTools.Tools.AutoFilters;

namespace LemoineTools.Tools.Ceilings
{
    /// <summary>
    /// Builds and applies the "Ceiling Levels" (CL) filter set: one
    /// <see cref="ParameterFilterElement"/> per level in the document, matching ceilings on
    /// that level, so a per-level ceiling plan can be isolated to its OWN level's ceilings by
    /// showing its filter and hiding every other level's.
    ///
    /// This replaces relying on a view's view range to decide which ceilings appear. A view
    /// range is a vertical band, and a ceiling belongs to a level by its Level PARAMETER, not
    /// by where it happens to sit in space — a bulkhead dropped well below its level, or a
    /// double-height space's ceiling running past the level above, lands on the wrong side of
    /// any band you pick.
    ///
    /// Registered as an <c>ExternallyManaged</c> Auto Filters trade, exactly like the Ceiling
    /// Heatmap's CH trade and Make Ceiling Grids' CG trade, so the rules appear in the rules
    /// list and group correctly while the generic "Create Filters" engine never tries to
    /// regenerate them.
    ///
    /// LINKED CEILINGS — the hard part. "Level" is an ElementId-valued parameter, so a host
    /// filter holding the host's level id is not expected to match a ceiling inside a link,
    /// whose level is a different element in a different document. That matters here more than
    /// usual: in this project ceilings typically live in linked architectural models. Each
    /// rule is therefore built as a <see cref="LogicalOrFilter"/> over the host level id PLUS
    /// the id of the equivalent level found in every loaded link, so the same filter can match
    /// both. If Revit rejects a foreign document's ElementId the build falls back to a
    /// host-only rule and says so in the run log rather than shipping a filter that silently
    /// matches nothing.
    ///
    /// UNVERIFIED: the OR-of-link-level-ids behaviour cannot be executed on Linux and needs a
    /// Windows/Revit run to confirm. Tag Ceilings' level check is the independent safety net —
    /// it reads each ceiling's own level through the API and reports anything still visible
    /// that does not belong to the view's level.
    /// </summary>
    internal static class CeilingLevelFilters
    {
        internal const string TradeId    = "CL";
        private  const string TradeLabel = "Ceiling Levels";
        private  const string TradeColor = "#8C6BD8";

        /// <summary>How close two levels' world elevations must be to be treated as the same
        /// level when their names differ, in feet (1/8 in). Only a fallback — names are the
        /// primary match, because a link positioned by shared coordinates can carry a small
        /// vertical offset that a name comparison is immune to.</summary>
        private const double ElevationMatchTolFt = 0.01;

        /// <summary>
        /// Candidate "which level does this ceiling belong to" parameters, in preference order.
        ///
        /// Which of these a document will actually accept in a view filter is NOT the same
        /// question as which one reads a ceiling's level. <c>LEVEL_PARAM</c> reads correctly
        /// (that is what <c>CeilingRegionSource</c> uses), but a parameter that reads fine can
        /// still be rejected by <c>ParameterFilterElement.Create</c> as not applicable to the
        /// filter's categories — the same trap already documented for
        /// <c>ELEM_CATEGORY_PARAM</c>. So the parameter is resolved against
        /// <c>ParameterFilterUtilities.GetFilterableParametersInCommon</c> at run time instead
        /// of being hardcoded, and the first candidate the document reports as filterable wins.
        /// </summary>
        private static readonly BuiltInParameter[] LevelParamCandidates =
        {
            BuiltInParameter.LEVEL_PARAM,
            BuiltInParameter.SCHEDULE_LEVEL_PARAM,
            BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM,
            BuiltInParameter.FAMILY_LEVEL_PARAM,
        };

        /// <summary>One level's filter, plus what went into it.</summary>
        internal sealed class LevelFilter
        {
            public ElementId LevelId   { get; set; } = ElementId.InvalidElementId;
            public string    LevelName { get; set; } = "";
            public string    FilterName { get; set; } = "";
            public ElementId FilterId  { get; set; } = ElementId.InvalidElementId;
            /// <summary>How many loaded links contributed an equivalent level id to the rule.</summary>
            public int       LinkedLevelsMatched { get; set; }
            /// <summary>True when the OR-of-link-ids rule was rejected and the rule fell back to
            /// matching the host level id alone — linked ceilings will not be isolated.</summary>
            public bool      HostOnly  { get; set; }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Build
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates or updates one filter per level in <paramref name="doc"/>, keyed by level id.
        /// Opens its own transaction. Existing filters are reused by name and rewritten in place
        /// with <c>SetElementFilter</c> — never deleted and recreated, so a filter already
        /// assigned to views keeps its ElementId and those assignments survive (and the rule
        /// picks up any link that has been loaded since the last run).
        /// </summary>
        internal static Dictionary<long, LevelFilter> EnsureAll(Document doc, Action<string, string> log)
        {
            var result = new Dictionary<long, LevelFilter>();

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            if (levels.Count == 0)
            {
                // A zero-result collector is indistinguishable from a broken one unless it says so.
                log(AppStrings.T("ceilings.levelFilters.log.noLevels"), "warn");
                return result;
            }

            List<LinkLevels> links = CollectLinkLevels(doc, log);

            var ceilingCatId = new ElementId(BuiltInCategory.OST_Ceilings);

            // Ask the document which level parameters it will accept in a ceiling filter, rather
            // than assuming LEVEL_PARAM. A wrong choice here fails EVERY level identically —
            // which is exactly how this presented: four "could not build" lines and zero filters.
            List<ElementId> paramCandidates = ResolveLevelParameters(doc, ceilingCatId, log);
            if (paramCandidates.Count == 0)
                return result;   // ResolveLevelParameters already said why, in detail

            // The candidate currently in use. If the first one is refused on a real write, the
            // remaining candidates are tried and whichever works is adopted for the rest of the
            // run — the filterable-parameter list says what MAY be bound, not what Create will
            // accept for these categories, so the write is the only authoritative test.
            ElementId levelParamId  = paramCandidates[0];
            bool      paramConfirmed = false;

            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .GroupBy(f => f.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            // Two levels whose names sanitise to the same filter name would otherwise share one
            // filter, and the second would silently inherit the first's rule.
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int created = 0, reused = 0, hostOnly = 0;
            // Counted over every level ATTEMPTED, not just the ones whose filter was written.
            // Deriving this from the success list made a total write failure report itself as
            // "no link level matched", pointing the user at their link's level names when the
            // names had in fact matched and the rule write was the thing that failed.
            int levelsWithLinkMatch = 0;

            using (var tx = new Transaction(doc, "Ceiling Level Filters"))
            {
                ConfigureFailures(tx);
                tx.Start();

                foreach (Level level in levels)
                {
                    string filterName = AutoFiltersSettings.MakeFilterName(TradeId, level.Name);
                    if (!usedNames.Add(filterName))
                    {
                        string baseName = filterName;
                        int n = 2;
                        while (!usedNames.Add(filterName = $"{baseName}_{n}")) n++;
                    }

                    var entry = new LevelFilter
                    {
                        LevelId    = level.Id,
                        LevelName  = level.Name,
                        FilterName = filterName,
                    };

                    // Host level id, plus the equivalent level in each loaded link.
                    var ids = new List<ElementId> { level.Id };
                    var seenIds = new HashSet<long> { level.Id.Value };
                    foreach (LinkLevels lk in links)
                    {
                        ElementId? match = FindEquivalentLevel(lk, level);
                        if (match is null) continue;
                        // Deduped by raw value: the rule can only compare ids, so two documents
                        // sharing an id value are covered by one entry either way.
                        if (!seenIds.Add(match.Value)) continue;
                        ids.Add(match);
                        entry.LinkedLevelsMatched++;
                    }

                    if (entry.LinkedLevelsMatched > 0) levelsWithLinkMatch++;

                    ParameterFilterElement? pfe = existing.TryGetValue(filterName, out var found) ? found : null;

                    if (!TryWriteFilter(doc, ref pfe, filterName, ceilingCatId, levelParamId,
                                        ids, out string orError))
                    {
                        // Before blaming the linked ids, check whether the PARAMETER is the
                        // problem: a parameter the document lists as filterable can still be
                        // refused for these categories, and that refusal looks identical to a
                        // rejected link id. Only worth doing until one candidate is proven.
                        if (!paramConfirmed
                            && TrySwitchParameter(doc, ref pfe, filterName, ceilingCatId,
                                                  paramCandidates, ids, ref levelParamId, out orError))
                        {
                            paramConfirmed = true;
                            log(AppStrings.T("ceilings.levelFilters.log.parameterSwitched",
                                             SafeLabel(levelParamId)), "info");
                        }
                        else
                        {
                            // The OR-of-link-ids rule was rejected — retry with the host level
                            // alone so the view still isolates host ceilings rather than getting
                            // no filter at all.
                            // levelsWithLinkMatch is deliberately NOT decremented here: it
                            // reports whether the link's level names matched the host's, which
                            // they did. That the rule was then refused is a separate fact, and
                            // the hostOnly warning below is what carries it.
                            entry.HostOnly = true;
                            entry.LinkedLevelsMatched = 0;

                            if (!TryWriteFilter(doc, ref pfe, filterName, ceilingCatId, levelParamId,
                                                new List<ElementId> { level.Id }, out string hostError))
                            {
                                // Report the reason, not just the fact. Without the message the
                                // user sees an unexplained refusal and the cause lives only in
                                // diagnostics.log — the run log has to carry it.
                                log(AppStrings.T("ceilings.levelFilters.log.filterFailed",
                                                 level.Name, hostError), "fail");
                                continue;
                            }
                            DiagnosticsLog.Info("CeilingLevelFilters",
                                $"level '{level.Name}': OR-of-link-ids rule rejected ({orError}); "
                                + "fell back to the host level id alone.");
                            hostOnly++;
                        }
                    }
                    else paramConfirmed = true;

                    if (pfe == null) continue;

                    if (found == null) created++; else reused++;
                    existing[filterName] = pfe;

                    entry.FilterId = pfe.Id;
                    result[level.Id.Value] = entry;
                }

                tx.Commit();
            }

            log(AppStrings.T("ceilings.levelFilters.log.built", result.Count, created, reused), "info");

            if (links.Count > 0)
            {
                // Reported from the MATCHING pass, not the write results. A level whose name
                // matched a link's level did match, whether or not its filter could then be
                // written — conflating the two blamed the link's level names for a rule the
                // document had rejected for an unrelated reason.
                if (levelsWithLinkMatch > 0)
                    log(AppStrings.T("ceilings.levelFilters.log.linkedMatched",
                                     levelsWithLinkMatch, links.Count), "info");
                else
                    log(AppStrings.T("ceilings.levelFilters.log.linkedNoMatch", links.Count), "warn");
            }
            if (hostOnly > 0)
                log(AppStrings.T("ceilings.levelFilters.log.hostOnly", hostOnly), "warn");

            // Every level failed to produce a filter. Say so as one headline rather than
            // leaving the user to infer it from N per-level lines plus a "0 level(s)" count.
            if (result.Count == 0 && levels.Count > 0)
                log(AppStrings.T("ceilings.levelFilters.log.allFailed", levels.Count), "fail");

            return result;
        }

        /// <summary>Creates or rewrites one filter. Returns false when Revit rejects the rule,
        /// leaving <paramref name="pfe"/> untouched so the caller can retry with a simpler one.
        /// <paramref name="error"/> carries Revit's own message so the caller can put the reason
        /// in the run log instead of only in diagnostics.log.</summary>
        private static bool TryWriteFilter(
            Document doc, ref ParameterFilterElement? pfe, string filterName,
            ElementId ceilingCatId, ElementId levelParamId, List<ElementId> levelIds,
            out string error)
        {
            error = "";
            try
            {
                var parts = levelIds
                    .Select(id => (ElementFilter)new ElementParameterFilter(
                        ParameterFilterRuleFactory.CreateEqualsRule(levelParamId, id)))
                    .ToList();

                ElementFilter ef = parts.Count == 1 ? parts[0] : new LogicalOrFilter(parts);

                if (pfe != null) pfe.SetElementFilter(ef);
                else pfe = ParameterFilterElement.Create(
                    doc, filterName, new List<ElementId> { ceilingCatId }, ef);

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                DiagnosticsLog.Swallowed(
                    $"CeilingLevelFilters: write rule for '{filterName}' with {levelIds.Count} level id(s)", ex);
                return false;
            }
        }

        /// <summary>
        /// The level parameters this document will accept in a ceiling view filter, most
        /// preferred first. Empty when it accepts none of them.
        ///
        /// Filterability is a property of the category schema, not of the elements present, so
        /// this is answerable without a single ceiling in the model. It is necessary but not
        /// sufficient — a listed parameter can still be refused by
        /// <c>ParameterFilterElement.Create</c> for these categories — which is why the caller
        /// keeps the whole ordered list and lets a real write settle it.
        ///
        /// When nothing matches, the log names every level-ish parameter the document DOES
        /// offer for ceilings, turning an unexplained refusal into a concrete next step.
        /// </summary>
        private static List<ElementId> ResolveLevelParameters(
            Document doc, ElementId ceilingCatId, Action<string, string> log)
        {
            ICollection<ElementId> filterable;
            try
            {
                filterable = ParameterFilterUtilities.GetFilterableParametersInCommon(
                    doc, new List<ElementId> { ceilingCatId });
            }
            catch (Exception ex)
            {
                // Be permissive on a query failure — same posture as AutoFilters'
                // NarrowToFilterable. Hand back every candidate and let the write attempts be
                // the judge, rather than building nothing because a diagnostic call failed.
                DiagnosticsLog.Swallowed("CeilingLevelFilters: query filterable ceiling parameters", ex);
                return LevelParamCandidates.Select(b => new ElementId(b)).ToList();
            }

            var available = new HashSet<long>(filterable.Select(id => id.Value));
            var usable = LevelParamCandidates
                .Where(b => available.Contains(new ElementId(b).Value))
                .Select(b => new ElementId(b))
                .ToList();

            if (usable.Count > 0)
            {
                DiagnosticsLog.Info("CeilingLevelFilters",
                    $"filterable level parameter(s) for OST_Ceilings, in preference order: "
                    + string.Join(", ", usable.Select(id => SafeLabel(id)))
                    + $" (of {available.Count} filterable ceiling parameter(s)).");
                return usable;
            }

            // Nothing matched — name what IS on offer so the next run is conclusive.
            var levelish = new List<string>();
            foreach (ElementId id in filterable)
            {
                if (id.Value >= 0) continue;   // only built-ins carry a stable label
                string label = SafeLabel(id);
                if (label.IndexOf("level", StringComparison.OrdinalIgnoreCase) >= 0)
                    levelish.Add(label);
            }

            string offered = levelish.Count > 0
                ? string.Join(", ", levelish.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s))
                : AppStrings.T("ceilings.levelFilters.log.noneOffered");

            log(AppStrings.T("ceilings.levelFilters.log.noLevelParameter", offered), "fail");
            DiagnosticsLog.Warn("CeilingLevelFilters",
                $"none of [{string.Join(", ", LevelParamCandidates)}] is filterable for OST_Ceilings; "
                + $"level-ish filterable parameters offered: {offered}");
            return new List<ElementId>();
        }

        /// <summary>
        /// Retries the write with each remaining candidate parameter. On success
        /// <paramref name="levelParamId"/> is updated to the parameter that worked, so the rest
        /// of the run uses it directly instead of re-failing on every level.
        /// </summary>
        private static bool TrySwitchParameter(
            Document doc, ref ParameterFilterElement? pfe, string filterName,
            ElementId ceilingCatId, List<ElementId> candidates, List<ElementId> levelIds,
            ref ElementId levelParamId, out string error)
        {
            error = "";
            foreach (ElementId candidate in candidates)
            {
                if (candidate.Value == levelParamId.Value) continue;   // already failed

                if (TryWriteFilter(doc, ref pfe, filterName, ceilingCatId, candidate,
                                   levelIds, out string candidateError))
                {
                    DiagnosticsLog.Info("CeilingLevelFilters",
                        $"switched level parameter to '{SafeLabel(candidate)}' after the previous "
                        + $"candidate was refused; using it for the rest of the run.");
                    levelParamId = candidate;
                    return true;
                }
                error = candidateError;
            }
            return false;
        }

        private static string SafeLabel(ElementId paramId)
        {
            if (paramId.Value >= 0) return paramId.Value.ToString();
            return SafeLabel((BuiltInParameter)paramId.Value);
        }

        private static string SafeLabel(BuiltInParameter bip)
        {
            try { return LabelUtils.GetLabelFor(bip) ?? bip.ToString(); }
            catch (Exception ex)
            {
                // A parameter with no localized label is not an error worth failing over.
                DiagnosticsLog.Swallowed($"CeilingLevelFilters: label for {bip}", ex);
                return bip.ToString();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Apply
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Isolates each view to its own level: every level filter is added to the view, the
        /// view's own level's filter is left visible and all the others are hidden. Opens its
        /// own transaction.
        ///
        /// A ceiling carrying NO level matches no filter and therefore stays visible — that is
        /// deliberate (hiding it would need a rule that cannot be written), and Tag Ceilings'
        /// level check reports it.
        /// </summary>
        internal static void ApplyIsolation(
            Document doc,
            IEnumerable<(ElementId ViewId, ElementId LevelId)> views,
            Dictionary<long, LevelFilter> filters,
            Action<string, string> log)
        {
            if (filters.Count == 0) return;

            int applied = 0, refused = 0, skipped = 0;

            using (var tx = new Transaction(doc, "Isolate Ceilings by Level"))
            {
                ConfigureFailures(tx);
                tx.Start();

                foreach (var (viewId, levelId) in views)
                {
                    if (!(doc.GetElement(viewId) is View view)) continue;

                    // No level means NO filter would be the view's own, so every level would be
                    // hidden and the plan would come out blank. Refuse rather than empty it.
                    if (levelId is null || levelId == ElementId.InvalidElementId)
                    {
                        skipped++;
                        log(AppStrings.T("ceilings.levelFilters.log.viewNoLevel", view.Name), "warn");
                        continue;
                    }

                    bool ok = true;
                    string firstError = "";
                    var have = new HashSet<long>(view.GetFilters().Select(id => id.Value));

                    foreach (LevelFilter lf in filters.Values)
                    {
                        if (lf.FilterId == ElementId.InvalidElementId) continue;
                        bool isOwnLevel = lf.LevelId.Value == levelId.Value;

                        try
                        {
                            if (have.Add(lf.FilterId.Value)) view.AddFilter(lf.FilterId);
                            view.SetFilterVisibility(lf.FilterId, isOwnLevel);
                        }
                        catch (Exception ex)
                        {
                            // A view whose filters are governed by a view template refuses both
                            // calls — the template owns them. Report it once per view naming the
                            // template, rather than one identical error per level.
                            ok = false;
                            if (firstError.Length == 0) firstError = ex.Message;
                            DiagnosticsLog.Swallowed(
                                $"CeilingLevelFilters: apply '{lf.FilterName}' to view '{view.Name}'", ex);
                        }
                    }

                    if (ok) applied++;
                    else
                    {
                        refused++;
                        string template = TemplateNameOf(doc, view);
                        log(template.Length > 0
                                ? AppStrings.T("ceilings.levelFilters.log.viewTemplateBlocked", view.Name, template)
                                : AppStrings.T("ceilings.levelFilters.log.viewRefused", view.Name, firstError),
                            "warn");
                    }
                }

                tx.Commit();
            }

            if (applied > 0)
                log(AppStrings.T("ceilings.levelFilters.log.isolated", applied, filters.Count), "info");
            if (refused > 0)
                log(AppStrings.T("ceilings.levelFilters.log.isolatedRefused", refused), "warn");
            if (applied == 0 && refused == 0 && skipped == 0)
                log(AppStrings.T("ceilings.levelFilters.log.isolatedNone"), "warn");
        }

        private static string TemplateNameOf(Document doc, View view)
        {
            try
            {
                if (view.ViewTemplateId == ElementId.InvalidElementId) return "";
                return (doc.GetElement(view.ViewTemplateId) as View)?.Name ?? "";
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("CeilingLevelFilters: read view template name", ex);
                return "";
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Trade registration
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Mirrors the level filters into the "Ceiling Levels" trade so they appear (grouped) in
        /// the rules list. Rebuilt every run so the rules always match the document's levels.
        ///
        /// The stored rule carries <c>Visible = true</c> — "these are the ceilings on this
        /// level". Per-view visibility is the isolation decision and belongs to the view, not the
        /// rule: the same filter is shown in its own level's plan and hidden in every other one,
        /// which a single stored flag cannot express.
        /// </summary>
        internal static void RegisterTrade(IEnumerable<LevelFilter> levelFilters, Action<string, string> log)
        {
            try
            {
                var settings = AutoFiltersSettings.Instance;

                var trade = settings.Trades.FirstOrDefault(
                    t => string.Equals(t.Id, TradeId, StringComparison.OrdinalIgnoreCase));
                if (trade == null)
                {
                    trade = new FilterTradeConfig { Id = TradeId };
                    settings.Trades.Add(trade);
                }

                trade.Label             = TradeLabel;
                trade.Color             = TradeColor;
                trade.ExternallyManaged = true;

                trade.Rules.Clear();
                foreach (LevelFilter lf in levelFilters)
                {
                    var rule = FilterRuleConfig.NewBlank();
                    rule.Name              = lf.LevelName;
                    rule.Enabled           = true;
                    rule.Parameter         = "Level";
                    rule.BuiltInCategories = new List<string> { "OST_Ceilings" };
                    rule.MatchType         = "equals";
                    rule.Match             = new List<string> { lf.LevelName };
                    rule.Visible           = true;
                    rule.FilterOn          = true;
                    rule.OverrideCut       = false;
                    rule.OverrideSurf      = false;
                    rule.OverrideLine      = false;
                    rule.Notes             = "Auto-generated by the ceiling tools. Matches ceilings on this "
                                           + "level; each per-level ceiling plan shows its own level's filter "
                                           + "and hides the rest. Managed by the ceiling tools.";
                    trade.Rules.Add(rule);
                }

                settings.Save();
                log(AppStrings.T("ceilings.levelFilters.log.tradeRegistered", TradeLabel, trade.Rules.Count), "info");
            }
            catch (Exception ex)
            {
                // Non-fatal: the Revit filters were already created and applied. Surface the
                // failure so the rules-list sync issue isn't hidden.
                DiagnosticsLog.Error("CeilingLevelFilters: register Ceiling Levels trade", ex);
                log(AppStrings.T("ceilings.levelFilters.log.rulesUpdateFailed", ex.Message), "fail");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Link levels
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>One loaded link's levels, with the transform that puts them in world feet.</summary>
        private sealed class LinkLevels
        {
            public string      Name    { get; set; } = "";
            public List<Level> Levels  { get; } = new List<Level>();
            /// <summary>World Z of each level, index-matched to <see cref="Levels"/>.</summary>
            public List<double> WorldZ { get; } = new List<double>();
        }

        private static List<LinkLevels> CollectLinkLevels(Document doc, Action<string, string> log)
        {
            var result = new List<LinkLevels>();
            // Distinct link DOCUMENTS, not instances — two instances of one file carry the same
            // level elements, so the second contributes nothing but duplicate work.
            var seenDocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (RevitLinkInstance link in new FilteredElementCollector(doc)
                         .OfClass(typeof(RevitLinkInstance))
                         .Cast<RevitLinkInstance>())
            {
                Document? linkDoc;
                Transform xform;
                try
                {
                    linkDoc = link.GetLinkDocument();
                    if (linkDoc == null) continue;              // unloaded link — nothing to read
                    if (!seenDocs.Add(linkDoc.PathName ?? link.Name)) continue;
                    xform = link.GetTotalTransform();
                }
                catch (Exception ex)
                {
                    // A link that cannot even be opened contributes no levels, so its ceilings
                    // will not be isolated — the user has to be told which link, not just see a
                    // smaller "matched" count later.
                    DiagnosticsLog.Swallowed($"CeilingLevelFilters: read link '{link.Name}'", ex);
                    log(AppStrings.T("ceilings.levelFilters.log.linkUnreadable", link.Name, ex.Message), "warn");
                    continue;
                }

                var entry = new LinkLevels { Name = link.Name };
                try
                {
                    foreach (Level lv in new FilteredElementCollector(linkDoc)
                                 .OfClass(typeof(Level)).Cast<Level>())
                    {
                        entry.Levels.Add(lv);
                        entry.WorldZ.Add(xform.OfPoint(new XYZ(0, 0, lv.Elevation)).Z);
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed($"CeilingLevelFilters: read levels of link '{link.Name}'", ex);
                    log(AppStrings.T("ceilings.levelFilters.log.linkUnreadable", link.Name, ex.Message), "warn");
                    continue;
                }

                if (entry.Levels.Count > 0) result.Add(entry);
                else log(AppStrings.T("ceilings.levelFilters.log.linkNoLevels", link.Name), "warn");
            }

            return result;
        }

        /// <summary>The link level equivalent to <paramref name="hostLevel"/>: same name, or
        /// failing that the same world elevation. Null when the link has neither.</summary>
        private static ElementId? FindEquivalentLevel(LinkLevels link, Level hostLevel)
        {
            string want = (hostLevel.Name ?? "").Trim();
            for (int i = 0; i < link.Levels.Count; i++)
            {
                if (string.Equals((link.Levels[i].Name ?? "").Trim(), want, StringComparison.OrdinalIgnoreCase))
                    return link.Levels[i].Id;
            }

            for (int i = 0; i < link.Levels.Count; i++)
            {
                if (Math.Abs(link.WorldZ[i] - hostLevel.Elevation) <= ElevationMatchTolFt)
                    return link.Levels[i].Id;
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
    }
}
