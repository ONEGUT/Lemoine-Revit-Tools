using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.AutoFilters
{
    /// <summary>
    /// ViewModel for the Filter Legend Creator tool.
    /// Reads all filter overrides from the active view and generates a new
    /// Legend view with colored swatches. No user inputs required beyond
    /// confirming the run.
    /// </summary>
    public class AutoFiltersLegendViewModel : ILemoineTool, ILemoineReviewable, ILemoineRunResult, ILemoineToolCleanup
    {
        // Self-describing result label for the run strip (see ILemoineRunResult).
        public string? ResultNoun => "swatches";
        public System.Collections.Generic.IReadOnlyList<LemoineTools.Lemoine.ResultChip>? ResultChips => null;

        // ── ILemoineTool identity ──────────────────────────────────────────────
        public string Title    => LemoineStrings.T("autofilters.legend.title");
        public string RunLabel => LemoineStrings.T("autofilters.legend.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", LemoineStrings.T("autofilters.legend.steps.S1"), required: false),
        };

        // ── Validation change notification ─────────────────────────────────────
        // Required by ILemoineTool; never raised because this tool has no inputs.
#pragma warning disable CS0067
        public event EventHandler? ValidationChanged;

        // Null the callbacks parked on the static handler so this VM isn't retained after close.
        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.PushLog    = null;
            _handler.OnProgress = null;
            _handler.OnComplete = null;
        }

#pragma warning restore CS0067

        // ── ExternalEvent wiring ────────────────────────────────────────────────
        private readonly AutoFiltersLegendEventHandler      _handler;
        private readonly Autodesk.Revit.UI.ExternalEvent    _event;

        public AutoFiltersLegendViewModel(
            AutoFiltersLegendEventHandler handler,
            Autodesk.Revit.UI.ExternalEvent externalEvent)
        {
            _handler = handler;
            _event   = externalEvent;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // GetStepContent
        // ═══════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "S1")
                return null; // framework renders review (ILemoineReviewable)
            return null;
        }


                // ── ILemoineReviewable (P3) — framework renders the review step ───
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("source",   LemoineStrings.T("autofilters.legend.review.itemSource")),
            ("output",   LemoineStrings.T("autofilters.legend.review.itemOutput")),
            ("grouping", LemoineStrings.T("autofilters.legend.review.itemGrouping")),
            ("swatches", LemoineStrings.T("autofilters.legend.review.itemSwatches")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["source"]   = LemoineStrings.T("autofilters.legend.review.source"),
            ["output"]   = LemoineStrings.T("autofilters.legend.review.output"),
            ["grouping"] = LemoineStrings.T("autofilters.legend.review.grouping"),
            ["swatches"] = LemoineStrings.T("autofilters.legend.review.swatches"),
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => LemoineStrings.T("autofilters.legend.review.note");
        public string?        ReviewWarning => null;

        // ═══════════════════════════════════════════════════════════════════════
        // IsValid / SummaryFor / Run
        // ═══════════════════════════════════════════════════════════════════════
        public bool IsValid(string stepId) => true;

        public string SummaryFor(string stepId) =>
            stepId == "S1" ? LemoineStrings.T("autofilters.legend.summaries.S1") : "—";

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _handler.PushLog    = pushLog;
            _handler.OnProgress = onProgress;
            _handler.OnComplete = onComplete;

            pushLog(LemoineStrings.T("autofilters.legend.log.raising"), "info");
            _event.Raise();
        }
    }
}
