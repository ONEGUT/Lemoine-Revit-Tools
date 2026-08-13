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
using LemoineTools.Tools.ScopeBoxes;

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
    ///
    /// Optionally (S1's "one copy per scope box" toggle, off by default) fans each selected
    /// view out across the scope boxes picked in the conditional "SB" step instead of making
    /// a single copy — every copy is bound to its scope box, which is what drives its crop.
    /// The duplicate mode above applies unchanged to every copy either way.
    /// </summary>
    public class ViewsBulkDuplicateViewModel : IStepFlowTool, IConditionalSteps, IReviewableTool, IRunResult, IToolCleanup
    {
        // Self-describing result label for the run strip (see IRunResult).
        public string? ResultNoun => "views";
        public System.Collections.Generic.IReadOnlyList<LemoineTools.Framework.ResultChip>? ResultChips => null;

        // ── Identity ──────────────────────────────────────────────────
        public string Title    => AppStrings.T("linkviews.duplicate.title");
        public string RunLabel => AppStrings.T("linkviews.duplicate.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", AppStrings.T("linkviews.duplicate.steps.S1"),   required: true),
            // Hidden unless S1's per-scope-box toggle is on (IConditionalSteps). Never last —
            // the final step carries the Run button and review summary.
            new StepDefinition("SB", AppStrings.T("linkviews.duplicate.steps.SB"),   required: true),
            new StepDefinition("S2", AppStrings.T("linkviews.duplicate.steps.S2"), required: true),
            new StepDefinition("S3", AppStrings.T("linkviews.duplicate.steps.S3"),    required: true),
            new StepDefinition("S4", AppStrings.T("linkviews.duplicate.steps.S4"),   required: false),
        };

        // ── IConditionalSteps — the scope-box step exists only in per-scope-box mode ──
        public bool IsStepVisible(string stepId) => stepId != "SB" || _bindToScopeBoxes;

        // ── Duplicate-mode labels (must match the run handler's mapping) ──
        public const string ModeDuplicate     = "Duplicate";
        public const string ModeWithDetailing = "Duplicate with Detailing";
        public const string ModeAsDependent   = "Duplicate as Dependent";
        private static readonly string[] DuplicateModes =
        {
            ModeDuplicate, ModeWithDetailing, ModeAsDependent,
        };

        // ── Naming ──────────────────────────────────────────────────────
        private const string ToolId         = "views.duplicate";
        private const string DefaultPattern = "{ViewName} - Copy";

        // Per-scope-box mode keeps its OWN remembered pattern and default: every copy of one
        // source view differs only by scope box, so a pattern without {ScopeBoxName} would
        // resolve every copy to the same name and all but the first would be skipped as a
        // name collision — a silent under-count. The default carries the token.
        private const string ToolIdScopeBox         = "views.duplicateScopeBox";
        private const string DefaultScopeBoxPattern = "{ViewName} - {ScopeBoxName}";

        /// <summary>Scope-box name for this run's copy — supplied per item via TokenContext.Computed.</summary>
        private static readonly TokenDefinition[] ScopeBoxComputedTokens =
        {
            new TokenDefinition("ScopeBoxName", AppStrings.T("naming.computed.duplicate.scopeBoxName.label"),
                TokenOrigin.Computed, TokenSubject.Target, TokenEntity.View,
                AppStrings.T("naming.computed.duplicate.scopeBoxName.desc")),
        };

        // ── Data type passed in from Command (main thread) ────────────
        public sealed class ViewEntry
        {
            public ElementId Id        { get; set; } = ElementId.InvalidElementId;
            public string    Name      { get; set; } = string.Empty;
            public string    TypeLabel { get; set; } = string.Empty;
        }

        // ── State ──────────────────────────────────────────────────────
        private readonly List<ViewEntry>     _views;
        private readonly BrowserTree         _browserTree;
        private readonly List<ScopeBoxEntry> _scopeBoxes;

        private List<ElementId> _selectedViewIds = new List<ElementId>();
        private string          _mode            = ModeWithDetailing;
        private string          _namePattern     = NamingPatternStore.Instance.GetOrDefault(ToolId, DefaultPattern);

        // ── Per-scope-box mode ─────────────────────────────────────────
        private bool            _bindToScopeBoxes    = false;
        private List<ElementId> _selectedScopeBoxIds = new List<ElementId>();
        private string          _scopeBoxNamePattern =
            NamingPatternStore.Instance.GetOrDefault(ToolIdScopeBox, DefaultScopeBoxPattern);

        /// <summary>Picker display label → scope-box id. Labels are made unique on build, so a
        /// project with two identically-named scope boxes still maps each row to one element.</summary>
        private readonly Dictionary<string, ElementId> _scopeBoxDisplayToId =
            new Dictionary<string, ElementId>(StringComparer.Ordinal);

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
            BrowserTree?           browserTree = null,
            List<ScopeBoxEntry>?   scopeBoxes  = null)
        {
            _runHandler  = runHandler;
            _runEvent    = runEvent;
            _views       = views ?? new List<ViewEntry>();
            _browserTree = browserTree ?? new BrowserTree();
            _scopeBoxes  = scopeBoxes ?? new List<ScopeBoxEntry>();
        }

        // ═══════════════════════════════════════════════════════════════
        // GetStepContent
        // ═══════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "S1") return BuildViewPicker();
            if (stepId == "SB") return BuildScopeBoxPicker();
            if (stepId == "S2") return BuildModePicker();
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
                AccessibleName = AppStrings.T("linkviews.duplicate.labels.sourceViews"),
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

            // ── Per-scope-box toggle ───────────────────────────────────
            // Toggling raises ValidationChanged, which is what makes StepFlowWindow
            // re-evaluate IsStepVisible and show/hide the "SB" step live.
            var toggle = new CheckBox
            {
                Content   = AppStrings.T("linkviews.duplicate.labels.perScopeBox"),
                IsChecked = _bindToScopeBoxes,
                Margin    = new Thickness(0, 14, 0, 0),
            };
            toggle.SetResourceReference(CheckBox.ForegroundProperty, "LemoineText");
            toggle.SetResourceReference(CheckBox.FontFamilyProperty, "LemoineUiFont");
            toggle.SetResourceReference(CheckBox.FontSizeProperty,   "LemoineFS_MD");
            toggle.Checked   += (s, e) => { _bindToScopeBoxes = true;  OnValidationChanged(); };
            toggle.Unchecked += (s, e) => { _bindToScopeBoxes = false; OnValidationChanged(); };
            outer.Children.Add(toggle);

            var toggleHint = new TextBlock
            {
                Text         = AppStrings.T("linkviews.duplicate.labels.perScopeBoxHint"),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(22, 3, 0, 0),
            };
            toggleHint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            toggleHint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            toggleHint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(toggleHint);

            return outer;
        }

        // ── SB: Scope Boxes (visible only in per-scope-box mode) ───────
        private FrameworkElement BuildScopeBoxPicker()
        {
            var outer = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            var header = new TextBlock
            {
                Text   = AppStrings.T("linkviews.duplicate.labels.scopeBoxHeader"),
                Margin = new Thickness(0, 0, 0, 6),
            };
            header.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            header.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            header.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(header);

            // No scope boxes in the project — say so plainly rather than showing an empty list.
            if (_scopeBoxes.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text         = AppStrings.T("linkviews.duplicate.labels.noScopeBoxes"),
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle    = FontStyles.Italic,
                };
                empty.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                empty.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                empty.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                outer.Children.Add(empty);
                return outer;
            }

            // Rebuild the label→id map from scratch; labels are disambiguated so two scope
            // boxes sharing a name still resolve to distinct elements.
            _scopeBoxDisplayToId.Clear();
            var labels = new List<string>();
            foreach (var box in _scopeBoxes)
            {
                string baseName = string.IsNullOrWhiteSpace(box.Name)
                    ? AppStrings.T("linkviews.duplicate.labels.unnamedScopeBox", box.Id.Value)
                    : box.Name;
                string label = baseName;
                int n = 2;
                while (_scopeBoxDisplayToId.ContainsKey(label))
                    label = AppStrings.T("linkviews.duplicate.labels.scopeBoxDisambiguated", baseName, n++);
                _scopeBoxDisplayToId[label] = box.Id;
                labels.Add(label);
            }

            var tabs = new MultiSelectTabs { AccessibleName = AppStrings.T("linkviews.duplicate.labels.scopeBoxes") };
            // Subscribe BEFORE SetGroups — its end-of-setup SelectionChanged seeds the mirror list.
            tabs.SelectionChanged += sel =>
            {
                _selectedScopeBoxIds = sel
                    .Where(_scopeBoxDisplayToId.ContainsKey)
                    .Select(s => _scopeBoxDisplayToId[s])
                    .ToList();
                OnValidationChanged();
            };

            var preSelected = _scopeBoxDisplayToId
                .Where(kv => _selectedScopeBoxIds.Any(id => id.Value == kv.Value.Value))
                .Select(kv => kv.Key)
                .ToList();

            tabs.SetGroups(
                new Dictionary<string, List<string>>
                {
                    [AppStrings.T("linkviews.duplicate.labels.scopeBoxes")] = labels,
                },
                preSelected);
            outer.Children.Add(tabs);

            return outer;
        }

        // ── S2: Duplicate Mode ─────────────────────────────────────────
        private FrameworkElement BuildModePicker()
        {
            var outer = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            var header = new TextBlock { Text = AppStrings.T("linkviews.duplicate.labels.modeHeader"), Margin = new Thickness(0, 0, 0, 6) };
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
                        ? AppStrings.T("linkviews.duplicate.labels.hintDuplicate")
                    : _mode == ModeWithDetailing
                        ? AppStrings.T("linkviews.duplicate.labels.hintDetailing")
                        : AppStrings.T("linkviews.duplicate.labels.hintDependent");
            }

            var select = new SingleSelect
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
        // Both panels are built once and live in the same container; the S1 toggle only swaps
        // which one is visible. Each keeps its own remembered pattern, so switching modes never
        // overwrites the other's. (Visibility swap rather than an IStepAware rebuild — no
        // re-parenting, so nothing can hit the "already the logical child" crash.)
        private FrameworkElement BuildNaming()
        {
            var outer = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };

            var standardPanel  = BuildStandardNamingPanel();
            var scopeBoxPanel  = BuildScopeBoxNamingPanel();
            outer.Children.Add(standardPanel);
            outer.Children.Add(scopeBoxPanel);

            void SyncPanels()
            {
                standardPanel.Visibility = _bindToScopeBoxes ? WpfVisibility.Collapsed : WpfVisibility.Visible;
                scopeBoxPanel.Visibility = _bindToScopeBoxes ? WpfVisibility.Visible   : WpfVisibility.Collapsed;
            }
            SyncPanels();
            ValidationChanged += (s, e) => SyncPanels();

            return outer;
        }

        private FrameworkElement BuildStandardNamingPanel()
        {
            var panel = new StackPanel();

            var header = new TextBlock { Text = AppStrings.T("linkviews.duplicate.labels.namePattern"), Margin = new Thickness(0, 0, 0, 6) };
            header.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            header.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            header.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            panel.Children.Add(header);

            var tokens = NamingTokenRegistry.TokensFor(TokenEntity.View, hasSource: false);
            var tokenInput = new TokenInput(tokens, DefaultPattern) { Text = _namePattern };
            panel.Children.Add(tokenInput);

            tokenInput.SetPreview(pattern =>
            {
                var ctx = BuildPreviewContext();
                return TokenResolver.Resolve(pattern, ctx);
            });

            tokenInput.TextChanged += (s, e) =>
            {
                _namePattern = tokenInput.Text;
                NamingPatternStore.Instance.Set(ToolId, _namePattern);
                OnValidationChanged();
            };
            ValidationChanged += (s, e) => tokenInput.RefreshPreview();

            return panel;
        }

        private FrameworkElement BuildScopeBoxNamingPanel()
        {
            var panel = new StackPanel();

            var header = new TextBlock { Text = AppStrings.T("linkviews.duplicate.labels.namePatternScopeBox"), Margin = new Thickness(0, 0, 0, 6) };
            header.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            header.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            header.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            panel.Children.Add(header);

            var tokens = NamingTokenRegistry.TokensFor(TokenEntity.View, hasSource: false, ScopeBoxComputedTokens);
            var tokenInput = new TokenInput(tokens, DefaultScopeBoxPattern) { Text = _scopeBoxNamePattern };
            panel.Children.Add(tokenInput);

            tokenInput.SetPreview(pattern =>
            {
                var ctx = BuildPreviewContext();
                ctx.Computed["ScopeBoxName"] = FirstPreviewScopeBoxName();
                return TokenResolver.Resolve(pattern, ctx);
            });

            tokenInput.TextChanged += (s, e) =>
            {
                _scopeBoxNamePattern = tokenInput.Text;
                NamingPatternStore.Instance.Set(ToolIdScopeBox, _scopeBoxNamePattern);
                OnValidationChanged();
            };
            ValidationChanged += (s, e) => tokenInput.RefreshPreview();

            return panel;
        }

        private TokenContext BuildPreviewContext()
        {
            var v = FirstSelectedView();
            var ctx = new TokenContext();
            ctx.Computed["ViewName"] = v?.Name      ?? AppStrings.T("linkviews.duplicate.labels.exView");
            ctx.Computed["ViewType"] = v?.TypeLabel ?? AppStrings.T("linkviews.duplicate.labels.exType");
            return ctx;
        }

        /// <summary>Sample scope-box name for the naming preview — the first selected box,
        /// else the first in the project, else a placeholder.</summary>
        private string FirstPreviewScopeBoxName()
        {
            var firstSelected = _selectedScopeBoxIds.FirstOrDefault();
            if (firstSelected != null)
            {
                var match = _scopeBoxes.FirstOrDefault(b => b.Id.Value == firstSelected.Value);
                if (match != null && !string.IsNullOrWhiteSpace(match.Name)) return match.Name;
            }
            var first = _scopeBoxes.FirstOrDefault(b => !string.IsNullOrWhiteSpace(b.Name));
            return first?.Name ?? AppStrings.T("linkviews.duplicate.labels.exScopeBox");
        }

        private ViewEntry? FirstSelectedView()
        {
            var id = _selectedViewIds.FirstOrDefault();
            return id == null ? null : _views.FirstOrDefault(v => v.Id.Value == id.Value);
        }

        // ── IReviewableTool — framework renders the review step ─────
        // Built per-get so the scope-box row appears only in per-scope-box mode.
        public IList<(string id, string label)> ReviewItems
        {
            get
            {
                var items = new List<(string, string)>
                {
                    ("views", AppStrings.T("linkviews.duplicate.review.itemViews")),
                };
                if (_bindToScopeBoxes)
                    items.Add(("scopeBoxes", AppStrings.T("linkviews.duplicate.review.itemScopeBoxes")));
                items.Add(("mode",    AppStrings.T("linkviews.duplicate.review.itemMode")));
                items.Add(("pattern", AppStrings.T("linkviews.duplicate.review.itemPattern")));
                items.Add(("total",   AppStrings.T("linkviews.duplicate.review.itemTotal")));
                return items;
            }
        }

        public IDictionary<string, string> ReviewValues
        {
            get
            {
                var values = new Dictionary<string, string>
                {
                    ["views"]   = _selectedViewIds.Count > 0 ? AppStrings.T("linkviews.duplicate.review.viewsValue", _selectedViewIds.Count) : "—",
                    ["mode"]    = _mode,
                    ["pattern"] = string.IsNullOrWhiteSpace(ActivePattern) ? "—" : ActivePattern,
                    ["total"]   = PlannedCount > 0 ? AppStrings.T("linkviews.duplicate.review.totalValue", PlannedCount) : "—",
                };
                if (_bindToScopeBoxes)
                    values["scopeBoxes"] = _selectedScopeBoxIds.Count > 0
                        ? AppStrings.T("linkviews.duplicate.review.scopeBoxesValue", _selectedScopeBoxIds.Count)
                        : "—";
                return values;
            }
        }

        public IList<string>? ReviewChips => null;

        public string? ReviewNote => _bindToScopeBoxes
            ? AppStrings.T("linkviews.duplicate.review.noteScopeBox")
            : AppStrings.T("linkviews.duplicate.review.note");

        public string? ReviewWarning
        {
            get
            {
                if (_bindToScopeBoxes)
                {
                    // Without {ScopeBoxName}, every copy of one source view resolves to the same
                    // name — all but the first are skipped as collisions and the user silently
                    // gets one view instead of N. Catch it before the run, not after.
                    if (_selectedScopeBoxIds.Count > 1 &&
                        (_scopeBoxNamePattern == null ||
                         _scopeBoxNamePattern.IndexOf("{ScopeBoxName}", StringComparison.OrdinalIgnoreCase) < 0))
                        return AppStrings.T("linkviews.duplicate.review.warnNoScopeBoxToken");
                }
                return (ActivePattern != null && ActivePattern.Trim() == "{ViewName}")
                    ? AppStrings.T("linkviews.duplicate.review.warnSameName")
                    : null;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // IsValid / SummaryFor
        // ═══════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _selectedViewIds.Count > 0;
            if (stepId == "SB") return !_bindToScopeBoxes || _selectedScopeBoxIds.Count > 0;
            if (stepId == "S2") return !string.IsNullOrEmpty(_mode);
            if (stepId == "S3") return !string.IsNullOrWhiteSpace(ActivePattern);
            return true;
        }

        /// <summary>The naming pattern the current mode actually runs with.</summary>
        private string ActivePattern => _bindToScopeBoxes ? _scopeBoxNamePattern : _namePattern;

        /// <summary>Copies this run will attempt: one per view, or one per view × scope box.</summary>
        private int PlannedCount => _bindToScopeBoxes
            ? _selectedViewIds.Count * _selectedScopeBoxIds.Count
            : _selectedViewIds.Count;

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1")
            {
                if (_selectedViewIds.Count == 0) return "—";
                return _bindToScopeBoxes
                    ? AppStrings.T("linkviews.duplicate.summaries.viewCountScoped", _selectedViewIds.Count)
                    : AppStrings.T("linkviews.duplicate.summaries.viewCount", _selectedViewIds.Count);
            }
            if (stepId == "SB") return _selectedScopeBoxIds.Count > 0
                ? AppStrings.T("linkviews.duplicate.summaries.scopeBoxCount", _selectedScopeBoxIds.Count)
                : "—";
            if (stepId == "S2") return _mode;
            if (stepId == "S3") return string.IsNullOrWhiteSpace(ActivePattern) ? "—" : ActivePattern;
            if (stepId == "S4") return AppStrings.T("linkviews.duplicate.summaries.S4");
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
            // Each mode runs with its own pattern — hand over the active one.
            _runHandler.NamePattern     = ActivePattern;
            _runHandler.BindToScopeBoxes = _bindToScopeBoxes;
            // Built by filtering the authoritative scope-box list (rather than resolving each
            // selected id back into it), so no selection can silently resolve to nothing and
            // the run order stays the picker's own alphabetical order.
            _runHandler.SelectedScopeBoxes = _bindToScopeBoxes
                ? _scopeBoxes
                    .Where(b => _selectedScopeBoxIds.Any(id => id.Value == b.Id.Value))
                    .Select(b => new ViewsBulkDuplicateRunHandler.ScopeBoxTarget { Id = b.Id, Name = b.Name })
                    .ToList()
                : new List<ViewsBulkDuplicateRunHandler.ScopeBoxTarget>();
            _runHandler.PushLog         = pushLog;
            _runHandler.OnProgress      = onProgress;
            _runHandler.OnComplete      = onComplete;

            pushLog(AppStrings.T("linkviews.duplicate.log.raising"), "info");
            _runEvent!.Raise();
        }
    }
}
