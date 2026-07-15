using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.Ceilings
{
    /// <summary>Web port of <see cref="ReprojectCeilingGridsViewModel"/> — re-run the grid
    /// projection in selected ceiling plan views. Same handler and AppStrings keys; the view
    /// picker is the shared browser tree pruned to eligible ceiling plans.</summary>
    public class ReprojectCeilingGridsWebTool : WebToolBase, IWebToolCleanup
    {
        private readonly CeilingGridEventHandler _handler;
        private readonly Autodesk.Revit.UI.ExternalEvent _event;
        private readonly List<ReprojectCeilingGridsViewModel.CeilingPlanViewEntry> _availableViews;
        private readonly BrowserTree _browserTree;

        private List<ElementId> _selectedViewIds = new List<ElementId>();

        public ReprojectCeilingGridsWebTool(
            CeilingGridEventHandler handler,
            Autodesk.Revit.UI.ExternalEvent externalEvent,
            List<ReprojectCeilingGridsViewModel.CeilingPlanViewEntry>? availableViews,
            BrowserTree? browserTree = null)
        {
            _handler        = handler;
            _event          = externalEvent;
            _availableViews = availableViews ?? new List<ReprojectCeilingGridsViewModel.CeilingPlanViewEntry>();
            _browserTree    = browserTree ?? new BrowserTree();
        }

        public override string Title    => AppStrings.T("ceilings.reprojectGrids.title");
        public override string RunLabel => AppStrings.T("ceilings.reprojectGrids.runLabel");

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            var s1 = new WebStep("S1", AppStrings.T("ceilings.reprojectGrids.steps.S1"));
            if (_availableViews.Count == 0)
            {
                s1.Add(WebInput.Hint("noViews", AppStrings.T("ceilings.reprojectGrids.labels.noViews")));
            }
            else
            {
                var eligible = new HashSet<long>(_availableViews.Select(v => v.Id.Value));
                s1.Add(WebInput.BrowserTree("views", AppStrings.T("ceilings.reprojectGrids.labels.pickerName"),
                    PruneTree(_browserTree, eligible),
                    _selectedViewIds.Select(id => id.Value)));
                s1.Add(WebInput.Warn("warning", AppStrings.T("ceilings.reprojectGrids.labels.warning")));
            }

            var s2 = new WebStep("S2", AppStrings.T("ceilings.reprojectGrids.steps.S2"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("ceilings.reprojectGrids.review.itemViews"),
                     _selectedViewIds.Count == 0 ? AppStrings.T("ceilings.reprojectGrids.review.viewsNone")
                        : AppStrings.T("ceilings.reprojectGrids.labels.planCount", _selectedViewIds.Count)),
                    (AppStrings.T("ceilings.reprojectGrids.review.itemTarget"), AppStrings.T("ceilings.reprojectGrids.review.target")),
                    (AppStrings.T("ceilings.reprojectGrids.review.itemOp"),     AppStrings.T("ceilings.reprojectGrids.review.op")),
                    (AppStrings.T("ceilings.reprojectGrids.review.itemOutput"), AppStrings.T("ceilings.reprojectGrids.review.output")),
                },
                note: AppStrings.T("ceilings.reprojectGrids.review.note")));

            return new List<WebStep> { s1, s2 };
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            if (inputId == "views")
            {
                _selectedViewIds = IdList(value).Select(id => new ElementId(id)).ToList();
                Fire();
            }
        }

        public override bool IsStepValid(string stepId) => stepId != "S1" || _selectedViewIds.Count > 0;
        public override bool CanRun() => _selectedViewIds.Count > 0;

        public override string SummaryFor(string stepId)
        {
            if (stepId == "S1")
                return _selectedViewIds.Count == 0 ? "-"
                    : AppStrings.T("ceilings.reprojectGrids.labels.planCount", _selectedViewIds.Count);
            if (stepId == "S2") return AppStrings.T("ceilings.reprojectGrids.summaries.S2");
            return "-";
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            _handler.Mode            = CeilingGridEventHandler.ToolMode.Reproject;
            _handler.SelectedViewIds = new List<ElementId>(_selectedViewIds);
            _handler.BatchDwgFolder  = "";
            _handler.PushLog         = pushLog;
            _handler.OnProgress      = onProgress;
            _handler.OnComplete      = onComplete;

            pushLog(AppStrings.T("ceilings.reprojectGrids.log.raising"), "info");
            _event.Raise();
        }

        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.PushLog = null; _handler.OnProgress = null; _handler.OnComplete = null;
        }
    }
}
