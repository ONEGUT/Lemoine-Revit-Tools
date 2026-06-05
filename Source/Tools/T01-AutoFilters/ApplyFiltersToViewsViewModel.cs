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
    public class ApplyFiltersToViewsViewModel : ILemoineTool, ILemoineReviewable
    {
        // ── ILemoineTool identity ──────────────────────────────────────────────
        public string Title    => "Apply Filters to Views";
        public string RunLabel => "Apply to Views →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Filters",     required: true),
            new StepDefinition("S2", "Select Views",       required: true),
            new StepDefinition("S3", "Options",            required: false),
            new StepDefinition("S4", "Review & Run",       required: false),
        };

        // ── State ──────────────────────────────────────────────────────────────
        private readonly IReadOnlyList<string> _allFilterNames;
        private readonly IReadOnlyList<(long Id, string Name, string ViewType)> _allViews;

        private List<string> _selectedFilters = new List<string>();
        // S2 selection is keyed by the multiselect's display label; the map below
        // resolves each label back to its view ElementId (the value passed to the
        // handler). Labels are made unique in GetStepContent so colliding view names
        // remain individually targetable.
        private List<string> _selectedViews   = new List<string>();
        private readonly Dictionary<string, long> _viewLabelToId =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private Dictionary<string, bool> _optionState = new Dictionary<string, bool>
        {
            ["overwrite"] = false,
            ["color"]     = true,
        };

        // ── Validation change notification ─────────────────────────────────────
        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── ExternalEvent wiring ────────────────────────────────────────────────
        private readonly ApplyFiltersToViewsEventHandler   _handler;
        private readonly Autodesk.Revit.UI.ExternalEvent   _event;

        public ApplyFiltersToViewsViewModel(
            ApplyFiltersToViewsEventHandler handler,
            Autodesk.Revit.UI.ExternalEvent externalEvent,
            IReadOnlyList<string> allFilterNames,
            IReadOnlyList<(long Id, string Name, string ViewType)> allViews)
        {
            _handler        = handler;
            _event          = externalEvent;
            _allFilterNames = allFilterNames ?? new List<string>();
            _allViews       = allViews       ?? new List<(long, string, string)>();
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
                        Text         = "No ParameterFilterElements found in this project. Run Auto Filters first.",
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
                        Text         = "No views supporting graphic overrides found in this project.",
                        TextWrapping = TextWrapping.Wrap,
                        FontStyle    = FontStyles.Italic,
                    };
                    none.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    none.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                    none.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    return none;
                }

                // View names are NOT unique in Revit (a Floor Plan and its RCP, or two
                // 3D views, can share a name), but the multiselect keys items by their
                // display string. Disambiguate any colliding name with its ElementId so
                // each view stays an independent, individually-targetable item; the
                // label→id map is the authoritative key handed to the handler.
                var nameCounts = _allViews
                    .GroupBy(v => v.Name, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

                _viewLabelToId.Clear();
                var groups = new Dictionary<string, List<string>>();
                foreach (var (id, name, vt) in _allViews)
                {
                    string label = nameCounts.TryGetValue(name, out int n) && n > 1
                        ? $"{name}  [#{id}]"
                        : name;
                    _viewLabelToId[label] = id;
                    if (!groups.ContainsKey(vt)) groups[vt] = new List<string>();
                    groups[vt].Add(label);
                }

                var tabs = new LemoineMultiSelectTabs();
                // Pass the current selection so re-entering the step keeps prior picks.
                tabs.SetGroups(groups, _selectedViews);
                tabs.SelectionChanged += selected =>
                {
                    _selectedViews = new List<string>(selected);
                    OnValidationChanged();
                };
                return tabs;
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
                        Label     = "Overwrite Existing Overrides",
                        Desc      = "Re-apply color overrides to views that already have the filter. " +
                                    "Off = skip filters already present on a view.",
                        DefaultOn = false,
                    },
                    new ToggleItem
                    {
                        Id        = "color",
                        Label     = "Apply Color Overrides",
                        Desc      = "Set fill and line colors from the MEP color map. " +
                                    "Off = add filters without any graphic override.",
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
            ("filters",   "Filters"),
            ("views",     "Target Views"),
            ("overwrite", "Overwrite"),
            ("color",     "Color Overrides"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["filters"]   = $"{_selectedFilters.Count} selected",
            ["views"]     = $"{_selectedViews.Count} selected",
            ["overwrite"] = GetOpt("overwrite") ? "Yes — replace overrides" : "No — skip if present",
            ["color"]     = GetOpt("color")     ? "Apply from color map"    : "None",
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => null;
        public string?        ReviewWarning => null;

        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _selectedFilters.Count > 0;
            if (stepId == "S2") return _selectedViews.Count > 0;
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1") return $"{_selectedFilters.Count} filter(s) selected";
            if (stepId == "S2") return $"{_selectedViews.Count} view(s) selected";
            if (stepId == "S3") return "Ready to run";
            return "—";
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _handler.SelectedFilterNames = new List<string>(_selectedFilters);
            // Resolve the selected display labels back to view ElementIds. A label
            // missing from the map can only happen if state desynced; skip it rather
            // than guessing a view by name.
            _handler.SelectedViewIds = _selectedViews
                .Where(lbl => _viewLabelToId.ContainsKey(lbl))
                .Select(lbl => _viewLabelToId[lbl])
                .ToList();
            if (_handler.SelectedViewIds.Count < _selectedViews.Count)
                pushLog($"{_selectedViews.Count - _handler.SelectedViewIds.Count} selected view(s) could not be resolved and were skipped.", "fail");
            _handler.OverwriteExisting   = GetOpt("overwrite");
            _handler.ApplyColorOverrides = GetOpt("color");
            _handler.PushLog             = pushLog;
            _handler.OnProgress          = onProgress;
            _handler.OnComplete          = onComplete;

            pushLog($"Applying {_selectedFilters.Count} filter(s) to {_selectedViews.Count} view(s)…", "info");
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
