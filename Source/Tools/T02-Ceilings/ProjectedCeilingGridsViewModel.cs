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
    public class ProjectedCeilingGridsViewModel : ILemoineTool, ILemoineReviewable
    {
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

                var pathBox = new WpfTextBox
                {
                    Text            = _folderPath,
                    Padding         = new Thickness(8, 4, 8, 4),
                    BorderThickness = new Thickness(1),
                };
                pathBox.SetResourceReference(WpfTextBox.MinHeightProperty,   "LemoineH_Input");
                pathBox.SetResourceReference(WpfTextBox.BackgroundProperty,  "LemoineSelectBg");
                pathBox.SetResourceReference(WpfTextBox.ForegroundProperty,  "LemoineText");
                pathBox.SetResourceReference(WpfTextBox.BorderBrushProperty, "LemoineBorderMid");
                pathBox.SetResourceReference(WpfTextBox.FontFamilyProperty,  "LemoineMonoFont");
                pathBox.SetResourceReference(WpfTextBox.FontSizeProperty,    "LemoineFS_MD");
                pathBox.TextChanged += (s, e) => { _folderPath = pathBox.Text; OnValidationChanged(); };
                inner.Children.Add(pathBox);

                var browseBtn = LemoineControlStyles.BuildButton("Browse…");
                browseBtn.Margin = new Thickness(0, 4, 0, 0);
                browseBtn.Click += (s, e) =>
                {
                    var dlg = new System.Windows.Forms.FolderBrowserDialog
                    {
                        Description         = "Select DWG Export Folder",
                        SelectedPath        = _folderPath,
                        ShowNewFolderButton = false,
                    };
                    if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        pathBox.Text  = dlg.SelectedPath;
                        _folderPath   = dlg.SelectedPath;
                        OnValidationChanged();
                    }
                };
                inner.Children.Add(browseBtn);

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
        private struct CardDef
        {
            public string       Label;
            public Func<string> Val;
            public int          Row;
            public int          Col;
            public CardDef(string label, Func<string> val, int row, int col)
            { Label = label; Val = val; Row = row; Col = col; }
        }

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

        private WpfGrid BuildInfoPanel()
        {
            var grid = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var cards = new[]
            {
                new CardDef("Source",      () => _batchMode
                    ? (string.IsNullOrEmpty(_folderPath) ? "—" : System.IO.Path.GetFileName(_folderPath))
                    : (string.IsNullOrEmpty(_dwgPath)    ? "—" : System.IO.Path.GetFileName(_dwgPath)), 0, 0),
                new CardDef("Mode",        () => _batchMode ? "Batch — folder"   : "Single file",  0, 1),
                new CardDef("Target View", () => _batchMode ? "Per DWG filename" : "Active view",  1, 0),
                new CardDef("Output",      () => "Model curves",                                    1, 1),
            };

            foreach (var c in cards)
            {
                var card = new Border
                {
                    Margin          = new Thickness(c.Col == 0 ? 0 : 4, c.Row == 0 ? 0 : 4, 0, 0),
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(3),
                    Padding         = new Thickness(10, 7, 10, 7),
                };
                card.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

                var lbl = new TextBlock { Text = c.Label.ToUpper(), Margin = new Thickness(0, 0, 0, 2) };
                lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

                var capturedVal = c.Val;
                var valText = new TextBlock
                {
                    Text         = capturedVal(),
                    FontWeight   = FontWeights.Medium,
                    TextWrapping = TextWrapping.Wrap,
                };
                valText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                valText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                valText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                ValidationChanged += (s, e) => valText.Text = capturedVal();

                var sp = new StackPanel();
                sp.Children.Add(lbl);
                sp.Children.Add(valText);
                card.Child = sp;

                WpfGrid.SetRow(card, c.Row);
                WpfGrid.SetColumn(card, c.Col);
                grid.Children.Add(card);
            }

            return grid;
        }

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
