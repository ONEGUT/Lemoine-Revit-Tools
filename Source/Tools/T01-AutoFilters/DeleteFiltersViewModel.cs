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
    /// ViewModel for removing selected filters from the active view.
    /// The list of applied filters is captured on the main thread in the launch
    /// command and passed into this ViewModel for display.
    /// </summary>
    public class DeleteFiltersViewModel : ILemoineTool, ILemoineReviewable
    {
        // ── ILemoineTool identity ──────────────────────────────────────────────
        public string Title    => "Remove Filters from View";
        public string RunLabel => "Remove Selected →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Filters", required: true),
            new StepDefinition("S2", "Review & Run",   required: false),
        };

        // ── State ──────────────────────────────────────────────────────────────
        private readonly string                 _viewName;
        private readonly IReadOnlyList<string>  _allFilterNames;
        private List<string>                    _selectedNames = new List<string>();

        // ── Validation change notification ─────────────────────────────────────
        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── ExternalEvent wiring ────────────────────────────────────────────────
        private readonly DeleteFiltersEventHandler          _handler;
        private readonly Autodesk.Revit.UI.ExternalEvent    _event;

        public DeleteFiltersViewModel(
            DeleteFiltersEventHandler handler,
            Autodesk.Revit.UI.ExternalEvent externalEvent,
            IReadOnlyList<string> filterNames,
            string viewName)
        {
            _handler        = handler;
            _event          = externalEvent;
            _allFilterNames = filterNames ?? new List<string>();
            _viewName       = viewName    ?? "Active View";
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
                        Text         = "No filters are currently applied to the active view.",
                        TextWrapping = TextWrapping.Wrap,
                        FontStyle    = FontStyles.Italic,
                    };
                    none.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    none.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                    none.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    return none;
                }

                // Group by discipline prefix
                var groups = new Dictionary<string, List<string>>();
                foreach (var name in _allFilterNames)
                {
                    int sep  = name.IndexOf(" - ");
                    string g = sep >= 0 ? name.Substring(0, sep).Trim() : "Other";
                    if (!groups.ContainsKey(g)) groups[g] = new List<string>();
                    groups[g].Add(name);
                }

                var tabs = new LemoineMultiSelectTabs();
                tabs.SetGroups(groups);
                tabs.SelectionChanged += selected =>
                {
                    _selectedNames = new List<string>(selected);
                    OnValidationChanged();
                };

                return tabs;
            }

            if (stepId == "S2")
                return null; // framework renders review (ILemoineReviewable)

            return null;
        }


                // ── ILemoineReviewable (P3) — framework renders the review step ───
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("view",      "View"),
            ("remove",    "Filters to Remove"),
            ("remaining", "Remaining"),
            ("action",    "Action"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["view"]      = _viewName,
            ["remove"]    = $"{_selectedNames.Count} selected",
            ["remaining"] = $"{_allFilterNames.Count - _selectedNames.Count} will stay",
            ["action"]    = "Detach from view (not deleted)",
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => "Selected filters will be detached from the active view only — they " +
            "will NOT be deleted from the project and can be re-applied later.";
        public string?        ReviewWarning => null;

        // ═══════════════════════════════════════════════════════════════════════
        // IsValid / SummaryFor / Run
        // ═══════════════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _selectedNames.Count > 0;
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1") return $"{_selectedNames.Count} filter(s) selected";
            if (stepId == "S2") return "Ready to run";
            return "—";
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _handler.SelectedFilterNames = new List<string>(_selectedNames);
            _handler.PushLog             = pushLog;
            _handler.OnProgress          = onProgress;
            _handler.OnComplete          = onComplete;

            pushLog($"Removing {_selectedNames.Count} filter(s) from view…", "info");
            _event.Raise();
        }
    }
}
