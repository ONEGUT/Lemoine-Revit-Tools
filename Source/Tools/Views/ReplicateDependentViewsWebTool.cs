using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Naming;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.LinkViews
{
    /// <summary>Web port of <see cref="ReplicateDependentViewsViewModel"/> — copy one view's
    /// dependents onto target views. Same handler, entries, naming tokens, and AppStrings keys.
    /// S2 (dependent preview) and S3 (targets) rebuild live when the source changes
    /// (IWebStepRefresh, mirroring the WPF LiveStep mechanism).</summary>
    public class ReplicateDependentViewsWebTool : WebToolBase, IWebToolCleanup, IWebStepRefresh
    {
        private const string ToolId         = "views.replicateDependents";
        private const string DefaultPattern = "{HostLevel} - {SourceViewName} - {DepSuffix}";

        private static readonly TokenDefinition[] RdvComputedTokens =
        {
            new TokenDefinition("HostLevel",      AppStrings.T("naming.computed.replicateDependent.hostLevel.label"),      TokenOrigin.Computed, TokenSubject.Target, TokenEntity.View),
            new TokenDefinition("TargetViewName", AppStrings.T("naming.computed.replicateDependent.targetViewName.label"), TokenOrigin.Computed, TokenSubject.Target, TokenEntity.View),
            new TokenDefinition("DepSuffix",      AppStrings.T("naming.computed.replicateDependent.depSuffix.label"),      TokenOrigin.Computed, TokenSubject.Target, TokenEntity.View),
        };

        private readonly List<SourceViewEntry> _allSources;
        private readonly List<TargetViewEntry> _allTargets;
        private readonly Dictionary<long, SourceViewEntry> _sourceById = new Dictionary<long, SourceViewEntry>();
        private readonly Dictionary<long, TargetViewEntry> _targetById = new Dictionary<long, TargetViewEntry>();
        private readonly BrowserTree _browserTree;
        private readonly ReplicateDependentViewsRunHandler _runHandler;
        private readonly ExternalEvent                     _runEvent;

        private long? _selectedSourceId;
        private List<long> _selectedTargetIds = new List<long>();
        private string _namePattern = NamingPatternStore.Instance.GetOrDefault(ToolId, DefaultPattern);

        public Dictionary<long, XYZ>? TargetBasisXMap { get; set; }

        public event Action<string>? StepInputsChanged;

        public ReplicateDependentViewsWebTool(
            ReplicateDependentViewsRunHandler runHandler, ExternalEvent runEvent,
            List<SourceViewEntry> allSources, List<TargetViewEntry> allTargets,
            BrowserTree? browserTree = null)
        {
            _runHandler  = runHandler;
            _runEvent    = runEvent;
            _allSources  = allSources ?? new List<SourceViewEntry>();
            _allTargets  = allTargets ?? new List<TargetViewEntry>();
            _browserTree = browserTree ?? new BrowserTree();
            foreach (var s in _allSources) if (!_sourceById.ContainsKey(s.ViewId.Value)) _sourceById[s.ViewId.Value] = s;
            foreach (var t in _allTargets) if (!_targetById.ContainsKey(t.ViewId.Value)) _targetById[t.ViewId.Value] = t;
        }

        public override string Title    => AppStrings.T("linkviews.replicateDependent.title");
        public override string RunLabel => AppStrings.T("linkviews.replicateDependent.runLabel");

        private SourceViewEntry? SelectedSource =>
            _selectedSourceId.HasValue && _sourceById.TryGetValue(_selectedSourceId.Value, out var s) ? s : null;

        // The WPF picker shows only eligible sources; mirror by pruning the shared browser tree
        // to the given leaf ids (folders kept when they still contain an eligible leaf).
        private static BrowserTree PruneTree(BrowserTree tree, HashSet<long> keepIds)
        {
            var pruned = new BrowserTree();
            foreach (var root in tree.Roots)
            {
                var copy = PruneNode(root, keepIds);
                if (copy != null) pruned.Roots.Add(copy);
            }
            return pruned;
        }

        private static BrowserNode? PruneNode(BrowserNode node, HashSet<long> keepIds)
        {
            var copy = new BrowserNode { Title = node.Title, Id = node.Id, IsSheet = node.IsSheet };
            foreach (var child in node.Children)
            {
                var kept = PruneNode(child, keepIds);
                if (kept != null) copy.Children.Add(kept);
            }
            bool selfEligible = node.Id.HasValue && keepIds.Contains(node.Id.Value);
            if (selfEligible || copy.Children.Count > 0)
            {
                if (node.Id.HasValue && !selfEligible) copy.Id = null; // ineligible leaf demoted to folder
                return copy;
            }
            return null;
        }

        private List<TargetViewEntry> CompatibleTargets()
        {
            var source = SelectedSource;
            if (source == null) return new List<TargetViewEntry>();
            var allowed = new HashSet<ViewType> { ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.ThreeD };
            var list = _allTargets.Where(t => allowed.Contains(t.ViewType) && t.ViewId != source.ViewId).ToList();
            foreach (var t in list)
            {
                t.OrientationWarning = false;
                if (source.BasisX != null && TargetBasisXMap != null
                    && TargetBasisXMap.TryGetValue(t.ViewId.Value, out var basis) && basis != null)
                    t.OrientationWarning = source.BasisX.CrossProduct(basis).GetLength() > 0.01;
            }
            return list;
        }

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            // S1 — source (single-select tree, eligible sources only)
            var s1 = new WebStep("S1", AppStrings.T("linkviews.replicateDependent.steps.S1"));
            if (_allSources.Count == 0)
                s1.Add(WebInput.Hint("noSources", AppStrings.T("linkviews.replicateDependent.labels.noSources")));
            else
                s1.Add(WebInput.BrowserTree("source", AppStrings.T("linkviews.replicateDependent.labels.sourceView"),
                    PruneTree(_browserTree, new HashSet<long>(_sourceById.Keys)),
                    _selectedSourceId.HasValue ? new[] { _selectedSourceId.Value } : Array.Empty<long>(),
                    singleSelect: true));

            // S2 — dependent preview (read-only)
            var s2 = new WebStep("S2", AppStrings.T("linkviews.replicateDependent.steps.S2"), required: false);
            var src = SelectedSource;
            if (src == null)
                s2.Add(WebInput.Hint("pickFirst", AppStrings.T("linkviews.replicateDependent.labels.selectSourceFirst")));
            else if (src.Deps == null || src.Deps.Count == 0)
                s2.Add(WebInput.Hint("noDeps", AppStrings.T("linkviews.replicateDependent.labels.noDepsOnSource")));
            else
            {
                s2.Add(WebInput.Hint("s2header", AppStrings.T("linkviews.replicateDependent.labels.s2Header", src.Name)));
                s2.Add(WebInput.Review("deps", src.Deps.Select(d =>
                    (AppStrings.T("linkviews.replicateDependent.labels.depName", src.Name, d.Suffix),
                     d.HasCrop ? AppStrings.T("linkviews.replicateDependent.labels.crop")
                               : AppStrings.T("linkviews.replicateDependent.labels.noCrop"))).ToArray()));
            }

            // S3 — targets (compatible, prior selections preserved)
            var s3 = new WebStep("S3", AppStrings.T("linkviews.replicateDependent.steps.S3"));
            var compatible = CompatibleTargets();
            if (src == null)
                s3.Add(WebInput.Hint("pickFirst3", AppStrings.T("linkviews.replicateDependent.labels.selectSourceFirst")));
            else if (compatible.Count == 0)
                s3.Add(WebInput.Hint("noTargets", AppStrings.T("linkviews.replicateDependent.labels.noOtherViews", src.TypeLabel)));
            else
            {
                int warnCount = compatible.Count(t => t.OrientationWarning);
                if (warnCount > 0)
                    s3.Add(WebInput.Warn("orientWarn", AppStrings.T("linkviews.replicateDependent.labels.orientationWarn", warnCount)));
                var keep = new HashSet<long>(compatible.Select(t => t.ViewId.Value));
                s3.Add(WebInput.BrowserTree("targets", AppStrings.T("linkviews.replicateDependent.labels.targetViews"),
                    PruneTree(_browserTree, keep),
                    _selectedTargetIds.Where(keep.Contains)));
            }

            // S4 — naming
            var tokens = NamingTokenRegistry.TokensFor(TokenEntity.View, hasSource: true, RdvComputedTokens);
            var firstTarget = _selectedTargetIds.Where(_targetById.ContainsKey).Select(id => _targetById[id]).FirstOrDefault();
            var ctx = new TokenContext();
            ctx.Computed["HostLevel"] = firstTarget != null
                ? (string.IsNullOrEmpty(firstTarget.LevelName) ? AppStrings.T("linkviews.replicateDependent.labels.noLevel") : firstTarget.LevelName)
                : AppStrings.T("linkviews.replicateDependent.labels.exLevel");
            ctx.Computed["SourceViewName"] = src?.Name ?? AppStrings.T("linkviews.replicateDependent.labels.exSource");
            ctx.Computed["TargetViewName"] = firstTarget?.Name ?? AppStrings.T("linkviews.replicateDependent.labels.exTarget");
            ctx.Computed["ViewType"]       = firstTarget?.TypeLabel ?? AppStrings.T("linkviews.replicateDependent.labels.exType");
            ctx.Computed["DepSuffix"]      = src?.Deps?.FirstOrDefault()?.Suffix ?? AppStrings.T("linkviews.replicateDependent.labels.exSuffix");
            var s4 = new WebStep("S4", AppStrings.T("linkviews.replicateDependent.steps.S4"), required: false)
                .Add(WebInput.TokenInput("pattern", "", _namePattern, DefaultPattern, tokens, SampleFor(tokens, ctx)));

            // S5 — review
            int deps = src?.Deps?.Count ?? 0;
            int tgts = _selectedTargetIds.Count;
            var s5 = new WebStep("S5", AppStrings.T("linkviews.replicateDependent.steps.S5"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("linkviews.replicateDependent.review.itemSource"), src?.Name ?? "-"),
                    (AppStrings.T("linkviews.replicateDependent.review.itemDeps"), deps.ToString()),
                    (AppStrings.T("linkviews.replicateDependent.review.itemTargets"),
                     tgts > 0 ? AppStrings.T("linkviews.replicateDependent.review.targetsValue", tgts) : "-"),
                    (AppStrings.T("linkviews.replicateDependent.review.itemCreate"),
                     deps > 0 && tgts > 0 ? AppStrings.T("linkviews.replicateDependent.review.createValue", deps * tgts) : "-"),
                },
                note: AppStrings.T("linkviews.replicateDependent.review.note")));

            return new List<WebStep> { s1, s2, s3, s4, s5 };
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "source":
                    var picked = IdList(value).Where(_sourceById.ContainsKey).Cast<long?>().FirstOrDefault();
                    if (picked == _selectedSourceId) return;
                    _selectedSourceId  = picked;
                    _selectedTargetIds = new List<long>();
                    StepInputsChanged?.Invoke("S2");
                    StepInputsChanged?.Invoke("S3");
                    Fire();
                    break;
                case "targets":
                    _selectedTargetIds = IdList(value).Where(_targetById.ContainsKey).ToList();
                    Fire();
                    break;
                case "pattern":
                    _namePattern = AsString(value);
                    NamingPatternStore.Instance.Set(ToolId, _namePattern);
                    Fire();
                    break;
            }
        }

        public override bool IsStepValid(string stepId)
        {
            if (stepId == "S1") return SelectedSource != null;
            if (stepId == "S3") return _selectedTargetIds.Count > 0;
            return true;
        }

        public override bool CanRun() => SelectedSource != null && _selectedTargetIds.Count > 0;

        public override string SummaryFor(string stepId)
        {
            if (stepId == "S1") return SelectedSource?.Name ?? "-";
            if (stepId == "S2")
            {
                int d = SelectedSource?.Deps?.Count ?? 0;
                return d > 0 ? AppStrings.T("linkviews.replicateDependent.summaries.depCount", d)
                             : AppStrings.T("linkviews.replicateDependent.summaries.noDeps");
            }
            if (stepId == "S3") return _selectedTargetIds.Count > 0
                ? AppStrings.T("linkviews.replicateDependent.summaries.targetCount", _selectedTargetIds.Count) : "-";
            if (stepId == "S4") return string.IsNullOrWhiteSpace(_namePattern) ? "-" : _namePattern;
            if (stepId == "S5") return AppStrings.T("linkviews.replicateDependent.summaries.S5");
            return "-";
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            var source = SelectedSource;
            if (source == null) { onComplete(0, 1, 0); return; }

            _runHandler.SourceEntry   = source;
            _runHandler.TargetEntries = _selectedTargetIds.Where(_targetById.ContainsKey).Select(id => _targetById[id]).ToList();
            _runHandler.NamePattern   = _namePattern;
            _runHandler.PushLog       = pushLog;
            _runHandler.OnProgress    = onProgress;
            _runHandler.OnComplete    = onComplete;

            pushLog(AppStrings.T("linkviews.replicateDependent.log.raising"), "info");
            _runEvent.Raise();
        }

        public void OnWindowClosed()
        {
            if (_runHandler == null) return;
            _runHandler.PushLog = null; _runHandler.OnProgress = null; _runHandler.OnComplete = null;
        }
    }
}
