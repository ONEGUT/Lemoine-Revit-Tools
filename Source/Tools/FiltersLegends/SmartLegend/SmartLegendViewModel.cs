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

namespace LemoineTools.Tools.FiltersLegends.SmartLegend
{
    /// <summary>
    /// Smart Legend — pick sheets, and the tool reads the views on each one, works out which
    /// filters actually colour something there, and builds a legend per sheet from the
    /// existing Auto Filters rules.
    ///
    /// The generated legend is registered in the Legend Creator library, so it can be opened,
    /// edited and re-run there afterwards.
    /// </summary>
    public class SmartLegendViewModel : IStepFlowTool, IToolCleanup, IRunResult
    {
        public string Title    => AppStrings.T("filtersLegends.smartLegend.title");
        public string RunLabel => AppStrings.T("filtersLegends.smartLegend.runLabel");

        public string? ResultNoun => AppStrings.T("filtersLegends.smartLegend.resultNoun");
        public IReadOnlyList<ResultChip>? ResultChips => null;

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("sheets",  AppStrings.T("filtersLegends.smartLegend.steps.sheets"),  required: true),
            new StepDefinition("content", AppStrings.T("filtersLegends.smartLegend.steps.content"), required: false),
            new StepDefinition("run",     AppStrings.T("filtersLegends.smartLegend.steps.run"),     required: false),
        };

        // ── Data captured on the Revit main thread ──────────────────────────
        public sealed class SheetEntry
        {
            public ElementId Id     { get; set; } = ElementId.InvalidElementId;
            public string    Number { get; set; } = "";
            public string    Name   { get; set; } = "";
        }

        private readonly List<SheetEntry> _sheets;
        private readonly BrowserTree      _browserTree;
        private readonly List<(ElementId Id, string Name)> _textTypes;

        // ── State ────────────────────────────────────────────────────────────
        private readonly HashSet<long> _selectedSheetIds = new HashSet<long>();

        private string _namePattern  = DefaultNamePattern;
        private string _titlePattern = DefaultTitlePattern;
        private bool   _groupByTrade     = true;
        private bool   _includeUnmatched = true;
        private bool   _placeOnSheet     = true;
        private int    _groupsPerRow = 4;
        private int    _viewScale    = 48;
        private SmartLegendCorner _corner = SmartLegendCorner.TopRight;

        private string _titleTypeName  = "";
        private string _headerTypeName = "";
        private string _labelTypeName  = "";

        private const string ToolIdName  = "SmartLegendName";
        private const string ToolIdTitle = "SmartLegendTitle";
        private const string DefaultNamePattern  = "{SheetNumber} - LEGEND";
        private const string DefaultTitlePattern = "LEGEND";

        private readonly SmartLegendRunHandler? _runHandler;
        private readonly ExternalEvent?         _runEvent;

        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public SmartLegendViewModel(
            SmartLegendRunHandler? runHandler, ExternalEvent? runEvent,
            List<SheetEntry>? sheets,
            List<(ElementId Id, string Name)>? textTypes,
            BrowserTree? browserTree)
        {
            _runHandler  = runHandler;
            _runEvent    = runEvent;
            _sheets      = sheets      ?? new List<SheetEntry>();
            _textTypes   = textTypes   ?? new List<(ElementId, string)>();
            _browserTree = browserTree ?? new BrowserTree();

            _namePattern  = NamingPatternStore.Instance.GetOrDefault(ToolIdName,  DefaultNamePattern);
            _titlePattern = NamingPatternStore.Instance.GetOrDefault(ToolIdTitle, DefaultTitlePattern);
        }

        // Callbacks parked on the session-long static handler must be released, or this
        // ViewModel (and the step content it holds) stays rooted after the window closes.
        public void OnWindowClosed()
        {
            if (_runHandler != null)
            {
                _runHandler.PushLog    = null;
                _runHandler.OnProgress = null;
                _runHandler.OnComplete = null;
            }

            // The legends this tool generated were added to the project's legend library, which
            // lives in the .rvt so it travels with the model. The Legend Creator writes it on
            // close; this tool must do the same or its legends would exist only in this user's
            // %AppData% and never reach the rest of the team.
            try
            {
                LemoineTools.Framework.Project.ProjectLibraries.Save(
                    LemoineTools.Framework.Project.ProjectLibraryStore.SectionLegends,
                    LemoineTools.Tools.FiltersLegends.LegendCreator.LegendCreatorSettings
                        .SerializeProjectLibrary());
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("SmartLegend: save project legend library", ex);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Step content
        // ─────────────────────────────────────────────────────────────────────
        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "sheets")  return BuildSheetPicker();
            if (stepId == "content") return BuildContentOptions();
            return null;   // "run" is the framework's run step
        }

        private FrameworkElement BuildSheetPicker()
        {
            var outer = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            if (_sheets.Count == 0)
            {
                outer.Children.Add(Hint(AppStrings.T("filtersLegends.smartLegend.labels.noSheets")));
                return outer;
            }

            var picker = new BrowserTreePicker
            {
                Height         = 300,
                AccessibleName = AppStrings.T("filtersLegends.smartLegend.labels.sheetPicker"),
            };
            // Subscribe BEFORE SetTree — the single SelectionChanged fired at the end of
            // SetTree is what seeds the mirror set on a step rebuild.
            picker.SelectionChanged += ids =>
            {
                _selectedSheetIds.Clear();
                foreach (long id in ids) _selectedSheetIds.Add(id);
                OnValidationChanged();
            };
            picker.SetTree(_browserTree,
                _sheets.Select(s => s.Id.Value),
                _selectedSheetIds.ToList());

            outer.Children.Add(picker);
            outer.Children.Add(Hint(AppStrings.T("filtersLegends.smartLegend.labels.sheetHint")));
            return outer;
        }

        private FrameworkElement BuildContentOptions()
        {
            var outer = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            // ── Naming ────────────────────────────────────────────────────
            outer.Children.Add(SectionHeader(AppStrings.T("filtersLegends.smartLegend.labels.namingHeader")));

            var sheetTokens = NamingTokenRegistry.TokensFor(TokenEntity.Sheet, hasSource: false);

            var nameInput = new TokenInput(sheetTokens, DefaultNamePattern) { Text = _namePattern };
            nameInput.TextChanged += (s, e) =>
            {
                _namePattern = nameInput.Text;
                NamingPatternStore.Instance.Set(ToolIdName, _namePattern);
                OnValidationChanged();
            };
            outer.Children.Add(Labelled(AppStrings.T("filtersLegends.smartLegend.labels.viewName"), nameInput));

            var titleInput = new TokenInput(sheetTokens, DefaultTitlePattern) { Text = _titlePattern };
            titleInput.TextChanged += (s, e) =>
            {
                _titlePattern = titleInput.Text;
                NamingPatternStore.Instance.Set(ToolIdTitle, _titlePattern);
                OnValidationChanged();
            };
            outer.Children.Add(Labelled(AppStrings.T("filtersLegends.smartLegend.labels.legendTitle"), titleInput));

            // ── Content toggles ───────────────────────────────────────────
            outer.Children.Add(SectionHeader(AppStrings.T("filtersLegends.smartLegend.labels.contentHeader")));

            var toggles = new ToggleSwitches();
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem
                {
                    Id = "group", DefaultOn = _groupByTrade,
                    Label = AppStrings.T("filtersLegends.smartLegend.labels.groupByTrade"),
                    Desc  = AppStrings.T("filtersLegends.smartLegend.labels.groupByTradeDesc"),
                },
                new ToggleItem
                {
                    Id = "unmatched", DefaultOn = _includeUnmatched,
                    Label = AppStrings.T("filtersLegends.smartLegend.labels.includeUnmatched"),
                    Desc  = AppStrings.T("filtersLegends.smartLegend.labels.includeUnmatchedDesc"),
                },
                new ToggleItem
                {
                    Id = "place", DefaultOn = _placeOnSheet,
                    Label = AppStrings.T("filtersLegends.smartLegend.labels.placeOnSheet"),
                    Desc  = AppStrings.T("filtersLegends.smartLegend.labels.placeOnSheetDesc"),
                },
            });
            toggles.StateChanged += state =>
            {
                if (state.TryGetValue("group",     out bool g)) _groupByTrade     = g;
                if (state.TryGetValue("unmatched", out bool u)) _includeUnmatched = u;
                if (state.TryGetValue("place",     out bool p)) _placeOnSheet     = p;
                OnValidationChanged();
            };
            outer.Children.Add(toggles);

            // ── Layout ────────────────────────────────────────────────────
            outer.Children.Add(SectionHeader(AppStrings.T("filtersLegends.smartLegend.labels.layoutHeader")));

            var perRow = new InlineStepper
            {
                Value = _groupsPerRow, MinValue = 1, MaxValue = 12, Step = 1,
                Decimals = 0, ValueWidth = 48, HorizontalAlignment = HorizontalAlignment.Left,
            };
            perRow.ValueChanged += (s, v) => { _groupsPerRow = (int)v; OnValidationChanged(); };
            outer.Children.Add(Labelled(AppStrings.T("filtersLegends.smartLegend.labels.groupsPerRow"), perRow));

            // Items= selects index 0 and fires SelectionChanged, so seed the value first and
            // subscribe afterwards — otherwise opening the step silently rewrites the setting.
            var scale = new SingleSelect { Label = AppStrings.T("filtersLegends.smartLegend.labels.viewScale") };
            scale.Items        = ScaleLadder.Select(x => x.Label).ToList();
            scale.SelectedItem = ScaleLabelFor(_viewScale);
            scale.SelectionChanged += label =>
            {
                var hit = ScaleLadder.FirstOrDefault(x => x.Label == label);
                if (hit.Label != null) _viewScale = hit.Denom;
                OnValidationChanged();
            };
            outer.Children.Add(Wrap(scale));

            var corner = new SingleSelect { Label = AppStrings.T("filtersLegends.smartLegend.labels.corner") };
            corner.Items        = Corners.Select(c => c.Label).ToList();
            corner.SelectedItem = Corners.First(c => c.Value == _corner).Label;
            corner.SelectionChanged += label =>
            {
                var hit = Corners.FirstOrDefault(c => c.Label == label);
                if (hit.Label != null) _corner = hit.Value;
                OnValidationChanged();
            };
            outer.Children.Add(Wrap(corner));

            // ── Text styles ───────────────────────────────────────────────
            if (_textTypes.Count > 0)
            {
                outer.Children.Add(SectionHeader(AppStrings.T("filtersLegends.smartLegend.labels.textHeader")));
                outer.Children.Add(TextTypeRow(
                    AppStrings.T("filtersLegends.smartLegend.labels.textTitle"),
                    _titleTypeName, v => _titleTypeName = v));
                outer.Children.Add(TextTypeRow(
                    AppStrings.T("filtersLegends.smartLegend.labels.textHeaderStyle"),
                    _headerTypeName, v => _headerTypeName = v));
                outer.Children.Add(TextTypeRow(
                    AppStrings.T("filtersLegends.smartLegend.labels.textLabel"),
                    _labelTypeName, v => _labelTypeName = v));
            }

            return outer;
        }

        private FrameworkElement TextTypeRow(string label, string current, Action<string> onChanged)
        {
            var names = new List<string> { AppStrings.T("filtersLegends.smartLegend.labels.projectDefault") };
            names.AddRange(_textTypes.Select(t => t.Name));

            var sel = new SingleSelect { Label = label };
            sel.Items        = names;
            sel.SelectedItem = string.IsNullOrEmpty(current) ? names[0] : current;
            sel.SelectionChanged += v => onChanged(v == names[0] ? "" : v ?? "");
            return Wrap(sel);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Validation + summaries
        // ─────────────────────────────────────────────────────────────────────
        public bool IsValid(string stepId)
        {
            if (stepId == "sheets") return _selectedSheetIds.Count > 0;
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "sheets")
                return _selectedSheetIds.Count == 0
                    ? AppStrings.T("filtersLegends.smartLegend.summary.noSheets")
                    : AppStrings.T("filtersLegends.smartLegend.summary.sheets", _selectedSheetIds.Count);

            if (stepId == "content")
                return AppStrings.T("filtersLegends.smartLegend.summary.content",
                    _groupByTrade
                        ? AppStrings.T("filtersLegends.smartLegend.summary.grouped")
                        : AppStrings.T("filtersLegends.smartLegend.summary.flat"),
                    _placeOnSheet
                        ? AppStrings.T("filtersLegends.smartLegend.summary.placed")
                        : AppStrings.T("filtersLegends.smartLegend.summary.notPlaced"));

            return "";
        }

        // ─────────────────────────────────────────────────────────────────────
        // Run
        // ─────────────────────────────────────────────────────────────────────
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            if (_runHandler == null || _runEvent == null)
            {
                pushLog(AppStrings.T("filtersLegends.smartLegend.log.notInitialized"), "fail");
                onComplete(0, 1, 0);
                return;
            }

            _runHandler.SheetIds = _sheets
                .Where(s => _selectedSheetIds.Contains(s.Id.Value))
                .Select(s => s.Id)
                .ToList();

            _runHandler.NamePattern      = _namePattern;
            _runHandler.TitlePattern     = _titlePattern;
            _runHandler.GroupByTrade     = _groupByTrade;
            _runHandler.IncludeUnmatched = _includeUnmatched;
            _runHandler.PlaceOnSheet     = _placeOnSheet;
            _runHandler.GroupsPerRow     = _groupsPerRow;
            _runHandler.ViewScale        = _viewScale;
            _runHandler.Corner           = _corner;

            _runHandler.TitleTypeId       = ResolveTextTypeId(_titleTypeName);
            _runHandler.GroupHeaderTypeId = ResolveTextTypeId(_headerTypeName);
            _runHandler.LabelTypeId       = ResolveTextTypeId(_labelTypeName);

            _runHandler.PushLog    = pushLog;
            _runHandler.OnProgress = onProgress;
            _runHandler.OnComplete = onComplete;

            _runEvent.Raise();
        }

        // A style name persists across projects; an ElementId does not. Resolve against the
        // types captured from THIS document and fall back to the project default.
        private ElementId? ResolveTextTypeId(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var (id, n) in _textTypes)
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return id;
            return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Small UI helpers
        // ─────────────────────────────────────────────────────────────────────
        private static readonly (string Label, int Denom)[] ScaleLadder =
        {
            ("1/16\" = 1'-0\"", 192), ("3/32\" = 1'-0\"", 128), ("1/8\" = 1'-0\"", 96),
            ("3/16\" = 1'-0\"", 64),  ("1/4\" = 1'-0\"",  48),  ("3/8\" = 1'-0\"",  32),
            ("1/2\" = 1'-0\"",  24),  ("3/4\" = 1'-0\"",  16),  ("1\" = 1'-0\"",    12),
        };

        private static string ScaleLabelFor(int denom)
        {
            foreach (var (label, d) in ScaleLadder) if (d == denom) return label;
            return ScaleLadder[4].Label;   // 1/4" = 1'-0"
        }

        private static (string Label, SmartLegendCorner Value)[] Corners => new[]
        {
            (AppStrings.T("filtersLegends.smartLegend.labels.cornerTopRight"),    SmartLegendCorner.TopRight),
            (AppStrings.T("filtersLegends.smartLegend.labels.cornerTopLeft"),     SmartLegendCorner.TopLeft),
            (AppStrings.T("filtersLegends.smartLegend.labels.cornerBottomRight"), SmartLegendCorner.BottomRight),
            (AppStrings.T("filtersLegends.smartLegend.labels.cornerBottomLeft"),  SmartLegendCorner.BottomLeft),
        };

        private static TextBlock SectionHeader(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 10, 0, 4) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            return tb;
        }

        private static TextBlock Hint(string text)
        {
            var tb = new TextBlock
            {
                Text         = text,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 6, 0, 0),
                FontStyle    = FontStyles.Italic,
            };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            return tb;
        }

        // SingleSelect renders its own label, so it only needs spacing.
        private static FrameworkElement Wrap(FrameworkElement input)
        {
            input.Margin = new Thickness(0, 4, 0, 0);
            return input;
        }

        private static FrameworkElement Labelled(string label, FrameworkElement input)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            var tb = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 3) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            panel.Children.Add(tb);
            panel.Children.Add(input);
            return panel;
        }
    }
}
