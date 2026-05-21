using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

using WpfGrid    = System.Windows.Controls.Grid;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace LemoineTools.Tools.Testing
{
    public class SheetPackViewModel : ILemoineTool, ILemoineToolSettings
    {
        // ── ILemoineTool ──────────────────────────────────────────────────────
        public string Title    => "Sheet Pack";
        public string RunLabel => "Stamp & Export →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Sheets",  required: true),
            new StepDefinition("S2", "Build Packs",    required: true),
            new StepDefinition("S3", "Review & Run",   required: false),
        };

        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── State ─────────────────────────────────────────────────────────────

        // All project sheets: number → name
        private readonly Dictionary<string, string> _allSheets;

        // Sheets selected in step 1
        private List<string> _selectedSheetNumbers = new List<string>();

        // Pack definitions built in step 2
        private readonly List<SheetPackLayout> _packs = new List<SheetPackLayout>();
        private int _activePack = 0;

        // Step 3 options
        private bool   _stampParams   = true;
        private bool   _exportPdf     = false;
        private string _outputFolder  = "";

        // Revit wiring
        private readonly SheetPackEventHandler? _handler;
        private readonly ExternalEvent?          _event;

        // ── Constructor ───────────────────────────────────────────────────────
        public SheetPackViewModel(
            SheetPackEventHandler? handler,
            ExternalEvent?          externalEvent,
            Dictionary<string, string>? allSheets)
        {
            _handler   = handler;
            _event     = externalEvent;
            _allSheets = allSheets ?? new Dictionary<string, string>();

            var settings = SheetPackSettings.Instance;
            _outputFolder = settings.DefaultExportFolder;
            _exportPdf    = settings.ExportPdfAfterStamp;

            // Restore saved packs if any
            if (settings.SavedPacks.Count > 0)
            {
                foreach (var p in settings.SavedPacks)
                    _packs.Add(p.Clone());
            }
            else
            {
                _packs.Add(new SheetPackLayout("Pack 1"));
            }
        }

        public SheetPackViewModel() : this(null, null, null) { }

        // ═════════════════════════════════════════════════════════════════════
        //  GetStepContent
        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildS1();
                case "S2": return BuildS2();
                case "S3": return BuildS3();
                default:   return null;
            }
        }

        // ── Step 1 — Select Sheets ────────────────────────────────────────────
        private FrameworkElement BuildS1()
        {
            if (_allSheets.Count == 0)
            {
                var msg = new TextBlock
                {
                    Text         = "No sheets found in the active document.",
                    TextWrapping = TextWrapping.Wrap,
                };
                msg.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                msg.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                msg.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                return msg;
            }

            // Group sheets by first letter of sheet number (prefix grouping)
            var groups = new Dictionary<string, List<string>>();
            foreach (var num in _allSheets.Keys.OrderBy(n => n))
            {
                string prefix = num.Length > 0 && char.IsLetter(num[0])
                    ? num[0].ToString().ToUpper()
                    : "#";
                if (!groups.ContainsKey(prefix)) groups[prefix] = new List<string>();
                // Include name in the display label
                string display = $"{num}  {_allSheets[num]}";
                groups[prefix].Add(display);
            }

            // We need a reverse map from display label → sheet number
            var labelToNum = _allSheets
                .ToDictionary(kv => $"{kv.Key}  {kv.Value}", kv => kv.Key);

            var tabs = new LemoineMultiSelectTabs();
            tabs.SetGroups(groups);

            // Pre-select previously selected sheets
            if (_selectedSheetNumbers.Count > 0)
            {
                var initialSelected = labelToNum
                    .Where(kv => _selectedSheetNumbers.Contains(kv.Value))
                    .Select(kv => kv.Key)
                    .ToList();
                if (initialSelected.Count > 0)
                    tabs.SetGroups(groups, initialSelected);
            }

            tabs.SelectionChanged += selected =>
            {
                _selectedSheetNumbers = selected
                    .Where(l => labelToNum.ContainsKey(l))
                    .Select(l => labelToNum[l])
                    .ToList();
                OnValidationChanged();
            };

            return tabs;
        }

        // ── Step 2 — Build Packs ──────────────────────────────────────────────
        private FrameworkElement BuildS2()
        {
            var outer = new StackPanel();

            // ── Pack tabs (selector row) ──────────────────────────────────────
            var tabsRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            var tabContainer = new ContentControl { Margin = new Thickness(0, 0, 0, 12) };

            Action RebuildPackTabs = null!;

            RebuildPackTabs = () =>
            {
                tabsRow.Children.Clear();

                for (int i = 0; i < _packs.Count; i++)
                {
                    int captured = i;
                    var tab = new Border
                    {
                        BorderThickness = new Thickness(1),
                        CornerRadius    = new CornerRadius(3, 3, 0, 0),
                        Margin          = new Thickness(0, 0, 4, 0),
                        Padding         = new Thickness(10, 4, 10, 4),
                        Cursor          = System.Windows.Input.Cursors.Hand,
                    };
                    if (captured == _activePack)
                    {
                        tab.SetResourceReference(Border.BackgroundProperty,  "LemoineSelectBg");
                        tab.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                    }
                    else
                    {
                        tab.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                        tab.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                    }

                    var tabText = new TextBlock { Text = _packs[captured].PackName };
                    tabText.SetResourceReference(TextBlock.ForegroundProperty, captured == _activePack ? "LemoineAccent" : "LemoineText");
                    tabText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    tabText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    tab.Child = tabText;

                    tab.MouseLeftButtonDown += (s, e) =>
                    {
                        _activePack = captured;
                        RebuildPackTabs();
                        tabContainer.Content = BuildPackEditor(RebuildPackTabs);
                    };
                    tabsRow.Children.Add(tab);
                }

                // + New Pack button
                var addBtn = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(3, 3, 0, 0),
                    Padding         = new Thickness(10, 4, 10, 4),
                    Cursor          = System.Windows.Input.Cursors.Hand,
                };
                addBtn.SetResourceReference(Border.BackgroundProperty,  "LemoineCanvas");
                addBtn.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
                var addText = new TextBlock { Text = "+ New Pack" };
                addText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                addText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                addText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                addBtn.Child = addText;
                addBtn.MouseLeftButtonDown += (s, e) =>
                {
                    _packs.Add(new SheetPackLayout($"Pack {_packs.Count + 1}"));
                    _activePack = _packs.Count - 1;
                    RebuildPackTabs();
                    tabContainer.Content = BuildPackEditor(RebuildPackTabs);
                    OnValidationChanged();
                };
                tabsRow.Children.Add(addBtn);
            };

            outer.Children.Add(tabsRow);

            // ── Pack editor panel ─────────────────────────────────────────────
            tabContainer.Content = BuildPackEditor(RebuildPackTabs);
            outer.Children.Add(tabContainer);

            RebuildPackTabs();
            return outer;
        }

        private FrameworkElement BuildPackEditor(Action rebuildTabs)
        {
            if (_activePack < 0 || _activePack >= _packs.Count)
            {
                var msg = new TextBlock { Text = "No pack selected." };
                msg.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                msg.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                msg.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                return msg;
            }

            var pack  = _packs[_activePack];
            var outer = new StackPanel();

            var outerBorder = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(0, 3, 3, 3),
                Padding         = new Thickness(12),
            };
            outerBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");

            var innerStack = new StackPanel();

            // ── Pack name field ───────────────────────────────────────────────
            var nameRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };

            var nameLabel = new TextBlock
            {
                Text              = "PACK NAME",
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 8, 0),
            };
            nameLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            nameLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            nameLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            nameRow.Children.Add(nameLabel);

            var nameBox = new WpfTextBox
            {
                Text    = pack.PackName,
                Width   = 180,
                Padding = new Thickness(6, 2, 6, 2),
            };
            nameBox.SetResourceReference(WpfTextBox.BackgroundProperty,  "LemoineSelectBg");
            nameBox.SetResourceReference(WpfTextBox.ForegroundProperty,  "LemoineText");
            nameBox.SetResourceReference(WpfTextBox.BorderBrushProperty, "LemoineBorderMid");
            nameBox.SetResourceReference(WpfTextBox.FontFamilyProperty,  "LemoineUiFont");
            nameBox.SetResourceReference(WpfTextBox.FontSizeProperty,    "LemoineFS_SM");
            nameBox.SetResourceReference(WpfTextBox.MinHeightProperty,   "LemoineH_Input");
            nameBox.TextChanged += (s, e) =>
            {
                pack.PackName = nameBox.Text;
                rebuildTabs();
                OnValidationChanged();
            };
            nameRow.Children.Add(nameBox);

            // Delete pack button (hidden if only one pack)
            if (_packs.Count > 1)
            {
                var delBtn = new Button
                {
                    Content         = "Remove Pack",
                    Margin          = new Thickness(8, 0, 0, 0),
                    Padding         = new Thickness(8, 0, 8, 0),
                    BorderThickness = new Thickness(1),
                    Cursor          = System.Windows.Input.Cursors.Hand,
                };
                delBtn.SetResourceReference(Button.MinHeightProperty,  "LemoineH_BtnMin");
                delBtn.SetResourceReference(Button.FontSizeProperty,   "LemoineFS_SM");
                delBtn.SetResourceReference(Button.FontFamilyProperty, "LemoineUiFont");
                delBtn.SetResourceReference(Button.ForegroundProperty, "LemoineText");
                delBtn.SetResourceReference(Button.BackgroundProperty, "LemoineCanvas");
                delBtn.SetResourceReference(Button.BorderBrushProperty, "LemoineBorder");
                delBtn.Click += (s, e) =>
                {
                    _packs.RemoveAt(_activePack);
                    _activePack = Math.Max(0, _activePack - 1);
                    rebuildTabs();
                    OnValidationChanged();
                };
                nameRow.Children.Add(delBtn);
            }
            innerStack.Children.Add(nameRow);

            // ── Issue purpose field ───────────────────────────────────────────
            var purposeLabel = new TextBlock
            {
                Text   = "ISSUE PURPOSE",
                Margin = new Thickness(0, 0, 0, 4),
            };
            purposeLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            purposeLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            purposeLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            innerStack.Children.Add(purposeLabel);

            var purposeBox = new WpfTextBox
            {
                Text    = pack.IssuePurpose,
                Padding = new Thickness(6, 2, 6, 2),
                Margin  = new Thickness(0, 0, 0, 10),
            };
            purposeBox.SetResourceReference(WpfTextBox.BackgroundProperty,  "LemoineSelectBg");
            purposeBox.SetResourceReference(WpfTextBox.ForegroundProperty,  "LemoineText");
            purposeBox.SetResourceReference(WpfTextBox.BorderBrushProperty, "LemoineBorderMid");
            purposeBox.SetResourceReference(WpfTextBox.FontFamilyProperty,  "LemoineUiFont");
            purposeBox.SetResourceReference(WpfTextBox.FontSizeProperty,    "LemoineFS_SM");
            purposeBox.SetResourceReference(WpfTextBox.MinHeightProperty,   "LemoineH_Input");
            purposeBox.TextChanged += (s, e) =>
            {
                pack.IssuePurpose = purposeBox.Text;
                OnValidationChanged();
            };
            innerStack.Children.Add(purposeBox);

            // ── Sheet layout editor ───────────────────────────────────────────
            var editorLabel = new TextBlock
            {
                Text   = "SHEET ORDER",
                Margin = new Thickness(0, 0, 0, 6),
            };
            editorLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            editorLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            editorLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            innerStack.Children.Add(editorLabel);

            // Only show sheets selected in step 1 as available
            var availableForPack = new Dictionary<string, string>();
            foreach (var num in _selectedSheetNumbers.Count > 0
                                 ? _selectedSheetNumbers
                                 : _allSheets.Keys.ToList())
            {
                if (_allSheets.TryGetValue(num, out var name))
                    availableForPack[num] = name;
            }

            var editor = new SheetPackLayoutEditor();
            editor.Load(availableForPack, pack.SheetNumbers);
            editor.LayoutChanged += () =>
            {
                pack.SheetNumbers = new List<string>(editor.PackSheetNumbers);
                OnValidationChanged();
            };
            innerStack.Children.Add(editor);

            outerBorder.Child = innerStack;
            return outerBorder;
        }

        // ── Step 3 — Review & Run ─────────────────────────────────────────────
        private FrameworkElement BuildS3()
        {
            var outer = new StackPanel();

            // ── Options ───────────────────────────────────────────────────────
            var optionsLabel = new TextBlock
            {
                Text   = "OPTIONS",
                Margin = new Thickness(0, 0, 0, 6),
            };
            optionsLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            optionsLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            optionsLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(optionsLabel);

            var toggles = new LemoineToggleSwitches();
            toggles.SetItems(new System.Collections.Generic.List<ToggleItem>
            {
                new ToggleItem
                {
                    Id        = "stamp",
                    Label     = "Stamp sheet parameters",
                    Desc      = "Write the pack name and issue purpose to matching Revit parameters on each sheet.",
                    DefaultOn = _stampParams,
                },
                new ToggleItem
                {
                    Id        = "pdf",
                    Label     = "Export to PDF after stamp",
                    Desc      = "Export each pack's sheets as a combined PDF into the output folder.",
                    DefaultOn = _exportPdf,
                },
            });
            toggles.StateChanged += state =>
            {
                state.TryGetValue("stamp", out _stampParams);
                state.TryGetValue("pdf",   out _exportPdf);
                OnValidationChanged();
            };
            outer.Children.Add(toggles);

            // ── Output folder (PDF) ───────────────────────────────────────────
            var folderSection = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

            var folderLabel = new TextBlock
            {
                Text   = "OUTPUT FOLDER (PDF EXPORT)",
                Margin = new Thickness(0, 0, 0, 4),
            };
            folderLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            folderLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            folderLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            folderSection.Children.Add(folderLabel);

            var folderRow = new WrapPanel { Orientation = Orientation.Horizontal };

            var folderBox = new WpfTextBox
            {
                Text    = _outputFolder,
                MinWidth = 250,
                Padding  = new Thickness(6, 2, 6, 2),
                IsReadOnly = true,
            };
            folderBox.SetResourceReference(WpfTextBox.BackgroundProperty,  "LemoineSelectBg");
            folderBox.SetResourceReference(WpfTextBox.ForegroundProperty,  "LemoineText");
            folderBox.SetResourceReference(WpfTextBox.BorderBrushProperty, "LemoineBorderMid");
            folderBox.SetResourceReference(WpfTextBox.FontFamilyProperty,  "LemoineMonoFont");
            folderBox.SetResourceReference(WpfTextBox.FontSizeProperty,    "LemoineFS_SM");
            folderBox.SetResourceReference(WpfTextBox.MinHeightProperty,   "LemoineH_Input");
            folderRow.Children.Add(folderBox);

            var browseBtn = LemoineControlStyles.BuildButton("Browse…", LemoineControlStyles.LemoineButtonVariant.Ghost);
            browseBtn.Margin = new Thickness(6, 0, 0, 0);
            browseBtn.Click += (s, e) =>
            {
                var dlg = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description         = "Select PDF output folder",
                    ShowNewFolderButton = true,
                };
                if (!string.IsNullOrEmpty(_outputFolder) && Directory.Exists(_outputFolder))
                    dlg.SelectedPath = _outputFolder;

                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    _outputFolder    = dlg.SelectedPath;
                    folderBox.Text   = _outputFolder;
                    OnValidationChanged();
                }
            };
            folderRow.Children.Add(browseBtn);
            folderSection.Children.Add(folderRow);
            outer.Children.Add(folderSection);

            // ── Review cards ──────────────────────────────────────────────────
            var divider = new Border
            {
                Height          = 1,
                Margin          = new Thickness(0, 14, 0, 14),
                BorderThickness = new Thickness(0),
            };
            divider.SetResourceReference(Border.BackgroundProperty, "LemoineBorder");
            outer.Children.Add(divider);

            var reviewGrid = new WpfGrid();
            reviewGrid.ColumnDefinitions.Add(new ColumnDefinition());
            reviewGrid.ColumnDefinitions.Add(new ColumnDefinition());
            reviewGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            reviewGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddCard(reviewGrid, "Selected Sheets",
                () => _selectedSheetNumbers.Count == 0 ? "—" : $"{_selectedSheetNumbers.Count} sheet(s)",
                0, 0);
            AddCard(reviewGrid, "Packs",
                () => _packs.Count == 0 ? "—" : $"{_packs.Count} pack(s)  |  {_packs.Sum(p => p.SheetNumbers.Count)} total entries",
                0, 1);
            AddCard(reviewGrid, "Stamp Parameters",
                () => _stampParams ? "Yes — will write pack name & purpose" : "No",
                1, 0);
            AddCard(reviewGrid, "PDF Export",
                () => _exportPdf
                    ? (string.IsNullOrEmpty(_outputFolder) ? "Yes — (no folder set)" : $"Yes → {Path.GetFileName(_outputFolder.TrimEnd('\\', '/'))}")
                    : "No",
                1, 1);

            outer.Children.Add(reviewGrid);
            return outer;
        }

        private void AddCard(WpfGrid grid, string label, Func<string> val, int row, int col)
        {
            var card = new Border
            {
                Margin          = new Thickness(col == 0 ? 0 : 4, row == 0 ? 0 : 4, 0, 0),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
            };
            card.SetResourceReference(Border.PaddingProperty,    "LemoineTh_CardPad");
            card.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var lbl = new TextBlock { Text = label.ToUpper(), Margin = new Thickness(0, 0, 0, 2) };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var valText = new TextBlock { FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap };
            valText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            valText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            valText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            valText.Text = val();
            ValidationChanged += (s, e) => valText.Text = val();

            var sp = new StackPanel();
            sp.Children.Add(lbl);
            sp.Children.Add(valText);
            card.Child = sp;
            WpfGrid.SetRow(card, row);
            WpfGrid.SetColumn(card, col);
            grid.Children.Add(card);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  IsValid / SummaryFor / Run
        // ═════════════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _selectedSheetNumbers.Count > 0;
            if (stepId == "S2")
                return _packs.Count > 0 &&
                       _packs.All(p => !string.IsNullOrWhiteSpace(p.PackName)) &&
                       _packs.Sum(p => p.SheetNumbers.Count) > 0;
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1")
                return _selectedSheetNumbers.Count == 0 ? "—"
                    : $"{_selectedSheetNumbers.Count} sheet(s) selected";
            if (stepId == "S2")
                return _packs.Count == 0 ? "—"
                    : $"{_packs.Count} pack(s)";
            if (stepId == "S3")
                return "Ready to run";
            return "—";
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            if (_handler == null || _event == null) return;

            // Persist settings
            var s = SheetPackSettings.Instance;
            s.DefaultExportFolder  = _outputFolder;
            s.ExportPdfAfterStamp  = _exportPdf;
            s.SavedPacks           = _packs.Select(p => p.Clone()).ToList();
            s.Save();

            var settings = SheetPackSettings.Instance;
            _handler.Packs                = new List<SheetPackLayout>(_packs);
            _handler.PackNameParameter    = settings.PackNameParameter;
            _handler.IssuePurposeParameter = settings.IssuePurposeParameter;
            _handler.ExportPdf            = _exportPdf;
            _handler.OutputFolder         = _outputFolder;
            _handler.PushLog              = pushLog;
            _handler.OnProgress           = onProgress;
            _handler.OnComplete           = onComplete;

            _event.Raise();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  ILemoineToolSettings
        // ═════════════════════════════════════════════════════════════════════
        public LemoineToolSettingsSpec GetSettingsSpec()
        {
            var s = SheetPackSettings.Instance;
            return new LemoineToolSettingsSpec
            {
                Id          = "tw",
                Label       = "Sheet Pack",
                Icon        = "",
                Description = "Parameter names and default export folder.",
                Groups = new List<LemoineSettingsGroup>
                {
                    new LemoineSettingsGroup
                    {
                        Id            = "G1",
                        Title         = "Parameter Mapping",
                        Hint          = "Names of the Revit parameters to write pack metadata into.",
                        OpenByDefault = true,
                        Settings      = new List<LemoineSettingDef>
                        {
                            new LemoineSettingDef
                            {
                                Id      = "packNameParam",
                                Label   = "Pack Name Parameter",
                                Hint    = "Revit parameter name on ViewSheet to receive the pack name (e.g. \"Issue Set\").",
                                Kind    = "text",
                                Default = s.PackNameParameter,
                                Options = new TextOpts { Placeholder = "Issue Set" },
                            },
                            new LemoineSettingDef
                            {
                                Id      = "purposeParam",
                                Label   = "Issue Purpose Parameter",
                                Hint    = "Revit parameter name on ViewSheet to receive the issue purpose.",
                                Kind    = "text",
                                Default = s.IssuePurposeParameter,
                                Options = new TextOpts { Placeholder = "Issue Purpose" },
                            },
                        },
                    },
                    new LemoineSettingsGroup
                    {
                        Id       = "G2",
                        Title    = "Export Defaults",
                        Settings = new List<LemoineSettingDef>
                        {
                            new LemoineSettingDef
                            {
                                Id      = "exportFolder",
                                Label   = "Default Export Folder",
                                Hint    = "Pre-filled output folder path for PDF export.",
                                Kind    = "file",
                                Default = s.DefaultExportFolder,
                                Options = new FileOpts { Placeholder = "Select folder…" },
                            },
                        },
                    },
                },
            };
        }

        public void ApplySettings(string groupId, string settingId, object value)
        {
            var s   = SheetPackSettings.Instance;
            var str = value?.ToString() ?? "";
            switch (settingId)
            {
                case "packNameParam":
                    s.PackNameParameter = str;
                    break;
                case "purposeParam":
                    s.IssuePurposeParameter = str;
                    break;
                case "exportFolder":
                    s.DefaultExportFolder = str;
                    _outputFolder = str;
                    break;
            }
            s.Save();
            OnValidationChanged();
        }
    }
}
