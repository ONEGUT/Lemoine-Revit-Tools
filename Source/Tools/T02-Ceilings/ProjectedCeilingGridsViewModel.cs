using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

using WpfTextBox = System.Windows.Controls.TextBox;
using WpfGrid    = System.Windows.Controls.Grid;

namespace LemoineTools.Tools.Ceilings
{
    public class ProjectedCeilingGridsViewModel : ILemoineTool, ILemoineReviewable, ILemoineRunResult, ILemoineToolCleanup
    {
        // Self-describing result label for the run strip (see ILemoineRunResult).
        public string? ResultNoun => "curves";
        public System.Collections.Generic.IReadOnlyList<LemoineTools.Lemoine.ResultChip>? ResultChips => null;

        // ── ILemoineTool identity ─────────────────────────────────────────────
        public string Title    => "Project Ceiling Grids";
        public string RunLabel => "Run in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "DWG Source",   required: true),
            new StepDefinition("S2", "Review & Run", required: false),
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
            if (stepId == "S2") return null; // framework renders review (ILemoineReviewable)
            return null;
        }

        private FrameworkElement BuildS1()
        {
            var outer = new StackPanel();

            // Mode selector
            var modeSelect = new LemoineSingleSelect { Label = "Import mode" };
            modeSelect.Items = new List<string> { "Single file", "Batch from folder" };
            modeSelect.SelectedItem = _batchMode ? "Batch from folder" : "Single file";
            modeSelect.SelectionChanged += val =>
            {
                _batchMode = val == "Batch from folder";
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
                    Text         = "Select the folder containing DWG ceiling plan exports from Make Ceiling Grids. " +
                                   "Each DWG filename (without extension) must match a ceiling plan view name in the project.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(0, 0, 0, 8),
                    FontStyle    = FontStyles.Italic,
                };
                desc.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                desc.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                desc.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                inner.Children.Add(desc);

                var folder = new LemoineFolderBrowser
                {
                    Path        = _folderPath,
                    DialogTitle = "Select DWG Export Folder",
                };
                folder.PathChanged += p => { _folderPath = p; OnValidationChanged(); };
                inner.Children.Add(folder);

                _pickerHost.Child = inner;
            }
            else
            {
                var browser = new LemoineFileBrowser
                {
                    Label       = "Select the ceiling plan DWG to project onto ceiling soffit faces in the active view.",
                    Filter      = "AutoCAD DWG|*.dwg|All files|*.*",
                    DialogTitle = "Select Ceiling Plan DWG",
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

        // ── ILemoineReviewable (P3) — framework renders the review step ───────
        public IList<(string id, string label)> ReviewItems
        {
            get
            {
                var items = new List<(string, string)>
                {
                    ("source", "Source"),
                    ("mode",   "Mode"),
                    ("target", "Target View"),
                    ("output", "Output"),
                };
                if (_batchMode) items.Add(("dwg", "DWG Files"));
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
                    ["mode"]   = _batchMode ? "Batch — folder"   : "Single file",
                    ["target"] = _batchMode ? "Per DWG filename" : "Active view",
                    ["output"] = "Model curves",
                };
                if (_batchMode) d["dwg"] = $"{CountDwgs()} found";
                return d;
            }
        }

        public IList<string>? ReviewChips => null;

        public string? ReviewNote => _batchMode
            ? "Each DWG in the selected folder will be matched to a ceiling plan view by filename (without " +
              "extension). Matched pairs will be projected; unmatched DWGs will be logged and skipped."
            : "The DWG will be imported into the active view at origin, all curves extracted, then the import " +
              "deleted. Each curve is projected vertically onto matching ceiling soffit faces and recreated as a " +
              "model curve at the correct elevation.";

        public string? ReviewWarning => _batchMode && CountDwgs() == 0 ? "No DWG files found in folder." : null;

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
            if (stepId == "S2") return "Ready to run";
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

            pushLog("Raising Revit ExternalEvent…", "info");
            _event.Raise();
        }
    }
}
