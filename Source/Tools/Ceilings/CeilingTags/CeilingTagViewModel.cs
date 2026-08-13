using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;
using WpfTextBlock = System.Windows.Controls.TextBlock;

namespace LemoineTools.Tools.Ceilings.CeilingTags
{
    /// <summary>
    /// Step-flow tool for Tag Ceilings: pick views → set options → review and run.
    /// </summary>
    public class CeilingTagViewModel : IStepFlowTool, IReviewableTool, IRunResult, IToolCleanup
    {
        /// <summary>One loaded ceiling tag type, captured on the Revit main thread at launch.</summary>
        public sealed class TagTypeEntry
        {
            public ElementId Id    { get; set; } = ElementId.InvalidElementId;
            /// <summary>"Family : Type" — also the key persisted in settings.</summary>
            public string    Label { get; set; } = "";
        }

        // ── Identity ─────────────────────────────────────────────────────────
        public string Title    => AppStrings.T("ceilings.tags.title");
        public string RunLabel => AppStrings.T("ceilings.tags.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", AppStrings.T("ceilings.tags.steps.S1"), required: true),
            new StepDefinition("S2", AppStrings.T("ceilings.tags.steps.S2"), required: false),
            new StepDefinition("S3", AppStrings.T("ceilings.tags.steps.S3"), required: false),
        };

        // ── Run result ───────────────────────────────────────────────────────
        public string? ResultNoun => AppStrings.T("ceilings.tags.noun");
        private IReadOnlyList<ResultChip>? _resultChips;
        public IReadOnlyList<ResultChip>? ResultChips => _resultChips;

        // ── State ────────────────────────────────────────────────────────────
        private List<long> _selectedViewIds = new List<long>();
        private readonly List<long> _allViewIds = new List<long>();
        private readonly BrowserTree _browserTree;
        private readonly List<TagTypeEntry> _tagTypes;

        private ElementId _tagTypeId = ElementId.InvalidElementId;
        private double _maxSpacingFt     = CeilingTagSettings.Instance.MaxTagSpacingFt;
        private bool   _replaceExisting  = CeilingTagSettings.Instance.ReplaceExisting;
        private bool   _accountForCovered = CeilingTagSettings.Instance.AccountForCovered;

        // ── Validation ───────────────────────────────────────────────────────
        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── Revit wiring ─────────────────────────────────────────────────────
        private readonly CeilingTagEventHandler? _handler;
        private readonly ExternalEvent?          _event;

        public CeilingTagViewModel(
            CeilingTagEventHandler? handler,
            ExternalEvent?          externalEvent,
            BrowserTree?            browserTree = null,
            IEnumerable<long>?      eligibleViewIds = null,
            List<TagTypeEntry>?     tagTypes = null)
        {
            _handler     = handler;
            _event       = externalEvent;
            _browserTree = browserTree ?? new BrowserTree();
            _tagTypes    = tagTypes ?? new List<TagTypeEntry>();

            if (eligibleViewIds != null) _allViewIds.AddRange(eligibleViewIds);

            // Restore the last-used tag type BY NAME — a persisted ElementId would belong to
            // whichever project was open when it was saved.
            string last = CeilingTagSettings.Instance.LastTagTypeName;
            TagTypeEntry? match = null;
            if (!string.IsNullOrWhiteSpace(last))
                match = _tagTypes.FirstOrDefault(t => string.Equals(t.Label, last, StringComparison.Ordinal));

            _tagTypeId = (match ?? _tagTypes.FirstOrDefault())?.Id ?? ElementId.InvalidElementId;
        }

        // ── IToolCleanup ─────────────────────────────────────────────────────
        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.PushLog       = null;
            _handler.OnProgress    = null;
            _handler.OnComplete    = null;
            _handler.OnResultChips = null;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Step content
        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildS1();
                case "S2": return BuildS2();
                case "S3": return null;   // framework renders the review (IReviewableTool)
                default:   return null;
            }
        }

        // ── S1: view selection ───────────────────────────────────────────────
        private FrameworkElement BuildS1()
        {
            var outer = new StackPanel();

            if (_allViewIds.Count == 0)
            {
                outer.Children.Add(Hint(AppStrings.T("ceilings.tags.labels.noViews")));
                return outer;
            }

            outer.Children.Add(Caption(AppStrings.T("ceilings.tags.labels.viewsCaption")));

            var picker = new BrowserTreePicker
            {
                Height         = 300,
                AccessibleName = AppStrings.T("ceilings.tags.labels.pickerName"),
            };
            // Subscribe BEFORE SetTree — its end-of-setup SelectionChanged seeds the mirror list.
            picker.SelectionChanged += ids =>
            {
                _selectedViewIds = ids.ToList();
                OnValidationChanged();
            };
            picker.SetTree(_browserTree, _allViewIds, _selectedViewIds.ToList());
            outer.Children.Add(picker);

            outer.Children.Add(Hint(AppStrings.T("ceilings.tags.labels.viewsHint")));
            return outer;
        }

        // ── S2: options ──────────────────────────────────────────────────────
        private FrameworkElement BuildS2()
        {
            var outer = new StackPanel();

            // ── Tag type ─────────────────────────────────────────────────────
            outer.Children.Add(Caption(AppStrings.T("ceilings.tags.labels.tagTypeCaption")));

            if (_tagTypes.Count == 0)
            {
                // No ceiling tag family loaded — say why the run can't proceed instead of
                // auto-loading one behind the user's back.
                outer.Children.Add(Hint(AppStrings.T("ceilings.tags.labels.noTagTypes")));
            }
            else
            {
                var labels = _tagTypes.Select(t => t.Label).ToList();
                string current = _tagTypes.FirstOrDefault(t => t.Id == _tagTypeId)?.Label ?? labels[0];

                var combo = new SingleSelect
                {
                    Items          = labels,
                    SelectedItem   = current,
                    AccessibleName = AppStrings.T("ceilings.tags.labels.tagTypeCaption"),
                };
                combo.SelectionChanged += sel =>
                {
                    var hit = _tagTypes.FirstOrDefault(t => string.Equals(t.Label, sel, StringComparison.Ordinal));
                    if (hit != null)
                    {
                        _tagTypeId = hit.Id;
                        CeilingTagSettings.Instance.LastTagTypeName = hit.Label;
                        CeilingTagSettings.Instance.Save();
                    }
                    OnValidationChanged();
                };
                outer.Children.Add(combo);
                outer.Children.Add(Hint(AppStrings.T("ceilings.tags.labels.tagTypeHint")));
            }

            // ── Spacing ──────────────────────────────────────────────────────
            outer.Children.Add(Caption(AppStrings.T("ceilings.tags.labels.spacingCaption")));

            var stepper = new InlineStepper
            {
                Value               = _maxSpacingFt,
                MinValue            = 5,
                MaxValue            = 500,
                Step                = 5,
                Decimals            = 0,
                ValueWidth          = 56,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            stepper.ValueChanged += (s, v) =>
            {
                _maxSpacingFt = v;
                CeilingTagSettings.Instance.MaxTagSpacingFt = v;
                CeilingTagSettings.Instance.Save();
                OnValidationChanged();
            };
            outer.Children.Add(stepper);
            outer.Children.Add(Hint(AppStrings.T("ceilings.tags.labels.spacingHint")));

            // ── Behaviour toggles ────────────────────────────────────────────
            outer.Children.Add(Caption(AppStrings.T("ceilings.tags.labels.behaviourCaption")));

            var tog = new ToggleSwitches();
            tog.SetItems(new List<ToggleItem>
            {
                new ToggleItem
                {
                    Id        = "replace",
                    Label     = AppStrings.T("ceilings.tags.labels.toggleReplaceLabel"),
                    Desc      = AppStrings.T("ceilings.tags.labels.toggleReplaceDesc"),
                    DefaultOn = _replaceExisting,
                },
                new ToggleItem
                {
                    Id        = "covered",
                    Label     = AppStrings.T("ceilings.tags.labels.toggleCoveredLabel"),
                    Desc      = AppStrings.T("ceilings.tags.labels.toggleCoveredDesc"),
                    DefaultOn = _accountForCovered,
                },
            });
            tog.StateChanged += state =>
            {
                if (state.TryGetValue("replace", out bool rv)) _replaceExisting  = rv;
                if (state.TryGetValue("covered", out bool cv)) _accountForCovered = cv;
                CeilingTagSettings.Instance.ReplaceExisting   = _replaceExisting;
                CeilingTagSettings.Instance.AccountForCovered = _accountForCovered;
                CeilingTagSettings.Instance.Save();
                OnValidationChanged();
            };
            outer.Children.Add(tog);

            return outer;
        }

        private static WpfTextBlock Caption(string text)
        {
            var tb = new WpfTextBlock { Text = text, Margin = new Thickness(0, 10, 0, 5) };
            tb.SetResourceReference(WpfTextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(WpfTextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(WpfTextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static WpfTextBlock Hint(string text)
        {
            var tb = new WpfTextBlock
            {
                Text         = text,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 5, 0, 0),
            };
            tb.SetResourceReference(WpfTextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(WpfTextBlock.ForegroundProperty, "LemoineTextSub");
            tb.SetResourceReference(WpfTextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        // ── IReviewableTool ──────────────────────────────────────────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("views",   AppStrings.T("ceilings.tags.review.itemViews")),
            ("tagType", AppStrings.T("ceilings.tags.review.itemTagType")),
            ("spacing", AppStrings.T("ceilings.tags.review.itemSpacing")),
            ("replace", AppStrings.T("ceilings.tags.review.itemReplace")),
            ("covered", AppStrings.T("ceilings.tags.review.itemCovered")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["views"]   = _selectedViewIds.Count == 0
                ? "—"
                : AppStrings.T("ceilings.tags.review.viewsValue", _selectedViewIds.Count),
            ["tagType"] = _tagTypes.FirstOrDefault(t => t.Id == _tagTypeId)?.Label ?? "—",
            ["spacing"] = FormatFt(_maxSpacingFt),
            ["replace"] = _replaceExisting
                ? AppStrings.T("ceilings.tags.review.yes")
                : AppStrings.T("ceilings.tags.review.no"),
            ["covered"] = _accountForCovered
                ? AppStrings.T("ceilings.tags.review.coveredVisible")
                : AppStrings.T("ceilings.tags.review.coveredWhole"),
        };

        public IList<string>? ReviewChips => null;
        public string? ReviewNote => AppStrings.T("ceilings.tags.review.note");
        public string? ReviewWarning => _tagTypes.Count == 0
            ? AppStrings.T("ceilings.tags.review.warnNoTagType")
            : null;

        private static string FormatFt(double ft)
        {
            int whole = (int)Math.Round(ft);
            return AppStrings.T("ceilings.tags.review.spacingValue", whole);
        }

        // ── Validation / summaries ───────────────────────────────────────────
        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _selectedViewIds.Count > 0;
            if (stepId == "S2") return _tagTypeId != ElementId.InvalidElementId;
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1")
                return _selectedViewIds.Count == 0
                    ? "—"
                    : AppStrings.T("ceilings.tags.summaries.viewsSelected", _selectedViewIds.Count);

            if (stepId == "S2")
                return AppStrings.T("ceilings.tags.summaries.options",
                    (int)Math.Round(_maxSpacingFt),
                    _replaceExisting
                        ? AppStrings.T("ceilings.tags.summaries.replace")
                        : AppStrings.T("ceilings.tags.summaries.keep"));

            if (stepId == "S3") return AppStrings.T("ceilings.tags.summaries.S3");
            return "—";
        }

        // ── Run ──────────────────────────────────────────────────────────────
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _resultChips = null;

            if (_handler == null || _event == null)
            {
                pushLog(AppStrings.T("ceilings.tags.log.runHandlerMissing"), "fail");
                onComplete(0, 1, 0);
                return;
            }

            if (_tagTypeId == ElementId.InvalidElementId)
            {
                pushLog(AppStrings.T("ceilings.tags.log.noTagType"), "fail");
                onComplete(0, 1, 0);
                return;
            }

            CeilingTagSettings.Instance.MaxTagSpacingFt   = _maxSpacingFt;
            CeilingTagSettings.Instance.ReplaceExisting   = _replaceExisting;
            CeilingTagSettings.Instance.AccountForCovered = _accountForCovered;
            CeilingTagSettings.Instance.Save();

            _handler.SelectedViewIds   = _selectedViewIds.Select(id => new ElementId(id)).ToList();
            _handler.TagTypeId         = _tagTypeId;
            _handler.MaxTagSpacingFt   = _maxSpacingFt;
            _handler.ReplaceExisting   = _replaceExisting;
            _handler.AccountForCovered = _accountForCovered;
            _handler.PushLog           = pushLog;
            _handler.OnProgress        = onProgress;
            _handler.OnComplete        = onComplete;
            _handler.OnResultChips     = chips => _resultChips = chips;

            pushLog(AppStrings.T("ceilings.tags.log.starting"), "info");
            _event.Raise();
        }
    }
}
