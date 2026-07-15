using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.BulkExport
{
    /// <summary>Web port of <see cref="PrintViewViewModel"/> — export the active view/sheet to
    /// PDF/DWG (+ NWC/IFC when 3D). Same handler, shared BulkExportSettings, and AppStrings
    /// keys; the per-format settings steps hide via IsStepVisible exactly like the WPF
    /// IConditionalSteps.</summary>
    public class PrintViewWebTool : WebToolBase, IWebToolCleanup, IWebStepRefresh
    {
        private static readonly string[] NwcFacetingLabels = { "Low — 0.5", "Standard — 1.0", "High — 2.0", "Ultra — 5.0" };
        private static readonly double[] NwcFacetingValues = { 0.5, 1.0, 2.0, 5.0 };

        private readonly PrintViewEventHandler? _handler;
        private readonly ExternalEvent?         _event;

        private readonly ElementId    _viewId;
        private readonly string       _viewDisplayName;
        private readonly bool         _isThreeD;
        private readonly List<string> _dwgSetupNames;

        private bool _pdfOn = BulkExportSettings.Instance.ExportPdf;
        private bool _dwgOn = BulkExportSettings.Instance.ExportDwg;
        private bool _nwcOn;
        private bool _ifcOn;

        private string _zoomSetting     = BulkExportSettings.Instance.ZoomSetting;
        private int    _zoomPct         = BulkExportSettings.Instance.ZoomPercent;
        private string _colorDepth      = BulkExportSettings.Instance.ColorDepth;
        private string _rasterQuality   = BulkExportSettings.Instance.RasterQuality;
        private bool   _viewLinksBlue   = BulkExportSettings.Instance.ViewLinksInBlue;
        private bool   _replaceHalftone = BulkExportSettings.Instance.ReplaceHalftoneWithThinLines;

        private string _dwgSetup = BulkExportSettings.Instance.DwgExportSetupName;

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

        private string _ifcVersion = BulkExportSettings.Instance.IfcVersion;

        private string _outputFolder = BulkExportSettings.Instance.OutputFolder;

        public event Action<string>? StepInputsChanged;

        public PrintViewWebTool(
            PrintViewEventHandler? handler,
            ExternalEvent?         externalEvent,
            ElementId              viewId,
            string                 viewDisplayName,
            bool                   isThreeD,
            List<string>           dwgSetupNames)
        {
            _handler         = handler;
            _event           = externalEvent;
            _viewId          = viewId;
            _viewDisplayName = viewDisplayName;
            _isThreeD        = isThreeD;
            _dwgSetupNames   = dwgSetupNames ?? new List<string>();

            if (_isThreeD)
            {
                _nwcOn = BulkExportSettings.Instance.ExportNwc;
                _ifcOn = BulkExportSettings.Instance.ExportIfc;
            }

            if (string.IsNullOrEmpty(_dwgSetup) && _dwgSetupNames.Count > 0)
                _dwgSetup = _dwgSetupNames[0];
        }

        public override string Title    => AppStrings.T("export.printView.title");
        public override string RunLabel => AppStrings.T("export.printView.runLabel");

        public override bool IsStepVisible(string stepId)
        {
            switch (stepId)
            {
                case "S2": return _pdfOn;
                case "S3": return _dwgOn;
                case "S4": return _nwcOn;
                case "S5": return _ifcOn;
                default:   return true;
            }
        }

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            // ── S1: formats (NWC/IFC only offered for a 3D view) ──────────────
            var s1 = new WebStep("S1", AppStrings.T("export.printView.steps.S1"))
                .Add(WebInput.Toggle("pdf", "PDF", _pdfOn))
                .Add(WebInput.Hint("descPdf", AppStrings.T("export.printView.labels.descPdf")))
                .Add(WebInput.Toggle("dwg", "DWG", _dwgOn))
                .Add(WebInput.Hint("descDwg", AppStrings.T("export.printView.labels.descDwg")));
            if (_isThreeD)
            {
                s1.Add(WebInput.Toggle("nwc", "NWC", _nwcOn));
                s1.Add(WebInput.Hint("descNwc", AppStrings.T("export.printView.labels.descNwc")));
                s1.Add(WebInput.Toggle("ifc", "IFC", _ifcOn));
                s1.Add(WebInput.Hint("descIfc", AppStrings.T("export.printView.labels.descIfc")));
            }
            else
            {
                s1.Add(WebInput.Hint("non3dHint", AppStrings.T("export.printView.labels.non3dHint")));
            }

            // ── S2: PDF settings ──────────────────────────────────────────────
            var s2 = new WebStep("S2", AppStrings.T("export.printView.steps.S2"), required: false)
                .Add(WebInput.SingleSelect("zoom", AppStrings.T("export.printView.labels.lblZoom"),
                    _zoomSetting, new[]
                    {
                        new WebOption("Fit to Page", "Fit to Page"),
                        new WebOption("Scale %",     "Scale %"),
                    }));
            if (_zoomSetting == "Scale %")
                s2.Add(WebInput.Stepper("zoomPct", AppStrings.T("export.printView.labels.pctPercent"),
                    _zoomPct, 10, 500, 5, 0));
            s2.Add(WebInput.SingleSelect("colorDepth", AppStrings.T("export.printView.labels.lblColorDepth"),
                    _colorDepth, new[] { "Color", "Grayscale", "Black & White" }.Select(v => new WebOption(v, v))))
              .Add(WebInput.SingleSelect("rasterQuality", AppStrings.T("export.printView.labels.lblRasterQuality"),
                    _rasterQuality, new[] { "Low", "Medium", "High", "Presentation" }.Select(v => new WebOption(v, v))))
              .Add(WebInput.Toggle("viewLinks", AppStrings.T("export.printView.labels.advViewLinks"), _viewLinksBlue))
              .Add(WebInput.Toggle("replaceHalftone", AppStrings.T("export.printView.labels.advReplaceHalftone"), _replaceHalftone));

            // ── S3: DWG settings ──────────────────────────────────────────────
            var s3 = new WebStep("S3", AppStrings.T("export.printView.steps.S3"), required: false);
            if (_dwgSetupNames.Count == 0)
                s3.Add(WebInput.Hint("dwgNoSetups", AppStrings.T("export.printView.labels.dwgNoSetups")));
            else
                s3.Add(WebInput.SingleSelect("dwgSetup", AppStrings.T("export.printView.labels.lblExportSetup"),
                    _dwgSetupNames.Contains(_dwgSetup) ? _dwgSetup : _dwgSetupNames[0],
                    _dwgSetupNames.Select(n => new WebOption(n, n))));
            s3.Add(WebInput.Hint("dwgNote", AppStrings.T("export.printView.labels.dwgNote")));

            // ── S4: NWC settings ──────────────────────────────────────────────
            int facetIdx = Array.IndexOf(NwcFacetingValues, _nwcFacetingFactor);
            if (facetIdx < 0) facetIdx = 1;
            var s4 = new WebStep("S4", AppStrings.T("export.printView.steps.S4"), required: false)
                .Add(WebInput.Hint("nwcNote", AppStrings.T("export.printView.labels.nwcNote")))
                .Add(WebInput.SingleSelect("nwcCoords", AppStrings.T("export.printView.labels.lblCoordSystem"),
                    _nwcCoordinates == "Internal" ? "Internal" : "Shared",
                    new[] { "Shared", "Internal" }.Select(v => new WebOption(v, v))))
                .Add(WebInput.SingleSelect("nwcParams", AppStrings.T("export.printView.labels.lblElementParams"),
                    _nwcParameters == "Elements" ? "Elements" : _nwcParameters == "None" ? "None" : "All",
                    new[] { "All", "Elements", "None" }.Select(v => new WebOption(v, v))))
                .Add(WebInput.SingleSelect("nwcFaceting", AppStrings.T("export.printView.labels.lblMeshQuality"),
                    NwcFacetingLabels[facetIdx], NwcFacetingLabels.Select(v => new WebOption(v, v))))
                .Add(WebInput.Toggle("nwcConvertProps",  AppStrings.T("export.printView.labels.cbConvertProps"),  _nwcConvertElementProps))
                .Add(WebInput.Toggle("nwcConvertLights", AppStrings.T("export.printView.labels.cbConvertLights"), _nwcConvertLights))
                .Add(WebInput.Toggle("nwcConvertCad",    AppStrings.T("export.printView.labels.cbConvertCad"),    _nwcConvertLinkedCad))
                .Add(WebInput.Toggle("nwcDivideLevels",  AppStrings.T("export.printView.labels.cbDivideLevels"),  _nwcDivideByLevel))
                .Add(WebInput.Toggle("nwcLinkedRevit",   AppStrings.T("export.printView.labels.cbLinkedRevit"),   _nwcExportLinks))
                .Add(WebInput.Toggle("nwcParts",         AppStrings.T("export.printView.labels.cbParts"),         _nwcExportParts))
                .Add(WebInput.Toggle("nwcElementIds",    AppStrings.T("export.printView.labels.cbElementIds"),    _nwcExportElementIds))
                .Add(WebInput.Toggle("nwcUrls",          AppStrings.T("export.printView.labels.cbUrls"),          _nwcExportUrls))
                .Add(WebInput.Toggle("nwcMissingMats",   AppStrings.T("export.printView.labels.cbMissingMats"),   _nwcFindMissingMaterials))
                .Add(WebInput.Toggle("nwcRoomGeom",      AppStrings.T("export.printView.labels.cbRoomGeom"),      _nwcExportRoomGeometry))
                .Add(WebInput.Toggle("nwcRoomAttr",      AppStrings.T("export.printView.labels.cbRoomAttr"),      _nwcExportRoomAsAttr));

            // ── S5: IFC settings ──────────────────────────────────────────────
            var s5 = new WebStep("S5", AppStrings.T("export.printView.steps.S5"), required: false)
                .Add(WebInput.Hint("ifcNote", AppStrings.T("export.printView.labels.ifcNote")))
                .Add(WebInput.SingleSelect("ifcVersion", AppStrings.T("export.printView.labels.lblIfcVersion"),
                    _ifcVersion == "IFC4" ? "IFC4" : "IFC2x3",
                    new[] { "IFC2x3", "IFC4" }.Select(v => new WebOption(v, v))));

            // ── S6: output ────────────────────────────────────────────────────
            var s6 = new WebStep("S6", AppStrings.T("export.printView.steps.S6"))
                .Add(WebInput.FolderBrowser("folder", AppStrings.T("export.printView.labels.secOutputFolder"), _outputFolder));

            // ── S7: review ────────────────────────────────────────────────────
            var s7 = new WebStep("S7", AppStrings.T("export.printView.steps.S7"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("export.printView.review.itemView"),    _viewDisplayName),
                    (AppStrings.T("export.printView.review.itemFormats"), FormatsSummary()),
                    (AppStrings.T("export.printView.review.itemQuality"), $"{_colorDepth} · {_rasterQuality} · {_zoomSetting}"),
                    (AppStrings.T("export.printView.review.itemFolder"),
                     string.IsNullOrEmpty(_outputFolder) ? "-"
                        : _outputFolder.Length > 40 ? "…" + _outputFolder.Substring(_outputFolder.Length - 37)
                        : _outputFolder),
                }));

            return new List<WebStep> { s1, s2, s3, s4, s5, s6, s7 };
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "pdf": _pdfOn = AsBool(value, _pdfOn); Fire(); break;
                case "dwg": _dwgOn = AsBool(value, _dwgOn); Fire(); break;
                case "nwc": _nwcOn = _isThreeD && AsBool(value, _nwcOn); Fire(); break;
                case "ifc": _ifcOn = _isThreeD && AsBool(value, _ifcOn); Fire(); break;
                case "zoom":
                {
                    var z = AsString(value) == "Scale %" ? "Scale %" : "Fit to Page";
                    if (z != _zoomSetting)
                    {
                        _zoomSetting = z;
                        StepInputsChanged?.Invoke("S2"); // zoom stepper is conditional
                        Fire();
                    }
                    break;
                }
                case "zoomPct":       _zoomPct = (int)AsDouble(value, _zoomPct); Fire(); break;
                case "colorDepth":    _colorDepth    = AsString(value, _colorDepth);    Fire(); break;
                case "rasterQuality": _rasterQuality = AsString(value, _rasterQuality); Fire(); break;
                case "viewLinks":       _viewLinksBlue   = AsBool(value, _viewLinksBlue);   Fire(); break;
                case "replaceHalftone": _replaceHalftone = AsBool(value, _replaceHalftone); Fire(); break;
                case "dwgSetup":      _dwgSetup = AsString(value, _dwgSetup); Fire(); break;
                case "nwcCoords":     _nwcCoordinates = AsString(value, _nwcCoordinates); Fire(); break;
                case "nwcParams":     _nwcParameters  = AsString(value, _nwcParameters);  Fire(); break;
                case "nwcFaceting":
                {
                    int idx = Array.IndexOf(NwcFacetingLabels, AsString(value));
                    _nwcFacetingFactor = idx >= 0 ? NwcFacetingValues[idx] : 1.0;
                    Fire(); break;
                }
                case "nwcConvertProps":  _nwcConvertElementProps  = AsBool(value, _nwcConvertElementProps);  Fire(); break;
                case "nwcConvertLights": _nwcConvertLights        = AsBool(value, _nwcConvertLights);        Fire(); break;
                case "nwcConvertCad":    _nwcConvertLinkedCad     = AsBool(value, _nwcConvertLinkedCad);     Fire(); break;
                case "nwcDivideLevels":  _nwcDivideByLevel        = AsBool(value, _nwcDivideByLevel);        Fire(); break;
                case "nwcLinkedRevit":   _nwcExportLinks          = AsBool(value, _nwcExportLinks);          Fire(); break;
                case "nwcParts":         _nwcExportParts          = AsBool(value, _nwcExportParts);          Fire(); break;
                case "nwcElementIds":    _nwcExportElementIds     = AsBool(value, _nwcExportElementIds);     Fire(); break;
                case "nwcUrls":          _nwcExportUrls           = AsBool(value, _nwcExportUrls);           Fire(); break;
                case "nwcMissingMats":   _nwcFindMissingMaterials = AsBool(value, _nwcFindMissingMaterials); Fire(); break;
                case "nwcRoomGeom":      _nwcExportRoomGeometry   = AsBool(value, _nwcExportRoomGeometry);   Fire(); break;
                case "nwcRoomAttr":      _nwcExportRoomAsAttr     = AsBool(value, _nwcExportRoomAsAttr);     Fire(); break;
                case "ifcVersion":       _ifcVersion              = AsString(value, _ifcVersion);            Fire(); break;
                case "folder":           _outputFolder            = AsString(value, _outputFolder);          Fire(); break;
            }
        }

        public override bool IsStepValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _pdfOn || _dwgOn || _nwcOn || _ifcOn;
                case "S6": return !string.IsNullOrWhiteSpace(_outputFolder);
                default:   return true;
            }
        }

        public override bool CanRun() => IsStepValid("S1") && IsStepValid("S6");

        public override string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return FormatsSummary();
                case "S2": return $"{_colorDepth} · {_rasterQuality} · {_zoomSetting}";
                case "S3": return string.IsNullOrEmpty(_dwgSetup) ? AppStrings.T("export.printView.summaries.dwgDefault") : _dwgSetup;
                case "S4": return $"{_nwcCoordinates} · {_nwcParameters}";
                case "S5": return _ifcVersion;
                case "S6": return string.IsNullOrEmpty(_outputFolder) ? AppStrings.T("export.printView.summaries.noFolder") : _outputFolder;
                default:   return "-";
            }
        }

        private string FormatsSummary()
        {
            var on = new List<string>();
            if (_pdfOn) on.Add("PDF");
            if (_dwgOn) on.Add("DWG");
            if (_nwcOn) on.Add("NWC");
            if (_ifcOn) on.Add("IFC");
            return on.Count > 0 ? string.Join(" · ", on) : AppStrings.T("export.printView.summaries.formatsNone");
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_handler == null || _event == null) return;

            // Persist shared quality/format settings so both tools track each other.
            // The format on/off flags are intentionally NOT written back — they depend on
            // the transient active-view type and would clobber Bulk Export's selection.
            var s = BulkExportSettings.Instance;
            s.ZoomSetting                  = _zoomSetting;
            s.ZoomPercent                  = _zoomPct;
            s.ColorDepth                   = _colorDepth;
            s.RasterQuality                = _rasterQuality;
            s.ViewLinksInBlue              = _viewLinksBlue;
            s.ReplaceHalftoneWithThinLines = _replaceHalftone;
            s.DwgExportSetupName           = _dwgSetup;
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
            s.IfcVersion                   = _ifcVersion;
            s.OutputFolder                 = _outputFolder;
            s.Save();

            _handler.ViewId       = _viewId;
            _handler.OutputFolder = _outputFolder;

            _handler.ExportPdf = _pdfOn;
            _handler.ExportDwg = _dwgOn;
            _handler.ExportNwc = _nwcOn;
            _handler.ExportIfc = _ifcOn;

            _handler.ZoomSetting                  = _zoomSetting;
            _handler.ZoomPercent                  = _zoomPct;
            _handler.ColorDepth                   = _colorDepth;
            _handler.RasterQuality                = _rasterQuality;
            _handler.ViewLinksInBlue              = _viewLinksBlue;
            _handler.ReplaceHalftoneWithThinLines = _replaceHalftone;

            _handler.DwgSetupName = _dwgSetup;

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

            _handler.IfcVersion = _ifcVersion;

            _handler.PushLog    = pushLog;
            _handler.OnProgress = onProgress;
            _handler.OnComplete = onComplete;

            _event.Raise();
        }

        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.PushLog    = null;
            _handler.OnProgress = null;
            _handler.OnComplete = null;
        }
    }
}
