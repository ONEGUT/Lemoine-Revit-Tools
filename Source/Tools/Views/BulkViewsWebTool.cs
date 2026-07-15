using System;
using System.Collections.Generic;
using System.Linq;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.LinkViews
{
    /// <summary>Web port of <see cref="BulkViewsViewModel"/> — the composite Bulk Views window.
    /// Routes prefixed step ids ("{Mode}_{InnerId}") to the matching inner IWebTool and hides
    /// inactive modes' steps, exactly like the WPF composite. Inner tools are the web ports,
    /// wired to the same run handlers.</summary>
    public class BulkViewsWebTool : WebToolBase, IWebToolCleanup, IWebStepRefresh
    {
        private const string ModeByLevel             = "ByLevel";
        private const string ModeDuplicate           = "Duplicate";
        private const string ModeByTemplate          = "ByTemplate";
        private const string ModeReplicateDependents = "ReplicateDependents";
        private const string ModeByLink              = "ByLink";

        private string _mode = ModeByLevel;

        private readonly LinkViewsLevelWebTool          _byLevel;
        private readonly ViewsBulkDuplicateWebTool      _duplicate;
        private readonly ViewsByTemplateWebTool         _byTemplate;
        private readonly ReplicateDependentViewsWebTool _replicateDeps;
        private readonly ViewsByLinkWebTool             _byLink;

        public event Action<string>? StepInputsChanged;

        public BulkViewsWebTool(
            LinkViewsLevelWebTool byLevel, ViewsBulkDuplicateWebTool duplicate,
            ViewsByTemplateWebTool byTemplate, ReplicateDependentViewsWebTool replicateDeps,
            ViewsByLinkWebTool byLink)
        {
            _byLevel = byLevel; _duplicate = duplicate; _byTemplate = byTemplate;
            _replicateDeps = replicateDeps; _byLink = byLink;

            foreach (var (mode, tool) in Inners())
            {
                tool.ValidationChanged += (s, e) => Fire();
                if (tool is IWebStepRefresh r)
                {
                    var capturedMode = mode;
                    r.StepInputsChanged += innerId => StepInputsChanged?.Invoke(capturedMode + "_" + innerId);
                }
            }
        }

        private IEnumerable<(string Mode, IWebTool Tool)> Inners()
        {
            yield return (ModeByLevel, _byLevel);
            yield return (ModeDuplicate, _duplicate);
            yield return (ModeByTemplate, _byTemplate);
            yield return (ModeReplicateDependents, _replicateDeps);
            yield return (ModeByLink, _byLink);
        }

        private IWebTool ModeToInner(string mode) => mode switch
        {
            ModeDuplicate           => _duplicate,
            ModeByTemplate          => _byTemplate,
            ModeReplicateDependents => _replicateDeps,
            ModeByLink              => _byLink,
            _                       => _byLevel,
        };

        private IWebTool CurrentInner => ModeToInner(_mode);

        public override string Title    => AppStrings.T("linkviews.bulkViews.title");
        public override string RunLabel => CurrentInner.RunLabel;

        private static readonly (string Token, string LabelKey)[] ModeOrder =
        {
            (ModeByLevel,             "linkviews.bulkViews.modes.byLevel"),
            (ModeDuplicate,           "linkviews.bulkViews.modes.duplicate"),
            (ModeByTemplate,          "linkviews.bulkViews.modes.byTemplate"),
            (ModeReplicateDependents, "linkviews.bulkViews.modes.replicateDependents"),
            (ModeByLink,              "linkviews.bulkViews.modes.byLink"),
        };

        private static string ModeLabel(string token) =>
            AppStrings.T(ModeOrder.First(m => m.Token == token).LabelKey);

        private static bool TrySplit(string stepId, out string mode, out string innerId)
        {
            int idx = stepId.IndexOf('_');
            if (idx < 0) { mode = ""; innerId = ""; return false; }
            mode = stepId.Substring(0, idx);
            innerId = stepId.Substring(idx + 1);
            return true;
        }

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            var list = new List<WebStep>();

            var modeStep = new WebStep("mode", AppStrings.T("linkviews.bulkViews.steps.mode"))
                .Add(WebInput.SingleSelect("mode", AppStrings.T("linkviews.bulkViews.labels.modeHeader"),
                    _mode, ModeOrder.Select(m => new WebOption(m.Token, ModeLabel(m.Token)))))
                .Add(WebInput.Hint("modeHint", AppStrings.T("linkviews.bulkViews.labels.modeHint")));
            list.Add(modeStep);

            foreach (var (mode, tool) in Inners())
            {
                foreach (var inner in tool.BuildSteps())
                {
                    var prefixed = new WebStep(mode + "_" + inner.Id, inner.Title, inner.Required);
                    foreach (var input in inner.Inputs) prefixed.Add(input);
                    list.Add(prefixed);
                }
            }

            var runStep = new WebStep("run", AppStrings.T("linkviews.bulkViews.steps.run"), required: false)
                .Add(WebInput.Hint("runNote", AppStrings.T("linkviews.bulkViews.review.modeNote", ModeLabel(_mode))));
            list.Add(runStep);

            return list;
        }

        public override bool IsStepVisible(string stepId)
        {
            if (stepId == "mode" || stepId == "run") return true;
            return stepId.StartsWith(_mode + "_", StringComparison.Ordinal);
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            if (stepId == "mode" && inputId == "mode")
            {
                var m = AsString(value);
                if (ModeOrder.Any(x => x.Token == m)) { _mode = m; Fire(); }
                return;
            }
            if (TrySplit(stepId, out var mode, out var innerId))
                ModeToInner(mode).OnState(innerId, inputId, value);
        }

        public override bool IsStepValid(string stepId)
        {
            if (stepId == "mode" || stepId == "run") return true;
            return TrySplit(stepId, out var mode, out var innerId) && ModeToInner(mode).IsStepValid(innerId);
        }

        public override bool CanRun() => CurrentInner.CanRun();

        public override string SummaryFor(string stepId)
        {
            if (stepId == "mode") return ModeLabel(_mode);
            if (stepId == "run")  return ModeLabel(_mode);
            return TrySplit(stepId, out var mode, out var innerId) ? ModeToInner(mode).SummaryFor(innerId) : "-";
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
            => CurrentInner.Run(pushLog, onProgress, onComplete);

        public void OnWindowClosed()
        {
            foreach (var (_, tool) in Inners())
                (tool as IWebToolCleanup)?.OnWindowClosed();
        }
    }
}
