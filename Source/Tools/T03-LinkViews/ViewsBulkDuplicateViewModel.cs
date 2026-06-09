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
    public class ViewsBulkDuplicateViewModel : ILemoineTool, ILemoineReviewable
    {
        // ── Identity ──────────────────────────────────────────────────
        public string Title    => "Bulk Duplicate Views";
        public string RunLabel => "Duplicate in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Source Views",   required: true),
            new StepDefinition("S2", "Duplicate Mode", required: true),
            new StepDefinition("S3", "View Naming",    required: true),
            new StepDefinition("S4", "Review & Run",   required: false),
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
            public ElementId Id        { get; set; }
            public string    Name      { get; set; }
            public string    TypeLabel { get; set; }
        }

        // ── State ──────────────────────────────────────────────────────
        private readonly List<ViewEntry> _views;
        private readonly Dictionary<string, ElementId> _viewKeyToId = new Dictionary<string, ElementId>(StringComparer.Ordinal);

        private List<ElementId> _selectedViewIds = new List<ElementId>();
        private string          _mode            = ModeWithDetailing;
        private string          _namePattern     = DefaultPattern;

        // ── ExternalEvent wiring ───────────────────────────────────────
        private readonly ViewsBulkDuplicateRunHandler? _runHandler;
        private readonly ExternalEvent?                _runEvent;

        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public ViewsBulkDuplicateViewModel(
            ViewsBulkDuplicateRunHandler? runHandler, ExternalEvent? runEvent,
            List<ViewEntry>?              views)
        {
            _runHandler = runHandler;
            _runEvent   = runEvent;
            _views      = views ?? new List<ViewEntry>();
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
            _viewKeyToId.Clear();

            var groups = new Dictionary<string, List<string>>();
            foreach (var v in _views.OrderBy(v => v.TypeLabel).ThenBy(v => v.Name))
            {
                string key = v.Name;                 // view names are unique in a document
                if (_viewKeyToId.ContainsKey(key))   // defensive: disambiguate any rare clash
                    key = $"{v.Name}  [{v.Id.Value}]";
                _viewKeyToId[key] = v.Id;

                if (!groups.TryGetValue(v.TypeLabel, out var list))
                    groups[v.TypeLabel] = list = new List<string>();
                list.Add(key);
            }

            var tabs = new LemoineMultiSelectTabs { AccessibleName = "Source views" };
            tabs.SelectionChanged += selected =>
            {
                _selectedViewIds = selected
                    .Where(s => _viewKeyToId.ContainsKey(s))
                    .Select(s => _viewKeyToId[s])
                    .ToList();
                OnValidationChanged();
            };
            tabs.SetGroups(groups, _selectedViewIds
                .Select(id => _viewKeyToId.FirstOrDefault(kv => kv.Value.Value == id.Value).Key)
                .Where(k => k != null));

            var outer = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            outer.Children.Add(tabs);
            return outer;
        }

        // ── S2: Duplicate Mode ─────────────────────────────────────────
        private FrameworkElement BuildModePicker()
        {
            var outer = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            var header = new TextBlock { Text = "DUPLICATE MODE", Margin = new Thickness(0, 0, 0, 6) };
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
                        ? "Copies the model view only — no view-specific annotations or detailing."
                    : _mode == ModeWithDetailing
                        ? "Copies the view together with its view-specific annotations (dimensions, tags, detail lines)."
                        : "Creates a dependent view that shares the parent's crop region and stays linked to it.";
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
                _mode = v;
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

            var header = new TextBlock { Text = "NAME PATTERN", Margin = new Thickness(0, 0, 0, 6) };
            header.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            header.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            header.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(header);

            var tokenInput = new LemoineTokenInput(NamingTokens, DefaultPattern) { Text = _namePattern };
            outer.Children.Add(tokenInput);

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
                string viewName = v?.Name      ?? "Floor Plan 01";
                string viewType = v?.TypeLabel ?? "Floor Plan";

                string resolved = LemoineTokenInput.Resolve(_namePattern,
                    new Dictionary<string, string>
                    {
                        ["ViewName"] = viewName,
                        ["ViewType"] = viewType,
                    });
                previewText.Text = string.IsNullOrWhiteSpace(resolved) ? "(empty name)" : resolved;
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
            ("views",   "Source Views"),
            ("mode",    "Duplicate Mode"),
            ("pattern", "Name Pattern"),
            ("total",   "Views to Create"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["views"]   = _selectedViewIds.Count > 0 ? $"{_selectedViewIds.Count} view(s)" : "—",
            ["mode"]    = _mode,
            ["pattern"] = string.IsNullOrWhiteSpace(_namePattern) ? "—" : _namePattern,
            ["total"]   = _selectedViewIds.Count > 0 ? $"up to {_selectedViewIds.Count}" : "—",
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => "Each selected view is duplicated once. Copies whose name already exists are skipped; " +
            "a view that does not support the chosen duplicate mode is reported as failed.";
        public string?        ReviewWarning =>
            (_namePattern != null && _namePattern.Trim() == "{ViewName}")
                ? "Name pattern resolves to each view's own name — every copy would collide with its source and be skipped. " +
                  "Add a suffix or token to differentiate."
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
            if (stepId == "S1") return _selectedViewIds.Count > 0 ? $"{_selectedViewIds.Count} view(s)" : "—";
            if (stepId == "S2") return _mode;
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
            _runHandler!.SelectedViewIds = new List<ElementId>(_selectedViewIds);
            _runHandler.Mode            = _mode;
            _runHandler.NamePattern     = _namePattern;
            _runHandler.PushLog         = pushLog;
            _runHandler.OnProgress      = onProgress;
            _runHandler.OnComplete      = onComplete;

            pushLog("Raising Revit ExternalEvent…", "info");
            _runEvent!.Raise();
        }
    }
}
