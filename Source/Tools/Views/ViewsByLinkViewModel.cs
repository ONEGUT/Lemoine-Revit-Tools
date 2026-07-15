using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;
using LemoineTools.Framework.Naming;

namespace LemoineTools.Tools.LinkViews
{
    /// <summary>
    /// Bulk Views — "By Link" mode: create one 3D view per selected link, isolated (only that
    /// link visible, all host categories and every other link hidden). See
    /// <see cref="ViewsByLinkRunHandler"/> for the isolation mechanics.
    /// </summary>
    public class ViewsByLinkViewModel : IStepFlowTool, IReviewableTool, IRunResult, IToolCleanup
    {
        public string? ResultNoun => "views";
        public IReadOnlyList<ResultChip>? ResultChips => null;

        public string Title    => AppStrings.T("linkviews.bulkViews.byLink.title");
        public string RunLabel => AppStrings.T("linkviews.bulkViews.byLink.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", AppStrings.T("linkviews.bulkViews.byLink.steps.S1"), required: true),
            new StepDefinition("S2", AppStrings.T("linkviews.bulkViews.byLink.steps.S2"), required: true),
            new StepDefinition("S3", AppStrings.T("linkviews.bulkViews.byLink.steps.S3"), required: false),
        };

        public sealed class LinkEntry
        {
            public ElementId Id   { get; set; } = ElementId.InvalidElementId;
            public string    Name { get; set; } = "";
        }

        public sealed class TemplateEntry
        {
            public ElementId Id   { get; set; } = ElementId.InvalidElementId;
            public string    Name { get; set; } = "";
        }

        private const string ToolId         = "views.byLink";
        private const string DefaultPattern = "{LinkName}";

        private readonly List<LinkEntry>     _links;
        private readonly List<TemplateEntry> _templates;

        private List<ElementId> _selectedLinkIds = new List<ElementId>();
        private string          _namePattern     = NamingPatternStore.Instance.GetOrDefault(ToolId, DefaultPattern);
        private ElementId       _templateId      = ElementId.InvalidElementId;

        private readonly ViewsByLinkRunHandler? _runHandler;
        private readonly ExternalEvent?         _runEvent;

        public event EventHandler? ValidationChanged;
        private void Fire() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public void OnWindowClosed()
        {
            if (_runHandler == null) return;
            _runHandler.PushLog    = null;
            _runHandler.OnProgress = null;
            _runHandler.OnComplete = null;
        }

        public ViewsByLinkViewModel(
            ViewsByLinkRunHandler? runHandler, ExternalEvent? runEvent,
            List<LinkEntry>?       links, List<TemplateEntry>? templates)
        {
            _runHandler = runHandler;
            _runEvent   = runEvent;
            _links      = links     ?? new List<LinkEntry>();
            _templates  = templates ?? new List<TemplateEntry>();
        }

        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "S1") return BuildS1();
            if (stepId == "S2") return BuildS2();
            return null; // "S3" rendered by the framework (IReviewableTool)
        }

        private FrameworkElement BuildS1()
        {
            var outer = new StackPanel();
            if (_links.Count == 0)
            {
                outer.Children.Add(Dim(AppStrings.T("linkviews.bulkViews.byLink.labels.noLinks")));
                return outer;
            }

            var groups = new Dictionary<string, List<string>> { ["Links"] = _links.Select(l => l.Name).ToList() };
            var byName = _links.ToDictionary(l => l.Name, l => l.Id, StringComparer.Ordinal);

            var tabs = new MultiSelectTabs();
            tabs.SelectionChanged += sel =>
            {
                _selectedLinkIds = sel.Where(byName.ContainsKey).Select(n => byName[n]).ToList();
                Fire();
            };
            tabs.SetGroups(groups, new List<string>());
            outer.Children.Add(tabs);
            return outer;
        }

        private FrameworkElement BuildS2()
        {
            var outer = new StackPanel();

            outer.Children.Add(Label(AppStrings.T("linkviews.bulkViews.byLink.labels.namePattern")));
            var tokens = NamingTokenRegistry.TokensFor(TokenEntity.View, hasSource: true);
            var tokenInput = new TokenInput(tokens, DefaultPattern) { Text = _namePattern };
            tokenInput.SetPreview(pattern =>
            {
                string linkName = _links.FirstOrDefault(l => _selectedLinkIds.Any(id => id.Value == l.Id.Value))?.Name
                    ?? AppStrings.T("linkviews.bulkViews.byLink.labels.exLink");
                var ctx = new TokenContext();
                ctx.Computed["LinkName"] = linkName;
                return TokenResolver.Resolve(pattern, ctx);
            });
            tokenInput.TextChanged += (s, e) =>
            {
                _namePattern = tokenInput.Text;
                NamingPatternStore.Instance.Set(ToolId, _namePattern);
                Fire();
            };
            ValidationChanged += (s, e) => tokenInput.RefreshPreview();
            outer.Children.Add(tokenInput);

            outer.Children.Add(new FrameworkElement { Height = 10 });
            outer.Children.Add(Label(AppStrings.T("linkviews.bulkViews.byLink.labels.template")));

            string noneLabel = AppStrings.T("linkviews.bulkViews.byLink.labels.templateNone");
            var names = new List<string> { noneLabel }.Concat(_templates.Select(t => t.Name)).ToList();
            string curName = _templates.FirstOrDefault(t => t.Id.Value == _templateId.Value)?.Name ?? noneLabel;
            var select = new SingleSelect { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Items = names, SelectedItem = curName };
            select.SelectionChanged += name =>
            {
                var entry = _templates.FirstOrDefault(t => t.Name == name);
                _templateId = entry?.Id ?? ElementId.InvalidElementId;
            };
            outer.Children.Add(select);

            return outer;
        }

        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("links", AppStrings.T("linkviews.bulkViews.byLink.review.itemLinks")),
            ("pattern", AppStrings.T("linkviews.bulkViews.byLink.review.itemPattern")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["links"]   = _selectedLinkIds.Count > 0 ? AppStrings.T("linkviews.bulkViews.byLink.review.linksValue", _selectedLinkIds.Count) : "—",
            ["pattern"] = string.IsNullOrWhiteSpace(_namePattern) ? "—" : _namePattern,
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => AppStrings.T("linkviews.bulkViews.byLink.review.note");
        public string?        ReviewWarning => null;

        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _selectedLinkIds.Count > 0;
            if (stepId == "S2") return !string.IsNullOrWhiteSpace(_namePattern);
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1") return _selectedLinkIds.Count > 0 ? AppStrings.T("linkviews.bulkViews.byLink.summaries.linkCount", _selectedLinkIds.Count) : "—";
            if (stepId == "S2") return string.IsNullOrWhiteSpace(_namePattern) ? "—" : _namePattern;
            if (stepId == "S3") return AppStrings.T("linkviews.bulkViews.byLink.summaries.S3");
            return "—";
        }

        public void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null) { pushLog(AppStrings.T("linkviews.bulkViews.byLink.log.handlerMissing"), "fail"); onComplete(0, 1, 0); return; }

            _runHandler.LinkInstanceIds = new List<ElementId>(_selectedLinkIds);
            _runHandler.NamePattern     = _namePattern;
            _runHandler.TemplateId      = _templateId;
            _runHandler.PushLog         = pushLog;
            _runHandler.OnProgress      = onProgress;
            _runHandler.OnComplete      = onComplete;

            pushLog(AppStrings.T("linkviews.bulkViews.byLink.log.raising"), "info");
            _runEvent.Raise();
        }

        private static TextBlock Label(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 6, 0, 4) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static TextBlock Dim(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4), FontStyle = FontStyles.Italic };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }
    }
}
