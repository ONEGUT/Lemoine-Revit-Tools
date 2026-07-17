using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.ModifyElements
{
    /// <summary>Web port of <see cref="SplitByReferencePlaneViewModel"/>. Same handler, category groups,
    /// pre-selection path, and AppStrings keys. The WPF refresh-levels-on-S2-activation becomes
    /// a refresh on S1 confirm (IWebConfirmable) + IWebStepRefresh rebuild of S2.</summary>
    public class SplitByReferencePlaneWebTool : WebToolBase, IWebToolCleanup, IWebConfirmable, IWebStepRefresh
    {
        private readonly SplitByReferencePlaneEventHandler _handler;
        private readonly ExternalEvent            _event;
        private readonly Dictionary<string, List<string>> _categoryGroups;
        private readonly int _totalElements;
        private readonly ElementId? _activeViewId;
        private readonly IReadOnlyList<ElementId> _preSelectedIds;

        private List<string> _selectedCats;
        private List<string> _selectedRefNames = new List<string>();
        private bool _useActiveView;
        private Dictionary<string, ReferencePlane> _refPlanesByName;

        public event Action<string>? StepInputsChanged;

        public SplitByReferencePlaneWebTool(
            SplitByReferencePlaneEventHandler handler, ExternalEvent externalEvent,
            IEnumerable<ReferencePlane> allPlanes,
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
            _refPlanesByName = BuildRefPlaneMap(allPlanes);
            _selectedCats   = _preSelectedIds.Count > 0 ? new List<string>(preSelectedCats) : new List<string>();
        }

        private static string RefPlaneName(ReferencePlane rp) =>
            string.IsNullOrWhiteSpace(rp.Name)
                ? AppStrings.T("modify.splitByReferencePlane.labels.refPlaneFallback", rp.Id.Value)
                : rp.Name;

        private static Dictionary<string, ReferencePlane> BuildRefPlaneMap(IEnumerable<ReferencePlane> planes) =>
            planes.GroupBy(p => RefPlaneName(p)).ToDictionary(p => p.Key, p => p.First());

        public override string Title    => AppStrings.T("modify.splitByReferencePlane.title");
        public override string RunLabel => AppStrings.T("modify.splitByReferencePlane.runLabel");

        public string? ConfirmLabelFor(string stepId) => null;

        // Mirrors WPF OnStepActivated("S2"): confirming S1 re-collects levels on the Revit
        // thread and rebuilds S2 when the fresh list lands.
        public void OnStepConfirm(string stepId)
        {
            if (stepId != "S1") return;
            _handler.IsRefreshRequest = true;
            _handler.OnRefreshed = freshPlanes =>
            {
                _refPlanesByName = BuildRefPlaneMap(freshPlanes);
                _selectedRefNames = _selectedRefNames.Where(n => _refPlanesByName.ContainsKey(n)).ToList();
                StepInputsChanged?.Invoke("S2");
                Fire();
            };
            _event.Raise();
        }

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            var s1 = new WebStep("S1", AppStrings.T("modify.splitByReferencePlane.steps.S1"));
            if (_preSelectedIds.Count > 0)
            {
                s1.Add(WebInput.Hint("presel",
                    AppStrings.T("modify.splitByReferencePlane.labels.fromSelection") + "  -  " +
                    AppStrings.T("modify.splitByReferencePlane.labels.preselCount", _preSelectedIds.Count, _selectedCats.Count) +
                    (_selectedCats.Count > 0 ? "  (" + string.Join(" · ", _selectedCats) + ")" : "")));
                s1.Add(WebInput.Hint("preselNote", AppStrings.T("modify.splitByReferencePlane.labels.preselNote")));
            }
            else
            {
                int totalCats = _categoryGroups.Values.Sum(g => g.Count);
                s1.Add(WebInput.Hint("countStrip",
                    AppStrings.T("modify.splitByReferencePlane.labels.countStrip", totalCats, _totalElements)));
                s1.Add(WebInput.MultiSelectTabs("cats", "", _categoryGroups, _selectedCats));
                if (_activeViewId != null)
                    s1.Add(WebInput.Toggle("activeView",
                        AppStrings.T("modify.splitByReferencePlane.labels.activeViewLabel"), _useActiveView));
            }

            var s2 = new WebStep("S2", AppStrings.T("modify.splitByReferencePlane.steps.S2"));
            if (_refPlanesByName.Count == 0)
                s2.Add(WebInput.Hint("noItems", AppStrings.T("modify.splitByReferencePlane.labels.noItems")));
            else
                s2.Add(WebInput.MultiSelectTabs("planes", "",
                    new Dictionary<string, List<string>> { ["Reference Planes"] = _refPlanesByName.Keys.OrderBy(n => n).ToList() },
                    _selectedRefNames));

            var s3 = new WebStep("S3", AppStrings.T("modify.splitByReferencePlane.steps.S3"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("modify.splitByReferencePlane.review.itemCats"),
                     _selectedCats.Count == 0 ? "-" : string.Join(", ", _selectedCats)),
                    (AppStrings.T("modify.splitByReferencePlane.review.itemX"),
                     _selectedRefNames.Count == 0 ? "-" : AppStrings.T("modify.splitByReferencePlane.review.xValue", _selectedRefNames.Count)),
                    (AppStrings.T("modify.splitByReferencePlane.review.itemOp"), AppStrings.T("modify.splitByReferencePlane.review.op")),
                    (AppStrings.T("modify.splitByReferencePlane.review.itemScope"),
                     _preSelectedIds.Count > 0 ? AppStrings.T("modify.splitByReferencePlane.review.scopeFromSel", _preSelectedIds.Count)
                        : _useActiveView ? AppStrings.T("modify.splitByReferencePlane.review.scopeActive")
                        : AppStrings.T("modify.splitByReferencePlane.review.scopeDoc")),
                },
                note: AppStrings.T("modify.splitByReferencePlane.review.note")));

            return new List<WebStep> { s1, s2, s3 };
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "cats":       _selectedCats       = StrList(value); Fire(); break;
                case "planes":     _selectedRefNames = StrList(value); Fire(); break;
                case "activeView": _useActiveView      = AsBool(value, _useActiveView); Fire(); break;
            }
        }

        public override bool IsStepValid(string stepId)
        {
            if (stepId == "S1") return _preSelectedIds.Count > 0 || _selectedCats.Count > 0;
            if (stepId == "S2") return _selectedRefNames.Count > 0;
            return true;
        }

        public override bool CanRun() =>
            (_preSelectedIds.Count > 0 || _selectedCats.Count > 0) && _selectedRefNames.Count > 0;

        public override string SummaryFor(string stepId)
        {
            if (stepId == "S1")
            {
                if (_preSelectedIds.Count > 0) return AppStrings.T("modify.splitByReferencePlane.review.scopeFromSel", _preSelectedIds.Count);
                return _selectedCats.Count == 0 ? "-" : string.Join(", ", _selectedCats);
            }
            if (stepId == "S2")
                return _selectedRefNames.Count == 0 ? "-" : AppStrings.T("modify.splitByReferencePlane.summaries.s2", _selectedRefNames.Count);
            if (stepId == "S3") return AppStrings.T("modify.splitByReferencePlane.summaries.S3");
            return "-";
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            _handler.PreSelectedIds        = _preSelectedIds.Count > 0 ? new List<ElementId>(_preSelectedIds) : null;
            _handler.ActiveViewId          = (_useActiveView && _activeViewId != null) ? _activeViewId : null;
            _handler.SelectedCategoryNames = new List<string>(_selectedCats);
            _handler.SelectedRefPlaneIds      = _selectedRefNames
                .Where(n => _refPlanesByName.ContainsKey(n)).Select(n => _refPlanesByName[n].Id).ToList();
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
