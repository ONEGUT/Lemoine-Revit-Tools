using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

using WpfTextBox = System.Windows.Controls.TextBox;
using WpfGrid    = System.Windows.Controls.Grid;

namespace LemoineTools.Tools.Ceilings
{
    public class MakeCeilingGridsViewModel : ILemoineTool
    {
        // ── DocEntry — passed in from Command ─────────────────────────────────
        public sealed class DocEntry
        {
            public string    Label;
            public bool      IsHost;
            public ElementId LinkInstId;
        }

        // ── ILemoineTool identity ─────────────────────────────────────────────
        public string Title    => "Make Ceiling Grids";
        public string RunLabel => "Create in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("docs",    "Select Documents",    required: true),
            new StepDefinition("filter",  "Filter Ceiling Types", required: false),
            new StepDefinition("export",  "Export Location",      required: true),
            new StepDefinition("run",     "Review & Run",         required: false),
        };

        // ── State ─────────────────────────────────────────────────────────────
        private readonly List<DocEntry>            _availableDocs;
        private readonly Dictionary<string, DocEntry> _docByLabel;

        private List<string>           _selectedDocLabels        = new List<string>();
        private List<CeilingTypeEntry> _ceilingTypes             = new List<CeilingTypeEntry>();
        private HashSet<string>        _excludedTypeKeys         = new HashSet<string>(StringComparer.Ordinal);
        private bool                   _scanning                 = false;
        private bool                   _scanDone                 = false;

        private string _outputFolder             = MakeCeilingGridsSettings.Instance.OutputFolder;
        private bool   _useCeilingGridsSubfolder = MakeCeilingGridsSettings.Instance.UseCeilingGridsSubfolder;

        // Live UI handles
        private StackPanel? _filterContainer;
        private Dispatcher? _filterDispatcher;
        private WpfTextBox? _filterFamilyBox;
        private WpfTextBox? _filterTypeBox;

        // ── ExternalEvent wiring ───────────────────────────────────────────
        private readonly MakeCeilingGridsPhase1Handler? _phase1Handler;
        private readonly Autodesk.Revit.UI.ExternalEvent?  _phase1Event;
        private readonly MakeCeilingGridsRunHandler?    _runHandler;
        private readonly Autodesk.Revit.UI.ExternalEvent?  _runEvent;

        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public MakeCeilingGridsViewModel(
            MakeCeilingGridsPhase1Handler? phase1Handler, Autodesk.Revit.UI.ExternalEvent? phase1Event,
            MakeCeilingGridsRunHandler?    runHandler,    Autodesk.Revit.UI.ExternalEvent? runEvent,
            List<DocEntry>?                availableDocs)
        {
            _phase1Handler = phase1Handler;
            _phase1Event   = phase1Event;
            _runHandler    = runHandler;
            _runEvent      = runEvent;
            _availableDocs = availableDocs ?? new List<DocEntry>();

            _docByLabel = new Dictionary<string, DocEntry>(StringComparer.Ordinal);
            foreach (var d in _availableDocs)
                _docByLabel[d.Label] = d;

            _selectedDocLabels = _availableDocs.Select(d => d.Label).ToList();
        }

        // ═════════════════════════════════════════════════════════════════════
        // GetStepContent
        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "docs":   return BuildDocsStep();
                case "filter": return BuildFilterStep();
                case "export": return BuildExportStep();
                case "run":    return BuildRunStep();
                default:       return null;
            }
        }

        // ── Step 1: Select Documents ───────────────────────────────────────
        private FrameworkElement BuildDocsStep()
        {
            var outer = new StackPanel();

            if (_availableDocs.Count == 0)
            {
                var none = new TextBlock
                {
                    Text         = "No documents available. Open the host project in Revit.",
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle    = FontStyles.Italic,
                };
                none.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                none.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                none.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                outer.Children.Add(none);
                return outer;
            }

            var tabs = new LemoineMultiSelectTabs();
            tabs.SelectionChanged += selected =>
            {
                _selectedDocLabels = new List<string>(selected);
                _scanDone  = false;
                _scanning  = false;
                _ceilingTypes.Clear();
                _excludedTypeKeys.Clear();
                OnValidationChanged();
            };
            tabs.SetGroups(
                new Dictionary<string, List<string>>
                {
                    { "Documents", _availableDocs.Select(d => d.Label).ToList() }
                },
                _selectedDocLabels);

            outer.Children.Add(tabs);
            return outer;
        }

        // ── Step 2: Filter Ceiling Types ───────────────────────────────────
        private FrameworkElement BuildFilterStep()
        {
            _filterDispatcher = Dispatcher.CurrentDispatcher;
            _filterContainer  = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            if (_scanDone && _ceilingTypes.Count > 0)
                PopulateFilterPanel();
            else if (_scanDone && _ceilingTypes.Count == 0)
                ShowFilterMessage("No ceiling types found in the selected documents.");
            else if (!_scanning)
            {
                ShowFilterMessage("Scanning ceiling types in selected documents…");
                TriggerPhase1();
            }
            else
                ShowFilterMessage("Scanning ceiling types in selected documents…");

            return _filterContainer;
        }

        private void ShowFilterMessage(string text)
        {
            _filterContainer!.Children.Clear();
            var tb = new TextBlock
            {
                Text         = text,
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, 4, 0, 0),
            };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _filterContainer.Children.Add(tb);
        }

        private void TriggerPhase1()
        {
            if (_phase1Handler == null || _phase1Event == null) return;
            _scanning = true;

            bool includeHost = false;
            var  linkInstIds = new List<ElementId>();

            foreach (var label in _selectedDocLabels)
            {
                if (!_docByLabel.TryGetValue(label, out var entry)) continue;
                if (entry.IsHost) includeHost = true;
                else              linkInstIds.Add(entry.LinkInstId);
            }

            _phase1Handler.IncludeHost = includeHost;
            _phase1Handler.LinkInstIds = linkInstIds;

            _phase1Handler.OnTypesLoaded = results =>
            {
                _filterDispatcher?.BeginInvoke((Action)(() =>
                {
                    _scanning     = false;
                    _scanDone     = true;
                    _ceilingTypes = results ?? new List<CeilingTypeEntry>();

                    if (_ceilingTypes.Count == 0)
                    {
                        ShowFilterMessage("No ceiling types found in the selected documents.");
                        OnValidationChanged();
                        return;
                    }

                    PopulateFilterPanel();
                    OnValidationChanged();
                }));
            };

            _phase1Handler.OnError = err =>
            {
                _filterDispatcher?.BeginInvoke((Action)(() =>
                {
                    _scanning = false;
                    ShowFilterMessage($"Scan error: {err}");
                }));
            };

            _phase1Event.Raise();
        }

        private void PopulateFilterPanel()
        {
            if (_filterContainer == null) return;
            _filterContainer.Children.Clear();

            var searchGrid = new WpfGrid { Margin = new Thickness(0, 0, 0, 8) };
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition());
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition());
            searchGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            searchGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var familyLbl = MakeFilterLabel("Family name");
            var typeLbl   = MakeFilterLabel("Type name");
            WpfGrid.SetRow(familyLbl, 0); WpfGrid.SetColumn(familyLbl, 0);
            WpfGrid.SetRow(typeLbl,   0); WpfGrid.SetColumn(typeLbl,   2);
            searchGrid.Children.Add(familyLbl);
            searchGrid.Children.Add(typeLbl);

            _filterFamilyBox = BuildSearchBox("Family name…");
            _filterTypeBox   = BuildSearchBox("Type name…");
            _filterFamilyBox.TextChanged += (s, e) => RefreshTypeRows();
            _filterTypeBox.TextChanged   += (s, e) => RefreshTypeRows();

            WpfGrid.SetRow(_filterFamilyBox, 1); WpfGrid.SetColumn(_filterFamilyBox, 0);
            WpfGrid.SetRow(_filterTypeBox,   1); WpfGrid.SetColumn(_filterTypeBox,   2);
            searchGrid.Children.Add(_filterFamilyBox);
            searchGrid.Children.Add(_filterTypeBox);
            _filterContainer.Children.Add(searchGrid);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 280,
            };
            var rowPanel = new StackPanel();
            scroll.Content = rowPanel;
            _filterContainer.Children.Add(scroll);

            _filterContainer.Tag = rowPanel;
            RefreshTypeRows();
        }

        private void RefreshTypeRows()
        {
            if (_filterContainer?.Tag is not StackPanel rowPanel) return;
            rowPanel.Children.Clear();

            string familyFilter = _filterFamilyBox?.Text.Trim() ?? "";
            string typeFilter   = _filterTypeBox?.Text.Trim()   ?? "";

            var filtered = _ceilingTypes
                .Where(t => (string.IsNullOrEmpty(familyFilter) ||
                             t.FamilyName.IndexOf(familyFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                         && (string.IsNullOrEmpty(typeFilter) ||
                             t.TypeName.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(t => t.Source)
                .ThenBy(t => t.FamilyName)
                .ThenBy(t => t.TypeName)
                .ToList();

            foreach (var entry in filtered)
            {
                string key      = $"{entry.Source}|{entry.FamilyName}|{entry.TypeName}";
                bool   included = !_excludedTypeKeys.Contains(key);

                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };

                var cb = new CheckBox { IsChecked = included, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                var capturedKey = key;
                cb.Checked   += (s, e) => { _excludedTypeKeys.Remove(capturedKey);   OnValidationChanged(); };
                cb.Unchecked += (s, e) => { _excludedTypeKeys.Add(capturedKey);      OnValidationChanged(); };

                var label = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming      = TextTrimming.CharacterEllipsis,
                };
                label.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                label.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                label.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

                var src   = entry.Source == "(Host document)" ? "" : $"[{entry.Source}] ";
                label.Text = $"{src}{entry.FamilyName}  —  {entry.TypeName}";

                row.Children.Add(cb);
                row.Children.Add(label);
                rowPanel.Children.Add(row);
            }

            if (filtered.Count == 0)
            {
                var none = new TextBlock
                {
                    Text      = "No ceiling types match the current filter.",
                    FontStyle = FontStyles.Italic,
                    Margin    = new Thickness(0, 4, 0, 0),
                };
                none.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                none.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                none.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                rowPanel.Children.Add(none);
            }
        }

        private static TextBlock MakeFilterLabel(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 3) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static WpfTextBox BuildSearchBox(string placeholder)
        {
            var tb = new WpfTextBox
            {
                Padding         = new Thickness(6, 3, 6, 3),
                BorderThickness = new Thickness(1),
                ToolTip         = placeholder,
            };
            tb.SetResourceReference(WpfTextBox.MinHeightProperty,   "LemoineH_Input");
            tb.SetResourceReference(WpfTextBox.BackgroundProperty,  "LemoineSelectBg");
            tb.SetResourceReference(WpfTextBox.ForegroundProperty,  "LemoineText");
            tb.SetResourceReference(WpfTextBox.BorderBrushProperty, "LemoineBorderMid");
            tb.SetResourceReference(WpfTextBox.FontFamilyProperty,  "LemoineMonoFont");
            tb.SetResourceReference(WpfTextBox.FontSizeProperty,    "LemoineFS_MD");
            return tb;
        }

        // ── Step 3: Export Location ───────────────────────────────────────
        private FrameworkElement BuildExportStep()
        {
            var outer = new StackPanel();

            var folderLabel = new TextBlock { Text = "Output folder", Margin = new Thickness(0, 0, 0, 4) };
            folderLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            folderLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            folderLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(folderLabel);

            var pathBox = new WpfTextBox
            {
                Text            = _outputFolder,
                Padding         = new Thickness(8, 4, 8, 4),
                BorderThickness = new Thickness(1),
            };
            pathBox.SetResourceReference(WpfTextBox.MinHeightProperty,   "LemoineH_Input");
            pathBox.SetResourceReference(WpfTextBox.BackgroundProperty,  "LemoineSelectBg");
            pathBox.SetResourceReference(WpfTextBox.ForegroundProperty,  "LemoineText");
            pathBox.SetResourceReference(WpfTextBox.BorderBrushProperty, "LemoineBorderMid");
            pathBox.SetResourceReference(WpfTextBox.FontFamilyProperty,  "LemoineMonoFont");
            pathBox.SetResourceReference(WpfTextBox.FontSizeProperty,    "LemoineFS_MD");
            pathBox.TextChanged += (s, e) => { _outputFolder = pathBox.Text; OnValidationChanged(); };
            outer.Children.Add(pathBox);

            var browseBtn = LemoineControlStyles.BuildButton("Browse…");
            browseBtn.Margin = new Thickness(0, 4, 0, 0);
            browseBtn.Click += (s, e) =>
            {
                var dlg = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description         = "Select export output folder",
                    SelectedPath        = _outputFolder,
                    ShowNewFolderButton = true,
                };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    pathBox.Text  = dlg.SelectedPath;
                    _outputFolder = dlg.SelectedPath;
                    OnValidationChanged();
                }
            };
            outer.Children.Add(browseBtn);

            // Split by level toggle
            var toggleItems = new List<ToggleItem>
            {
                new ToggleItem
                {
                    Id        = "subfolder",
                    Label     = "Place in a 'Ceiling Grids' subfolder",
                    Desc      = "Creates a 'Ceiling Grids' folder inside the selected location. Off = files go directly into the selected folder.",
                    DefaultOn = _useCeilingGridsSubfolder,
                },
            };
            var toggleSwitch = new LemoineToggleSwitches();
            toggleSwitch.Margin = new Thickness(0, 14, 0, 0);
            toggleSwitch.SetItems(toggleItems);
            toggleSwitch.StateChanged += state =>
            {
                if (state.TryGetValue("subfolder", out bool on))
                    _useCeilingGridsSubfolder = on;
            };
            outer.Children.Add(toggleSwitch);

            return outer;
        }

        // ── Step 5: Review & Run ───────────────────────────────────────────
        private FrameworkElement BuildRunStep()
        {
            var outer = new StackPanel();

            var grid = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddReviewCard(grid, "Ceiling Types",
                () =>
                {
                    int total    = _ceilingTypes.Count;
                    int excluded = _excludedTypeKeys.Count;
                    return total == 0 ? "Scan pending" : $"{total - excluded} / {total} included";
                }, 0, 0);

            AddReviewCard(grid, "Output Folder",
                () => string.IsNullOrEmpty(_outputFolder) ? "—"
                    : (_outputFolder.Length > 40 ? "…" + _outputFolder.Substring(_outputFolder.Length - 37) : _outputFolder),
                0, 1);

            AddReviewCard(grid, "Subfolder Mode",
                () => _useCeilingGridsSubfolder ? "'Ceiling Grids' subfolder" : "Direct to folder",
                1, 0);

            AddReviewCard(grid, "DWG Version",
                () => "DWG 2018",
                1, 1);

            AddReviewCard(grid, "Documents",
                () => _selectedDocLabels.Count == 0 ? "None"
                    : string.Join(", ", _selectedDocLabels.Take(2)) + (_selectedDocLabels.Count > 2 ? $" +{_selectedDocLabels.Count - 2}" : ""),
                2, 0);

            outer.Children.Add(grid);
            return outer;
        }

        private void AddReviewCard(WpfGrid grid, string label, Func<string> val, int row, int col)
        {
            while (grid.RowDefinitions.Count <= row)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var card = new Border
            {
                Margin          = new Thickness(col == 1 ? 4 : 0, row > 0 ? 4 : 0, 0, 0),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
                Padding         = new Thickness(10, 7, 10, 7),
            };
            card.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var lbl = new TextBlock { Text = label.ToUpper(), Margin = new Thickness(0, 0, 0, 2) };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var capturedVal = val;
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

            WpfGrid.SetRow(card, row);
            WpfGrid.SetColumn(card, col);
            grid.Children.Add(card);
        }

        // ═════════════════════════════════════════════════════════════════════
        // IsValid
        // ═════════════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "docs")   return _selectedDocLabels.Count > 0;
            if (stepId == "export") return !string.IsNullOrWhiteSpace(_outputFolder);
            return true;
        }

        // ═════════════════════════════════════════════════════════════════════
        // SummaryFor
        // ═════════════════════════════════════════════════════════════════════
        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "docs":
                    return _selectedDocLabels.Count == 0 ? "—"
                        : $"{_selectedDocLabels.Count} document(s)";
                case "filter":
                    if (!_scanDone) return "Scan pending";
                    int excluded = _excludedTypeKeys.Count;
                    return excluded == 0 ? $"All {_ceilingTypes.Count} type(s) included"
                        : $"{_ceilingTypes.Count - excluded}/{_ceilingTypes.Count} included";
                case "export":
                    return string.IsNullOrEmpty(_outputFolder) ? "—"
                        : System.IO.Path.GetFileName(_outputFolder.TrimEnd('\\', '/'));
                case "run":
                    return "Ready to run";
                default:
                    return "—";
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Run
        // ═════════════════════════════════════════════════════════════════════
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            if (_runHandler == null || _runEvent == null)
            {
                pushLog("Run handler not registered.", "fail");
                onComplete(0, 1, 0);
                return;
            }

            // Save settings
            MakeCeilingGridsSettings.Instance.OutputFolder             = _outputFolder;
            MakeCeilingGridsSettings.Instance.UseCeilingGridsSubfolder = _useCeilingGridsSubfolder;
            MakeCeilingGridsSettings.Instance.Save();

            // Build included types list
            var includedTypes = _ceilingTypes
                .Where(t => !_excludedTypeKeys.Contains($"{t.Source}|{t.FamilyName}|{t.TypeName}"))
                .ToList();

            bool includeHost = false;
            var  linkInstIds = new List<ElementId>();
            foreach (var label in _selectedDocLabels)
            {
                if (!_docByLabel.TryGetValue(label, out var entry)) continue;
                if (entry.IsHost) includeHost = true;
                else              linkInstIds.Add(entry.LinkInstId);
            }

            _runHandler.IncludeHost             = includeHost;
            _runHandler.LinkInstIds             = linkInstIds;
            _runHandler.IncludedTypes           = includedTypes;
            _runHandler.OutputFolder            = _outputFolder;
            _runHandler.UseCeilingGridsSubfolder = _useCeilingGridsSubfolder;
            _runHandler.PushLog                 = pushLog;
            _runHandler.OnProgress    = onProgress;
            _runHandler.OnComplete    = onComplete;

            pushLog("Raising Revit ExternalEvent…", "info");
            _runEvent.Raise();
        }
    }
}
