using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Naming;
using LemoineTools.Framework.Web;
using LemoineTools.Tools.ScopeBoxes;

namespace LemoineTools.Tools.LinkViews
{
    /// <summary>Web port of <see cref="LinkViewsLevelViewModel"/> — Bulk Views by Level /
    /// by Scope Box. Same handler, mode tokens, per-type sub-discipline/template options,
    /// mode-aware naming patterns, and AppStrings keys. Implements IWebStepRefresh to
    /// rebuild S1/S2 when the mode or type toggles change (intra-step conditionality).</summary>
    public class LinkViewsLevelWebTool : WebToolBase, IWebToolCleanup, IWebStepRefresh
    {
        private const string ModeByLevel = "ByLevel";
        private const string ModeByBox   = "ByScopeBox";

        private const string ToolIdByLevel         = "views.byLevel";
        private const string ToolIdByBox           = "views.byLevel.byBox";
        private const string DefaultPatternByLevel = "L{LevelName} - {ViewType}";
        private const string DefaultPatternByBox   = "L{LevelName} - {ScopeBox} - {ViewType}";

        private static readonly TokenDefinition[] LevelComputedTokens =
        {
            new TokenDefinition("LevelName", AppStrings.T("naming.computed.byLevel.levelName.label"), TokenOrigin.Computed, TokenSubject.Target, TokenEntity.View),
            new TokenDefinition("ScopeBox",  AppStrings.T("naming.computed.byLevel.scopeBox.label"),  TokenOrigin.Computed, TokenSubject.Target, TokenEntity.View),
        };

        private readonly List<LinkViewsLevelViewModel.LevelEntry>        _levels;
        private readonly List<ScopeBoxEntry>                             _scopeBoxes;
        private readonly List<LinkViewsLevelViewModel.ViewTemplateEntry> _templates3D;
        private readonly List<LinkViewsLevelViewModel.ViewTemplateEntry> _templatesFP;
        private readonly List<LinkViewsLevelViewModel.ViewTemplateEntry> _templatesRCP;
        private readonly LinkViewsLevelRunHandler? _runHandler;
        private readonly ExternalEvent?            _runEvent;

        private string _mode = ModeByLevel;
        private List<ElementId> _selectedLevelIds;
        private List<ElementId> _selectedBoxIds = new List<ElementId>();
        private readonly Dictionary<string, ElementId> _levelKeyToId = new Dictionary<string, ElementId>(StringComparer.Ordinal);
        private readonly Dictionary<string, ElementId> _boxKeyToId   = new Dictionary<string, ElementId>(StringComparer.Ordinal);

        private bool _create3D = true, _createFP = true, _createRCP = true;
        private string _subDisc3D = "", _subDiscFP = "", _subDiscRCP = "";
        private ElementId _template3DId  = ElementId.InvalidElementId;
        private ElementId _templateFPId  = ElementId.InvalidElementId;
        private ElementId _templateRCPId = ElementId.InvalidElementId;

        private string _namePatternByLevel = NamingPatternStore.Instance.GetOrDefault(ToolIdByLevel, DefaultPatternByLevel);
        private string _namePatternByBox   = NamingPatternStore.Instance.GetOrDefault(ToolIdByBox,   DefaultPatternByBox);

        public event Action<string>? StepInputsChanged;

        public LinkViewsLevelWebTool(
            LinkViewsLevelRunHandler? runHandler, ExternalEvent? runEvent,
            List<LinkViewsLevelViewModel.LevelEntry>? levels,
            List<ScopeBoxEntry>? scopeBoxes,
            List<LinkViewsLevelViewModel.ViewTemplateEntry>? templates3D  = null,
            List<LinkViewsLevelViewModel.ViewTemplateEntry>? templatesFP  = null,
            List<LinkViewsLevelViewModel.ViewTemplateEntry>? templatesRCP = null)
        {
            _runHandler   = runHandler;
            _runEvent     = runEvent;
            _levels       = levels       ?? new List<LinkViewsLevelViewModel.LevelEntry>();
            _scopeBoxes   = scopeBoxes   ?? new List<ScopeBoxEntry>();
            _templates3D  = templates3D  ?? new List<LinkViewsLevelViewModel.ViewTemplateEntry>();
            _templatesFP  = templatesFP  ?? new List<LinkViewsLevelViewModel.ViewTemplateEntry>();
            _templatesRCP = templatesRCP ?? new List<LinkViewsLevelViewModel.ViewTemplateEntry>();

            _selectedLevelIds = _levels.Select(l => l.Id).ToList();  // default: all levels
            foreach (var l in _levels.OrderBy(l => l.ElevationFt))
                _levelKeyToId[AppStrings.T("linkviews.level.labels.levelRow", l.Name, l.ElevationFt)] = l.Id;
            foreach (var b in _scopeBoxes) _boxKeyToId[b.Name] = b.Id;
        }

        public override string Title    => AppStrings.T("linkviews.level.title");
        public override string RunLabel => AppStrings.T("linkviews.level.runLabel");

        private string ActiveToolId         => _mode == ModeByBox ? ToolIdByBox        : ToolIdByLevel;
        private string ActiveDefaultPattern => _mode == ModeByBox ? DefaultPatternByBox : DefaultPatternByLevel;
        private string ActiveNamePattern
        {
            get => _mode == ModeByBox ? _namePatternByBox : _namePatternByLevel;
            set { if (_mode == ModeByBox) _namePatternByBox = value; else _namePatternByLevel = value; }
        }

        private int PlannedViewCount()
        {
            int typeCount = (_create3D ? 1 : 0) + (_createFP ? 1 : 0) + (_createRCP ? 1 : 0);
            int targets   = _mode == ModeByBox ? _selectedBoxIds.Count : 1;
            return _selectedLevelIds.Count * targets * typeCount;
        }

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            string byLevelLabel = AppStrings.T("linkviews.level.labels.modeByLevel");
            string byBoxLabel   = AppStrings.T("linkviews.level.labels.modeByScopeBox");

            // ── S1: mode + (boxes) + levels + planned count ────────────────────
            var s1 = new WebStep("S1", AppStrings.T("linkviews.level.steps.S1"))
                .Add(WebInput.SingleSelect("mode", AppStrings.T("linkviews.level.labels.modeHeader"),
                    _mode == ModeByBox ? ModeByBox : ModeByLevel,
                    new[] { new WebOption(ModeByLevel, byLevelLabel), new WebOption(ModeByBox, byBoxLabel) }))
                .Add(WebInput.Hint("modeHint", AppStrings.T("linkviews.level.labels.modeHint")));

            if (_mode == ModeByBox)
            {
                if (_scopeBoxes.Count == 0)
                    s1.Add(WebInput.Hint("noBoxes", AppStrings.T("linkviews.level.labels.noBoxes")));
                else
                    s1.Add(WebInput.MultiSelectTabs("boxes", "",
                        new Dictionary<string, List<string>> { ["Scope Boxes"] = _boxKeyToId.Keys.ToList() },
                        _scopeBoxes.Where(b => _selectedBoxIds.Any(id => id.Value == b.Id.Value)).Select(b => b.Name)));
            }

            var selectedLevelKeys = _levelKeyToId
                .Where(kv => _selectedLevelIds.Any(id => id.Value == kv.Value.Value))
                .Select(kv => kv.Key);
            s1.Add(WebInput.MultiSelectTabs("levels", AppStrings.T("linkviews.level.labels.levelsHeader"),
                new Dictionary<string, List<string>> { ["Levels"] = _levelKeyToId.Keys.ToList() }, selectedLevelKeys));
            s1.Add(WebInput.Hint("planned", AppStrings.T("linkviews.level.labels.plannedCount", PlannedViewCount())));

            // ── S2: view types + per-type sub-discipline/template ──────────────
            string noneLabel = AppStrings.T("linkviews.level.labels.templateNone");
            var s2 = new WebStep("S2", AppStrings.T("linkviews.level.steps.S2"))
                .Add(WebInput.Toggle("t3d",  AppStrings.T("linkviews.level.labels.type3dLabel"),  _create3D))
                .Add(WebInput.Toggle("tfp",  AppStrings.T("linkviews.level.labels.typeFpLabel"),  _createFP))
                .Add(WebInput.Toggle("trcp", AppStrings.T("linkviews.level.labels.typeRcpLabel"), _createRCP));

            void AddTypeRow(WebStep step, string tag, bool enabled, string subDisc,
                List<LinkViewsLevelViewModel.ViewTemplateEntry> templates, ElementId templateId)
            {
                if (!enabled) return;
                step.Add(WebInput.TextField("subDisc" + tag,
                    tag + " - " + AppStrings.T("linkviews.level.labels.colSubDiscipline"), subDisc));
                step.Add(WebInput.SingleSelect("template" + tag,
                    tag + " - " + AppStrings.T("linkviews.level.labels.colViewTemplate"),
                    templates.FirstOrDefault(t => t.Id.Value == templateId.Value)?.Name ?? noneLabel,
                    new[] { new WebOption(noneLabel, noneLabel) }.Concat(templates.Select(t => new WebOption(t.Name, t.Name)))));
            }
            AddTypeRow(s2, "3D",  _create3D,  _subDisc3D,  _templates3D,  _template3DId);
            AddTypeRow(s2, "FP",  _createFP,  _subDiscFP,  _templatesFP,  _templateFPId);
            AddTypeRow(s2, "RCP", _createRCP, _subDiscRCP, _templatesRCP, _templateRCPId);

            // ── S3: naming ─────────────────────────────────────────────────────
            var tokens = NamingTokenRegistry.TokensFor(TokenEntity.View, hasSource: false, LevelComputedTokens);
            var ctx = new TokenContext();
            ctx.Computed["LevelName"] = _levels
                .Where(l => _selectedLevelIds.Any(id => id.Value == l.Id.Value))
                .OrderBy(l => l.ElevationFt).FirstOrDefault()?.Name ?? "02";
            ctx.Computed["ScopeBox"] = _mode == ModeByBox
                ? (_scopeBoxes.FirstOrDefault(b => _selectedBoxIds.Any(id => id.Value == b.Id.Value))?.Name
                   ?? _scopeBoxes.FirstOrDefault()?.Name ?? "")
                : "";
            ctx.Computed["ViewType"] = _create3D ? "3D" : _createFP ? "FP" : "RCP";
            var s3 = new WebStep("S3", AppStrings.T("linkviews.level.steps.S3"), required: false)
                .Add(WebInput.TokenInput("pattern", "", ActiveNamePattern, ActiveDefaultPattern,
                    tokens, SampleFor(tokens, ctx)));

            // ── S4: review ─────────────────────────────────────────────────────
            var types = new List<string>();
            if (_create3D) types.Add("3D");
            if (_createFP) types.Add("FP");
            if (_createRCP) types.Add("RCP");
            int planned = PlannedViewCount();
            var s4 = new WebStep("S4", AppStrings.T("linkviews.level.steps.S4"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("linkviews.level.review.itemMode"),
                     _mode == ModeByBox ? byBoxLabel : byLevelLabel),
                    (AppStrings.T("linkviews.level.review.itemLevels"),
                     _selectedLevelIds.Count > 0 ? AppStrings.T("linkviews.level.review.levelsValue", _selectedLevelIds.Count) : "-"),
                    (AppStrings.T("linkviews.level.review.itemBoxes"),
                     _mode == ModeByBox
                        ? (_selectedBoxIds.Count > 0 ? AppStrings.T("linkviews.level.review.boxesValue", _selectedBoxIds.Count) : "-")
                        : AppStrings.T("linkviews.level.review.boxesNotUsed")),
                    (AppStrings.T("linkviews.level.review.itemTypes"),
                     types.Count > 0 ? string.Join(" · ", types) : "-"),
                    (AppStrings.T("linkviews.level.review.itemMin"),
                     planned > 0 ? AppStrings.T("linkviews.level.review.minValue", planned) : "-"),
                },
                note: AppStrings.T("linkviews.level.review.note"),
                warning: _mode == ModeByBox && _scopeBoxes.Count == 0
                    ? AppStrings.T("linkviews.level.review.warnNoBoxes") : null));

            return new List<WebStep> { s1, s2, s3, s4 };
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "mode":
                    _mode = AsString(value) == ModeByBox ? ModeByBox : ModeByLevel;
                    StepInputsChanged?.Invoke("S1");
                    StepInputsChanged?.Invoke("S3"); // mode-aware pattern swaps
                    Fire();
                    break;
                case "boxes":
                    _selectedBoxIds = StrList(value).Where(_boxKeyToId.ContainsKey).Select(k => _boxKeyToId[k]).ToList();
                    Fire();
                    break;
                case "levels":
                    _selectedLevelIds = StrList(value).Where(_levelKeyToId.ContainsKey)
                        .Select(k => _levelKeyToId[k]).GroupBy(id => id.Value).Select(g => g.First()).ToList();
                    Fire();
                    break;
                case "t3d":  _create3D  = AsBool(value, _create3D);  StepInputsChanged?.Invoke("S2"); Fire(); break;
                case "tfp":  _createFP  = AsBool(value, _createFP);  StepInputsChanged?.Invoke("S2"); Fire(); break;
                case "trcp": _createRCP = AsBool(value, _createRCP); StepInputsChanged?.Invoke("S2"); Fire(); break;
                case "subDisc3D":  _subDisc3D  = AsString(value); break;
                case "subDiscFP":  _subDiscFP  = AsString(value); break;
                case "subDiscRCP": _subDiscRCP = AsString(value); break;
                case "template3D":  _template3DId  = TemplateIdFor(_templates3D,  AsString(value)); break;
                case "templateFP":  _templateFPId  = TemplateIdFor(_templatesFP,  AsString(value)); break;
                case "templateRCP": _templateRCPId = TemplateIdFor(_templatesRCP, AsString(value)); break;
                case "pattern":
                    ActiveNamePattern = AsString(value);
                    NamingPatternStore.Instance.Set(ActiveToolId, ActiveNamePattern);
                    Fire();
                    break;
            }
        }

        private static ElementId TemplateIdFor(List<LinkViewsLevelViewModel.ViewTemplateEntry> templates, string name) =>
            templates.FirstOrDefault(t => t.Name == name)?.Id ?? ElementId.InvalidElementId;

        private string byBoxLabel   => AppStrings.T("linkviews.level.labels.modeByScopeBox");
        private string byLevelLabel => AppStrings.T("linkviews.level.labels.modeByLevel");

        public override bool IsStepValid(string stepId)
        {
            if (stepId == "S1") return _selectedLevelIds.Count > 0 && (_mode == ModeByLevel || _selectedBoxIds.Count > 0);
            if (stepId == "S2") return _create3D || _createFP || _createRCP;
            return true;
        }

        public override bool CanRun() =>
            _selectedLevelIds.Count > 0 && (_mode == ModeByLevel || _selectedBoxIds.Count > 0)
            && (_create3D || _createFP || _createRCP);

        public override string SummaryFor(string stepId)
        {
            if (stepId == "S1")
            {
                string mode = _mode == ModeByBox ? byBoxLabel : byLevelLabel;
                return _mode == ModeByBox
                    ? AppStrings.T("linkviews.level.summaries.s1Boxes", mode, _selectedLevelIds.Count, _selectedBoxIds.Count)
                    : AppStrings.T("linkviews.level.summaries.s1Levels", mode, _selectedLevelIds.Count);
            }
            if (stepId == "S2")
            {
                var t = new List<string>();
                if (_create3D) t.Add("3D");
                if (_createFP) t.Add("FP");
                if (_createRCP) t.Add("RCP");
                return t.Count > 0 ? string.Join("/", t) : "-";
            }
            if (stepId == "S3") return string.IsNullOrWhiteSpace(ActiveNamePattern) ? "-" : ActiveNamePattern;
            if (stepId == "S4") return AppStrings.T("linkviews.level.summaries.S4");
            return "-";
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null) { pushLog("Run handler not registered.", "fail"); onComplete(0, 1, 0); return; }
            _runHandler.Mode             = _mode;
            _runHandler.SelectedLevelIds = new List<ElementId>(_selectedLevelIds.GroupBy(id => id.Value).Select(g => g.First()));
            _runHandler.SelectedBoxIds   = new List<ElementId>(_selectedBoxIds);
            _runHandler.Create3D         = _create3D;
            _runHandler.CreateFP         = _createFP;
            _runHandler.CreateRCP        = _createRCP;
            _runHandler.NamePattern      = ActiveNamePattern;
            _runHandler.SubDisc3D        = _subDisc3D;
            _runHandler.SubDiscFP        = _subDiscFP;
            _runHandler.SubDiscRCP       = _subDiscRCP;
            _runHandler.Template3D       = _template3DId;
            _runHandler.TemplateFP       = _templateFPId;
            _runHandler.TemplateRCP      = _templateRCPId;
            _runHandler.PushLog          = pushLog;
            _runHandler.OnProgress       = onProgress;
            _runHandler.OnComplete       = onComplete;
            pushLog(AppStrings.T("linkviews.level.log.raising"), "info");
            _runEvent.Raise();
        }

        public void OnWindowClosed()
        {
            if (_runHandler == null) return;
            _runHandler.PushLog = null; _runHandler.OnProgress = null; _runHandler.OnComplete = null;
        }
    }
}
