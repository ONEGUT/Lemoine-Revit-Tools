using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.AutoFilters
{
    /// <summary>
    /// WebView2 (IWebTool) version of Delete Filters from Project (Phase 3, wave 1). Same domain
    /// behaviour and the SAME ExternalEvent handler as <see cref="DeleteFiltersFromProjectViewModel"/>;
    /// only the view layer differs. First tool to exercise the MultiSelectTabs component end-to-end.
    /// Parallel to the WPF version until verified on Windows (rule R25).
    /// </summary>
    public class DeleteFiltersWebTool : IWebTool, IWebToolCleanup
    {
        private readonly DeleteFiltersFromProjectEventHandler _handler;
        private readonly ExternalEvent                        _event;
        private readonly IReadOnlyList<string>                _allFilterNames;
        private List<string> _selected = new List<string>();

        public event EventHandler? ValidationChanged;
        private void Changed() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public DeleteFiltersWebTool(
            DeleteFiltersFromProjectEventHandler handler, ExternalEvent externalEvent,
            IReadOnlyList<string> allFilterNames)
        {
            _handler        = handler;
            _event          = externalEvent;
            _allFilterNames = allFilterNames ?? new List<string>();
        }

        public string Title    => AppStrings.T("autofilters.deleteFromProject.title");
        public string RunLabel => AppStrings.T("autofilters.deleteFromProject.runLabel");

        public IReadOnlyList<WebStep> BuildSteps()
        {
            var s1 = new WebStep("S1", AppStrings.T("autofilters.deleteFromProject.steps.S1"))
                .Add(WebInput.Warn("warn", AppStrings.T("autofilters.deleteFromProject.labels.warnBanner")));

            if (_allFilterNames.Count == 0)
                s1.Add(WebInput.Warn("empty", AppStrings.T("autofilters.deleteFromProject.labels.noFilters")));
            else
                s1.Add(WebInput.MultiSelectTabs("filters", "",
                    AutoFiltersSettings.GroupFilterNamesByTrade(_allFilterNames), _selected));

            var s2 = new WebStep("S2", AppStrings.T("autofilters.deleteFromProject.steps.S2"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("autofilters.deleteFromProject.review.itemDelete"),
                     AppStrings.T("autofilters.deleteFromProject.review.deleteValue", _selected.Count)),
                    (AppStrings.T("autofilters.deleteFromProject.review.itemRemaining"),
                     AppStrings.T("autofilters.deleteFromProject.review.remainingValue", _allFilterNames.Count - _selected.Count)),
                    (AppStrings.T("autofilters.deleteFromProject.review.itemScope"),
                     AppStrings.T("autofilters.deleteFromProject.review.scope")),
                    (AppStrings.T("autofilters.deleteFromProject.review.itemAction"),
                     AppStrings.T("autofilters.deleteFromProject.review.action")),
                },
                warning: AppStrings.T("autofilters.deleteFromProject.review.warning", _selected.Count)));

            return new List<WebStep> { s1, s2 };
        }

        public void OnState(string stepId, string inputId, object? value)
        {
            if (inputId == "filters" && value is IEnumerable seq && !(value is string))
            {
                _selected = seq.Cast<object?>().Select(o => o?.ToString())
                    .Where(s => s != null).Select(s => s!).ToList();
                Changed();
            }
        }

        public bool IsStepValid(string stepId) => stepId != "S1" || _selected.Count > 0;
        public bool CanRun() => _selected.Count > 0;

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1") return AppStrings.T("autofilters.deleteFromProject.summaries.S1", _selected.Count);
            if (stepId == "S2") return AppStrings.T("autofilters.deleteFromProject.summaries.S2");
            return "-";
        }

        public bool IsStepVisible(string stepId) => true;

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _handler.SelectedFilterNames = new List<string>(_selected);
            _handler.PushLog             = pushLog;
            _handler.OnProgress          = onProgress;
            _handler.OnComplete          = onComplete;

            pushLog(AppStrings.T("autofilters.deleteFromProject.log.deleting", _selected.Count), "info");
            _event.Raise();
        }

        public void OnWindowClosed()
        {
            _handler.PushLog = null; _handler.OnProgress = null; _handler.OnComplete = null;
        }
    }
}
