using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.AutoFilters
{
    /// <summary>
    /// ViewModel for applying existing project filters to multiple views at once.
    /// Step 1 — select which filters to apply.
    /// Step 2 — select target views.
    /// Step 3 — options + Review &amp; Run.
    ///
    /// Color overrides are sourced from AutoFiltersSettings / MepColorMap,
    /// matching the same logic as Auto Filters.
    /// </summary>
    public class ApplyFiltersToViewsViewModel : ILemoineTool, ILemoineReviewable, ILemoineRunResult, ILemoineToolCleanup
    {
        // Self-describing result label for the run strip (see ILemoineRunResult).
        public string? ResultNoun => "applied";
        public System.Collections.Generic.IReadOnlyList<LemoineTools.Lemoine.ResultChip>? ResultChips => null;

        // ── ILemoineTool identity ──────────────────────────────────────────────
        public string Title    => LemoineStrings.T("autofilters.applyFilters.title");
        public string RunLabel => LemoineStrings.T("autofilters.applyFilters.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", LemoineStrings.T("autofilters.applyFilters.steps.S1"),     required: true),
            new StepDefinition("S2", LemoineStrings.T("autofilters.applyFilters.steps.S2"),       required: true),
            new StepDefinition("S3", LemoineStrings.T("autofilters.applyFilters.steps.S3"),            required: false),
            new StepDefinition("S4", LemoineStrings.T("autofilters.applyFilters.steps.S4"),       required: false),
        };

        // ── State ──────────────────────────────────────────────────────────────
        private readonly IReadOnlyList<string> _allFilterNames;
        private readonly IReadOnlyList<(long Id, string Name, string ViewType)> _allViews;

        private List<string> _selectedFilters = new List<string>();
        // S2 selection: view ElementId values straight from the browser-tree picker.
        private List<long>   _selectedViewIds = new List<long>();
        private readonly LemoineBrowserTree _browserTree;
        private Dictionary<string, bool> _optionState = new Dictionary<string, bool>
        {
            ["overwrite"] = false,
            ["color"]     = true,
        };

        // ── Validation change notification ─────────────────────────────────────
        public event EventHandler? ValidationChanged;

        // Null the callbacks parked on the static handler so this VM isn't retained after close.
        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.PushLog    = null;
            _handler.OnProgress = null;
            _handler.OnComplete = null;
        }

        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── ExternalEvent wiring ────────────────────────────────────────────────
        private readonly ApplyFiltersToViewsEventHandler   _handler;
        private readonly Autodesk.Revit.UI.ExternalEvent   _event;

        public ApplyFiltersToViewsViewModel(
            ApplyFiltersToViewsEventHandler handler,
            Autodesk.Revit.UI.ExternalEvent externalEvent,
            IReadOnlyList<string> allFilterNames,
            IReadOnlyList<(long Id, string Name, string ViewType)> allViews,
            LemoineBrowserTree? browserTree = null)
        {
            _handler        = handler;
            _event          = externalEvent;
            _allFilterNames = allFilterNames ?? new List<string>();
            _allViews       = allViews       ?? new List<(long, string, string)>();
            _browserTree    = browserTree    ?? new LemoineBrowserTree();
        }

        // ═══════════════════════════════════════════════════════════════════════
        // GetStepContent
        // ═══════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "S1")
            {
                if (_allFilterNames.Count == 0)
                {
                    // FIX: empty-state text uses LemoineText + italic, not LemoineTextDim
                    var none = new TextBlock
                    {
                        Text         = LemoineStrings.T("autofilters.applyFilters.labels.noFilters"),
                        TextWrapping = TextWrapping.Wrap,
                        FontStyle    = FontStyles.Italic,
                    };
                    none.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    none.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                    none.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    return none;
                }

                var groups = BuildFilterGroups(_allFilterNames);
                var tabs = new LemoineMultiSelectTabs();
                tabs.SetGroups(groups);
                tabs.SelectionChanged += selected =>
                {
                    _selectedFilters = new List<string>(selected);
                    OnValidationChanged();
                };
                return tabs;
            }

            if (stepId == "S2")
            {
                if (_allViews.Count == 0)
                {
                    // FIX: empty-state text uses LemoineText + italic, not LemoineTextDim
                    var none = new TextBlock
                    {
                        Text         = LemoineStrings.T("autofilters.applyFilters.labels.noViews"),
                        TextWrapping = TextWrapping.Wrap,
                        FontStyle    = FontStyles.Italic,
                    };
                    none.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    none.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                    none.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    return none;
                }

                var picker = new LemoineBrowserTreePicker
                {
                    Height         = 300,
                    AccessibleName = LemoineStrings.T("autofilters.applyFilters.labels.pickerName"),
                };
                // Subscribe BEFORE SetTree — its end-of-setup SelectionChanged seeds the mirror list.
                picker.SelectionChanged += ids =>
                {
                    _selectedViewIds = ids.ToList();
                    OnValidationChanged();
                };
                // Pass the current selection so re-entering the step keeps prior picks.
                picker.SetTree(_browserTree,
                    _allViews.Select(v => v.Id),
                    _selectedViewIds.ToList());
                return picker;
            }

            if (stepId == "S3")
            {
                var outer = new StackPanel();

                var toggles = new LemoineToggleSwitches();
                toggles.SetItems(new List<ToggleItem>
                {
                    new ToggleItem
                    {
                        Id        = "overwrite",
                        Label     = LemoineStrings.T("autofilters.applyFilters.labels.overwriteLabel"),
                        Desc      = LemoineStrings.T("autofilters.applyFilters.labels.overwriteDesc"),
                        DefaultOn = false,
                    },
                    new ToggleItem
                    {
                        Id        = "color",
                        Label     = LemoineStrings.T("autofilters.applyFilters.labels.colorLabel"),
                        Desc      = LemoineStrings.T("autofilters.applyFilters.labels.colorDesc"),
                        DefaultOn = true,
                    },
                }, _optionState.Count > 0 ? _optionState : null);

                toggles.StateChanged += state =>
                {
                    _optionState = new Dictionary<string, bool>(state);
                    OnValidationChanged();
                };
                outer.Children.Add(toggles);
                return outer;
            }

            return null;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // IsValid / SummaryFor / Run
        // ═══════════════════════════════════════════════════════════════════════
        // ── ILemoineReviewable (P3) — framework renders the final review step ─
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("filters",   LemoineStrings.T("autofilters.applyFilters.review.itemFilters")),
            ("views",     LemoineStrings.T("autofilters.applyFilters.review.itemViews")),
            ("overwrite", LemoineStrings.T("autofilters.applyFilters.review.itemOverwrite")),
            ("color",     LemoineStrings.T("autofilters.applyFilters.review.itemColor")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["filters"]   = LemoineStrings.T("autofilters.applyFilters.review.filtersValue", _selectedFilters.Count),
            ["views"]     = LemoineStrings.T("autofilters.applyFilters.review.viewsValue", _selectedViewIds.Count),
            ["overwrite"] = GetOpt("overwrite") ? LemoineStrings.T("autofilters.applyFilters.review.overwriteYes") : LemoineStrings.T("autofilters.applyFilters.review.overwriteNo"),
            ["color"]     = GetOpt("color")     ? LemoineStrings.T("autofilters.applyFilters.review.colorYes")    : LemoineStrings.T("autofilters.applyFilters.review.colorNo"),
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => null;
        public string?        ReviewWarning => null;

        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _selectedFilters.Count > 0;
            if (stepId == "S2") return _selectedViewIds.Count > 0;
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1") return LemoineStrings.T("autofilters.applyFilters.summaries.S1", _selectedFilters.Count);
            if (stepId == "S2") return LemoineStrings.T("autofilters.applyFilters.summaries.S2", _selectedViewIds.Count);
            if (stepId == "S3") return LemoineStrings.T("autofilters.applyFilters.summaries.S3");
            return "—";
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _handler.SelectedFilterNames = new List<string>(_selectedFilters);
            // The picker hands back view ElementId values directly — no label resolution.
            _handler.SelectedViewIds     = new List<long>(_selectedViewIds);
            _handler.OverwriteExisting   = GetOpt("overwrite");
            _handler.ApplyColorOverrides = GetOpt("color");
            _handler.PushLog             = pushLog;
            _handler.OnProgress          = onProgress;
            _handler.OnComplete          = onComplete;

            pushLog(LemoineStrings.T("autofilters.applyFilters.log.applying", _selectedFilters.Count, _selectedViewIds.Count), "info");
            _event.Raise();
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private bool GetOpt(string key) =>
            _optionState.TryGetValue(key, out bool v) ? v : key == "color";

        // Group by the trade that owns the matching rule; unmatched → "Others".
        private static Dictionary<string, List<string>> BuildFilterGroups(
            IReadOnlyList<string> names)
            => AutoFiltersSettings.GroupFilterNamesByTrade(names);
    }
}
