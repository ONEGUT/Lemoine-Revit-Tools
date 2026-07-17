using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.Ceilings
{
    /// <summary>Web port of <see cref="ProjectedCeilingGridsViewModel"/> — import ceiling-grid
    /// DWGs (single file or batch folder). Same handler and AppStrings keys; the WPF
    /// swap-the-picker-on-mode-change becomes an IWebStepRefresh rebuild of S1.</summary>
    public class ProjectedCeilingGridsWebTool : WebToolBase, IWebToolCleanup, IWebStepRefresh
    {
        private readonly CeilingGridEventHandler _handler;
        private readonly Autodesk.Revit.UI.ExternalEvent _event;

        private bool   _batchMode  = false;
        private string _dwgPath    = "";
        private string _folderPath = "";

        public event Action<string>? StepInputsChanged;

        public ProjectedCeilingGridsWebTool(CeilingGridEventHandler handler, Autodesk.Revit.UI.ExternalEvent externalEvent)
        {
            _handler = handler;
            _event   = externalEvent;
        }

        public override string Title    => AppStrings.T("ceilings.projectGrids.title");
        public override string RunLabel => AppStrings.T("ceilings.projectGrids.runLabel");

        private const string ModeSingle = "single";
        private const string ModeBatch  = "batch";

        private int CountDwgs()
            => Directory.Exists(_folderPath)
                ? Directory.GetFiles(_folderPath, "*.dwg", SearchOption.TopDirectoryOnly).Length
                : 0;

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            var s1 = new WebStep("S1", AppStrings.T("ceilings.projectGrids.steps.S1"))
                .Add(WebInput.SingleSelect("mode", AppStrings.T("ceilings.projectGrids.labels.importMode"),
                    _batchMode ? ModeBatch : ModeSingle, new[]
                    {
                        new WebOption(ModeSingle, AppStrings.T("ceilings.projectGrids.labels.optionSingleFile")),
                        new WebOption(ModeBatch,  AppStrings.T("ceilings.projectGrids.labels.optionBatchFolder")),
                    }));

            if (_batchMode)
            {
                s1.Add(WebInput.Hint("batchHelp", AppStrings.T("ceilings.projectGrids.labels.batchHelp")));
                s1.Add(WebInput.FolderBrowser("folder",
                    AppStrings.T("ceilings.projectGrids.labels.folderDialogTitle"), _folderPath));
            }
            else
            {
                s1.Add(WebInput.FileBrowser("dwg",
                    AppStrings.T("ceilings.projectGrids.labels.fileLabel"), _dwgPath,
                    filter: AppStrings.T("ceilings.projectGrids.labels.fileFilter")));
            }

            var rows = new List<(string, string)>
            {
                (AppStrings.T("ceilings.projectGrids.review.itemSource"),
                 _batchMode
                    ? (string.IsNullOrEmpty(_folderPath) ? "-" : Path.GetFileName(_folderPath.TrimEnd('\\', '/')))
                    : (string.IsNullOrEmpty(_dwgPath)    ? "-" : Path.GetFileName(_dwgPath))),
                (AppStrings.T("ceilings.projectGrids.review.itemMode"),
                 _batchMode ? AppStrings.T("ceilings.projectGrids.review.modeBatch") : AppStrings.T("ceilings.projectGrids.review.modeSingle")),
                (AppStrings.T("ceilings.projectGrids.review.itemTarget"),
                 _batchMode ? AppStrings.T("ceilings.projectGrids.review.targetBatch") : AppStrings.T("ceilings.projectGrids.review.targetSingle")),
                (AppStrings.T("ceilings.projectGrids.review.itemOutput"), AppStrings.T("ceilings.projectGrids.review.output")),
            };
            if (_batchMode)
                rows.Add((AppStrings.T("ceilings.projectGrids.review.itemDwg"),
                          AppStrings.T("ceilings.projectGrids.review.dwgFound", CountDwgs())));

            var s2 = new WebStep("S2", AppStrings.T("ceilings.projectGrids.steps.S2"), required: false);
            if (_batchMode && CountDwgs() == 0 && Directory.Exists(_folderPath))
                s2.Add(WebInput.Warn("warnNoDwg", AppStrings.T("ceilings.projectGrids.review.warnNoDwg")));
            s2.Add(WebInput.Review("review", rows.ToArray(),
                note: _batchMode
                    ? AppStrings.T("ceilings.projectGrids.review.noteBatch")
                    : AppStrings.T("ceilings.projectGrids.review.noteSingle")));

            return new List<WebStep> { s1, s2 };
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "mode":
                {
                    bool batch = AsString(value) == ModeBatch;
                    if (batch != _batchMode)
                    {
                        _batchMode = batch;
                        StepInputsChanged?.Invoke("S1");
                        Fire();
                    }
                    break;
                }
                case "folder": _folderPath = AsString(value); Fire(); break;
                case "dwg":    _dwgPath    = AsString(value); Fire(); break;
            }
        }

        public override bool IsStepValid(string stepId)
        {
            if (stepId == "S1")
                return _batchMode
                    ? !string.IsNullOrWhiteSpace(_folderPath) && Directory.Exists(_folderPath)
                    : !string.IsNullOrWhiteSpace(_dwgPath);
            return true;
        }

        public override bool CanRun() => IsStepValid("S1");

        public override string SummaryFor(string stepId)
        {
            if (stepId == "S1")
            {
                if (_batchMode)
                    return string.IsNullOrEmpty(_folderPath) ? "-"
                        : Path.GetFileName(_folderPath.TrimEnd('\\', '/'));
                return string.IsNullOrEmpty(_dwgPath) ? "-" : Path.GetFileName(_dwgPath);
            }
            if (stepId == "S2") return AppStrings.T("ceilings.projectGrids.summaries.S2");
            return "-";
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            _handler.Mode            = CeilingGridEventHandler.ToolMode.Project;
            _handler.SelectedViewIds = new List<ElementId>();
            _handler.PushLog         = pushLog;
            _handler.OnProgress      = onProgress;
            _handler.OnComplete      = onComplete;

            if (_batchMode)
            {
                _handler.BatchDwgFolder = _folderPath;
                _handler.DwgPath        = "";
            }
            else
            {
                _handler.DwgPath        = _dwgPath;
                _handler.BatchDwgFolder = "";
            }

            pushLog(AppStrings.T("ceilings.projectGrids.log.raising"), "info");
            _event.Raise();
        }

        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.PushLog = null; _handler.OnProgress = null; _handler.OnComplete = null;
        }
    }
}
