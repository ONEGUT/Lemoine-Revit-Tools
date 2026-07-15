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
    /// <summary>Web (IWebTool) port of <see cref="ViewsByLinkViewModel"/> — one isolated 3D view
    /// per selected link. Same run handler, data entries, pattern store, and AppStrings keys.</summary>
    public class ViewsByLinkWebTool : WebToolBase, IWebToolCleanup
    {
        private const string ToolId         = "views.byLink";
        private const string DefaultPattern = "{LinkName}";

        private readonly List<ViewsByLinkViewModel.LinkEntry>     _links;
        private readonly List<ViewsByLinkViewModel.TemplateEntry> _templates;
        private readonly ViewsByLinkRunHandler? _runHandler;
        private readonly ExternalEvent?         _runEvent;

        private List<ElementId> _selectedLinkIds = new List<ElementId>();
        private string          _namePattern     = NamingPatternStore.Instance.GetOrDefault(ToolId, DefaultPattern);
        private ElementId       _templateId      = ElementId.InvalidElementId;

        public ViewsByLinkWebTool(
            ViewsByLinkRunHandler? runHandler, ExternalEvent? runEvent,
            List<ViewsByLinkViewModel.LinkEntry>? links, List<ViewsByLinkViewModel.TemplateEntry>? templates)
        {
            _runHandler = runHandler;
            _runEvent   = runEvent;
            _links      = links     ?? new List<ViewsByLinkViewModel.LinkEntry>();
            _templates  = templates ?? new List<ViewsByLinkViewModel.TemplateEntry>();
        }

        public override string Title    => AppStrings.T("linkviews.bulkViews.byLink.title");
        public override string RunLabel => AppStrings.T("linkviews.bulkViews.byLink.runLabel");

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            var s1 = new WebStep("S1", AppStrings.T("linkviews.bulkViews.byLink.steps.S1"));
            if (_links.Count == 0)
                s1.Add(WebInput.Hint("noLinks", AppStrings.T("linkviews.bulkViews.byLink.labels.noLinks")));
            else
                s1.Add(WebInput.MultiSelectTabs("links", "",
                    new Dictionary<string, List<string>> { ["Links"] = _links.Select(l => l.Name).ToList() },
                    _links.Where(l => _selectedLinkIds.Any(id => id.Value == l.Id.Value)).Select(l => l.Name)));

            var tokens = NamingTokenRegistry.TokensFor(TokenEntity.View, hasSource: true);
            var ctx = new TokenContext();
            ctx.Computed["LinkName"] =
                _links.FirstOrDefault(l => _selectedLinkIds.Any(id => id.Value == l.Id.Value))?.Name
                ?? AppStrings.T("linkviews.bulkViews.byLink.labels.exLink");

            string noneLabel = AppStrings.T("linkviews.bulkViews.byLink.labels.templateNone");
            var s2 = new WebStep("S2", AppStrings.T("linkviews.bulkViews.byLink.steps.S2"))
                .Add(WebInput.TokenInput("pattern", AppStrings.T("linkviews.bulkViews.byLink.labels.namePattern"),
                    _namePattern, DefaultPattern, tokens, SampleFor(tokens, ctx)))
                .Add(WebInput.SingleSelect("template", AppStrings.T("linkviews.bulkViews.byLink.labels.template"),
                    _templates.FirstOrDefault(t => t.Id.Value == _templateId.Value)?.Name ?? noneLabel,
                    new[] { new WebOption(noneLabel, noneLabel) }
                        .Concat(_templates.Select(t => new WebOption(t.Name, t.Name)))));

            var s3 = new WebStep("S3", AppStrings.T("linkviews.bulkViews.byLink.steps.S3"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("linkviews.bulkViews.byLink.review.itemLinks"),
                     _selectedLinkIds.Count > 0 ? AppStrings.T("linkviews.bulkViews.byLink.review.linksValue", _selectedLinkIds.Count) : "-"),
                    (AppStrings.T("linkviews.bulkViews.byLink.review.itemPattern"),
                     string.IsNullOrWhiteSpace(_namePattern) ? "-" : _namePattern),
                },
                note: AppStrings.T("linkviews.bulkViews.byLink.review.note")));

            return new List<WebStep> { s1, s2, s3 };
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "links":
                    var names = new HashSet<string>(StrList(value), StringComparer.Ordinal);
                    _selectedLinkIds = _links.Where(l => names.Contains(l.Name)).Select(l => l.Id).ToList();
                    Fire();
                    break;
                case "pattern":
                    _namePattern = AsString(value);
                    NamingPatternStore.Instance.Set(ToolId, _namePattern);
                    Fire();
                    break;
                case "template":
                    var entry = _templates.FirstOrDefault(t => t.Name == AsString(value));
                    _templateId = entry?.Id ?? ElementId.InvalidElementId;
                    break;
            }
        }

        public override bool IsStepValid(string stepId)
        {
            if (stepId == "S1") return _selectedLinkIds.Count > 0;
            if (stepId == "S2") return !string.IsNullOrWhiteSpace(_namePattern);
            return true;
        }

        public override bool CanRun() =>
            _selectedLinkIds.Count > 0 && !string.IsNullOrWhiteSpace(_namePattern);

        public override string SummaryFor(string stepId)
        {
            if (stepId == "S1") return _selectedLinkIds.Count > 0 ? AppStrings.T("linkviews.bulkViews.byLink.summaries.linkCount", _selectedLinkIds.Count) : "-";
            if (stepId == "S2") return string.IsNullOrWhiteSpace(_namePattern) ? "-" : _namePattern;
            if (stepId == "S3") return AppStrings.T("linkviews.bulkViews.byLink.summaries.S3");
            return "-";
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null)
            {
                pushLog(AppStrings.T("linkviews.bulkViews.byLink.log.handlerMissing"), "fail");
                onComplete(0, 1, 0);
                return;
            }
            _runHandler.LinkInstanceIds = new List<ElementId>(_selectedLinkIds);
            _runHandler.NamePattern     = _namePattern;
            _runHandler.TemplateId      = _templateId;
            _runHandler.PushLog         = pushLog;
            _runHandler.OnProgress      = onProgress;
            _runHandler.OnComplete      = onComplete;
            pushLog(AppStrings.T("linkviews.bulkViews.byLink.log.raising"), "info");
            _runEvent.Raise();
        }

        public void OnWindowClosed()
        {
            if (_runHandler == null) return;
            _runHandler.PushLog = null; _runHandler.OnProgress = null; _runHandler.OnComplete = null;
        }
    }
}
