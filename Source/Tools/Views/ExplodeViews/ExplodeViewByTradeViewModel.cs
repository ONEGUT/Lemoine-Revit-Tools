using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;
using LemoineTools.Framework.Naming;
using WpfTextBlock = System.Windows.Controls.TextBlock;

namespace LemoineTools.Tools.ExplodeViews
{
    /// <summary>
    /// Step-flow ViewModel for the Explode 3D View by Trade tool. Picks one source 3D view
    /// and the AutoFilters trades to explode, then duplicates the view once per trade with
    /// only that trade's filters visible. Output views are ordered by element elevation.
    /// </summary>
    public class ExplodeViewByTradeViewModel
        : IStepFlowTool, IReviewableTool, IRunResult, IToolCleanup
    {
        // ── Identity ─────────────────────────────────────────────────────────────
        public string Title    => AppStrings.T("explode.byTrade.title");
        public string RunLabel => AppStrings.T("explode.byTrade.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", AppStrings.T("explode.byTrade.steps.S1"), required: true),
            new StepDefinition("S2", AppStrings.T("explode.byTrade.steps.S2"),         required: true),
            new StepDefinition("S3", AppStrings.T("explode.byTrade.steps.S3"),               required: false),
            new StepDefinition("S4", AppStrings.T("explode.byTrade.steps.S4"),          required: false),
        };

        // ── Run result strip ───────────────────────────────────────────────────
        public string? ResultNoun => "views";
        private IReadOnlyList<ResultChip>? _resultChips;
        public IReadOnlyList<ResultChip>? ResultChips => _resultChips;

        // ── Revit wiring ─────────────────────────────────────────────────────────
        private readonly ExplodeViewByTradeEventHandler? _handler;
        private readonly ExternalEvent?                  _event;

        // ── Source-view selection state ────────────────────────────────────────
        private long                        _sourceViewId;
        private readonly List<long>         _eligibleViewIds = new List<long>();
        private readonly BrowserTree _browserTree;
        private readonly Dictionary<long, string> _viewNames = new Dictionary<long, string>();

        // ── Trade selection state ──────────────────────────────────────────────
        // Display label → trade id (only trades that have created filters are listed).
        private readonly Dictionary<string, string> _labelToTradeId =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<string> _tradesWithoutFilters = new List<string>();
        private List<string> _selectedTradeIds = new List<string>();

        // ── Options state ──────────────────────────────────────────────────────
        private bool _orderByElevation  = true;
        private bool _numberPrefix      = true;
        private bool _applyColorOverride = true;
        private bool _hideOthers        = false;

        // ── Naming ──────────────────────────────────────────────────────
        private const string ToolId         = "views.explodeByTrade";
        private const string DefaultPattern = "{Counter}_{SourceView} - {Trade}";
        private string _namePattern = NamingPatternStore.Instance.GetOrDefault(ToolId, DefaultPattern);

        private static readonly TokenDefinition[] ExplodeComputedTokens =
        {
            new TokenDefinition("Counter",    AppStrings.T("naming.computed.explodeByTrade.counter.label"),    TokenOrigin.Computed, TokenSubject.Target, TokenEntity.View),
            new TokenDefinition("SourceView", AppStrings.T("naming.computed.explodeByTrade.sourceView.label"), TokenOrigin.Computed, TokenSubject.Source, TokenEntity.View),
            new TokenDefinition("Trade",      AppStrings.T("naming.computed.explodeByTrade.trade.label"),      TokenOrigin.Computed, TokenSubject.Target, TokenEntity.View),
        };

        // ── Validation ───────────────────────────────────────────────────────────
        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public ExplodeViewByTradeViewModel(
            ExplodeViewByTradeEventHandler? handler,
            ExternalEvent?                  externalEvent,
            IEnumerable<long>?              eligibleViewIds,
            BrowserTree?             browserTree,
            IEnumerable<(string Id, string Label, bool HasFilters)>? trades,
            IReadOnlyDictionary<long, string>? viewNames = null)
        {
            _handler     = handler;
            _event       = externalEvent;
            _browserTree = browserTree ?? new BrowserTree();

            if (eligibleViewIds != null)
                _eligibleViewIds.AddRange(eligibleViewIds);

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
                    // Guarantee a unique display key even if two trades share a label.
                    string display = t.Label;
                    int n = 2;
                    while (_labelToTradeId.ContainsKey(display))
                        display = $"{t.Label} ({n++})";
                    _labelToTradeId[display] = t.Id;
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildS1();
                case "S2": return BuildS2();
                case "S3": return BuildS3();
                case "S4": return null; // framework renders the review (IReviewableTool)
                default:   return null;
            }
        }

        // ── S1: source 3D view ─────────────────────────────────────────────────
        private FrameworkElement BuildS1()
        {
            if (_eligibleViewIds.Count == 0)
                return Hint(AppStrings.T("explode.byTrade.labels.noViews"));

            var picker = new BrowserTreePicker
            {
                Height         = 300,
                SingleSelect   = true,
                AccessibleName = AppStrings.T("explode.byTrade.labels.sourceView"),
            };
            picker.SelectionChanged += ids =>
            {
                _sourceViewId = ids.FirstOrDefault();
                OnValidationChanged();
            };
            picker.SetTree(_browserTree, _eligibleViewIds,
                _sourceViewId != 0 ? new[] { _sourceViewId } : null);
            return picker;
        }

        // ── S2: trades to explode ──────────────────────────────────────────────
        private FrameworkElement BuildS2()
        {
            if (_labelToTradeId.Count == 0)
                return Hint(AppStrings.T("explode.byTrade.labels.noTrades"));

            var outer = new StackPanel();

            var tabs = new MultiSelectTabs { Height = 280 };
            // Map current trade-id selection back to display labels for re-entry.
            var initialDisplay = _labelToTradeId
                .Where(kv => _selectedTradeIds.Contains(kv.Value))
                .Select(kv => kv.Key)
                .ToList();

            // Subscribe BEFORE SetGroups — its end-of-setup SelectionChanged seeds the mirror.
            tabs.SelectionChanged += items =>
            {
                _selectedTradeIds = items
                    .Where(_labelToTradeId.ContainsKey)
                    .Select(label => _labelToTradeId[label])
                    .Distinct()
                    .ToList();
                OnValidationChanged();
            };
            tabs.SetGroups(
                new Dictionary<string, List<string>> { ["Trades"] = _labelToTradeId.Keys.ToList() },
                initialDisplay);
            outer.Children.Add(tabs);

            if (_tradesWithoutFilters.Count > 0)
            {
                outer.Children.Add(new FrameworkElement { Height = 8 });
                outer.Children.Add(Hint(AppStrings.T("explode.byTrade.labels.tradesHidden", _tradesWithoutFilters.Count, string.Join(", ", _tradesWithoutFilters))));
            }

            return outer;
        }

        // ── S3: options ────────────────────────────────────────────────────────
        private FrameworkElement BuildS3()
        {
            var tog = new ToggleSwitches();
            tog.SetItems(new List<ToggleItem>
            {
                new ToggleItem
                {
                    Id        = "order",
                    Label     = AppStrings.T("explode.byTrade.labels.orderLabel"),
                    Desc      = AppStrings.T("explode.byTrade.labels.orderDesc"),
                    DefaultOn = _orderByElevation,
                },
                new ToggleItem
                {
                    Id        = "number",
                    Label     = AppStrings.T("explode.byTrade.labels.numberLabel"),
                    Desc      = AppStrings.T("explode.byTrade.labels.numberDesc"),
                    DefaultOn = _numberPrefix,
                },
                new ToggleItem
                {
                    Id        = "color",
                    Label     = AppStrings.T("explode.byTrade.labels.colorLabel"),
                    Desc      = AppStrings.T("explode.byTrade.labels.colorDesc"),
                    DefaultOn = _applyColorOverride,
                },
                new ToggleItem
                {
                    Id        = "hide",
                    Label     = AppStrings.T("explode.byTrade.labels.hideLabel"),
                    Desc      = AppStrings.T("explode.byTrade.labels.hideDesc"),
                    DefaultOn = _hideOthers,
                },
            });
            tog.StateChanged += state =>
            {
                _orderByElevation   = state.TryGetValue("order",  out bool ov) && ov;
                _numberPrefix       = state.TryGetValue("number", out bool nv) && nv;
                _applyColorOverride = state.TryGetValue("color",  out bool cv) && cv;
                _hideOthers         = state.TryGetValue("hide",   out bool hv) && hv;
                OnValidationChanged();
            };

            var outer = new StackPanel();
            outer.Children.Add(tog);

            var patLabel = new WpfTextBlock { Text = AppStrings.T("explode.byTrade.labels.namePattern"), Margin = new Thickness(0, 14, 0, 6) };
            patLabel.SetResourceReference(WpfTextBlock.FontSizeProperty,   "LemoineFS_SM");
            patLabel.SetResourceReference(WpfTextBlock.ForegroundProperty, "LemoineTextDim");
            patLabel.SetResourceReference(WpfTextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(patLabel);

            var tokens = NamingTokenRegistry.TokensFor(TokenEntity.View, hasSource: true, ExplodeComputedTokens);
            var tokenInput = new TokenInput(tokens, DefaultPattern) { Text = _namePattern };
            tokenInput.SetPreview(pattern =>
            {
                string srcName = SourceViewName() ?? AppStrings.T("explode.byTrade.labels.exSource");
                string trade   = _labelToTradeId
                    .Where(kv => _selectedTradeIds.Contains(kv.Value))
                    .Select(kv => kv.Key)
                    .FirstOrDefault()
                    ?? AppStrings.T("explode.byTrade.labels.exTrade");

                var ctx = new TokenContext();
                ctx.Computed["Counter"]    = _numberPrefix ? "01" : "";
                ctx.Computed["SourceView"] = srcName;
                ctx.Computed["Trade"]      = trade;
                string resolved = TokenResolver.Resolve(pattern, ctx);
                return resolved.Trim().TrimStart('_', ' ', '-').Trim();
            });
            tokenInput.TextChanged += (s, e) =>
            {
                _namePattern = tokenInput.Text;
                NamingPatternStore.Instance.Set(ToolId, _namePattern);
            };
            ValidationChanged += (s, e) => tokenInput.RefreshPreview();
            outer.Children.Add(tokenInput);

            return outer;
        }

        // ── Review (IReviewableTool) ────────────────────────────────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("source", AppStrings.T("explode.byTrade.review.itemSource")),
            ("trades", AppStrings.T("explode.byTrade.review.itemTrades")),
            ("order",  AppStrings.T("explode.byTrade.review.itemOrder")),
            ("number", AppStrings.T("explode.byTrade.review.itemNumber")),
            ("color",  AppStrings.T("explode.byTrade.review.itemColor")),
            ("hide",   AppStrings.T("explode.byTrade.review.itemHide")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["source"] = _sourceViewId == 0 ? "—" : (SourceViewName() ?? AppStrings.T("explode.byTrade.review.oneView")),
            ["trades"] = _selectedTradeIds.Count == 0 ? "—"
                : AppStrings.T("explode.byTrade.review.tradeCount", _selectedTradeIds.Count),
            ["order"]  = _orderByElevation ? AppStrings.T("explode.byTrade.review.yes") : AppStrings.T("explode.byTrade.review.no"),
            ["number"] = _numberPrefix ? AppStrings.T("explode.byTrade.review.yes") : AppStrings.T("explode.byTrade.review.no"),
            ["color"]  = _applyColorOverride ? AppStrings.T("explode.byTrade.review.yes") : AppStrings.T("explode.byTrade.review.no"),
            ["hide"]   = _hideOthers ? AppStrings.T("explode.byTrade.review.yes") : AppStrings.T("explode.byTrade.review.no"),
        };

        public IList<string>? ReviewChips => _selectedTradeIds.Count == 0 ? null
            : _labelToTradeId.Where(kv => _selectedTradeIds.Contains(kv.Value))
                             .Select(kv => kv.Key).ToList();

        public string? ReviewNote =>
            AppStrings.T("explode.byTrade.review.note");

        public string? ReviewWarning => null;

        // ── Validation / summaries ───────────────────────────────────────────────
        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _sourceViewId != 0;
            if (stepId == "S2") return _selectedTradeIds.Count > 0;
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1")
                return _sourceViewId == 0 ? "—" : (SourceViewName() ?? AppStrings.T("explode.byTrade.summaries.oneView"));
            if (stepId == "S2")
                return _selectedTradeIds.Count == 0 ? "—"
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
            return "—";
        }

        // ── Run ────────────────────────────────────────────────────────────────
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _resultChips = null;

            _handler!.SourceViewId       = new ElementId(_sourceViewId);
            _handler.SelectedTradeIds    = _selectedTradeIds.ToList();
            _handler.OrderByElevation    = _orderByElevation;
            _handler.NumberPrefix        = _numberPrefix;
            _handler.ApplyColorOverride  = _applyColorOverride;
            _handler.HideOthers          = _hideOthers;
            _handler.NamePattern         = _namePattern;
            _handler.PushLog             = pushLog;
            _handler.OnProgress          = onProgress;
            _handler.OnComplete          = onComplete;
            _handler.OnResultChips       = chips => _resultChips = chips;

            pushLog(AppStrings.T("explode.byTrade.log.starting"), "info");
            _event!.Raise();
        }

        // ── Cleanup ──────────────────────────────────────────────────────────────
        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.PushLog       = null;
            _handler.OnProgress    = null;
            _handler.OnComplete    = null;
            _handler.OnResultChips = null;
        }

        // ── Helpers ──────────────────────────────────────────────────────────────
        private string? SourceViewName()
            => _sourceViewId != 0 && _viewNames.TryGetValue(_sourceViewId, out var n) ? n : null;

        private static WpfTextBlock Hint(string text)
        {
            var tb = new WpfTextBlock
            {
                Text         = text,
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
            };
            tb.SetResourceReference(WpfTextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(WpfTextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(WpfTextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }
    }
}
