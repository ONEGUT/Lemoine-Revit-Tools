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
    /// <summary>
    /// View model for the Batch Export tool (Tx).
    /// 3-step wizard: Select Sheets/Views → Filename &amp; Formats → Output &amp; Review.
    /// </summary>
    public class BatchExportViewModel : ILemoineTool, ILemoineToolSettings
    {
        public string Title    => "Batch Export";
        public string RunLabel => "Export in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Sheets / Views",  required: true),
            new StepDefinition("S2", "Filename & Formats",     required: true),
            new StepDefinition("S3", "Output & Review",        required: true),
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

        // ── State ─────────────────────────────────────────────────────────────
        private string                    _exportMode       = "Sheets"; // "Sheets" | "Views"
        private List<string>              _selectedNames    = new List<string>();
        private Dictionary<string, ElementId> _nameToId    = new Dictionary<string, ElementId>();
        private string                    _filenamePattern  = BatchExportSettings.Instance.FilenamePattern;
        private bool                      _pdfOn            = BatchExportSettings.Instance.ExportPdf;
        private bool                      _dwgOn            = BatchExportSettings.Instance.ExportDwg;
        private bool                      _combinePdf       = BatchExportSettings.Instance.CombinePdf;
        private string                    _pdfPlacement     = BatchExportSettings.Instance.PdfPaperPlacement;
        private string                    _hiddenLines      = BatchExportSettings.Instance.HiddenLinesVector
                                                              ? "Vector Processing" : "Raster Processing";
        private string                    _dwgSetup         = BatchExportSettings.Instance.DwgExportSetupName;
        private string                    _outputFolder     = BatchExportSettings.Instance.OutputFolder;
        private bool                      _splitByFormat    = BatchExportSettings.Instance.SplitByFormat;

        // ── Data from Revit (populated via constructor) ───────────────────────
        private readonly List<ViewSheet>  _allSheets;
        private readonly List<View>       _allViews;
        private readonly List<string>     _dwgSetupNames;

        // ── Step 2 token input (stored to access Text from Run()) ─────────────
        private LemoineTokenInput?        _tokenInput;

        // ── Preview element (first selected) ─────────────────────────────────
        private string _previewSheetNumber = "A101";
        private string _previewSheetName   = "Ground Floor";

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly BatchExportEventHandler? _handler;
        private readonly ExternalEvent?           _event;

        // ── Constructor ───────────────────────────────────────────────────────

        /// <summary>
        /// Main constructor — pass null handler/event for settings-only instantiation.
        /// </summary>
        public BatchExportViewModel(
            BatchExportEventHandler? handler,
            ExternalEvent?           externalEvent,
            List<string>?            dwgSetupNames)
        {
            _handler       = handler;
            _event         = externalEvent;
            _dwgSetupNames = dwgSetupNames ?? new List<string>();
            _allSheets     = new List<ViewSheet>();
            _allViews      = new List<View>();
        }

        /// <summary>
        /// Full constructor — pass all Revit data collected on the main thread.
        /// </summary>
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

            // Seed name→id map
            foreach (var s in _allSheets)
            {
                string key = $"{s.SheetNumber} — {s.Name}";
                if (!_nameToId.ContainsKey(key)) _nameToId[key] = s.Id;
            }
            foreach (var v in _allViews)
            {
                string key = v.Name;
                if (!_nameToId.ContainsKey(key)) _nameToId[key] = v.Id;
            }

            // Seed preview names from first sheet
            if (_allSheets.Count > 0)
            {
                _previewSheetNumber = _allSheets[0].SheetNumber;
                _previewSheetName   = _allSheets[0].Name;
            }
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
                default:   return null;
            }
        }

        // ── Step 1 — Select Sheets / Views ────────────────────────────────────
        private FrameworkElement BuildS1()
        {
            var outer = new StackPanel();

            // Mode toggle row
            var modeRow = new WpfGrid();
            modeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            modeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            modeRow.Margin = new Thickness(0, 0, 0, 8);

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

            // Show-all checkbox (for Views mode)
            var showAllCb = new CheckBox
            {
                Content  = "Show all non-template views",
                IsChecked = false,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin   = new Thickness(0, 0, 0, 6),
            };
            showAllCb.SetResourceReference(CheckBox.ForegroundProperty,  "LemoineText");
            showAllCb.SetResourceReference(CheckBox.FontFamilyProperty,  "LemoineUiFont");
            showAllCb.SetResourceReference(CheckBox.FontSizeProperty,    "LemoineFS_SM");
            showAllCb.Tag = false;
            showAllCb.Checked   += (s, e) => { showAllCb.Tag = true;  RefreshMultiSelect(outer); Fire(); };
            showAllCb.Unchecked += (s, e) => { showAllCb.Tag = false; RefreshMultiSelect(outer); Fire(); };
            showAllCb.Visibility = _exportMode == "Views" ? WpfVisibility.Visible : WpfVisibility.Collapsed;
            outer.Tag = showAllCb;
            outer.Children.Add(showAllCb);

            // MultiSelectTabs placeholder
            var multiSelect = BuildMultiSelect(showAllCb);
            multiSelect.Tag = "multiselect";
            outer.Children.Add(multiSelect);

            return outer;
        }

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
                b.Background = WpfBrushes.Transparent;
                b.SetResourceReference(Button.BorderBrushProperty, "LemoineBorder");
                b.SetResourceReference(Button.ForegroundProperty,  "LemoineText");
            }
        }

        private static void RefreshModeButtons(Button sheets, Button views, bool sheetsActive)
        {
            ApplyModeButtonStyle(sheets, sheetsActive);
            ApplyModeButtonStyle(views,  !sheetsActive);
        }

        private LemoineMultiSelectTabs BuildMultiSelect(CheckBox showAllCb)
        {
            var tabs = new LemoineMultiSelectTabs();
            var groups = BuildGroups((bool)(showAllCb.Tag ?? false));
            tabs.SetGroups(groups);
            tabs.SelectionChanged += selected =>
            {
                _selectedNames = new List<string>(selected);
                Fire();
            };
            return tabs;
        }

        private void RefreshMultiSelect(StackPanel outer)
        {
            // Remove old multiselect, rebuild from current mode
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
                    string key = $"{sheet.SheetNumber} — {sheet.Name}";
                    // Group by first letter-prefix of sheet number (A-, S-, M-, etc.)
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
                    if (!showAll && !allowedFamilies.Contains(view.ViewType == ViewType.DraftingView
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

            if (groups.Count == 0)
                groups["(No items)"] = new List<string>();

            return groups;
        }

        private static string GetSheetPrefix(string sheetNumber)
        {
            if (string.IsNullOrEmpty(sheetNumber)) return "Other";
            // Take leading letters
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

        // ── Step 2 — Filename & Formats ───────────────────────────────────────
        private FrameworkElement BuildS2()
        {
            var outer = new StackPanel();

            // ── Section A: Filename Pattern ───────────────────────────────────
            AddSectionLabel(outer, "FILENAME PATTERN");

            _tokenInput = new LemoineTokenInput(ExportTokens, "{SheetNumber}-{SheetName}");
            _tokenInput.Text = _filenamePattern;
            outer.Children.Add(_tokenInput);

            // Live preview
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

            _tokenInput.TextChanged += (s, e) =>
            {
                _filenamePattern = _tokenInput.Text;
                UpdatePreview(preview);
                Fire();
            };
            outer.Children.Add(preview);

            AddDivider(outer);

            // ── Section B: Formats ────────────────────────────────────────────
            AddSectionLabel(outer, "FORMATS");

            var formatToggles = new LemoineToggleSwitches();
            formatToggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "pdf", Label = "PDF",       Desc = "Vector PDF via Revit engine",       DefaultOn = _pdfOn  },
                new ToggleItem { Id = "dwg", Label = "DWG",       Desc = "AutoCAD DWG via Revit export",      DefaultOn = _dwgOn  },
                new ToggleItem { Id = "ifc", Label = "IFC",       Desc = "Coming soon — not yet active",     DefaultOn = false   },
                new ToggleItem { Id = "nwc", Label = "NWC",       Desc = "Coming soon — not yet active",     DefaultOn = false   },
            });

            // Dim out IFC/NWC rows
            formatToggles.IsEnabled = true;
            formatToggles.Loaded += (s, e) =>
            {
                // Disable IFC/NWC items visually after render — they show but don't toggle
                // LemoineToggleSwitches doesn't expose per-item disable; handled by opacity
            };

            formatToggles.StateChanged += state =>
            {
                _pdfOn = state.TryGetValue("pdf", out bool pdfVal) && pdfVal;
                _dwgOn = state.TryGetValue("dwg", out bool dwgVal) && dwgVal;
                Fire();
            };
            outer.Children.Add(formatToggles);

            AddDivider(outer);

            // ── Section C: PDF Options ────────────────────────────────────────
            var pdfSection = new StackPanel { Tag = "pdfSection" };
            AddSectionLabel(pdfSection, "PDF OPTIONS");
            BuildPdfOptions(pdfSection);
            pdfSection.Visibility = _pdfOn ? WpfVisibility.Visible : WpfVisibility.Collapsed;
            outer.Children.Add(pdfSection);

            // ── Section D: DWG Options ────────────────────────────────────────
            var dwgSection = new StackPanel { Tag = "dwgSection" };
            AddSectionLabel(dwgSection, "DWG OPTIONS");
            BuildDwgOptions(dwgSection);
            dwgSection.Visibility = _dwgOn ? WpfVisibility.Visible : WpfVisibility.Collapsed;
            outer.Children.Add(dwgSection);

            // Wire visibility to toggle state
            formatToggles.StateChanged += state =>
            {
                bool pdf = state.TryGetValue("pdf", out bool pv) && pv;
                bool dwg = state.TryGetValue("dwg", out bool dv) && dv;
                pdfSection.Visibility = pdf ? WpfVisibility.Visible : WpfVisibility.Collapsed;
                dwgSection.Visibility = dwg ? WpfVisibility.Visible : WpfVisibility.Collapsed;
            };

            return outer;
        }

        private void BuildPdfOptions(StackPanel parent)
        {
            // Combine PDF
            var combineCb = new CheckBox
            {
                Content   = "Merge all sheets into one PDF file",
                IsChecked = _combinePdf,
                Margin    = new Thickness(0, 0, 0, 6),
            };
            combineCb.SetResourceReference(CheckBox.ForegroundProperty,  "LemoineText");
            combineCb.SetResourceReference(CheckBox.FontFamilyProperty,  "LemoineUiFont");
            combineCb.SetResourceReference(CheckBox.FontSizeProperty,    "LemoineFS_MD");
            combineCb.Checked   += (s, e) => { _combinePdf = true;  Fire(); };
            combineCb.Unchecked += (s, e) => { _combinePdf = false; Fire(); };
            parent.Children.Add(combineCb);

            // Paper placement
            AddLabeledComboBox(parent, "Paper Placement",
                new[] { "Center", "Offset from Corner" },
                _pdfPlacement == "Center" ? 0 : 1,
                val => { _pdfPlacement = val; Fire(); });

            // Hidden line rendering
            AddLabeledComboBox(parent, "Hidden Line Views",
                new[] { "Vector Processing", "Raster Processing" },
                _hiddenLines == "Vector Processing" ? 0 : 1,
                val => { _hiddenLines = val; Fire(); });
        }

        private void BuildDwgOptions(StackPanel parent)
        {
            var setupNames = _dwgSetupNames.Count > 0
                ? _dwgSetupNames.ToArray()
                : new[] { "(No DWG setups found in project)" };

            int initIdx = setupNames.Contains(_dwgSetup)
                ? Array.IndexOf(setupNames, _dwgSetup)
                : 0;

            AddLabeledComboBox(parent, "Export Setup", setupNames, initIdx,
                val => { _dwgSetup = val; Fire(); });
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
            preview.Text = $"Preview: {SanitizeFilenamePreview(resolved)}.pdf";
        }

        private static string SanitizeFilenamePreview(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }

        // ── Step 3 — Output & Review ──────────────────────────────────────────
        private FrameworkElement BuildS3()
        {
            var outer = new StackPanel();

            // ── Section A: Output Folder ──────────────────────────────────────
            AddSectionLabel(outer, "OUTPUT FOLDER");
            BuildFolderPicker(outer);

            // Split by format
            var splitCb = new CheckBox
            {
                Content   = "Split output into subfolders by file format",
                IsChecked = _splitByFormat,
                Margin    = new Thickness(0, 6, 0, 0),
            };
            splitCb.SetResourceReference(CheckBox.ForegroundProperty,  "LemoineText");
            splitCb.SetResourceReference(CheckBox.FontFamilyProperty,  "LemoineUiFont");
            splitCb.SetResourceReference(CheckBox.FontSizeProperty,    "LemoineFS_MD");
            splitCb.Checked   += (s, e) => { _splitByFormat = true;  Fire(); };
            splitCb.Unchecked += (s, e) => { _splitByFormat = false; Fire(); };
            outer.Children.Add(splitCb);

            AddDivider(outer);

            // ── Section B: Review Summary ─────────────────────────────────────
            AddSectionLabel(outer, "REVIEW SUMMARY");

            var grid = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());

            var cardDefs = new (string Label, Func<string> Value)[]
            {
                ("Sheets / Views", () => _selectedNames.Count == 0 ? "—" : $"{_selectedNames.Count} selected"),
                ("Formats",        () => GetActiveFormats()),
                ("Filename Pattern", () => string.IsNullOrEmpty(_filenamePattern) ? "—" : _filenamePattern),
                ("Output Folder",  () => _outputFolder.Length > 40
                    ? "…" + _outputFolder.Substring(_outputFolder.Length - 37)
                    : (_outputFolder.Length == 0 ? "—" : _outputFolder)),
                ("Combine PDF",    () => _combinePdf ? "Yes" : "No"),
            };

            for (int i = 0; i < cardDefs.Length; i++)
            {
                int row = i / 2;
                int col = i % 2;
                if (grid.RowDefinitions.Count <= row)
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                AddReviewCard(grid, cardDefs[i].Label, cardDefs[i].Value, row, col);
            }
            outer.Children.Add(grid);

            return outer;
        }

        private void BuildFolderPicker(StackPanel parent)
        {
            var pathBox = new WpfTextBox
            {
                Text        = _outputFolder,
                Padding     = new Thickness(8, 4, 8, 4),
                BorderThickness = new Thickness(1),
            };
            pathBox.SetResourceReference(WpfTextBox.MinHeightProperty,    "LemoineH_Input");
            pathBox.SetResourceReference(WpfTextBox.BackgroundProperty,   "LemoineSelectBg");
            pathBox.SetResourceReference(WpfTextBox.ForegroundProperty,   "LemoineText");
            pathBox.SetResourceReference(WpfTextBox.BorderBrushProperty,  "LemoineBorderMid");
            pathBox.SetResourceReference(WpfTextBox.FontFamilyProperty,   "LemoineMonoFont");
            pathBox.SetResourceReference(WpfTextBox.FontSizeProperty,     "LemoineFS_MD");
            pathBox.TextChanged += (s, e) => { _outputFolder = pathBox.Text; Fire(); };

            var browseBtn = LemoineControlStyles.BuildButton("Browse…");
            browseBtn.Margin = new Thickness(0, 4, 0, 0);
            browseBtn.Click += (s, e) =>
            {
                var dlg = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description  = "Select output folder",
                    SelectedPath = _outputFolder,
                    ShowNewFolderButton = true,
                };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    pathBox.Text  = dlg.SelectedPath;
                    _outputFolder = dlg.SelectedPath;
                    Fire();
                }
            };

            parent.Children.Add(pathBox);
            parent.Children.Add(browseBtn);
        }

        private void AddReviewCard(WpfGrid grid, string label, Func<string> valueFn, int row, int col)
        {
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

            var val = new TextBlock { FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap };
            val.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            val.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            val.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            val.Text = valueFn();
            ValidationChanged += (s, e) => val.Text = valueFn();

            var sp = new StackPanel();
            sp.Children.Add(lbl);
            sp.Children.Add(val);
            card.Child = sp;
            WpfGrid.SetRow(card, row);
            WpfGrid.SetColumn(card, col);
            grid.Children.Add(card);
        }

        private string GetActiveFormats()
        {
            var fmts = new List<string>();
            if (_pdfOn) fmts.Add("PDF");
            if (_dwgOn) fmts.Add("DWG");
            return fmts.Count > 0 ? string.Join(", ", fmts) : "—";
        }

        // ── Shared UI helpers ─────────────────────────────────────────────────

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

        private static void AddDivider(System.Windows.Controls.Panel parent)
        {
            var sep = new System.Windows.Shapes.Rectangle
            {
                Height = 1,
                Margin = new Thickness(0, 10, 0, 10),
            };
            sep.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "LemoineBorder");
            parent.Children.Add(sep);
        }

        private void AddLabeledComboBox(System.Windows.Controls.Panel parent, string label, string[] items,
            int selectedIndex, Action<string> onChange)
        {
            var lbl = new TextBlock
            {
                Text   = label,
                Margin = new Thickness(0, 4, 0, 2),
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            parent.Children.Add(lbl);

            var combo = new WpfComboBox
            {
                ItemsSource         = items,
                SelectedIndex       = Math.Max(0, Math.Min(selectedIndex, items.Length - 1)),
                IsEditable          = false,
                MaxDropDownHeight   = 200,
                Margin              = new Thickness(0, 0, 0, 4),
            };
            combo.SetResourceReference(WpfComboBox.BackgroundProperty,  "LemoineSelectBg");
            combo.SetResourceReference(WpfComboBox.ForegroundProperty,  "LemoineText");
            combo.SetResourceReference(WpfComboBox.FontFamilyProperty,  "LemoineUiFont");
            combo.SetResourceReference(WpfComboBox.FontSizeProperty,    "LemoineFS_MD");
            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedItem is string val) onChange(val);
            };
            parent.Children.Add(combo);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  IsValid / SummaryFor / Run
        // ═════════════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedNames.Count > 0;
                case "S2": return _pdfOn || _dwgOn;
                case "S3": return !string.IsNullOrWhiteSpace(_outputFolder);
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedNames.Count == 0 ? "—"
                    : $"{_selectedNames.Count} {_exportMode.ToLower()} selected";
                case "S2": return GetActiveFormats() == "—"
                    ? "No formats selected"
                    : $"{GetActiveFormats()} — {_filenamePattern}";
                case "S3": return string.IsNullOrEmpty(_outputFolder) ? "No output folder"
                    : _outputFolder;
                default: return "—";
            }
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            if (_handler == null || _event == null) return;

            // Persist settings
            BatchExportSettings.Instance.FilenamePattern  = _filenamePattern;
            BatchExportSettings.Instance.OutputFolder     = _outputFolder;
            BatchExportSettings.Instance.SplitByFormat    = _splitByFormat;
            BatchExportSettings.Instance.ExportPdf        = _pdfOn;
            BatchExportSettings.Instance.ExportDwg        = _dwgOn;
            BatchExportSettings.Instance.CombinePdf       = _combinePdf;
            BatchExportSettings.Instance.PdfPaperPlacement = _pdfPlacement;
            BatchExportSettings.Instance.HiddenLinesVector = _hiddenLines == "Vector Processing";
            BatchExportSettings.Instance.DwgExportSetupName = _dwgSetup;
            BatchExportSettings.Instance.Save();

            // Set handler properties
            _handler.SelectedIds     = _selectedNames
                .Where(n => _nameToId.ContainsKey(n))
                .Select(n => _nameToId[n])
                .ToList();
            _handler.ExportMode      = _exportMode;
            _handler.FilenamePattern = _filenamePattern;
            _handler.OutputFolder    = _outputFolder;
            _handler.SplitByFormat   = _splitByFormat;
            _handler.ExportPdf       = _pdfOn;
            _handler.ExportDwg       = _dwgOn;
            _handler.CombinePdf      = _combinePdf;
            _handler.DwgSetupName    = _dwgSetup;
            _handler.PdfPlacement    = _pdfPlacement;
            _handler.HiddenLines     = _hiddenLines;
            _handler.PushLog         = pushLog;
            _handler.OnProgress      = onProgress;
            _handler.OnComplete      = onComplete;

            _event.Raise();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  ILemoineToolSettings
        // ═════════════════════════════════════════════════════════════════════
        public LemoineToolSettingsSpec? GetSettingsSpec()
        {
            var s = BatchExportSettings.Instance;
            return new LemoineToolSettingsSpec
            {
                Id          = "tx",
                Label       = "Batch Export",
                Icon        = "Tx",
                Description = "Export sheets and views to PDF and DWG with parametric filenames.",
                Groups      = new List<LemoineSettingsGroup>
                {
                    new LemoineSettingsGroup
                    {
                        Id = "G1", Title = "Output", OpenByDefault = true,
                        Settings = new List<LemoineSettingDef>
                        {
                            new LemoineSettingDef { Id = "outdir",      Kind = "file",   Label = "Default output folder",
                                Options = new FileOpts { Placeholder = @"C:\Projects\Exports\" }, Default = s.OutputFolder },
                            new LemoineSettingDef { Id = "splitformat", Kind = "toggle", Label = "Split output by file format",
                                Hint = "Creates PDF\\, DWG\\ subfolders automatically.", Default = s.SplitByFormat },
                        }
                    },
                    new LemoineSettingsGroup
                    {
                        Id = "G2", Title = "Filename",
                        Settings = new List<LemoineSettingDef>
                        {
                            new LemoineSettingDef { Id = "pattern", Kind = "text", Label = "Default filename pattern",
                                Hint = "Tokens: {SheetNumber} {SheetName} {Revision} {IssueDate} {ProjectNumber} {Year} {Month} {Day}",
                                Options = new TextOpts { Mono = true, Placeholder = "{SheetNumber}-{SheetName}" },
                                Default = s.FilenamePattern },
                        }
                    },
                    new LemoineSettingsGroup
                    {
                        Id = "G3", Title = "Default Formats",
                        Settings = new List<LemoineSettingDef>
                        {
                            new LemoineSettingDef { Id = "defpdf", Kind = "toggle", Label = "PDF on by default", Default = s.ExportPdf },
                            new LemoineSettingDef { Id = "defdwg", Kind = "toggle", Label = "DWG on by default", Default = s.ExportDwg },
                        }
                    },
                    new LemoineSettingsGroup
                    {
                        Id = "G4", Title = "PDF Options",
                        Settings = new List<LemoineSettingDef>
                        {
                            new LemoineSettingDef { Id = "combinepdf",  Kind = "toggle", Label = "Combine into single PDF by default", Default = s.CombinePdf },
                            new LemoineSettingDef { Id = "placement",   Kind = "single", Label = "Paper placement",
                                Options = new SingleSelectOpts { Items = new List<string> { "Center", "Offset from Corner" } },
                                Default = s.PdfPaperPlacement },
                            new LemoineSettingDef { Id = "hiddenlines", Kind = "single", Label = "Hidden line views",
                                Options = new SingleSelectOpts { Items = new List<string> { "Vector Processing", "Raster Processing" } },
                                Default = s.HiddenLinesVector ? "Vector Processing" : "Raster Processing" },
                        }
                    },
                    new LemoineSettingsGroup
                    {
                        Id = "G5", Title = "DWG Options",
                        Settings = new List<LemoineSettingDef>
                        {
                            new LemoineSettingDef { Id = "dwgsetup", Kind = "text", Label = "Default DWG export setup name",
                                Hint = "Must match a setup created in Revit via File → Export → DWG.",
                                Options = new TextOpts { Placeholder = "Standard DWG" }, Default = s.DwgExportSetupName },
                        }
                    },
                }
            };
        }

        public void ApplySettings(string groupId, string settingId, object value)
        {
            var s = BatchExportSettings.Instance;
            switch (settingId)
            {
                case "outdir":      s.OutputFolder          = value as string ?? "";   break;
                case "splitformat": s.SplitByFormat         = value is bool b1 && b1;  break;
                case "pattern":     s.FilenamePattern       = value as string ?? "";   break;
                case "defpdf":      s.ExportPdf             = value is bool b2 && b2;  break;
                case "defdwg":      s.ExportDwg             = value is bool b3 && b3;  break;
                case "combinepdf":  s.CombinePdf            = value is bool b4 && b4;  break;
                case "placement":   s.PdfPaperPlacement     = value as string ?? "Center"; break;
                case "hiddenlines": s.HiddenLinesVector     = value as string == "Vector Processing"; break;
                case "dwgsetup":    s.DwgExportSetupName    = value as string ?? "";   break;
            }
            s.Save();
        }
    }
}
