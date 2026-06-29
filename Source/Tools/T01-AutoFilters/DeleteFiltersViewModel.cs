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
    public class DeleteFiltersViewModel : ILemoineTool, ILemoineReviewable, ILemoineRunResult, ILemoineToolCleanup
    {
        // Self-describing result label for the run strip (see ILemoineRunResult).
        public string? ResultNoun => "filters";
        public System.Collections.Generic.IReadOnlyList<LemoineTools.Lemoine.ResultChip>? ResultChips => null;

        // ── ILemoineTool identity ──────────────────────────────────────────────
        public string Title    => LemoineStrings.T("autofilters.deleteFilters.title");
        public string RunLabel => LemoineStrings.T("autofilters.deleteFilters.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", LemoineStrings.T("autofilters.deleteFilters.steps.S1"), required: true),
            new StepDefinition("S2", LemoineStrings.T("autofilters.deleteFilters.steps.S2"),   required: false),
        };

        // ── State ──────────────────────────────────────────────────────────────
        private readonly string                 _viewName;
        private readonly IReadOnlyList<string>  _allFilterNames;
        private List<string>                    _selectedNames = new List<string>();

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
            _viewName       = viewName    ?? LemoineStrings.T("autofilters.deleteFilters.labels.activeViewFallback");
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
                        Text         = LemoineStrings.T("autofilters.deleteFilters.labels.noFilters"),
                        TextWrapping = TextWrapping.Wrap,
                        FontStyle    = FontStyles.Italic,
                    };
                    none.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    none.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                    none.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    return none;
                }

                // Group by the trade that owns the matching rule; unmatched → "Others".
                var groups = AutoFiltersSettings.GroupFilterNamesByTrade(_allFilterNames);

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
            ("view",      LemoineStrings.T("autofilters.deleteFilters.review.itemView")),
            ("remove",    LemoineStrings.T("autofilters.deleteFilters.review.itemRemove")),
            ("remaining", LemoineStrings.T("autofilters.deleteFilters.review.itemRemaining")),
            ("action",    LemoineStrings.T("autofilters.deleteFilters.review.itemAction")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["view"]      = _viewName,
            ["remove"]    = LemoineStrings.T("autofilters.deleteFilters.review.removeValue", _selectedNames.Count),
            ["remaining"] = LemoineStrings.T("autofilters.deleteFilters.review.remainingValue", _allFilterNames.Count - _selectedNames.Count),
            ["action"]    = LemoineStrings.T("autofilters.deleteFilters.review.action"),
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => LemoineStrings.T("autofilters.deleteFilters.review.note");
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
            if (stepId == "S1") return LemoineStrings.T("autofilters.deleteFilters.summaries.S1", _selectedNames.Count);
            if (stepId == "S2") return LemoineStrings.T("autofilters.deleteFilters.summaries.S2");
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

            pushLog(LemoineStrings.T("autofilters.deleteFilters.log.removing", _selectedNames.Count), "info");
            _event.Raise();
        }
    }
}
