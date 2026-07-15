using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.ModifyElements
{
    /// <summary>Web port of <see cref="SplitByLevelViewModel"/>. Same handler, category groups,
    /// pre-selection path, and AppStrings keys. The WPF refresh-levels-on-S2-activation becomes
    /// a refresh on S1 confirm (IWebConfirmable) + IWebStepRefresh rebuild of S2.</summary>
    public class SplitByLevelWebTool : WebToolBase, IWebToolCleanup, IWebConfirmable, IWebStepRefresh
    {
        private readonly SplitByLevelEventHandler _handler;
        private readonly ExternalEvent            _event;
        private readonly Dictionary<string, List<string>> _categoryGroups;
        private readonly int _totalElements;
        private readonly ElementId? _activeViewId;
        private readonly IReadOnlyList<ElementId> _preSelectedIds;

        private List<string> _selectedCats;
        private List<string> _selectedLevelNames = new List<string>();
        private bool _useActiveView;
        private Dictionary<string, Level> _levelsByName;

        public event Action<string>? StepInputsChanged;

        public SplitByLevelWebTool(
            SplitByLevelEventHandler handler, ExternalEvent externalEvent,
            IEnumerable<Level> allLevels,
            Dictionary<string, List<string>> categoryGroups,
            int totalElements, ElementId? activeViewId,
            IReadOnlyList<ElementId> preSelectedIds, IReadOnlyList<string> preSelectedCats)
        {
            _handler        = handler;
            _event          = externalEvent;
            _categoryGroups = categoryGroups;
            _totalElements  = totalElements;
            _activeViewId   = activeViewId;
            _preSelectedIds = preSelectedIds;
            _levelsByName   = BuildLevelMap(allLevels);
            _selectedCats   = _preSelectedIds.Count > 0 ? new List<string>(preSelectedCats) : new List<string>();
        }

        private static Dictionary<string, Level> BuildLevelMap(IEnumerable<Level> levels) =>
            levels.OrderBy(l => l.Elevation).GroupBy(l => l.Name).ToDictionary(g => g.Key, g => g.First());

        public override string Title    => AppStrings.T("modify.splitByLevel.title");
        public override string RunLabel => AppStrings.T("modify.splitByLevel.runLabel");

        public string? ConfirmLabelFor(string stepId) => null;

        // Mirrors WPF OnStepActivated("S2"): confirming S1 re-collects levels on the Revit
        // thread and rebuilds S2 when the fresh list lands.
        public void OnStepConfirm(string stepId)
        {
            if (stepId != "S1") return;
            _handler.IsRefreshRequest = true;
            _handler.OnRefreshed = freshLevels =>
            {
                _levelsByName = BuildLevelMap(freshLevels);
                _selectedLevelNames = _selectedLevelNames.Where(n => _levelsByName.ContainsKey(n)).ToList();
                StepInputsChanged?.Invoke("S2");
                Fire();
            };
            _event.Raise();
        }

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            var s1 = new WebStep("S1", AppStrings.T("modify.splitByLevel.steps.S1"));
            if (_preSelectedIds.Count > 0)
            {
                s1.Add(WebInput.Hint("presel",
                    AppStrings.T("modify.splitByLevel.labels.fromSelection") + "  -  " +
                    AppStrings.T("modify.splitByLevel.labels.preselCount", _preSelectedIds.Count, _selectedCats.Count) +
                    (_selectedCats.Count > 0 ? "  (" + string.Join(" · ", _selectedCats) + ")" : "")));
                s1.Add(WebInput.Hint("preselNote", AppStrings.T("modify.splitByLevel.labels.preselNote")));
            }
            else
            {
                int totalCats = _categoryGroups.Values.Sum(g => g.Count);
                s1.Add(WebInput.Hint("countStrip",
                    AppStrings.T("modify.splitByLevel.labels.countStrip", totalCats, _totalElements)));
                s1.Add(WebInput.MultiSelectTabs("cats", "", _categoryGroups, _selectedCats));
                if (_activeViewId != null)
                    s1.Add(WebInput.Toggle("activeView",
                        AppStrings.T("modify.splitByLevel.labels.activeViewLabel"), _useActiveView));
            }

            var s2 = new WebStep("S2", AppStrings.T("modify.splitByLevel.steps.S2"));
            if (_levelsByName.Count == 0)
                s2.Add(WebInput.Hint("noItems", AppStrings.T("modify.splitByLevel.labels.noItems")));
            else
                s2.Add(WebInput.MultiSelectTabs("levels", "",
                    new Dictionary<string, List<string>> { ["Levels"] = _levelsByName.Keys.ToList() },
                    _selectedLevelNames));

            var s3 = new WebStep("S3", AppStrings.T("modify.splitByLevel.steps.S3"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("modify.splitByLevel.review.itemCats"),
                     _selectedCats.Count == 0 ? "-" : string.Join(", ", _selectedCats)),
                    (AppStrings.T("modify.splitByLevel.review.itemX"),
                     _selectedLevelNames.Count == 0 ? "-" : AppStrings.T("modify.splitByLevel.review.xValue", _selectedLevelNames.Count)),
                    (AppStrings.T("modify.splitByLevel.review.itemOp"), AppStrings.T("modify.splitByLevel.review.op")),
                    (AppStrings.T("modify.splitByLevel.review.itemScope"),
                     _preSelectedIds.Count > 0 ? AppStrings.T("modify.splitByLevel.review.scopeFromSel", _preSelectedIds.Count)
                        : _useActiveView ? AppStrings.T("modify.splitByLevel.review.scopeActive")
                        : AppStrings.T("modify.splitByLevel.review.scopeDoc")),
                },
                note: AppStrings.T("modify.splitByLevel.review.note")));

            return new List<WebStep> { s1, s2, s3 };
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "cats":       _selectedCats       = StrList(value); Fire(); break;
                case "levels":     _selectedLevelNames = StrList(value); Fire(); break;
                case "activeView": _useActiveView      = AsBool(value, _useActiveView); Fire(); break;
            }
        }

        public override bool IsStepValid(string stepId)
        {
            if (stepId == "S1") return _preSelectedIds.Count > 0 || _selectedCats.Count > 0;
            if (stepId == "S2") return _selectedLevelNames.Count > 0;
            return true;
        }

        public override bool CanRun() =>
            (_preSelectedIds.Count > 0 || _selectedCats.Count > 0) && _selectedLevelNames.Count > 0;

        public override string SummaryFor(string stepId)
        {
            if (stepId == "S1")
            {
                if (_preSelectedIds.Count > 0) return AppStrings.T("modify.splitByLevel.review.scopeFromSel", _preSelectedIds.Count);
                return _selectedCats.Count == 0 ? "-" : string.Join(", ", _selectedCats);
            }
            if (stepId == "S2")
                return _selectedLevelNames.Count == 0 ? "-" : AppStrings.T("modify.splitByLevel.summaries.s2", _selectedLevelNames.Count);
            if (stepId == "S3") return AppStrings.T("modify.splitByLevel.summaries.S3");
            return "-";
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            _handler.PreSelectedIds        = _preSelectedIds.Count > 0 ? new List<ElementId>(_preSelectedIds) : null;
            _handler.ActiveViewId          = (_useActiveView && _activeViewId != null) ? _activeViewId : null;
            _handler.SelectedCategoryNames = new List<string>(_selectedCats);
            _handler.SelectedLevelIds      = _selectedLevelNames
                .Where(n => _levelsByName.ContainsKey(n)).Select(n => _levelsByName[n].Id).ToList();
            _handler.OnLog      = pushLog;
            _handler.OnProgress = onProgress;
            _handler.OnComplete = onComplete;
            _event.Raise();
        }

        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.OnLog = null; _handler.OnProgress = null; _handler.OnComplete = null; _handler.OnRefreshed = null;
        }
    }
}
