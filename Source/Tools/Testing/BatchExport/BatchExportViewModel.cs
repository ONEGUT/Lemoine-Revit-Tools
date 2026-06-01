using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

using WpfGrid       = System.Windows.Controls.Grid;
using WpfTextBox    = System.Windows.Controls.TextBox;
using WpfComboBox   = System.Windows.Controls.ComboBox;
using WpfVisibility = System.Windows.Visibility;
using WpfBrushes    = System.Windows.Media.Brushes;

namespace LemoineTools.Tools.Testing
{
    public class BatchExportViewModel : ILemoineTool, ILemoineReviewable
    {
        // ── ILemoineTool ──────────────────────────────────────────────────────
        public string Title    => "Batch Export";
        public string RunLabel => "Export in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Sheets / Views", required: true),
            new StepDefinition("S2", "Build Packs",           required: false),
            new StepDefinition("S3", "Filename & Formats",    required: true),
            new StepDefinition("S4", "PDF Settings",          required: false),
            new StepDefinition("S5", "Output",                required: true),
            new StepDefinition("S6", "Review & Run",          required: false),
        };

        public event EventHandler? ValidationChanged;
        private void Fire() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── Token definitions ─────────────────────────────────────────────────
        private static readonly (string Label, string Token)[] ExportTokens =
        {
            ("Sheet Number",  "{SheetNumber}"),
            ("Sheet Name",    "{SheetName}"),
            ("Revision",      "{Revision}"),
            ("Issue Date",    "{IssueDate}"),
            ("Project No.",   "{ProjectNumber}"),
            ("Project Name",  "{ProjectName}"),
            ("Year",          "{Year}"),
            ("Month",         "{Month}"),
            ("Day",           "{Day}"),
        };

        // ── Named struct for review cards (avoids anonymous Func<string> tuples) ──

        // ── S1 state ──────────────────────────────────────────────────────────
        private string                        _exportMode    = "Sheets";
        private List<string>                  _selectedNames = new List<string>();
        private Dictionary<string, ElementId> _nameToId      = new Dictionary<string, ElementId>();

        // ── S2 state (packs) ──────────────────────────────────────────────────
        private readonly List<SheetPackLayout> _packs      = new List<SheetPackLayout>();
        private int                            _activePack = 0;

        // ── S3 state (filename & formats) ────────────────────────────────────
        private string             _filenamePattern = BatchExportSettings.Instance.FilenamePattern;
        private bool               _pdfOn           = BatchExportSettings.Instance.ExportPdf;
        private bool               _dwgOn           = BatchExportSettings.Instance.ExportDwg;
        private string             _dwgSetup        = BatchExportSettings.Instance.DwgExportSetupName;
        private LemoineTokenInput? _tokenInput;

        // ── S4 state (PDF settings) ───────────────────────────────────────────
        private string _pdfPlacement   = BatchExportSettings.Instance.PdfPaperPlacement;
        private string _zoomSetting    = BatchExportSettings.Instance.ZoomSetting;
        private int    _zoomPct        = BatchExportSettings.Instance.ZoomPercent;
        private string _colorDepth     = BatchExportSettings.Instance.ColorDepth;
        private string _rasterQuality  = BatchExportSettings.Instance.RasterQuality;
        private string _hiddenLines    = BatchExportSettings.Instance.HiddenLinesVector
                                         ? "Vector Processing" : "Raster Processing";
        private bool   _combinePdf     = BatchExportSettings.Instance.CombinePdf;
        private bool   _viewLinksBlue  = BatchExportSettings.Instance.ViewLinksInBlue;
        private bool   _replaceHalftone = BatchExportSettings.Instance.ReplaceHalftoneWithThinLines;

        // ── S5 state (output) ─────────────────────────────────────────────────
        private string _outputFolder  = BatchExportSettings.Instance.OutputFolder;
        private bool   _splitByFormat = BatchExportSettings.Instance.SplitByFormat;

        // ── Revit data ────────────────────────────────────────────────────────
        private readonly List<ViewSheet>             _allSheets;
        private readonly List<View>                  _allViews;
        private readonly List<string>                _dwgSetupNames;
        private readonly Dictionary<ElementId, ViewSheet> _sheetById;

        // ── Preview (token preview in S3) ─────────────────────────────────────
        private string _previewSheetNumber = "A101";
        private string _previewSheetName   = "Ground Floor";

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly BatchExportEventHandler? _handler;
        private readonly ExternalEvent?           _event;

        // ── Constructor ───────────────────────────────────────────────────────
        public BatchExportViewModel(
            BatchExportEventHandler? handler,
            ExternalEvent?           externalEvent,
            List<string>             dwgSetupNames,
            List<ViewSheet>          allSheets,
            List<View>               allViews)
        {
            _handler       = handler;
            _event         = externalEvent;
            _dwgSetupNames = dwgSetupNames;
            _allSheets     = allSheets;
            _allViews      = allViews;

            // Build fast ID→Sheet lookup
            _sheetById = new Dictionary<ElementId, ViewSheet>();
            foreach (var s in _allSheets)
            {
                _sheetById[s.Id] = s;
                string key = $"{s.SheetNumber} — {s.Name}";
                if (!_nameToId.ContainsKey(key)) _nameToId[key] = s.Id;
            }
            foreach (var v in _allViews)
            {
                string key = v.Name;
                if (!_nameToId.ContainsKey(key)) _nameToId[key] = v.Id;
            }

            // Seed preview from first sheet
            if (_allSheets.Count > 0)
            {
                _previewSheetNumber = _allSheets[0].SheetNumber;
                _previewSheetName   = _allSheets[0].Name;
            }

            // Restore saved packs
            var saved = BatchExportSettings.Instance.SavedPacks;
            if (saved.Count > 0)
                foreach (var p in saved) _packs.Add(p.Clone());
            else
                _packs.Add(new SheetPackLayout("Pack 1"));
        }

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
                case "S4": return BuildS4();
                case "S5": return BuildS5();
                default:   return null;
            }
        }

        // ── S1 — Select Sheets / Views ────────────────────────────────────────
        private FrameworkElement BuildS1()
        {
            var outer = new StackPanel();

            var sheetsBtn = BuildModeButton("Sheets", _exportMode == "Sheets");
            var viewsBtn  = BuildModeButton("Views",  _exportMode == "Views");

            sheetsBtn.Click += (s, e) =>
            {
                _exportMode = "Sheets";
                RefreshModeButtons(sheetsBtn, viewsBtn, true);
                RefreshMultiSelect(outer);
                Fire();
            };
            viewsBtn.Click += (s, e) =>
            {
                _exportMode = "Views";
                RefreshModeButtons(sheetsBtn, viewsBtn, false);
                RefreshMultiSelect(outer);
                Fire();
            };

            var toggleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            toggleRow.Children.Add(sheetsBtn);
            toggleRow.Children.Add(viewsBtn);
            outer.Children.Add(toggleRow);

            var showAllCb = new CheckBox
            {
                Content   = "Show all non-template views",
                IsChecked = false,
                Margin    = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            showAllCb.SetResourceReference(CheckBox.ForegroundProperty, "LemoineText");
            showAllCb.SetResourceReference(CheckBox.FontFamilyProperty, "LemoineUiFont");
            showAllCb.SetResourceReference(CheckBox.FontSizeProperty,   "LemoineFS_SM");
            showAllCb.Tag = false;
            showAllCb.Checked   += (s, e) => { showAllCb.Tag = true;  RefreshMultiSelect(outer); Fire(); };
            showAllCb.Unchecked += (s, e) => { showAllCb.Tag = false; RefreshMultiSelect(outer); Fire(); };
            showAllCb.Visibility = _exportMode == "Views" ? WpfVisibility.Visible : WpfVisibility.Collapsed;
            outer.Tag = showAllCb;
            outer.Children.Add(showAllCb);

            var multiSelect = BuildMultiSelect(showAllCb);
            multiSelect.Tag = "multiselect";
            outer.Children.Add(multiSelect);

            return outer;
        }

        private LemoineMultiSelectTabs BuildMultiSelect(CheckBox showAllCb)
        {
            var tabs = new LemoineMultiSelectTabs();
            tabs.SetGroups(BuildGroups((bool)(showAllCb.Tag ?? false)));
            tabs.SelectionChanged += selected =>
            {
                _selectedNames = new List<string>(selected);
                Fire();
            };
            return tabs;
        }

        private void RefreshMultiSelect(StackPanel outer)
        {
            for (int i = outer.Children.Count - 1; i >= 0; i--)
            {
                if (outer.Children[i] is FrameworkElement fe && (string?)fe.Tag == "multiselect")
                {
                    outer.Children.RemoveAt(i);
                    break;
                }
            }

            var showAllCb = outer.Tag as CheckBox;
            if (showAllCb != null)
                showAllCb.Visibility = _exportMode == "Views" ? WpfVisibility.Visible : WpfVisibility.Collapsed;

            var newTabs = BuildMultiSelect(showAllCb ?? new CheckBox { Tag = false });
            newTabs.Tag = "multiselect";
            outer.Children.Add(newTabs);
            _selectedNames.Clear();
            Fire();
        }

        private Dictionary<string, List<string>> BuildGroups(bool showAll)
        {
            var groups = new Dictionary<string, List<string>>();

            if (_exportMode == "Sheets")
            {
                foreach (var sheet in _allSheets)
                {
                    string key    = $"{sheet.SheetNumber} — {sheet.Name}";
                    string prefix = GetSheetPrefix(sheet.SheetNumber);
                    if (!groups.ContainsKey(prefix)) groups[prefix] = new List<string>();
                    groups[prefix].Add(key);
                    if (!_nameToId.ContainsKey(key)) _nameToId[key] = sheet.Id;
                }
            }
            else
            {
                var allowedFamilies = new HashSet<ViewFamily>
                {
                    ViewFamily.FloorPlan, ViewFamily.CeilingPlan,
                    ViewFamily.Section,   ViewFamily.Elevation,
                    ViewFamily.Detail,
                };
                foreach (var view in _allViews)
                {
                    if (!showAll && !allowedFamilies.Contains(
                            view.ViewType == ViewType.DraftingView
                                ? ViewFamily.Detail
                                : GetViewFamily(view)))
                        continue;

                    string groupName = GetViewGroupName(view);
                    if (!groups.ContainsKey(groupName)) groups[groupName] = new List<string>();
                    string key = view.Name;
                    groups[groupName].Add(key);
                    if (!_nameToId.ContainsKey(key)) _nameToId[key] = view.Id;
                }
            }

            if (groups.Count == 0) groups["(No items)"] = new List<string>();
            return groups;
        }

        // ── S2 — Build Packs ──────────────────────────────────────────────────
        private FrameworkElement BuildS2()
        {
            if (_exportMode == "Views")
            {
                var info = new TextBlock
                {
                    Text         = "Pack organisation is only available when exporting sheets. Switch to 'Sheets' mode in Step 1 to use this feature.",
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle    = FontStyles.Italic,
                    Margin       = new Thickness(0, 4, 0, 0),
                };
                info.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                info.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                info.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                return info;
            }

            var outer = new StackPanel();

            // Optional note
            var note = new TextBlock
            {
                Text         = "Optional — leave all packs empty to export each sheet individually with the filename pattern from Step 3.",
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, 0, 0, 10),
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            note.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            note.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            outer.Children.Add(note);

            var tabsRow      = new WrapPanel { Margin = new Thickness(0, 0, 0, 0) };
            var tabContainer = new ContentControl { Margin = new Thickness(0, 0, 0, 0) };

            Action rebuildPackTabs = null!;
            rebuildPackTabs = () =>
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
                        Cursor          = Cursors.Hand,
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
                    LemoineMotion.WireHover(tab,
                        normalBgKey:     captured == _activePack ? "LemoineSelectBg" : "LemoineRaised",
                        hoverBgKey:      "LemoineAccentDim",
                        normalBorderKey: captured == _activePack ? "LemoineAccent" : "LemoineBorder",
                        hoverBorderKey:  "LemoineAccent");

                    var tabText = new TextBlock { Text = _packs[captured].PackName };
                    tabText.SetResourceReference(TextBlock.ForegroundProperty,
                        captured == _activePack ? "LemoineAccent" : "LemoineText");
                    tabText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    tabText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    tab.Child = tabText;

                    tab.MouseLeftButtonDown += (s, e) =>
                    {
                        _activePack = captured;
                        rebuildPackTabs();
                        tabContainer.Content = BuildPackEditor(rebuildPackTabs);
                    };
                    tabsRow.Children.Add(tab);
                }

                // "+ New Pack" button
                var addBtn = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(3, 3, 0, 0),
                    Padding         = new Thickness(10, 4, 10, 4),
                    Cursor          = Cursors.Hand,
                };
                addBtn.SetResourceReference(Border.BackgroundProperty,  "LemoineCanvas");
                addBtn.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
                LemoineMotion.WireHover(addBtn,
                    normalBgKey:     "LemoineCanvas",  hoverBgKey:     "LemoineAccentDim",
                    normalBorderKey: "LemoineBorderMid", hoverBorderKey: "LemoineAccent");
                var addText = new TextBlock { Text = "+ New Pack" };
                addText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                addText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                addText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                addBtn.Child = addText;
                addBtn.MouseLeftButtonDown += (s, e) =>
                {
                    _packs.Add(new SheetPackLayout($"Pack {_packs.Count + 1}"));
                    _activePack = _packs.Count - 1;
                    rebuildPackTabs();
                    tabContainer.Content = BuildPackEditor(rebuildPackTabs);
                    Fire();
                };
                tabsRow.Children.Add(addBtn);
            };

            outer.Children.Add(tabsRow);
            tabContainer.Content = BuildPackEditor(rebuildPackTabs);
            outer.Children.Add(tabContainer);
            rebuildPackTabs();

            return outer;
        }

        private FrameworkElement BuildPackEditor(Action rebuildTabs)
        {
            if (_activePack < 0 || _activePack >= _packs.Count)
            {
                var empty = new TextBlock { Text = "No pack selected." };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                empty.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                empty.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                return empty;
            }

            var pack  = _packs[_activePack];
            var outer = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(0, 3, 3, 3),
                Padding         = new Thickness(12),
            };
            outer.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            outer.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");

            var inner = new StackPanel();

            // Pack name row
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
                Fire();
            };
            nameRow.Children.Add(nameBox);

            if (_packs.Count > 1)
            {
                var delBtn = new Button
                {
                    Content         = "Remove Pack",
                    Margin          = new Thickness(8, 0, 0, 0),
                    Padding         = new Thickness(8, 0, 8, 0),
                    BorderThickness = new Thickness(1),
                    Cursor          = Cursors.Hand,
                };
                delBtn.SetResourceReference(Button.MinHeightProperty,   "LemoineH_BtnMin");
                delBtn.SetResourceReference(Button.FontSizeProperty,    "LemoineFS_SM");
                delBtn.SetResourceReference(Button.FontFamilyProperty,  "LemoineUiFont");
                delBtn.SetResourceReference(Button.ForegroundProperty,  "LemoineText");
                delBtn.SetResourceReference(Button.BackgroundProperty,  "LemoineCanvas");
                delBtn.SetResourceReference(Button.BorderBrushProperty, "LemoineBorder");
                delBtn.Template = LemoineControlStyles.BuildFlatButtonTemplate();
                delBtn.Click += (s, e) =>
                {
                    _packs.RemoveAt(_activePack);
                    _activePack = Math.Max(0, _activePack - 1);
                    rebuildTabs();
                    Fire();
                };
                nameRow.Children.Add(delBtn);
            }
            inner.Children.Add(nameRow);

            // Sheet order label
            var editorLabel = new TextBlock
            {
                Text   = "SHEET ORDER",
                Margin = new Thickness(0, 0, 0, 6),
            };
            editorLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            editorLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            editorLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            inner.Children.Add(editorLabel);

            // Build available-for-pack dict from S1 selections
            var availableForPack = new Dictionary<string, string>();
            foreach (var name in _selectedNames)
            {
                if (_nameToId.TryGetValue(name, out var id) && _sheetById.TryGetValue(id, out var sheet))
                    availableForPack[sheet.SheetNumber] = sheet.Name;
            }

            var editor = new SheetPackLayoutEditor();
            editor.Load(availableForPack, pack.SheetNumbers);
            editor.LayoutChanged += () =>
            {
                pack.SheetNumbers = new List<string>(editor.PackSheetNumbers);
                Fire();
            };
            inner.Children.Add(editor);

            outer.Child = inner;
            return outer;
        }

        // ── S3 — Filename & Formats ───────────────────────────────────────────
        private FrameworkElement BuildS3()
        {
            var outer = new StackPanel();

            // Filename pattern
            AddSectionLabel(outer, "FILENAME PATTERN");

            _tokenInput      = new LemoineTokenInput(ExportTokens, "{SheetNumber}-{SheetName}");
            _tokenInput.Text = _filenamePattern;
            outer.Children.Add(_tokenInput);

            var preview = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, 4, 0, 0),
            };
            preview.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            preview.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            preview.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            UpdatePreview(preview);
            outer.Children.Add(preview);

            _tokenInput.TextChanged += (s, e) =>
            {
                _filenamePattern = _tokenInput.Text;
                UpdatePreview(preview);
                Fire();
            };

            // Pack filename note (shown when packs have sheets)
            if (HasActivePacks())
            {
                var packNote = new TextBlock
                {
                    Text         = "Pack names are used as PDF filenames when packs are defined. The pattern above applies to DWG exports and individual PDF exports only.",
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle    = FontStyles.Italic,
                    Margin       = new Thickness(0, 6, 0, 0),
                };
                packNote.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                packNote.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                packNote.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                outer.Children.Add(packNote);
            }

            AddDivider(outer);

            // Formats
            AddSectionLabel(outer, "FORMATS");

            var formatToggles = new LemoineToggleSwitches();
            formatToggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "pdf", Label = "PDF", Desc = "Vector PDF via Revit engine",       DefaultOn = _pdfOn  },
                new ToggleItem { Id = "dwg", Label = "DWG", Desc = "AutoCAD DWG via Revit export",      DefaultOn = _dwgOn  },
                new ToggleItem { Id = "ifc", Label = "IFC", Desc = "Coming soon — not yet active",      DefaultOn = false   },
                new ToggleItem { Id = "nwc", Label = "NWC", Desc = "Coming soon — not yet active",      DefaultOn = false   },
            });
            outer.Children.Add(formatToggles);

            AddDivider(outer);

            // DWG options (shown when DWG is on)
            var dwgSection = new StackPanel { Tag = "dwgSection" };
            AddSectionLabel(dwgSection, "DWG OPTIONS");

            var setupNames = _dwgSetupNames.Count > 0
                ? _dwgSetupNames.ToArray()
                : new[] { "(No DWG setups found in project)" };
            int initIdx = setupNames.Contains(_dwgSetup) ? Array.IndexOf(setupNames, _dwgSetup) : 0;
            AddLabeledComboBox(dwgSection, "Export Setup", setupNames, initIdx,
                val => { _dwgSetup = val; Fire(); });

            dwgSection.Visibility = _dwgOn ? WpfVisibility.Visible : WpfVisibility.Collapsed;
            outer.Children.Add(dwgSection);

            formatToggles.StateChanged += state =>
            {
                _pdfOn = state.TryGetValue("pdf", out bool pdfVal) && pdfVal;
                _dwgOn = state.TryGetValue("dwg", out bool dwgVal) && dwgVal;
                dwgSection.Visibility = _dwgOn ? WpfVisibility.Visible : WpfVisibility.Collapsed;
                Fire();
            };

            return outer;
        }

        private void UpdatePreview(TextBlock preview)
        {
            var tokens = new Dictionary<string, string>
            {
                ["SheetNumber"]   = _previewSheetNumber,
                ["SheetName"]     = _previewSheetName,
                ["Revision"]      = "3",
                ["IssueDate"]     = DateTime.Now.ToString("dd/MM/yy"),
                ["ProjectNumber"] = "2024-001",
                ["ProjectName"]   = "Sample Project",
                ["Year"]          = DateTime.Now.Year.ToString(),
                ["Month"]         = DateTime.Now.Month.ToString("D2"),
                ["Day"]           = DateTime.Now.Day.ToString("D2"),
            };
            string resolved = LemoineTokenInput.Resolve(_filenamePattern, tokens);
            preview.Text = $"Preview: {SanitiseFilenamePreview(resolved)}.pdf";
        }

        private static string SanitiseFilenamePreview(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }

        // ── S4 — PDF Settings ─────────────────────────────────────────────────
        private FrameworkElement BuildS4()
        {
            var outer = new StackPanel();

            // Banner when PDF is disabled
            if (!_pdfOn)
            {
                var offNote = new TextBlock
                {
                    Text         = "PDF output is disabled in Step 3. These settings are saved but will not take effect until PDF is enabled.",
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle    = FontStyles.Italic,
                    Margin       = new Thickness(0, 0, 0, 12),
                };
                offNote.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                offNote.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                offNote.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                outer.Children.Add(offNote);
            }

            // PAGE SETUP ──────────────────────────────────────────────────────
            AddSectionLabel(outer, "PAGE SETUP");

            // Paper placement
            AddSmallLabel(outer, "Paper Placement");
            var offsetBtn  = BuildModeButton("Offset from Corner", _pdfPlacement == "Offset from Corner");
            var centerBtn  = BuildModeButton("Center",             _pdfPlacement == "Center");
            offsetBtn.Click += (s, e) => { _pdfPlacement = "Offset from Corner"; RefreshModeButtons(offsetBtn, centerBtn, true);  Fire(); };
            centerBtn.Click += (s, e) => { _pdfPlacement = "Center";             RefreshModeButtons(offsetBtn, centerBtn, false); Fire(); };
            var placementRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 8) };
            placementRow.Children.Add(offsetBtn);
            placementRow.Children.Add(centerBtn);
            outer.Children.Add(placementRow);

            var placementHint = new TextBlock
            {
                Text         = "Offset from Corner is recommended for mixed landscape/portrait exports.",
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, -4, 0, 8),
            };
            placementHint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            placementHint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            placementHint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            outer.Children.Add(placementHint);

            // Zoom type
            AddSmallLabel(outer, "Zoom");
            var fitBtn   = BuildModeButton("Fit to Page", _zoomSetting == "Fit to Page");
            var scaleBtn = BuildModeButton("Scale %",     _zoomSetting == "Scale %");
            var zoomRow  = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
            zoomRow.Children.Add(fitBtn);
            zoomRow.Children.Add(scaleBtn);
            outer.Children.Add(zoomRow);

            // Zoom stepper row (Collapsed when Fit to Page)
            var stepperRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 4, 0, 8),
                Visibility  = _zoomSetting == "Scale %" ? WpfVisibility.Visible : WpfVisibility.Collapsed,
            };
            var stepper = new LemoineInlineStepper { Value = _zoomPct, MinValue = 10, MaxValue = 500, Step = 5, Decimals = 0, ValueWidth = 48 };
            stepper.ValueChanged += (s, v) => { _zoomPct = (int)v; Fire(); };
            var pctLabel = new TextBlock
            {
                Text              = "%",
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(6, 0, 0, 0),
            };
            pctLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            pctLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            pctLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            stepperRow.Children.Add(stepper);
            stepperRow.Children.Add(pctLabel);
            outer.Children.Add(stepperRow);

            fitBtn.Click += (s, e) =>
            {
                _zoomSetting = "Fit to Page";
                RefreshModeButtons(fitBtn, scaleBtn, true);
                stepperRow.Visibility = WpfVisibility.Collapsed;
                Fire();
            };
            scaleBtn.Click += (s, e) =>
            {
                _zoomSetting = "Scale %";
                RefreshModeButtons(fitBtn, scaleBtn, false);
                stepperRow.Visibility = WpfVisibility.Visible;
                Fire();
            };

            AddDivider(outer);

            // OUTPUT QUALITY ──────────────────────────────────────────────────
            AddSectionLabel(outer, "OUTPUT QUALITY");

            AddLabeledComboBox(outer, "Color Depth",
                new[] { "Color", "Grayscale", "Black & White" },
                GetIndex(new[] { "Color", "Grayscale", "Black & White" }, _colorDepth),
                val => { _colorDepth = val; Fire(); });

            AddLabeledComboBox(outer, "Raster Quality",
                new[] { "Draft", "Low", "Medium", "High", "Presentation" },
                GetIndex(new[] { "Draft", "Low", "Medium", "High", "Presentation" }, _rasterQuality),
                val => { _rasterQuality = val; Fire(); });

            AddLabeledComboBox(outer, "Hidden Line Views",
                new[] { "Vector Processing", "Raster Processing" },
                _hiddenLines == "Vector Processing" ? 0 : 1,
                val => { _hiddenLines = val; Fire(); });

            AddDivider(outer);

            // COMBINE ─────────────────────────────────────────────────────────
            AddSectionLabel(outer, "COMBINE");

            var combineToggle = new LemoineToggleSwitches();
            combineToggle.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "combine", Label = "Combine into one PDF", DefaultOn = _combinePdf },
            });
            combineToggle.StateChanged += state => { state.TryGetValue("combine", out _combinePdf); Fire(); };
            outer.Children.Add(combineToggle);

            if (HasActivePacks())
            {
                var packCombineNote = new TextBlock
                {
                    Text         = "Each pack is always exported as its own combined PDF regardless of this setting.",
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle    = FontStyles.Italic,
                    Margin       = new Thickness(0, 2, 0, 0),
                };
                packCombineNote.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                packCombineNote.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                packCombineNote.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                outer.Children.Add(packCombineNote);
            }

            AddDivider(outer);

            // ADVANCED ────────────────────────────────────────────────────────
            AddSectionLabel(outer, "ADVANCED");

            var advToggles = new LemoineToggleSwitches();
            advToggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "viewlinks",      Label = "View links in blue",         Desc = "Render linked Revit views with a blue tint in the PDF.",      DefaultOn = _viewLinksBlue    },
                new ToggleItem { Id = "replacehalftone", Label = "Replace halftone with thin lines", Desc = "Substitute halftone patterns with thin black lines.", DefaultOn = _replaceHalftone  },
            });
            advToggles.StateChanged += state =>
            {
                state.TryGetValue("viewlinks",       out _viewLinksBlue);
                state.TryGetValue("replacehalftone", out _replaceHalftone);
                Fire();
            };
            outer.Children.Add(advToggles);

            AddDivider(outer);

            // Paper size note
            var sizeNote = new TextBlock
            {
                Text         = "Paper size and orientation are read automatically from each sheet's titleblock. Sheets without a titleblock will be flagged in the export log.",
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
            };
            sizeNote.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            sizeNote.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            sizeNote.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            outer.Children.Add(sizeNote);

            return outer;
        }

        // ── S5 — Output & Review ──────────────────────────────────────────────
        private FrameworkElement BuildS5()
        {
            var outer = new StackPanel();

            AddSectionLabel(outer, "OUTPUT FOLDER");
            BuildFolderPicker(outer);

            var splitToggle = new LemoineToggleSwitches { Margin = new Thickness(0, 6, 0, 0) };
            splitToggle.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "split", Label = "Split output into subfolders by file format", DefaultOn = _splitByFormat },
            });
            splitToggle.StateChanged += state => { state.TryGetValue("split", out _splitByFormat); Fire(); };
            outer.Children.Add(splitToggle);

            return outer;
        }

        private void BuildFolderPicker(StackPanel parent)
        {
            var folder = new LemoineFolderBrowser
            {
                Path        = _outputFolder,
                DialogTitle = "Select output folder",
            };
            folder.PathChanged += p => { _outputFolder = p; Fire(); };
            parent.Children.Add(folder);
        }


        // ═════════════════════════════════════════════════════════════════════
        //  IsValid / SummaryFor / Run
        // ═════════════════════════════════════════════════════════════════════
        // ── ILemoineReviewable (P3) — framework renders the final review step ─
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("sheets",  "Sheets / Views"),
            ("formats", "Formats"),
            ("packs",   "Packs"),
            ("quality", "Quality"),
            ("pattern", "Filename Pattern"),
            ("folder",  "Output Folder"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["sheets"]  = _selectedNames.Count == 0 ? "—" : $"{_selectedNames.Count} selected",
            ["formats"] = GetActiveFormats(),
            ["packs"]   = HasActivePacks() ? $"{_packs.Count(p => p.SheetNumbers.Count > 0)} pack(s)" : "None — individual export",
            ["quality"] = _pdfOn ? $"{_colorDepth} · {_rasterQuality}" : "PDF disabled",
            ["pattern"] = string.IsNullOrEmpty(_filenamePattern) ? "—" : _filenamePattern,
            ["folder"]  = _outputFolder.Length == 0 ? "—"
                : _outputFolder.Length > 40 ? "…" + _outputFolder.Substring(_outputFolder.Length - 37)
                : _outputFolder,
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => null;
        public string?        ReviewWarning => null;

        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedNames.Count > 0;
                case "S2": return true;
                case "S3": return _pdfOn || _dwgOn;
                case "S4": return true;
                case "S5": return !string.IsNullOrWhiteSpace(_outputFolder);
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedNames.Count == 0 ? "—"
                    : $"{_selectedNames.Count} {_exportMode.ToLower()} selected";
                case "S2": return HasActivePacks()
                    ? $"{_packs.Count(p => p.SheetNumbers.Count > 0)} pack(s)"
                    : "Individual export";
                case "S3": return GetActiveFormats() == "—"
                    ? "No formats selected"
                    : $"{GetActiveFormats()} — {_filenamePattern}";
                case "S4": return _pdfOn
                    ? $"{_hiddenLines.Split(' ')[0]} · {_rasterQuality} · {_colorDepth} · {_pdfPlacement.Split(' ')[0]}"
                    : "PDF disabled";
                case "S5": return string.IsNullOrEmpty(_outputFolder) ? "No output folder" : _outputFolder;
                default:   return "—";
            }
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            if (_handler == null || _event == null) return;

            // Persist settings
            var s = BatchExportSettings.Instance;
            s.FilenamePattern              = _filenamePattern;
            s.OutputFolder                 = _outputFolder;
            s.SplitByFormat                = _splitByFormat;
            s.ExportPdf                    = _pdfOn;
            s.ExportDwg                    = _dwgOn;
            s.CombinePdf                   = _combinePdf;
            s.PdfPaperPlacement            = _pdfPlacement;
            s.HiddenLinesVector            = _hiddenLines == "Vector Processing";
            s.DwgExportSetupName           = _dwgSetup;
            s.ColorDepth                   = _colorDepth;
            s.RasterQuality                = _rasterQuality;
            s.ZoomSetting                  = _zoomSetting;
            s.ZoomPercent                  = _zoomPct;
            s.ViewLinksInBlue              = _viewLinksBlue;
            s.ReplaceHalftoneWithThinLines = _replaceHalftone;
            s.SavedPacks                   = _packs.Select(p => p.Clone()).ToList();
            s.Save();

            // Packs to export: only when in Sheets mode and at least one pack has sheets
            var packsToExport = HasActivePacks()
                ? _packs.Where(p => p.SheetNumbers.Count > 0).ToList()
                : new List<SheetPackLayout>();

            _handler.SelectedIds              = _selectedNames
                .Where(n => _nameToId.ContainsKey(n))
                .Select(n => _nameToId[n])
                .ToList();
            _handler.ExportMode               = _exportMode;
            _handler.FilenamePattern          = _filenamePattern;
            _handler.OutputFolder             = _outputFolder;
            _handler.SplitByFormat            = _splitByFormat;
            _handler.ExportPdf                = _pdfOn;
            _handler.ExportDwg                = _dwgOn;
            _handler.CombinePdf               = _combinePdf;
            _handler.DwgSetupName             = _dwgSetup;
            _handler.PdfPlacement             = _pdfPlacement;
            _handler.HiddenLines              = _hiddenLines;
            _handler.ColorDepth               = _colorDepth;
            _handler.RasterQuality            = _rasterQuality;
            _handler.ZoomSetting              = _zoomSetting;
            _handler.ZoomPercent              = _zoomPct;
            _handler.ViewLinksInBlue          = _viewLinksBlue;
            _handler.ReplaceHalftoneWithThinLines = _replaceHalftone;
            _handler.Packs                    = packsToExport;
            _handler.PushLog                  = pushLog;
            _handler.OnProgress               = onProgress;
            _handler.OnComplete               = onComplete;

            _event.Raise();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Shared UI helpers
        // ═════════════════════════════════════════════════════════════════════

        private Button BuildModeButton(string label, bool active)
        {
            var b = new Button
            {
                Content         = label,
                Margin          = new Thickness(0, 0, 4, 0),
                BorderThickness = new Thickness(1),
                Template        = LemoineControlStyles.BuildFlatButtonTemplate(),
                Cursor          = Cursors.Hand,
            };
            b.SetResourceReference(Button.MinHeightProperty,  "LemoineH_BtnMin");
            b.SetResourceReference(Button.PaddingProperty,    "LemoineTh_BtnPad");
            b.SetResourceReference(Button.FontSizeProperty,   "LemoineFS_MD");
            b.SetResourceReference(Button.FontFamilyProperty, "LemoineUiFont");
            ApplyModeButtonStyle(b, active);
            return b;
        }

        private static void ApplyModeButtonStyle(Button b, bool active)
        {
            if (active)
            {
                b.SetResourceReference(Button.BackgroundProperty,  "LemoineAccentDim");
                b.SetResourceReference(Button.BorderBrushProperty, "LemoineAccent");
                b.SetResourceReference(Button.ForegroundProperty,  "LemoineAccent");
            }
            else
            {
                b.Background = WpfBrushes.Transparent; // ⚠ direct assignment — "Transparent" is not a resource key
                b.SetResourceReference(Button.BorderBrushProperty, "LemoineBorder");
                b.SetResourceReference(Button.ForegroundProperty,  "LemoineText");
            }
        }

        private static void RefreshModeButtons(Button a, Button b, bool aActive)
        {
            ApplyModeButtonStyle(a, aActive);
            ApplyModeButtonStyle(b, !aActive);
        }

        private static void AddSectionLabel(System.Windows.Controls.Panel parent, string text)
        {
            var lbl = new TextBlock
            {
                Text         = text,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, 0, 0, 4),
                TextWrapping = TextWrapping.Wrap,
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            parent.Children.Add(lbl);
        }

        private static void AddSmallLabel(System.Windows.Controls.Panel parent, string text)
        {
            var lbl = new TextBlock
            {
                Text   = text,
                Margin = new Thickness(0, 0, 0, 2),
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            parent.Children.Add(lbl);
        }

        private static void AddDivider(System.Windows.Controls.Panel parent)
        {
            var sep = new System.Windows.Shapes.Rectangle { Height = 1, Margin = new Thickness(0, 10, 0, 10) };
            sep.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "LemoineBorder");
            parent.Children.Add(sep);
        }

        private static void AddLabeledComboBox(System.Windows.Controls.Panel parent, string label,
            string[] items, int selectedIndex, Action<string> onChange)
        {
            var lbl = new TextBlock { Text = label, Margin = new Thickness(0, 4, 0, 2) };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            parent.Children.Add(lbl);

            var combo = new WpfComboBox
            {
                ItemsSource       = items,
                SelectedIndex     = Math.Max(0, Math.Min(selectedIndex, items.Length - 1)),
                IsEditable        = false,
                MaxDropDownHeight = 200,
                Margin            = new Thickness(0, 0, 0, 4),
            };
            combo.SetResourceReference(WpfComboBox.BackgroundProperty,  "LemoineSelectBg");
            combo.SetResourceReference(WpfComboBox.ForegroundProperty,  "LemoineText");
            combo.SetResourceReference(WpfComboBox.FontFamilyProperty,  "LemoineUiFont");
            combo.SetResourceReference(WpfComboBox.FontSizeProperty,    "LemoineFS_MD");
            LemoineControlStyles.WireComboWheelBubbling(combo); // don't eat page scroll when closed
            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedItem is string val) onChange(val);
            };
            parent.Children.Add(combo);
        }

        private string GetActiveFormats()
        {
            var fmts = new List<string>();
            if (_pdfOn) fmts.Add("PDF");
            if (_dwgOn) fmts.Add("DWG");
            return fmts.Count > 0 ? string.Join(", ", fmts) : "—";
        }

        private bool HasActivePacks() =>
            _exportMode == "Sheets" &&
            _packs.Any(p => p.SheetNumbers.Count > 0);

        private static int GetIndex(string[] items, string value)
        {
            int idx = Array.IndexOf(items, value);
            return idx >= 0 ? idx : 0;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Sheet / view grouping helpers
        // ═════════════════════════════════════════════════════════════════════

        private static string GetSheetPrefix(string sheetNumber)
        {
            if (string.IsNullOrEmpty(sheetNumber)) return "Other";
            int i = 0;
            while (i < sheetNumber.Length && char.IsLetter(sheetNumber[i])) i++;
            return i > 0 ? sheetNumber.Substring(0, i) + "-" : "Other";
        }

        private static ViewFamily GetViewFamily(View v)
        {
            if (v is ViewPlan vp) return vp.ViewType == ViewType.CeilingPlan
                ? ViewFamily.CeilingPlan : ViewFamily.FloorPlan;
            if (v is ViewSection) return v.ViewType == ViewType.Elevation
                ? ViewFamily.Elevation : ViewFamily.Section;
            return ViewFamily.Invalid;
        }

        private static string GetViewGroupName(View v)
        {
            if (v is ViewPlan vp)
                return vp.ViewType == ViewType.CeilingPlan ? "Reflected Ceiling Plans" : "Floor Plans";
            if (v is ViewSection)
                return v.ViewType == ViewType.Elevation ? "Elevations" : "Sections";
            if (v.ViewType == ViewType.DraftingView) return "Drafting Views";
            return v.ViewType.ToString();
        }
    }
}
