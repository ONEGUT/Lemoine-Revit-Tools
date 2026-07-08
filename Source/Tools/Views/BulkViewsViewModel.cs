using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;

namespace LemoineTools.Tools.LinkViews
{
    /// <summary>
    /// Bulk Views — merges Bulk Views by Level, Duplicate Views, Bulk Views by Template,
    /// Replicate Dependent Views, and the new "By Link" mode behind one mode dropdown (S1
    /// of this tool). Each mode's steps are the ORIGINAL, unmodified inner tool — this class
    /// only routes step ids ("{Mode}_{InnerId}") to the matching inner <see cref="IStepFlowTool"/>
    /// instance and shows/hides that mode's steps via <see cref="IConditionalSteps"/>. The four
    /// original run handlers (and the new ViewsByLinkRunHandler) are untouched; only the
    /// ViewModel/Command layer changed. See CLAUDE.md-adjacent plan doc for the full rationale.
    /// </summary>
    public sealed class BulkViewsViewModel : IStepFlowTool, IConditionalSteps, IReviewableTool, IRunResult, IToolCleanup
    {
        // Mode tokens (logic identifiers — persisted only as the current selection, not saved).
        private const string ModeByLevel            = "ByLevel";
        private const string ModeDuplicate          = "Duplicate";
        private const string ModeByTemplate         = "ByTemplate";
        private const string ModeReplicateDependents = "ReplicateDependents";
        private const string ModeByLink             = "ByLink";

        private string _mode = ModeByLevel;

        private readonly LinkViewsLevelViewModel        _byLevel;
        private readonly ViewsBulkDuplicateViewModel    _duplicate;
        private readonly ViewsByTemplateViewModel       _byTemplate;
        private readonly ReplicateDependentViewsViewModel _replicateDeps;
        private readonly ViewsByLinkViewModel           _byLink;

        private IStepFlowTool CurrentInner => ModeToInner(_mode);

        private IStepFlowTool ModeToInner(string mode)
        {
            switch (mode)
            {
                case ModeDuplicate:           return _duplicate;
                case ModeByTemplate:          return _byTemplate;
                case ModeReplicateDependents: return _replicateDeps;
                case ModeByLink:              return _byLink;
                default:                      return _byLevel;
            }
        }

        public string? ResultNoun => "views";
        public IReadOnlyList<ResultChip>? ResultChips => (CurrentInner as IRunResult)?.ResultChips;

        public string Title    => AppStrings.T("linkviews.bulkViews.title");
        public string RunLabel => CurrentInner.RunLabel;

        public event EventHandler? ValidationChanged;
        private void Fire() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public BulkViewsViewModel(
            LinkViewsLevelViewModel byLevel,
            ViewsBulkDuplicateViewModel duplicate,
            ViewsByTemplateViewModel byTemplate,
            ReplicateDependentViewsViewModel replicateDeps,
            ViewsByLinkViewModel byLink)
        {
            _byLevel       = byLevel;
            _duplicate     = duplicate;
            _byTemplate    = byTemplate;
            _replicateDeps = replicateDeps;
            _byLink        = byLink;

            // Bubble every inner tool's own ValidationChanged up so the merged step list
            // (accordion state, Run-button eligibility) reacts to their internal changes.
            _byLevel.ValidationChanged       += (s, e) => Fire();
            _duplicate.ValidationChanged     += (s, e) => Fire();
            _byTemplate.ValidationChanged    += (s, e) => Fire();
            _replicateDeps.ValidationChanged += (s, e) => Fire();
            _byLink.ValidationChanged        += (s, e) => Fire();
        }

        public void OnWindowClosed()
        {
            (_byLevel as IToolCleanup)?.OnWindowClosed();
            (_duplicate as IToolCleanup)?.OnWindowClosed();
            (_byTemplate as IToolCleanup)?.OnWindowClosed();
            (_replicateDeps as IToolCleanup)?.OnWindowClosed();
            (_byLink as IToolCleanup)?.OnWindowClosed();
        }

        // ── Steps: "mode" + every inner tool's own steps (prefixed) + shared "run" ──────
        public StepDefinition[] Steps
        {
            get
            {
                var list = new List<StepDefinition>
                {
                    new StepDefinition("mode", AppStrings.T("linkviews.bulkViews.steps.mode"), required: true),
                };
                AddPrefixed(list, ModeByLevel,             _byLevel.Steps);
                AddPrefixed(list, ModeDuplicate,           _duplicate.Steps);
                AddPrefixed(list, ModeByTemplate,          _byTemplate.Steps);
                AddPrefixed(list, ModeReplicateDependents, _replicateDeps.Steps);
                AddPrefixed(list, ModeByLink,               _byLink.Steps);
                list.Add(new StepDefinition("run", AppStrings.T("linkviews.bulkViews.steps.run"), required: false));
                return list.ToArray();
            }
        }

        private static void AddPrefixed(List<StepDefinition> list, string mode, StepDefinition[] inner)
        {
            foreach (var s in inner)
                list.Add(new StepDefinition(mode + "_" + s.Id, s.Title, s.Required));
        }

        // ── IConditionalSteps — only the active mode's own steps (+ mode/run) are visible ──
        public bool IsStepVisible(string stepId)
        {
            if (stepId == "mode" || stepId == "run") return true;
            return stepId.StartsWith(_mode + "_", StringComparison.Ordinal);
        }

        // ── Step id routing ──────────────────────────────────────────────────────
        private static bool TrySplit(string stepId, out string mode, out string innerId)
        {
            int idx = stepId.IndexOf('_');
            if (idx < 0) { mode = ""; innerId = ""; return false; }
            mode = stepId.Substring(0, idx);
            innerId = stepId.Substring(idx + 1);
            return true;
        }

        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "mode") return BuildModeStep();
            if (stepId == "run") return null; // framework renders review (IReviewableTool)
            return TrySplit(stepId, out var mode, out var innerId)
                ? ModeToInner(mode).GetStepContent(innerId)
                : null;
        }

        public bool IsValid(string stepId)
        {
            if (stepId == "mode") return true;
            if (stepId == "run") return true;
            return TrySplit(stepId, out var mode, out var innerId) && ModeToInner(mode).IsValid(innerId);
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "mode") return ModeLabel(_mode);
            if (stepId == "run") return CurrentInner.SummaryFor(LastInnerStepId(CurrentInner));
            return TrySplit(stepId, out var mode, out var innerId) ? ModeToInner(mode).SummaryFor(innerId) : "—";
        }

        private static string LastInnerStepId(IStepFlowTool tool) => tool.Steps.LastOrDefault()?.Id ?? "";

        // ── Mode step ────────────────────────────────────────────────────────────
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

        private FrameworkElement BuildModeStep()
        {
            var outer = new StackPanel();

            var header = new TextBlock { Text = AppStrings.T("linkviews.bulkViews.labels.modeHeader"), Margin = new Thickness(0, 0, 0, 6) };
            header.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            header.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            header.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(header);

            var labels = ModeOrder.Select(m => ModeLabel(m.Token)).ToList();
            var select = new SingleSelect
            {
                Width = 320,
                HorizontalAlignment = HorizontalAlignment.Left,
                Items = labels,
                SelectedItem = ModeLabel(_mode),
            };
            select.SelectionChanged += v =>
            {
                var match = ModeOrder.FirstOrDefault(m => ModeLabel(m.Token) == v);
                _mode = match.Token ?? ModeByLevel;
                Fire();
            };
            outer.Children.Add(select);

            var hint = new TextBlock
            {
                Text = AppStrings.T("linkviews.bulkViews.labels.modeHint"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            };
            hint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            hint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            hint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(hint);

            return outer;
        }

        // ── IReviewableTool — delegate to the current mode's inner tool ─────────
        public IList<(string id, string label)> ReviewItems =>
            (CurrentInner as IReviewableTool)?.ReviewItems ?? new List<(string, string)>();
        public IDictionary<string, string> ReviewValues =>
            (CurrentInner as IReviewableTool)?.ReviewValues ?? new Dictionary<string, string>();
        public IList<string>? ReviewChips => (CurrentInner as IReviewableTool)?.ReviewChips;
        public string? ReviewNote =>
            AppStrings.T("linkviews.bulkViews.review.modeNote", ModeLabel(_mode)) +
            ((CurrentInner as IReviewableTool)?.ReviewNote is string n ? " " + n : "");
        public string? ReviewWarning => (CurrentInner as IReviewableTool)?.ReviewWarning;

        // ── Run — delegate to the current mode's inner tool ─────────────────────
        public void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
            => CurrentInner.Run(pushLog, onProgress, onComplete);
    }
}
