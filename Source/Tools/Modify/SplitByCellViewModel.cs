using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;

using WpfGrid = System.Windows.Controls.Grid;

namespace LemoineTools.Tools.ModifyElements
{
    public class SplitByCellViewModel : IStepFlowTool, IReviewableTool, IRunResult, IToolCleanup
    {
        // Self-describing result label for the run strip (see IRunResult).
        public string? ResultNoun => "pieces";
        public System.Collections.Generic.IReadOnlyList<LemoineTools.Framework.ResultChip>? ResultChips => null;

        public string Title    => AppStrings.T("modify.splitByCell.title");
        public string RunLabel => AppStrings.T("modify.splitByCell.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", AppStrings.T("modify.splitByCell.steps.S1"), required: true),
            new StepDefinition("S2", AppStrings.T("modify.splitByCell.steps.S2"),         required: true),
            new StepDefinition("S3", AppStrings.T("modify.splitByCell.steps.S3"),      required: false),
        };

        // Maps BuiltInCategory → display label; shared with SplitByCellCommand for counts + pre-selection.
        internal static readonly IReadOnlyDictionary<BuiltInCategory, string> CatLabels =
            new Dictionary<BuiltInCategory, string>
            {
                { BuiltInCategory.OST_Floors,               "Floors" },
                { BuiltInCategory.OST_Ceilings,             "Ceilings" },
                { BuiltInCategory.OST_Roofs,                "Roofs" },
                { BuiltInCategory.OST_StructuralFoundation, "Structural Foundations" },
                { BuiltInCategory.OST_FilledRegion,         "Filled Regions" },
            };

        // ── State ─────────────────────────────────────────────────────────────
        private List<string> _selectedCats     = new List<string>();
        private double       _cellX            = 10.0;
        private double       _cellY            = 10.0;
        private bool         _useProjectOrigin = false;

        private readonly IReadOnlyDictionary<string, int> _elementCounts;
        private readonly IReadOnlyList<ElementId>         _preSelectedIds;
        private readonly IReadOnlyList<string>            _preSelectedCats;

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly SplitByCellEventHandler _handler;
        private readonly ExternalEvent           _event;

        public event EventHandler? ValidationChanged;

        // Null the callbacks parked on the static handler so this VM isn't retained after close.
        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.OnLog    = null;
            _handler.OnProgress = null;
            _handler.OnComplete = null;
        }

        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public SplitByCellViewModel(
            SplitByCellEventHandler          handler,
            ExternalEvent                    externalEvent,
            IReadOnlyDictionary<string, int> elementCounts,
            IReadOnlyList<ElementId>         preSelectedIds,
            IReadOnlyList<string>            preSelectedCats)
        {
            _handler         = handler;
            _event           = externalEvent;
            _elementCounts   = elementCounts;
            _preSelectedIds  = preSelectedIds;
            _preSelectedCats = preSelectedCats;

            if (_preSelectedIds.Count > 0)
                _selectedCats = new List<string>(_preSelectedCats);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  GetStepContent
        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildS1();
                case "S2": return BuildS2();
                case "S3": return null; // framework renders review (IReviewableTool)
                default:   return null;
            }
        }

        private FrameworkElement BuildS1()
        {
            if (_preSelectedIds.Count > 0)
                return BuildS1_PreSelected();

            var outer = new StackPanel();

            // A — element count strip (scoped to active view since Cell always uses it)
            var parts = SplitByCellHelpers.SupportedCategories
                .Select(bic => CatLabels.TryGetValue(bic, out string? lbl) ? lbl : bic.ToString().Replace("OST_", ""))
                .Select(label => $"{label}: {(_elementCounts.TryGetValue(label, out int n) ? n : 0)}");
            var countStrip = new TextBlock
            {
                Text         = string.Join("  ·  ", parts),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 6),
            };
            countStrip.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            countStrip.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            countStrip.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            outer.Children.Add(countStrip);

            // Category picker
            var catLabels = SplitByCellHelpers.SupportedCategories
                .Select(bic => CatLabels.TryGetValue(bic, out string? lbl) ? lbl : bic.ToString().Replace("OST_", ""))
                .ToList();
            var groups = new Dictionary<string, List<string>> { { "Categories", catLabels } };
            var tabs = new MultiSelectTabs();
            tabs.SetGroups(groups);
            tabs.SelectionChanged += selected =>
            {
                _selectedCats = new List<string>(selected);
                OnValidationChanged();
            };
            outer.Children.Add(tabs);

            return outer;
        }

        private FrameworkElement BuildS1_PreSelected()
        {
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
            };
            card.SetResourceReference(Border.PaddingProperty,     "LemoineTh_CardPad");
            card.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var header = new TextBlock { Text = AppStrings.T("modify.splitByCell.labels.fromSelection"), Margin = new Thickness(0, 0, 0, 4) };
            header.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            header.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            header.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            int cnt  = _preSelectedIds.Count;
            int cats = _selectedCats.Count;
            var countLine = new TextBlock
            {
                Text         = AppStrings.T("modify.splitByCell.labels.preselCount", cnt, cats),
                FontWeight   = FontWeights.Medium,
                TextWrapping = TextWrapping.Wrap,
            };
            countLine.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            countLine.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            countLine.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var catLine = new TextBlock
            {
                Text         = string.Join("  ·  ", _selectedCats),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 2, 0, 6),
            };
            catLine.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            catLine.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            catLine.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var note = new TextBlock
            {
                Text         = AppStrings.T("modify.splitByCell.labels.preselNote"),
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
            };
            note.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            note.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            note.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var sp = new StackPanel();
            sp.Children.Add(header);
            sp.Children.Add(countLine);
            sp.Children.Add(catLine);
            sp.Children.Add(note);
            card.Child = sp;
            return card;
        }

        private FrameworkElement BuildS2()
        {
            var outer = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

            var sizeRange = new NumberRange
            {
                MinLabel = AppStrings.T("modify.splitByCell.labels.cellXLabel"),
                MaxLabel = AppStrings.T("modify.splitByCell.labels.cellYLabel"),
                AbsMin   = 0.1,
                AbsMax   = 1000,
                Step     = 0.5,
            };
            sizeRange.SetValues(_cellX, _cellY);
            sizeRange.RangeChanged += (min, max) =>
            {
                if (min.HasValue && min.Value > 0) _cellX = min.Value;
                if (max.HasValue && max.Value > 0) _cellY = max.Value;
                OnValidationChanged();
            };
            outer.Children.Add(sizeRange);

            var toggle = new ToggleSwitches();
            toggle.SetItems(new List<ToggleItem>
            {
                new ToggleItem
                {
                    Id        = "projOrigin",
                    Label     = AppStrings.T("modify.splitByCell.labels.originLabel"),
                    Desc      = AppStrings.T("modify.splitByCell.labels.originDesc"),
                    DefaultOn = false,
                },
            });
            toggle.StateChanged += state =>
            {
                _useProjectOrigin = state.TryGetValue("projOrigin", out bool v) && v;
                OnValidationChanged();
            };
            outer.Children.Add(toggle);

            return outer;
        }



        // ═════════════════════════════════════════════════════════════════════
        //  IsValid / SummaryFor / Run
        // ═════════════════════════════════════════════════════════════════════
        // ── IReviewableTool (P3) — framework renders the review step ───────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("cats",   AppStrings.T("modify.splitByCell.review.itemCats")),
            ("cell",   AppStrings.T("modify.splitByCell.review.itemCell")),
            ("origin", AppStrings.T("modify.splitByCell.review.itemOrigin")),
            ("scope",  AppStrings.T("modify.splitByCell.review.itemScope")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["cats"]   = _selectedCats.Count == 0 ? "—" : string.Join(", ", _selectedCats),
            ["cell"]   = AppStrings.T("modify.splitByCell.review.cellValue", _cellX, _cellY),
            ["origin"] = _useProjectOrigin ? AppStrings.T("modify.splitByCell.review.originProject") : AppStrings.T("modify.splitByCell.review.originBbox"),
            ["scope"]  = _preSelectedIds.Count > 0
                ? AppStrings.T("modify.splitByCell.review.scopeFromSel", _preSelectedIds.Count)
                : AppStrings.T("modify.splitByCell.review.scopeActive"),
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => AppStrings.T("modify.splitByCell.review.note");
        public string?        ReviewWarning => null;

        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _preSelectedIds.Count > 0 || _selectedCats.Count > 0;
            if (stepId == "S2") return _cellX > 0 && _cellY > 0;
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1")
            {
                if (_preSelectedIds.Count > 0) return AppStrings.T("modify.splitByCell.summaries.fromSelection", _preSelectedIds.Count);
                return _selectedCats.Count == 0 ? "—" : string.Join(", ", _selectedCats);
            }
            if (stepId == "S2")
                return AppStrings.T("modify.splitByCell.review.cellValue", _cellX, _cellY);
            if (stepId == "S3")
                return AppStrings.T("modify.splitByCell.summaries.S3");
            return "—";
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _handler.PreSelectedIds         = _preSelectedIds.Count > 0 ? new List<ElementId>(_preSelectedIds) : null;
            _handler.SelectedCategoryLabels = new List<string>(_selectedCats);
            _handler.CellX                  = _cellX;
            _handler.CellY                  = _cellY;
            _handler.UseProjectOrigin       = _useProjectOrigin;
            _handler.OnLog                  = pushLog;
            _handler.OnProgress             = onProgress;
            _handler.OnComplete             = onComplete;

            _event.Raise();
        }
    }
}
