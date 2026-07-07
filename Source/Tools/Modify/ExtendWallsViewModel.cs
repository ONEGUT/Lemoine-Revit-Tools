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
    public class ExtendWallsViewModel : IStepFlowTool, IReviewableTool, IRunResult, IToolCleanup
    {
        // Self-describing result label for the run strip (see IRunResult).
        public string? ResultNoun => "walls";
        public System.Collections.Generic.IReadOnlyList<LemoineTools.Framework.ResultChip>? ResultChips => null;

        public string Title    => AppStrings.T("modify.extendWalls.title");
        public string RunLabel => AppStrings.T("modify.extendWalls.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", AppStrings.T("modify.extendWalls.steps.S1"), required: true),
            new StepDefinition("S2", AppStrings.T("modify.extendWalls.steps.S2"),            required: false),
            new StepDefinition("S3", AppStrings.T("modify.extendWalls.steps.S3"),       required: false),
        };

        // ── State ─────────────────────────────────────────────────────────────
        private List<string>                       _selectedLevelNames  = new List<string>();
        private readonly Dictionary<string, Level> _levelsByName;
        private double                             _assumedCeilingFt    = 9.0;
        private bool                               _activeViewOnly      = true;

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly ExtendWallsEventHandler _handler;
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

        public ExtendWallsViewModel(
            ExtendWallsEventHandler handler,
            ExternalEvent           externalEvent,
            IEnumerable<Level>      processableLevels)
        {
            _handler      = handler;
            _event        = externalEvent;
            _levelsByName = processableLevels
                .OrderBy(l => l.Elevation)
                .GroupBy(l => l.Name)
                .ToDictionary(g => g.Key, g => g.First());
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
            if (_levelsByName.Count == 0)
            {
                var msg = new TextBlock
                {
                    Text         = AppStrings.T("modify.extendWalls.labels.noItems"),
                    TextWrapping = TextWrapping.Wrap,
                };
                msg.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                msg.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                msg.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                return msg;
            }

            var groups = new Dictionary<string, List<string>>
            {
                { "Levels", _levelsByName.Keys.ToList() }
            };

            var tabs = new MultiSelectTabs();
            tabs.SetGroups(groups);
            tabs.SelectionChanged += selected =>
            {
                _selectedLevelNames = new List<string>(selected);
                OnValidationChanged();
            };
            return tabs;
        }

        private FrameworkElement BuildS2()
        {
            var outer = new StackPanel();

            // Assumed ceiling height — labeled single-value input via NumberRange
            // Using MinLabel for the value, MaxLabel deliberately left empty
            var heightRange = new NumberRange
            {
                MinLabel = AppStrings.T("modify.extendWalls.labels.ceilingLabel"),
                MaxLabel = "",
                AbsMin   = 1.0,
                AbsMax   = 100.0,
                Step     = 0.5,
            };
            heightRange.SetValues(_assumedCeilingFt, null);
            heightRange.RangeChanged += (min, max) =>
            {
                if (min.HasValue && min.Value > 0) _assumedCeilingFt = min.Value;
                OnValidationChanged();
            };
            outer.Children.Add(heightRange);

            var toggle = new ToggleSwitches();
            toggle.SetItems(new List<ToggleItem>
            {
                new ToggleItem
                {
                    Id        = "viewOnly",
                    Label     = AppStrings.T("modify.extendWalls.labels.viewOnlyLabel"),
                    Desc      = AppStrings.T("modify.extendWalls.labels.viewOnlyDesc"),
                    DefaultOn = true,
                },
            });
            toggle.StateChanged += state =>
            {
                _activeViewOnly = !state.TryGetValue("viewOnly", out bool v) || v;
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
            ("levels",  AppStrings.T("modify.extendWalls.review.itemLevels")),
            ("ceiling", AppStrings.T("modify.extendWalls.review.itemCeiling")),
            ("scope",   AppStrings.T("modify.extendWalls.review.itemScope")),
            ("target",  AppStrings.T("modify.extendWalls.review.itemTarget")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["levels"]  = _selectedLevelNames.Count == 0 ? "—" : AppStrings.T("modify.extendWalls.review.levelsValue", _selectedLevelNames.Count),
            ["ceiling"] = AppStrings.T("modify.extendWalls.review.ceilingValue", _assumedCeilingFt),
            ["scope"]   = _activeViewOnly ? AppStrings.T("modify.extendWalls.review.scopeActive") : AppStrings.T("modify.extendWalls.review.scopeDoc"),
            ["target"]  = AppStrings.T("modify.extendWalls.review.target"),
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => AppStrings.T("modify.extendWalls.review.note");
        public string?        ReviewWarning => null;

        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _selectedLevelNames.Count > 0;
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1")
                return _selectedLevelNames.Count == 0 ? "—"
                    : AppStrings.T("modify.extendWalls.summaries.s1", _selectedLevelNames.Count);
            if (stepId == "S2")
                return AppStrings.T("modify.extendWalls.summaries.s2", _assumedCeilingFt, _activeViewOnly ? AppStrings.T("modify.extendWalls.summaries.scopeActiveWord") : AppStrings.T("modify.extendWalls.summaries.scopeDocWord"));
            if (stepId == "S3")
                return AppStrings.T("modify.extendWalls.summaries.S3");
            return "—";
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _handler.SelectedLevelIds  = _selectedLevelNames
                .Where(n => _levelsByName.ContainsKey(n))
                .Select(n => _levelsByName[n].Id)
                .ToList();
            _handler.AssumedCeilingFt  = _assumedCeilingFt;
            _handler.ActiveViewOnly    = _activeViewOnly;
            _handler.OnLog             = pushLog;
            _handler.OnProgress        = onProgress;
            _handler.OnComplete        = onComplete;

            _event.Raise();
        }
    }
}
