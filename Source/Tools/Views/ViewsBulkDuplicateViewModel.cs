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
    /// Bulk-duplicates selected views using one of the three standard Revit duplicate
    /// options (Duplicate, Duplicate with Detailing, Duplicate as Dependent) and names
    /// each copy from a token/chip pattern (the Bulk Export naming control).
    /// </summary>
    public class ViewsBulkDuplicateViewModel : ILemoineTool, ILemoineReviewable, ILemoineRunResult, ILemoineToolCleanup
    {
        // Self-describing result label for the run strip (see ILemoineRunResult).
        public string? ResultNoun => "views";
        public System.Collections.Generic.IReadOnlyList<LemoineTools.Lemoine.ResultChip>? ResultChips => null;

        // ── Identity ──────────────────────────────────────────────────
        public string Title    => LemoineStrings.T("linkviews.duplicate.title");
        public string RunLabel => LemoineStrings.T("linkviews.duplicate.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", LemoineStrings.T("linkviews.duplicate.steps.S1"),   required: true),
            new StepDefinition("S2", LemoineStrings.T("linkviews.duplicate.steps.S2"), required: true),
            new StepDefinition("S3", LemoineStrings.T("linkviews.duplicate.steps.S3"),    required: true),
            new StepDefinition("S4", LemoineStrings.T("linkviews.duplicate.steps.S4"),   required: false),
        };

        // ── Duplicate-mode labels (must match the run handler's mapping) ──
        public const string ModeDuplicate     = "Duplicate";
        public const string ModeWithDetailing = "Duplicate with Detailing";
        public const string ModeAsDependent   = "Duplicate as Dependent";
        private static readonly string[] DuplicateModes =
        {
            ModeDuplicate, ModeWithDetailing, ModeAsDependent,
        };

        // ── Naming tokens (same chip control as Bulk Export) ──────────
        private static readonly (string Label, string Token)[] NamingTokens =
        {
            ("View Name", "{ViewName}"),
            ("View Type", "{ViewType}"),
        };
        private const string DefaultPattern = "{ViewName} - Copy";

        // ── Data type passed in from Command (main thread) ────────────
        public sealed class ViewEntry
        {
            public ElementId Id        { get; set; } = ElementId.InvalidElementId;
            public string    Name      { get; set; } = string.Empty;
            public string    TypeLabel { get; set; } = string.Empty;
        }

        // ── State ──────────────────────────────────────────────────────
        private readonly List<ViewEntry>    _views;
        private readonly LemoineBrowserTree _browserTree;

        private List<ElementId> _selectedViewIds = new List<ElementId>();
        private string          _mode            = ModeWithDetailing;
        private string          _namePattern     = DefaultPattern;

        // ── ExternalEvent wiring ───────────────────────────────────────
        private readonly ViewsBulkDuplicateRunHandler? _runHandler;
        private readonly ExternalEvent?                _runEvent;

        public event EventHandler? ValidationChanged;

        // Null the callbacks parked on the static handler so this VM isn't retained after close.
        public void OnWindowClosed()
        {
            if (_runHandler == null) return;
            _runHandler.PushLog    = null;
            _runHandler.OnProgress = null;
            _runHandler.OnComplete = null;
        }

        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public ViewsBulkDuplicateViewModel(
            ViewsBulkDuplicateRunHandler? runHandler, ExternalEvent? runEvent,
            List<ViewEntry>?              views,
            LemoineBrowserTree?           browserTree = null)
        {
            _runHandler  = runHandler;
            _runEvent    = runEvent;
            _views       = views ?? new List<ViewEntry>();
            _browserTree = browserTree ?? new LemoineBrowserTree();
        }

        // ═══════════════════════════════════════════════════════════════
        // GetStepContent
        // ═══════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "S1") return BuildViewPicker();
            if (stepId == "S2") return BuildModePicker();
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
                AccessibleName = LemoineStrings.T("linkviews.duplicate.labels.sourceViews"),
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

        // ── S2: Duplicate Mode ─────────────────────────────────────────
        private FrameworkElement BuildModePicker()
        {
            var outer = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            var header = new TextBlock { Text = LemoineStrings.T("linkviews.duplicate.labels.modeHeader"), Margin = new Thickness(0, 0, 0, 6) };
            header.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            header.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            header.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(header);

            var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            hint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            hint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            hint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            void UpdateHint()
            {
                hint.Text = _mode == ModeDuplicate
                        ? LemoineStrings.T("linkviews.duplicate.labels.hintDuplicate")
                    : _mode == ModeWithDetailing
                        ? LemoineStrings.T("linkviews.duplicate.labels.hintDetailing")
                        : LemoineStrings.T("linkviews.duplicate.labels.hintDependent");
            }

            var select = new LemoineSingleSelect
            {
                Width        = 220,
                Items        = DuplicateModes,
                SelectedItem = _mode,
            };
            select.SelectionChanged += v =>
            {
                if (string.IsNullOrEmpty(v)) return;
                _mode = v!;
                UpdateHint();
                OnValidationChanged();
            };
            outer.Children.Add(select);

            UpdateHint();
            outer.Children.Add(hint);
            return outer;
        }

        // ── S3: View Naming (token/chip pattern) ───────────────────────
        private FrameworkElement BuildNaming()
        {
            var outer = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };

            var header = new TextBlock { Text = LemoineStrings.T("linkviews.duplicate.labels.namePattern"), Margin = new Thickness(0, 0, 0, 6) };
            header.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            header.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            header.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(header);

            var tokenInput = new LemoineTokenInput(NamingTokens, DefaultPattern) { Text = _namePattern };
            outer.Children.Add(tokenInput);

            var sep = new System.Windows.Shapes.Rectangle { Height = 1, Margin = new Thickness(0, 12, 0, 10) };
            sep.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "LemoineBorder");
            outer.Children.Add(sep);

            var previewHeader = new TextBlock { Text = LemoineStrings.T("linkviews.duplicate.labels.preview"), Margin = new Thickness(0, 0, 0, 4) };
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
                string viewName = v?.Name      ?? LemoineStrings.T("linkviews.duplicate.labels.exView");
                string viewType = v?.TypeLabel ?? LemoineStrings.T("linkviews.duplicate.labels.exType");

                string resolved = LemoineTokenInput.Resolve(_namePattern,
                    new Dictionary<string, string>
                    {
                        ["ViewName"] = viewName,
                        ["ViewType"] = viewType,
                    });
                previewText.Text = string.IsNullOrWhiteSpace(resolved) ? LemoineStrings.T("linkviews.duplicate.labels.emptyName") : resolved;
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

        private ViewEntry? FirstSelectedView()
        {
            var id = _selectedViewIds.FirstOrDefault();
            return id == null ? null : _views.FirstOrDefault(v => v.Id.Value == id.Value);
        }

        // ── ILemoineReviewable — framework renders the review step ─────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("views",   LemoineStrings.T("linkviews.duplicate.review.itemViews")),
            ("mode",    LemoineStrings.T("linkviews.duplicate.review.itemMode")),
            ("pattern", LemoineStrings.T("linkviews.duplicate.review.itemPattern")),
            ("total",   LemoineStrings.T("linkviews.duplicate.review.itemTotal")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["views"]   = _selectedViewIds.Count > 0 ? LemoineStrings.T("linkviews.duplicate.review.viewsValue", _selectedViewIds.Count) : "—",
            ["mode"]    = _mode,
            ["pattern"] = string.IsNullOrWhiteSpace(_namePattern) ? "—" : _namePattern,
            ["total"]   = _selectedViewIds.Count > 0 ? LemoineStrings.T("linkviews.duplicate.review.totalValue", _selectedViewIds.Count) : "—",
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => LemoineStrings.T("linkviews.duplicate.review.note");
        public string?        ReviewWarning =>
            (_namePattern != null && _namePattern.Trim() == "{ViewName}")
                ? LemoineStrings.T("linkviews.duplicate.review.warnSameName")
                : null;

        // ═══════════════════════════════════════════════════════════════
        // IsValid / SummaryFor
        // ═══════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _selectedViewIds.Count > 0;
            if (stepId == "S2") return !string.IsNullOrEmpty(_mode);
            if (stepId == "S3") return !string.IsNullOrWhiteSpace(_namePattern);
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1") return _selectedViewIds.Count > 0 ? LemoineStrings.T("linkviews.duplicate.summaries.viewCount", _selectedViewIds.Count) : "—";
            if (stepId == "S2") return _mode;
            if (stepId == "S3") return string.IsNullOrWhiteSpace(_namePattern) ? "—" : _namePattern;
            if (stepId == "S4") return LemoineStrings.T("linkviews.duplicate.summaries.S4");
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
            _runHandler!.SelectedViewIds = new List<ElementId>(_selectedViewIds);
            _runHandler.Mode            = _mode;
            _runHandler.NamePattern     = _namePattern;
            _runHandler.PushLog         = pushLog;
            _runHandler.OnProgress      = onProgress;
            _runHandler.OnComplete      = onComplete;

            pushLog(LemoineStrings.T("linkviews.duplicate.log.raising"), "info");
            _runEvent!.Raise();
        }
    }
}
