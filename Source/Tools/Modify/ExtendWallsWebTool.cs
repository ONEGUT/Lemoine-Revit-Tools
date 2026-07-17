using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.ModifyElements
{
    /// <summary>Web port of <see cref="ExtendWallsViewModel"/> — extend walls to the level
    /// above. Same handler and AppStrings keys; the single-value ceiling height uses a stepper
    /// instead of the WPF NumberRange-with-empty-max trick.</summary>
    public class ExtendWallsWebTool : WebToolBase, IWebToolCleanup
    {
        private readonly ExtendWallsEventHandler _handler;
        private readonly ExternalEvent           _event;
        private readonly Dictionary<string, Level> _levelsByName;

        private List<string> _selectedLevelNames = new List<string>();
        private double _assumedCeilingFt = 9.0;
        private bool _activeViewOnly = true;

        public ExtendWallsWebTool(ExtendWallsEventHandler handler, ExternalEvent externalEvent,
            IEnumerable<Level> processableLevels)
        {
            _handler = handler;
            _event   = externalEvent;
            _levelsByName = processableLevels
                .OrderBy(l => l.Elevation).GroupBy(l => l.Name).ToDictionary(g => g.Key, g => g.First());
        }

        public override string Title    => AppStrings.T("modify.extendWalls.title");
        public override string RunLabel => AppStrings.T("modify.extendWalls.runLabel");

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            var s1 = new WebStep("S1", AppStrings.T("modify.extendWalls.steps.S1"));
            if (_levelsByName.Count == 0)
                s1.Add(WebInput.Hint("noItems", AppStrings.T("modify.extendWalls.labels.noItems")));
            else
                s1.Add(WebInput.MultiSelectTabs("levels", "",
                    new Dictionary<string, List<string>> { ["Levels"] = _levelsByName.Keys.ToList() },
                    _selectedLevelNames));

            var s2 = new WebStep("S2", AppStrings.T("modify.extendWalls.steps.S2"), required: false)
                .Add(WebInput.Stepper("ceiling", AppStrings.T("modify.extendWalls.labels.ceilingLabel"),
                    _assumedCeilingFt, 1.0, 100.0, 0.5, 1))
                .Add(WebInput.Toggle("viewOnly", AppStrings.T("modify.extendWalls.labels.viewOnlyLabel"), _activeViewOnly));

            var s3 = new WebStep("S3", AppStrings.T("modify.extendWalls.steps.S3"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("modify.extendWalls.review.itemLevels"),
                     _selectedLevelNames.Count == 0 ? "-" : AppStrings.T("modify.extendWalls.review.levelsValue", _selectedLevelNames.Count)),
                    (AppStrings.T("modify.extendWalls.review.itemCeiling"),
                     AppStrings.T("modify.extendWalls.review.ceilingValue", _assumedCeilingFt)),
                    (AppStrings.T("modify.extendWalls.review.itemScope"),
                     _activeViewOnly ? AppStrings.T("modify.extendWalls.review.scopeActive") : AppStrings.T("modify.extendWalls.review.scopeDoc")),
                    (AppStrings.T("modify.extendWalls.review.itemTarget"), AppStrings.T("modify.extendWalls.review.target")),
                },
                note: AppStrings.T("modify.extendWalls.review.note")));

            return new List<WebStep> { s1, s2, s3 };
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "levels":   _selectedLevelNames = StrList(value); Fire(); break;
                case "ceiling":  { var v = AsDouble(value, _assumedCeilingFt); if (v > 0) _assumedCeilingFt = v; Fire(); break; }
                case "viewOnly": _activeViewOnly = AsBool(value, _activeViewOnly); Fire(); break;
            }
        }

        public override bool IsStepValid(string stepId) => stepId != "S1" || _selectedLevelNames.Count > 0;
        public override bool CanRun() => _selectedLevelNames.Count > 0;

        public override string SummaryFor(string stepId)
        {
            if (stepId == "S1")
                return _selectedLevelNames.Count == 0 ? "-" : AppStrings.T("modify.extendWalls.summaries.s1", _selectedLevelNames.Count);
            if (stepId == "S2")
                return AppStrings.T("modify.extendWalls.summaries.s2", _assumedCeilingFt,
                    _activeViewOnly ? AppStrings.T("modify.extendWalls.summaries.scopeActiveWord")
                                    : AppStrings.T("modify.extendWalls.summaries.scopeDocWord"));
            if (stepId == "S3") return AppStrings.T("modify.extendWalls.summaries.S3");
            return "-";
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            _handler.SelectedLevelIds = _selectedLevelNames
                .Where(n => _levelsByName.ContainsKey(n)).Select(n => _levelsByName[n].Id).ToList();
            _handler.AssumedCeilingFt = _assumedCeilingFt;
            _handler.ActiveViewOnly   = _activeViewOnly;
            _handler.OnLog            = pushLog;
            _handler.OnProgress       = onProgress;
            _handler.OnComplete       = onComplete;
            _event.Raise();
        }

        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.OnLog = null; _handler.OnProgress = null; _handler.OnComplete = null;
        }
    }
}
