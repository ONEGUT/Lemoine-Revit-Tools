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

using WpfVisibility = System.Windows.Visibility;
using WpfTextBox    = System.Windows.Controls.TextBox;

namespace LemoineTools.Tools.LinkViews
{
    /// <summary>
    /// Bulk Views by Level — creates 3D / floor-plan / ceiling-plan views per selected
    /// level, either uncropped ("By Level") or bounded by selected scope boxes
    /// ("By Scope Box": plans get the Scope Box parameter assigned; 3D views get the box
    /// bounds copied into their section box).
    ///
    /// The room search / clustering this tool used to run lives in the Scope Box Creator
    /// now — everything here is driven by data captured on the main thread at open, so
    /// there is no scan phase.
    /// </summary>
    public class LinkViewsLevelViewModel : IStepFlowTool, IReviewableTool, IRunResult, IToolCleanup
    {
        // Self-describing result label for the run strip (see IRunResult).
        public string? ResultNoun => "views";
        public IReadOnlyList<ResultChip>? ResultChips => null;

        // ── Identity ──────────────────────────────────────────────────
        public string Title    => AppStrings.T("linkviews.level.title");
        public string RunLabel => AppStrings.T("linkviews.level.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", AppStrings.T("linkviews.level.steps.S1"), required: true),
            new StepDefinition("S2", AppStrings.T("linkviews.level.steps.S2"), required: true),
            new StepDefinition("S3", AppStrings.T("linkviews.level.steps.S3"), required: false),
            new StepDefinition("S4", AppStrings.T("linkviews.level.steps.S4"), required: false),
        };

        // ── Data types passed in from Command ─────────────────────────
        public sealed class LevelEntry
        {
            public ElementId Id          { get; set; } = ElementId.InvalidElementId;
            public string    Name        { get; set; } = string.Empty;
            public double    ElevationFt { get; set; }
        }

        public sealed class ViewTemplateEntry
        {
            public ElementId Id   { get; set; } = ElementId.InvalidElementId;
            public string    Name { get; set; } = string.Empty;
        }

        // Mode tokens (logic identifiers, not externalized).
        private const string ModeByLevel = "ByLevel";
        private const string ModeByBox   = "ByScopeBox";

        // ── Naming ──────────────────────────────────────────────────────
        // Mode-aware default (same idea as Bulk Export's Sheet/View patterns): "By Level"
        // has no scope box, so its default omits the {ScopeBox} token entirely rather than
        // leaving a stray "-  -" when it resolves empty (a flat pattern, unlike the old
        // Front/Center/End slots, does not auto-drop empty segments).
        private const string ToolIdByLevel        = "views.byLevel";
        private const string ToolIdByBox          = "views.byLevel.byBox";
        private const string DefaultPatternByLevel = "L{LevelName} - {ViewType}";
        private const string DefaultPatternByBox   = "L{LevelName} - {ScopeBox} - {ViewType}";

        // ── State ──────────────────────────────────────────────────────
        private readonly List<LevelEntry>        _levels;
        private readonly List<ScopeBoxEntry>     _scopeBoxes;
        private readonly List<ViewTemplateEntry> _templates3D;
        private readonly List<ViewTemplateEntry> _templatesFP;
        private readonly List<ViewTemplateEntry> _templatesRCP;

        private string          _mode             = ModeByLevel;
        private List<ElementId> _selectedLevelIds = new List<ElementId>();
        private List<ElementId> _selectedBoxIds   = new List<ElementId>();
        private Dictionary<string, ElementId> _levelKeyToId = new Dictionary<string, ElementId>(StringComparer.Ordinal);
        private Dictionary<string, ElementId> _boxKeyToId   = new Dictionary<string, ElementId>(StringComparer.Ordinal);

        private bool _create3D  = true;
        private bool _createFP  = true;
        private bool _createRCP = true;
        private string _subDisc3D  = "";
        private string _subDiscFP  = "";
        private string _subDiscRCP = "";
        private ElementId _template3DId  = ElementId.InvalidElementId;
        private ElementId _templateFPId  = ElementId.InvalidElementId;
        private ElementId _templateRCPId = ElementId.InvalidElementId;
        private FrameworkElement? _subDiscRow3D;
        private FrameworkElement? _subDiscRowFP;
        private FrameworkElement? _subDiscRowRCP;

        private string _namePatternByLevel = NamingPatternStore.Instance.GetOrDefault(ToolIdByLevel, DefaultPatternByLevel);
        private string _namePatternByBox   = NamingPatternStore.Instance.GetOrDefault(ToolIdByBox,   DefaultPatternByBox);

        private string ActiveToolId         => _mode == ModeByBox ? ToolIdByBox          : ToolIdByLevel;
        private string ActiveDefaultPattern => _mode == ModeByBox ? DefaultPatternByBox   : DefaultPatternByLevel;
        private string ActiveNamePattern
        {
            get => _mode == ModeByBox ? _namePatternByBox : _namePatternByLevel;
            set { if (_mode == ModeByBox) _namePatternByBox = value; else _namePatternByLevel = value; }
        }

        // S1 live-update handles
        private StackPanel? _boxSection;
        private TextBlock?  _plannedText;

        // ── ExternalEvent wiring ───────────────────────────────────────
        private readonly LinkViewsLevelRunHandler? _runHandler;
        private readonly ExternalEvent?            _runEvent;

        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // Null the callbacks parked on the static handler so this VM (and the closed
        // window's content it references) isn't retained until the tool's next run.
        public void OnWindowClosed()
        {
            if (_runHandler != null)
            {
                _runHandler.PushLog    = null;
                _runHandler.OnProgress = null;
                _runHandler.OnComplete = null;
            }
        }

        public LinkViewsLevelViewModel(
            LinkViewsLevelRunHandler? runHandler, ExternalEvent? runEvent,
            List<LevelEntry>?         levels,
            List<ScopeBoxEntry>?      scopeBoxes,
            List<ViewTemplateEntry>?  templates3D  = null,
            List<ViewTemplateEntry>?  templatesFP  = null,
            List<ViewTemplateEntry>?  templatesRCP = null)
        {
            _runHandler   = runHandler;
            _runEvent     = runEvent;
            _levels       = levels       ?? new List<LevelEntry>();
            _scopeBoxes   = scopeBoxes   ?? new List<ScopeBoxEntry>();
            _templates3D  = templates3D  ?? new List<ViewTemplateEntry>();
            _templatesFP  = templatesFP  ?? new List<ViewTemplateEntry>();
            _templatesRCP = templatesRCP ?? new List<ViewTemplateEntry>();

            // Default: all levels selected; boxes start empty (mode defaults to By Level).
            _selectedLevelIds = _levels.Select(l => l.Id).ToList();
        }

        // ═══════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "S1") return BuildS1();
            if (stepId == "S2") return BuildS2();
            if (stepId == "S3") return BuildS3Naming();
            if (stepId == "S4") return null; // framework renders review (IReviewableTool)
            return null;
        }

        // ── S1: Mode & Levels ──────────────────────────────────────────
        private FrameworkElement BuildS1()
        {
            var outer = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            // ── Extents mode ───────────────────────────────────────────
            AddDimHeader(outer, AppStrings.T("linkviews.level.labels.modeHeader"));

            string byLevelLabel = AppStrings.T("linkviews.level.labels.modeByLevel");
            string byBoxLabel   = AppStrings.T("linkviews.level.labels.modeByScopeBox");
            var modeSelect = new SingleSelect
            {
                Width = 260,
                HorizontalAlignment = HorizontalAlignment.Left,
                Items = new List<string> { byLevelLabel, byBoxLabel },
                SelectedItem = _mode == ModeByBox ? byBoxLabel : byLevelLabel,
            };
            modeSelect.SelectionChanged += v =>
            {
                _mode = v == byBoxLabel ? ModeByBox : ModeByLevel;
                if (_boxSection != null)
                    _boxSection.Visibility = _mode == ModeByBox ? WpfVisibility.Visible : WpfVisibility.Collapsed;
                UpdatePlannedText();
                OnValidationChanged();
            };
            outer.Children.Add(modeSelect);

            var modeHint = new TextBlock
            {
                Text = AppStrings.T("linkviews.level.labels.modeHint"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 10),
            };
            modeHint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            modeHint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            modeHint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(modeHint);

            // ── Scope boxes (By Scope Box mode only) ───────────────────
            _boxSection = new StackPanel
            {
                Visibility = _mode == ModeByBox ? WpfVisibility.Visible : WpfVisibility.Collapsed,
            };

            if (_scopeBoxes.Count == 0)
            {
                var none = new TextBlock
                {
                    Text = AppStrings.T("linkviews.level.labels.noBoxes"),
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 0, 0, 10),
                };
                none.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                none.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                none.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                _boxSection.Children.Add(none);
            }
            else
            {
                _boxKeyToId.Clear();
                var boxGroups = new Dictionary<string, List<string>>
                {
                    ["Scope Boxes"] = _scopeBoxes.Select(b =>
                    {
                        string key = b.Name;
                        _boxKeyToId[key] = b.Id;
                        return key;
                    }).ToList(),
                };

                var boxTabs = new MultiSelectTabs();
                boxTabs.SelectionChanged += selected =>
                {
                    _selectedBoxIds = selected
                        .Where(k => _boxKeyToId.ContainsKey(k))
                        .Select(k => _boxKeyToId[k])
                        .ToList();
                    UpdatePlannedText();
                    OnValidationChanged();
                };
                boxTabs.SetGroups(boxGroups,
                    _selectedBoxIds.Count > 0
                        ? _scopeBoxes.Where(b => _selectedBoxIds.Any(id => id.Value == b.Id.Value))
                                     .Select(b => b.Name).ToList()
                        : new List<string>());
                _boxSection.Children.Add(boxTabs);
                _boxSection.Children.Add(new FrameworkElement { Height = 8 });
            }
            outer.Children.Add(_boxSection);

            // ── Levels ─────────────────────────────────────────────────
            AddDimHeader(outer, AppStrings.T("linkviews.level.labels.levelsHeader"));

            _levelKeyToId.Clear();
            var groups = new Dictionary<string, List<string>>
            {
                ["Levels"] = _levels
                    .OrderBy(l => l.ElevationFt)
                    .Select(l =>
                    {
                        string key = AppStrings.T("linkviews.level.labels.levelRow", l.Name, l.ElevationFt);
                        _levelKeyToId[key] = l.Id;
                        return key;
                    }).ToList(),
            };

            var selectedIdSet = new HashSet<long>(_selectedLevelIds.Select(id => id.Value));
            var preSelected   = _levelKeyToId
                .Where(kv => selectedIdSet.Contains(kv.Value.Value))
                .Select(kv => kv.Key)
                .ToList();

            var levelTabs = new MultiSelectTabs();
            levelTabs.SelectionChanged += selected =>
            {
                _selectedLevelIds = selected
                    .Where(k => _levelKeyToId.ContainsKey(k))
                    .Select(k => _levelKeyToId[k])
                    .GroupBy(id => id.Value)
                    .Select(g => g.First())
                    .ToList();
                UpdatePlannedText();
                OnValidationChanged();
            };
            levelTabs.SetGroups(groups, preSelected);
            outer.Children.Add(levelTabs);

            // ── Planned-count line ─────────────────────────────────────
            _plannedText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
            };
            _plannedText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            _plannedText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            _plannedText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            outer.Children.Add(_plannedText);
            UpdatePlannedText();

            return outer;
        }

        private static void AddDimHeader(StackPanel parent, string text)
        {
            var h = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 6) };
            h.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            h.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            h.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            parent.Children.Add(h);
        }

        private int PlannedViewCount()
        {
            int typeCount = (_create3D ? 1 : 0) + (_createFP ? 1 : 0) + (_createRCP ? 1 : 0);
            int targets   = _mode == ModeByBox ? _selectedBoxIds.Count : 1;
            return _selectedLevelIds.Count * targets * typeCount;
        }

        private void UpdatePlannedText()
        {
            if (_plannedText == null) return;
            _plannedText.Text = AppStrings.T("linkviews.level.labels.plannedCount", PlannedViewCount());
        }

        // ── S2: View Types & Templates ─────────────────────────────────
        private FrameworkElement BuildS2()
        {
            var outer = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            var typeHeader = new TextBlock { Text = AppStrings.T("linkviews.level.labels.typeHeader"),
                                             Margin = new Thickness(0, 0, 0, 6) };
            typeHeader.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            typeHeader.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            typeHeader.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(typeHeader);

            var toggles = new ToggleSwitches();
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "3d",  Label = AppStrings.T("linkviews.level.labels.type3dLabel"),
                                 Desc = AppStrings.T("linkviews.level.labels.type3dDesc"),  DefaultOn = _create3D  },
                new ToggleItem { Id = "fp",  Label = AppStrings.T("linkviews.level.labels.typeFpLabel"),
                                 Desc = AppStrings.T("linkviews.level.labels.typeFpDesc"),  DefaultOn = _createFP  },
                new ToggleItem { Id = "rcp", Label = AppStrings.T("linkviews.level.labels.typeRcpLabel"),
                                 Desc = AppStrings.T("linkviews.level.labels.typeRcpDesc"), DefaultOn = _createRCP },
            });

            // ── Sub Discipline / View Template section ─────────────────
            var subDiscHeader = new TextBlock { Text = AppStrings.T("linkviews.level.labels.optionsHeader"),
                                                Margin = new Thickness(0, 10, 0, 4) };
            subDiscHeader.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            subDiscHeader.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            subDiscHeader.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            // Column header row
            var colHeader = new StackPanel { Orientation = Orientation.Horizontal,
                                             Margin = new Thickness(40, 0, 0, 4) };
            var colSubDisc = new TextBlock { Text = AppStrings.T("linkviews.level.labels.colSubDiscipline"), Width = 120 };
            colSubDisc.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            colSubDisc.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            colSubDisc.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            var colTemplate = new TextBlock { Text = AppStrings.T("linkviews.level.labels.colViewTemplate"), Width = 150,
                                              Margin = new Thickness(8, 0, 0, 0) };
            colTemplate.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            colTemplate.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            colTemplate.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            colHeader.Children.Add(colSubDisc);
            colHeader.Children.Add(colTemplate);

            _subDiscRow3D  = BuildViewTypeRow("3D",  _subDisc3D,  v => _subDisc3D  = v,
                                              _templates3D,  _template3DId,  id => _template3DId  = id, _create3D);
            _subDiscRowFP  = BuildViewTypeRow("FP",  _subDiscFP,  v => _subDiscFP  = v,
                                              _templatesFP,  _templateFPId,  id => _templateFPId  = id, _createFP);
            _subDiscRowRCP = BuildViewTypeRow("RCP", _subDiscRCP, v => _subDiscRCP = v,
                                              _templatesRCP, _templateRCPId, id => _templateRCPId = id, _createRCP);

            toggles.StateChanged += state =>
            {
                _create3D  = state.TryGetValue("3d",  out var v3)  && v3;
                _createFP  = state.TryGetValue("fp",  out var vf)  && vf;
                _createRCP = state.TryGetValue("rcp", out var vr2) && vr2;
                if (_subDiscRow3D  != null) _subDiscRow3D.Visibility  = _create3D  ? WpfVisibility.Visible : WpfVisibility.Collapsed;
                if (_subDiscRowFP  != null) _subDiscRowFP.Visibility  = _createFP  ? WpfVisibility.Visible : WpfVisibility.Collapsed;
                if (_subDiscRowRCP != null) _subDiscRowRCP.Visibility = _createRCP ? WpfVisibility.Visible : WpfVisibility.Collapsed;
                UpdatePlannedText();
                OnValidationChanged();
            };

            outer.Children.Add(toggles);
            outer.Children.Add(subDiscHeader);
            outer.Children.Add(colHeader);
            outer.Children.Add(_subDiscRow3D!);
            outer.Children.Add(_subDiscRowFP!);
            outer.Children.Add(_subDiscRowRCP!);

            return outer;
        }

        private FrameworkElement BuildViewTypeRow(
            string typeLabel,
            string subDiscValue, Action<string> subDiscSetter,
            List<ViewTemplateEntry> templates, ElementId selectedTemplateId, Action<ElementId> templateSetter,
            bool visible)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 0, 0, 4),
                Visibility  = visible ? WpfVisibility.Visible : WpfVisibility.Collapsed,
            };

            var lbl = new TextBlock
            {
                Text              = typeLabel,
                Width             = 40,
                VerticalAlignment = VerticalAlignment.Center,
            };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            row.Children.Add(lbl);

            var tb = new WpfTextBox { Text = subDiscValue, Width = 120 };
            tb.SetResourceReference(FrameworkElement.HeightProperty,                 "LemoineH_Input");
            tb.SetResourceReference(System.Windows.Controls.Control.PaddingProperty, "LemoineTh_InputPad");
            tb.SetResourceReference(WpfTextBox.ForegroundProperty,  "LemoineText");
            tb.SetResourceReference(WpfTextBox.BackgroundProperty,  "LemoineSelectBg");
            tb.SetResourceReference(WpfTextBox.FontSizeProperty,    "LemoineFS_SM");
            tb.SetResourceReference(WpfTextBox.FontFamilyProperty,  "LemoineMonoFont");
            tb.SetResourceReference(WpfTextBox.BorderBrushProperty, "LemoineBorder");
            tb.TextChanged += (s, e) => subDiscSetter(tb.Text);
            row.Children.Add(tb);

            row.Children.Add(new FrameworkElement { Width = 8 });

            var templateNames = new List<string>(new[] { AppStrings.T("linkviews.level.labels.templateNone") }
                .Concat(templates.Select(t => t.Name)));
            string selectedName = templates.FirstOrDefault(
                t => t.Id != null && t.Id.Value == selectedTemplateId.Value)?.Name ?? AppStrings.T("linkviews.level.labels.templateNone");

            var templateSelect = new SingleSelect
            {
                Width        = 150,
                Items        = templateNames,
                SelectedItem = selectedName,
            };
            templateSelect.SelectionChanged += name =>
            {
                var entry = templates.FirstOrDefault(t => t.Name == name);
                templateSetter(entry?.Id ?? ElementId.InvalidElementId);
            };
            row.Children.Add(templateSelect);

            return row;
        }

        // ── S3: View Naming ────────────────────────────────────────────
        private static readonly TokenDefinition[] LevelComputedTokens =
        {
            new TokenDefinition("LevelName", AppStrings.T("naming.computed.byLevel.levelName.label"), TokenOrigin.Computed, TokenSubject.Target, TokenEntity.View),
            new TokenDefinition("ScopeBox",  AppStrings.T("naming.computed.byLevel.scopeBox.label"),  TokenOrigin.Computed, TokenSubject.Target, TokenEntity.View),
        };

        private FrameworkElement BuildS3Naming()
        {
            var outer = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };

            var tokens = NamingTokenRegistry.TokensFor(TokenEntity.View, hasSource: false, LevelComputedTokens);
            var tokenInput = new TokenInput(tokens, ActiveDefaultPattern) { Text = ActiveNamePattern };
            tokenInput.SetPreview(pattern =>
            {
                string levelEx = _levels
                    .Where(l => _selectedLevelIds.Any(id => id.Value == l.Id.Value))
                    .OrderBy(l => l.ElevationFt)
                    .FirstOrDefault()?.Name ?? "02";
                string boxEx = _mode == ModeByBox
                    ? (_scopeBoxes.FirstOrDefault(b => _selectedBoxIds.Any(id => id.Value == b.Id.Value))?.Name
                       ?? _scopeBoxes.FirstOrDefault()?.Name ?? "")
                    : "";
                string typeEx = _create3D ? "3D" : _createFP ? "FP" : "RCP";

                var ctx = new TokenContext();
                ctx.Computed["LevelName"] = levelEx;
                ctx.Computed["ScopeBox"]  = boxEx;
                ctx.Computed["ViewType"]  = typeEx;
                return TokenResolver.Resolve(pattern, ctx);
            });
            tokenInput.TextChanged += (s, e) =>
            {
                ActiveNamePattern = tokenInput.Text;
                NamingPatternStore.Instance.Set(ActiveToolId, ActiveNamePattern);
                OnValidationChanged();
            };
            ValidationChanged += (s, e) => tokenInput.RefreshPreview();
            outer.Children.Add(tokenInput);

            return outer;
        }

        // ── IReviewableTool (S4) ────────────────────────────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("mode",   AppStrings.T("linkviews.level.review.itemMode")),
            ("levels", AppStrings.T("linkviews.level.review.itemLevels")),
            ("boxes",  AppStrings.T("linkviews.level.review.itemBoxes")),
            ("types",  AppStrings.T("linkviews.level.review.itemTypes")),
            ("min",    AppStrings.T("linkviews.level.review.itemMin")),
        };

        public IDictionary<string, string> ReviewValues
        {
            get
            {
                var types = new List<string>();
                if (_create3D)  types.Add("3D");
                if (_createFP)  types.Add("FP");
                if (_createRCP) types.Add("RCP");
                int planned = PlannedViewCount();
                return new Dictionary<string, string>
                {
                    ["mode"]   = _mode == ModeByBox
                        ? AppStrings.T("linkviews.level.labels.modeByScopeBox")
                        : AppStrings.T("linkviews.level.labels.modeByLevel"),
                    ["levels"] = _selectedLevelIds.Count > 0
                        ? AppStrings.T("linkviews.level.review.levelsValue", _selectedLevelIds.Count) : "—",
                    ["boxes"]  = _mode == ModeByBox
                        ? (_selectedBoxIds.Count > 0
                            ? AppStrings.T("linkviews.level.review.boxesValue", _selectedBoxIds.Count) : "—")
                        : AppStrings.T("linkviews.level.review.boxesNotUsed"),
                    ["types"]  = types.Count > 0 ? string.Join(" · ", types) : "—",
                    ["min"]    = planned > 0 ? AppStrings.T("linkviews.level.review.minValue", planned) : "—",
                };
            }
        }

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => AppStrings.T("linkviews.level.review.note");
        public string?        ReviewWarning => _mode == ModeByBox && _scopeBoxes.Count == 0
            ? AppStrings.T("linkviews.level.review.warnNoBoxes")
            : null;

        // ═══════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _selectedLevelIds.Count > 0
                                     && (_mode == ModeByLevel || _selectedBoxIds.Count > 0);
            if (stepId == "S2") return _create3D || _createFP || _createRCP;
            if (stepId == "S3") return true; // naming is always optional
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1")
            {
                string mode = _mode == ModeByBox
                    ? AppStrings.T("linkviews.level.labels.modeByScopeBox")
                    : AppStrings.T("linkviews.level.labels.modeByLevel");
                return _mode == ModeByBox
                    ? AppStrings.T("linkviews.level.summaries.s1Boxes", mode, _selectedLevelIds.Count, _selectedBoxIds.Count)
                    : AppStrings.T("linkviews.level.summaries.s1Levels", mode, _selectedLevelIds.Count);
            }
            if (stepId == "S2")
            {
                var t = new List<string>();
                if (_create3D)  t.Add("3D");
                if (_createFP)  t.Add("FP");
                if (_createRCP) t.Add("RCP");
                return t.Count > 0 ? string.Join("/", t) : "—";
            }
            if (stepId == "S3")
                return string.IsNullOrWhiteSpace(ActiveNamePattern) ? "—" : ActiveNamePattern;
            if (stepId == "S4") return AppStrings.T("linkviews.level.summaries.S4");
            return "—";
        }

        // ═══════════════════════════════════════════════════════════════
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _runHandler!.Mode             = _mode;
            _runHandler.SelectedLevelIds  = new List<ElementId>(
                _selectedLevelIds.GroupBy(id => id.Value).Select(g => g.First()));
            _runHandler.SelectedBoxIds    = new List<ElementId>(_selectedBoxIds);
            _runHandler.Create3D          = _create3D;
            _runHandler.CreateFP          = _createFP;
            _runHandler.CreateRCP         = _createRCP;
            _runHandler.NamePattern        = ActiveNamePattern;
            _runHandler.SubDisc3D          = _subDisc3D;
            _runHandler.SubDiscFP          = _subDiscFP;
            _runHandler.SubDiscRCP         = _subDiscRCP;
            _runHandler.Template3D         = _template3DId;
            _runHandler.TemplateFP         = _templateFPId;
            _runHandler.TemplateRCP        = _templateRCPId;
            _runHandler.PushLog    = pushLog;
            _runHandler.OnProgress = onProgress;
            _runHandler.OnComplete = onComplete;

            pushLog(AppStrings.T("linkviews.level.log.raising"), "info");
            _runEvent!.Raise();
        }
    }
}
