using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;

namespace LemoineTools.Tools.AutoFilters
{
    /// <summary>
    /// ViewModel for permanently deleting ParameterFilterElements from the project.
    /// Step 1 — select which filters to delete (grouped by discipline prefix).
    /// Step 2 — Review &amp; Run (with explicit warning card).
    /// </summary>
    public class DeleteFiltersFromProjectViewModel : IStepFlowTool, IReviewableTool, IRunResult, IToolCleanup
    {
        // Self-describing result label for the run strip (see IRunResult).
        public string? ResultNoun => "filters";
        public System.Collections.Generic.IReadOnlyList<LemoineTools.Framework.ResultChip>? ResultChips => null;

        // ── IStepFlowTool identity ──────────────────────────────────────────────
        public string Title    => AppStrings.T("autofilters.deleteFromProject.title");
        public string RunLabel => AppStrings.T("autofilters.deleteFromProject.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", AppStrings.T("autofilters.deleteFromProject.steps.S1"), required: true),
            new StepDefinition("S2", AppStrings.T("autofilters.deleteFromProject.steps.S2"),   required: false),
        };

        // ── State ──────────────────────────────────────────────────────────────
        private readonly IReadOnlyList<string> _allFilterNames;
        private List<string> _selectedNames = new List<string>();

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
                        Text         = AppStrings.T("autofilters.deleteFromProject.labels.noFilters"),
                        TextWrapping = TextWrapping.Wrap,
                        FontStyle    = FontStyles.Italic,
                    };
                    none.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    none.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                    none.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    return none;
                }

                var outer = new StackPanel();

                var warn = new WarnBanner(
                    AppStrings.T("autofilters.deleteFromProject.labels.warnBanner"));
                outer.Children.Add(warn);

                // Multi-select grouped by owning trade — nothing pre-selected.
                // Filter names are "{TRADEID}_{RULE_NAME}", so group via the trade
                // map rather than splitting on " - " (which never matches).
                var groups = AutoFiltersSettings.GroupFilterNamesByTrade(_allFilterNames);
                var tabs = new MultiSelectTabs();
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
                return null; // framework renders review (IReviewableTool)

            return null;
        }

                // ── IReviewableTool (P3) — framework renders the review step ───
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("delete",    AppStrings.T("autofilters.deleteFromProject.review.itemDelete")),
            ("remaining", AppStrings.T("autofilters.deleteFromProject.review.itemRemaining")),
            ("scope",     AppStrings.T("autofilters.deleteFromProject.review.itemScope")),
            ("action",    AppStrings.T("autofilters.deleteFromProject.review.itemAction")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["delete"]    = AppStrings.T("autofilters.deleteFromProject.review.deleteValue", _selectedNames.Count),
            ["remaining"] = AppStrings.T("autofilters.deleteFromProject.review.remainingValue", _allFilterNames.Count - _selectedNames.Count),
            ["scope"]     = AppStrings.T("autofilters.deleteFromProject.review.scope"),
            ["action"]    = AppStrings.T("autofilters.deleteFromProject.review.action"),
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => null;
        public string?        ReviewWarning => AppStrings.T("autofilters.deleteFromProject.review.warning", _selectedNames.Count);

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
            if (stepId == "S1") return AppStrings.T("autofilters.deleteFromProject.summaries.S1", _selectedNames.Count);
            if (stepId == "S2") return AppStrings.T("autofilters.deleteFromProject.summaries.S2");
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

            pushLog(AppStrings.T("autofilters.deleteFromProject.log.deleting", _selectedNames.Count), "info");
            _event.Raise();
        }
    }
}
