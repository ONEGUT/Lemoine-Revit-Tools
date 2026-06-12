using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

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
    public class ViewsByTemplateViewModel : ILemoineTool, ILemoineReviewable, ILemoineRunResult
    {
        // Self-describing result label for the run strip (see ILemoineRunResult).
        public string? ResultNoun => "views";
        public System.Collections.Generic.IReadOnlyList<LemoineTools.Lemoine.ResultChip>? ResultChips => null;

        // ── Identity ──────────────────────────────────────────────────
        public string Title    => "Bulk Views by Template";
        public string RunLabel => "Create Views in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Source Views",   required: true),
            new StepDefinition("S2", "View Templates", required: true),
            new StepDefinition("S3", "View Naming",    required: true),
            new StepDefinition("S4", "Review & Run",   required: false),
        };

        // ── Data types passed in from Command (main thread) ───────────
        public sealed class ViewEntry
        {
            public ElementId Id        { get; set; }
            public string    Name      { get; set; }
            public string    TypeLabel { get; set; }
        }

        public sealed class TemplateEntry
        {
            public ElementId Id        { get; set; }
            public string    Name      { get; set; }
            public string    TypeLabel { get; set; }
        }

        // ── Naming tokens (same chip control as Bulk Export) ──────────
        private static readonly (string Label, string Token)[] NamingTokens =
        {
            ("View Name",     "{ViewName}"),
            ("Template Name", "{TemplateName}"),
            ("View Type",     "{ViewType}"),
        };
        private const string DefaultPattern = "{ViewName} - {TemplateName}";

        // ── State ──────────────────────────────────────────────────────
        private readonly List<ViewEntry>     _views;
        private readonly List<TemplateEntry> _templates;
        private readonly LemoineBrowserTree  _browserTree;

        private readonly Dictionary<string, ElementId> _templateKeyToId = new Dictionary<string, ElementId>(StringComparer.Ordinal);

        private List<ElementId> _selectedViewIds     = new List<ElementId>();
        private List<ElementId> _selectedTemplateIds = new List<ElementId>();
        private string          _namePattern         = DefaultPattern;

        // ── ExternalEvent wiring ───────────────────────────────────────
        private readonly ViewsByTemplateRunHandler? _runHandler;
        private readonly ExternalEvent?             _runEvent;

        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public ViewsByTemplateViewModel(
            ViewsByTemplateRunHandler? runHandler, ExternalEvent? runEvent,
            List<ViewEntry>?           views,
            List<TemplateEntry>?       templates,
            LemoineBrowserTree?        browserTree = null)
        {
            _runHandler  = runHandler;
            _runEvent    = runEvent;
            _views       = views     ?? new List<ViewEntry>();
            _templates   = templates ?? new List<TemplateEntry>();
            _browserTree = browserTree ?? new LemoineBrowserTree();
        }

        // ═══════════════════════════════════════════════════════════════
        // GetStepContent
        // ═══════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "S1") return BuildViewPicker();
            if (stepId == "S2") return BuildTemplatePicker();
            if (stepId == "S3") return BuildNaming();
            if (stepId == "S4") return null; // framework renders review (ILemoineReviewable)
            return null;
        }

        // ── S1: Source Views ───────────────────────────────────────────
        private FrameworkElement BuildViewPicker()
        {
            var picker = new LemoineBrowserTreePicker
            {
                Height         = 300,
                AccessibleName = "Source views",
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

            var tabs = new LemoineMultiSelectTabs { AccessibleName = "View templates" };
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
                    Text         = "No view templates found in this document.",
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

            var header = new TextBlock { Text = "NAME PATTERN", Margin = new Thickness(0, 0, 0, 6) };
            header.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            header.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            header.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(header);

            var tokenInput = new LemoineTokenInput(NamingTokens, DefaultPattern) { Text = _namePattern };
            outer.Children.Add(tokenInput);

            // ── Live preview ───────────────────────────────────────────
            var sep = new System.Windows.Shapes.Rectangle { Height = 1, Margin = new Thickness(0, 12, 0, 10) };
            sep.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "LemoineBorder");
            outer.Children.Add(sep);

            var previewHeader = new TextBlock { Text = "PREVIEW", Margin = new Thickness(0, 0, 0, 4) };
            previewHeader.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            previewHeader.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            previewHeader.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(previewHeader);

            var previewText = new TextBlock { TextWrapping = TextWrapping.Wrap };
            previewText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            previewText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            previewText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var previewBorder = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
                Padding         = new Thickness(10, 6, 10, 6),
            };
            previewBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            previewBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            previewBorder.Child = previewText;
            outer.Children.Add(previewBorder);

            void UpdatePreview()
            {
                var v = FirstSelectedView();
                var t = FirstSelectedTemplate();
                string viewName = v?.Name      ?? "Floor Plan 01";
                string viewType = v?.TypeLabel ?? "Floor Plan";
                string tmplName = t?.Name      ?? "Coordination";

                string resolved = LemoineTokenInput.Resolve(_namePattern,
                    new Dictionary<string, string>
                    {
                        ["ViewName"]     = viewName,
                        ["ViewType"]     = viewType,
                        ["TemplateName"] = tmplName,
                    });
                previewText.Text = string.IsNullOrWhiteSpace(resolved)
                    ? "(empty name)"
                    : resolved;
            }

            tokenInput.TextChanged += (s, e) =>
            {
                _namePattern = tokenInput.Text;
                UpdatePreview();
                OnValidationChanged();
            };
            ValidationChanged += (s, e) => UpdatePreview();
            UpdatePreview();

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

        // ── ILemoineReviewable — framework renders the review step ─────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("views",     "Source Views"),
            ("templates", "View Templates"),
            ("pattern",   "Name Pattern"),
            ("total",     "Views to Create"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["views"]     = _selectedViewIds.Count     > 0 ? $"{_selectedViewIds.Count} view(s)"         : "—",
            ["templates"] = _selectedTemplateIds.Count > 0 ? $"{_selectedTemplateIds.Count} template(s)" : "—",
            ["pattern"]   = string.IsNullOrWhiteSpace(_namePattern) ? "—" : _namePattern,
            ["total"]     = _selectedViewIds.Count > 0 && _selectedTemplateIds.Count > 0
                                ? $"up to {_selectedViewIds.Count * _selectedTemplateIds.Count}"
                                : "—",
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => "Each selected view is duplicated (with detailing) once per template. " +
            "Pairs whose name already exists are skipped; a template that cannot apply to a view's type is reported as failed.";
        public string?        ReviewWarning =>
            (_selectedViewIds.Count > 0 && _selectedTemplateIds.Count > 1
             && !_namePattern.Contains("{TemplateName}"))
                ? "Name pattern has no {TemplateName} token — every template resolves to the same name per view, " +
                  "so only the first copy is created and the rest are skipped. Add {TemplateName} to differentiate."
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
            if (stepId == "S1") return _selectedViewIds.Count     > 0 ? $"{_selectedViewIds.Count} view(s)"         : "—";
            if (stepId == "S2") return _selectedTemplateIds.Count > 0 ? $"{_selectedTemplateIds.Count} template(s)" : "—";
            if (stepId == "S3") return string.IsNullOrWhiteSpace(_namePattern) ? "—" : _namePattern;
            if (stepId == "S4") return "Ready to run";
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

            pushLog("Raising Revit ExternalEvent…", "info");
            _runEvent!.Raise();
        }
    }
}
