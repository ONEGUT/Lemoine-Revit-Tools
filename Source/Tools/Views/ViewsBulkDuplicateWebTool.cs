using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Naming;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.LinkViews
{
    /// <summary>Web port of <see cref="ViewsBulkDuplicateViewModel"/> — bulk duplicate views
    /// (Duplicate / With Detailing / As Dependent) named from a token pattern. Same handler,
    /// pattern store, mode tokens (logic identifiers), and AppStrings keys.</summary>
    public class ViewsBulkDuplicateWebTool : WebToolBase, IWebToolCleanup
    {
        private const string ToolId         = "views.duplicate";
        private const string DefaultPattern = "{ViewName} - Copy";

        private static readonly string[] DuplicateModes =
        {
            ViewsBulkDuplicateViewModel.ModeDuplicate,
            ViewsBulkDuplicateViewModel.ModeWithDetailing,
            ViewsBulkDuplicateViewModel.ModeAsDependent,
        };

        private readonly List<ViewsBulkDuplicateViewModel.ViewEntry> _views;
        private readonly BrowserTree _browserTree;
        private readonly ViewsBulkDuplicateRunHandler? _runHandler;
        private readonly ExternalEvent?                _runEvent;

        private List<ElementId> _selectedViewIds = new List<ElementId>();
        private string          _mode            = ViewsBulkDuplicateViewModel.ModeWithDetailing;
        private string          _namePattern     = NamingPatternStore.Instance.GetOrDefault(ToolId, DefaultPattern);

        public ViewsBulkDuplicateWebTool(
            ViewsBulkDuplicateRunHandler? runHandler, ExternalEvent? runEvent,
            List<ViewsBulkDuplicateViewModel.ViewEntry>? views, BrowserTree? browserTree = null)
        {
            _runHandler  = runHandler;
            _runEvent    = runEvent;
            _views       = views ?? new List<ViewsBulkDuplicateViewModel.ViewEntry>();
            _browserTree = browserTree ?? new BrowserTree();
        }

        public override string Title    => AppStrings.T("linkviews.duplicate.title");
        public override string RunLabel => AppStrings.T("linkviews.duplicate.runLabel");

        private string ModeHint() =>
            _mode == ViewsBulkDuplicateViewModel.ModeDuplicate     ? AppStrings.T("linkviews.duplicate.labels.hintDuplicate")
          : _mode == ViewsBulkDuplicateViewModel.ModeWithDetailing ? AppStrings.T("linkviews.duplicate.labels.hintDetailing")
          :                                                          AppStrings.T("linkviews.duplicate.labels.hintDependent");

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            var s1 = new WebStep("S1", AppStrings.T("linkviews.duplicate.steps.S1"))
                .Add(WebInput.BrowserTree("views", AppStrings.T("linkviews.duplicate.labels.sourceViews"),
                    _browserTree, _selectedViewIds.Select(id => id.Value)));

            var s2 = new WebStep("S2", AppStrings.T("linkviews.duplicate.steps.S2"))
                .Add(WebInput.SingleSelect("mode", AppStrings.T("linkviews.duplicate.labels.modeHeader"),
                    _mode, DuplicateModes.Select(m => new WebOption(m, m))))
                .Add(WebInput.Hint("modeHint", ModeHint()));

            var tokens = NamingTokenRegistry.TokensFor(TokenEntity.View, hasSource: false);
            var ctx = new TokenContext();
            var v = _views.FirstOrDefault(x => _selectedViewIds.Any(id => id.Value == x.Id.Value));
            ctx.Computed["ViewName"] = v?.Name      ?? AppStrings.T("linkviews.duplicate.labels.exView");
            ctx.Computed["ViewType"] = v?.TypeLabel ?? AppStrings.T("linkviews.duplicate.labels.exType");
            var s3 = new WebStep("S3", AppStrings.T("linkviews.duplicate.steps.S3"))
                .Add(WebInput.TokenInput("pattern", AppStrings.T("linkviews.duplicate.labels.namePattern"),
                    _namePattern, DefaultPattern, tokens, SampleFor(tokens, ctx)));

            var s4 = new WebStep("S4", AppStrings.T("linkviews.duplicate.steps.S4"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("linkviews.duplicate.review.itemViews"),
                     _selectedViewIds.Count > 0 ? AppStrings.T("linkviews.duplicate.review.viewsValue", _selectedViewIds.Count) : "-"),
                    (AppStrings.T("linkviews.duplicate.review.itemMode"), _mode),
                    (AppStrings.T("linkviews.duplicate.review.itemPattern"),
                     string.IsNullOrWhiteSpace(_namePattern) ? "-" : _namePattern),
                    (AppStrings.T("linkviews.duplicate.review.itemTotal"),
                     _selectedViewIds.Count > 0 ? AppStrings.T("linkviews.duplicate.review.totalValue", _selectedViewIds.Count) : "-"),
                },
                note: AppStrings.T("linkviews.duplicate.review.note"),
                warning: (_namePattern != null && _namePattern.Trim() == "{ViewName}")
                    ? AppStrings.T("linkviews.duplicate.review.warnSameName") : null));

            return new List<WebStep> { s1, s2, s3, s4 };
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "views":
                    _selectedViewIds = IdList(value).Select(v => new ElementId(v)).ToList();
                    Fire();
                    break;
                case "mode":
                    var m = AsString(value);
                    if (!string.IsNullOrEmpty(m)) { _mode = m; Fire(); }
                    break;
                case "pattern":
                    _namePattern = AsString(value);
                    NamingPatternStore.Instance.Set(ToolId, _namePattern);
                    Fire();
                    break;
            }
        }

        public override bool IsStepValid(string stepId)
        {
            if (stepId == "S1") return _selectedViewIds.Count > 0;
            if (stepId == "S2") return !string.IsNullOrEmpty(_mode);
            if (stepId == "S3") return !string.IsNullOrWhiteSpace(_namePattern);
            return true;
        }

        public override bool CanRun() =>
            _selectedViewIds.Count > 0 && !string.IsNullOrEmpty(_mode) && !string.IsNullOrWhiteSpace(_namePattern);

        public override string SummaryFor(string stepId)
        {
            if (stepId == "S1") return _selectedViewIds.Count > 0 ? AppStrings.T("linkviews.duplicate.summaries.viewCount", _selectedViewIds.Count) : "-";
            if (stepId == "S2") return _mode;
            if (stepId == "S3") return string.IsNullOrWhiteSpace(_namePattern) ? "-" : _namePattern;
            if (stepId == "S4") return AppStrings.T("linkviews.duplicate.summaries.S4");
            return "-";
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null) { pushLog("Run handler not registered.", "fail"); onComplete(0, 1, 0); return; }
            _runHandler.SelectedViewIds = new List<ElementId>(_selectedViewIds);
            _runHandler.Mode            = _mode;
            _runHandler.NamePattern     = _namePattern;
            _runHandler.PushLog         = pushLog;
            _runHandler.OnProgress      = onProgress;
            _runHandler.OnComplete      = onComplete;
            pushLog(AppStrings.T("linkviews.duplicate.log.raising"), "info");
            _runEvent.Raise();
        }

        public void OnWindowClosed()
        {
            if (_runHandler == null) return;
            _runHandler.PushLog = null; _runHandler.OnProgress = null; _runHandler.OnComplete = null;
        }
    }
}
