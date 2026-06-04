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
        public IList<string> SelectedViewNames   { get; set; } = new List<string>();
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
                LemoineLog.Error("AutoFilters: apply filters to views aborted", ex); Log($"Error: {ex.Message}", "fail");
                fail++;
            }

            Progress(100, pass, fail, skip);
            Complete(pass, fail, skip);
        }

        // ── Core logic ────────────────────────────────────────────────────────
        private void Apply(Document doc, ref int pass, ref int fail, ref int skip)
        {
            if (SelectedFilterNames.Count == 0 || SelectedViewNames.Count == 0)
            {
                Log("No filters or views selected.", "fail");
                fail++; return;
            }

            // Build filter name → ElementId map
            var filterMap = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .Where(f => SelectedFilterNames.Contains(f.Name))
                .ToDictionary(f => f.Name, f => f.Id);

            foreach (var m in SelectedFilterNames.Except(filterMap.Keys))
                Log($"Filter not found in project: '{m}'", "info");

            if (filterMap.Count == 0)
            {
                Log("None of the selected filters exist in this project.", "fail");
                fail++; return;
            }

            // Build view name → View map
            var selectedViewSet = new HashSet<string>(SelectedViewNames, StringComparer.Ordinal);
            var viewList = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && selectedViewSet.Contains(v.Name) &&
                            v.AreGraphicsOverridesAllowed())
                .ToList();

            if (viewList.Count == 0)
            {
                Log("None of the selected views support graphic overrides.", "fail");
                fail++; return;
            }

            Log($"{filterMap.Count} filter(s), {viewList.Count} view(s) — beginning apply…", "info");

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

            int totalOps = filterMap.Count * viewList.Count;
            int done     = 0;

            using (var tx = new Transaction(doc, "Apply Filters to Views"))
            {
                ConfigureFailures(tx);
                tx.Start();

                foreach (var view in viewList)
                {
                    var existingIds = new HashSet<long>(
                        view.GetFilters().Select(id => id.Value));

                    foreach (var pair in filterMap)
                    {
                        string    filterName = pair.Key;
                        ElementId filterId   = pair.Value;
                        bool alreadyPresent  = existingIds.Contains(filterId.Value);

                        if (alreadyPresent && !OverwriteExisting)
                        {
                            skip++; done++;
                            Progress((int)(done * 90.0 / totalOps), pass, fail, skip);
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
                            var rule = MatchRule(filterName);
                            if (rule != null)
                            {
                                view.SetIsFilterEnabled(filterId, rule.FilterOn);
                                if (ApplyColorOverrides)
                                    AutoFiltersEventHandler.ApplyRuleOverride(
                                        view, filterId, rule,
                                        solidFillId, solidLineId, fillPatternMap, linePatternMap);
                            }
                            else
                            {
                                Log($"No current rule owns '{filterName}' — added without overrides.", "info");
                            }

                            pass++;
                        }
                        catch (Exception ex)
                        {
                            Log($"'{filterName}' on '{view.Name}': {ex.Message}", "fail");
                            fail++;
                        }

                        done++;
                        Progress((int)(done * 90.0 / totalOps), pass, fail, skip);
                    }
                }

                tx.Commit();
            }

            Log($"Complete — {pass} applied, {skip} skipped (already present), {fail} failed.", "pass");
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
                            Log($"⚠ Link '{title}' is shown 'By Linked View' in '{view.Name}' — host filters won't affect it. Set its display to 'By Host View' to apply overrides.", "info");
                            warned.Add(li.Id.Value);
                        }
                    }
                    catch (Exception ex) { LemoineLog.Swallowed("ApplyFilters: read link display override", ex); }
                }
            }
        }

        // Finds the FilterRuleConfig whose generated name matches the Revit filter name.
        // Returns null when no current rule owns the filter, in which case the caller
        // leaves the filter untouched (no fabricated override).
        private static FilterRuleConfig? MatchRule(string filterName)
        {
            foreach (var trade in AutoFiltersSettings.Instance.Trades ?? new System.Collections.Generic.List<FilterTradeConfig>())
                foreach (var rule in trade.Rules ?? new System.Collections.Generic.List<FilterRuleConfig>())
                    if (string.Equals(filterName,
                            AutoFiltersSettings.MakeFilterName(trade.Id, rule.Name),
                            StringComparison.OrdinalIgnoreCase))
                        return rule;
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
