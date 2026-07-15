using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.ModifyElements
{
    /// <summary>Web port of <see cref="SplitByGridViewModel"/>. Same handler, category groups,
    /// pre-selection path, and AppStrings keys. The WPF refresh-levels-on-S2-activation becomes
    /// a refresh on S1 confirm (IWebConfirmable) + IWebStepRefresh rebuild of S2.</summary>
    public class SplitByGridWebTool : WebToolBase, IWebToolCleanup, IWebConfirmable, IWebStepRefresh
    {
        private readonly SplitByGridEventHandler _handler;
        private readonly ExternalEvent            _event;
        private readonly Dictionary<string, List<string>> _categoryGroups;
        private readonly int _totalElements;
        private readonly ElementId? _activeViewId;
        private readonly IReadOnlyList<ElementId> _preSelectedIds;

        private List<string> _selectedCats;
        private List<string> _selectedGridNames = new List<string>();
        private bool _useActiveView;
        private Dictionary<string, Autodesk.Revit.DB.Grid> _gridsByName;

        public event Action<string>? StepInputsChanged;

        public SplitByGridWebTool(
            SplitByGridEventHandler handler, ExternalEvent externalEvent,
            IEnumerable<Autodesk.Revit.DB.Grid> allGrids,
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
            _gridsByName    = BuildGridMap(allGrids);
            _selectedCats   = _preSelectedIds.Count > 0 ? new List<string>(preSelectedCats) : new List<string>();
        }

        private static Dictionary<string, Autodesk.Revit.DB.Grid> BuildGridMap(IEnumerable<Autodesk.Revit.DB.Grid> grids) =>
            grids.Where(x => x.Curve is Line).GroupBy(x => x.Name).ToDictionary(x => x.Key, x => x.First());

        public override string Title    => AppStrings.T("modify.splitByGrid.title");
        public override string RunLabel => AppStrings.T("modify.splitByGrid.runLabel");

        public string? ConfirmLabelFor(string stepId) => null;

        // Mirrors WPF OnStepActivated("S2"): confirming S1 re-collects levels on the Revit
        // thread and rebuilds S2 when the fresh list lands.
        public void OnStepConfirm(string stepId)
        {
            if (stepId != "S1") return;
            _handler.IsRefreshRequest = true;
            _handler.OnRefreshed = freshGrids =>
            {
                _gridsByName = BuildGridMap(freshGrids);
                _selectedGridNames = _selectedGridNames.Where(n => _gridsByName.ContainsKey(n)).ToList();
                StepInputsChanged?.Invoke("S2");
                Fire();
            };
            _event.Raise();
        }

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            var s1 = new WebStep("S1", AppStrings.T("modify.splitByGrid.steps.S1"));
            if (_preSelectedIds.Count > 0)
            {
                s1.Add(WebInput.Hint("presel",
                    AppStrings.T("modify.splitByGrid.labels.fromSelection") + "  -  " +
                    AppStrings.T("modify.splitByGrid.labels.preselCount", _preSelectedIds.Count, _selectedCats.Count) +
                    (_selectedCats.Count > 0 ? "  (" + string.Join(" · ", _selectedCats) + ")" : "")));
                s1.Add(WebInput.Hint("preselNote", AppStrings.T("modify.splitByGrid.labels.preselNote")));
            }
            else
            {
                int totalCats = _categoryGroups.Values.Sum(g => g.Count);
                s1.Add(WebInput.Hint("countStrip",
                    AppStrings.T("modify.splitByGrid.labels.countStrip", totalCats, _totalElements)));
                s1.Add(WebInput.MultiSelectTabs("cats", "", _categoryGroups, _selectedCats));
                if (_activeViewId != null)
                    s1.Add(WebInput.Toggle("activeView",
                        AppStrings.T("modify.splitByGrid.labels.activeViewLabel"), _useActiveView));
            }

            var s2 = new WebStep("S2", AppStrings.T("modify.splitByGrid.steps.S2"));
            if (_gridsByName.Count == 0)
                s2.Add(WebInput.Hint("noItems", AppStrings.T("modify.splitByGrid.labels.noItems")));
            else
                s2.Add(WebInput.MultiSelectTabs("grids", "",
                    new Dictionary<string, List<string>> { ["Grids"] = _gridsByName.Keys.OrderBy(n => n).ToList() },
                    _selectedGridNames));

            var s3 = new WebStep("S3", AppStrings.T("modify.splitByGrid.steps.S3"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("modify.splitByGrid.review.itemCats"),
                     _selectedCats.Count == 0 ? "-" : string.Join(", ", _selectedCats)),
                    (AppStrings.T("modify.splitByGrid.review.itemX"),
                     _selectedGridNames.Count == 0 ? "-" : AppStrings.T("modify.splitByGrid.review.xValue", _selectedGridNames.Count)),
                    (AppStrings.T("modify.splitByGrid.review.itemOp"), AppStrings.T("modify.splitByGrid.review.op")),
                    (AppStrings.T("modify.splitByGrid.review.itemScope"),
                     _preSelectedIds.Count > 0 ? AppStrings.T("modify.splitByGrid.review.scopeFromSel", _preSelectedIds.Count)
                        : _useActiveView ? AppStrings.T("modify.splitByGrid.review.scopeActive")
                        : AppStrings.T("modify.splitByGrid.review.scopeDoc")),
                },
                note: AppStrings.T("modify.splitByGrid.review.note")));

            return new List<WebStep> { s1, s2, s3 };
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "cats":       _selectedCats       = StrList(value); Fire(); break;
                case "grids":     _selectedGridNames = StrList(value); Fire(); break;
                case "activeView": _useActiveView      = AsBool(value, _useActiveView); Fire(); break;
            }
        }

        public override bool IsStepValid(string stepId)
        {
            if (stepId == "S1") return _preSelectedIds.Count > 0 || _selectedCats.Count > 0;
            if (stepId == "S2") return _selectedGridNames.Count > 0;
            return true;
        }

        public override bool CanRun() =>
            (_preSelectedIds.Count > 0 || _selectedCats.Count > 0) && _selectedGridNames.Count > 0;

        public override string SummaryFor(string stepId)
        {
            if (stepId == "S1")
            {
                if (_preSelectedIds.Count > 0) return AppStrings.T("modify.splitByGrid.review.scopeFromSel", _preSelectedIds.Count);
                return _selectedCats.Count == 0 ? "-" : string.Join(", ", _selectedCats);
            }
            if (stepId == "S2")
                return _selectedGridNames.Count == 0 ? "-" : AppStrings.T("modify.splitByGrid.summaries.s2", _selectedGridNames.Count);
            if (stepId == "S3") return AppStrings.T("modify.splitByGrid.summaries.S3");
            return "-";
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            _handler.PreSelectedIds        = _preSelectedIds.Count > 0 ? new List<ElementId>(_preSelectedIds) : null;
            _handler.ActiveViewId          = (_useActiveView && _activeViewId != null) ? _activeViewId : null;
            _handler.SelectedCategoryNames = new List<string>(_selectedCats);
            _handler.SelectedGridIds      = _selectedGridNames
                .Where(n => _gridsByName.ContainsKey(n)).Select(n => _gridsByName[n].Id).ToList();
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
