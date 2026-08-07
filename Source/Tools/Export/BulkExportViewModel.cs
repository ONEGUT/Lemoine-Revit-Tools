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

using WpfGrid       = System.Windows.Controls.Grid;
using WpfTextBox    = System.Windows.Controls.TextBox;
using WpfComboBox   = System.Windows.Controls.ComboBox;
using WpfVisibility = System.Windows.Visibility;
using WpfBrushes    = System.Windows.Media.Brushes;

namespace LemoineTools.Tools.BulkExport
{
    public partial class BulkExportViewModel : IStepFlowTool, IReviewableTool, IConditionalSteps, IStepAware, IRunResult, IToolCleanup
    {
        // Run strip: "files" during the run, per-format breakdown ("30 PDF · 30 DWG") on completion.
        public string? ResultNoun => "files";
        private System.Collections.Generic.IReadOnlyList<LemoineTools.Framework.ResultChip>? _resultChips;
        public System.Collections.Generic.IReadOnlyList<LemoineTools.Framework.ResultChip>? ResultChips => _resultChips;

        // Null the callbacks parked on the static handlers so this VM isn't retained after close.
        public void OnWindowClosed()
        {
            if (_handler != null)
            {
                _handler.PushLog       = null;
                _handler.OnProgress    = null;
                _handler.OnComplete    = null;
                _handler.OnResultChips = null;
            }

            // The print-set handler is session-long too, and SaveCurrentSelectionAsPrintSet parks
            // an OnCreated closure that captures this VM and its live step UI — clear it, or a
            // window that saved a print set stays rooted until the next save (or forever this session).
            var psHandler = App.BulkExportPrintSetHandler;
            if (psHandler != null)
            {
                psHandler.OnCreated = null;
                psHandler.OnError   = null;
            }

            // Same reasoning for the set-layout store handler — its callbacks close over this
            // VM and its live step UI.
            var storeHandler = App.BulkExportSetStoreHandler;
            if (storeHandler != null)
            {
                storeHandler.OnSaved = null;
                storeHandler.OnError = null;
            }

            if (_picker != null) _picker.RowBadgeProvider = null;
            _picker = null;
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
            new StepDefinition("S2", AppStrings.T("export.bulkExport.steps.S2"), required: true),
            new StepDefinition("S3", AppStrings.T("export.bulkExport.steps.S3"), required: false),
            new StepDefinition("S4", AppStrings.T("export.bulkExport.steps.S4"), required: false),
            new StepDefinition("S5", AppStrings.T("export.bulkExport.steps.S5"), required: false),
            new StepDefinition("S6", AppStrings.T("export.bulkExport.steps.S6"), required: false),
            new StepDefinition("S7", AppStrings.T("export.bulkExport.steps.S7"), required: true),
            new StepDefinition("S8", AppStrings.T("export.bulkExport.steps.S8"), required: false),
        };

        // ── IConditionalSteps ──────────────────────────────────────────
        public bool IsStepVisible(string stepId)
        {
            switch (stepId)
            {
                case "S3": return _pdfOn;
                case "S4": return _dwgOn;
                case "S5": return _nwcOn;
                case "S6": return _ifcOn;
                default:   return true;
            }
        }

        // ── IStepAware ────────────────────────────────────────────────────────
        // Step content is built eagerly when the window opens, so steps that depend on
        // earlier choices must be rebuilt when the user navigates to them:
        //   S2 reads the live S1 selection, S3's token picker matches the export mode,
        //   S6's note reflects sheets-vs-views mode.
        internal Action<string>? _refreshStep;
        public void SetContentRefreshCallback(Action<string> rebuildStepContent) => _refreshStep = rebuildStepContent;
        public void OnStepActivated(string stepId)
        {
            // S1 too: its target-set dropdown lists the sets, which Step 2 can change.
            // Rebuilding re-fires SelectionChanged with the surviving selection, which the
            // assignment diff sees as a no-op unless ids genuinely dropped out.
            if (stepId == "S1" || stepId == "S2" || stepId == "S5")
                _refreshStep?.Invoke(stepId);
        }

        public event EventHandler? ValidationChanged;
        internal void Fire() => ValidationChanged?.Invoke(this, EventArgs.Empty);

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
        private const string SetDefaultPattern   = "{SetName}";

        private bool ViewsMode => _exportMode == "Views";

        // ── S1 state ──────────────────────────────────────────────────────────
        private string                        _exportMode    = "Sheets";
        // Ordered by Project Browser position (see _browserRank) — this list IS the export order,
        // so it is stored as ids rather than display names: the old name round-trip both lost the
        // order and could not survive two items resolving to the same display key.
        internal List<long>                   _selectedIds   = new List<long>();

        // ── S2 state (print sets) ──────────────────────────────────────────────
        // Existing Revit print sets (ViewSheetSet elements), refreshed after creating a new
        // one. Checking a set makes it an export group with its own optional overrides.
        internal List<PrintSetInfo>      _availablePrintSets;
        // Live handles into the S2 "Save as print set" row so the create callback can clear
        // the name box and show a success/error message (rebuilt each time S2 is activated).

        // ── Format state ──────────────────────────────────────────────────────
        // There is no naming step. A set's file is called what the user typed as the set's name;
        // an individual file is called after its own sheet or view. Sheet NAMES are not unique in
        // Revit (numbers are), so a sheet file keeps its number — dropping it would make two
        // sheets collide and silently land as "… (2)".
        private string ActivePattern => ViewsMode ? ViewDefaultPattern : SheetDefaultPattern;
        internal bool              _pdfOn           = BulkExportSettings.Instance.ExportPdf;
        internal bool              _dwgOn           = BulkExportSettings.Instance.ExportDwg;
        private bool               _nwcOn           = BulkExportSettings.Instance.ExportNwc;
        private bool               _ifcOn           = BulkExportSettings.Instance.ExportIfc;
        private string             _ifcVersion      = BulkExportSettings.Instance.IfcVersion;
        private string             _dwgSetup        = BulkExportSettings.Instance.DwgExportSetupName;

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
        private string _exportQualityDpi = BulkExportSettings.Instance.PdfExportQualityDpi;
        private bool   _viewLinksBlue   = BulkExportSettings.Instance.ViewLinksInBlue;
        private bool   _replaceHalftone = BulkExportSettings.Instance.ReplaceHalftoneWithThinLines;

        // ── S5 state (output) ─────────────────────────────────────────────────
        private string _outputFolder  = BulkExportSettings.Instance.OutputFolder;
        private bool   _splitByFormat = BulkExportSettings.Instance.SplitByFormat;
        private bool   _setSubfolders = BulkExportSettings.Instance.SetSubfolders;
        private string _existingFileAction = BulkExportSettings.Instance.ExistingFileAction;

        // A set's combined PDF is simply named after the set. The user types the set name in the
        // rail, so the name they typed IS the filename — no pattern to author, nothing to resolve.
        private const string SetNamePattern = "{SetName}";

        // ── Revit data ────────────────────────────────────────────────────────
        internal readonly List<ViewSheet>            _allSheets;
        internal readonly List<View>                 _allViews;
        private readonly List<string>                _dwgSetupNames;
        private readonly Dictionary<ElementId, ViewSheet> _sheetById;
        internal readonly BrowserTree         _browserTree;
        internal readonly Dictionary<long, string>   _idToName = new Dictionary<long, string>();

        // Project Browser display position for every captured leaf, by ElementId.Value.
        // BrowserTreePicker hands its selection back as a HashSet, whose enumeration order is
        // unspecified — so without this the export order (and a combined PDF's page order) is
        // whatever the hash happens to yield. Every selection is sorted through this map.
        internal readonly Dictionary<long, int> _browserRank = new Dictionary<long, int>();

        // ── Preview (token preview on the naming step) ────────────────────────
        private string _previewSheetNumber = "A101";
        private string _previewSheetName   = "Ground Floor";

        // Real project info, captured on the main thread at launch. The previews resolve the
        // same tokens the run will, so a set card's filename is the filename — not a sample.
        private readonly string _previewProjectNumber;
        private readonly string _previewProjectName;

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
            List<PrintSetInfo>? availablePrintSets = null,
            ExportSetLayout?    storedLayout       = null,
            string              projectNumber      = "",
            string              projectName        = "")
        {
            _previewProjectNumber = projectNumber;
            _previewProjectName   = projectName;
            _handler       = handler;
            _event         = externalEvent;
            _dwgSetupNames = dwgSetupNames;
            _allSheets     = allSheets;
            _allViews      = allViews;
            _browserTree   = browserTree;
            _availablePrintSets = availablePrintSets ?? new List<PrintSetInfo>();

            BuildBrowserRanks();

            // Build fast ID→Sheet lookup
            _sheetById = new Dictionary<ElementId, ViewSheet>();
            foreach (var s in _allSheets)
            {
                _sheetById[s.Id] = s;
                string key = $"{s.SheetNumber} — {s.Name}";
                if (!_idToName.ContainsKey(s.Id.Value)) _idToName[s.Id.Value] = key;
            }
            foreach (var v in _allViews)
            {
                string key = v.Name;
                if (!_idToName.ContainsKey(v.Id.Value)) _idToName[v.Id.Value] = key;
            }

            // Restore the set layout stored in this document. Must run after _idToName and
            // _browserRank are populated — ApplyLayout re-resolves every member against them.
            ApplyLayout(storedLayout);

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

        // Ids checked in the current SelectionChanged batch, filed after the selection field is
        // updated so AssignToTarget sees a consistent view of what is selected.
        private readonly List<long> _pendingAssign = new List<long>();

        // ── PDF granularity labels ────────────────────────────────────────────

        // The settings window shows sentences; the file stores the enum name. Persisted tokens
        // stay hardcoded strings by house rule — they are compared and switched on, not displayed.
        private static string GranularityOptionLabel(PdfGranularity g)
        {
            switch (g)
            {
                case PdfGranularity.PerSheet:   return "One PDF per sheet";
                case PdfGranularity.SingleFile: return "One PDF for everything";
                default:                        return "One PDF per set";
            }
        }

        private static string GranularityFromOptionLabel(string? label)
        {
            switch (label)
            {
                case "One PDF per sheet":      return nameof(PdfGranularity.PerSheet);
                case "One PDF for everything": return nameof(PdfGranularity.SingleFile);
                default:                      return nameof(PdfGranularity.PerSet);
            }
        }

        internal string GranularityLabel()
        {
            switch (_granularity)
            {
                case PdfGranularity.PerSheet:   return AppStrings.T("export.bulkExport.sets.perSheetShort");
                case PdfGranularity.SingleFile: return AppStrings.T("export.bulkExport.sets.singleFileShort");
                default:                        return AppStrings.T("export.bulkExport.sets.perSetShort");
            }
        }

        // ── Export order ──────────────────────────────────────────────────────

        // Depth-first walk of the captured browser tree: index i is the leaf's Project Browser
        // display position. Runs once at construction; the tree is a Revit-free snapshot so this
        // is safe off the main thread.
        private void BuildBrowserRanks()
        {
            if (_browserTree == null) return;
            int next = 0;
            void Walk(BrowserNode n)
            {
                if (n.Id.HasValue && !_browserRank.ContainsKey(n.Id.Value))
                    _browserRank[n.Id.Value] = next++;
                foreach (var child in n.Children) Walk(child);
            }
            foreach (var root in _browserTree.Roots) Walk(root);
        }

        /// <summary>
        /// The tool's single definition of export order: Project Browser position. Ids missing
        /// from the captured tree (a view the browser does not list) sort last in natural name
        /// order, so the result is always deterministic — never the caller's hash order.
        /// </summary>
        private List<long> OrderByBrowser(IEnumerable<long> ids)
            => ids.OrderBy(id => _browserRank.TryGetValue(id, out int rank) ? rank : int.MaxValue)
                  .ThenBy(id => _idToName.TryGetValue(id, out var name) ? name : "",
                          NaturalOrderComparer.OrdinalIgnoreCase)
                  .ToList();

        private List<ElementId> SelectedElementIds()
            => _selectedIds.Select(id => new ElementId(id)).ToList();

        //  GetStepContent
        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildS1();
                case "S2": return BuildSetsAndOrder();
                case "S3": return BuildPdfSettings();
                case "S4": return BuildDwgSettings();
                case "S5": return BuildNwcSettings();
                case "S6": return BuildIfcSettings();
                case "S7": return BuildOutput();
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

            // Sets are a tab rail down the left of the tree, not a bar above it: the active set
            // is a persistent mode, and a rail shows every set (and every importable Revit print
            // set) at once instead of hiding them behind a dropdown.
            var split = new WpfGrid { Height = 340, Tag = "multiselect" };
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(158) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var rail = BuildSetRail();
            WpfGrid.SetColumn(rail, 0);
            split.Children.Add(rail);

            var multiSelect = BuildTreePicker(showAllCb);
            multiSelect.Height = double.NaN;   // fill the row instead of its own fixed 300
            WpfGrid.SetColumn(multiSelect, 2);
            split.Children.Add(multiSelect);

            outer.Children.Add(split);

            var assignHint = new TextBlock
            {
                Text         = AppStrings.T("export.bulkExport.sets.assignHint"),
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, 6, 0, 0),
            };
            assignHint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            assignHint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            assignHint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            outer.Children.Add(assignHint);

            return outer;
        }

        private BrowserTreePicker BuildTreePicker(CheckBox showAllCb)
        {
            var picker = new BrowserTreePicker { Height = 300 };
            // Subscribe BEFORE SetTree — per the BrowserTreePicker contract, the
            // single SelectionChanged fired at the end of SetTree is the only mechanism
            // that initialises the mirror field.
            // The badge on each checked row names the set it went into — assigning while
            // selecting makes the active set modal state, and this is what keeps it visible.
            picker.RowBadgeProvider = BadgeFor;

            // Publish the handle BEFORE SetTree: SetTree fires SelectionChanged synchronously at
            // its end, and that handler repaints badges through _picker. Assigning afterwards left
            // it pointing at the previous (now discarded) picker — or at null on the first build —
            // so the tree came up with no badges at all.
            _picker = picker;

            picker.SelectionChanged += ids =>
            {
                var now      = OrderByBrowser(ids);
                var previous = new HashSet<long>(_selectedIds);
                var current  = new HashSet<long>(now);

                foreach (long id in now.Where(id => !previous.Contains(id)))
                    _pendingAssign.Add(id);
                foreach (long id in _selectedIds.Where(id => !current.Contains(id)))
                    RemoveFromAllSets(id);

                _selectedIds = now;
                foreach (long id in _pendingAssign) AssignToTarget(id);
                _pendingAssign.Clear();

                // Repaints badges + the rail in place. Must never rebuild Step 1 from here:
                // SetTree fires this callback at the end of every rebuild, so a rebuild would
                // re-enter it forever.
                RefreshSetRail();
                Fire();
            };
            // Carry the current selection forward — SetTree keeps only the ids still eligible,
            // so a Sheets↔Views switch clears (no old id is eligible in the new mode) while a
            // "Show all" toggle preserves the views that remain pickable.
            picker.SetTree(_browserTree, BuildEligibleIds((bool)(showAllCb.Tag ?? false)), _selectedIds);
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

            var split = new WpfGrid { Height = 340, Tag = "multiselect" };
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(158) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var rail = BuildSetRail();
            WpfGrid.SetColumn(rail, 0);
            split.Children.Add(rail);

            var newPicker = BuildTreePicker(showAllCb ?? new CheckBox { Tag = false });
            newPicker.Height = double.NaN;
            WpfGrid.SetColumn(newPicker, 2);
            split.Children.Add(newPicker);

            outer.Children.Add(split);
            // BuildTreePicker's end-of-SetTree callback already re-seeded _selectedIds with
            // the surviving selection and fired validation; nothing to clear here.
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
        // ── S3 — Filename & Formats ───────────────────────────────────────────
        // Pattern + format toggles only. Each format's own options live in its dedicated
        // step (S4 PDF, S5 DWG, S6 NWC, S7 IFC), shown only when that format is enabled.
        // The dominant output extension for the preview (first enabled format).
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
        private FrameworkElement BuildPdfSettings()
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

            // Hidden Line Views sits above the two raster knobs: it decides whether
            // rasterizing happens at all, and Raster Quality / Export Quality only bite
            // once something is being rasterized.
            AddLabeledComboBox(outer, AppStrings.T("export.bulkExport.labels.lblHiddenLines"),
                ExportOptionsFactory.HiddenLineOptions(),
                GetIndex(ExportOptionsFactory.HiddenLineOptions(), _hiddenLines),
                val => { _hiddenLines = val; Fire(); });

            var hiddenLinesHint = new TextBlock
            {
                Text         = AppStrings.T("export.bulkExport.labels.hiddenLinesHint"),
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, -2, 0, 6),
            };
            hiddenLinesHint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            hiddenLinesHint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            hiddenLinesHint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            outer.Children.Add(hiddenLinesHint);

            AddLabeledComboBox(outer, AppStrings.T("export.bulkExport.labels.lblRasterQuality"),
                new[] { "Draft", "Low", "Medium", "High", "Presentation" },
                GetIndex(new[] { "Draft", "Low", "Medium", "High", "Presentation" }, _rasterQuality),
                val => { _rasterQuality = val; Fire(); });

            AddLabeledComboBox(outer, AppStrings.T("export.bulkExport.labels.lblExportQuality"),
                ExportOptionsFactory.ExportQualityDpiOptions(),
                GetIndex(ExportOptionsFactory.ExportQualityDpiOptions(), _exportQualityDpi),
                val => { _exportQualityDpi = val; Fire(); });

            AddDivider(outer);

            // Granularity (one PDF per sheet / per set / for everything) lives on the Sets step,
            // next to the sets it operates on — one control, one place. The step summary below
            // repeats it so the collapsed PDF row still shows it.

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
        private FrameworkElement BuildDwgSettings()
        {
            var outer = new StackPanel();
            AddSectionLabel(outer, AppStrings.T("export.bulkExport.labels.secDwgOptions"));

            bool hasSetups = _dwgSetupNames.Count > 0;
            var setupNames = hasSetups
                ? _dwgSetupNames.ToArray()
                : new[] { AppStrings.T("export.bulkExport.labels.dwgNoSetups") };
            int initIdx = setupNames.Contains(_dwgSetup) ? Array.IndexOf(setupNames, _dwgSetup) : 0;
            // Don't let the "(No DWG setups found)" placeholder be stored as a setup name —
            // the run would then report it as a missing setup.
            AddLabeledComboBox(outer, AppStrings.T("export.bulkExport.labels.lblExportSetup"), setupNames, initIdx,
                val => { if (hasSetups) { _dwgSetup = val; Fire(); } });

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
        private FrameworkElement BuildNwcSettings()
        {
            var outer = new StackPanel();
            BuildNwcOptions(outer);
            return outer;
        }

        // ── S7 — IFC Settings (shown only when IFC is enabled) ─────────────────
        private FrameworkElement BuildIfcSettings()
        {
            var outer = new StackPanel();
            BuildIfcOptions(outer);
            return outer;
        }

        // ── S8 — Output ───────────────────────────────────────────────────────
        private FrameworkElement BuildOutput()
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

            var subToggle = new ToggleSwitches();
            subToggle.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "setsub", Label = AppStrings.T("export.bulkExport.sets.subfolderLabel"),
                                 Desc = AppStrings.T("export.bulkExport.sets.subfolderDesc"), DefaultOn = _setSubfolders },
            });
            subToggle.StateChanged += state => { state.TryGetValue("setsub", out _setSubfolders); Fire(); };
            outer.Children.Add(subToggle);

            AddDivider(outer);

            // Re-exporting used to overwrite the previous issue in silence — name de-duplication
            // was per-run only and never looked at the disk. Overwrite stays the default (it is
            // what Revit does), but the count is now raised as a warning on the review step.
            AddSectionLabel(outer, AppStrings.T("export.bulkExport.sets.existingHeader"));
            var existingOptions = new[] { "Overwrite", "Skip", "Add suffix" };
            AddLabeledComboBox(outer, AppStrings.T("export.bulkExport.sets.existingLabel"),
                existingOptions, GetIndex(existingOptions, _existingFileAction),
                val => { _existingFileAction = val; Fire(); });

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
            ["sheets"]  = _selectedIds.Count == 0 ? "—" : AppStrings.T("export.bulkExport.review.sheetsValue", _selectedIds.Count),
            ["formats"] = GetActiveFormats(),
            ["packs"]   = AppStrings.T("export.bulkExport.review.setsValue", _sets.Count, GranularityLabel()),
            ["quality"] = _pdfOn ? AppStrings.T("export.bulkExport.review.qualityValue", _hiddenLines.Split(' ')[0], _exportQualityDpi, _rasterQuality, _colorDepth) : AppStrings.T("export.bulkExport.review.qualityPdfOff"),
            ["pattern"] = AppStrings.T("export.bulkExport.review.namingRule"),
            ["folder"]  = _outputFolder.Length == 0 ? "—"
                : _outputFolder.Length > 40 ? "…" + _outputFolder.Substring(_outputFolder.Length - 37)
                : _outputFolder,
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => null;

        // NWC/IFC only export 3D views (Views mode). If either is enabled while in Sheets mode
        // it produces nothing — say so on the final step, not just in the S3 hint.
        public string? ReviewWarning
        {
            get
            {
                var warnings = new List<string>();

                if (!ViewsMode && (_nwcOn || _ifcOn))
                {
                    var fmts = new List<string>();
                    if (_nwcOn) fmts.Add("NWC");
                    if (_ifcOn) fmts.Add("IFC");
                    warnings.Add(AppStrings.T("export.bulkExport.review.warnSheetsMode", string.Join(" & ", fmts)));
                }

                // Once named sets exist, a forgotten item would otherwise ship as a surprise
                // "Unassigned" file in the deliverable.
                int loose = UnassignedIds().Count;
                if (_sets.Count > 0 && loose > 0 && _unassignedEnabled)
                    warnings.Add(AppStrings.T("export.bulkExport.sets.unassignedWarn", loose));

                return warnings.Count == 0 ? null : string.Join("  ", warnings);
            }
        }

        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedIds.Count > 0;
                // Sets are optional (an ungrouped selection is one set), but a format is not.
                case "S2": return _pdfOn || _dwgOn || _nwcOn || _ifcOn;
                case "S3": return true;   // PDF settings
                case "S4": return true;   // DWG settings
                case "S5": return true;   // NWC settings
                case "S6": return true;   // IFC settings
                case "S7": return !string.IsNullOrWhiteSpace(_outputFolder);
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedIds.Count == 0 ? "—"
                    : AppStrings.T("export.bulkExport.summaries.s1", _selectedIds.Count, _exportMode.ToLower());
                case "S2": return GetActiveFormats() == "—"
                    ? AppStrings.T("export.bulkExport.summaries.s2None")
                    : _sets.Count == 0
                        ? AppStrings.T("export.bulkExport.summaries.s2Individual", GetActiveFormats(), GranularityLabel())
                        : AppStrings.T("export.bulkExport.summaries.s2Sets", _sets.Count, GetActiveFormats(), GranularityLabel());
                case "S3": return AppStrings.T("export.bulkExport.summaries.s4", _hiddenLines.Split(' ')[0], _exportQualityDpi, _rasterQuality, _colorDepth, GranularityLabel());
                case "S4": return string.IsNullOrEmpty(_dwgSetup) ? AppStrings.T("export.bulkExport.summaries.s5Default") : _dwgSetup;
                case "S5": return AppStrings.T("export.bulkExport.summaries.s6", _nwcCoordinates, _nwcParameters);
                case "S6": return _ifcVersion;
                case "S7": return string.IsNullOrEmpty(_outputFolder) ? AppStrings.T("export.bulkExport.summaries.s8NoFolder") : _outputFolder;
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

            // Persist settings (paths/patterns are Settings-window-only now — the
            // GlobalSettingsWindow Export tab writes them directly to BulkExportSettings).
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
            s.PdfGranularity               = _granularity.ToString();
            s.PdfPaperPlacement            = _pdfPlacement;
            s.HiddenLinesVector            = _hiddenLines == "Vector Processing";
            s.PdfExportQualityDpi          = _exportQualityDpi;
            s.DwgExportSetupName           = _dwgSetup;
            s.ColorDepth                   = _colorDepth;
            s.RasterQuality                = _rasterQuality;
            s.ZoomSetting                  = _zoomSetting;
            s.ZoomPercent                  = _zoomPct;
            s.ViewLinksInBlue              = _viewLinksBlue;
            s.ReplaceHalftoneWithThinLines = _replaceHalftone;
            s.Save();

            _handler.Sets                     = BuildRunSetsFromModel();
            _handler.Granularity              = _granularity;
            _handler.SetFilenamePattern       = SetNamePattern;
            _handler.SetSubfolders            = _setSubfolders;
            _handler.ExistingFileAction       = _existingFileAction;
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
            _handler.DwgSetupName             = _dwgSetup;
            _handler.PdfPlacement             = _pdfPlacement;
            _handler.HiddenLines              = _hiddenLines;
            _handler.ExportQualityDpi         = _exportQualityDpi;
            _handler.ColorDepth               = _colorDepth;
            _handler.RasterQuality            = _rasterQuality;
            _handler.ZoomSetting              = _zoomSetting;
            _handler.ZoomPercent              = _zoomPct;
            _handler.ViewLinksInBlue          = _viewLinksBlue;
            _handler.ReplaceHalftoneWithThinLines = _replaceHalftone;
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
                            new SettingDef { Id = "setsubfolders", Kind = "toggle", Label = "Give each set its own subfolder",
                                Hint = "Nests under the format folder, e.g. PDF\\Architectural\\.", Default = s.SetSubfolders },
                            new SettingDef { Id = "existingfile", Kind = "single", Label = "If an output file already exists",
                                Hint = "Overwrite matches Revit's own behaviour; the review step always reports the count.",
                                Options = new SingleSelectOpts { Items = new List<string> { "Overwrite", "Skip", "Add suffix" } },
                                Default = s.ExistingFileAction },
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
                            new SettingDef { Id = "granularity", Kind = "single", Label = "PDF output by default",
                                Options = new SingleSelectOpts { Items = new List<string> { "One PDF per sheet", "One PDF per set", "One PDF for everything" } },
                                Default = GranularityOptionLabel(ExportSetLayout.ParseGranularity(s.PdfGranularity)) },
                            new SettingDef { Id = "placement",   Kind = "single", Label = "Paper placement",
                                Options = new SingleSelectOpts { Items = new List<string> { "Center", "Offset from Corner" } },
                                Default = s.PdfPaperPlacement },
                            new SettingDef { Id = "hiddenlines", Kind = "single", Label = "Hidden line views",
                                Hint = "Vector keeps linework as vectors where possible; shaded and transparent views always rasterize. Raster forces every view to an image.",
                                Options = new SingleSelectOpts { Items = new List<string>(ExportOptionsFactory.HiddenLineOptions()) },
                                Default = s.HiddenLinesVector ? "Vector Processing" : "Raster Processing" },
                            new SettingDef { Id = "exportquality", Kind = "single", Label = "Export quality (DPI)",
                                Hint = "Output resolution of anything rasterized — applies under vector processing too.",
                                Options = new SingleSelectOpts { Items = new List<string>(ExportOptionsFactory.ExportQualityDpiOptions()) },
                                Default = s.PdfExportQualityDpi },
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
                case "setsubfolders":   s.SetSubfolders              = value is bool sf && sf;                     break;
                case "existingfile":    s.ExistingFileAction         = value as string ?? "Overwrite";             break;
                case "defpdf":          s.ExportPdf                  = value is bool b2 && b2;                     break;
                case "defdwg":          s.ExportDwg                  = value is bool b3 && b3;                     break;
                case "defnwc":          s.ExportNwc                  = value is bool b5 && b5;                     break;
                case "defifc":          s.ExportIfc                  = value is bool b6 && b6;                     break;
                case "granularity":     s.PdfGranularity             = GranularityFromOptionLabel(value as string); break;
                case "placement":       s.PdfPaperPlacement          = value as string ?? "Center";                break;
                case "hiddenlines":     s.HiddenLinesVector          = value as string == "Vector Processing";     break;
                case "exportquality":   s.PdfExportQualityDpi        = value as string ?? "300 DPI";              break;
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

        internal Button BuildModeButton(string label, bool active)
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

        internal static void ApplyModeButtonStyle(Button b, bool active)
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

        internal static void AddSectionLabel(System.Windows.Controls.Panel parent, string text)
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

        internal static TextBlock Dim(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 0, 0, 8) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return tb;
        }

        internal static void AddDivider(System.Windows.Controls.Panel parent)
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
