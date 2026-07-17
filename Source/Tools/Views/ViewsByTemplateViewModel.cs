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

using WpfGrid       = System.Windows.Controls.Grid;
using WpfPoint      = System.Windows.Point;
using WpfTextBox    = System.Windows.Controls.TextBox;
using WpfVisibility = System.Windows.Visibility;

namespace LemoineTools.Tools.LinkViews
{
    /// <summary>
    /// Cross-multiplies selected source views by selected view templates: for every
    /// view×template pair it duplicates the view (With Detailing), applies the template,
    /// and names the result from a token/chip pattern (the Bulk Export naming control).
    /// </summary>
    public class ViewsByTemplateViewModel : IStepFlowTool, IReviewableTool, IRunResult, IToolCleanup
    {
        // Self-describing result label for the run strip (see IRunResult).
        public string? ResultNoun => "views";
        public System.Collections.Generic.IReadOnlyList<LemoineTools.Framework.ResultChip>? ResultChips => null;

        // ── Identity ──────────────────────────────────────────────────
        public string Title    => AppStrings.T("linkviews.byTemplate.title");
        public string RunLabel => AppStrings.T("linkviews.byTemplate.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", AppStrings.T("linkviews.byTemplate.steps.S1"),   required: true),
            new StepDefinition("S2", AppStrings.T("linkviews.byTemplate.steps.S2"), required: true),
            new StepDefinition("S3", AppStrings.T("linkviews.byTemplate.steps.S3"),    required: true),
            new StepDefinition("S4", AppStrings.T("linkviews.byTemplate.steps.S4"),   required: false),
        };

        // ── Data types passed in from Command (main thread) ───────────
        public sealed class ViewEntry
        {
            public ElementId Id        { get; set; } = ElementId.InvalidElementId;
            public string    Name      { get; set; } = string.Empty;
            public string    TypeLabel { get; set; } = string.Empty;
        }

        public sealed class TemplateEntry
        {
            public ElementId Id        { get; set; } = ElementId.InvalidElementId;
            public string    Name      { get; set; } = string.Empty;
            public string    TypeLabel { get; set; } = string.Empty;
        }

        // ── Naming ──────────────────────────────────────────────────────
        private const string ToolId         = "views.byTemplate";
        private const string DefaultPattern = "{ViewName} - {TemplateName}";

        // ── State ──────────────────────────────────────────────────────
        private readonly List<ViewEntry>     _views;
        private readonly List<TemplateEntry> _templates;
        private readonly BrowserTree  _browserTree;

        private readonly Dictionary<string, ElementId> _templateKeyToId = new Dictionary<string, ElementId>(StringComparer.Ordinal);

        private List<ElementId> _selectedViewIds     = new List<ElementId>();
        private List<ElementId> _selectedTemplateIds = new List<ElementId>();
        private string          _namePattern         = NamingPatternStore.Instance.GetOrDefault(ToolId, DefaultPattern);

        // ── ExternalEvent wiring ───────────────────────────────────────
        private readonly ViewsByTemplateRunHandler? _runHandler;
        private readonly ExternalEvent?             _runEvent;

        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // Null the callbacks parked on the static handler so this VM isn't retained after close.
        public void OnWindowClosed()
        {
            if (_runHandler == null) return;
            _runHandler.PushLog    = null;
            _runHandler.OnProgress = null;
            _runHandler.OnComplete = null;
        }

        public ViewsByTemplateViewModel(
            ViewsByTemplateRunHandler? runHandler, ExternalEvent? runEvent,
            List<ViewEntry>?           views,
            List<TemplateEntry>?       templates,
            BrowserTree?        browserTree = null)
        {
            _runHandler  = runHandler;
            _runEvent    = runEvent;
            _views       = views     ?? new List<ViewEntry>();
            _templates   = templates ?? new List<TemplateEntry>();
            _browserTree = browserTree ?? new BrowserTree();
        }

        // ═══════════════════════════════════════════════════════════════
        // GetStepContent
        // ═══════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "S1") return BuildViewPicker();
            if (stepId == "S2") return BuildTemplatePicker();
            if (stepId == "S3") return BuildNaming();
            if (stepId == "S4") return null; // framework renders review (IReviewableTool)
            return null;
        }

        // ── S1: Source Views ───────────────────────────────────────────
        private FrameworkElement BuildViewPicker()
        {
            var picker = new BrowserTreePicker
            {
                Height         = 300,
                AccessibleName = AppStrings.T("linkviews.byTemplate.labels.sourceViews"),
            };
            // Subscribe BEFORE SetTree — its end-of-setup SelectionChanged seeds the mirror list.
            picker.SelectionChanged += ids =>
            {
                _selectedViewIds = ids.Select(id => new ElementId(id)).ToList();
                OnValidationChanged();
            };
            picker.SetTree(_browserTree,
                _views.Select(v => v.Id.Value),
                _selectedViewIds.Select(id => id.Value).ToList());

            var outer = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            outer.Children.Add(picker);
            return outer;
        }

        // ── S2: View Templates ─────────────────────────────────────────
        private FrameworkElement BuildTemplatePicker()
        {
            _templateKeyToId.Clear();

            var groups = new Dictionary<string, List<string>>();
            foreach (var t in _templates.OrderBy(t => t.TypeLabel).ThenBy(t => t.Name))
            {
                string key = t.Name;                     // template names are unique
                if (_templateKeyToId.ContainsKey(key))
                    key = $"{t.Name}  [{t.Id.Value}]";
                _templateKeyToId[key] = t.Id;

                if (!groups.TryGetValue(t.TypeLabel, out var list))
                    groups[t.TypeLabel] = list = new List<string>();
                list.Add(key);
            }

            var tabs = new MultiSelectTabs { AccessibleName = AppStrings.T("linkviews.byTemplate.labels.viewTemplates") };
            tabs.SelectionChanged += selected =>
            {
                _selectedTemplateIds = selected
                    .Where(s => _templateKeyToId.ContainsKey(s))
                    .Select(s => _templateKeyToId[s])
                    .ToList();
                OnValidationChanged();
            };
            tabs.SetGroups(groups, _selectedTemplateIds
                .Select(id => _templateKeyToId.FirstOrDefault(kv => kv.Value.Value == id.Value).Key)
                .Where(k => k != null));

            var outer = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            outer.Children.Add(tabs);

            if (_templates.Count == 0)
            {
                var msg = new TextBlock
                {
                    Text         = AppStrings.T("linkviews.byTemplate.labels.noTemplates"),
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle    = FontStyles.Italic,
                    Margin       = new Thickness(0, 4, 0, 0),
                };
                msg.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                msg.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                msg.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                outer.Children.Add(msg);
            }
            return outer;
        }

        // ── S3: View Naming (token/chip pattern) ───────────────────────
        private FrameworkElement BuildNaming()
        {
            var outer = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };

            var header = new TextBlock { Text = AppStrings.T("linkviews.byTemplate.labels.namePattern"), Margin = new Thickness(0, 0, 0, 6) };
            header.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            header.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            header.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(header);

            var tokens = NamingTokenRegistry.TokensFor(TokenEntity.View, hasSource: true);
            var tokenInput = new TokenInput(tokens, DefaultPattern) { Text = _namePattern };
            outer.Children.Add(tokenInput);

            tokenInput.SetPreview(pattern =>
            {
                var v = FirstSelectedView();
                var t = FirstSelectedTemplate();
                var ctx = new TokenContext();
                ctx.Computed["ViewName"]     = v?.Name ?? AppStrings.T("linkviews.byTemplate.labels.exView");
                ctx.Computed["ViewType"]     = v?.TypeLabel ?? AppStrings.T("linkviews.byTemplate.labels.exType");
                ctx.Computed["TemplateName"] = t?.Name ?? AppStrings.T("linkviews.byTemplate.labels.exTemplate");
                return TokenResolver.Resolve(pattern, ctx);
            });

            tokenInput.TextChanged += (s, e) =>
            {
                _namePattern = tokenInput.Text;
                NamingPatternStore.Instance.Set(ToolId, _namePattern);
                OnValidationChanged();
            };
            ValidationChanged += (s, e) => tokenInput.RefreshPreview();

            return outer;
        }

        private ViewEntry?     FirstSelectedView()
        {
            var id = _selectedViewIds.FirstOrDefault();
            return id == null ? null : _views.FirstOrDefault(v => v.Id.Value == id.Value);
        }

        private TemplateEntry? FirstSelectedTemplate()
        {
            var id = _selectedTemplateIds.FirstOrDefault();
            return id == null ? null : _templates.FirstOrDefault(t => t.Id.Value == id.Value);
        }

        // ── IReviewableTool — framework renders the review step ─────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("views",     AppStrings.T("linkviews.byTemplate.review.itemViews")),
            ("templates", AppStrings.T("linkviews.byTemplate.review.itemTemplates")),
            ("pattern",   AppStrings.T("linkviews.byTemplate.review.itemPattern")),
            ("total",     AppStrings.T("linkviews.byTemplate.review.itemTotal")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["views"]     = _selectedViewIds.Count     > 0 ? AppStrings.T("linkviews.byTemplate.review.viewsValue", _selectedViewIds.Count)         : "—",
            ["templates"] = _selectedTemplateIds.Count > 0 ? AppStrings.T("linkviews.byTemplate.review.templatesValue", _selectedTemplateIds.Count) : "—",
            ["pattern"]   = string.IsNullOrWhiteSpace(_namePattern) ? "—" : _namePattern,
            ["total"]     = _selectedViewIds.Count > 0 && _selectedTemplateIds.Count > 0
                                ? AppStrings.T("linkviews.byTemplate.review.totalValue", _selectedViewIds.Count * _selectedTemplateIds.Count)
                                : "—",
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => AppStrings.T("linkviews.byTemplate.review.note");
        public string?        ReviewWarning =>
            (_selectedViewIds.Count > 0 && _selectedTemplateIds.Count > 1
             && !_namePattern.Contains("{TemplateName}"))
                ? AppStrings.T("linkviews.byTemplate.review.warnNoToken")
                : null;

        // ═══════════════════════════════════════════════════════════════
        // IsValid / SummaryFor
        // ═══════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _selectedViewIds.Count     > 0;
            if (stepId == "S2") return _selectedTemplateIds.Count > 0;
            if (stepId == "S3") return !string.IsNullOrWhiteSpace(_namePattern);
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1") return _selectedViewIds.Count     > 0 ? AppStrings.T("linkviews.byTemplate.summaries.viewCount", _selectedViewIds.Count)         : "—";
            if (stepId == "S2") return _selectedTemplateIds.Count > 0 ? AppStrings.T("linkviews.byTemplate.summaries.templateCount", _selectedTemplateIds.Count) : "—";
            if (stepId == "S3") return string.IsNullOrWhiteSpace(_namePattern) ? "—" : _namePattern;
            if (stepId == "S4") return AppStrings.T("linkviews.byTemplate.summaries.S4");
            return "—";
        }

        // ═══════════════════════════════════════════════════════════════
        // Run
        // ═══════════════════════════════════════════════════════════════
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _runHandler!.SelectedViewIds     = new List<ElementId>(_selectedViewIds);
            _runHandler.SelectedTemplateIds = new List<ElementId>(_selectedTemplateIds);
            _runHandler.NamePattern         = _namePattern;
            _runHandler.PushLog             = pushLog;
            _runHandler.OnProgress          = onProgress;
            _runHandler.OnComplete          = onComplete;

            pushLog(AppStrings.T("linkviews.byTemplate.log.raising"), "info");
            _runEvent!.Raise();
        }
    }
}
