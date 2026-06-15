using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

using WpfGrid = System.Windows.Controls.Grid;

namespace LemoineTools.Tools.Testing.PlaceDependentViews
{
    public sealed class PlaceDependentViewsViewModel : ILemoineTool, ILemoineReviewable, ILemoineToolCleanup
    {
        private static readonly (string Label, string Token)[] NamingTokens =
        {
            ("Parent View", "{ParentViewName}"),
            ("View Type",   "{ViewType}"),
            ("Level",       "{Level}"),
            ("Sheet Number","{SheetNumber}"),
        };

        // ── ILemoineTool ──────────────────────────────────────────────────────
        public string Title    => "Place Dependent Views";
        public string RunLabel => "Place on Sheets →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Views to Place", required: true),
            new StepDefinition("S2", "Title Block",    required: true),
            new StepDefinition("S3", "Sheet Naming",   required: true),
            new StepDefinition("S4", "Layout",         required: false),
            new StepDefinition("S5", "Review & Run",   required: false),
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

        // ── State ─────────────────────────────────────────────────────────────
        private readonly List<ParentViewEntry>          _parents;
        private readonly LemoineBrowserTree             _browserTree;
        private List<ElementId>                          _selectedParentIds = new List<ElementId>();

        private readonly List<string>                   _titleblockNames;
        private readonly Dictionary<string, ElementId>  _titleblockMap;
        private string _selectedTitleblock = "";

        private int    _startingNumber = 1;
        private string _namingPattern  = "{ParentViewName}";
        private string _numberPrefix    = "";
        private string _numberSuffix    = "";
        private string _sheetSeries     = "";
        private string _seriesParamName = "Sheet Series";

        private bool   _estimateMode  = false;
        private bool   _trimBubbles   = true;

        private const string ModeAccurate = "Accurate (measured)";
        private const string ModeEstimate = "Quick estimate (fast)";
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
            List<FamilySymbol>?              titleblocks,
            LemoineBrowserTree?              browserTree = null)
        {
            _handler     = handler;
            _event       = externalEvent;
            _browserTree = browserTree ?? new LemoineBrowserTree();

            _parents = (parents ?? new List<ParentViewEntry>())
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
        public PlaceDependentViewsViewModel() : this(null, null, null, null) { }

        // ── Content ───────────────────────────────────────────────────────────
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildS1();
                case "S2": return BuildS2();
                case "S3": return BuildS3();
                case "S4": return BuildS4();
                case "S5": return null; // framework renders ILemoineReviewable
                default:   return null;
            }
        }

        // ── Step 1 — Views to place ───────────────────────────────────────────
        private FrameworkElement BuildS1()
        {
            if (_parents.Count == 0)
                return Hint("No views with dependent views were found in this project. " +
                            "Create dependents first, then reopen this tool.");

            var picker = new LemoineBrowserTreePicker
            {
                Height         = 300,
                AccessibleName = "Views to place",
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

        // ── Step 2 — Title block ──────────────────────────────────────────────
        private FrameworkElement BuildS2()
        {
            var outer = new StackPanel();
            outer.Children.Add(SectionLabel("TITLE BLOCK"));

            var sel = new LemoineSingleSelect();
            sel.Items = _titleblockNames.Count > 0
                ? _titleblockNames
                : new List<string> { "(no title blocks found)" };
            if (!string.IsNullOrEmpty(_selectedTitleblock)) sel.SelectedItem = _selectedTitleblock;
            sel.SelectionChanged += s => { _selectedTitleblock = s ?? ""; OnValidationChanged(); };
            outer.Children.Add(sel);
            return outer;
        }

        // ── Step 3 — Sheet naming ─────────────────────────────────────────────
        private FrameworkElement BuildS3()
        {
            var outer = new StackPanel();

            outer.Children.Add(SectionLabel("STARTING SHEET NUMBER"));
            var numStepper = new LemoineInlineStepper
            {
                Value = _startingNumber, MinValue = 1, MaxValue = 99999,
                Step = 1, Decimals = 0, ValueWidth = 56,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            numStepper.ValueChanged += (s, v) => { _startingNumber = (int)v; OnValidationChanged(); };
            outer.Children.Add(numStepper);

            // ── Number prefix / suffix ────────────────────────────────────────
            var prefixField = new LemoineTextField
            {
                Label = "Number prefix", Placeholder = "e.g. A-",
                Text = _numberPrefix, Margin = new Thickness(0, 14, 0, 0),
            };
            prefixField.TextChanged += t => { _numberPrefix = t ?? ""; OnValidationChanged(); };
            outer.Children.Add(prefixField);

            var suffixField = new LemoineTextField
            {
                Label = "Number suffix", Placeholder = "e.g. .1",
                Text = _numberSuffix, Margin = new Thickness(0, 8, 0, 0),
            };
            suffixField.TextChanged += t => { _numberSuffix = t ?? ""; OnValidationChanged(); };
            outer.Children.Add(suffixField);

            // ── Naming pattern ────────────────────────────────────────────────
            var patLabel = SectionLabel("SHEET NAMING PATTERN");
            patLabel.Margin = new Thickness(0, 14, 0, 4);
            outer.Children.Add(patLabel);

            var tokenInput = new LemoineTokenInput(NamingTokens) { Text = _namingPattern };
            tokenInput.TextChanged += (s, e) => { _namingPattern = tokenInput.Text; OnValidationChanged(); };
            outer.Children.Add(tokenInput);

            // ── Sheet series ──────────────────────────────────────────────────
            var seriesField = new LemoineTextField
            {
                Label = "Sheet series value", Placeholder = "e.g. Architectural",
                Text = _sheetSeries, Margin = new Thickness(0, 14, 0, 0),
            };
            seriesField.TextChanged += t => { _sheetSeries = t ?? ""; OnValidationChanged(); };
            outer.Children.Add(seriesField);

            var seriesParamField = new LemoineTextField
            {
                Label = "Series parameter name", Placeholder = "Sheet Series",
                Text = _seriesParamName, Margin = new Thickness(0, 8, 0, 0),
            };
            seriesParamField.TextChanged += t => { _seriesParamName = t ?? ""; OnValidationChanged(); };
            outer.Children.Add(seriesParamField);
            outer.Children.Add(Note("Written to this text parameter on each created sheet (e.g. for Project " +
                                    "Browser grouping). Skipped with a warning if the parameter doesn't exist."));

            // ── Preview ───────────────────────────────────────────────────────
            var prevLabel = SectionLabel("PREVIEW");
            prevLabel.Margin = new Thickness(0, 14, 0, 4);
            outer.Children.Add(prevLabel);

            var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic };
            preview.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            preview.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            preview.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");

            var sample = _parents.FirstOrDefault();
            var placeholders = new Dictionary<string, string>
            {
                ["ParentViewName"] = sample?.Name ?? "Level 1 - Power",
                ["ViewType"]       = sample?.TypeLabel ?? "FloorPlan",
                ["Level"]          = sample?.LevelName ?? "Level 1",
                ["SheetNumber"]    = _startingNumber.ToString(),
            };
            Action update = () =>
            {
                string fullNumber = _numberPrefix + _startingNumber.ToString() + _numberSuffix;
                placeholders["SheetNumber"] = fullNumber;
                string name = LemoineTokenInput.Resolve(_namingPattern, placeholders);
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
            outer.Children.Add(SectionLabel("PLACEMENT MODE"));
            var modeSelect = new LemoineSingleSelect();
            modeSelect.Items = new List<string> { ModeAccurate, ModeEstimate };
            modeSelect.SelectedItem = _estimateMode ? ModeEstimate : ModeAccurate;
            modeSelect.SelectionChanged += s => { _estimateMode = s == ModeEstimate; OnValidationChanged(); };
            outer.Children.Add(modeSelect);
            outer.Children.Add(Note("Accurate measures every placed view (one regen per sheet, with progress). " +
                                    "Quick estimate sizes views from their crop boxes for a fast, approximate " +
                                    "layout — best for testing margins, gap and trim before a final accurate run."));

            outer.Children.Add(Spaced(SeparatorLine(), 12));

            // Trim toggle
            var trim = new CheckBox
            {
                Content   = "Trim bubbles to just past each view's crop before placing",
                IsChecked = _trimBubbles,
                Margin    = new Thickness(0, 0, 0, 4),
            };
            trim.SetResourceReference(CheckBox.ForegroundProperty, "LemoineText");
            trim.SetResourceReference(CheckBox.FontFamilyProperty, "LemoineUiFont");
            trim.SetResourceReference(CheckBox.FontSizeProperty,   "LemoineFS_MD");
            trim.Checked   += (s, e) => { _trimBubbles = true;  OnValidationChanged(); };
            trim.Unchecked += (s, e) => { _trimBubbles = false; OnValidationChanged(); };
            outer.Children.Add(trim);

            outer.Children.Add(Note("Permanently shrinks each dependent's annotation crop — grid bubbles, " +
                                    "tags and section heads are clipped to the chosen distance past the crop."));

            outer.Children.Add(Spaced(SectionLabel("TRIM DISTANCE (inches on sheet)"), 14));
            outer.Children.Add(Stepper(_trimInches, 0.0, 5.0, 0.0625, 3, v => _trimInches = v));

            outer.Children.Add(Spaced(SectionLabel("GAP BETWEEN VIEWS (inches on sheet)"), 14));
            outer.Children.Add(Stepper(_gapInches, 0.0, 12.0, 0.125, 3, v => _gapInches = v));

            outer.Children.Add(Spaced(SectionLabel("SHEET MARGINS (inches)"), 14));
            var grid = new WpfGrid { Margin = new Thickness(0, 0, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int r = 0; r < 2; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddMarginCell(grid, 0, 0, "Top",    _marginTop,    v => _marginTop = v);
            AddMarginCell(grid, 0, 2, "Bottom", _marginBottom, v => _marginBottom = v);
            AddMarginCell(grid, 1, 0, "Left",   _marginLeft,   v => _marginLeft = v);
            AddMarginCell(grid, 1, 2, "Right",  _marginRight,  v => _marginRight = v);
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

        private LemoineInlineStepper Stepper(double value, double min, double max, double step, int decimals, Action<double> set)
        {
            var s = new LemoineInlineStepper
            {
                Value = value, MinValue = min, MaxValue = max, Step = step, Decimals = decimals,
                ValueWidth = 56, HorizontalAlignment = HorizontalAlignment.Left,
            };
            s.ValueChanged += (sender, v) => { set(v); OnValidationChanged(); };
            return s;
        }

        // ── ILemoineReviewable ────────────────────────────────────────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("views",  "Views"),
            ("tb",     "Title Block"),
            ("naming", "Naming / Numbering"),
            ("series", "Sheet Series"),
            ("trim",   "Bubble Trim"),
            ("layout", "Layout"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["views"]  = _selectedParentIds.Count == 0
                ? "— (none selected)"
                : $"{_selectedParentIds.Count} view(s) → {_selectedParentIds.Count} sheet(s)",
            ["tb"]     = string.IsNullOrEmpty(_selectedTitleblock) ? "—" : _selectedTitleblock,
            ["naming"] = $"No.: {_numberPrefix}{_startingNumber}{_numberSuffix} (+1…)  |  Name: {_namingPattern}",
            ["series"] = string.IsNullOrWhiteSpace(_sheetSeries)
                ? "—"
                : $"\"{_sheetSeries}\" → param '{_seriesParamName}'",
            ["trim"]   = _trimBubbles ? $"On — {_trimInches:0.###}\" past crop (permanent)" : "Off",
            ["layout"] = $"{(_estimateMode ? "Quick estimate" : "Accurate")}  |  auto best-fit  |  " +
                         $"gap {_gapInches:0.###}\"  |  margins " +
                         $"T{_marginTop:0.##} B{_marginBottom:0.##} L{_marginLeft:0.##} R{_marginRight:0.##}\"",
        };

        public IList<string>? ReviewChips => null;
        public string?        ReviewNote  =>
            "Creates one sheet per selected view and places that view's dependents, packed without overlap.";
        public string?        ReviewWarning =>
            _trimBubbles ? "Trimming permanently changes the dependent views' annotation crop." : null;

        // ── Validation / summary ──────────────────────────────────────────────
        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedParentIds.Count > 0;
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
                case "S1": return _selectedParentIds.Count == 0 ? "—" : $"{_selectedParentIds.Count} selected";
                case "S2": return string.IsNullOrEmpty(_selectedTitleblock) ? "—" : _selectedTitleblock;
                case "S3": return $"{_namingPattern}  |  start {_startingNumber}";
                case "S4": return $"{(_estimateMode ? "Estimate" : "Accurate")}  |  " +
                                  (_trimBubbles ? $"trim {_trimInches:0.###}\"" : "no trim") +
                                  $"  |  gap {_gapInches:0.###}\"";
                case "S5": return "Ready to run";
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

            _handler.ParentViewIds   = new List<ElementId>(_selectedParentIds);
            _handler.TitleBlockTypeId = _titleblockMap.TryGetValue(_selectedTitleblock, out var tbId)
                                        ? tbId : ElementId.InvalidElementId;
            _handler.StartingNumber  = _startingNumber;
            _handler.NamingPattern   = _namingPattern;
            _handler.NumberPrefix    = _numberPrefix;
            _handler.NumberSuffix    = _numberSuffix;
            _handler.SheetSeries     = _sheetSeries;
            _handler.SeriesParamName = _seriesParamName;
            _handler.EstimateMode    = _estimateMode;
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
