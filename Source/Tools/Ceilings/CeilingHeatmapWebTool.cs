using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Templates;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.Ceilings
{
    /// <summary>Web port of <see cref="CeilingHeatmapViewModel"/> — color ceilings by elevation
    /// band. Same handler, settings, ramp TemplateStore, and AppStrings keys. Color stops use
    /// the web colorPicker input; ramp save/load/delete are IWebToolAction buttons.</summary>
    public class CeilingHeatmapWebTool : WebToolBase, IWebToolCleanup, IWebStepRefresh, IWebToolAction
    {
        private const string ModeExisting = "Existing";
        private const string ModeGenerate = "Generate";

        private readonly CeilingHeatmapEventHandler? _handler;
        private readonly ExternalEvent?              _event;
        private readonly List<long>  _allViewIds = new List<long>();
        private readonly BrowserTree _browserTree;
        private readonly List<CeilingHeatmapViewModel.HeatmapLevelEntry>    _levels;
        private readonly List<CeilingHeatmapViewModel.HeatmapTemplateEntry> _rcpTemplates;

        private static readonly TemplateStore<CeilingColorRamp> _rampStore =
            new TemplateStore<CeilingColorRamp>(
                toolId:      "CeilingHeatmapRamp",
                serialize:   (data, path) => data.SaveTo(path),
                deserialize: path => CeilingColorRamp.LoadFrom(path));

        private string _colorLow  = CeilingHeatmapSettings.Instance.ColorLow;
        private string _colorMid  = CeilingHeatmapSettings.Instance.ColorMid;
        private string _colorHigh = CeilingHeatmapSettings.Instance.ColorHigh;

        private string _rampComboSelection = "";
        private string _rampSaveName       = "";

        private bool   _deleteExisting = true;
        private bool   _placeTags      = CeilingHeatmapSettings.Instance.PlaceTags;
        private double _elevTolerance  = CeilingHeatmapSettings.Instance.ElevTolerance;

        private string _viewMode = ModeExisting;
        private List<long> _selectedViewIds = new List<long>();
        private List<ElementId> _selectedGenerateLevelIds = new List<ElementId>();
        private string    _generateSuffix     = "_Heatmap";
        private ElementId _generateTemplateId = ElementId.InvalidElementId;
        private readonly Dictionary<string, ElementId> _levelKeyToId = new Dictionary<string, ElementId>(StringComparer.Ordinal);

        public event Action<string>? StepInputsChanged;

        public CeilingHeatmapWebTool(
            CeilingHeatmapEventHandler? handler,
            ExternalEvent?              externalEvent,
            Dictionary<string, List<(string Name, ElementId Id)>>? viewsByLevel = null,
            BrowserTree?                browserTree = null,
            List<CeilingHeatmapViewModel.HeatmapLevelEntry>?    levels = null,
            List<CeilingHeatmapViewModel.HeatmapTemplateEntry>? rcpTemplates = null)
        {
            _handler      = handler;
            _event        = externalEvent;
            _browserTree  = browserTree ?? new BrowserTree();
            _levels       = levels       ?? new List<CeilingHeatmapViewModel.HeatmapLevelEntry>();
            _rcpTemplates = rcpTemplates ?? new List<CeilingHeatmapViewModel.HeatmapTemplateEntry>();

            if (viewsByLevel != null)
                foreach (var kvp in viewsByLevel)
                    foreach (var (_, id) in kvp.Value)
                        _allViewIds.Add(id.Value);

            // Generate mode defaults to all levels (WPF SetGroups default).
            _selectedGenerateLevelIds = _levels.Select(l => l.Id).ToList();
        }

        public override string Title    => AppStrings.T("ceilings.heatmap.title");
        public override string RunLabel => AppStrings.T("ceilings.heatmap.runLabel");

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            // ── S1: views (mode-dependent body) ───────────────────────────────
            var s1 = new WebStep("S1", AppStrings.T("ceilings.heatmap.steps.S1"))
                .Add(WebInput.SingleSelect("viewMode", "",
                    _viewMode, new[]
                    {
                        new WebOption(ModeExisting, AppStrings.T("ceilings.heatmap.labels.modeExisting")),
                        new WebOption(ModeGenerate, AppStrings.T("ceilings.heatmap.labels.modeGenerate")),
                    }));
            if (_viewMode == ModeGenerate) BuildGenerateInputs(s1);
            else                           BuildExistingInputs(s1);

            // ── S_RAMP: color ramp ────────────────────────────────────────────
            var ramp = new WebStep("S_RAMP", AppStrings.T("ceilings.heatmap.steps.S_RAMP"), required: false);
            var savedNames = _rampStore.List().Select(t => t.Name).ToList();
            if (savedNames.Count > 0)
            {
                if (string.IsNullOrEmpty(_rampComboSelection) || !savedNames.Contains(_rampComboSelection))
                    _rampComboSelection = savedNames[0];
                ramp.Add(WebInput.SingleSelect("savedRamp", AppStrings.T("ceilings.heatmap.labels.savedRamps"),
                    _rampComboSelection, savedNames.Select(n => new WebOption(n, n))));
                ramp.Add(WebInput.Button("loadRamp",   AppStrings.T("ceilings.heatmap.labels.load"),   variant: "ghost"));
                ramp.Add(WebInput.Button("deleteRamp", AppStrings.T("ceilings.heatmap.labels.delete"), variant: "ghost"));
            }
            ramp.Add(WebInput.TextField("rampName", AppStrings.T("ceilings.heatmap.labels.rampNameTip"), _rampSaveName));
            ramp.Add(WebInput.Button("saveRamp", AppStrings.T("ceilings.heatmap.labels.save"), variant: "primary"));
            ramp.Add(WebInput.Color("colorLow",  AppStrings.T("ceilings.heatmap.labels.chipLow"),  _colorLow));
            ramp.Add(WebInput.Color("colorMid",  AppStrings.T("ceilings.heatmap.labels.chipMid"),  _colorMid));
            ramp.Add(WebInput.Color("colorHigh", AppStrings.T("ceilings.heatmap.labels.chipHigh"), _colorHigh));

            // ── S2: run options ───────────────────────────────────────────────
            var s2 = new WebStep("S2", AppStrings.T("ceilings.heatmap.steps.S2"), required: false)
                .Add(WebInput.Toggle("delete", AppStrings.T("ceilings.heatmap.labels.toggleDeleteLabel"), _deleteExisting))
                .Add(WebInput.Hint("deleteDesc", AppStrings.T("ceilings.heatmap.labels.toggleDeleteDesc")))
                .Add(WebInput.Toggle("tags", AppStrings.T("ceilings.heatmap.labels.toggleTagsLabel"), _placeTags))
                .Add(WebInput.Hint("tagsDesc", AppStrings.T("ceilings.heatmap.labels.toggleTagsDesc")))
                .Add(WebInput.Stepper("elevTol", AppStrings.T("ceilings.heatmap.labels.elevTol"),
                    Math.Round(_elevTolerance * 12.0, 2), 0, 24, 0.25, 2))
                .Add(WebInput.Hint("tolHint", AppStrings.T("ceilings.heatmap.labels.tolHint")));

            // ── S3: review ────────────────────────────────────────────────────
            var s3 = new WebStep("S3", AppStrings.T("ceilings.heatmap.steps.S3"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("ceilings.heatmap.review.itemViews"),
                     _viewMode == ModeGenerate
                        ? (_selectedGenerateLevelIds.Count == 0 ? "-"
                            : AppStrings.T("ceilings.heatmap.review.generateValue", _selectedGenerateLevelIds.Count))
                        : (_selectedViewIds.Count == 0 ? "-"
                            : AppStrings.T("ceilings.heatmap.review.viewsValue", _selectedViewIds.Count))),
                    (AppStrings.T("ceilings.heatmap.review.itemRamp"),
                     AppStrings.T("ceilings.heatmap.labels.rampDisplay", _colorLow, _colorMid, _colorHigh)),
                    (AppStrings.T("ceilings.heatmap.review.itemDelete"),
                     _deleteExisting ? AppStrings.T("ceilings.heatmap.review.yes") : AppStrings.T("ceilings.heatmap.review.no")),
                    (AppStrings.T("ceilings.heatmap.review.itemTags"),
                     _placeTags ? AppStrings.T("ceilings.heatmap.review.yes") : AppStrings.T("ceilings.heatmap.review.no")),
                    (AppStrings.T("ceilings.heatmap.review.itemTol"),
                     AppStrings.T("ceilings.heatmap.review.tolValue", Math.Round(_elevTolerance * 12.0, 4))),
                },
                note: AppStrings.T("ceilings.heatmap.review.note")));

            return new List<WebStep> { s1, ramp, s2, s3 };
        }

        private void BuildExistingInputs(WebStep s1)
        {
            if (_allViewIds.Count == 0)
                s1.Add(WebInput.Hint("noViews", AppStrings.T("ceilings.heatmap.labels.noViews")));
            else
                s1.Add(WebInput.BrowserTree("views", AppStrings.T("ceilings.heatmap.labels.pickerName"),
                    PruneTree(_browserTree, new HashSet<long>(_allViewIds)),
                    _selectedViewIds));
        }

        private void BuildGenerateInputs(WebStep s1)
        {
            if (_levels.Count == 0)
            {
                s1.Add(WebInput.Hint("noLevels", AppStrings.T("ceilings.heatmap.labels.noLevels")));
                return;
            }

            _levelKeyToId.Clear();
            var keys = _levels.OrderBy(l => l.ElevationFt).Select(l =>
            {
                string key = AppStrings.T("ceilings.heatmap.labels.levelRow", l.Name, l.ElevationFt);
                _levelKeyToId[key] = l.Id;
                return key;
            }).ToList();

            var selectedIdSet = new HashSet<long>(_selectedGenerateLevelIds.Select(id => id.Value));
            var preSelected = _levelKeyToId.Where(kv => selectedIdSet.Contains(kv.Value.Value)).Select(kv => kv.Key).ToList();

            s1.Add(WebInput.MultiSelectTabs("genLevels", "",
                new Dictionary<string, List<string>> { ["Levels"] = keys },
                preSelected.Count > 0 ? preSelected : keys));

            s1.Add(WebInput.TextField("genSuffix", AppStrings.T("ceilings.heatmap.labels.generateSuffix"), _generateSuffix));

            string noneLabel = AppStrings.T("ceilings.heatmap.labels.generateTemplateNone");
            string curTmplName = _rcpTemplates.FirstOrDefault(t => t.Id.Value == _generateTemplateId.Value)?.Name ?? noneLabel;
            s1.Add(WebInput.SingleSelect("genTemplate", AppStrings.T("ceilings.heatmap.labels.generateTemplate"),
                curTmplName,
                new[] { new WebOption(noneLabel, noneLabel) }
                    .Concat(_rcpTemplates.Select(t => new WebOption(t.Name, t.Name)))));
        }

        // ── Ramp save/load/delete buttons ─────────────────────────────────────

        public void OnToolAction(string stepId, string inputId)
        {
            switch (inputId)
            {
                case "saveRamp":
                {
                    string name = _rampSaveName.Trim();
                    if (string.IsNullOrEmpty(name)) return;
                    var ramp = new CeilingColorRamp { Low = _colorLow, Mid = _colorMid, High = _colorHigh };
                    _rampStore.Save(name, ramp, out _);
                    _rampSaveName = "";
                    _rampComboSelection = name;
                    StepInputsChanged?.Invoke("S_RAMP");
                    Fire();
                    break;
                }
                case "loadRamp":
                {
                    var info = _rampStore.List().FirstOrDefault(t => t.Name == _rampComboSelection);
                    if (info != null && _rampStore.Load(info, out var ramp, out _) && ramp != null)
                    {
                        _colorLow  = ramp.Low;
                        _colorMid  = ramp.Mid;
                        _colorHigh = ramp.High;
                        SaveColorsToSettings();
                        StepInputsChanged?.Invoke("S_RAMP");
                        Fire();
                    }
                    break;
                }
                case "deleteRamp":
                {
                    var info = _rampStore.List().FirstOrDefault(t => t.Name == _rampComboSelection);
                    if (info != null)
                    {
                        _rampStore.Delete(info, out _);
                        _rampComboSelection = "";
                        StepInputsChanged?.Invoke("S_RAMP");
                        Fire();
                    }
                    break;
                }
            }
        }

        private void SaveColorsToSettings()
        {
            CeilingHeatmapSettings.Instance.ColorLow  = _colorLow;
            CeilingHeatmapSettings.Instance.ColorMid  = _colorMid;
            CeilingHeatmapSettings.Instance.ColorHigh = _colorHigh;
            CeilingHeatmapSettings.Instance.Save();
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "viewMode":
                {
                    var m = AsString(value) == ModeGenerate ? ModeGenerate : ModeExisting;
                    if (m != _viewMode)
                    {
                        _viewMode = m;
                        StepInputsChanged?.Invoke("S1");
                        Fire();
                    }
                    break;
                }
                case "views":     _selectedViewIds = IdList(value); Fire(); break;
                case "genLevels":
                    _selectedGenerateLevelIds = StrList(value)
                        .Where(_levelKeyToId.ContainsKey)
                        .Select(k => _levelKeyToId[k])
                        .ToList();
                    Fire(); break;
                case "genSuffix":   _generateSuffix = AsString(value, _generateSuffix); Fire(); break;
                case "genTemplate":
                {
                    var entry = _rcpTemplates.FirstOrDefault(t => t.Name == AsString(value));
                    _generateTemplateId = entry?.Id ?? ElementId.InvalidElementId;
                    break;
                }
                case "savedRamp":  _rampComboSelection = AsString(value, _rampComboSelection); break;
                case "rampName":   _rampSaveName       = AsString(value); break;
                case "colorLow":   _colorLow  = AsString(value, _colorLow);  SaveColorsToSettings(); Fire(); break;
                case "colorMid":   _colorMid  = AsString(value, _colorMid);  SaveColorsToSettings(); Fire(); break;
                case "colorHigh":  _colorHigh = AsString(value, _colorHigh); SaveColorsToSettings(); Fire(); break;
                case "delete":     _deleteExisting = AsBool(value, _deleteExisting); Fire(); break;
                case "tags":       _placeTags      = AsBool(value, _placeTags);      Fire(); break;
                case "elevTol":    _elevTolerance  = AsDouble(value, _elevTolerance * 12.0) / 12.0; Fire(); break;
            }
        }

        public override bool IsStepValid(string stepId)
        {
            if (stepId == "S1")
                return _viewMode == ModeGenerate
                    ? _selectedGenerateLevelIds.Count > 0
                    : _selectedViewIds.Count > 0;
            return true;
        }

        public override bool CanRun() => IsStepValid("S1");

        public override string SummaryFor(string stepId)
        {
            if (stepId == "S1")
                return _viewMode == ModeGenerate
                    ? (_selectedGenerateLevelIds.Count == 0 ? "-"
                        : AppStrings.T("ceilings.heatmap.summaries.levelsSelected", _selectedGenerateLevelIds.Count))
                    : (_selectedViewIds.Count == 0 ? "-"
                        : AppStrings.T("ceilings.heatmap.summaries.viewsSelected", _selectedViewIds.Count));
            if (stepId == "S_RAMP")
                return AppStrings.T("ceilings.heatmap.labels.rampDisplay", _colorLow, _colorMid, _colorHigh);
            if (stepId == "S2")
            {
                var parts = new List<string>();
                if (_deleteExisting) parts.Add(AppStrings.T("ceilings.heatmap.summaries.s2Delete"));
                if (_placeTags)      parts.Add(AppStrings.T("ceilings.heatmap.summaries.s2Tags"));
                parts.Add(AppStrings.T("ceilings.heatmap.summaries.s2Tol", Math.Round(_elevTolerance * 12.0, 4)));
                return string.Join(" · ", parts);
            }
            if (stepId == "S3") return AppStrings.T("ceilings.heatmap.summaries.S3");
            return "-";
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            CeilingHeatmapSettings.Instance.PlaceTags     = _placeTags;
            CeilingHeatmapSettings.Instance.ElevTolerance = _elevTolerance;
            SaveColorsToSettings();

            if (_viewMode == ModeGenerate)
            {
                _handler!.SelectedViewIds    = new List<ElementId>();
                _handler.GenerateForLevelIds = new List<ElementId>(_selectedGenerateLevelIds);
                _handler.GenerateSuffix      = string.IsNullOrWhiteSpace(_generateSuffix) ? "_Heatmap" : _generateSuffix;
                _handler.GenerateTemplateId  = _generateTemplateId;
            }
            else
            {
                _handler!.SelectedViewIds    = _selectedViewIds.Select(id => new ElementId(id)).ToList();
                _handler.GenerateForLevelIds = new List<ElementId>();
            }
            _handler.DeleteExisting = _deleteExisting;
            _handler.PlaceTags      = _placeTags;
            _handler.ElevTolerance  = _elevTolerance;
            _handler.ColorLow       = CeilingHeatmapViewModel.ParseRevitColor(_colorLow,  0,   0, 255);
            _handler.ColorMid       = CeilingHeatmapViewModel.ParseRevitColor(_colorMid,  0, 255,   0);
            _handler.ColorHigh      = CeilingHeatmapViewModel.ParseRevitColor(_colorHigh, 255,  0,   0);
            _handler.PushLog        = pushLog;
            _handler.OnProgress     = onProgress;
            _handler.OnComplete     = onComplete;

            pushLog(AppStrings.T("ceilings.heatmap.log.starting"), "info");
            _event!.Raise();
        }

        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.PushLog       = null;
            _handler.OnProgress    = null;
            _handler.OnComplete    = null;
            _handler.OnResultChips = null;
        }
    }
}
