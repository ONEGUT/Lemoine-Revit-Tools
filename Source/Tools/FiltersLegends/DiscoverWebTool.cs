using System;
using System.Collections.Generic;
using System.Linq;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.AutoFilters
{
    /// <summary>Web port of <see cref="DiscoverViewModel"/> — scan links for trade rules and
    /// commit them as Auto Filters. Same scan/commit/create handlers and AppStrings keys. The
    /// per-link accordion flattens to dynamic inputs keyed by link id; the S2 Confirm starts the
    /// scan (IWebConfirmable) and the scan-complete callback rebuilds S3/S4 via IWebStepRefresh.
    /// The WPF live scan-log panel becomes a status + percentage line (divergence logged).</summary>
    public class DiscoverWebTool : WebToolBase, IWebToolCleanup, IWebConfirmable, IWebStepRefresh
    {
        // Reuse the WPF VM's nested types so the handler contract is identical.
        private readonly DiscoverEventHandler            _handler;
        private readonly Autodesk.Revit.UI.ExternalEvent _event;
        private readonly List<DiscoverViewModel.LinkEntry> _links;
        private readonly List<DiscoverViewModel.DiscoveredRuleRow> _discoveredRules = new List<DiscoverViewModel.DiscoveredRuleRow>();

        private readonly AutoFiltersEventHandler?         _createHandler;
        private readonly Autodesk.Revit.UI.ExternalEvent? _createEvent;
        private bool _createAfterCommit = true;

        private bool   _scanComplete;
        private bool   _isScanning;
        private int    _scanPct;
        private string _scanStatus = "";

        // Map param-input id → (link, category label) for the S2 per-category parameter rows.
        private readonly Dictionary<string, (DiscoverViewModel.LinkEntry Link, string Cat)> _paramInputs
            = new Dictionary<string, (DiscoverViewModel.LinkEntry, string)>();

        private static readonly Dictionary<string, List<string>> CategoryGroups =
            new Dictionary<string, List<string>>
            {
                ["MEP / HVAC"]    = new List<string> { "Ducts","Duct Fittings","Duct Accessories","Duct Insulation","Duct Linings","Air Terminals","Flex Ducts","Mechanical Equipment","Fabrication Ductwork" },
                ["Piping"]        = new List<string> { "Pipes","Pipe Fittings","Pipe Accessories","Pipe Insulation","Pipe Linings","Flex Pipes","Plumbing Fixtures","Sprinklers","Fabrication Pipework","Fabrication Hangers","Fabrication Containment" },
                ["Electrical"]    = new List<string> { "Cable Trays","Cable Tray Fittings","Conduits","Conduit Fittings","Electrical Equipment","Electrical Fixtures","Lighting Fixtures","Lighting Devices","Communication Devices","Fire Alarm Devices","Security Devices","Data Devices","Telephone Devices","Nurse Call Devices" },
                ["Structural"]    = new List<string> { "Structural Framing","Structural Columns","Structural Foundations","Structural Trusses","Rebar" },
                ["Architectural"] = new List<string> { "Walls","Floors","Roofs","Ceilings","Doors","Windows","Generic Models","Rooms","Spaces" },
            };

        public event Action<string>? StepInputsChanged;

        public DiscoverWebTool(
            DiscoverEventHandler             handler,
            Autodesk.Revit.UI.ExternalEvent  externalEvent,
            List<DiscoverViewModel.LinkEntry> links,
            AutoFiltersEventHandler?         createHandler = null,
            Autodesk.Revit.UI.ExternalEvent? createEvent   = null)
        {
            _handler       = handler       ?? throw new ArgumentNullException(nameof(handler));
            _event         = externalEvent ?? throw new ArgumentNullException(nameof(externalEvent));
            _links         = links         ?? new List<DiscoverViewModel.LinkEntry>();
            _createHandler = createHandler;
            _createEvent   = createEvent;
        }

        public override string Title    => AppStrings.T("autofilters.discover.title");
        public override string RunLabel => AppStrings.T("autofilters.discover.runLabel");

        // ── Confirm: S2 starts the scan ────────────────────────────────────────

        public string? ConfirmLabelFor(string stepId) =>
            stepId == "S2" ? AppStrings.T("autofilters.discover.labels.confirmS2") : null;

        public void OnStepConfirm(string stepId)
        {
            if (stepId == "S2") StartScan();
        }

        private void StartScan()
        {
            if (_isScanning) return;
            var selectedLinks = _links.Where(l => l.IsSelected && l.ConfigRows.Count > 0).ToList();
            if (selectedLinks.Count == 0) return;

            _isScanning   = true;
            _scanComplete = false;
            _scanPct      = 0;
            _scanStatus   = AppStrings.T("autofilters.discover.status.scanning");
            _discoveredRules.Clear();
            StepInputsChanged?.Invoke("S3");

            var specs = new List<ScanSpec>();
            foreach (var link in selectedLinks)
                foreach (var row in link.ConfigRows)
                    specs.Add(new ScanSpec
                    {
                        OstCategory = row.OstCategory,
                        Mode        = row.Mode,
                        Parameter   = row.Parameter,
                        LinkId      = link.Id.Value,
                        TradeName   = link.TradeName,
                    });

            // Scan callbacks land on the Revit thread; the window's StepInputsChanged
            // handler marshals to the UI dispatcher (SafeUi).
            _handler.Mode       = DiscoverMode.MainScan;
            _handler.ScanSpecs  = specs;
            _handler.PushLog    = (text, status) => { };   // per-line log dropped (divergence)
            _handler.OnProgress = (pct, p, f, sk) =>
            {
                _scanPct    = pct;
                _scanStatus = AppStrings.T("autofilters.discover.status.scanningPct", pct);
                StepInputsChanged?.Invoke("S3");
            };
            _handler.OnComplete = (pass, fail, skip) =>
            {
                var results = _handler.ScanResults;
                _isScanning   = false;
                _scanComplete = results.Count > 0;
                _scanPct      = 100;
                _scanStatus   = _scanComplete
                    ? AppStrings.T("autofilters.discover.status.scanCompleteFound", results.Count)
                    : AppStrings.T("autofilters.discover.status.scanCompleteNone");
                PopulateDiscovered(results);
                StepInputsChanged?.Invoke("S3");
                StepInputsChanged?.Invoke("S4");
                StepInputsChanged?.Invoke("S5");
                Fire();
            };
            _event.Raise();
        }

        // Merge per-value rows sharing (trade, param, value) — same logic as WPF PopulateS4.
        private void PopulateDiscovered(List<ScanResult> results)
        {
            _discoveredRules.Clear();
            var perValueByKey = new Dictionary<(string Trade, string Param, string Value), DiscoverViewModel.DiscoveredRuleRow>();
            foreach (var r in results)
            {
                if (r.IsWholeCategory)
                {
                    _discoveredRules.Add(new DiscoverViewModel.DiscoveredRuleRow
                    {
                        IsIncluded        = true,
                        RuleName          = AutoFiltersSettings.DisplayNameForOst(r.OstCategory),
                        HexColor          = r.HexColor,
                        TradeName         = r.TradeName ?? "",
                        ElementCount      = r.ElementCount,
                        IsWholeCategory   = true,
                        ParameterValue    = r.ParameterValue ?? "",
                        Parameter         = r.Parameter ?? "",
                        BuiltInCategories = new List<string> { r.OstCategory },
                    });
                    continue;
                }
                var key = (r.TradeName ?? "", r.Parameter ?? "", r.ParameterValue ?? "");
                if (perValueByKey.TryGetValue(key, out var existing))
                {
                    existing.ElementCount += r.ElementCount;
                    if (!existing.BuiltInCategories.Contains(r.OstCategory))
                        existing.BuiltInCategories.Add(r.OstCategory);
                }
                else
                {
                    var rowVm = new DiscoverViewModel.DiscoveredRuleRow
                    {
                        IsIncluded        = true,
                        RuleName          = r.ParameterValue ?? "",
                        HexColor          = r.HexColor,
                        TradeName         = r.TradeName ?? "",
                        ElementCount      = r.ElementCount,
                        IsWholeCategory   = false,
                        ParameterValue    = r.ParameterValue ?? "",
                        Parameter         = r.Parameter ?? "",
                        BuiltInCategories = new List<string> { r.OstCategory },
                    };
                    perValueByKey[key] = rowVm;
                    _discoveredRules.Add(rowVm);
                }
            }
        }

        private void RebuildConfigRows(DiscoverViewModel.LinkEntry link)
        {
            var existingByLabel = link.ConfigRows.ToDictionary(r => r.CategoryLabel, r => r);
            link.ConfigRows.Clear();
            foreach (var catLabel in link.SelectedCategories)
            {
                if (existingByLabel.TryGetValue(catLabel, out var existing))
                    link.ConfigRows.Add(existing);
                else if (AutoFiltersSettings.TryResolveCategoryOst(catLabel, out var ost))
                    link.ConfigRows.Add(new DiscoverViewModel.ScanConfigRow(catLabel, ost));
                else
                    DiagnosticsLog.Warn("AutoFilters.Discover",
                        $"Category '{catLabel}' has no filterable OST mapping in this document — skipped.");
            }
        }

        private void InvalidateScan()
        {
            _scanComplete = false;
            _discoveredRules.Clear();
        }

        // ── Steps ─────────────────────────────────────────────────────────────

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            _paramInputs.Clear();

            // S1 — pick links + name trades
            var s1 = new WebStep("S1", AppStrings.T("autofilters.discover.steps.S1"))
                .Add(WebInput.Hint("noteS1", AppStrings.T("autofilters.discover.labels.noteS1")));
            if (_links.Count == 0)
            {
                s1.Add(WebInput.Warn("noLinks", AppStrings.T("autofilters.discover.labels.noLinks")));
            }
            else
            {
                foreach (var link in _links)
                {
                    s1.Add(WebInput.Toggle($"sel_{link.Id.Value}", link.Label, link.IsSelected));
                    if (link.IsSelected)
                        s1.Add(WebInput.TextField($"trade_{link.Id.Value}",
                            AppStrings.T("autofilters.discover.labels.trade"), link.TradeName));
                }
            }

            // S2 — configure each selected link
            var s2 = new WebStep("S2", AppStrings.T("autofilters.discover.steps.S2"))
                .Add(WebInput.Hint("noteS2", AppStrings.T("autofilters.discover.labels.noteS2")));
            var selectedLinks = _links.Where(l => l.IsSelected).ToList();
            if (selectedLinks.Count == 0)
            {
                s2.Add(WebInput.Warn("noLinksSelected", AppStrings.T("autofilters.discover.labels.noLinksSelected")));
            }
            else
            {
                foreach (var link in selectedLinks)
                {
                    s2.Add(WebInput.Hint($"hdr_{link.Id.Value}", $"{link.TradeName}  ({link.Label})"));
                    s2.Add(WebInput.MultiSelectTabs($"cats_{link.Id.Value}",
                        AppStrings.T("autofilters.discover.labels.selectCats"),
                        CategoryGroups, link.SelectedCategories,
                        hierarchy: AutoFiltersSettings.CategorySubcategories
                            .ToDictionary(kv => kv.Key, kv => kv.Value.ToList())));

                    foreach (var row in link.ConfigRows)
                    {
                        string id = $"param_{link.Id.Value}_{link.ConfigRows.IndexOf(row)}";
                        _paramInputs[id] = (link, row.CategoryLabel);
                        var options = new List<WebOption>
                        {
                            new WebOption("__whole__", AppStrings.T("autofilters.discover.labels.wholeCategory")),
                        };
                        options.AddRange(row.AvailableParams.Select(p => new WebOption(p, p)));
                        string current = row.Mode == "WholeCategory" ? "__whole__" : row.Parameter;
                        s2.Add(WebInput.SingleSelect(id, row.CategoryLabel, current, options));
                    }
                }
            }

            // S3 — scanning status
            var s3 = new WebStep("S3", AppStrings.T("autofilters.discover.steps.S3"));
            if (_isScanning)
                s3.Add(WebInput.Hint("scanStatus", _scanStatus));
            else if (_scanComplete)
                s3.Add(WebInput.Hint("scanStatus", _scanStatus));
            else if (!string.IsNullOrEmpty(_scanStatus))
                s3.Add(WebInput.Warn("scanStatus", _scanStatus));
            else
                s3.Add(WebInput.Hint("waiting", AppStrings.T("autofilters.discover.status.waiting")));

            // S4 — review discovered rules
            var s4 = new WebStep("S4", AppStrings.T("autofilters.discover.steps.S4"));
            if (_discoveredRules.Count == 0)
            {
                s4.Add(WebInput.Hint("noRules", AppStrings.T("autofilters.discover.labels.noRules")));
            }
            else
            {
                s4.Add(WebInput.Hint("rulesDiscovered",
                    AppStrings.T("autofilters.discover.labels.rulesDiscovered", _discoveredRules.Count)));
                foreach (var group in _discoveredRules.GroupBy(r => r.TradeName, StringComparer.OrdinalIgnoreCase))
                {
                    s4.Add(WebInput.Hint($"trade_{group.Key}", $"{group.Key}  ({group.Count()})"));
                    foreach (var rule in group)
                    {
                        int idx = _discoveredRules.IndexOf(rule);
                        s4.Add(WebInput.Toggle($"inc_{idx}",
                            $"{rule.RuleName}  ·  {CategoryDisplayName(rule.BuiltInCategories.FirstOrDefault() ?? "")}  ·  {rule.ElementCount}",
                            rule.IsIncluded));
                        s4.Add(WebInput.Color($"color_{idx}", "", rule.HexColor));
                        s4.Add(WebInput.TextField($"name_{idx}", AppStrings.T("autofilters.discover.labels.colRuleName"), rule.RuleName));
                    }
                }
            }

            // S5 — confirm & commit
            int included   = _discoveredRules.Count(r => r.IsIncluded);
            int tradeCount = _discoveredRules.Where(r => r.IsIncluded).Select(r => r.TradeName)
                                             .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var s5 = new WebStep("S5", AppStrings.T("autofilters.discover.steps.S5"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("autofilters.discover.review.itemTotal"),   _discoveredRules.Count.ToString()),
                    (AppStrings.T("autofilters.discover.review.itemInclude"), included.ToString()),
                    (AppStrings.T("autofilters.discover.review.itemSkip"),    (_discoveredRules.Count - included).ToString()),
                    (AppStrings.T("autofilters.discover.review.itemTrades"),  tradeCount.ToString()),
                }));
            if (_createHandler != null && _createEvent != null)
            {
                s5.Add(WebInput.Toggle("create", AppStrings.T("autofilters.discover.labels.createLabel"), _createAfterCommit));
                s5.Add(WebInput.Hint("createDesc", AppStrings.T("autofilters.discover.labels.createDesc")));
            }
            else
            {
                s5.Add(WebInput.Warn("noCreateWarn", AppStrings.T("autofilters.discover.labels.noCreateWarn")));
            }

            return new List<WebStep> { s1, s2, s3, s4, s5 };
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            if (inputId.StartsWith("sel_", StringComparison.Ordinal)
                && long.TryParse(inputId.Substring(4), out var selId))
            {
                var link = _links.FirstOrDefault(l => l.Id.Value == selId);
                if (link != null)
                {
                    link.IsSelected = AsBool(value, link.IsSelected);
                    StepInputsChanged?.Invoke("S1"); // show/hide the trade field
                    StepInputsChanged?.Invoke("S2"); // add/remove the link card
                    InvalidateScan();
                    Fire();
                }
                return;
            }
            if (inputId.StartsWith("trade_", StringComparison.Ordinal)
                && long.TryParse(inputId.Substring(6), out var trId))
            {
                var link = _links.FirstOrDefault(l => l.Id.Value == trId);
                if (link != null) { link.TradeName = AsString(value, link.TradeName); Fire(); }
                return;
            }
            if (inputId.StartsWith("cats_", StringComparison.Ordinal)
                && long.TryParse(inputId.Substring(5), out var catId))
            {
                var link = _links.FirstOrDefault(l => l.Id.Value == catId);
                if (link != null)
                {
                    var newCats = StrList(value);
                    bool changed = !new HashSet<string>(newCats, StringComparer.OrdinalIgnoreCase)
                        .SetEquals(link.SelectedCategories);
                    link.SelectedCategories.Clear();
                    link.SelectedCategories.AddRange(newCats);
                    RebuildConfigRows(link);
                    StepInputsChanged?.Invoke("S2"); // per-category parameter rows
                    if (changed) InvalidateScan();
                    Fire();
                }
                return;
            }
            if (_paramInputs.TryGetValue(inputId, out var pi))
            {
                var row = pi.Link.ConfigRows.FirstOrDefault(r => r.CategoryLabel == pi.Cat);
                if (row != null)
                {
                    var sel = AsString(value);
                    if (sel == "__whole__") row.Mode = "WholeCategory";
                    else { row.Mode = "PerValue"; row.Parameter = sel; }
                    InvalidateScan();
                    Fire();
                }
                return;
            }
            if (inputId.StartsWith("inc_", StringComparison.Ordinal)
                && int.TryParse(inputId.Substring(4), out var incIdx)
                && incIdx >= 0 && incIdx < _discoveredRules.Count)
            {
                _discoveredRules[incIdx].IsIncluded = AsBool(value, _discoveredRules[incIdx].IsIncluded);
                StepInputsChanged?.Invoke("S5");
                Fire();
                return;
            }
            if (inputId.StartsWith("color_", StringComparison.Ordinal)
                && int.TryParse(inputId.Substring(6), out var colIdx)
                && colIdx >= 0 && colIdx < _discoveredRules.Count)
            {
                _discoveredRules[colIdx].HexColor = AsString(value, _discoveredRules[colIdx].HexColor);
                Fire();
                return;
            }
            if (inputId.StartsWith("name_", StringComparison.Ordinal)
                && int.TryParse(inputId.Substring(5), out var nameIdx)
                && nameIdx >= 0 && nameIdx < _discoveredRules.Count)
            {
                _discoveredRules[nameIdx].RuleName = AsString(value, _discoveredRules[nameIdx].RuleName);
                StepInputsChanged?.Invoke("S5");
                Fire();
                return;
            }
            if (inputId == "create") { _createAfterCommit = AsBool(value, _createAfterCommit); return; }
        }

        public override bool IsStepValid(string stepId)
        {
            switch (stepId)
            {
                case "S1":
                    return _links.Any(l => l.IsSelected)
                        && _links.Where(l => l.IsSelected).All(l => !string.IsNullOrWhiteSpace(l.TradeName));
                case "S2": return _links.Any(l => l.IsSelected && l.ConfigRows.Count > 0);
                case "S3": return _scanComplete;
                case "S4": return _discoveredRules.Any(r => r.IsIncluded);
                default:   return true;
            }
        }

        public override bool CanRun() => _discoveredRules.Any(r => r.IsIncluded);

        public override string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1":
                {
                    var sel = _links.Where(l => l.IsSelected).ToList();
                    if (sel.Count == 0) return AppStrings.T("autofilters.discover.summaries.s1None");
                    return sel.Count == 1
                        ? sel[0].Label
                        : AppStrings.T("autofilters.discover.summaries.s1Multi", sel.Count, string.Join(", ", sel.Select(l => l.TradeName)));
                }
                case "S2":
                {
                    var configured = _links.Where(l => l.IsSelected && l.ConfigRows.Count > 0).ToList();
                    return configured.Count == 0 ? AppStrings.T("autofilters.discover.summaries.s2None")
                        : AppStrings.T("autofilters.discover.summaries.s2Count", configured.Count);
                }
                case "S3":
                    return _scanComplete
                        ? AppStrings.T("autofilters.discover.summaries.s3Found", _discoveredRules.Count)
                        : AppStrings.T("autofilters.discover.summaries.s3NotScanned");
                case "S4":
                {
                    int inc = _discoveredRules.Count(r => r.IsIncluded);
                    return AppStrings.T("autofilters.discover.summaries.s4Selected", inc, _discoveredRules.Count);
                }
                default: return "";
            }
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            var specs = _discoveredRules
                .Where(r => r.IsIncluded)
                .Select(r => new CommitRuleSpec
                {
                    TradeName         = r.TradeName,
                    RuleName          = r.RuleName,
                    HexColor          = r.HexColor,
                    IsWholeCategory   = r.IsWholeCategory,
                    ParameterValue    = r.ParameterValue,
                    Parameter         = r.Parameter,
                    BuiltInCategories = r.BuiltInCategories,
                }).ToList();

            bool chainCreate = _createAfterCommit && _createHandler != null && _createEvent != null;

            Action<int, int, int> commitDone = onComplete;
            if (chainCreate)
                commitDone = (p, f, s) =>
                {
                    if (p <= 0) { onComplete(p, f, s); return; }
                    pushLog(AppStrings.T("autofilters.discover.log.creatingFilters"), "info");
                    _createHandler!.CreateOnly               = true;
                    _createHandler.OverwriteFilterDefinition = false;
                    _createHandler.ChangedFilterNames        = null;
                    _createHandler.SelectedDisciplines       = new List<string>();
                    _createHandler.SelectedLinkTitles        = new List<string>();
                    _createHandler.PushLog                   = pushLog;
                    _createHandler.OnProgress                = onProgress;
                    _createHandler.OnComplete                = (cp, cf, cs, _) => onComplete(cp, cf, cs);
                    _createEvent!.Raise();
                };

            _handler.Mode        = DiscoverMode.Commit;
            _handler.CommitSpecs = specs;
            _handler.PushLog     = pushLog;
            _handler.OnProgress  = onProgress;
            _handler.OnComplete  = commitDone;
            _event.Raise();
        }

        public void OnWindowClosed()
        {
            if (_handler != null)
            {
                _handler.PushLog    = null;
                _handler.OnProgress = null;
                _handler.OnComplete = null;
            }
            if (_createHandler != null)
            {
                _createHandler.PushLog    = null;
                _createHandler.OnProgress = null;
                _createHandler.OnComplete = null;
            }
        }

        private static string CategoryDisplayName(string ostCategory)
            => AutoFiltersSettings.DisplayNameForOst(ostCategory);
    }
}
