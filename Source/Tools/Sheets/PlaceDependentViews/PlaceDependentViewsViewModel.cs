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

using WpfGrid = System.Windows.Controls.Grid;

namespace LemoineTools.Tools.Sheets.PlaceDependentViews
{
    public sealed class PlaceDependentViewsViewModel : IStepFlowTool, IReviewableTool, IToolCleanup
    {
        private const string ToolId         = "sheets.placeDependent";
        private const string DefaultPattern = "{ParentViewName}";

        // ── IStepFlowTool ──────────────────────────────────────────────────────
        public string Title    => AppStrings.T("testing.placeDependentViews.title");
        public string RunLabel => AppStrings.T("testing.placeDependentViews.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", AppStrings.T("testing.placeDependentViews.steps.S1"), required: true),
            new StepDefinition("S2", AppStrings.T("testing.placeDependentViews.steps.S2"),    required: true),
            new StepDefinition("S3", AppStrings.T("testing.placeDependentViews.steps.S3"),   required: true),
            new StepDefinition("S4", AppStrings.T("testing.placeDependentViews.steps.S4"),         required: false),
            new StepDefinition("S5", AppStrings.T("testing.placeDependentViews.steps.S5"),   required: false),
        };

        public event EventHandler? ValidationChanged;

        // Null the callbacks parked on the static handler so this VM isn't retained after close.
        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.PushLog    = null;
            _handler.OnProgress = null;
            _handler.OnComplete = null;
        }

        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        private static readonly string ModeDependents = AppStrings.T("testing.placeDependentViews.modeDependents");
        private static readonly string ModeComposite  = AppStrings.T("testing.placeDependentViews.modeComposite");

        // ── State ─────────────────────────────────────────────────────────────
        private bool _compositeMode = false;

        private readonly List<ParentViewEntry>          _parents;
        private readonly BrowserTree             _browserTree;
        private List<ElementId>                          _selectedParentIds = new List<ElementId>();

        private readonly List<ParentViewEntry>          _compositeCandidates;
        private List<ElementId>                          _selectedCompositeIds = new List<ElementId>();

        private readonly List<string>                   _titleblockNames;
        private readonly Dictionary<string, ElementId>  _titleblockMap;
        private string _selectedTitleblock = "";

        private int    _startingNumber = 1;
        private string _namingPattern  = NamingPatternStore.Instance.GetOrDefault(ToolId, DefaultPattern);
        private string _numberPrefix    = "";
        private string _numberSuffix    = "";
        private string _sheetSeries     = "";
        private string _seriesParamName = "Sheet Series";

        private LayoutMode _layoutMode = LayoutMode.Measured;
        private bool   _trimBubbles   = true;

        private static readonly string ModeAccurate = AppStrings.T("testing.placeDependentViews.modeAccurate");
        private static readonly string ModeGrouped  = AppStrings.T("testing.placeDependentViews.modeGrouped");
        private static readonly string ModeEstimate = AppStrings.T("testing.placeDependentViews.modeEstimate");

        /// <summary>Short label for the active layout mode, used in the review/summary rows.</summary>
        private string LayoutLabel => _layoutMode == LayoutMode.Estimate ? AppStrings.T("testing.placeDependentViews.layoutLabel.estimate")
                                    : _layoutMode == LayoutMode.Grouped  ? AppStrings.T("testing.placeDependentViews.layoutLabel.grouped")
                                    : AppStrings.T("testing.placeDependentViews.layoutLabel.accurate");
        private double _trimInches    = 0.125;
        private double _marginTop     = 0.5;
        private double _marginBottom  = 0.5;
        private double _marginLeft    = 0.5;
        private double _marginRight   = 0.5;
        private double _gapInches     = 0.25;

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly PlaceDependentViewsEventHandler? _handler;
        private readonly ExternalEvent?                    _event;

        // ── Constructors ──────────────────────────────────────────────────────
        public PlaceDependentViewsViewModel(
            PlaceDependentViewsEventHandler? handler,
            ExternalEvent?                   externalEvent,
            List<ParentViewEntry>?           parents,
            List<ParentViewEntry>?           compositeCandidates,
            List<FamilySymbol>?              titleblocks,
            BrowserTree?              browserTree = null)
        {
            _handler     = handler;
            _event       = externalEvent;
            _browserTree = browserTree ?? new BrowserTree();

            _parents = (parents ?? new List<ParentViewEntry>())
                .OrderBy(p => p.TypeLabel).ThenBy(p => p.Name).ToList();

            _compositeCandidates = (compositeCandidates ?? new List<ParentViewEntry>())
                .OrderBy(p => p.TypeLabel).ThenBy(p => p.Name).ToList();

            _titleblockMap   = new Dictionary<string, ElementId>();
            _titleblockNames = new List<string>();
            foreach (var tb in titleblocks ?? Enumerable.Empty<FamilySymbol>())
            {
                string label = $"{tb.FamilyName} : {tb.Name}";
                if (!_titleblockMap.ContainsKey(label))
                {
                    _titleblockMap[label] = tb.Id;
                    _titleblockNames.Add(label);
                }
            }
            if (_titleblockNames.Count > 0) _selectedTitleblock = _titleblockNames[0];
        }

        /// <summary>Settings-only constructor (no document open).</summary>
        public PlaceDependentViewsViewModel() : this(null, null, null, null, null) { }

        // ── Content ───────────────────────────────────────────────────────────
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildS1();
                case "S2": return BuildS2();
                case "S3": return BuildS3();
                case "S4": return BuildS4();
                case "S5": return null; // framework renders IReviewableTool
                default:   return null;
            }
        }

        // ── Step 1 — Mode + views to place ────────────────────────────────────
        private FrameworkElement BuildS1()
        {
            var outer = new StackPanel();

            outer.Children.Add(SectionLabel(AppStrings.T("testing.placeDependentViews.labels.secMode")));
            var modeSelect = new SingleSelect();
            modeSelect.Items = new List<string> { ModeDependents, ModeComposite };
            modeSelect.SelectedItem = _compositeMode ? ModeComposite : ModeDependents;
            outer.Children.Add(modeSelect);

            var modeNote = Note(ModeNoteText());
            modeNote.Margin = new Thickness(0, 4, 0, 0);
            outer.Children.Add(modeNote);

            var pickerHost = new Border { Margin = new Thickness(0, 12, 0, 0) };
            pickerHost.Child = BuildModePicker();
            outer.Children.Add(pickerHost);

            modeSelect.SelectionChanged += s =>
            {
                _compositeMode = s == ModeComposite;
                modeNote.Text  = ModeNoteText();
                pickerHost.Child = BuildModePicker();
                OnValidationChanged();
            };

            return outer;
        }

        private string ModeNoteText() => _compositeMode
            ? AppStrings.T("testing.placeDependentViews.labels.modeNoteComposite")
            : AppStrings.T("testing.placeDependentViews.labels.modeNoteDependents");

        // Both modes pick views from the same Project-Browser tree; the mode only
        // changes which views are selectable and where the selection is mirrored.
        private FrameworkElement BuildModePicker()
        {
            if (_compositeMode)
            {
                if (_compositeCandidates.Count == 0)
                    return Hint(AppStrings.T("testing.placeDependentViews.labels.hintNoComposite"));

                var picker = new BrowserTreePicker
                {
                    Height         = 300,
                    AccessibleName = AppStrings.T("testing.placeDependentViews.labels.pickerCompositeName"),
                };
                // Subscribe BEFORE SetTree — its end-of-setup SelectionChanged seeds the mirror list.
                picker.SelectionChanged += ids =>
                {
                    _selectedCompositeIds = ids.Select(id => new ElementId(id)).ToList();
                    OnValidationChanged();
                };
                picker.SetTree(_browserTree,
                    _compositeCandidates.Select(p => p.Id.Value),
                    _selectedCompositeIds.Select(id => id.Value).ToList());
                return picker;
            }
            else
            {
                if (_parents.Count == 0)
                    return Hint(AppStrings.T("testing.placeDependentViews.labels.hintNoParents"));

                var picker = new BrowserTreePicker
                {
                    Height         = 300,
                    AccessibleName = AppStrings.T("testing.placeDependentViews.labels.pickerDependentsName"),
                };
                // Subscribe BEFORE SetTree — its end-of-setup SelectionChanged seeds the mirror list.
                picker.SelectionChanged += ids =>
                {
                    _selectedParentIds = ids.Select(id => new ElementId(id)).ToList();
                    OnValidationChanged();
                };
                picker.SetTree(_browserTree,
                    _parents.Select(p => p.Id.Value),
                    _selectedParentIds.Select(id => id.Value).ToList());
                return picker;
            }
        }

        // ── Step 2 — Title block ──────────────────────────────────────────────
        private FrameworkElement BuildS2()
        {
            var outer = new StackPanel();
            outer.Children.Add(SectionLabel(AppStrings.T("testing.placeDependentViews.labels.secTitleBlock")));

            var sel = new SingleSelect();
            sel.Items = _titleblockNames.Count > 0
                ? _titleblockNames
                : new List<string> { AppStrings.T("testing.placeDependentViews.labels.noTitleBlocks") };
            if (!string.IsNullOrEmpty(_selectedTitleblock)) sel.SelectedItem = _selectedTitleblock;
            sel.SelectionChanged += s => { _selectedTitleblock = s ?? ""; OnValidationChanged(); };
            outer.Children.Add(sel);
            return outer;
        }

        // ── Step 3 — Sheet naming ─────────────────────────────────────────────
        private FrameworkElement BuildS3()
        {
            var outer = new StackPanel();

            outer.Children.Add(SectionLabel(AppStrings.T("testing.placeDependentViews.labels.secStartNumber")));
            var numStepper = new InlineStepper
            {
                Value = _startingNumber, MinValue = 1, MaxValue = 99999,
                Step = 1, Decimals = 0, ValueWidth = 56,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            numStepper.ValueChanged += (s, v) => { _startingNumber = (int)v; OnValidationChanged(); };
            outer.Children.Add(numStepper);

            // ── Number prefix / suffix ────────────────────────────────────────
            var prefixField = new TextField
            {
                Label = AppStrings.T("testing.placeDependentViews.labels.lblNumberPrefix"), Placeholder = AppStrings.T("testing.placeDependentViews.labels.phNumberPrefix"),
                Text = _numberPrefix, Margin = new Thickness(0, 14, 0, 0),
            };
            prefixField.TextChanged += t => { _numberPrefix = t ?? ""; OnValidationChanged(); };
            outer.Children.Add(prefixField);

            var suffixField = new TextField
            {
                Label = AppStrings.T("testing.placeDependentViews.labels.lblNumberSuffix"), Placeholder = AppStrings.T("testing.placeDependentViews.labels.phNumberSuffix"),
                Text = _numberSuffix, Margin = new Thickness(0, 8, 0, 0),
            };
            suffixField.TextChanged += t => { _numberSuffix = t ?? ""; OnValidationChanged(); };
            outer.Children.Add(suffixField);

            // ── Naming pattern ────────────────────────────────────────────────
            var patLabel = SectionLabel(AppStrings.T("testing.placeDependentViews.labels.secNamingPattern"));
            patLabel.Margin = new Thickness(0, 14, 0, 4);
            outer.Children.Add(patLabel);

            var tokens = NamingTokenRegistry.TokensFor(TokenEntity.Sheet, hasSource: true);
            var tokenInput = new TokenInput(tokens, DefaultPattern) { Text = _namingPattern };
            tokenInput.TextChanged += (s, e) =>
            {
                _namingPattern = tokenInput.Text;
                NamingPatternStore.Instance.Set(ToolId, _namingPattern);
                OnValidationChanged();
            };
            outer.Children.Add(tokenInput);

            // ── Sheet series ──────────────────────────────────────────────────
            var seriesField = new TextField
            {
                Label = AppStrings.T("testing.placeDependentViews.labels.lblSeriesValue"), Placeholder = AppStrings.T("testing.placeDependentViews.labels.phSeriesValue"),
                Text = _sheetSeries, Margin = new Thickness(0, 14, 0, 0),
            };
            seriesField.TextChanged += t => { _sheetSeries = t ?? ""; OnValidationChanged(); };
            outer.Children.Add(seriesField);

            var seriesParamField = new TextField
            {
                Label = AppStrings.T("testing.placeDependentViews.labels.lblSeriesParam"), Placeholder = AppStrings.T("testing.placeDependentViews.labels.phSeriesParam"),
                Text = _seriesParamName, Margin = new Thickness(0, 8, 0, 0),
            };
            seriesParamField.TextChanged += t => { _seriesParamName = t ?? ""; OnValidationChanged(); };
            outer.Children.Add(seriesParamField);
            outer.Children.Add(Note(AppStrings.T("testing.placeDependentViews.labels.noteSeries")));

            // ── Preview ───────────────────────────────────────────────────────
            var prevLabel = SectionLabel(AppStrings.T("testing.placeDependentViews.labels.secPreview"));
            prevLabel.Margin = new Thickness(0, 14, 0, 4);
            outer.Children.Add(prevLabel);

            var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic };
            preview.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            preview.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            preview.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");

            var sample = _parents.FirstOrDefault();
            Action update = () =>
            {
                string fullNumber = _numberPrefix + _startingNumber.ToString() + _numberSuffix;
                var ctx = new TokenContext();
                ctx.Computed["ParentViewName"] = sample?.Name ?? AppStrings.T("testing.placeDependentViews.labels.previewSampleName");
                ctx.Computed["ViewType"]       = sample?.TypeLabel ?? "FloorPlan";
                ctx.Computed["Level"]          = sample?.LevelName ?? AppStrings.T("testing.placeDependentViews.labels.previewSampleLevel");
                ctx.Computed["SheetNumber"]    = fullNumber;
                string name = TokenResolver.Resolve(_namingPattern, ctx);
                preview.Text = $"{fullNumber}   |   {name}";
            };
            update();
            ValidationChanged += (s, e) => update();
            outer.Children.Add(preview);
            return outer;
        }

        // ── Step 4 — Layout ───────────────────────────────────────────────────
        private FrameworkElement BuildS4()
        {
            var outer = new StackPanel();

            // ── Placement mode ────────────────────────────────────────────────
            outer.Children.Add(SectionLabel(AppStrings.T("testing.placeDependentViews.labels.secPlacementMode")));
            var modeSelect = new SingleSelect();
            modeSelect.Items = new List<string> { ModeAccurate, ModeGrouped, ModeEstimate };
            modeSelect.SelectedItem = _layoutMode == LayoutMode.Estimate ? ModeEstimate
                                    : _layoutMode == LayoutMode.Grouped  ? ModeGrouped
                                    : ModeAccurate;
            modeSelect.SelectionChanged += s =>
            {
                _layoutMode = s == ModeEstimate ? LayoutMode.Estimate
                            : s == ModeGrouped  ? LayoutMode.Grouped
                            : LayoutMode.Measured;
                OnValidationChanged();
            };
            outer.Children.Add(modeSelect);
            outer.Children.Add(Note(AppStrings.T("testing.placeDependentViews.labels.notePlacement")));

            outer.Children.Add(Spaced(SeparatorLine(), 12));

            // Trim toggle
            var trim = new CheckBox
            {
                Content   = AppStrings.T("testing.placeDependentViews.labels.optTrim"),
                IsChecked = _trimBubbles,
                Margin    = new Thickness(0, 0, 0, 4),
            };
            trim.SetResourceReference(CheckBox.ForegroundProperty, "LemoineText");
            trim.SetResourceReference(CheckBox.FontFamilyProperty, "LemoineUiFont");
            trim.SetResourceReference(CheckBox.FontSizeProperty,   "LemoineFS_MD");
            trim.Checked   += (s, e) => { _trimBubbles = true;  OnValidationChanged(); };
            trim.Unchecked += (s, e) => { _trimBubbles = false; OnValidationChanged(); };
            outer.Children.Add(trim);

            outer.Children.Add(Note(AppStrings.T("testing.placeDependentViews.labels.noteTrim")));

            outer.Children.Add(Spaced(SectionLabel(AppStrings.T("testing.placeDependentViews.labels.secTrimDistance")), 14));
            outer.Children.Add(Stepper(_trimInches, 0.0, 5.0, 0.0625, 3, v => _trimInches = v));

            outer.Children.Add(Spaced(SectionLabel(AppStrings.T("testing.placeDependentViews.labels.secGap")), 14));
            outer.Children.Add(Stepper(_gapInches, 0.0, 12.0, 0.125, 3, v => _gapInches = v));

            outer.Children.Add(Spaced(SectionLabel(AppStrings.T("testing.placeDependentViews.labels.secMargins")), 14));
            var grid = new WpfGrid { Margin = new Thickness(0, 0, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int r = 0; r < 2; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddMarginCell(grid, 0, 0, AppStrings.T("testing.placeDependentViews.labels.marginTop"),    _marginTop,    v => _marginTop = v);
            AddMarginCell(grid, 0, 2, AppStrings.T("testing.placeDependentViews.labels.marginBottom"), _marginBottom, v => _marginBottom = v);
            AddMarginCell(grid, 1, 0, AppStrings.T("testing.placeDependentViews.labels.marginLeft"),   _marginLeft,   v => _marginLeft = v);
            AddMarginCell(grid, 1, 2, AppStrings.T("testing.placeDependentViews.labels.marginRight"),  _marginRight,  v => _marginRight = v);
            outer.Children.Add(grid);

            return outer;
        }

        private void AddMarginCell(WpfGrid grid, int row, int col, string label, double value, Action<double> set)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            var lbl = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 2) };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            sp.Children.Add(lbl);
            sp.Children.Add(Stepper(value, 0.0, 12.0, 0.125, 3, set));
            WpfGrid.SetRow(sp, row);
            WpfGrid.SetColumn(sp, col);
            grid.Children.Add(sp);
        }

        private InlineStepper Stepper(double value, double min, double max, double step, int decimals, Action<double> set)
        {
            var s = new InlineStepper
            {
                Value = value, MinValue = min, MaxValue = max, Step = step, Decimals = decimals,
                ValueWidth = 56, HorizontalAlignment = HorizontalAlignment.Left,
            };
            s.ValueChanged += (sender, v) => { set(v); OnValidationChanged(); };
            return s;
        }

        // ── IReviewableTool ────────────────────────────────────────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("mode",   AppStrings.T("testing.placeDependentViews.review.itemMode")),
            ("views",  AppStrings.T("testing.placeDependentViews.review.itemViews")),
            ("tb",     AppStrings.T("testing.placeDependentViews.review.itemTb")),
            ("naming", AppStrings.T("testing.placeDependentViews.review.itemNaming")),
            ("series", AppStrings.T("testing.placeDependentViews.review.itemSeries")),
            ("trim",   AppStrings.T("testing.placeDependentViews.review.itemTrim")),
            ("layout", AppStrings.T("testing.placeDependentViews.review.itemLayout")),
        };

        private List<ElementId> ActiveSelection => _compositeMode ? _selectedCompositeIds : _selectedParentIds;

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["mode"]   = _compositeMode ? ModeComposite : ModeDependents,
            ["views"]  = ActiveSelection.Count == 0
                ? AppStrings.T("testing.placeDependentViews.review.viewsNone")
                : AppStrings.T("testing.placeDependentViews.review.viewsValue", ActiveSelection.Count),
            ["tb"]     = string.IsNullOrEmpty(_selectedTitleblock) ? "—" : _selectedTitleblock,
            ["naming"] = AppStrings.T("testing.placeDependentViews.review.namingValue", _numberPrefix, _startingNumber, _numberSuffix, _namingPattern),
            ["series"] = string.IsNullOrWhiteSpace(_sheetSeries)
                ? "—"
                : AppStrings.T("testing.placeDependentViews.review.seriesValue", _sheetSeries, _seriesParamName),
            ["trim"]   = _trimBubbles ? AppStrings.T("testing.placeDependentViews.review.trimOn", _trimInches) : AppStrings.T("testing.placeDependentViews.review.trimOff"),
            ["layout"] = AppStrings.T("testing.placeDependentViews.review.layoutValue", LayoutLabel, _gapInches, _marginTop, _marginBottom, _marginLeft, _marginRight),
        };

        public IList<string>? ReviewChips => null;
        public string?        ReviewNote  => _compositeMode
            ? AppStrings.T("testing.placeDependentViews.review.noteComposite")
            : AppStrings.T("testing.placeDependentViews.review.noteDependents");
        public string?        ReviewWarning =>
            _trimBubbles ? AppStrings.T("testing.placeDependentViews.review.warning") : null;

        // ── Validation / summary ──────────────────────────────────────────────
        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return ActiveSelection.Count > 0;
                case "S2": return !string.IsNullOrEmpty(_selectedTitleblock) &&
                                  _titleblockMap.ContainsKey(_selectedTitleblock);
                case "S3": return !string.IsNullOrWhiteSpace(_namingPattern);
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return ActiveSelection.Count == 0
                    ? "—"
                    : AppStrings.T("testing.placeDependentViews.summaries.s1Value", ActiveSelection.Count, (_compositeMode ? AppStrings.T("testing.placeDependentViews.summaries.s1Composite") : AppStrings.T("testing.placeDependentViews.summaries.s1Dependents")));
                case "S2": return string.IsNullOrEmpty(_selectedTitleblock) ? "—" : _selectedTitleblock;
                case "S3": return AppStrings.T("testing.placeDependentViews.summaries.s3Value", _namingPattern, _startingNumber);
                case "S4": return AppStrings.T("testing.placeDependentViews.summaries.s4Value", LayoutLabel, (_trimBubbles ? AppStrings.T("testing.placeDependentViews.summaries.s4Trim", _trimInches) : AppStrings.T("testing.placeDependentViews.summaries.s4NoTrim")), _gapInches);
                case "S5": return AppStrings.T("testing.placeDependentViews.summaries.S5");
                default:   return "—";
            }
        }

        // ── Run ───────────────────────────────────────────────────────────────
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            if (_handler == null || _event == null) return;

            _handler.Mode            = _compositeMode
                                       ? PlaceViewsMode.CompositeOneSheet
                                       : PlaceViewsMode.DependentsPerParent;
            _handler.ParentViewIds   = new List<ElementId>(ActiveSelection);
            _handler.TitleBlockTypeId = _titleblockMap.TryGetValue(_selectedTitleblock, out var tbId)
                                        ? tbId : ElementId.InvalidElementId;
            _handler.StartingNumber  = _startingNumber;
            _handler.NamingPattern   = _namingPattern;
            _handler.NumberPrefix    = _numberPrefix;
            _handler.NumberSuffix    = _numberSuffix;
            _handler.SheetSeries     = _sheetSeries;
            _handler.SeriesParamName = _seriesParamName;
            _handler.Layout          = _layoutMode;
            _handler.TrimBubbles     = _trimBubbles;
            _handler.TrimInches      = _trimInches;
            _handler.MarginTopIn     = _marginTop;
            _handler.MarginBottomIn  = _marginBottom;
            _handler.MarginLeftIn    = _marginLeft;
            _handler.MarginRightIn   = _marginRight;
            _handler.GapIn           = _gapInches;
            _handler.PushLog         = pushLog;
            _handler.OnProgress      = onProgress;
            _handler.OnComplete      = onComplete;

            _event.Raise();
        }

        // ── Small UI helpers ──────────────────────────────────────────────────
        private static TextBlock SectionLabel(string text)
        {
            var t = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 4) };
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            t.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return t;
        }

        private static FrameworkElement Spaced(FrameworkElement el, double top)
        {
            el.Margin = new Thickness(0, top, 0, 4);
            return el;
        }

        private static Border SeparatorLine()
        {
            var b = new Border { Height = 1, BorderThickness = new Thickness(0) };
            b.SetResourceReference(Border.BackgroundProperty, "LemoineBorder");
            return b;
        }

        private static TextBlock Note(string text)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 0, 0, 0) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            t.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return t;
        }

        private static TextBlock Hint(string text)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            t.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return t;
        }
    }
}
