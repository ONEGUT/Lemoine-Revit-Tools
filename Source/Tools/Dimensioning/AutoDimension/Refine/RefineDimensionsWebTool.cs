using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.Dimensioning.AutoDimension.Refine
{
    /// <summary>Web port of <see cref="RefineDimensionsViewModel"/> — re-dimension views that
    /// already carry clash markers to the nearest grid/slab edge. Same handler and AppStrings
    /// keys.</summary>
    public class RefineDimensionsWebTool : WebToolBase, IWebToolCleanup
    {
        private readonly RefineDimensionsEventHandler? _handler;
        private readonly ExternalEvent?                _event;
        private readonly List<long>  _allViewIds;
        private readonly BrowserTree _browserTree;

        private List<long> _selectedViewIds = new List<long>();
        private string _dimTargetType =
            string.Equals(AutoDimensionConfig.Instance.TargetType, "Grid", StringComparison.OrdinalIgnoreCase)
              ? "Grid" : "SlabEdge";

        public RefineDimensionsWebTool(
            RefineDimensionsEventHandler? handler,
            ExternalEvent?                externalEvent,
            List<View>                    allViews,
            BrowserTree?                  browserTree = null)
        {
            _handler     = handler;
            _event       = externalEvent;
            _allViewIds  = (allViews ?? new List<View>()).Select(v => v.Id.Value).ToList();
            _browserTree = browserTree ?? new BrowserTree();
        }

        public override string Title    => AppStrings.T("clash.refineDimensions.title");
        public override string RunLabel => AppStrings.T("clash.refineDimensions.runLabel");

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            var s1 = new WebStep("S1", AppStrings.T("clash.refineDimensions.steps.S1"))
                .Add(WebInput.Hint("s1Help", AppStrings.T("clash.refineDimensions.labels.s1Help")))
                .Add(WebInput.BrowserTree("views", AppStrings.T("clash.refineDimensions.labels.pickerName"),
                    PruneTree(_browserTree, new HashSet<long>(_allViewIds)),
                    _selectedViewIds));

            var s2 = new WebStep("S2", AppStrings.T("clash.refineDimensions.steps.S2"), required: false)
                .Add(WebInput.SingleSelect("dest", AppStrings.T("clash.refineDimensions.labels.destLabel"),
                    _dimTargetType, new[]
                    {
                        new WebOption("SlabEdge", AppStrings.T("clash.refineDimensions.labels.destSlab")),
                        new WebOption("Grid",     AppStrings.T("clash.refineDimensions.labels.destGrid")),
                    }))
                .Add(WebInput.Hint("s2Help", AppStrings.T("clash.refineDimensions.labels.s2Help")));

            var s3 = new WebStep("S3", AppStrings.T("clash.refineDimensions.steps.S3"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("clash.refineDimensions.review.itemViews"),
                     _selectedViewIds.Count > 0 ? AppStrings.T("clash.refineDimensions.review.viewsValue", _selectedViewIds.Count) : "-"),
                    (AppStrings.T("clash.refineDimensions.review.itemDest"),
                     _dimTargetType == "Grid" ? AppStrings.T("clash.refineDimensions.review.destGrid") : AppStrings.T("clash.refineDimensions.review.destSlab")),
                    (AppStrings.T("clash.refineDimensions.review.chipNoCallouts"), "✓"),
                    (AppStrings.T("clash.refineDimensions.review.chipScaleUnchanged"), "✓"),
                },
                note: AppStrings.T("clash.refineDimensions.review.note")));

            return new List<WebStep> { s1, s2, s3 };
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "views": _selectedViewIds = IdList(value); Fire(); break;
                case "dest":  _dimTargetType = AsString(value) == "Grid" ? "Grid" : "SlabEdge"; Fire(); break;
            }
        }

        public override bool IsStepValid(string stepId) => stepId != "S1" || _selectedViewIds.Count > 0;
        public override bool CanRun() => _selectedViewIds.Count > 0;

        public override string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedViewIds.Count == 0 ? "-" : AppStrings.T("clash.refineDimensions.summaries.viewCount", _selectedViewIds.Count);
                case "S2": return _dimTargetType == "Grid" ? AppStrings.T("clash.refineDimensions.summaries.destGrid") : AppStrings.T("clash.refineDimensions.summaries.destSlab");
                case "S3": return AppStrings.T("clash.refineDimensions.summaries.S3");
                default:   return "-";
            }
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_handler == null || _event == null) return;

            _handler.ViewIds       = _selectedViewIds.Select(id => new ElementId(id)).ToList();
            _handler.DimTargetType = _dimTargetType;
            _handler.PushLog       = pushLog;
            _handler.OnProgress    = onProgress;
            _handler.OnComplete    = onComplete;

            _event.Raise();
        }

        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.PushLog       = null;
            _handler.OnProgress    = null;
            _handler.OnComplete    = null;
            _handler.OnResultChips = null;
        }
    }
}
