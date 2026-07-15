using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Naming;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.ExplodeViews
{
    /// <summary>Web port of <see cref="ExplodeViewByTradeViewModel"/> — duplicate one 3D view
    /// per selected AutoFilters trade. Same handler, naming pattern store, and AppStrings keys.</summary>
    public class ExplodeViewByTradeWebTool : WebToolBase, IWebToolCleanup
    {
        private readonly ExplodeViewByTradeEventHandler? _handler;
        private readonly ExternalEvent?                  _event;

        private long                _sourceViewId;
        private readonly List<long> _eligibleViewIds = new List<long>();
        private readonly BrowserTree _browserTree;
        private readonly Dictionary<long, string> _viewNames = new Dictionary<long, string>();

        private readonly Dictionary<string, string> _labelToTradeId =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<string> _tradesWithoutFilters = new List<string>();
        private List<string> _selectedTradeIds = new List<string>();

        private bool _orderByElevation   = true;
        private bool _numberPrefix       = true;
        private bool _applyColorOverride = true;
        private bool _hideOthers         = false;

        private const string ToolId         = "views.explodeByTrade";
        private const string DefaultPattern = "{Counter}_{SourceView} - {Trade}";
        private string _namePattern = NamingPatternStore.Instance.GetOrDefault(ToolId, DefaultPattern);

        private static readonly TokenDefinition[] ExplodeComputedTokens =
        {
            new TokenDefinition("Counter",    AppStrings.T("naming.computed.explodeByTrade.counter.label"),    TokenOrigin.Computed, TokenSubject.Target, TokenEntity.View),
            new TokenDefinition("SourceView", AppStrings.T("naming.computed.explodeByTrade.sourceView.label"), TokenOrigin.Computed, TokenSubject.Source, TokenEntity.View),
            new TokenDefinition("Trade",      AppStrings.T("naming.computed.explodeByTrade.trade.label"),      TokenOrigin.Computed, TokenSubject.Target, TokenEntity.View),
        };

        public ExplodeViewByTradeWebTool(
            ExplodeViewByTradeEventHandler? handler,
            ExternalEvent?                  externalEvent,
            IEnumerable<long>?              eligibleViewIds,
            BrowserTree?                    browserTree,
            IEnumerable<(string Id, string Label, bool HasFilters)>? trades,
            IReadOnlyDictionary<long, string>? viewNames = null)
        {
            _handler     = handler;
            _event       = externalEvent;
            _browserTree = browserTree ?? new BrowserTree();

            if (eligibleViewIds != null) _eligibleViewIds.AddRange(eligibleViewIds);
            if (viewNames != null)
                foreach (var kv in viewNames) _viewNames[kv.Key] = kv.Value;

            if (trades != null)
            {
                foreach (var t in trades)
                {
                    if (!t.HasFilters)
                    {
                        _tradesWithoutFilters.Add(t.Label);
                        continue;
                    }
                    string display = t.Label;
                    int n = 2;
                    while (_labelToTradeId.ContainsKey(display))
                        display = $"{t.Label} ({n++})";
                    _labelToTradeId[display] = t.Id;
                }
            }
        }

        public override string Title    => AppStrings.T("explode.byTrade.title");
        public override string RunLabel => AppStrings.T("explode.byTrade.runLabel");

        private string? SourceViewName()
            => _sourceViewId != 0 && _viewNames.TryGetValue(_sourceViewId, out var n) ? n : null;

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            var s1 = new WebStep("S1", AppStrings.T("explode.byTrade.steps.S1"));
            if (_eligibleViewIds.Count == 0)
                s1.Add(WebInput.Hint("noViews", AppStrings.T("explode.byTrade.labels.noViews")));
            else
                s1.Add(WebInput.BrowserTree("source", AppStrings.T("explode.byTrade.labels.sourceView"),
                    PruneTree(_browserTree, new HashSet<long>(_eligibleViewIds)),
                    _sourceViewId != 0 ? new[] { _sourceViewId } : Array.Empty<long>(),
                    singleSelect: true));

            var s2 = new WebStep("S2", AppStrings.T("explode.byTrade.steps.S2"));
            if (_labelToTradeId.Count == 0)
            {
                s2.Add(WebInput.Hint("noTrades", AppStrings.T("explode.byTrade.labels.noTrades")));
            }
            else
            {
                var initialDisplay = _labelToTradeId
                    .Where(kv => _selectedTradeIds.Contains(kv.Value))
                    .Select(kv => kv.Key)
                    .ToList();
                s2.Add(WebInput.MultiSelectTabs("trades", "",
                    new Dictionary<string, List<string>> { ["Trades"] = _labelToTradeId.Keys.ToList() },
                    initialDisplay));
                if (_tradesWithoutFilters.Count > 0)
                    s2.Add(WebInput.Hint("tradesHidden",
                        AppStrings.T("explode.byTrade.labels.tradesHidden",
                            _tradesWithoutFilters.Count, string.Join(", ", _tradesWithoutFilters))));
            }

            var tokens = NamingTokenRegistry.TokensFor(TokenEntity.View, hasSource: true, ExplodeComputedTokens);
            var ctx = new TokenContext();
            ctx.Computed["Counter"]    = _numberPrefix ? "01" : "";
            ctx.Computed["SourceView"] = SourceViewName() ?? AppStrings.T("explode.byTrade.labels.exSource");
            ctx.Computed["Trade"]      = _labelToTradeId
                .Where(kv => _selectedTradeIds.Contains(kv.Value))
                .Select(kv => kv.Key).FirstOrDefault() ?? AppStrings.T("explode.byTrade.labels.exTrade");

            var s3 = new WebStep("S3", AppStrings.T("explode.byTrade.steps.S3"), required: false)
                .Add(WebInput.Toggle("order",  AppStrings.T("explode.byTrade.labels.orderLabel"),  _orderByElevation))
                .Add(WebInput.Hint("orderDesc", AppStrings.T("explode.byTrade.labels.orderDesc")))
                .Add(WebInput.Toggle("number", AppStrings.T("explode.byTrade.labels.numberLabel"), _numberPrefix))
                .Add(WebInput.Hint("numberDesc", AppStrings.T("explode.byTrade.labels.numberDesc")))
                .Add(WebInput.Toggle("color",  AppStrings.T("explode.byTrade.labels.colorLabel"),  _applyColorOverride))
                .Add(WebInput.Hint("colorDesc", AppStrings.T("explode.byTrade.labels.colorDesc")))
                .Add(WebInput.Toggle("hide",   AppStrings.T("explode.byTrade.labels.hideLabel"),   _hideOthers))
                .Add(WebInput.Hint("hideDesc", AppStrings.T("explode.byTrade.labels.hideDesc")))
                .Add(WebInput.TokenInput("pattern", AppStrings.T("explode.byTrade.labels.namePattern"),
                    _namePattern, DefaultPattern, tokens, SampleFor(tokens, ctx)));

            var s4 = new WebStep("S4", AppStrings.T("explode.byTrade.steps.S4"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("explode.byTrade.review.itemSource"),
                     _sourceViewId == 0 ? "-" : (SourceViewName() ?? AppStrings.T("explode.byTrade.review.oneView"))),
                    (AppStrings.T("explode.byTrade.review.itemTrades"),
                     _selectedTradeIds.Count == 0 ? "-" : AppStrings.T("explode.byTrade.review.tradeCount", _selectedTradeIds.Count)),
                    (AppStrings.T("explode.byTrade.review.itemOrder"),
                     _orderByElevation ? AppStrings.T("explode.byTrade.review.yes") : AppStrings.T("explode.byTrade.review.no")),
                    (AppStrings.T("explode.byTrade.review.itemNumber"),
                     _numberPrefix ? AppStrings.T("explode.byTrade.review.yes") : AppStrings.T("explode.byTrade.review.no")),
                    (AppStrings.T("explode.byTrade.review.itemColor"),
                     _applyColorOverride ? AppStrings.T("explode.byTrade.review.yes") : AppStrings.T("explode.byTrade.review.no")),
                    (AppStrings.T("explode.byTrade.review.itemHide"),
                     _hideOthers ? AppStrings.T("explode.byTrade.review.yes") : AppStrings.T("explode.byTrade.review.no")),
                },
                note: AppStrings.T("explode.byTrade.review.note")));

            return new List<WebStep> { s1, s2, s3, s4 };
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "source":
                    _sourceViewId = IdList(value).FirstOrDefault();
                    Fire(); break;
                case "trades":
                    _selectedTradeIds = StrList(value)
                        .Where(_labelToTradeId.ContainsKey)
                        .Select(label => _labelToTradeId[label])
                        .Distinct()
                        .ToList();
                    Fire(); break;
                case "order":  _orderByElevation   = AsBool(value, _orderByElevation);   Fire(); break;
                case "number": _numberPrefix       = AsBool(value, _numberPrefix);       Fire(); break;
                case "color":  _applyColorOverride = AsBool(value, _applyColorOverride); Fire(); break;
                case "hide":   _hideOthers         = AsBool(value, _hideOthers);         Fire(); break;
                case "pattern":
                    _namePattern = AsString(value, _namePattern);
                    NamingPatternStore.Instance.Set(ToolId, _namePattern);
                    Fire(); break;
            }
        }

        public override bool IsStepValid(string stepId)
        {
            if (stepId == "S1") return _sourceViewId != 0;
            if (stepId == "S2") return _selectedTradeIds.Count > 0;
            return true;
        }

        public override bool CanRun() => _sourceViewId != 0 && _selectedTradeIds.Count > 0;

        public override string SummaryFor(string stepId)
        {
            if (stepId == "S1")
                return _sourceViewId == 0 ? "-" : (SourceViewName() ?? AppStrings.T("explode.byTrade.summaries.oneView"));
            if (stepId == "S2")
                return _selectedTradeIds.Count == 0 ? "-"
                    : AppStrings.T("explode.byTrade.summaries.tradeCount", _selectedTradeIds.Count);
            if (stepId == "S3")
            {
                var parts = new List<string>();
                parts.Add(_orderByElevation ? AppStrings.T("explode.byTrade.summaries.byElevation") : AppStrings.T("explode.byTrade.summaries.configOrder"));
                if (_numberPrefix)       parts.Add(AppStrings.T("explode.byTrade.summaries.numbered"));
                if (_applyColorOverride) parts.Add(AppStrings.T("explode.byTrade.summaries.colored"));
                parts.Add(_hideOthers ? AppStrings.T("explode.byTrade.summaries.isolated") : AppStrings.T("explode.byTrade.summaries.withContext"));
                return string.Join(" · ", parts);
            }
            if (stepId == "S4") return AppStrings.T("explode.byTrade.summaries.S4");
            return "-";
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            _handler!.SourceViewId      = new ElementId(_sourceViewId);
            _handler.SelectedTradeIds   = _selectedTradeIds.ToList();
            _handler.OrderByElevation   = _orderByElevation;
            _handler.NumberPrefix       = _numberPrefix;
            _handler.ApplyColorOverride = _applyColorOverride;
            _handler.HideOthers         = _hideOthers;
            _handler.NamePattern        = _namePattern;
            _handler.PushLog            = pushLog;
            _handler.OnProgress         = onProgress;
            _handler.OnComplete         = onComplete;

            pushLog(AppStrings.T("explode.byTrade.log.starting"), "info");
            _event!.Raise();
        }

        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.PushLog       = null;
            _handler.OnProgress    = null;
            _handler.OnComplete    = null;
            _handler.OnResultChips = null;
        }
    }
}
