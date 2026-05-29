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
    /// ViewModel for permanently deleting ParameterFilterElements from the project.
    /// Step 1 — select which filters to delete (grouped by discipline prefix).
    /// Step 2 — Review &amp; Run (with explicit warning card).
    /// </summary>
    public class DeleteFiltersFromProjectViewModel : ILemoineTool, ILemoineReviewable
    {
        // ── ILemoineTool identity ──────────────────────────────────────────────
        public string Title    => "Delete Filters from Project";
        public string RunLabel => "Permanently Delete →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Filters", required: true),
            new StepDefinition("S2", "Review & Run",   required: false),
        };

        // ── State ──────────────────────────────────────────────────────────────
        private readonly IReadOnlyList<string> _allFilterNames;
        private List<string> _selectedNames = new List<string>();

        // ── Validation change notification ─────────────────────────────────────
        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── ExternalEvent wiring ────────────────────────────────────────────────
        private readonly DeleteFiltersFromProjectEventHandler   _handler;
        private readonly Autodesk.Revit.UI.ExternalEvent        _event;

        public DeleteFiltersFromProjectViewModel(
            DeleteFiltersFromProjectEventHandler handler,
            Autodesk.Revit.UI.ExternalEvent externalEvent,
            IReadOnlyList<string> allFilterNames)
        {
            _handler        = handler;
            _event          = externalEvent;
            _allFilterNames = allFilterNames ?? new List<string>();
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
                        Text         = "No ParameterFilterElements found in this project.",
                        TextWrapping = TextWrapping.Wrap,
                        FontStyle    = FontStyles.Italic,
                    };
                    none.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    none.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                    none.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    return none;
                }

                var outer = new StackPanel();

                var warn = new LemoineWarnBanner(
                    "⚠  Deleted filters are permanently removed from the project " +
                    "and cannot be recovered. They will be removed from all views.");
                outer.Children.Add(warn);

                // Multi-select grouped by discipline prefix — nothing pre-selected
                var groups = BuildFilterGroups(_allFilterNames);
                var tabs = new LemoineMultiSelectTabs();
                tabs.SetGroups(groups);
                tabs.SelectionChanged += selected =>
                {
                    _selectedNames = new List<string>(selected);
                    OnValidationChanged();
                };
                outer.Children.Add(tabs);

                return outer;
            }

            if (stepId == "S2")
                return null; // framework renders review (ILemoineReviewable)

            return null;
        }

        // ── CardDef struct ───────────────────────────────────────────────────────
        private struct CardDef
        {
            public string       Label;
            public Func<string> Val;
            public int          Row;
            public int          Col;
            public CardDef(string label, Func<string> val, int row, int col)
            { Label = label; Val = val; Row = row; Col = col; }
        }

                // ── ILemoineReviewable (P3) — framework renders the review step ───
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("delete",    "Filters to Delete"),
            ("remaining", "Remaining in Project"),
            ("scope",     "Scope"),
            ("action",    "Action"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["delete"]    = $"{_selectedNames.Count} selected",
            ["remaining"] = $"{_allFilterNames.Count - _selectedNames.Count} will remain",
            ["scope"]     = "Entire project",
            ["action"]    = "Permanent deletion (no undo)",
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => null;
        public string?        ReviewWarning => $"⚠  This will permanently delete {_selectedNames.Count} filter(s) from the project. This cannot be undone.";

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
            if (stepId == "S2") return "Ready to run — action is permanent";
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

            pushLog($"Permanently deleting {_selectedNames.Count} filter(s) from project…", "info");
            _event.Raise();
        }

        // ── Helpers ─────────────────────────────────────────────────────────────
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
