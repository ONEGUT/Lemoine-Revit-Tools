using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Naming;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.BulkExport
{
    /// <summary>Web port of <see cref="BulkExportViewModel"/> — export sheets/views to
    /// PDF/DWG/NWC/IFC with parametric filenames and print-set groups. Same handler, settings,
    /// and AppStrings keys. Per-format steps hide via IsStepVisible; print-set rows are dynamic
    /// inputs keyed by set id; Save-as-print-set runs through IWebToolAction with the created
    /// list rebuilding S2 from the Revit thread.</summary>
    public class BulkExportWebTool : WebToolBase, IWebToolCleanup, IWebStepRefresh, IWebToolAction
    {
        private static readonly string[] NwcFacetingLabels = { "Low — 0.5", "Standard — 1.0", "High — 2.0", "Ultra — 5.0" };
        private static readonly double[] NwcFacetingValues = { 0.5, 1.0, 2.0, 5.0 };

        private const string SheetDefaultPattern = "{SheetNumber}-{SheetName}";
        private const string ViewDefaultPattern  = "{ViewName}";

        private readonly BulkExportEventHandler? _handler;
        private readonly ExternalEvent?          _event;

        // ── S1 state ──────────────────────────────────────────────────────────
        private string _exportMode = "Sheets";
        private bool   _showAllViews;
        private List<string> _selectedNames = new List<string>();
        private readonly Dictionary<string, ElementId> _nameToId = new Dictionary<string, ElementId>();
        private readonly Dictionary<long, string>      _idToName = new Dictionary<long, string>();

        // ── S2 state (print sets) ─────────────────────────────────────────────
        private List<PrintSetInfo> _availablePrintSets;
        private readonly HashSet<long> _selectedPrintSetIds = new HashSet<long>();
        private readonly Dictionary<long, string?> _patternOverrides = new Dictionary<long, string?>();
        private readonly Dictionary<long, bool?>   _pdfOverrides     = new Dictionary<long, bool?>();
        private readonly Dictionary<long, bool?>   _dwgOverrides     = new Dictionary<long, bool?>();
        private string _newPrintSetName = "";

        // ── S3 state ──────────────────────────────────────────────────────────
        private string _sheetPattern = BulkExportSettings.Instance.FilenamePattern;
        private string _viewPattern  = BulkExportSettings.Instance.ViewFilenamePattern;
        private bool ViewsMode => _exportMode == "Views";
        private string ActivePattern
        {
            get => ViewsMode ? _viewPattern : _sheetPattern;
            set { if (ViewsMode) _viewPattern = value; else _sheetPattern = value; }
        }
        private bool   _pdfOn      = BulkExportSettings.Instance.ExportPdf;
        private bool   _dwgOn      = BulkExportSettings.Instance.ExportDwg;
        private bool   _nwcOn      = BulkExportSettings.Instance.ExportNwc;
        private bool   _ifcOn      = BulkExportSettings.Instance.ExportIfc;
        private string _ifcVersion = BulkExportSettings.Instance.IfcVersion;
        private string _dwgSetup   = BulkExportSettings.Instance.DwgExportSetupName;

        // ── NWC state ─────────────────────────────────────────────────────────
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

        // ── S4 state (PDF) ────────────────────────────────────────────────────
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

        // ── S8 state ──────────────────────────────────────────────────────────
        private string _outputFolder  = BulkExportSettings.Instance.OutputFolder;
        private bool   _splitByFormat = BulkExportSettings.Instance.SplitByFormat;

        // ── Revit data ────────────────────────────────────────────────────────
        private readonly List<ViewSheet> _allSheets;
        private readonly List<View>      _allViews;
        private readonly List<string>    _dwgSetupNames;
        private readonly BrowserTree     _browserTree;

        private string _previewSheetNumber = "A101";
        private string _previewSheetName   = "Ground Floor";

        public event Action<string>? StepInputsChanged;

        public BulkExportWebTool(
            BulkExportEventHandler? handler,
            ExternalEvent?          externalEvent,
            List<string>            dwgSetupNames,
            List<ViewSheet>         allSheets,
            List<View>              allViews,
            BrowserTree             browserTree,
            List<PrintSetInfo>?     availablePrintSets = null)
        {
            _handler       = handler;
            _event         = externalEvent;
            _dwgSetupNames = dwgSetupNames;
            _allSheets     = allSheets;
            _allViews      = allViews;
            _browserTree   = browserTree;
            _availablePrintSets = availablePrintSets ?? new List<PrintSetInfo>();

            foreach (var s in _allSheets)
            {
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

            if (_allSheets.Count > 0)
            {
                _previewSheetNumber = _allSheets[0].SheetNumber;
                _previewSheetName   = _allSheets[0].Name;
            }

            if (string.IsNullOrEmpty(_dwgSetup) && _dwgSetupNames.Count > 0)
                _dwgSetup = _dwgSetupNames[0];
        }

        public override string Title    => AppStrings.T("export.bulkExport.title");
        public override string RunLabel => AppStrings.T("export.bulkExport.runLabel");

        public override bool IsStepVisible(string stepId)
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

        // Which captured browser-tree leaves are pickable in the current mode.
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

        private static ViewFamily GetViewFamily(View v)
        {
            if (v is View3D)    return ViewFamily.ThreeDimensional;
            if (v is ViewPlan vp) return vp.ViewType == ViewType.CeilingPlan
                ? ViewFamily.CeilingPlan : ViewFamily.FloorPlan;
            if (v is ViewSection) return v.ViewType == ViewType.Elevation
                ? ViewFamily.Elevation : ViewFamily.Section;
            return ViewFamily.Invalid;
        }

        // ── Steps ─────────────────────────────────────────────────────────────

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            // ── S1: select sheets/views ───────────────────────────────────────
            var s1 = new WebStep("S1", AppStrings.T("export.bulkExport.steps.S1"))
                .Add(WebInput.SingleSelect("mode", "", _exportMode, new[]
                {
                    new WebOption("Sheets", "Sheets"),
                    new WebOption("Views",  "Views"),
                }));
            if (ViewsMode)
                s1.Add(WebInput.Toggle("showAll", AppStrings.T("export.bulkExport.labels.showAllViews"), _showAllViews));
            var eligible = new HashSet<long>(BuildEligibleIds(_showAllViews));
            var selectedIds = _selectedNames
                .Where(_nameToId.ContainsKey)
                .Select(n => _nameToId[n].Value)
                .Where(eligible.Contains);
            s1.Add(WebInput.BrowserTree("items", "", PruneTree(_browserTree, eligible), selectedIds));

            // ── S2: print sets ────────────────────────────────────────────────
            var s2 = new WebStep("S2", AppStrings.T("export.bulkExport.steps.S2"), required: false)
                .Add(WebInput.Hint("printSetsNote", AppStrings.T("export.bulkExport.labels.printSetsNote")));
            if (_availablePrintSets.Count == 0)
                s2.Add(WebInput.Hint("noPrintSets", AppStrings.T("export.bulkExport.labels.noPrintSets")));
            foreach (var ps in _availablePrintSets)
            {
                long key = ps.Id.Value;
                bool selected = _selectedPrintSetIds.Contains(key);
                s2.Add(WebInput.Toggle($"ps_{key}",
                    AppStrings.T("export.bulkExport.labels.printSetRow", ps.Name, ps.MemberIds.Count), selected));
                if (selected)
                {
                    _patternOverrides.TryGetValue(key, out var curPattern);
                    s2.Add(WebInput.TextField($"psp_{key}",
                        AppStrings.T("export.bulkExport.labels.printSetPatternOverride"), curPattern ?? ""));

                    string inheritLabel = AppStrings.T("export.bulkExport.labels.printSetInherit");
                    string onLabel      = AppStrings.T("export.bulkExport.labels.printSetOn");
                    string offLabel     = AppStrings.T("export.bulkExport.labels.printSetOff");
                    var tristate = new[]
                    {
                        new WebOption("inherit", inheritLabel),
                        new WebOption("on",      onLabel),
                        new WebOption("off",     offLabel),
                    };
                    _pdfOverrides.TryGetValue(key, out var curPdf);
                    _dwgOverrides.TryGetValue(key, out var curDwg);
                    s2.Add(WebInput.SingleSelect($"pspdf_{key}", "PDF",
                        curPdf == true ? "on" : curPdf == false ? "off" : "inherit", tristate));
                    s2.Add(WebInput.SingleSelect($"psdwg_{key}", "DWG",
                        curDwg == true ? "on" : curDwg == false ? "off" : "inherit", tristate));
                }
            }
            s2.Add(WebInput.TextField("newSetName", AppStrings.T("export.bulkExport.labels.saveAsPrintSet"), _newPrintSetName));
            s2.Add(WebInput.Button("saveSet", AppStrings.T("export.bulkExport.labels.savePrintSetBtn")));
            s2.Add(WebInput.Hint("saveHint", AppStrings.T("export.bulkExport.labels.saveAsPrintSetHint")));

            // ── S3: filename & formats ────────────────────────────────────────
            var patternTokens = NamingTokenRegistry.TokensFor(
                ViewsMode ? TokenEntity.View : TokenEntity.Sheet, hasSource: false);
            var s3 = new WebStep("S3", AppStrings.T("export.bulkExport.steps.S3"))
                .Add(WebInput.TokenInput("pattern",
                    ViewsMode ? AppStrings.T("export.bulkExport.labels.patternViews") : AppStrings.T("export.bulkExport.labels.patternSheets"),
                    ActivePattern, ViewsMode ? ViewDefaultPattern : SheetDefaultPattern,
                    patternTokens, PreviewTokens()));
            if (HasActivePrintSets())
                s3.Add(WebInput.Hint("printSetFilenameNote", AppStrings.T("export.bulkExport.labels.printSetFilenameNote")));
            s3.Add(WebInput.Toggle("pdf", "PDF", _pdfOn));
            s3.Add(WebInput.Hint("descPdf", AppStrings.T("export.bulkExport.labels.descPdf")));
            s3.Add(WebInput.Toggle("dwg", "DWG", _dwgOn));
            s3.Add(WebInput.Hint("descDwg", AppStrings.T("export.bulkExport.labels.descDwg")));
            s3.Add(WebInput.Toggle("nwc", "NWC", _nwcOn));
            s3.Add(WebInput.Hint("descNwc", AppStrings.T("export.bulkExport.labels.descNwc")));
            s3.Add(WebInput.Toggle("ifc", "IFC", _ifcOn));
            s3.Add(WebInput.Hint("descIfc", AppStrings.T("export.bulkExport.labels.descIfc")));
            if ((_nwcOn || _ifcOn) && !ViewsMode)
                s3.Add(WebInput.Hint("modeHint", AppStrings.T("export.bulkExport.labels.modeHint")));

            // ── S4: PDF settings ──────────────────────────────────────────────
            var s4 = new WebStep("S4", AppStrings.T("export.bulkExport.steps.S4"), required: false)
                .Add(WebInput.SingleSelect("pdfPlacement", AppStrings.T("export.bulkExport.labels.lblPaperPlacement"),
                    _pdfPlacement, new[]
                    {
                        new WebOption("Offset from Corner", "Offset from Corner"),
                        new WebOption("Center",             "Center"),
                    }))
                .Add(WebInput.Hint("placementHint", AppStrings.T("export.bulkExport.labels.placementHint")))
                .Add(WebInput.SingleSelect("zoom", AppStrings.T("export.bulkExport.labels.lblZoom"),
                    _zoomSetting, new[]
                    {
                        new WebOption("Fit to Page", "Fit to Page"),
                        new WebOption("Scale %",     "Scale %"),
                    }));
            if (_zoomSetting == "Scale %")
                s4.Add(WebInput.Stepper("zoomPct", AppStrings.T("export.bulkExport.labels.pctPercent"),
                    _zoomPct, 10, 500, 5, 0));
            s4.Add(WebInput.SingleSelect("colorDepth", AppStrings.T("export.bulkExport.labels.lblColorDepth"),
                    _colorDepth, new[] { "Color", "Grayscale", "Black & White" }.Select(v => new WebOption(v, v))))
              .Add(WebInput.SingleSelect("rasterQuality", AppStrings.T("export.bulkExport.labels.lblRasterQuality"),
                    _rasterQuality, new[] { "Draft", "Low", "Medium", "High", "Presentation" }.Select(v => new WebOption(v, v))))
              .Add(WebInput.SingleSelect("hiddenLines", AppStrings.T("export.bulkExport.labels.lblHiddenLines"),
                    _hiddenLines, new[] { "Vector Processing", "Raster Processing" }.Select(v => new WebOption(v, v))))
              .Add(WebInput.Toggle("combine", AppStrings.T("export.bulkExport.labels.combineLabel"), _combinePdf));
            if (HasActivePrintSets())
                s4.Add(WebInput.Hint("printSetCombineNote", AppStrings.T("export.bulkExport.labels.printSetCombineNote")));
            s4.Add(WebInput.Toggle("viewLinks", AppStrings.T("export.bulkExport.labels.advViewLinks"), _viewLinksBlue))
              .Add(WebInput.Hint("viewLinksDesc", AppStrings.T("export.bulkExport.labels.advViewLinksDesc")))
              .Add(WebInput.Toggle("replaceHalftone", AppStrings.T("export.bulkExport.labels.advReplaceHalftone"), _replaceHalftone))
              .Add(WebInput.Hint("replaceHalftoneDesc", AppStrings.T("export.bulkExport.labels.advReplaceHalftoneDesc")))
              .Add(WebInput.Hint("sizeNote", AppStrings.T("export.bulkExport.labels.sizeNote")));

            // ── S5: DWG settings ──────────────────────────────────────────────
            var s5 = new WebStep("S5", AppStrings.T("export.bulkExport.steps.S5"), required: false);
            if (_dwgSetupNames.Count == 0)
                s5.Add(WebInput.Hint("dwgNoSetups", AppStrings.T("export.bulkExport.labels.dwgNoSetups")));
            else
                s5.Add(WebInput.SingleSelect("dwgSetup", AppStrings.T("export.bulkExport.labels.lblExportSetup"),
                    _dwgSetupNames.Contains(_dwgSetup) ? _dwgSetup : _dwgSetupNames[0],
                    _dwgSetupNames.Select(n => new WebOption(n, n))));
            s5.Add(WebInput.Hint("dwgNote", AppStrings.T("export.bulkExport.labels.dwgNote")));

            // ── S6: NWC settings (note is mode-aware) ─────────────────────────
            int facetIdx = Array.IndexOf(NwcFacetingValues, _nwcFacetingFactor);
            if (facetIdx < 0) facetIdx = 1;
            var s6 = new WebStep("S6", AppStrings.T("export.bulkExport.steps.S6"), required: false)
                .Add(WebInput.Hint("nwcNote", _exportMode == "Sheets"
                    ? AppStrings.T("export.bulkExport.labels.nwcNoteSheets")
                    : AppStrings.T("export.bulkExport.labels.nwcNoteViews")))
                .Add(WebInput.SingleSelect("nwcCoords", AppStrings.T("export.bulkExport.labels.lblCoordSystem"),
                    _nwcCoordinates == "Internal" ? "Internal" : "Shared",
                    new[] { "Shared", "Internal" }.Select(v => new WebOption(v, v))))
                .Add(WebInput.SingleSelect("nwcParams", AppStrings.T("export.bulkExport.labels.lblElementParams"),
                    _nwcParameters == "Elements" ? "Elements" : _nwcParameters == "None" ? "None" : "All",
                    new[] { "All", "Elements", "None" }.Select(v => new WebOption(v, v))))
                .Add(WebInput.SingleSelect("nwcFaceting", AppStrings.T("export.bulkExport.labels.lblMeshQuality"),
                    NwcFacetingLabels[facetIdx], NwcFacetingLabels.Select(v => new WebOption(v, v))))
                .Add(WebInput.Toggle("nwcConvertProps",  AppStrings.T("export.bulkExport.labels.cbConvertProps"),  _nwcConvertElementProps))
                .Add(WebInput.Toggle("nwcConvertLights", AppStrings.T("export.bulkExport.labels.cbConvertLights"), _nwcConvertLights))
                .Add(WebInput.Toggle("nwcConvertCad",    AppStrings.T("export.bulkExport.labels.cbConvertCad"),    _nwcConvertLinkedCad))
                .Add(WebInput.Toggle("nwcDivideLevels",  AppStrings.T("export.bulkExport.labels.cbDivideLevels"),  _nwcDivideByLevel))
                .Add(WebInput.Toggle("nwcLinkedRevit",   AppStrings.T("export.bulkExport.labels.cbLinkedRevit"),   _nwcExportLinks))
                .Add(WebInput.Toggle("nwcParts",         AppStrings.T("export.bulkExport.labels.cbParts"),         _nwcExportParts))
                .Add(WebInput.Toggle("nwcElementIds",    AppStrings.T("export.bulkExport.labels.cbElementIds"),    _nwcExportElementIds))
                .Add(WebInput.Toggle("nwcUrls",          AppStrings.T("export.bulkExport.labels.cbUrls"),          _nwcExportUrls))
                .Add(WebInput.Toggle("nwcMissingMats",   AppStrings.T("export.bulkExport.labels.cbMissingMats"),   _nwcFindMissingMaterials))
                .Add(WebInput.Toggle("nwcRoomGeom",      AppStrings.T("export.bulkExport.labels.cbRoomGeom"),      _nwcExportRoomGeometry))
                .Add(WebInput.Toggle("nwcRoomAttr",      AppStrings.T("export.bulkExport.labels.cbRoomAttr"),      _nwcExportRoomAsAttr));

            // ── S7: IFC settings ──────────────────────────────────────────────
            var s7 = new WebStep("S7", AppStrings.T("export.bulkExport.steps.S7"), required: false)
                .Add(WebInput.Hint("ifcNote", AppStrings.T("export.bulkExport.labels.ifcNote")))
                .Add(WebInput.SingleSelect("ifcVersion", AppStrings.T("export.bulkExport.labels.lblIfcVersion"),
                    _ifcVersion == "IFC4" ? "IFC4" : "IFC2x3",
                    new[] { "IFC2x3", "IFC4" }.Select(v => new WebOption(v, v))));

            // ── S8: output ────────────────────────────────────────────────────
            var s8 = new WebStep("S8", AppStrings.T("export.bulkExport.steps.S8"))
                .Add(WebInput.FolderBrowser("folder", AppStrings.T("export.bulkExport.labels.secOutputFolder"), _outputFolder))
                .Add(WebInput.Toggle("split", AppStrings.T("export.bulkExport.labels.splitLabel"), _splitByFormat));

            // ── S9: review ────────────────────────────────────────────────────
            var s9 = new WebStep("S9", AppStrings.T("export.bulkExport.steps.S9"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("export.bulkExport.review.itemSheets"),
                     _selectedNames.Count == 0 ? "-" : AppStrings.T("export.bulkExport.review.sheetsValue", _selectedNames.Count)),
                    (AppStrings.T("export.bulkExport.review.itemFormats"), GetActiveFormats()),
                    (AppStrings.T("export.bulkExport.review.itemPacks"),
                     HasActivePrintSets() ? AppStrings.T("export.bulkExport.review.printSetsValue", _selectedPrintSetIds.Count) : AppStrings.T("export.bulkExport.review.printSetsNone")),
                    (AppStrings.T("export.bulkExport.review.itemQuality"),
                     _pdfOn ? AppStrings.T("export.bulkExport.review.qualityValue", _colorDepth, _rasterQuality) : AppStrings.T("export.bulkExport.review.qualityPdfOff")),
                    (AppStrings.T("export.bulkExport.review.itemPattern"),
                     string.IsNullOrEmpty(ActivePattern) ? "-" : ActivePattern),
                    (AppStrings.T("export.bulkExport.review.itemFolder"),
                     _outputFolder.Length == 0 ? "-"
                        : _outputFolder.Length > 40 ? "…" + _outputFolder.Substring(_outputFolder.Length - 37)
                        : _outputFolder),
                }));

            return new List<WebStep> { s1, s2, s3, s4, s5, s6, s7, s8, s9 };
        }

        // Token→sample map for the S3 pattern preview (mode-aware, mirrors WPF UpdatePreview).
        private Dictionary<string, string> PreviewTokens()
        {
            if (ViewsMode)
                return new Dictionary<string, string>
                {
                    ["ViewName"]      = "Level 1 - Lighting",
                    ["ViewType"]      = "FloorPlan",
                    ["ProjectNumber"] = "2024-001",
                    ["ProjectName"]   = "Sample Project",
                    ["Year"]          = DateTime.Now.Year.ToString(),
                    ["Month"]         = DateTime.Now.Month.ToString("D2"),
                    ["Day"]           = DateTime.Now.Day.ToString("D2"),
                };
            return new Dictionary<string, string>
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

        // ── Save-as-print-set button ──────────────────────────────────────────

        public void OnToolAction(string stepId, string inputId)
        {
            if (inputId != "saveSet") return;
            if (string.IsNullOrWhiteSpace(_newPrintSetName)) return;
            var handler = App.BulkExportPrintSetHandler;
            var evt     = App.BulkExportPrintSetEvent;
            if (handler == null || evt == null) return;

            handler.Name      = _newPrintSetName.Trim();
            handler.MemberIds = _selectedNames.Where(_nameToId.ContainsKey).Select(n => _nameToId[n]).ToList();
            // Lands on the Revit thread; the window marshals StepInputsChanged.
            handler.OnCreated = sets =>
            {
                _availablePrintSets = sets;
                _newPrintSetName    = "";
                StepInputsChanged?.Invoke("S2");
                Fire();
            };
            handler.OnError = msg => DiagnosticsLog.Warn("BulkExportWebTool: save print set", msg ?? "");
            evt.Raise();
        }

        // ── State ─────────────────────────────────────────────────────────────

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "mode":
                {
                    var m = AsString(value) == "Views" ? "Views" : "Sheets";
                    if (m != _exportMode)
                    {
                        _exportMode = m;
                        _selectedNames.Clear();
                        StepInputsChanged?.Invoke("S1"); // eligible tree + show-all toggle
                        StepInputsChanged?.Invoke("S3"); // token vocabulary follows the mode
                        StepInputsChanged?.Invoke("S6"); // NWC note is mode-aware
                        Fire();
                    }
                    return;
                }
                case "showAll":
                {
                    bool sa = AsBool(value, _showAllViews);
                    if (sa != _showAllViews)
                    {
                        _showAllViews = sa;
                        _selectedNames.Clear();
                        StepInputsChanged?.Invoke("S1");
                        Fire();
                    }
                    return;
                }
                case "items":
                    _selectedNames = IdList(value)
                        .Where(_idToName.ContainsKey)
                        .Select(id => _idToName[id])
                        .ToList();
                    Fire();
                    return;
                case "newSetName": _newPrintSetName = AsString(value); return;
                case "pattern":    ActivePattern = AsString(value, ActivePattern); Fire(); return;
                case "pdf": _pdfOn = AsBool(value, _pdfOn); Fire(); return;
                case "dwg": _dwgOn = AsBool(value, _dwgOn); Fire(); return;
                case "nwc": _nwcOn = AsBool(value, _nwcOn); Fire(); return;
                case "ifc": _ifcOn = AsBool(value, _ifcOn); Fire(); return;
                case "pdfPlacement": _pdfPlacement = AsString(value, _pdfPlacement); Fire(); return;
                case "zoom":
                {
                    var z = AsString(value) == "Scale %" ? "Scale %" : "Fit to Page";
                    if (z != _zoomSetting)
                    {
                        _zoomSetting = z;
                        StepInputsChanged?.Invoke("S4"); // zoom stepper is conditional
                        Fire();
                    }
                    return;
                }
                case "zoomPct":       _zoomPct = (int)AsDouble(value, _zoomPct); Fire(); return;
                case "colorDepth":    _colorDepth    = AsString(value, _colorDepth);    Fire(); return;
                case "rasterQuality": _rasterQuality = AsString(value, _rasterQuality); Fire(); return;
                case "hiddenLines":   _hiddenLines   = AsString(value, _hiddenLines);   Fire(); return;
                case "combine":         _combinePdf      = AsBool(value, _combinePdf);      Fire(); return;
                case "viewLinks":       _viewLinksBlue   = AsBool(value, _viewLinksBlue);   Fire(); return;
                case "replaceHalftone": _replaceHalftone = AsBool(value, _replaceHalftone); Fire(); return;
                case "dwgSetup":  _dwgSetup = AsString(value, _dwgSetup); Fire(); return;
                case "nwcCoords": _nwcCoordinates = AsString(value, _nwcCoordinates); Fire(); return;
                case "nwcParams": _nwcParameters  = AsString(value, _nwcParameters);  Fire(); return;
                case "nwcFaceting":
                {
                    int idx = Array.IndexOf(NwcFacetingLabels, AsString(value));
                    _nwcFacetingFactor = idx >= 0 ? NwcFacetingValues[idx] : 1.0;
                    Fire(); return;
                }
                case "nwcConvertProps":  _nwcConvertElementProps  = AsBool(value, _nwcConvertElementProps);  Fire(); return;
                case "nwcConvertLights": _nwcConvertLights        = AsBool(value, _nwcConvertLights);        Fire(); return;
                case "nwcConvertCad":    _nwcConvertLinkedCad     = AsBool(value, _nwcConvertLinkedCad);     Fire(); return;
                case "nwcDivideLevels":  _nwcDivideByLevel        = AsBool(value, _nwcDivideByLevel);        Fire(); return;
                case "nwcLinkedRevit":   _nwcExportLinks          = AsBool(value, _nwcExportLinks);          Fire(); return;
                case "nwcParts":         _nwcExportParts          = AsBool(value, _nwcExportParts);          Fire(); return;
                case "nwcElementIds":    _nwcExportElementIds     = AsBool(value, _nwcExportElementIds);     Fire(); return;
                case "nwcUrls":          _nwcExportUrls           = AsBool(value, _nwcExportUrls);           Fire(); return;
                case "nwcMissingMats":   _nwcFindMissingMaterials = AsBool(value, _nwcFindMissingMaterials); Fire(); return;
                case "nwcRoomGeom":      _nwcExportRoomGeometry   = AsBool(value, _nwcExportRoomGeometry);   Fire(); return;
                case "nwcRoomAttr":      _nwcExportRoomAsAttr     = AsBool(value, _nwcExportRoomAsAttr);     Fire(); return;
                case "ifcVersion":       _ifcVersion              = AsString(value, _ifcVersion);            Fire(); return;
                case "folder": _outputFolder  = AsString(value, _outputFolder); Fire(); return;
                case "split":  _splitByFormat = AsBool(value, _splitByFormat);  Fire(); return;
            }

            // Print-set rows: ps_<id> / psp_<id> / pspdf_<id> / psdwg_<id>
            if (TrySplitKey(inputId, "ps_", out var psKey))
            {
                if (AsBool(value)) _selectedPrintSetIds.Add(psKey);
                else               _selectedPrintSetIds.Remove(psKey);
                StepInputsChanged?.Invoke("S2"); // overrides are conditional per row
                Fire();
                return;
            }
            if (TrySplitKey(inputId, "psp_", out var patKey))
            {
                var text = AsString(value);
                _patternOverrides[patKey] = string.IsNullOrWhiteSpace(text) ? null : text;
                Fire();
                return;
            }
            if (TrySplitKey(inputId, "pspdf_", out var pdfKey))
            {
                _pdfOverrides[pdfKey] = AsString(value) == "on" ? true : AsString(value) == "off" ? false : (bool?)null;
                Fire();
                return;
            }
            if (TrySplitKey(inputId, "psdwg_", out var dwgKey))
            {
                _dwgOverrides[dwgKey] = AsString(value) == "on" ? true : AsString(value) == "off" ? false : (bool?)null;
                Fire();
            }
        }

        private static bool TrySplitKey(string inputId, string prefix, out long key)
        {
            key = 0;
            return inputId.StartsWith(prefix, StringComparison.Ordinal)
                && long.TryParse(inputId.Substring(prefix.Length), out key);
        }

        // ── Validation / summaries ────────────────────────────────────────────

        private bool HasActivePrintSets() => _selectedPrintSetIds.Count > 0;

        private string GetActiveFormats()
        {
            var fmts = new List<string>();
            if (_pdfOn) fmts.Add("PDF");
            if (_dwgOn) fmts.Add("DWG");
            if (_nwcOn) fmts.Add("NWC");
            if (_ifcOn) fmts.Add("IFC");
            return fmts.Count > 0 ? string.Join(", ", fmts) : "-";
        }

        public override bool IsStepValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedNames.Count > 0;
                case "S3": return _pdfOn || _dwgOn || _nwcOn || _ifcOn;
                case "S8": return !string.IsNullOrWhiteSpace(_outputFolder);
                default:   return true;
            }
        }

        public override bool CanRun() => IsStepValid("S1") && IsStepValid("S3") && IsStepValid("S8");

        public override string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedNames.Count == 0 ? "-"
                    : AppStrings.T("export.bulkExport.summaries.s1", _selectedNames.Count, _exportMode.ToLower());
                case "S2": return HasActivePrintSets()
                    ? AppStrings.T("export.bulkExport.summaries.s2PrintSets", _selectedPrintSetIds.Count)
                    : AppStrings.T("export.bulkExport.summaries.s2Individual");
                case "S3": return GetActiveFormats() == "-"
                    ? AppStrings.T("export.bulkExport.summaries.s3None")
                    : AppStrings.T("export.bulkExport.summaries.s3", GetActiveFormats(), ActivePattern);
                case "S4": return AppStrings.T("export.bulkExport.summaries.s4", _hiddenLines.Split(' ')[0], _rasterQuality, _colorDepth, _pdfPlacement.Split(' ')[0]);
                case "S5": return string.IsNullOrEmpty(_dwgSetup) ? AppStrings.T("export.bulkExport.summaries.s5Default") : _dwgSetup;
                case "S6": return AppStrings.T("export.bulkExport.summaries.s6", _nwcCoordinates, _nwcParameters);
                case "S7": return _ifcVersion;
                case "S8": return string.IsNullOrEmpty(_outputFolder) ? AppStrings.T("export.bulkExport.summaries.s8NoFolder") : _outputFolder;
                default:   return "-";
            }
        }

        // ── Run ───────────────────────────────────────────────────────────────

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_handler == null || _event == null) return;

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
                .Where(_nameToId.ContainsKey)
                .Select(n => _nameToId[n])
                .ToList();
            _handler.ExportMode               = _exportMode;
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

            _event.Raise();
        }

        public void OnWindowClosed()
        {
            if (App.BulkExportPrintSetHandler != null)
            {
                App.BulkExportPrintSetHandler.OnCreated = null;
                App.BulkExportPrintSetHandler.OnError   = null;
            }
            if (_handler == null) return;
            _handler.PushLog       = null;
            _handler.OnProgress    = null;
            _handler.OnComplete    = null;
            _handler.OnResultChips = null;
        }
    }
}
