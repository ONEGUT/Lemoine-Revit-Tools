using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.AutoFilters
{
    // =========================================================================
    // Discover Event Handler — two modes:
    //   MainScan : reads parameter values from selected link documents and
    //              returns a flat list of ScanResult objects.
    //   Commit   : converts CommitRuleSpec list into FilterRuleConfig objects
    //              and persists them to AutoFiltersSettings.
    // =========================================================================

    public enum DiscoverMode { MainScan, Commit }

    /// <summary>
    /// One scan task: category × link × mode × parameter.
    /// Built by DiscoverViewModel and set on the handler before Raise().
    /// </summary>
    public struct ScanSpec
    {
        public string OstCategory;   // e.g. "OST_DuctCurves"
        public string Mode;          // "WholeCategory" | "PerValue"
        public string Parameter;     // parameter display name
        public long   LinkId;        // ElementId.Value of the RevitLinkInstance
        public string TradeName;     // trade label for the link
    }

    /// <summary>
    /// One row returned by MainScan — consumed by DiscoverViewModel to build S4 rows.
    /// </summary>
    public class ScanResult
    {
        public bool   IsWholeCategory { get; set; }
        public string TradeName       { get; set; } = "";
        public string OstCategory     { get; set; } = "";
        public string ParameterValue  { get; set; } = "";
        public string Parameter       { get; set; } = "";
        public int    ElementCount    { get; set; }
        public string HexColor        { get; set; } = "#888888";
    }

    /// <summary>
    /// Approved rule from S4 to persist — set by DiscoverViewModel.Run() before Raise().
    /// </summary>
    public struct CommitRuleSpec
    {
        public string       TradeName;
        public string       RuleName;
        public string       HexColor;
        public bool         IsWholeCategory;
        public string       ParameterValue;
        public string       Parameter;
        public List<string> BuiltInCategories;
    }

    /// <summary>
    /// IExternalEventHandler for the Discover Rules tool.
    /// Runs on Revit's main thread via ExternalEvent.Raise().
    /// </summary>
    public class DiscoverEventHandler : IExternalEventHandler
    {
        // ── Set by ViewModel before Raise() ──────────────────────────────────

        public DiscoverMode         Mode        { get; set; }
        public List<ScanSpec>       ScanSpecs   { get; set; } = new List<ScanSpec>();
        public List<CommitRuleSpec> CommitSpecs { get; set; } = new List<CommitRuleSpec>();

        // ── Output (MainScan) — read by ViewModel in OnComplete callback ───

        public List<ScanResult> ScanResults { get; private set; } = new List<ScanResult>();

        // ── Callbacks ────────────────────────────────────────────────────────

        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        // ── Known-safe parameters (resolve reliably without live elements) ──

        public static readonly HashSet<string> KnownSafeParams =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "System Classification", "Fabrication Service",
                "Type Name", "Family Name", "Structural Material",
            };

        // ── 20-colour auto-palette ────────────────────────────────────────────

        private static readonly string[] AutoPalette =
        {
            "#E63946","#F4A261","#2A9D8F","#457B9D","#8338EC",
            "#FB5607","#FFBE0B","#06D6A0","#118AB2","#EF476F",
            "#BDE0FE","#CAFFBF","#FFD6A5","#FDFFB6","#C8B8FF",
            "#F72585","#7209B7","#3A0CA3","#4361EE","#4CC9F0",
        };

        private int _paletteIndex = 0;

        // ── IExternalEventHandler ─────────────────────────────────────────────

        public string GetName() => "LemoineTools.Tools.AutoFilters.DiscoverEventHandler";

        public void Execute(UIApplication app)
        {
            int pass = 0, fail = 0, skip = 0;
            try
            {
                if (Mode == DiscoverMode.MainScan)
                    RunMainScan(app, ref pass, ref fail, ref skip);
                else
                    RunCommit(ref pass, ref fail, ref skip);
            }
            catch (Exception ex)
            {
                LemoineLog.Error("AutoFilters: discover aborted", ex); Log($"Error: {ex.Message}", "fail");
                fail++;
            }
            Progress(100, pass, fail, skip);
            Complete(pass, fail, skip);
        }

        // ── MainScan ─────────────────────────────────────────────────────────

        private void RunMainScan(UIApplication app, ref int pass, ref int fail, ref int skip)
        {
            var doc     = app.ActiveUIDocument.Document;
            var results = new List<ScanResult>();

            // Build linkId → Document map (–1 = host document)
            var linkDocMap = new Dictionary<long, Document> { [-1L] = doc };
            foreach (RevitLinkInstance li in
                new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var ld = li.GetLinkDocument();
                if (ld != null)
                    linkDocMap[li.Id.Value] = ld;
            }

            // Group specs by link so we can log per-link progress
            var specsByLink = ScanSpecs
                .GroupBy(s => s.LinkId)
                .Select(g => (TradeId: g.Key, TradeName: g.First().TradeName, Specs: g.ToList()))
                .ToList();

            int total = ScanSpecs.Count;
            int done  = 0;

            // Log queued links upfront so user can see what will be scanned
            foreach (var link in specsByLink)
            {
                int catCount = link.Specs.Count;
                Log($"Queued  {link.TradeName}  ({catCount} categor{(catCount == 1 ? "y" : "ies")})", "info");
            }

            foreach (var link in specsByLink)
            {
                Log($"→  Scanning {link.TradeName}…", "info");
                int resultsBefore = results.Count;

                foreach (var spec in link.Specs)
                {
                    done++;
                    Progress((int)(5 + 90.0 * done / Math.Max(1, total)), pass, fail, skip);

                    Log($"     {spec.OstCategory}", "info");

                    if (!Enum.TryParse<BuiltInCategory>(spec.OstCategory, false, out var bic))
                    {
                        Log($"     ✗ unknown category — skipped", "fail");
                        skip++;
                        continue;
                    }

                    if (!linkDocMap.TryGetValue(spec.LinkId, out var scanDoc))
                    {
                        Log($"     ✗ link document not found — skipped", "fail");
                        skip++;
                        continue;
                    }

                    var catId     = new ElementId((long)(int)bic);
                    var collector = new FilteredElementCollector(scanDoc)
                        .OfCategoryId(catId)
                        .WhereElementIsNotElementType();

                    if (spec.Mode == "WholeCategory")
                    {
                        int count = collector.GetElementCount();
                        if (count == 0) { skip++; continue; }

                        results.Add(new ScanResult
                        {
                            IsWholeCategory = true,
                            TradeName       = spec.TradeName,
                            OstCategory     = spec.OstCategory,
                            ParameterValue  = "",
                            Parameter       = "Type Name",
                            ElementCount    = count,
                            HexColor        = ResolveColor(""),
                        });
                        pass++;
                    }
                    else // PerValue
                    {
                        var valueCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        foreach (Element el in collector)
                        {
                            string? val = ReadParameterValue(el, spec.Parameter);
                            if (string.IsNullOrWhiteSpace(val)) continue;
                            valueCounts.TryGetValue(val!, out int c);
                            valueCounts[val!] = c + 1;
                        }

                        if (valueCounts.Count == 0) { skip++; continue; }

                        foreach (var kvp in valueCounts.OrderByDescending(x => x.Value))
                        {
                            results.Add(new ScanResult
                            {
                                IsWholeCategory = false,
                                TradeName       = spec.TradeName,
                                OstCategory     = spec.OstCategory,
                                ParameterValue  = kvp.Key,
                                Parameter       = spec.Parameter,
                                ElementCount    = kvp.Value,
                                HexColor        = ResolveColor(kvp.Key),
                            });
                            pass++;
                        }
                    }
                }

                int linkRules = results.Count - resultsBefore;
                Log($"   ✓  {link.TradeName} — {linkRules} rule(s) found", linkRules > 0 ? "pass" : "info");
            }

            ScanResults = results;
            Log($"Scan complete — {pass} rule(s) discovered, {skip} skipped.", pass > 0 ? "pass" : "info");
        }

        /// <summary>
        /// Three-priority colour lookup:
        /// 1. ColorMemory (user overrides)
        /// 2. Existing AutoFiltersSettings rules (keyword match)
        /// 3. Auto-palette cycling
        /// </summary>
        private string ResolveColor(string paramValue)
        {
            if (!string.IsNullOrEmpty(paramValue) &&
                ColorMemory.Instance.TryGetColor(paramValue, out string memHex))
                return memHex;

            if (!string.IsNullOrEmpty(paramValue))
            {
                foreach (var trade in AutoFiltersSettings.Instance.Trades)
                    foreach (var rule in trade.Rules)
                        foreach (var kw in rule.Match)
                            if (paramValue.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                                return rule.CutColor;
            }

            return AutoPalette[_paletteIndex++ % AutoPalette.Length];
        }

        private static string? ReadParameterValue(Element el, string paramName)
        {
            if (paramName == "Type Name")
            {
                var et = el.Document.GetElement(el.GetTypeId()) as ElementType;
                return et?.Name;
            }
            if (paramName == "Family Name")
            {
                var fs = el.Document.GetElement(el.GetTypeId()) as FamilySymbol;
                return fs?.FamilyName;
            }

            // Instance parameter
            var p = el.LookupParameter(paramName);
            if (p != null && p.HasValue)
            {
                string? v = p.AsString();
                if (!string.IsNullOrWhiteSpace(v)) return v;
                v = p.AsValueString();
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }

            // Fall back to type parameter
            var typeEl = el.Document.GetElement(el.GetTypeId());
            if (typeEl != null)
            {
                var tp = typeEl.LookupParameter(paramName);
                if (tp != null && tp.HasValue)
                {
                    string? v = tp.AsString();
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                    return tp.AsValueString();
                }
            }

            return null;
        }

        // ── Commit ────────────────────────────────────────────────────────────

        private void RunCommit(ref int pass, ref int fail, ref int skip)
        {
            Log("Saving rules to Auto Filters settings…", "info");
            var settings = AutoFiltersSettings.Instance;

            foreach (var spec in CommitSpecs)
            {
                try
                {
                    // Find or create trade by label
                    var trade = settings.Trades.FirstOrDefault(t =>
                        string.Equals(t.Label, spec.TradeName, StringComparison.OrdinalIgnoreCase));

                    if (trade == null)
                    {
                        trade = FilterTradeConfig.NewBlank();
                        trade.Label = spec.TradeName;
                        trade.Color = spec.HexColor;
                        settings.Trades.Add(trade);
                    }

                    // Deduplicate by rule name
                    if (trade.Rules.Any(r =>
                        string.Equals(r.Name, spec.RuleName, StringComparison.OrdinalIgnoreCase)))
                    {
                        Log($"'{spec.RuleName}' already exists in '{spec.TradeName}' — skipped.", "info");
                        skip++;
                        continue;
                    }

                    var rule = FilterRuleConfig.NewBlank();
                    rule.Name              = spec.RuleName;
                    rule.BuiltInCategories = spec.BuiltInCategories ?? new List<string>();
                    rule.CutColor          = spec.HexColor;
                    rule.SurfColor         = spec.HexColor;
                    rule.LineColor         = spec.HexColor;

                    if (spec.IsWholeCategory)
                    {
                        // ⚠  Gap 1 fix: MatchType="all" requires a non-empty Parameter
                        // to resolve correctly in AutoFiltersEventHandler.ResolveParamId.
                        rule.Parameter = "Type Name";
                        rule.MatchType = "all";
                        rule.Match     = new List<string>();
                    }
                    else
                    {
                        rule.Parameter = spec.Parameter;
                        // "contains" (not "equals"): discovered values can surface only via
                        // AsValueString (formatted with units/separators), which an exact
                        // equals rule would never match against the underlying value.
                        rule.MatchType = "contains";
                        rule.Match     = new List<string> { spec.ParameterValue };

                        // Persist colour choice so future scans remember it
                        if (!string.IsNullOrEmpty(spec.ParameterValue))
                            ColorMemory.Instance.SetColor(spec.ParameterValue, spec.HexColor);
                    }

                    trade.Rules.Add(rule);
                    Log($"Added '{rule.Name}' → '{trade.Label}'", "pass");
                    pass++;
                }
                catch (Exception ex)
                {
                    Log($"Failed to commit '{spec.RuleName}': {ex.Message}", "fail");
                    fail++;
                }
            }

            settings.Save();
            Log($"Done — {pass} rule(s) added, {skip} skipped, {fail} failed.", pass > 0 ? "pass" : "info");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        // Mirror to the durable diagnostic log so failure reasons survive the session
        // even when the UI log is closed. "fail" maps to Warn; everything else is info.
        private void Log(string msg, string status)
        {
            PushLog?.Invoke(msg, status);

            if (status == "fail")
                LemoineLog.Warn("AutoFilters.Discover", msg);
            else
                LemoineLog.Info("AutoFilters.Discover", msg);
        }
        private void Progress(int pct, int p, int f, int s) => OnProgress?.Invoke(pct, p, f, s);
        private void Complete(int p, int f, int s)          => OnComplete?.Invoke(p, f, s);
    }
}
