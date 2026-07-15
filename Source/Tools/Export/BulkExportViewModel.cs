using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;
using LemoineTools.Framework.Naming;

using WpfGrid       = System.Windows.Controls.Grid;
using WpfTextBox    = System.Windows.Controls.TextBox;
using WpfComboBox   = System.Windows.Controls.ComboBox;
using WpfVisibility = System.Windows.Visibility;
using WpfBrushes    = System.Windows.Media.Brushes;

namespace LemoineTools.Tools.BulkExport
{
    public class BulkExportViewModel : IStepFlowTool, IReviewableTool, IConditionalSteps, IStepAware, IRunResult, IToolCleanup
    {
        // Run strip: "files" during the run, per-format breakdown ("30 PDF · 30 DWG") on completion.
        public string? ResultNoun => "files";
        private System.Collections.Generic.IReadOnlyList<LemoineTools.Framework.ResultChip>? _resultChips;
        public System.Collections.Generic.IReadOnlyList<LemoineTools.Framework.ResultChip>? ResultChips => _resultChips;

        // Null the callbacks parked on the static handler so this VM isn't retained after close.
        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.PushLog       = null;
            _handler.OnProgress    = null;
            _handler.OnComplete    = null;
            _handler.OnResultChips = null;
        }

        // ── IStepFlowTool ──────────────────────────────────────────────────────
        public string Title    => AppStrings.T("export.bulkExport.title");
        public string RunLabel => AppStrings.T("export.bulkExport.runLabel");

        // PDF/DWG/NWC/IFC settings each get their own step, shown only when that format
        // is enabled (IConditionalSteps). The settings steps must never be last —
        // S9 (review/run) is always visible.
        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", AppStrings.T("export.bulkExport.steps.S1"), required: true),
            new StepDefinition("S2", AppStrings.T("export.bulkExport.steps.S2"),           required: false),
            new StepDefinition("S3", AppStrings.T("export.bulkExport.steps.S3"),    required: true),
            new StepDefinition("S4", AppStrings.T("export.bulkExport.steps.S4"),          required: false),
            new StepDefinition("S5", AppStrings.T("export.bulkExport.steps.S5"),          required: false),
            new StepDefinition("S6", AppStrings.T("export.bulkExport.steps.S6"),          required: false),
            new StepDefinition("S7", AppStrings.T("export.bulkExport.steps.S7"),          required: false),
            new StepDefinition("S8", AppStrings.T("export.bulkExport.steps.S8"),                required: true),
            new StepDefinition("S9", AppStrings.T("export.bulkExport.steps.S9"),          required: false),
        };

        // ── IConditionalSteps ──────────────────────────────────────────
        public bool IsStepVisible(string stepId)
        {
            switch (stepId)
            {
                case "S4": return _pdfOn;
                case "S5": return _dwgOn;
                case "S6": return _nwcOn;
                case "S7": return _ifcOn;
                default:   return true;
            }
        }

        // ── IStepAware ────────────────────────────────────────────────────────
        // Step content is built eagerly when the window opens, so steps that depend on
        // earlier choices must be rebuilt when the user navigates to them:
        //   S2 reads the live S1 selection, S3's token picker matches the export mode,
        //   S6's note reflects sheets-vs-views mode.
        private Action<string>? _refreshStep;
        public void SetContentRefreshCallback(Action<string> rebuildStepContent) => _refreshStep = rebuildStepContent;
        public void OnStepActivated(string stepId)
        {
            if (stepId == "S2" || stepId == "S3" || stepId == "S6")
                _refreshStep?.Invoke(stepId);
        }

        public event EventHandler? ValidationChanged;
        private void Fire() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── NWC faceting presets (label→value, no anonymous tuples) ──────────
        private static readonly string[] NwcFacetingLabels = { "Low — 0.5", "Standard — 1.0", "High — 2.0", "Ultra — 5.0" };
        private static readonly double[] NwcFacetingValues = { 0.5, 1.0, 2.0, 5.0 };

        // Named struct required — anonymous tuple arrays with Func<string> are forbidden (net48 constraint)
        private struct CardDef
        {
            internal string        Label;
            internal Func<string>  Value;
            internal CardDef(string label, Func<string> value) { Label = label; Value = value; }
        }

        // ── Token definitions ─────────────────────────────────────────────────
        // Registry-fed per mode so the picker only ever offers tokens that are valid for
        // what is being exported. Sheet-only tokens (number/revision/issue date) do not
        // exist on a view, so offering them in Views mode would produce empty/degenerate names.
        private const string SheetDefaultPattern = "{SheetNumber}-{SheetName}";
        private const string ViewDefaultPattern  = "{ViewName}";

        private bool ViewsMode => _exportMode == "Views";

        // ── S1 state ──────────────────────────────────────────────────────────
        private string                        _exportMode    = "Sheets";
        private List<string>                  _selectedNames = new List<string>();
        private Dictionary<string, ElementId> _nameToId      = new Dictionary<string, ElementId>();

        // ── S2 state (print sets) ──────────────────────────────────────────────
        // Existing Revit print sets (ViewSheetSet elements), refreshed after creating a new
        // one. Checking a set makes it an export group with its own optional overrides.
        private List<PrintSetInfo>       _availablePrintSets;
        private readonly HashSet<long>   _selectedPrintSetIds = new HashSet<long>();
        private readonly Dictionary<long, string?> _patternOverrides = new Dictionary<long, string?>();
        private readonly Dictionary<long, bool?>   _pdfOverrides     = new Dictionary<long, bool?>();
        private readonly Dictionary<long, bool?>   _dwgOverrides     = new Dictionary<long, bool?>();
        private string _newPrintSetName = "";

        // ── S3 state (filename & formats) ────────────────────────────────────
        // Separate patterns per mode so each carries a default built from its own valid
        // token set. ActivePattern resolves to whichever applies to the current mode.
        private string             _sheetPattern    = BulkExportSettings.Instance.FilenamePattern;
        private string             _viewPattern     = BulkExportSettings.Instance.ViewFilenamePattern;
        private string ActivePattern
        {
            get => ViewsMode ? _viewPattern : _sheetPattern;
            set { if (ViewsMode) _viewPattern = value; else _sheetPattern = value; }
        }
        private bool               _pdfOn           = BulkExportSettings.Instance.ExportPdf;
        private bool               _dwgOn           = BulkExportSettings.Instance.ExportDwg;
        private bool               _nwcOn           = BulkExportSettings.Instance.ExportNwc;
        private bool               _ifcOn           = BulkExportSettings.Instance.ExportIfc;
        private string             _ifcVersion      = BulkExportSettings.Instance.IfcVersion;
        private string             _dwgSetup        = BulkExportSettings.Instance.DwgExportSetupName;
        private TokenInput? _tokenInput;

        // ── NWC option state (all NavisworksExportOptions properties) ─────────
        private string _nwcCoordinates         = BulkExportSettings.Instance.NwcCoordinates;
        private string _nwcParameters          = BulkExportSettings.Instance.NwcParameters;
        private bool   _nwcConvertElementProps  = BulkExportSettings.Instance.NwcConvertElementProps;
        private bool   _nwcDivideByLevel        = BulkExportSettings.Instance.NwcDivideByLevel;
        private bool   _nwcExportLinks          = BulkExportSettings.Instance.NwcExportLinks;
        private bool   _nwcExportParts          = BulkExportSettings.Instance.NwcExportParts;
        private bool   _nwcExportElementIds     = BulkExportSettings.Instance.NwcExportElementIds;
        private bool   _nwcExportUrls           = BulkExportSettings.Instance.NwcExportUrls;
        private bool   _nwcFindMissingMaterials = BulkExportSettings.Instance.NwcFindMissingMaterials;
        private bool   _nwcExportRoomGeometry   = BulkExportSettings.Instance.NwcExportRoomGeometry;
        private bool   _nwcExportRoomAsAttr     = BulkExportSettings.Instance.NwcExportRoomAsAttribute;
        private bool   _nwcConvertLights        = BulkExportSettings.Instance.NwcConvertLights;
        private bool   _nwcConvertLinkedCad     = BulkExportSettings.Instance.NwcConvertLinkedCad;
        private double _nwcFacetingFactor       = BulkExportSettings.Instance.NwcFacetingFactor;

        // ── S4 state (PDF settings) ───────────────────────────────────────────
        private string _pdfPlacement    = BulkExportSettings.Instance.PdfPaperPlacement;
        private string _zoomSetting     = BulkExportSettings.Instance.ZoomSetting;
        private int    _zoomPct         = BulkExportSettings.Instance.ZoomPercent;
        private string _colorDepth      = BulkExportSettings.Instance.ColorDepth;
        private string _rasterQuality   = BulkExportSettings.Instance.RasterQuality;
        private string _hiddenLines     = BulkExportSettings.Instance.HiddenLinesVector
                                          ? "Vector Processing" : "Raster Processing";
        private bool   _combinePdf      = BulkExportSettings.Instance.CombinePdf;
        private bool   _viewLinksBlue   = BulkExportSettings.Instance.ViewLinksInBlue;
        private bool   _replaceHalftone = BulkExportSettings.Instance.ReplaceHalftoneWithThinLines;

        // ── S5 state (output) ─────────────────────────────────────────────────
        private string _outputFolder  = BulkExportSettings.Instance.OutputFolder;
        private bool   _splitByFormat = BulkExportSettings.Instance.SplitByFormat;

        // ── Revit data ────────────────────────────────────────────────────────
        private readonly List<ViewSheet>             _allSheets;
        private readonly List<View>                  _allViews;
        private readonly List<string>                _dwgSetupNames;
        private readonly Dictionary<ElementId, ViewSheet> _sheetById;
        private readonly BrowserTree          _browserTree;
        private readonly Dictionary<long, string>    _idToName = new Dictionary<long, string>();

        // ── Preview (token preview in S3) ─────────────────────────────────────
        private string _previewSheetNumber = "A101";
        private string _previewSheetName   = "Ground Floor";

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly BulkExportEventHandler? _handler;
        private readonly ExternalEvent?           _event;

        // ── Constructor ───────────────────────────────────────────────────────
        public BulkExportViewModel(
            BulkExportEventHandler? handler,
            ExternalEvent?           externalEvent,
            List<string>             dwgSetupNames,
            List<ViewSheet>          allSheets,
            List<View>               allViews,
            BrowserTree       browserTree,
            List<PrintSetInfo>? availablePrintSets = null)
        {
            _handler       = handler;
            _event         = externalEvent;
            _dwgSetupNames = dwgSetupNames;
            _allSheets     = allSheets;
            _allViews      = allViews;
            _browserTree   = browserTree;
            _availablePrintSets = availablePrintSets ?? new List<PrintSetInfo>();

            // Build fast ID→Sheet lookup
            _sheetById = new Dictionary<ElementId, ViewSheet>();
            foreach (var s in _allSheets)
            {
                _sheetById[s.Id] = s;
                string key = $"{s.SheetNumber} — {s.Name}";
                if (!_nameToId.ContainsKey(key)) _nameToId[key] = s.Id;
                if (!_idToName.ContainsKey(s.Id.Value)) _idToName[s.Id.Value] = key;
            }
            foreach (var v in _allViews)
            {
                string key = v.Name;
                if (!_nameToId.ContainsKey(key)) _nameToId[key] = v.Id;
                if (!_idToName.ContainsKey(v.Id.Value)) _idToName[v.Id.Value] = key;
            }

            // Seed preview from first sheet
            if (_allSheets.Count > 0)
            {
                _previewSheetNumber = _allSheets[0].SheetNumber;
                _previewSheetName   = _allSheets[0].Name;
            }

            // Default the DWG setup to the first available so the combo's shown value
            // matches what actually gets used (previously the combo displayed setup [0]
            // while _dwgSetup stayed empty until the user touched it).
            if (string.IsNullOrEmpty(_dwgSetup) && _dwgSetupNames.Count > 0)
                _dwgSetup = _dwgSetupNames[0];
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
                case "S4": return BuildS4Pdf();
                case "S5": return BuildS5Dwg();
                case "S6": return BuildS6Nwc();
                case "S7": return BuildS7Ifc();
                case "S8": return BuildS8Output();
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
                Content   = AppStrings.T("export.bulkExport.labels.showAllViews"),
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

            var multiSelect = BuildTreePicker(showAllCb);
            multiSelect.Tag = "multiselect";
            outer.Children.Add(multiSelect);

            return outer;
        }

        private BrowserTreePicker BuildTreePicker(CheckBox showAllCb)
        {
            var picker = new BrowserTreePicker { Height = 300 };
            // Subscribe BEFORE SetTree — per the BrowserTreePicker contract, the
            // single SelectionChanged fired at the end of SetTree is the only mechanism
            // that initialises the mirror field.
            picker.SelectionChanged += ids =>
            {
                _selectedNames = ids
                    .Where(id => _idToName.ContainsKey(id))
                    .Select(id => _idToName[id])
                    .ToList();
                Fire();
            };
            picker.SetTree(_browserTree, BuildEligibleIds((bool)(showAllCb.Tag ?? false)));
            return picker;
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

            var newPicker = BuildTreePicker(showAllCb ?? new CheckBox { Tag = false });
            newPicker.Tag = "multiselect";
            outer.Children.Add(newPicker);
            _selectedNames.Clear();
            Fire();
        }

        // Which captured browser-tree leaves are pickable in the current mode. Roots
        // with no eligible leaves (e.g. Views while exporting sheets) are hidden.
        private IEnumerable<long> BuildEligibleIds(bool showAll)
        {
            if (_exportMode == "Sheets")
                return _allSheets.Select(s => s.Id.Value);

            var allowedFamilies = new HashSet<ViewFamily>
            {
                ViewFamily.FloorPlan, ViewFamily.CeilingPlan,
                ViewFamily.Section,   ViewFamily.Elevation,
                ViewFamily.Detail,    ViewFamily.ThreeDimensional,
            };
            return _allViews
                .Where(v => showAll || allowedFamilies.Contains(
                    v.ViewType == ViewType.DraftingView
                        ? ViewFamily.Detail
                        : GetViewFamily(v)))
                .Select(v => v.Id.Value);
        }

        // ── S2 — Build Packs ──────────────────────────────────────────────────
        // Print Sets step — pick existing Revit print sets (ViewSheetSets) as export groups,
        // each with an optional filename-pattern and format override, or save the current
        // S1 selection as a new print set. Membership comes from Revit itself, not a drag-drop
        // editor, so no "Load"/reorder UI is needed — a real print set already knows its members.
        private FrameworkElement BuildS2()
        {
            var outer = new StackPanel();

            var note = new TextBlock
            {
                Text         = AppStrings.T("export.bulkExport.labels.printSetsNote"),
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, 0, 0, 10),
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            note.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            note.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            outer.Children.Add(note);

            var listContainer = new StackPanel();
            outer.Children.Add(listContainer);

            void RebuildList()
            {
                listContainer.Children.Clear();
                if (_availablePrintSets.Count == 0)
                {
                    listContainer.Children.Add(Dim(AppStrings.T("export.bulkExport.labels.noPrintSets")));
                }
                foreach (var ps in _availablePrintSets)
                    listContainer.Children.Add(BuildPrintSetRow(ps, RebuildList));
            }
            RebuildList();

            AddDivider(outer);
            AddSectionLabel(outer, AppStrings.T("export.bulkExport.labels.saveAsPrintSet"));

            var saveRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };
            var saveBtn = new Button
            {
                Content = AppStrings.T("export.bulkExport.labels.savePrintSetBtn"),
                Margin  = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(8, 0, 8, 0),
            };
            saveBtn.SetResourceReference(Button.MinHeightProperty,   "LemoineH_BtnMin");
            saveBtn.SetResourceReference(Button.FontSizeProperty,    "LemoineFS_SM");
            saveBtn.SetResourceReference(Button.FontFamilyProperty,  "LemoineUiFont");
            saveBtn.SetResourceReference(Button.ForegroundProperty,  "LemoineText");
            saveBtn.SetResourceReference(Button.BackgroundProperty,  "LemoineCanvas");
            saveBtn.SetResourceReference(Button.BorderBrushProperty, "LemoineBorder");
            saveBtn.Template = ControlStyles.BuildFlatButtonTemplate();
            DockPanel.SetDock(saveBtn, Dock.Right);

            var nameBox = new WpfTextBox { Text = _newPrintSetName, Padding = new Thickness(6, 2, 6, 2) };
            nameBox.SetResourceReference(WpfTextBox.BackgroundProperty,  "LemoineSelectBg");
            nameBox.SetResourceReference(WpfTextBox.ForegroundProperty,  "LemoineText");
            nameBox.SetResourceReference(WpfTextBox.BorderBrushProperty, "LemoineBorderMid");
            nameBox.SetResourceReference(WpfTextBox.FontFamilyProperty,  "LemoineUiFont");
            nameBox.SetResourceReference(WpfTextBox.FontSizeProperty,    "LemoineFS_SM");
            nameBox.SetResourceReference(WpfTextBox.MinHeightProperty,   "LemoineH_Input");
            nameBox.TextChanged += (s, e) => _newPrintSetName = nameBox.Text;

            saveBtn.Click += (s, e) => SaveCurrentSelectionAsPrintSet(RebuildList);

            saveRow.Children.Add(saveBtn);
            saveRow.Children.Add(nameBox);
            outer.Children.Add(saveRow);

            var saveHint = new TextBlock
            {
                Text         = AppStrings.T("export.bulkExport.labels.saveAsPrintSetHint"),
                TextWrapping = TextWrapping.Wrap,
            };
            saveHint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            saveHint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            saveHint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            outer.Children.Add(saveHint);

            return outer;
        }

        private void SaveCurrentSelectionAsPrintSet(Action rebuildList)
        {
            if (string.IsNullOrWhiteSpace(_newPrintSetName)) return;
            var handler = App.BulkExportPrintSetHandler;
            var evt     = App.BulkExportPrintSetEvent;
            if (handler == null || evt == null) return;

            handler.Name      = _newPrintSetName.Trim();
            handler.MemberIds = _selectedNames.Where(_nameToId.ContainsKey).Select(n => _nameToId[n]).ToList();
            handler.OnCreated = sets =>
            {
                _availablePrintSets = sets;
                _newPrintSetName    = "";
                rebuildList();
                Fire();
            };
            handler.OnError = msg => DiagnosticsLog.Warn("BulkExport: save print set", msg ?? "");
            evt.Raise();
        }

        private FrameworkElement BuildPrintSetRow(PrintSetInfo ps, Action rebuildList)
        {
            long key = ps.Id.Value;
            bool selected = _selectedPrintSetIds.Contains(key);

            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(5),
                Padding         = new Thickness(10, 8, 10, 8),
                Margin          = new Thickness(0, 0, 0, 8),
            };
            card.SetResourceReference(Border.BackgroundProperty,  selected ? "LemoineAccentDim" : "LemoineRaised");
            card.SetResourceReference(Border.BorderBrushProperty, selected ? "LemoineAccent" : "LemoineBorder");

            var stack = new StackPanel();

            var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
            var cb = new CheckBox { IsChecked = selected, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            headerRow.Children.Add(cb);

            var nameTb = new TextBlock
            {
                Text = AppStrings.T("export.bulkExport.labels.printSetRow", ps.Name, ps.MemberIds.Count),
                VerticalAlignment = VerticalAlignment.Center,
            };
            nameTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            nameTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            nameTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            headerRow.Children.Add(nameTb);
            stack.Children.Add(headerRow);

            var overridesPanel = new StackPanel { Margin = new Thickness(24, 8, 0, 0) };
            overridesPanel.Visibility = selected ? WpfVisibility.Visible : WpfVisibility.Collapsed;
            stack.Children.Add(overridesPanel);

            void RebuildOverrides()
            {
                overridesPanel.Children.Clear();

                var patternLabel = new TextBlock { Text = AppStrings.T("export.bulkExport.labels.printSetPatternOverride"), Margin = new Thickness(0, 0, 0, 4) };
                patternLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                patternLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                patternLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                overridesPanel.Children.Add(patternLabel);

                _patternOverrides.TryGetValue(key, out var curPattern);
                var patternBox = new WpfTextBox
                {
                    Text = curPattern ?? "", Width = 260, HorizontalAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(6, 2, 6, 2),
                };
                patternBox.SetResourceReference(WpfTextBox.BackgroundProperty,  "LemoineSelectBg");
                patternBox.SetResourceReference(WpfTextBox.ForegroundProperty,  "LemoineText");
                patternBox.SetResourceReference(WpfTextBox.BorderBrushProperty, "LemoineBorderMid");
                patternBox.SetResourceReference(WpfTextBox.FontFamilyProperty,  "LemoineMonoFont");
                patternBox.SetResourceReference(WpfTextBox.FontSizeProperty,    "LemoineFS_SM");
                patternBox.SetResourceReference(WpfTextBox.MinHeightProperty,   "LemoineH_Input");
                patternBox.TextChanged += (s, e) =>
                    _patternOverrides[key] = string.IsNullOrWhiteSpace(patternBox.Text) ? null : patternBox.Text;
                overridesPanel.Children.Add(patternBox);

                overridesPanel.Children.Add(new FrameworkElement { Height = 8 });

                string inheritLabel = AppStrings.T("export.bulkExport.labels.printSetInherit");
                string onLabel      = AppStrings.T("export.bulkExport.labels.printSetOn");
                string offLabel     = AppStrings.T("export.bulkExport.labels.printSetOff");
                var tristate = new List<string> { inheritLabel, onLabel, offLabel };

                FrameworkElement FormatOverrideRow(string label, Dictionary<long, bool?> map)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
                    var lbl = new TextBlock { Text = label, Width = 50, VerticalAlignment = VerticalAlignment.Center };
                    lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                    lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    row.Children.Add(lbl);

                    map.TryGetValue(key, out var cur);
                    var sel = new SingleSelect
                    {
                        Width = 140,
                        Items = tristate,
                        SelectedItem = cur == true ? onLabel : cur == false ? offLabel : inheritLabel,
                    };
                    sel.SelectionChanged += v =>
                        map[key] = v == onLabel ? true : v == offLabel ? false : (bool?)null;
                    row.Children.Add(sel);
                    return row;
                }

                overridesPanel.Children.Add(FormatOverrideRow("PDF", _pdfOverrides));
                overridesPanel.Children.Add(FormatOverrideRow("DWG", _dwgOverrides));
            }
            RebuildOverrides();

            cb.Checked += (s, e) =>
            {
                _selectedPrintSetIds.Add(key);
                card.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                card.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                overridesPanel.Visibility = WpfVisibility.Visible;
                Fire();
            };
            cb.Unchecked += (s, e) =>
            {
                _selectedPrintSetIds.Remove(key);
                card.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                overridesPanel.Visibility = WpfVisibility.Collapsed;
                Fire();
            };

            card.Child = stack;
            return card;
        }

        // ── S3 — Filename & Formats ───────────────────────────────────────────
        // Pattern + format toggles only. Each format's own options live in its dedicated
        // step (S4 PDF, S5 DWG, S6 NWC, S7 IFC), shown only when that format is enabled.
        private FrameworkElement BuildS3()
        {
            var outer = new StackPanel();

            // Filename pattern — token vocabulary matches the export mode so only valid
            // tokens are ever offered (sheet tokens for sheets, view tokens for views).
            AddSectionLabel(outer, ViewsMode ? AppStrings.T("export.bulkExport.labels.patternViews") : AppStrings.T("export.bulkExport.labels.patternSheets"));

            var patternTokens = NamingTokenRegistry.TokensFor(
                ViewsMode ? TokenEntity.View : TokenEntity.Sheet, hasSource: false);
            _tokenInput      = new TokenInput(patternTokens,
                                                     ViewsMode ? ViewDefaultPattern : SheetDefaultPattern);
            _tokenInput.Text = ActivePattern;
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
                ActivePattern = _tokenInput.Text;
                UpdatePreview(preview);
                Fire();
            };

            // Print set filename note (shown when print sets are selected)
            if (HasActivePrintSets())
            {
                var packNote = new TextBlock
                {
                    Text         = AppStrings.T("export.bulkExport.labels.printSetFilenameNote"),
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

            // Formats — toggling a format reveals/hides its settings step (via Fire →
            // the window re-evaluates IsStepVisible).
            AddSectionLabel(outer, AppStrings.T("export.bulkExport.labels.secFormats"));

            var formatToggles = new ToggleSwitches();
            formatToggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "pdf", Label = "PDF", Desc = AppStrings.T("export.bulkExport.labels.descPdf"),                     DefaultOn = _pdfOn  },
                new ToggleItem { Id = "dwg", Label = "DWG", Desc = AppStrings.T("export.bulkExport.labels.descDwg"),                    DefaultOn = _dwgOn  },
                new ToggleItem { Id = "nwc", Label = "NWC", Desc = AppStrings.T("export.bulkExport.labels.descNwc"),                  DefaultOn = _nwcOn  },
                new ToggleItem { Id = "ifc", Label = "IFC", Desc = AppStrings.T("export.bulkExport.labels.descIfc"),   DefaultOn = _ifcOn  },
            });
            formatToggles.StateChanged += state =>
            {
                _pdfOn = state.TryGetValue("pdf", out bool pdfVal) && pdfVal;
                _dwgOn = state.TryGetValue("dwg", out bool dwgVal) && dwgVal;
                _nwcOn = state.TryGetValue("nwc", out bool nwcVal) && nwcVal;
                _ifcOn = state.TryGetValue("ifc", out bool ifcVal) && ifcVal;
                Fire();
            };
            outer.Children.Add(formatToggles);

            // Mode hint for the 3D-only formats
            if ((_nwcOn || _ifcOn) && !ViewsMode)
            {
                var modeHint = new TextBlock
                {
                    Text         = AppStrings.T("export.bulkExport.labels.modeHint"),
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle    = FontStyles.Italic,
                    Margin       = new Thickness(0, 8, 0, 0),
                };
                modeHint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                modeHint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                modeHint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                outer.Children.Add(modeHint);
            }

            return outer;
        }

        private void UpdatePreview(TextBlock preview)
        {
            Dictionary<string, string> tokens;
            if (ViewsMode)
            {
                tokens = new Dictionary<string, string>
                {
                    ["ViewName"]      = "Level 1 - Lighting",
                    ["ViewType"]      = "FloorPlan",
                    ["ProjectNumber"] = "2024-001",
                    ["ProjectName"]   = "Sample Project",
                    ["Year"]          = DateTime.Now.Year.ToString(),
                    ["Month"]         = DateTime.Now.Month.ToString("D2"),
                    ["Day"]           = DateTime.Now.Day.ToString("D2"),
                };
            }
            else
            {
                tokens = new Dictionary<string, string>
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
            }
            var ctx = new TokenContext();
            foreach (var kvp in tokens) ctx.Computed[kvp.Key] = kvp.Value;
            string resolved = TokenResolver.Resolve(ActivePattern, ctx);
            preview.Text = AppStrings.T("export.bulkExport.labels.preview", SanitiseFilenamePreview(resolved), PreviewExtension());
        }

        // The dominant output extension for the preview (first enabled format).
        private string PreviewExtension()
        {
            if (_pdfOn) return ".pdf";
            if (_dwgOn) return ".dwg";
            if (_nwcOn) return ".nwc";
            if (_ifcOn) return ".ifc";
            return "";
        }

        private static string SanitiseFilenamePreview(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }

        // ── NWC options builder ───────────────────────────────────────────────
        private void BuildNwcOptions(StackPanel parent)
        {
            AddSectionLabel(parent, AppStrings.T("export.bulkExport.labels.secNwcOptions"));

            // Mode-aware note. This step is rebuilt every time it is activated
            // (IStepAware.OnStepActivated → "S6"), so the text reflects the current mode
            // without a ValidationChanged subscription — the old subscription accumulated
            // one handler per rebuild and is removed.
            var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            note.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            note.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            note.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            note.Text = _exportMode == "Sheets"
                ? AppStrings.T("export.bulkExport.labels.nwcNoteSheets")
                : AppStrings.T("export.bulkExport.labels.nwcNoteViews");
            parent.Children.Add(note);

            AddDivider(parent);

            // ── Coordinates & Parameters ──────────────────────────────────────
            AddSectionLabel(parent, AppStrings.T("export.bulkExport.labels.secCoordParams"));

            AddLabeledComboBox(parent, AppStrings.T("export.bulkExport.labels.lblCoordSystem"),
                new[] { "Shared", "Internal" },
                _nwcCoordinates == "Internal" ? 1 : 0,
                val => { _nwcCoordinates = val; Fire(); });

            AddLabeledComboBox(parent, AppStrings.T("export.bulkExport.labels.lblElementParams"),
                new[] { "All", "Elements", "None" },
                _nwcParameters == "Elements" ? 1 : _nwcParameters == "None" ? 2 : 0,
                val => { _nwcParameters = val; Fire(); });

            AddDivider(parent);

            // ── Geometry & Mesh ───────────────────────────────────────────────
            AddSectionLabel(parent, AppStrings.T("export.bulkExport.labels.secGeomMesh"));

            int initFacetIdx = Array.IndexOf(NwcFacetingValues, _nwcFacetingFactor);
            if (initFacetIdx < 0) initFacetIdx = 1; // fallback to Standard

            AddLabeledComboBox(parent, AppStrings.T("export.bulkExport.labels.lblMeshQuality"),
                NwcFacetingLabels, initFacetIdx,
                val =>
                {
                    int idx = Array.IndexOf(NwcFacetingLabels, val);
                    _nwcFacetingFactor = idx >= 0 ? NwcFacetingValues[idx] : 1.0;
                    Fire();
                });

            AddNwcCheckBox(parent, AppStrings.T("export.bulkExport.labels.cbConvertProps"),  _nwcConvertElementProps, v => _nwcConvertElementProps = v);
            AddNwcCheckBox(parent, AppStrings.T("export.bulkExport.labels.cbConvertLights"),        _nwcConvertLights,       v => _nwcConvertLights       = v);
            AddNwcCheckBox(parent, AppStrings.T("export.bulkExport.labels.cbConvertCad"),  _nwcConvertLinkedCad,    v => _nwcConvertLinkedCad    = v);

            AddDivider(parent);

            // ── Content to Include ────────────────────────────────────────────
            AddSectionLabel(parent, AppStrings.T("export.bulkExport.labels.secContent"));

            AddNwcCheckBox(parent, AppStrings.T("export.bulkExport.labels.cbDivideLevels"),                    _nwcDivideByLevel,       v => _nwcDivideByLevel        = v);
            AddNwcCheckBox(parent, AppStrings.T("export.bulkExport.labels.cbLinkedRevit"),                _nwcExportLinks,         v => _nwcExportLinks          = v);
            AddNwcCheckBox(parent, AppStrings.T("export.bulkExport.labels.cbParts"),                        _nwcExportParts,         v => _nwcExportParts          = v);
            AddNwcCheckBox(parent, AppStrings.T("export.bulkExport.labels.cbElementIds"), _nwcExportElementIds,    v => _nwcExportElementIds     = v);
            AddNwcCheckBox(parent, AppStrings.T("export.bulkExport.labels.cbUrls"),                     _nwcExportUrls,          v => _nwcExportUrls           = v);
            AddNwcCheckBox(parent, AppStrings.T("export.bulkExport.labels.cbMissingMats"),                     _nwcFindMissingMaterials, v => _nwcFindMissingMaterials = v);
            AddNwcCheckBox(parent, AppStrings.T("export.bulkExport.labels.cbRoomGeom"), _nwcExportRoomGeometry, v => _nwcExportRoomGeometry = v);
            AddNwcCheckBox(parent, AppStrings.T("export.bulkExport.labels.cbRoomAttr"),     _nwcExportRoomAsAttr,    v => _nwcExportRoomAsAttr     = v);
        }

        private void AddNwcCheckBox(StackPanel parent, string label, bool isChecked, Action<bool> onChange)
        {
            var cb = new CheckBox { Content = label, IsChecked = isChecked, Margin = new Thickness(0, 0, 0, 4) };
            cb.SetResourceReference(CheckBox.ForegroundProperty, "LemoineText");
            cb.SetResourceReference(CheckBox.FontFamilyProperty, "LemoineUiFont");
            cb.SetResourceReference(CheckBox.FontSizeProperty,   "LemoineFS_MD");
            cb.Checked   += (s, e) => { onChange(true);  Fire(); };
            cb.Unchecked += (s, e) => { onChange(false); Fire(); };
            parent.Children.Add(cb);
        }

        // ── IFC options builder ───────────────────────────────────────────────
        private void BuildIfcOptions(StackPanel parent)
        {
            AddSectionLabel(parent, AppStrings.T("export.bulkExport.labels.secIfcOptions"));

            var note = new TextBlock
            {
                Text         = AppStrings.T("export.bulkExport.labels.ifcNote"),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 4),
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            note.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            note.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            parent.Children.Add(note);

            AddLabeledComboBox(parent, AppStrings.T("export.bulkExport.labels.lblIfcVersion"),
                new[] { "IFC2x3", "IFC4" },
                _ifcVersion == "IFC4" ? 1 : 0,
                val => { _ifcVersion = val; Fire(); });
        }

        // ── S4 — PDF Settings (shown only when PDF is enabled) ─────────────────
        private FrameworkElement BuildS4Pdf()
        {
            var outer = new StackPanel();

            // PAGE SETUP ──────────────────────────────────────────────────────
            AddSectionLabel(outer, AppStrings.T("export.bulkExport.labels.secPageSetup"));

            // Paper placement
            AddSmallLabel(outer, AppStrings.T("export.bulkExport.labels.lblPaperPlacement"));
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
                Text         = AppStrings.T("export.bulkExport.labels.placementHint"),
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, -4, 0, 8),
            };
            placementHint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            placementHint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            placementHint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            outer.Children.Add(placementHint);

            // Zoom type
            AddSmallLabel(outer, AppStrings.T("export.bulkExport.labels.lblZoom"));
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
            var stepper = new InlineStepper { Value = _zoomPct, MinValue = 10, MaxValue = 500, Step = 5, Decimals = 0, ValueWidth = 48 };
            stepper.ValueChanged += (s, v) => { _zoomPct = (int)v; Fire(); };
            var pctLabel = new TextBlock
            {
                Text              = AppStrings.T("export.bulkExport.labels.pctPercent"),
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
            AddSectionLabel(outer, AppStrings.T("export.bulkExport.labels.secOutputQuality"));

            AddLabeledComboBox(outer, AppStrings.T("export.bulkExport.labels.lblColorDepth"),
                new[] { "Color", "Grayscale", "Black & White" },
                GetIndex(new[] { "Color", "Grayscale", "Black & White" }, _colorDepth),
                val => { _colorDepth = val; Fire(); });

            AddLabeledComboBox(outer, AppStrings.T("export.bulkExport.labels.lblRasterQuality"),
                new[] { "Draft", "Low", "Medium", "High", "Presentation" },
                GetIndex(new[] { "Draft", "Low", "Medium", "High", "Presentation" }, _rasterQuality),
                val => { _rasterQuality = val; Fire(); });

            AddLabeledComboBox(outer, AppStrings.T("export.bulkExport.labels.lblHiddenLines"),
                new[] { "Vector Processing", "Raster Processing" },
                _hiddenLines == "Vector Processing" ? 0 : 1,
                val => { _hiddenLines = val; Fire(); });

            AddDivider(outer);

            // COMBINE ─────────────────────────────────────────────────────────
            AddSectionLabel(outer, AppStrings.T("export.bulkExport.labels.secCombine"));

            var combineToggle = new ToggleSwitches();
            combineToggle.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "combine", Label = AppStrings.T("export.bulkExport.labels.combineLabel"), DefaultOn = _combinePdf },
            });
            combineToggle.StateChanged += state => { state.TryGetValue("combine", out _combinePdf); Fire(); };
            outer.Children.Add(combineToggle);

            if (HasActivePrintSets())
            {
                var packCombineNote = new TextBlock
                {
                    Text         = AppStrings.T("export.bulkExport.labels.printSetCombineNote"),
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
            AddSectionLabel(outer, AppStrings.T("export.bulkExport.labels.secAdvanced"));

            var advToggles = new ToggleSwitches();
            advToggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "viewlinks",       Label = AppStrings.T("export.bulkExport.labels.advViewLinks"),              Desc = AppStrings.T("export.bulkExport.labels.advViewLinksDesc"),      DefaultOn = _viewLinksBlue   },
                new ToggleItem { Id = "replacehalftone", Label = AppStrings.T("export.bulkExport.labels.advReplaceHalftone"), Desc = AppStrings.T("export.bulkExport.labels.advReplaceHalftoneDesc"),         DefaultOn = _replaceHalftone },
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
                Text         = AppStrings.T("export.bulkExport.labels.sizeNote"),
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
            };
            sizeNote.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            sizeNote.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            sizeNote.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            outer.Children.Add(sizeNote);

            return outer;
        }

        // ── S5 — DWG Settings (shown only when DWG is enabled) ─────────────────
        private FrameworkElement BuildS5Dwg()
        {
            var outer = new StackPanel();
            AddSectionLabel(outer, AppStrings.T("export.bulkExport.labels.secDwgOptions"));

            var setupNames = _dwgSetupNames.Count > 0
                ? _dwgSetupNames.ToArray()
                : new[] { AppStrings.T("export.bulkExport.labels.dwgNoSetups") };
            int initIdx = setupNames.Contains(_dwgSetup) ? Array.IndexOf(setupNames, _dwgSetup) : 0;
            AddLabeledComboBox(outer, AppStrings.T("export.bulkExport.labels.lblExportSetup"), setupNames, initIdx,
                val => { _dwgSetup = val; Fire(); });

            var note = new TextBlock
            {
                Text         = AppStrings.T("export.bulkExport.labels.dwgNote"),
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, 6, 0, 0),
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            note.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            note.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            outer.Children.Add(note);

            return outer;
        }

        // ── S6 — NWC Settings (shown only when NWC is enabled) ─────────────────
        private FrameworkElement BuildS6Nwc()
        {
            var outer = new StackPanel();
            BuildNwcOptions(outer);
            return outer;
        }

        // ── S7 — IFC Settings (shown only when IFC is enabled) ─────────────────
        private FrameworkElement BuildS7Ifc()
        {
            var outer = new StackPanel();
            BuildIfcOptions(outer);
            return outer;
        }

        // ── S8 — Output ───────────────────────────────────────────────────────
        private FrameworkElement BuildS8Output()
        {
            var outer = new StackPanel();

            AddSectionLabel(outer, AppStrings.T("export.bulkExport.labels.secOutputFolder"));
            BuildFolderPicker(outer);

            var splitToggle = new ToggleSwitches { Margin = new Thickness(0, 6, 0, 0) };
            splitToggle.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "split", Label = AppStrings.T("export.bulkExport.labels.splitLabel"), DefaultOn = _splitByFormat },
            });
            splitToggle.StateChanged += state => { state.TryGetValue("split", out _splitByFormat); Fire(); };
            outer.Children.Add(splitToggle);

            return outer;
        }

        private void BuildFolderPicker(StackPanel parent)
        {
            var folder = new FolderBrowser
            {
                Path        = _outputFolder,
                DialogTitle = AppStrings.T("export.bulkExport.labels.folderDialog"),
            };
            folder.PathChanged += p => { _outputFolder = p; Fire(); };
            parent.Children.Add(folder);
        }


        // ═════════════════════════════════════════════════════════════════════
        //  IsValid / SummaryFor / Run
        // ═════════════════════════════════════════════════════════════════════
        // ── IReviewableTool (P3) — framework renders the final review step ─
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("sheets",  AppStrings.T("export.bulkExport.review.itemSheets")),
            ("formats", AppStrings.T("export.bulkExport.review.itemFormats")),
            ("packs",   AppStrings.T("export.bulkExport.review.itemPacks")),
            ("quality", AppStrings.T("export.bulkExport.review.itemQuality")),
            ("pattern", AppStrings.T("export.bulkExport.review.itemPattern")),
            ("folder",  AppStrings.T("export.bulkExport.review.itemFolder")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["sheets"]  = _selectedNames.Count == 0 ? "—" : AppStrings.T("export.bulkExport.review.sheetsValue", _selectedNames.Count),
            ["formats"] = GetActiveFormats(),
            ["packs"]   = HasActivePrintSets() ? AppStrings.T("export.bulkExport.review.printSetsValue", _selectedPrintSetIds.Count) : AppStrings.T("export.bulkExport.review.printSetsNone"),
            ["quality"] = _pdfOn ? AppStrings.T("export.bulkExport.review.qualityValue", _colorDepth, _rasterQuality) : AppStrings.T("export.bulkExport.review.qualityPdfOff"),
            ["pattern"] = string.IsNullOrEmpty(ActivePattern) ? "—" : ActivePattern,
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
                case "S3": return _pdfOn || _dwgOn || _nwcOn || _ifcOn;
                case "S4": return true;   // PDF settings
                case "S5": return true;   // DWG settings
                case "S6": return true;   // NWC settings
                case "S7": return true;   // IFC settings
                case "S8": return !string.IsNullOrWhiteSpace(_outputFolder);
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedNames.Count == 0 ? "—"
                    : AppStrings.T("export.bulkExport.summaries.s1", _selectedNames.Count, _exportMode.ToLower());
                case "S2": return HasActivePrintSets()
                    ? AppStrings.T("export.bulkExport.summaries.s2PrintSets", _selectedPrintSetIds.Count)
                    : AppStrings.T("export.bulkExport.summaries.s2Individual");
                case "S3": return GetActiveFormats() == "—"
                    ? AppStrings.T("export.bulkExport.summaries.s3None")
                    : AppStrings.T("export.bulkExport.summaries.s3", GetActiveFormats(), ActivePattern);
                case "S4": return AppStrings.T("export.bulkExport.summaries.s4", _hiddenLines.Split(' ')[0], _rasterQuality, _colorDepth, _pdfPlacement.Split(' ')[0]);
                case "S5": return string.IsNullOrEmpty(_dwgSetup) ? AppStrings.T("export.bulkExport.summaries.s5Default") : _dwgSetup;
                case "S6": return AppStrings.T("export.bulkExport.summaries.s6", _nwcCoordinates, _nwcParameters);
                case "S7": return _ifcVersion;
                case "S8": return string.IsNullOrEmpty(_outputFolder) ? AppStrings.T("export.bulkExport.summaries.s8NoFolder") : _outputFolder;
                default:   return "—";
            }
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            if (_handler == null || _event == null) return;

            _resultChips = null;   // clear any breakdown from a previous run

            // Persist settings (paths/patterns are Settings-window-only now — see ApplySettings)
            var s = BulkExportSettings.Instance;
            s.ExportPdf                    = _pdfOn;
            s.ExportDwg                    = _dwgOn;
            s.ExportNwc                    = _nwcOn;
            s.NwcCoordinates               = _nwcCoordinates;
            s.NwcParameters                = _nwcParameters;
            s.NwcConvertElementProps       = _nwcConvertElementProps;
            s.NwcDivideByLevel             = _nwcDivideByLevel;
            s.NwcExportLinks               = _nwcExportLinks;
            s.NwcExportParts               = _nwcExportParts;
            s.NwcExportElementIds          = _nwcExportElementIds;
            s.NwcExportUrls                = _nwcExportUrls;
            s.NwcFindMissingMaterials      = _nwcFindMissingMaterials;
            s.NwcExportRoomGeometry        = _nwcExportRoomGeometry;
            s.NwcExportRoomAsAttribute     = _nwcExportRoomAsAttr;
            s.NwcConvertLights             = _nwcConvertLights;
            s.NwcConvertLinkedCad          = _nwcConvertLinkedCad;
            s.NwcFacetingFactor            = _nwcFacetingFactor;
            s.ExportIfc                    = _ifcOn;
            s.IfcVersion                   = _ifcVersion;
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
            s.Save();

            // Print sets to export: every checked set, each carrying its own overrides
            // (null = inherit the tool's global pattern/format for that field).
            var printSetsToExport = _availablePrintSets
                .Where(ps => _selectedPrintSetIds.Contains(ps.Id.Value))
                .Select(ps => new PrintSetExportSpec
                {
                    Name            = ps.Name,
                    MemberIds       = new List<ElementId>(ps.MemberIds),
                    PatternOverride = _patternOverrides.TryGetValue(ps.Id.Value, out var pat) ? pat : null,
                    PdfOverride     = _pdfOverrides.TryGetValue(ps.Id.Value, out var pdf) ? pdf : null,
                    DwgOverride     = _dwgOverrides.TryGetValue(ps.Id.Value, out var dwg) ? dwg : null,
                })
                .ToList();

            _handler.SelectedIds              = _selectedNames
                .Where(n => _nameToId.ContainsKey(n))
                .Select(n => _nameToId[n])
                .ToList();
            _handler.ExportMode               = _exportMode;
            // Send the pattern for the mode being exported — its tokens are guaranteed
            // valid for those elements.
            _handler.FilenamePattern          = ActivePattern;
            _handler.OutputFolder             = _outputFolder;
            _handler.SplitByFormat            = _splitByFormat;
            _handler.ExportPdf                = _pdfOn;
            _handler.ExportDwg                = _dwgOn;
            _handler.ExportNwc                = _nwcOn;
            _handler.NwcCoordinates           = _nwcCoordinates;
            _handler.NwcParameters            = _nwcParameters;
            _handler.NwcConvertElementProps   = _nwcConvertElementProps;
            _handler.NwcDivideByLevel         = _nwcDivideByLevel;
            _handler.NwcExportLinks           = _nwcExportLinks;
            _handler.NwcExportParts           = _nwcExportParts;
            _handler.NwcExportElementIds      = _nwcExportElementIds;
            _handler.NwcExportUrls            = _nwcExportUrls;
            _handler.NwcFindMissingMaterials  = _nwcFindMissingMaterials;
            _handler.NwcExportRoomGeometry    = _nwcExportRoomGeometry;
            _handler.NwcExportRoomAsAttribute = _nwcExportRoomAsAttr;
            _handler.NwcConvertLights         = _nwcConvertLights;
            _handler.NwcConvertLinkedCad      = _nwcConvertLinkedCad;
            _handler.NwcFacetingFactor        = _nwcFacetingFactor;
            _handler.ExportIfc                = _ifcOn;
            _handler.IfcVersion               = _ifcVersion;
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
            _handler.PrintSets                = printSetsToExport;
            _handler.PushLog                  = pushLog;
            _handler.OnProgress               = onProgress;
            _handler.OnComplete               = onComplete;
            _handler.OnResultChips            = chips => _resultChips = chips;

            _event.Raise();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  IToolSettings
        // ═════════════════════════════════════════════════════════════════════
        public ToolSettingsSpec? GetSettingsSpec()
        {
            var s = BulkExportSettings.Instance;
            return new ToolSettingsSpec
            {
                Id          = "tx",
                Label       = "Bulk Export",
                Icon        = "Tx",
                Description = "Export sheets and views to PDF, DWG, NWC and IFC with parametric filenames.",
                Groups      = new List<SettingsGroup>
                {
                    new SettingsGroup
                    {
                        Id = "G1", Title = "Output", OpenByDefault = true,
                        Settings = new List<SettingDef>
                        {
                            new SettingDef { Id = "outdir",      Kind = "file",   Label = "Default output folder",
                                Options = new FileOpts { Placeholder = @"C:\Projects\Exports\" }, Default = s.OutputFolder },
                            new SettingDef { Id = "splitformat", Kind = "toggle", Label = "Split output by file format",
                                Hint = "Creates PDF\\, DWG\\ subfolders automatically.", Default = s.SplitByFormat },
                        }
                    },
                    new SettingsGroup
                    {
                        Id = "G2", Title = "Filename",
                        Settings = new List<SettingDef>
                        {
                            new SettingDef { Id = "pattern", Kind = "text", Label = "Default filename pattern",
                                Hint = "Tokens: {SheetNumber} {SheetName} {Revision} {IssueDate} {ProjectNumber} {Year} {Month} {Day} + any custom tokens defined in Naming settings",
                                Options = new TextOpts { Mono = true, Placeholder = "{SheetNumber}-{SheetName}" },
                                Default = s.FilenamePattern },
                        }
                    },
                    new SettingsGroup
                    {
                        Id = "G3", Title = "Default Formats",
                        Settings = new List<SettingDef>
                        {
                            new SettingDef { Id = "defpdf", Kind = "toggle", Label = "PDF on by default", Default = s.ExportPdf },
                            new SettingDef { Id = "defdwg", Kind = "toggle", Label = "DWG on by default", Default = s.ExportDwg },
                            new SettingDef { Id = "defnwc", Kind = "toggle", Label = "NWC on by default (Views mode only)", Default = s.ExportNwc },
                            new SettingDef { Id = "defifc", Kind = "toggle", Label = "IFC on by default (Views mode only)", Default = s.ExportIfc },
                        }
                    },
                    new SettingsGroup
                    {
                        Id = "G4", Title = "PDF Options",
                        Settings = new List<SettingDef>
                        {
                            new SettingDef { Id = "combinepdf",  Kind = "toggle", Label = "Combine into single PDF by default", Default = s.CombinePdf },
                            new SettingDef { Id = "placement",   Kind = "single", Label = "Paper placement",
                                Options = new SingleSelectOpts { Items = new List<string> { "Center", "Offset from Corner" } },
                                Default = s.PdfPaperPlacement },
                            new SettingDef { Id = "hiddenlines", Kind = "single", Label = "Hidden line views",
                                Options = new SingleSelectOpts { Items = new List<string> { "Vector Processing", "Raster Processing" } },
                                Default = s.HiddenLinesVector ? "Vector Processing" : "Raster Processing" },
                            new SettingDef { Id = "colordepth",  Kind = "single", Label = "Color depth",
                                Options = new SingleSelectOpts { Items = new List<string> { "Color", "Grayscale", "Black & White" } },
                                Default = s.ColorDepth },
                            new SettingDef { Id = "rasterquality", Kind = "single", Label = "Raster quality",
                                Options = new SingleSelectOpts { Items = new List<string> { "Draft", "Low", "Medium", "High", "Presentation" } },
                                Default = s.RasterQuality },
                            new SettingDef { Id = "zoomsetting", Kind = "single", Label = "Zoom",
                                Options = new SingleSelectOpts { Items = new List<string> { "Fit to Page", "Scale %" } },
                                Default = s.ZoomSetting },
                            new SettingDef { Id = "zoompercent",  Kind = "number", Label = "Zoom percent (when Scale % mode)", Default = s.ZoomPercent },
                            new SettingDef { Id = "viewlinksblue",    Kind = "toggle", Label = "View links in blue",               Default = s.ViewLinksInBlue },
                            new SettingDef { Id = "replacehalftone",  Kind = "toggle", Label = "Replace halftone with thin lines", Default = s.ReplaceHalftoneWithThinLines },
                        }
                    },
                    new SettingsGroup
                    {
                        Id = "G5", Title = "DWG Options",
                        Settings = new List<SettingDef>
                        {
                            new SettingDef { Id = "dwgsetup", Kind = "text", Label = "Default DWG export setup name",
                                Hint = "Must match a setup created in Revit via File → Export → DWG.",
                                Options = new TextOpts { Placeholder = "Standard DWG" }, Default = s.DwgExportSetupName },
                        }
                    },
                    new SettingsGroup
                    {
                        Id = "G7", Title = "NWC Options",
                        Settings = new List<SettingDef>
                        {
                            new SettingDef { Id = "nwccoords",    Kind = "single", Label = "Coordinate system",
                                Options = new SingleSelectOpts { Items = new List<string> { "Shared", "Internal" } }, Default = s.NwcCoordinates },
                            new SettingDef { Id = "nwcparams",    Kind = "single", Label = "Element parameters",
                                Options = new SingleSelectOpts { Items = new List<string> { "All", "Elements", "None" } }, Default = s.NwcParameters },
                            new SettingDef { Id = "nwcfaceting",  Kind = "single", Label = "Mesh quality (faceting factor)",
                                Options = new SingleSelectOpts { Items = new List<string>(NwcFacetingLabels) }, Default = NwcFacetingLabels[1] },
                            new SettingDef { Id = "nwcconvelemprop",  Kind = "toggle", Label = "Convert element properties",     Default = s.NwcConvertElementProps },
                            new SettingDef { Id = "nwcdivide",        Kind = "toggle", Label = "Divide file into levels",         Default = s.NwcDivideByLevel },
                            new SettingDef { Id = "nwclinks",         Kind = "toggle", Label = "Include linked Revit models",     Default = s.NwcExportLinks },
                            new SettingDef { Id = "nwcparts",         Kind = "toggle", Label = "Include Revit parts",             Default = s.NwcExportParts },
                            new SettingDef { Id = "nwcelementids",    Kind = "toggle", Label = "Include element IDs",             Default = s.NwcExportElementIds },
                            new SettingDef { Id = "nwcurls",          Kind = "toggle", Label = "Include URL parameters",          Default = s.NwcExportUrls },
                            new SettingDef { Id = "nwcmissingmats",   Kind = "toggle", Label = "Find missing materials",          Default = s.NwcFindMissingMaterials },
                            new SettingDef { Id = "nwcroomgeo",       Kind = "toggle", Label = "Export room geometry",            Default = s.NwcExportRoomGeometry },
                            new SettingDef { Id = "nwcroomattr",      Kind = "toggle", Label = "Attach room data as attributes",  Default = s.NwcExportRoomAsAttribute },
                            new SettingDef { Id = "nwclights",        Kind = "toggle", Label = "Convert Revit lights",            Default = s.NwcConvertLights },
                            new SettingDef { Id = "nwclinkedcad",     Kind = "toggle", Label = "Convert linked CAD formats",      Default = s.NwcConvertLinkedCad },
                        }
                    },
                    new SettingsGroup
                    {
                        Id = "G6", Title = "IFC Options",
                        Settings = new List<SettingDef>
                        {
                            new SettingDef { Id = "ifcversion", Kind = "single", Label = "Default IFC version",
                                Options = new SingleSelectOpts { Items = new List<string> { "IFC2x3", "IFC4" } },
                                Default = s.IfcVersion },
                        }
                    },
                }
            };
        }

        public void ApplySettings(string groupId, string settingId, object value)
        {
            var s = BulkExportSettings.Instance;
            switch (settingId)
            {
                case "outdir":          s.OutputFolder               = value as string ?? "";                      break;
                case "splitformat":     s.SplitByFormat              = value is bool b1 && b1;                     break;
                case "pattern":         s.FilenamePattern            = value as string ?? "";                      break;
                case "defpdf":          s.ExportPdf                  = value is bool b2 && b2;                     break;
                case "defdwg":          s.ExportDwg                  = value is bool b3 && b3;                     break;
                case "defnwc":          s.ExportNwc                  = value is bool b5 && b5;                     break;
                case "defifc":          s.ExportIfc                  = value is bool b6 && b6;                     break;
                case "combinepdf":      s.CombinePdf                 = value is bool b4 && b4;                     break;
                case "placement":       s.PdfPaperPlacement          = value as string ?? "Center";                break;
                case "hiddenlines":     s.HiddenLinesVector          = value as string == "Vector Processing";     break;
                case "colordepth":      s.ColorDepth                 = value as string ?? "Color";                 break;
                case "rasterquality":   s.RasterQuality              = value as string ?? "High";                  break;
                case "zoomsetting":     s.ZoomSetting                = value as string ?? "Fit to Page";           break;
                case "zoompercent":     s.ZoomPercent                = value is int zi ? zi : 100;                 break;
                case "viewlinksblue":   s.ViewLinksInBlue            = value is bool vl && vl;                     break;
                case "replacehalftone": s.ReplaceHalftoneWithThinLines = value is bool rh && rh;                   break;
                case "dwgsetup":        s.DwgExportSetupName         = value as string ?? "";                      break;
                case "nwccoords":       s.NwcCoordinates             = value as string ?? "Shared";                break;
                case "nwcparams":       s.NwcParameters              = value as string ?? "All";                   break;
                case "nwcfaceting":
                {
                    int fi = Array.IndexOf(NwcFacetingLabels, value as string ?? "");
                    s.NwcFacetingFactor = fi >= 0 ? NwcFacetingValues[fi] : 1.0;
                    break;
                }
                case "nwcconvelemprop": s.NwcConvertElementProps    = value is bool c1 && c1; break;
                case "nwcdivide":       s.NwcDivideByLevel          = value is bool c2 && c2; break;
                case "nwclinks":        s.NwcExportLinks            = value is bool c3 && c3; break;
                case "nwcparts":        s.NwcExportParts            = value is bool c4 && c4; break;
                case "nwcelementids":   s.NwcExportElementIds       = value is bool c5 && c5; break;
                case "nwcurls":         s.NwcExportUrls             = value is bool c6 && c6; break;
                case "nwcmissingmats":  s.NwcFindMissingMaterials   = value is bool c7 && c7; break;
                case "nwcroomgeo":      s.NwcExportRoomGeometry     = value is bool c8 && c8; break;
                case "nwcroomattr":     s.NwcExportRoomAsAttribute  = value is bool c9 && c9; break;
                case "nwclights":       s.NwcConvertLights          = value is bool d1 && d1; break;
                case "nwclinkedcad":    s.NwcConvertLinkedCad       = value is bool d2 && d2; break;
                case "ifcversion":      s.IfcVersion                = value as string ?? "IFC2x3"; break;
            }
            s.Save();
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
                Template        = ControlStyles.BuildFlatButtonTemplate(),
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

        private static TextBlock Dim(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 0, 0, 8) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return tb;
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
            ControlStyles.WireComboWheelBubbling(combo); // don't eat page scroll when closed
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
            if (_nwcOn) fmts.Add("NWC");
            if (_ifcOn) fmts.Add("IFC");
            return fmts.Count > 0 ? string.Join(", ", fmts) : "—";
        }

        private bool HasActivePrintSets() => _selectedPrintSetIds.Count > 0;

        private static int GetIndex(string[] items, string value)
        {
            int idx = Array.IndexOf(items, value);
            return idx >= 0 ? idx : 0;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Sheet / view grouping helpers
        // ═════════════════════════════════════════════════════════════════════

        private static ViewFamily GetViewFamily(View v)
        {
            if (v is View3D)    return ViewFamily.ThreeDimensional;
            if (v is ViewPlan vp) return vp.ViewType == ViewType.CeilingPlan
                ? ViewFamily.CeilingPlan : ViewFamily.FloorPlan;
            if (v is ViewSection) return v.ViewType == ViewType.Elevation
                ? ViewFamily.Elevation : ViewFamily.Section;
            return ViewFamily.Invalid;
        }

    }
}
