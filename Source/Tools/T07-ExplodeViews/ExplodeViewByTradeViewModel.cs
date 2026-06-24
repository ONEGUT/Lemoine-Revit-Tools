using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;
using WpfTextBlock = System.Windows.Controls.TextBlock;

namespace LemoineTools.Tools.ExplodeViews
{
    /// <summary>
    /// Step-flow ViewModel for the Explode 3D View by Trade tool. Picks one source 3D view
    /// and the AutoFilters trades to explode, then duplicates the view once per trade with
    /// only that trade's filters visible. Output views are ordered by element elevation.
    /// </summary>
    public class ExplodeViewByTradeViewModel
        : ILemoineTool, ILemoineReviewable, ILemoineRunResult, ILemoineToolCleanup
    {
        // ── Identity ─────────────────────────────────────────────────────────────
        public string Title    => "Explode 3D View by Trade";
        public string RunLabel => "Explode View →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Source 3D View", required: true),
            new StepDefinition("S2", "Select Trades",         required: true),
            new StepDefinition("S3", "Options",               required: false),
            new StepDefinition("S4", "Review & Run",          required: false),
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
        private readonly LemoineBrowserTree _browserTree;
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

        // ── Validation ───────────────────────────────────────────────────────────
        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public ExplodeViewByTradeViewModel(
            ExplodeViewByTradeEventHandler? handler,
            ExternalEvent?                  externalEvent,
            IEnumerable<long>?              eligibleViewIds,
            LemoineBrowserTree?             browserTree,
            IEnumerable<(string Id, string Label, bool HasFilters)>? trades,
            IReadOnlyDictionary<long, string>? viewNames = null)
        {
            _handler     = handler;
            _event       = externalEvent;
            _browserTree = browserTree ?? new LemoineBrowserTree();

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
                case "S4": return null; // framework renders the review (ILemoineReviewable)
                default:   return null;
            }
        }

        // ── S1: source 3D view ─────────────────────────────────────────────────
        private FrameworkElement BuildS1()
        {
            if (_eligibleViewIds.Count == 0)
                return Hint("No 3D views found in this document.");

            var picker = new LemoineBrowserTreePicker
            {
                Height         = 300,
                SingleSelect   = true,
                AccessibleName = "Source 3D view",
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
                return Hint("No AutoFilters trades have created filters in this project. "
                    + "Open AutoFilters and run \"Create Filters\" first, then reopen this tool.");

            var outer = new StackPanel();

            var tabs = new LemoineMultiSelectTabs { Height = 280 };
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
                outer.Children.Add(Hint(
                    $"{_tradesWithoutFilters.Count} trade(s) are hidden because their filters aren't "
                    + "created in this project yet: " + string.Join(", ", _tradesWithoutFilters)
                    + ". Run AutoFilters → Create Filters to include them."));
            }

            return outer;
        }

        // ── S3: options ────────────────────────────────────────────────────────
        private FrameworkElement BuildS3()
        {
            var tog = new LemoineToggleSwitches();
            tog.SetItems(new List<ToggleItem>
            {
                new ToggleItem
                {
                    Id        = "order",
                    Label     = "Order views by elevation",
                    Desc      = "Scan each trade's element elevations (host + linked) and stack the output "
                              + "views top → bottom by median height. Off keeps the AutoFilters order.",
                    DefaultOn = _orderByElevation,
                },
                new ToggleItem
                {
                    Id        = "number",
                    Label     = "Number-prefix view names",
                    Desc      = "Prefix each new view name with 01_, 02_… so the Project Browser sorts the stack.",
                    DefaultOn = _numberPrefix,
                },
                new ToggleItem
                {
                    Id        = "color",
                    Label     = "Apply trade colour overrides",
                    Desc      = "Apply each trade's AutoFilters colour/line overrides to the trade shown in its view.",
                    DefaultOn = _applyColorOverride,
                },
            });
            tog.StateChanged += state =>
            {
                _orderByElevation   = state.TryGetValue("order",  out bool ov) && ov;
                _numberPrefix       = state.TryGetValue("number", out bool nv) && nv;
                _applyColorOverride = state.TryGetValue("color",  out bool cv) && cv;
                OnValidationChanged();
            };
            return tog;
        }

        // ── Review (ILemoineReviewable) ────────────────────────────────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("source", "Source View"),
            ("trades", "Trades"),
            ("order",  "Order by Elevation"),
            ("number", "Number Prefix"),
            ("color",  "Colour Overrides"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["source"] = _sourceViewId == 0 ? "—" : (SourceViewName() ?? "1 view"),
            ["trades"] = _selectedTradeIds.Count == 0 ? "—"
                : $"{_selectedTradeIds.Count} trade{(_selectedTradeIds.Count == 1 ? "" : "s")}",
            ["order"]  = _orderByElevation ? "Yes" : "No",
            ["number"] = _numberPrefix ? "Yes" : "No",
            ["color"]  = _applyColorOverride ? "Yes" : "No",
        };

        public IList<string>? ReviewChips => _selectedTradeIds.Count == 0 ? null
            : _labelToTradeId.Where(kv => _selectedTradeIds.Contains(kv.Value))
                             .Select(kv => kv.Key).ToList();

        public string? ReviewNote =>
            "One 3D view is created per selected trade at the source view's exact camera angle and "
            + "section box. Each view shows its own trade and hides the other selected trades via "
            + "AutoFilters view filters. Linked elements are only hidden/shown where the link is "
            + "displayed \"By Host View\".";

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
                return _sourceViewId == 0 ? "—" : (SourceViewName() ?? "1 view selected");
            if (stepId == "S2")
                return _selectedTradeIds.Count == 0 ? "—"
                    : $"{_selectedTradeIds.Count} trade{(_selectedTradeIds.Count == 1 ? "" : "s")} selected";
            if (stepId == "S3")
            {
                var parts = new List<string>();
                parts.Add(_orderByElevation ? "By elevation" : "Config order");
                if (_numberPrefix)       parts.Add("Numbered");
                if (_applyColorOverride) parts.Add("Coloured");
                return string.Join(" · ", parts);
            }
            if (stepId == "S4") return "Ready to run";
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
            _handler.PushLog             = pushLog;
            _handler.OnProgress          = onProgress;
            _handler.OnComplete          = onComplete;
            _handler.OnResultChips       = chips => _resultChips = chips;

            pushLog("Starting Explode 3D View by Trade…", "info");
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
