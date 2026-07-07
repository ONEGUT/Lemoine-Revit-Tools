using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;

using WpfTextBox = System.Windows.Controls.TextBox;
using WpfGrid    = System.Windows.Controls.Grid;

namespace LemoineTools.Tools.Ceilings
{
    public class ProjectedCeilingGridsViewModel : IStepFlowTool, IReviewableTool, IRunResult, IToolCleanup
    {
        // Self-describing result label for the run strip (see IRunResult).
        public string? ResultNoun => "curves";
        public System.Collections.Generic.IReadOnlyList<LemoineTools.Framework.ResultChip>? ResultChips => null;

        // ── IStepFlowTool identity ─────────────────────────────────────────────
        public string Title    => AppStrings.T("ceilings.projectGrids.title");
        public string RunLabel => AppStrings.T("ceilings.projectGrids.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", AppStrings.T("ceilings.projectGrids.steps.S1"),   required: true),
            new StepDefinition("S2", AppStrings.T("ceilings.projectGrids.steps.S2"), required: false),
        };

        // ── State ─────────────────────────────────────────────────────────────
        private bool   _batchMode  = false;
        private string _dwgPath    = "";
        private string _folderPath = "";

        // Dynamic picker host — replaced when mode changes
        private Border? _pickerHost;

        public event EventHandler? ValidationChanged;

        // Null the callbacks parked on the static handler so this VM isn't retained after close.
        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.PushLog    = null;
            _handler.OnProgress = null;
            _handler.OnComplete = null;
        }

        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── ExternalEvent wiring ───────────────────────────────────────────
        private readonly CeilingGridEventHandler _handler;
        private readonly Autodesk.Revit.UI.ExternalEvent _event;

        public ProjectedCeilingGridsViewModel(
            CeilingGridEventHandler handler,
            Autodesk.Revit.UI.ExternalEvent externalEvent)
        {
            _handler = handler;
            _event   = externalEvent;
        }

        // ═════════════════════════════════════════════════════════════════════
        // GetStepContent
        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "S1") return BuildS1();
            if (stepId == "S2") return null; // framework renders review (IReviewableTool)
            return null;
        }

        private FrameworkElement BuildS1()
        {
            var outer = new StackPanel();

            // Mode selector
            var modeSelect = new SingleSelect { Label = AppStrings.T("ceilings.projectGrids.labels.importMode") };
            modeSelect.Items = new List<string> { AppStrings.T("ceilings.projectGrids.labels.optionSingleFile"), AppStrings.T("ceilings.projectGrids.labels.optionBatchFolder") };
            modeSelect.SelectedItem = _batchMode ? AppStrings.T("ceilings.projectGrids.labels.optionBatchFolder") : AppStrings.T("ceilings.projectGrids.labels.optionSingleFile");
            modeSelect.SelectionChanged += val =>
            {
                _batchMode = val == AppStrings.T("ceilings.projectGrids.labels.optionBatchFolder");
                RefreshPickerHost();
                OnValidationChanged();
            };
            outer.Children.Add(modeSelect);

            // Dynamic picker area
            _pickerHost = new Border { Margin = new Thickness(0, 10, 0, 0) };
            RefreshPickerHost();
            outer.Children.Add(_pickerHost);

            return outer;
        }

        private void RefreshPickerHost()
        {
            if (_pickerHost == null) return;

            if (_batchMode)
            {
                var inner = new StackPanel();

                var desc = new TextBlock
                {
                    Text         = AppStrings.T("ceilings.projectGrids.labels.batchHelp"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(0, 0, 0, 8),
                    FontStyle    = FontStyles.Italic,
                };
                desc.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                desc.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                desc.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                inner.Children.Add(desc);

                var folder = new FolderBrowser
                {
                    Path        = _folderPath,
                    DialogTitle = AppStrings.T("ceilings.projectGrids.labels.folderDialogTitle"),
                };
                folder.PathChanged += p => { _folderPath = p; OnValidationChanged(); };
                inner.Children.Add(folder);

                _pickerHost.Child = inner;
            }
            else
            {
                var browser = new FileBrowser
                {
                    Label       = AppStrings.T("ceilings.projectGrids.labels.fileLabel"),
                    Filter      = AppStrings.T("ceilings.projectGrids.labels.fileFilter"),
                    DialogTitle = AppStrings.T("ceilings.projectGrids.labels.fileDialogTitle"),
                    Path        = _dwgPath,
                };
                browser.PathChanged += path =>
                {
                    _dwgPath = path ?? "";
                    OnValidationChanged();
                };
                _pickerHost.Child = browser;
            }
        }

        // ─────────────────────────────────────────────────────────────────────

        // ── IReviewableTool (P3) — framework renders the review step ───────
        public IList<(string id, string label)> ReviewItems
        {
            get
            {
                var items = new List<(string, string)>
                {
                    ("source", AppStrings.T("ceilings.projectGrids.review.itemSource")),
                    ("mode",   AppStrings.T("ceilings.projectGrids.review.itemMode")),
                    ("target", AppStrings.T("ceilings.projectGrids.review.itemTarget")),
                    ("output", AppStrings.T("ceilings.projectGrids.review.itemOutput")),
                };
                if (_batchMode) items.Add(("dwg", AppStrings.T("ceilings.projectGrids.review.itemDwg")));
                return items;
            }
        }

        public IDictionary<string, string> ReviewValues
        {
            get
            {
                var d = new Dictionary<string, string>
                {
                    ["source"] = _batchMode
                        ? (string.IsNullOrEmpty(_folderPath) ? "—" : System.IO.Path.GetFileName(_folderPath))
                        : (string.IsNullOrEmpty(_dwgPath)    ? "—" : System.IO.Path.GetFileName(_dwgPath)),
                    ["mode"]   = _batchMode ? AppStrings.T("ceilings.projectGrids.review.modeBatch")   : AppStrings.T("ceilings.projectGrids.review.modeSingle"),
                    ["target"] = _batchMode ? AppStrings.T("ceilings.projectGrids.review.targetBatch") : AppStrings.T("ceilings.projectGrids.review.targetSingle"),
                    ["output"] = AppStrings.T("ceilings.projectGrids.review.output"),
                };
                if (_batchMode) d["dwg"] = AppStrings.T("ceilings.projectGrids.review.dwgFound", CountDwgs());
                return d;
            }
        }

        public IList<string>? ReviewChips => null;

        public string? ReviewNote => _batchMode
            ? AppStrings.T("ceilings.projectGrids.review.noteBatch")
            : AppStrings.T("ceilings.projectGrids.review.noteSingle");

        public string? ReviewWarning => _batchMode && CountDwgs() == 0 ? AppStrings.T("ceilings.projectGrids.review.warnNoDwg") : null;

        private int CountDwgs()
            => Directory.Exists(_folderPath)
                ? Directory.GetFiles(_folderPath, "*.dwg", SearchOption.TopDirectoryOnly).Length
                : 0;


        // ═════════════════════════════════════════════════════════════════════
        // IsValid
        // ═════════════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "S1")
                return _batchMode
                    ? !string.IsNullOrWhiteSpace(_folderPath) && Directory.Exists(_folderPath)
                    : !string.IsNullOrWhiteSpace(_dwgPath);
            return true;
        }

        // ═════════════════════════════════════════════════════════════════════
        // SummaryFor
        // ═════════════════════════════════════════════════════════════════════
        public string SummaryFor(string stepId)
        {
            if (stepId == "S1")
            {
                if (_batchMode)
                    return string.IsNullOrEmpty(_folderPath) ? "—"
                        : System.IO.Path.GetFileName(_folderPath.TrimEnd('\\', '/'));
                return string.IsNullOrEmpty(_dwgPath) ? "—"
                    : System.IO.Path.GetFileName(_dwgPath);
            }
            if (stepId == "S2") return AppStrings.T("ceilings.projectGrids.summaries.S2");
            return "—";
        }

        // ═════════════════════════════════════════════════════════════════════
        // Run
        // ═════════════════════════════════════════════════════════════════════
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _handler.Mode           = CeilingGridEventHandler.ToolMode.Project;
            _handler.SelectedViewIds = new List<ElementId>();
            _handler.PushLog        = pushLog;
            _handler.OnProgress     = onProgress;
            _handler.OnComplete     = onComplete;

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
    }
}
