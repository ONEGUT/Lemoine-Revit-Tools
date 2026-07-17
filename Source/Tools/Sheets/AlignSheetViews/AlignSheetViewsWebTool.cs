using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.Sheets.AlignSheetViews
{
    /// <summary>Web port of <see cref="AlignSheetViewsViewModel"/> — align target sheets'
    /// viewports to their counterparts on reference sheets. Same handler and AppStrings keys.</summary>
    public sealed class AlignSheetViewsWebTool : WebToolBase, IWebToolCleanup
    {
        private readonly AlignSheetViewsEventHandler? _handler;
        private readonly ExternalEvent?               _event;
        private readonly BrowserTree                  _browserTree;
        private readonly List<long>                   _sheetIds;
        private readonly Dictionary<long, string>     _sheetLabels;

        private List<ElementId> _sourceSheetIds = new List<ElementId>();
        private List<ElementId> _targetSheetIds = new List<ElementId>();
        private int             _overlapPercent = 50;
        private bool            _alignTitles    = true;
        private bool            _previewOnly    = false;

        private bool _inheritGrids          = false;
        private bool _inheritScopeBox       = false;
        private bool _inheritCropVisibility = false;
        private bool _inheritCropSize       = false;

        public AlignSheetViewsWebTool(
            AlignSheetViewsEventHandler? handler,
            ExternalEvent?               externalEvent,
            IEnumerable<(ElementId Id, string Label)>? sheets,
            BrowserTree?                 browserTree = null)
        {
            _handler     = handler;
            _event       = externalEvent;
            _browserTree = browserTree ?? new BrowserTree();

            _sheetIds    = new List<long>();
            _sheetLabels = new Dictionary<long, string>();
            foreach (var s in sheets ?? Enumerable.Empty<(ElementId, string)>())
            {
                long key = s.Id.Value;
                if (_sheetLabels.ContainsKey(key)) continue;
                _sheetIds.Add(key);
                _sheetLabels[key] = s.Label;
            }
        }

        public override string Title    => AppStrings.T("testing.alignSheetViews.title");
        public override string RunLabel => AppStrings.T("testing.alignSheetViews.runLabel");

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            var eligible = new HashSet<long>(_sheetIds);

            var s1 = new WebStep("S1", AppStrings.T("testing.alignSheetViews.steps.S1"))
                .Add(WebInput.Hint("noteReference", AppStrings.T("testing.alignSheetViews.labels.noteReference")));
            if (_sheetIds.Count == 0)
                s1.Add(WebInput.Hint("noSheets", AppStrings.T("testing.alignSheetViews.labels.noSheets")));
            else
                s1.Add(WebInput.BrowserTree("source", AppStrings.T("testing.alignSheetViews.labels.secReference"),
                    PruneTree(_browserTree, eligible),
                    _sourceSheetIds.Select(id => id.Value)));

            var s2 = new WebStep("S2", AppStrings.T("testing.alignSheetViews.steps.S2"))
                .Add(WebInput.Hint("noteTargets", AppStrings.T("testing.alignSheetViews.labels.noteTargets")));
            if (_sheetIds.Count == 0)
                s2.Add(WebInput.Hint("noSheets2", AppStrings.T("testing.alignSheetViews.labels.noSheets")));
            else
                s2.Add(WebInput.BrowserTree("targets", AppStrings.T("testing.alignSheetViews.labels.secTargets"),
                    PruneTree(_browserTree, eligible),
                    _targetSheetIds.Select(id => id.Value)));

            var s3 = new WebStep("S3", AppStrings.T("testing.alignSheetViews.steps.S3"), required: false)
                .Add(WebInput.Stepper("overlap", AppStrings.T("testing.alignSheetViews.labels.secOverlap"),
                    _overlapPercent, 5, 100, 5, 0))
                .Add(WebInput.Hint("noteOverlap", AppStrings.T("testing.alignSheetViews.labels.noteOverlap")))
                .Add(WebInput.Toggle("titles", AppStrings.T("testing.alignSheetViews.labels.optAlignTitles"), _alignTitles))
                .Add(WebInput.Hint("noteTitles", AppStrings.T("testing.alignSheetViews.labels.noteAlignTitles")))
                .Add(WebInput.Hint("secInherit", AppStrings.T("testing.alignSheetViews.labels.secInherit")))
                .Add(WebInput.Toggle("scopeBox", AppStrings.T("testing.alignSheetViews.labels.optScopeBox"), _inheritScopeBox))
                .Add(WebInput.Hint("noteScopeBox", AppStrings.T("testing.alignSheetViews.labels.noteScopeBox")))
                .Add(WebInput.Toggle("grids", AppStrings.T("testing.alignSheetViews.labels.optGrids"), _inheritGrids))
                .Add(WebInput.Hint("noteGrids", AppStrings.T("testing.alignSheetViews.labels.noteGrids")))
                .Add(WebInput.Toggle("cropVis", AppStrings.T("testing.alignSheetViews.labels.optCropVis"), _inheritCropVisibility))
                .Add(WebInput.Hint("noteCropVis", AppStrings.T("testing.alignSheetViews.labels.noteCropVis")))
                .Add(WebInput.Toggle("cropSize", AppStrings.T("testing.alignSheetViews.labels.optCropSize"), _inheritCropSize))
                .Add(WebInput.Hint("noteCropSize", AppStrings.T("testing.alignSheetViews.labels.noteCropSize")))
                .Add(WebInput.Hint("secMode", AppStrings.T("testing.alignSheetViews.labels.secMode")))
                .Add(WebInput.Toggle("preview", AppStrings.T("testing.alignSheetViews.labels.optPreview"), _previewOnly))
                .Add(WebInput.Hint("notePreview", AppStrings.T("testing.alignSheetViews.labels.notePreview")));

            string inherit = InheritSummary;
            var s4 = new WebStep("S4", AppStrings.T("testing.alignSheetViews.steps.S4"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("testing.alignSheetViews.review.itemSource"),
                     EffectiveSourceCount == 0
                        ? AppStrings.T("testing.alignSheetViews.review.none")
                        : (EffectiveSourceCount == 1
                            ? (_sheetLabels.TryGetValue(_sourceSheetIds[0].Value, out var lbl) ? lbl : _sourceSheetIds[0].Value.ToString())
                            : AppStrings.T("testing.alignSheetViews.review.sheetCount", EffectiveSourceCount))),
                    (AppStrings.T("testing.alignSheetViews.review.itemTargets"),
                     EffectiveTargetCount == 0
                        ? AppStrings.T("testing.alignSheetViews.review.none")
                        : AppStrings.T("testing.alignSheetViews.review.sheetCount", EffectiveTargetCount)),
                    (AppStrings.T("testing.alignSheetViews.review.itemOverlap"),
                     AppStrings.T("testing.alignSheetViews.review.overlapValue", _overlapPercent)),
                    (AppStrings.T("testing.alignSheetViews.review.itemTitles"),
                     _alignTitles ? AppStrings.T("testing.alignSheetViews.review.titlesAligned") : AppStrings.T("testing.alignSheetViews.review.titlesUnchanged")),
                    (AppStrings.T("testing.alignSheetViews.review.itemInherit"), inherit),
                    (AppStrings.T("testing.alignSheetViews.review.itemMode"),
                     _previewOnly ? AppStrings.T("testing.alignSheetViews.review.modePreview") : AppStrings.T("testing.alignSheetViews.review.modeAlign")),
                },
                note: AppStrings.T("testing.alignSheetViews.review.note"),
                warning: _previewOnly
                    ? null
                    : AppStrings.T("testing.alignSheetViews.review.warning",
                        (_alignTitles ? AppStrings.T("testing.alignSheetViews.review.warnTitles") : ""),
                        (inherit == AppStrings.T("testing.alignSheetViews.inherit.nothing") ? ""
                            : AppStrings.T("testing.alignSheetViews.review.warnInherit", inherit.ToLowerInvariant())))));

            return new List<WebStep> { s1, s2, s3, s4 };
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "source":   _sourceSheetIds = IdList(value).Select(id => new ElementId(id)).ToList(); Fire(); break;
                case "targets":  _targetSheetIds = IdList(value).Select(id => new ElementId(id)).ToList(); Fire(); break;
                case "overlap":  { var v = (int)AsDouble(value, _overlapPercent); if (v > 0) _overlapPercent = v; Fire(); break; }
                case "titles":   _alignTitles           = AsBool(value, _alignTitles);           Fire(); break;
                case "scopeBox": _inheritScopeBox       = AsBool(value, _inheritScopeBox);       Fire(); break;
                case "grids":    _inheritGrids          = AsBool(value, _inheritGrids);          Fire(); break;
                case "cropVis":  _inheritCropVisibility = AsBool(value, _inheritCropVisibility); Fire(); break;
                case "cropSize": _inheritCropSize       = AsBool(value, _inheritCropSize);       Fire(); break;
                case "preview":  _previewOnly           = AsBool(value, _previewOnly);           Fire(); break;
            }
        }

        private string InheritSummary
        {
            get
            {
                var parts = new List<string>();
                if (_inheritScopeBox)       parts.Add(AppStrings.T("testing.alignSheetViews.inherit.scopeBox"));
                if (_inheritGrids)          parts.Add(AppStrings.T("testing.alignSheetViews.inherit.gridExtents"));
                if (_inheritCropVisibility) parts.Add(AppStrings.T("testing.alignSheetViews.inherit.cropVisibility"));
                if (_inheritCropSize)       parts.Add(AppStrings.T("testing.alignSheetViews.inherit.cropSize"));
                return parts.Count == 0 ? AppStrings.T("testing.alignSheetViews.inherit.nothing") : string.Join(", ", parts);
            }
        }

        private List<long> SourceKeys => _sourceSheetIds.Select(id => id.Value).ToList();
        private int EffectiveSourceCount => _sourceSheetIds.Count;
        private int EffectiveTargetCount
        {
            get
            {
                var srcKeys = new HashSet<long>(SourceKeys);
                return _targetSheetIds.Count(id => !srcKeys.Contains(id.Value));
            }
        }

        public override bool IsStepValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return EffectiveSourceCount > 0;
                case "S2": return EffectiveTargetCount > 0;
                default:   return true;
            }
        }

        public override bool CanRun() => EffectiveSourceCount > 0 && EffectiveTargetCount > 0;

        public override string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return EffectiveSourceCount == 0
                    ? "-"
                    : (EffectiveSourceCount == 1
                        ? (_sheetLabels.TryGetValue(_sourceSheetIds[0].Value, out var lbl) ? lbl : AppStrings.T("testing.alignSheetViews.summaries.s1Single"))
                        : AppStrings.T("testing.alignSheetViews.review.sheetCount", EffectiveSourceCount));
                case "S2": return EffectiveTargetCount == 0 ? "-" : AppStrings.T("testing.alignSheetViews.review.sheetCount", EffectiveTargetCount);
                case "S3": return AppStrings.T("testing.alignSheetViews.summaries.s3Overlap", _overlapPercent) +
                                  (_alignTitles ? AppStrings.T("testing.alignSheetViews.summaries.s3Titles") : "") +
                                  (InheritSummary == AppStrings.T("testing.alignSheetViews.inherit.nothing") ? "" : AppStrings.T("testing.alignSheetViews.summaries.s3Inherit")) +
                                  (_previewOnly ? AppStrings.T("testing.alignSheetViews.summaries.s3Preview") : "");
                case "S4": return AppStrings.T("testing.alignSheetViews.summaries.S4");
                default:   return "-";
            }
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_handler == null || _event == null) return;

            var srcKeys = new HashSet<long>(SourceKeys);

            _handler.SourceSheetIds        = _sourceSheetIds.ToList();
            _handler.TargetSheetIds        = _targetSheetIds.Where(id => !srcKeys.Contains(id.Value)).ToList();
            _handler.OverlapThreshold      = _overlapPercent / 100.0;
            _handler.AlignTitles           = _alignTitles;
            _handler.PreviewOnly           = _previewOnly;
            _handler.InheritScopeBox       = _inheritScopeBox;
            _handler.InheritGridExtents    = _inheritGrids;
            _handler.InheritCropVisibility = _inheritCropVisibility;
            _handler.InheritCropSize       = _inheritCropSize;
            _handler.PushLog               = pushLog;
            _handler.OnProgress            = onProgress;
            _handler.OnComplete            = onComplete;

            _event.Raise();
        }

        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.PushLog    = null;
            _handler.OnProgress = null;
            _handler.OnComplete = null;
        }
    }
}
