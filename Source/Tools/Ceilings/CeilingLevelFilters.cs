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

            var ceilingCatId  = new ElementId(BuiltInCategory.OST_Ceilings);
            var levelParamId  = new ElementId(BuiltInParameter.LEVEL_PARAM);

            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .GroupBy(f => f.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            // Two levels whose names sanitise to the same filter name would otherwise share one
            // filter, and the second would silently inherit the first's rule.
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int created = 0, reused = 0, hostOnly = 0;

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

                    ParameterFilterElement? pfe = existing.TryGetValue(filterName, out var found) ? found : null;

                    if (!TryWriteFilter(doc, ref pfe, filterName, ceilingCatId, levelParamId, ids, log))
                    {
                        // The OR-of-link-ids rule was rejected — retry with the host level alone
                        // so the view still isolates host ceilings instead of getting no filter.
                        entry.HostOnly = true;
                        entry.LinkedLevelsMatched = 0;
                        if (!TryWriteFilter(doc, ref pfe, filterName, ceilingCatId, levelParamId,
                                            new List<ElementId> { level.Id }, log))
                        {
                            log(AppStrings.T("ceilings.levelFilters.log.filterFailed", level.Name), "fail");
                            continue;
                        }
                        hostOnly++;
                    }

                    if (pfe == null) continue;

                    if (found == null) created++; else reused++;
                    existing[filterName] = pfe;

                    entry.FilterId = pfe.Id;
                    result[level.Id.Value] = entry;
                }

                tx.Commit();
            }

            log(AppStrings.T("ceilings.levelFilters.log.built", result.Count, created, reused), "info");

            int withLinks = result.Values.Count(e => e.LinkedLevelsMatched > 0);
            if (links.Count > 0)
            {
                // Saying this out loud is the difference between "linked ceilings are isolated"
                // and "the filter quietly matched nothing in the links".
                if (withLinks > 0)
                    log(AppStrings.T("ceilings.levelFilters.log.linkedMatched", withLinks, links.Count), "info");
                else
                    log(AppStrings.T("ceilings.levelFilters.log.linkedNoMatch", links.Count), "warn");
            }
            if (hostOnly > 0)
                log(AppStrings.T("ceilings.levelFilters.log.hostOnly", hostOnly), "warn");

            return result;
        }

        /// <summary>Creates or rewrites one filter. Returns false when Revit rejects the rule,
        /// leaving <paramref name="pfe"/> untouched so the caller can retry with a simpler one.</summary>
        private static bool TryWriteFilter(
            Document doc, ref ParameterFilterElement? pfe, string filterName,
            ElementId ceilingCatId, ElementId levelParamId, List<ElementId> levelIds,
            Action<string, string> log)
        {
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
                DiagnosticsLog.Swallowed(
                    $"CeilingLevelFilters: write rule for '{filterName}' with {levelIds.Count} level id(s)", ex);
                return false;
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
