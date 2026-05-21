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
    public class ApplyFiltersToViewsViewModel : ILemoineTool
    {
        // ── ILemoineTool identity ──────────────────────────────────────────────
        public string Title    => "Apply Filters to Views";
        public string RunLabel => "Apply to Views →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Filters",     required: true),
            new StepDefinition("S2", "Select Views",       required: true),
            new StepDefinition("S3", "Options & Review",   required: false),
        };

        // ── State ──────────────────────────────────────────────────────────────
        private readonly IReadOnlyList<string> _allFilterNames;
        private readonly IReadOnlyList<(string Name, string ViewType)> _allViews;

        private List<string> _selectedFilters = new List<string>();
        private List<string> _selectedViews   = new List<string>();
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
            IReadOnlyList<(string Name, string ViewType)> allViews)
        {
            _handler        = handler;
            _event          = externalEvent;
            _allFilterNames = allFilterNames ?? new List<string>();
            _allViews       = allViews       ?? new List<(string, string)>();
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

                // Group by ViewType
                var groups = new Dictionary<string, List<string>>();
                foreach (var (name, vt) in _allViews)
                {
                    if (!groups.ContainsKey(vt)) groups[vt] = new List<string>();
                    groups[vt].Add(name);
                }

                var tabs = new LemoineMultiSelectTabs();
                tabs.SetGroups(groups);
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

                outer.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Height  = 1,
                    Margin  = new Thickness(0, 10, 0, 10),
                });

                // ── Review summary via reusable LemoineReviewSummary ──────────────
                var reviewItems = new List<(string id, string label)>
                {
                    ("filters",  "Filters"),
                    ("views",    "Target Views"),
                    ("overwrite","Overwrite"),
                    ("color",    "Color Overrides"),
                };

                IDictionary<string, string> BuildValues() => new Dictionary<string, string>
                {
                    ["filters"]   = $"{_selectedFilters.Count} selected",
                    ["views"]     = $"{_selectedViews.Count} selected",
                    ["overwrite"] = GetOpt("overwrite") ? "Yes — replace overrides" : "No — skip if present",
                    ["color"]     = GetOpt("color")     ? "Apply from color map"    : "None",
                };

                var review = new LemoineReviewSummary();
                review.SetItems(reviewItems, BuildValues());
                // Refresh card values whenever options or selections change
                ValidationChanged += (s, e) => review.SetItems(reviewItems, BuildValues());
                outer.Children.Add(review);

                return outer;
            }

            return null;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // IsValid / SummaryFor / Run
        // ═══════════════════════════════════════════════════════════════════════
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
            _handler.SelectedViewNames   = new List<string>(_selectedViews);
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

        private static Dictionary<string, List<string>> BuildFilterGroups(
            IReadOnlyList<string> names)
        {
            var groups = new Dictionary<string, List<string>>();
            foreach (var name in names)
            {
                int sep  = name.IndexOf(" - ");
                string g = sep >= 0 ? name.Substring(0, sep).Trim() : "Other";
                if (!groups.ContainsKey(g)) groups[g] = new List<string>();
                groups[g].Add(name);
            }
            return groups;
        }
    }
}
