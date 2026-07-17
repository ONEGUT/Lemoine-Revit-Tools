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
    /// <summary>Web port of <see cref="ViewsByTemplateViewModel"/> — cross-multiply views by
    /// templates. Same handler, entries, pattern store, and AppStrings keys.</summary>
    public class ViewsByTemplateWebTool : WebToolBase, IWebToolCleanup
    {
        private const string ToolId         = "views.byTemplate";
        private const string DefaultPattern = "{ViewName} - {TemplateName}";

        private readonly List<ViewsByTemplateViewModel.ViewEntry>     _views;
        private readonly List<ViewsByTemplateViewModel.TemplateEntry> _templates;
        private readonly BrowserTree _browserTree;
        private readonly Dictionary<string, ElementId> _templateKeyToId = new Dictionary<string, ElementId>(StringComparer.Ordinal);
        private readonly ViewsByTemplateRunHandler? _runHandler;
        private readonly ExternalEvent?             _runEvent;

        private List<ElementId> _selectedViewIds     = new List<ElementId>();
        private List<ElementId> _selectedTemplateIds = new List<ElementId>();
        private string          _namePattern         = NamingPatternStore.Instance.GetOrDefault(ToolId, DefaultPattern);

        public ViewsByTemplateWebTool(
            ViewsByTemplateRunHandler? runHandler, ExternalEvent? runEvent,
            List<ViewsByTemplateViewModel.ViewEntry>?     views,
            List<ViewsByTemplateViewModel.TemplateEntry>? templates,
            BrowserTree? browserTree = null)
        {
            _runHandler  = runHandler;
            _runEvent    = runEvent;
            _views       = views     ?? new List<ViewsByTemplateViewModel.ViewEntry>();
            _templates   = templates ?? new List<ViewsByTemplateViewModel.TemplateEntry>();
            _browserTree = browserTree ?? new BrowserTree();

            foreach (var t in _templates.OrderBy(t => t.TypeLabel).ThenBy(t => t.Name))
            {
                string key = _templateKeyToId.ContainsKey(t.Name) ? $"{t.Name}  [{t.Id.Value}]" : t.Name;
                _templateKeyToId[key] = t.Id;
            }
        }

        public override string Title    => AppStrings.T("linkviews.byTemplate.title");
        public override string RunLabel => AppStrings.T("linkviews.byTemplate.runLabel");

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            var s1 = new WebStep("S1", AppStrings.T("linkviews.byTemplate.steps.S1"))
                .Add(WebInput.BrowserTree("views", AppStrings.T("linkviews.byTemplate.labels.sourceViews"),
                    _browserTree, _selectedViewIds.Select(id => id.Value)));

            var groups = new Dictionary<string, List<string>>();
            foreach (var kv in _templateKeyToId)
            {
                var t = _templates.First(x => x.Id.Value == kv.Value.Value);
                if (!groups.TryGetValue(t.TypeLabel, out var list)) groups[t.TypeLabel] = list = new List<string>();
                list.Add(kv.Key);
            }
            var selectedKeys = _selectedTemplateIds
                .Select(id => _templateKeyToId.FirstOrDefault(kv => kv.Value.Value == id.Value).Key)
                .Where(k => k != null).Select(k => k!);
            var s2 = new WebStep("S2", AppStrings.T("linkviews.byTemplate.steps.S2"));
            if (_templates.Count == 0)
                s2.Add(WebInput.Hint("noTemplates", AppStrings.T("linkviews.byTemplate.labels.noTemplates")));
            else
                s2.Add(WebInput.MultiSelectTabs("templates",
                    AppStrings.T("linkviews.byTemplate.labels.viewTemplates"), groups, selectedKeys));

            var tokens = NamingTokenRegistry.TokensFor(TokenEntity.View, hasSource: true);
            var ctx = new TokenContext();
            var v = _views.FirstOrDefault(x => _selectedViewIds.Any(id => id.Value == x.Id.Value));
            var tpl = _templates.FirstOrDefault(x => _selectedTemplateIds.Any(id => id.Value == x.Id.Value));
            ctx.Computed["ViewName"]     = v?.Name      ?? AppStrings.T("linkviews.byTemplate.labels.exView");
            ctx.Computed["ViewType"]     = v?.TypeLabel ?? AppStrings.T("linkviews.byTemplate.labels.exType");
            ctx.Computed["TemplateName"] = tpl?.Name    ?? AppStrings.T("linkviews.byTemplate.labels.exTemplate");
            var s3 = new WebStep("S3", AppStrings.T("linkviews.byTemplate.steps.S3"))
                .Add(WebInput.TokenInput("pattern", AppStrings.T("linkviews.byTemplate.labels.namePattern"),
                    _namePattern, DefaultPattern, tokens, SampleFor(tokens, ctx)));

            var s4 = new WebStep("S4", AppStrings.T("linkviews.byTemplate.steps.S4"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("linkviews.byTemplate.review.itemViews"),
                     _selectedViewIds.Count > 0 ? AppStrings.T("linkviews.byTemplate.review.viewsValue", _selectedViewIds.Count) : "-"),
                    (AppStrings.T("linkviews.byTemplate.review.itemTemplates"),
                     _selectedTemplateIds.Count > 0 ? AppStrings.T("linkviews.byTemplate.review.templatesValue", _selectedTemplateIds.Count) : "-"),
                    (AppStrings.T("linkviews.byTemplate.review.itemPattern"),
                     string.IsNullOrWhiteSpace(_namePattern) ? "-" : _namePattern),
                    (AppStrings.T("linkviews.byTemplate.review.itemTotal"),
                     _selectedViewIds.Count > 0 && _selectedTemplateIds.Count > 0
                        ? AppStrings.T("linkviews.byTemplate.review.totalValue", _selectedViewIds.Count * _selectedTemplateIds.Count) : "-"),
                },
                note: AppStrings.T("linkviews.byTemplate.review.note"),
                warning: (_selectedViewIds.Count > 0 && _selectedTemplateIds.Count > 1
                          && !_namePattern.Contains("{TemplateName}"))
                    ? AppStrings.T("linkviews.byTemplate.review.warnNoToken") : null));

            return new List<WebStep> { s1, s2, s3, s4 };
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "views":
                    _selectedViewIds = IdList(value).Select(v2 => new ElementId(v2)).ToList();
                    Fire();
                    break;
                case "templates":
                    _selectedTemplateIds = StrList(value)
                        .Where(_templateKeyToId.ContainsKey).Select(k => _templateKeyToId[k]).ToList();
                    Fire();
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
            if (stepId == "S2") return _selectedTemplateIds.Count > 0;
            if (stepId == "S3") return !string.IsNullOrWhiteSpace(_namePattern);
            return true;
        }

        public override bool CanRun() =>
            _selectedViewIds.Count > 0 && _selectedTemplateIds.Count > 0 && !string.IsNullOrWhiteSpace(_namePattern);

        public override string SummaryFor(string stepId)
        {
            if (stepId == "S1") return _selectedViewIds.Count > 0 ? AppStrings.T("linkviews.byTemplate.summaries.viewCount", _selectedViewIds.Count) : "-";
            if (stepId == "S2") return _selectedTemplateIds.Count > 0 ? AppStrings.T("linkviews.byTemplate.summaries.templateCount", _selectedTemplateIds.Count) : "-";
            if (stepId == "S3") return string.IsNullOrWhiteSpace(_namePattern) ? "-" : _namePattern;
            if (stepId == "S4") return AppStrings.T("linkviews.byTemplate.summaries.S4");
            return "-";
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null) { pushLog("Run handler not registered.", "fail"); onComplete(0, 1, 0); return; }
            _runHandler.SelectedViewIds     = new List<ElementId>(_selectedViewIds);
            _runHandler.SelectedTemplateIds = new List<ElementId>(_selectedTemplateIds);
            _runHandler.NamePattern         = _namePattern;
            _runHandler.PushLog             = pushLog;
            _runHandler.OnProgress          = onProgress;
            _runHandler.OnComplete          = onComplete;
            pushLog(AppStrings.T("linkviews.byTemplate.log.raising"), "info");
            _runEvent.Raise();
        }

        public void OnWindowClosed()
        {
            if (_runHandler == null) return;
            _runHandler.PushLog = null; _runHandler.OnProgress = null; _runHandler.OnComplete = null;
        }
    }
}
