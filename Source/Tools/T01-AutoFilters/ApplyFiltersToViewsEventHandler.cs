using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Helpers;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.AutoFilters
{
    /// <summary>
    /// Applies selected ParameterFilterElements to one or more views,
    /// optionally setting color overrides from the MEP color map.
    /// </summary>
    public class ApplyFiltersToViewsEventHandler : IExternalEventHandler
    {
        // ── Set by ViewModel before Raise() ───────────────────────────────────
        public IList<string> SelectedFilterNames { get; set; } = new List<string>();
        // View ElementIds (as long) — NOT names. View names are not unique in Revit,
        // so targeting by name would apply a filter to every view sharing that name
        // (e.g. a Floor Plan and its RCP). The ViewModel resolves the user's picks to
        // ElementIds and passes them here.
        public IList<long>   SelectedViewIds     { get; set; } = new List<long>();
        public bool          OverwriteExisting   { get; set; } = false;
        public bool          ApplyColorOverrides { get; set; } = true;

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.ApplyFiltersToViewsEventHandler";

        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument.Document;
            int pass = 0, fail = 0, skip = 0;

            try
            {
                Apply(doc, ref pass, ref fail, ref skip);
            }
            catch (Exception ex)
            {
                LemoineLog.Error("AutoFilters: apply filters to views aborted", ex); Log(LemoineStrings.T("autofilters.applyFilters.log.error", ex.Message), "fail");
                fail++;
            }
            finally
            {
                // Session-long static handler — drop the run's payload.
                SelectedFilterNames = new List<string>();
                SelectedViewIds     = new List<long>();
            }

            Progress(100, pass, fail, skip);
            Complete(pass, fail, skip);
        }

        // ── Core logic ────────────────────────────────────────────────────────
        private void Apply(Document doc, ref int pass, ref int fail, ref int skip)
        {
            if (SelectedFilterNames.Count == 0 || SelectedViewIds.Count == 0)
            {
                Log(LemoineStrings.T("autofilters.applyFilters.log.noSelection"), "fail");
                fail++; return;
            }

            // Case-insensitive to match the OrdinalIgnoreCase rule-name comparison used elsewhere,
            // so a filter with differing stored casing is found. filterMap (name → id) is built
            // inside the transaction AFTER definitions are refreshed, because a refresh that has to
            // rebuild a filter mints a new ElementId.
            var selectedSet = new HashSet<string>(SelectedFilterNames, StringComparer.OrdinalIgnoreCase);

            // Resolve the selected view ElementIds directly — each id is one specific
            // view, so same-named views (Floor Plan vs RCP, duplicate 3D views) are
            // never conflated. Ids that no longer resolve (view deleted since launch)
            // or that can't take overrides are dropped and reported.
            var viewList = SelectedViewIds
                .Select(id => doc.GetElement(new ElementId(id)))
                .OfType<View>()
                .Where(v => !v.IsTemplate && v.AreGraphicsOverridesAllowed())
                .ToList();

            int droppedViews = SelectedViewIds.Count - viewList.Count;
            if (droppedViews > 0)
                Log(LemoineStrings.T("autofilters.applyFilters.log.droppedViews", droppedViews), "info");

            if (viewList.Count == 0)
            {
                Log(LemoineStrings.T("autofilters.applyFilters.log.noViewsSupport"), "fail");
                fail++; return;
            }

            // View filters only override linked elements when the link is displayed
            // "By Host View". Warn once per link shown "By Linked View" so the user knows
            // why a filter appears to do nothing on a linked model.
            WarnLinksNotByHostView(doc, viewList);

            ElementId solidFillId = GetSolidFillId(doc);
            ElementId solidLineId = GetSolidLineId();

            // Pattern lookup maps so a rule's named fill/line patterns resolve by name —
            // same maps the Create engine builds. Used only by ApplyRuleOverride below.
            var fillPatternMap = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .GroupBy(fp => fp.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
            var linePatternMap = new FilteredElementCollector(doc)
                .OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>()
                .GroupBy(lp => lp.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

            // Build filterName → owning (trade, rule) ONCE, instead of re-scanning every trade and
            // rule for each filter×view op. First rule to claim a name wins (config order). The
            // trade is kept so the definition refresh can rebuild the filter from its rule.
            var ruleByName = new Dictionary<string, (FilterTradeConfig Trade, FilterRuleConfig Rule)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var trade in AutoFiltersSettings.Instance.Trades ?? new List<FilterTradeConfig>())
                foreach (var rule in trade.Rules ?? new List<FilterRuleConfig>())
                {
                    string n = AutoFiltersSettings.MakeFilterName(trade.Id, rule.Name);
                    if (!ruleByName.ContainsKey(n)) ruleByName[n] = (trade, rule);
                }

            // Source docs for parameter resolution during the definition refresh: host plus every
            // loaded link (a link-only parameter still resolves to a host-valid id this way).
            var sourceDocs = new List<Document> { doc };
            foreach (RevitLinkInstance li in new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document ld = li.GetLinkDocument();
                if (ld != null && !sourceDocs.Any(d => d.Equals(ld))) sourceDocs.Add(ld);
            }

            int done     = 0;
            int lastPct  = -1;

            using (var tx = new Transaction(doc, "Apply Filters to Views"))
            {
                ConfigureFailures(tx);
                tx.Start();

                // Refresh each selected filter's DEFINITION from its current owning rule before
                // attaching it, so a filter created before its rule was merged picks up ALL the
                // merged keywords (not just the first). Done in place to preserve the ElementId
                // and existing view assignments; a filter with no owning rule is left untouched.
                var ctx = AutoFiltersEventHandler.CreateRefreshContext(doc, sourceDocs);
                int refreshed = 0;
                foreach (var name in selectedSet)
                {
                    if (!ruleByName.TryGetValue(name, out var owner)) continue;
                    var pfe = AutoFiltersEventHandler.EnsureRuleFilterDefinition(
                        ctx, owner.Trade, owner.Rule, out var warn);
                    if (warn != null) Log(LemoineStrings.T("autofilters.applyFilters.log.warnEntry", name, warn), "info");
                    if (pfe != null) refreshed++;
                }
                if (refreshed > 0)
                    Log(LemoineStrings.T("autofilters.applyFilters.log.synced", refreshed), "info");

                // Build name → id from the refreshed index (ids may have changed on rebuild).
                var filterMap = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in selectedSet)
                    if (ctx.ExistingFilters.TryGetValue(name, out var pfe))
                        filterMap[name] = pfe.Id;

                foreach (var m in SelectedFilterNames.Where(n => !filterMap.ContainsKey(n)))
                    Log(LemoineStrings.T("autofilters.applyFilters.log.filterNotFound", m), "info");

                if (filterMap.Count == 0)
                {
                    Log(LemoineStrings.T("autofilters.applyFilters.log.noneExist"), "fail");
                    fail++;
                    tx.RollBack();
                    return;
                }

                Log(LemoineStrings.T("autofilters.applyFilters.log.beginning", filterMap.Count, viewList.Count), "info");

                int totalOps = filterMap.Count * viewList.Count;

                bool cancelled = false;
                foreach (var view in viewList)
                {
                    if (cancelled) break;

                    var existingIds = new HashSet<long>(
                        view.GetFilters().Select(id => id.Value));

                    foreach (var pair in filterMap)
                    {
                        if (LemoineRun.CancelRequested)
                        {
                            Log(LemoineStrings.T("common.log.stoppedByUser", done, totalOps), "warn");
                            cancelled = true;
                            break;
                        }

                        string    filterName = pair.Key;
                        ElementId filterId   = pair.Value;
                        bool alreadyPresent  = existingIds.Contains(filterId.Value);

                        if (alreadyPresent && !OverwriteExisting)
                        {
                            skip++; done++;
                            ReportProgress(done, totalOps, pass, fail, skip, ref lastPct);
                            continue;
                        }

                        try
                        {
                            if (!alreadyPresent)
                            {
                                view.AddFilter(filterId);
                                existingIds.Add(filterId.Value);
                            }

                            // Every override + visibility/enabled value comes from the owning
                            // rule via the shared engine. A filter with no matching rule (or one
                            // whose override flags are all off) is left as "no override" —
                            // nothing is fabricated here.
                            if (ruleByName.TryGetValue(filterName, out var owner))
                            {
                                view.SetIsFilterEnabled(filterId, owner.Rule.FilterOn);
                                if (ApplyColorOverrides)
                                    AutoFiltersEventHandler.ApplyRuleOverride(
                                        view, filterId, owner.Rule,
                                        solidFillId, solidLineId, fillPatternMap, linePatternMap);
                            }
                            else
                            {
                                Log(LemoineStrings.T("autofilters.applyFilters.log.noRuleOwns", filterName), "info");
                            }

                            pass++;
                        }
                        catch (Exception ex)
                        {
                            Log(LemoineStrings.T("autofilters.applyFilters.log.applyFailed", filterName, view.Name, ex.Message), "fail");
                            fail++;
                        }

                        done++;
                        ReportProgress(done, totalOps, pass, fail, skip, ref lastPct);
                    }
                }

                tx.Commit();
            }

            Log(LemoineStrings.T("autofilters.applyFilters.log.complete", pass, skip, fail), "pass");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        // Warns (once per link) when a loaded RevitLinkInstance is displayed "By Linked
        // View" in any target view, because host view filters cannot override its elements.
        private void WarnLinksNotByHostView(Document doc, IEnumerable<View> views)
        {
            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
            if (links.Count == 0) return;

            var warned = new HashSet<long>();
            foreach (var view in views)
            {
                foreach (var li in links)
                {
                    if (warned.Contains(li.Id.Value)) continue;
                    try
                    {
                        var ovr = view.GetLinkOverrides(li.Id);
                        if (ovr != null && ovr.LinkVisibilityType == LinkVisibility.ByLinkView)
                        {
                            string title = li.GetLinkDocument()?.Title ?? li.Name;
                            Log(LemoineStrings.T("autofilters.applyFilters.log.linkByLinkedView", title, view.Name), "info");
                            warned.Add(li.Id.Value);
                        }
                    }
                    catch (Exception ex) { LemoineLog.Swallowed("ApplyFilters: read link display override", ex); }
                }
            }
        }

        // Reports progress only when the integer percentage actually changes, so a large
        // filter×view run doesn't flood the UI dispatcher with one marshalled call per op.
        private void ReportProgress(int done, int totalOps, int pass, int fail, int skip, ref int lastPct)
        {
            int pct = totalOps > 0 ? (int)(done * 90.0 / totalOps) : 90;
            if (pct == lastPct) return;
            lastPct = pct;
            Progress(pct, pass, fail, skip);
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
                LemoineLog.Swallowed("AutoFilters.Apply: resolve solid line pattern id", ex);
                return ElementId.InvalidElementId;
            }
        }

        private void Log(string text, string status)
        {
            PushLog?.Invoke(text, status);
            if (status == "fail")
                LemoineLog.Warn("AutoFilters.Apply", text);
            else
                LemoineLog.Info("AutoFilters.Apply", text);
        }
        private void Progress(int pct, int p, int f, int s) => OnProgress?.Invoke(pct, p, f, s);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
