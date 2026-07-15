using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Naming;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.Sheets.PlaceDependentViews
{
    /// <summary>Web port of <see cref="PlaceDependentViewsViewModel"/> — one sheet per parent
    /// view (or one composite sheet), dependents packed on it. Same handler, naming pattern
    /// store, and AppStrings keys; the mode swap rebuilds S1 via IWebStepRefresh.</summary>
    public sealed class PlaceDependentViewsWebTool : WebToolBase, IWebToolCleanup, IWebStepRefresh
    {
        private const string ToolId         = "sheets.placeDependent";
        private const string DefaultPattern = "{ParentViewName}";

        private const string ModeTokenDependents = "dependents";
        private const string ModeTokenComposite  = "composite";
        private const string LayoutTokenAccurate = "measured";
        private const string LayoutTokenGrouped  = "grouped";
        private const string LayoutTokenEstimate = "estimate";

        private readonly PlaceDependentViewsEventHandler? _handler;
        private readonly ExternalEvent?                   _event;
        private readonly BrowserTree                      _browserTree;

        private bool _compositeMode = false;
        private readonly List<ParentViewEntry> _parents;
        private List<ElementId> _selectedParentIds = new List<ElementId>();
        private readonly List<ParentViewEntry> _compositeCandidates;
        private List<ElementId> _selectedCompositeIds = new List<ElementId>();

        private readonly List<string>                  _titleblockNames;
        private readonly Dictionary<string, ElementId> _titleblockMap;
        private string _selectedTitleblock = "";

        private int    _startingNumber = 1;
        private string _namingPattern  = NamingPatternStore.Instance.GetOrDefault(ToolId, DefaultPattern);
        private string _numberPrefix    = "";
        private string _numberSuffix    = "";
        private string _sheetSeries     = "";
        private string _seriesParamName = "Sheet Series";

        private LayoutMode _layoutMode = LayoutMode.Measured;
        private bool   _trimBubbles  = true;
        private double _trimInches   = 0.125;
        private double _marginTop    = 0.5;
        private double _marginBottom = 0.5;
        private double _marginLeft   = 0.5;
        private double _marginRight  = 0.5;
        private double _gapInches    = 0.25;

        public event Action<string>? StepInputsChanged;

        public PlaceDependentViewsWebTool(
            PlaceDependentViewsEventHandler? handler,
            ExternalEvent?                   externalEvent,
            List<ParentViewEntry>?           parents,
            List<ParentViewEntry>?           compositeCandidates,
            List<FamilySymbol>?              titleblocks,
            BrowserTree?                     browserTree = null)
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

        public override string Title    => AppStrings.T("testing.placeDependentViews.title");
        public override string RunLabel => AppStrings.T("testing.placeDependentViews.runLabel");

        private string ModeLabel(bool composite) => composite
            ? AppStrings.T("testing.placeDependentViews.modeComposite")
            : AppStrings.T("testing.placeDependentViews.modeDependents");

        private string LayoutLabel => _layoutMode == LayoutMode.Estimate ? AppStrings.T("testing.placeDependentViews.layoutLabel.estimate")
                                    : _layoutMode == LayoutMode.Grouped  ? AppStrings.T("testing.placeDependentViews.layoutLabel.grouped")
                                    : AppStrings.T("testing.placeDependentViews.layoutLabel.accurate");

        private List<ElementId> ActiveSelection => _compositeMode ? _selectedCompositeIds : _selectedParentIds;

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            // S1 — mode + views to place (picker restricted to the active mode's candidates)
            var s1 = new WebStep("S1", AppStrings.T("testing.placeDependentViews.steps.S1"))
                .Add(WebInput.SingleSelect("mode", AppStrings.T("testing.placeDependentViews.labels.secMode"),
                    _compositeMode ? ModeTokenComposite : ModeTokenDependents, new[]
                    {
                        new WebOption(ModeTokenDependents, ModeLabel(false)),
                        new WebOption(ModeTokenComposite,  ModeLabel(true)),
                    }))
                .Add(WebInput.Hint("modeNote", _compositeMode
                    ? AppStrings.T("testing.placeDependentViews.labels.modeNoteComposite")
                    : AppStrings.T("testing.placeDependentViews.labels.modeNoteDependents")));

            if (_compositeMode)
            {
                if (_compositeCandidates.Count == 0)
                    s1.Add(WebInput.Hint("noComposite", AppStrings.T("testing.placeDependentViews.labels.hintNoComposite")));
                else
                    s1.Add(WebInput.BrowserTree("compositeViews",
                        AppStrings.T("testing.placeDependentViews.labels.pickerCompositeName"),
                        PruneTree(_browserTree, new HashSet<long>(_compositeCandidates.Select(p => p.Id.Value))),
                        _selectedCompositeIds.Select(id => id.Value)));
            }
            else
            {
                if (_parents.Count == 0)
                    s1.Add(WebInput.Hint("noParents", AppStrings.T("testing.placeDependentViews.labels.hintNoParents")));
                else
                    s1.Add(WebInput.BrowserTree("parentViews",
                        AppStrings.T("testing.placeDependentViews.labels.pickerDependentsName"),
                        PruneTree(_browserTree, new HashSet<long>(_parents.Select(p => p.Id.Value))),
                        _selectedParentIds.Select(id => id.Value)));
            }

            // S2 — title block
            var s2 = new WebStep("S2", AppStrings.T("testing.placeDependentViews.steps.S2"));
            if (_titleblockNames.Count == 0)
                s2.Add(WebInput.Hint("noTb", AppStrings.T("testing.placeDependentViews.labels.noTitleBlocks")));
            else
                s2.Add(WebInput.SingleSelect("titleblock", AppStrings.T("testing.placeDependentViews.labels.secTitleBlock"),
                    _selectedTitleblock, _titleblockNames.Select(n => new WebOption(n, n))));

            // S3 — sheet naming
            var tokens = NamingTokenRegistry.TokensFor(TokenEntity.Sheet, hasSource: true);
            var sample = _parents.FirstOrDefault();
            var ctx = new TokenContext();
            ctx.Computed["ParentViewName"] = sample?.Name ?? AppStrings.T("testing.placeDependentViews.labels.previewSampleName");
            ctx.Computed["ViewType"]       = sample?.TypeLabel ?? "FloorPlan";
            ctx.Computed["Level"]          = sample?.LevelName ?? AppStrings.T("testing.placeDependentViews.labels.previewSampleLevel");
            ctx.Computed["SheetNumber"]    = _numberPrefix + _startingNumber.ToString() + _numberSuffix;

            var s3 = new WebStep("S3", AppStrings.T("testing.placeDependentViews.steps.S3"))
                .Add(WebInput.Stepper("startNumber", AppStrings.T("testing.placeDependentViews.labels.secStartNumber"),
                    _startingNumber, 1, 99999, 1, 0))
                .Add(WebInput.TextField("numberPrefix", AppStrings.T("testing.placeDependentViews.labels.lblNumberPrefix"),
                    _numberPrefix, AppStrings.T("testing.placeDependentViews.labels.phNumberPrefix")))
                .Add(WebInput.TextField("numberSuffix", AppStrings.T("testing.placeDependentViews.labels.lblNumberSuffix"),
                    _numberSuffix, AppStrings.T("testing.placeDependentViews.labels.phNumberSuffix")))
                .Add(WebInput.TokenInput("pattern", AppStrings.T("testing.placeDependentViews.labels.secNamingPattern"),
                    _namingPattern, DefaultPattern, tokens, SampleFor(tokens, ctx)))
                .Add(WebInput.TextField("series", AppStrings.T("testing.placeDependentViews.labels.lblSeriesValue"),
                    _sheetSeries, AppStrings.T("testing.placeDependentViews.labels.phSeriesValue")))
                .Add(WebInput.TextField("seriesParam", AppStrings.T("testing.placeDependentViews.labels.lblSeriesParam"),
                    _seriesParamName, AppStrings.T("testing.placeDependentViews.labels.phSeriesParam")))
                .Add(WebInput.Hint("noteSeries", AppStrings.T("testing.placeDependentViews.labels.noteSeries")));

            // S4 — layout
            var s4 = new WebStep("S4", AppStrings.T("testing.placeDependentViews.steps.S4"), required: false)
                .Add(WebInput.SingleSelect("layoutMode", AppStrings.T("testing.placeDependentViews.labels.secPlacementMode"),
                    _layoutMode == LayoutMode.Estimate ? LayoutTokenEstimate
                        : _layoutMode == LayoutMode.Grouped ? LayoutTokenGrouped : LayoutTokenAccurate,
                    new[]
                    {
                        new WebOption(LayoutTokenAccurate, AppStrings.T("testing.placeDependentViews.modeAccurate")),
                        new WebOption(LayoutTokenGrouped,  AppStrings.T("testing.placeDependentViews.modeGrouped")),
                        new WebOption(LayoutTokenEstimate, AppStrings.T("testing.placeDependentViews.modeEstimate")),
                    }))
                .Add(WebInput.Hint("notePlacement", AppStrings.T("testing.placeDependentViews.labels.notePlacement")))
                .Add(WebInput.Toggle("trim", AppStrings.T("testing.placeDependentViews.labels.optTrim"), _trimBubbles))
                .Add(WebInput.Hint("noteTrim", AppStrings.T("testing.placeDependentViews.labels.noteTrim")))
                .Add(WebInput.Stepper("trimDist", AppStrings.T("testing.placeDependentViews.labels.secTrimDistance"),
                    _trimInches, 0.0, 5.0, 0.0625, 3))
                .Add(WebInput.Stepper("gap", AppStrings.T("testing.placeDependentViews.labels.secGap"),
                    _gapInches, 0.0, 12.0, 0.125, 3))
                .Add(WebInput.Hint("secMargins", AppStrings.T("testing.placeDependentViews.labels.secMargins")))
                .Add(WebInput.Stepper("marginTop",    AppStrings.T("testing.placeDependentViews.labels.marginTop"),    _marginTop,    0.0, 12.0, 0.125, 3))
                .Add(WebInput.Stepper("marginBottom", AppStrings.T("testing.placeDependentViews.labels.marginBottom"), _marginBottom, 0.0, 12.0, 0.125, 3))
                .Add(WebInput.Stepper("marginLeft",   AppStrings.T("testing.placeDependentViews.labels.marginLeft"),   _marginLeft,   0.0, 12.0, 0.125, 3))
                .Add(WebInput.Stepper("marginRight",  AppStrings.T("testing.placeDependentViews.labels.marginRight"),  _marginRight,  0.0, 12.0, 0.125, 3));

            // S5 — review
            var s5 = new WebStep("S5", AppStrings.T("testing.placeDependentViews.steps.S5"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("testing.placeDependentViews.review.itemMode"), ModeLabel(_compositeMode)),
                    (AppStrings.T("testing.placeDependentViews.review.itemViews"),
                     ActiveSelection.Count == 0
                        ? AppStrings.T("testing.placeDependentViews.review.viewsNone")
                        : AppStrings.T("testing.placeDependentViews.review.viewsValue", ActiveSelection.Count)),
                    (AppStrings.T("testing.placeDependentViews.review.itemTb"),
                     string.IsNullOrEmpty(_selectedTitleblock) ? "-" : _selectedTitleblock),
                    (AppStrings.T("testing.placeDependentViews.review.itemNaming"),
                     AppStrings.T("testing.placeDependentViews.review.namingValue", _numberPrefix, _startingNumber, _numberSuffix, _namingPattern)),
                    (AppStrings.T("testing.placeDependentViews.review.itemSeries"),
                     string.IsNullOrWhiteSpace(_sheetSeries) ? "-"
                        : AppStrings.T("testing.placeDependentViews.review.seriesValue", _sheetSeries, _seriesParamName)),
                    (AppStrings.T("testing.placeDependentViews.review.itemTrim"),
                     _trimBubbles ? AppStrings.T("testing.placeDependentViews.review.trimOn", _trimInches) : AppStrings.T("testing.placeDependentViews.review.trimOff")),
                    (AppStrings.T("testing.placeDependentViews.review.itemLayout"),
                     AppStrings.T("testing.placeDependentViews.review.layoutValue", LayoutLabel, _gapInches, _marginTop, _marginBottom, _marginLeft, _marginRight)),
                },
                note: _compositeMode
                    ? AppStrings.T("testing.placeDependentViews.review.noteComposite")
                    : AppStrings.T("testing.placeDependentViews.review.noteDependents"),
                warning: _trimBubbles ? AppStrings.T("testing.placeDependentViews.review.warning") : null));

            return new List<WebStep> { s1, s2, s3, s4, s5 };
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "mode":
                {
                    bool composite = AsString(value) == ModeTokenComposite;
                    if (composite != _compositeMode)
                    {
                        _compositeMode = composite;
                        StepInputsChanged?.Invoke("S1");
                        Fire();
                    }
                    break;
                }
                case "parentViews":    _selectedParentIds    = IdList(value).Select(id => new ElementId(id)).ToList(); Fire(); break;
                case "compositeViews": _selectedCompositeIds = IdList(value).Select(id => new ElementId(id)).ToList(); Fire(); break;
                case "titleblock":     _selectedTitleblock   = AsString(value, _selectedTitleblock); Fire(); break;
                case "startNumber":    { var v = (int)AsDouble(value, _startingNumber); if (v >= 1) _startingNumber = v; Fire(); break; }
                case "numberPrefix":   _numberPrefix    = AsString(value); Fire(); break;
                case "numberSuffix":   _numberSuffix    = AsString(value); Fire(); break;
                case "pattern":
                    _namingPattern = AsString(value, _namingPattern);
                    NamingPatternStore.Instance.Set(ToolId, _namingPattern);
                    Fire(); break;
                case "series":         _sheetSeries     = AsString(value); Fire(); break;
                case "seriesParam":    _seriesParamName = AsString(value); Fire(); break;
                case "layoutMode":
                    _layoutMode = AsString(value) == LayoutTokenEstimate ? LayoutMode.Estimate
                                : AsString(value) == LayoutTokenGrouped  ? LayoutMode.Grouped
                                : LayoutMode.Measured;
                    Fire(); break;
                case "trim":         _trimBubbles  = AsBool(value, _trimBubbles); Fire(); break;
                case "trimDist":     _trimInches   = AsDouble(value, _trimInches);   Fire(); break;
                case "gap":          _gapInches    = AsDouble(value, _gapInches);    Fire(); break;
                case "marginTop":    _marginTop    = AsDouble(value, _marginTop);    Fire(); break;
                case "marginBottom": _marginBottom = AsDouble(value, _marginBottom); Fire(); break;
                case "marginLeft":   _marginLeft   = AsDouble(value, _marginLeft);   Fire(); break;
                case "marginRight":  _marginRight  = AsDouble(value, _marginRight);  Fire(); break;
            }
        }

        public override bool IsStepValid(string stepId)
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

        public override bool CanRun() =>
            IsStepValid("S1") && IsStepValid("S2") && IsStepValid("S3");

        public override string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return ActiveSelection.Count == 0
                    ? "-"
                    : AppStrings.T("testing.placeDependentViews.summaries.s1Value", ActiveSelection.Count,
                        (_compositeMode ? AppStrings.T("testing.placeDependentViews.summaries.s1Composite") : AppStrings.T("testing.placeDependentViews.summaries.s1Dependents")));
                case "S2": return string.IsNullOrEmpty(_selectedTitleblock) ? "-" : _selectedTitleblock;
                case "S3": return AppStrings.T("testing.placeDependentViews.summaries.s3Value", _namingPattern, _startingNumber);
                case "S4": return AppStrings.T("testing.placeDependentViews.summaries.s4Value", LayoutLabel,
                    (_trimBubbles ? AppStrings.T("testing.placeDependentViews.summaries.s4Trim", _trimInches) : AppStrings.T("testing.placeDependentViews.summaries.s4NoTrim")), _gapInches);
                case "S5": return AppStrings.T("testing.placeDependentViews.summaries.S5");
                default:   return "-";
            }
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_handler == null || _event == null) return;

            _handler.Mode             = _compositeMode
                                        ? PlaceViewsMode.CompositeOneSheet
                                        : PlaceViewsMode.DependentsPerParent;
            _handler.ParentViewIds    = new List<ElementId>(ActiveSelection);
            _handler.TitleBlockTypeId = _titleblockMap.TryGetValue(_selectedTitleblock, out var tbId)
                                        ? tbId : ElementId.InvalidElementId;
            _handler.StartingNumber   = _startingNumber;
            _handler.NamingPattern    = _namingPattern;
            _handler.NumberPrefix     = _numberPrefix;
            _handler.NumberSuffix     = _numberSuffix;
            _handler.SheetSeries      = _sheetSeries;
            _handler.SeriesParamName  = _seriesParamName;
            _handler.Layout           = _layoutMode;
            _handler.TrimBubbles      = _trimBubbles;
            _handler.TrimInches       = _trimInches;
            _handler.MarginTopIn      = _marginTop;
            _handler.MarginBottomIn   = _marginBottom;
            _handler.MarginLeftIn     = _marginLeft;
            _handler.MarginRightIn    = _marginRight;
            _handler.GapIn            = _gapInches;
            _handler.PushLog          = pushLog;
            _handler.OnProgress       = onProgress;
            _handler.OnComplete       = onComplete;

            _event.Raise();
        }

        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.PushLog    = null;
            _handler.OnProgress = null;
            _handler.OnComplete = null;
        }
    }
}
