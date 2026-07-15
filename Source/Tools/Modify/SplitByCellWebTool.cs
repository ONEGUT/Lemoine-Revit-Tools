using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.ModifyElements
{
    /// <summary>Web port of <see cref="SplitByCellViewModel"/> — split floors/ceilings/roofs/
    /// foundations/filled regions into an X×Y cell grid. Same handler, category labels,
    /// pre-selection path, and AppStrings keys.</summary>
    public class SplitByCellWebTool : WebToolBase, IWebToolCleanup
    {
        private readonly SplitByCellEventHandler _handler;
        private readonly ExternalEvent           _event;
        private readonly IReadOnlyDictionary<string, int> _elementCounts;
        private readonly IReadOnlyList<ElementId>         _preSelectedIds;

        private List<string> _selectedCats;
        private double _cellX = 10.0, _cellY = 10.0;
        private bool _useProjectOrigin;

        public SplitByCellWebTool(
            SplitByCellEventHandler handler, ExternalEvent externalEvent,
            IReadOnlyDictionary<string, int> elementCounts,
            IReadOnlyList<ElementId> preSelectedIds, IReadOnlyList<string> preSelectedCats)
        {
            _handler        = handler;
            _event          = externalEvent;
            _elementCounts  = elementCounts;
            _preSelectedIds = preSelectedIds;
            _selectedCats   = _preSelectedIds.Count > 0 ? new List<string>(preSelectedCats) : new List<string>();
        }

        public override string Title    => AppStrings.T("modify.splitByCell.title");
        public override string RunLabel => AppStrings.T("modify.splitByCell.runLabel");

        private static List<string> CatLabels() => SplitByCellHelpers.SupportedCategories
            .Select(bic => SplitByCellViewModel.CatLabels.TryGetValue(bic, out string? lbl)
                ? lbl! : bic.ToString().Replace("OST_", ""))
            .ToList();

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            var s1 = new WebStep("S1", AppStrings.T("modify.splitByCell.steps.S1"));
            if (_preSelectedIds.Count > 0)
            {
                s1.Add(WebInput.Hint("presel",
                    AppStrings.T("modify.splitByCell.labels.fromSelection") + "  -  " +
                    AppStrings.T("modify.splitByCell.labels.preselCount", _preSelectedIds.Count, _selectedCats.Count) +
                    (_selectedCats.Count > 0 ? "  (" + string.Join(" · ", _selectedCats) + ")" : "")));
                s1.Add(WebInput.Hint("preselNote", AppStrings.T("modify.splitByCell.labels.preselNote")));
            }
            else
            {
                var labels = CatLabels();
                s1.Add(WebInput.Hint("countStrip", string.Join("  ·  ",
                    labels.Select(label => $"{label}: {(_elementCounts.TryGetValue(label, out int n) ? n : 0)}"))));
                s1.Add(WebInput.MultiSelectTabs("cats", "",
                    new Dictionary<string, List<string>> { ["Categories"] = labels }, _selectedCats));
            }

            var s2 = new WebStep("S2", AppStrings.T("modify.splitByCell.steps.S2"))
                .Add(WebInput.NumberRange("cell",
                    AppStrings.T("modify.splitByCell.labels.cellXLabel") + " / " + AppStrings.T("modify.splitByCell.labels.cellYLabel"),
                    0.1, 1000, _cellX, _cellY, 0.5, 1))
                .Add(WebInput.Toggle("projOrigin", AppStrings.T("modify.splitByCell.labels.originLabel"), _useProjectOrigin));

            var s3 = new WebStep("S3", AppStrings.T("modify.splitByCell.steps.S3"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("modify.splitByCell.review.itemCats"),
                     _selectedCats.Count == 0 ? "-" : string.Join(", ", _selectedCats)),
                    (AppStrings.T("modify.splitByCell.review.itemCell"),
                     AppStrings.T("modify.splitByCell.review.cellValue", _cellX, _cellY)),
                    (AppStrings.T("modify.splitByCell.review.itemOrigin"),
                     _useProjectOrigin ? AppStrings.T("modify.splitByCell.review.originProject")
                                       : AppStrings.T("modify.splitByCell.review.originBbox")),
                    (AppStrings.T("modify.splitByCell.review.itemScope"),
                     _preSelectedIds.Count > 0
                        ? AppStrings.T("modify.splitByCell.review.scopeFromSel", _preSelectedIds.Count)
                        : AppStrings.T("modify.splitByCell.review.scopeActive")),
                },
                note: AppStrings.T("modify.splitByCell.review.note")));

            return new List<WebStep> { s1, s2, s3 };
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "cats": _selectedCats = StrList(value); Fire(); break;
                case "cell":
                    var (x, y) = AsRange(value, _cellX, _cellY);
                    if (x > 0) _cellX = x;
                    if (y > 0) _cellY = y;
                    Fire();
                    break;
                case "projOrigin": _useProjectOrigin = AsBool(value, _useProjectOrigin); Fire(); break;
            }
        }

        public override bool IsStepValid(string stepId)
        {
            if (stepId == "S1") return _preSelectedIds.Count > 0 || _selectedCats.Count > 0;
            if (stepId == "S2") return _cellX > 0 && _cellY > 0;
            return true;
        }

        public override bool CanRun() =>
            (_preSelectedIds.Count > 0 || _selectedCats.Count > 0) && _cellX > 0 && _cellY > 0;

        public override string SummaryFor(string stepId)
        {
            if (stepId == "S1")
            {
                if (_preSelectedIds.Count > 0) return AppStrings.T("modify.splitByCell.summaries.fromSelection", _preSelectedIds.Count);
                return _selectedCats.Count == 0 ? "-" : string.Join(", ", _selectedCats);
            }
            if (stepId == "S2") return AppStrings.T("modify.splitByCell.review.cellValue", _cellX, _cellY);
            if (stepId == "S3") return AppStrings.T("modify.splitByCell.summaries.S3");
            return "-";
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            _handler.PreSelectedIds         = _preSelectedIds.Count > 0 ? new List<ElementId>(_preSelectedIds) : null;
            _handler.SelectedCategoryLabels = new List<string>(_selectedCats);
            _handler.CellX                  = _cellX;
            _handler.CellY                  = _cellY;
            _handler.UseProjectOrigin       = _useProjectOrigin;
            _handler.OnLog                  = pushLog;
            _handler.OnProgress             = onProgress;
            _handler.OnComplete             = onComplete;
            _event.Raise();
        }

        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.OnLog = null; _handler.OnProgress = null; _handler.OnComplete = null;
        }
    }
}
