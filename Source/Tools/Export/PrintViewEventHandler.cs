using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.BulkExport
{
    /// <summary>
    /// Exports the single active view/sheet to any combination of PDF/DWG/NWC/IFC —
    /// the same formats Bulk Export supports. Set all properties before Raise().
    /// </summary>
    public class PrintViewEventHandler : IExternalEventHandler
    {
        // ── Inputs (set by ViewModel before Raise) ────────────────────────────
        public ElementId ViewId        { get; set; } = ElementId.InvalidElementId;
        public string    OutputFolder  { get; set; } = "";

        // Formats
        public bool      ExportPdf     { get; set; } = true;
        public bool      ExportDwg     { get; set; } = false;
        public bool      ExportNwc     { get; set; } = false;
        public bool      ExportIfc     { get; set; } = false;

        // PDF settings
        public string    ZoomSetting   { get; set; } = "Fit to Page";
        public int       ZoomPercent   { get; set; } = 100;
        public string    ColorDepth    { get; set; } = "Color";
        public string    RasterQuality { get; set; } = "High";
        public bool      ViewLinksInBlue              { get; set; } = false;
        public bool      ReplaceHalftoneWithThinLines { get; set; } = false;

        // DWG settings
        public string    DwgSetupName  { get; set; } = "";

        // NWC settings
        public string    NwcCoordinates           { get; set; } = "Shared";
        public string    NwcParameters            { get; set; } = "All";
        public bool      NwcConvertElementProps   { get; set; } = true;
        public bool      NwcDivideByLevel         { get; set; } = false;
        public bool      NwcExportLinks           { get; set; } = true;
        public bool      NwcExportParts           { get; set; } = false;
        public bool      NwcExportElementIds      { get; set; } = true;
        public bool      NwcExportUrls            { get; set; } = false;
        public bool      NwcFindMissingMaterials  { get; set; } = false;
        public bool      NwcExportRoomGeometry    { get; set; } = false;
        public bool      NwcExportRoomAsAttribute { get; set; } = false;
        public bool      NwcConvertLights         { get; set; } = false;
        public bool      NwcConvertLinkedCad      { get; set; } = false;
        public double    NwcFacetingFactor        { get; set; } = 1.0;

        // IFC settings
        public string    IfcVersion    { get; set; } = "IFC2x3";

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "PrintView";

        public void Execute(UIApplication app)
        {
            var pushLog    = PushLog    ?? ((m, s) => { });
            var onProgress = OnProgress ?? ((p, a, b, c) => { });
            var onComplete = OnComplete ?? ((a, b, c) => { });

            int pass = 0, fail = 0, skip = 0;

            try
            {
                if (app.ActiveUIDocument == null)
                {
                    pushLog(LemoineStrings.T("export.printView.log.noDoc"), "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                var doc = app.ActiveUIDocument.Document;

                if (string.IsNullOrWhiteSpace(OutputFolder))
                {
                    pushLog(LemoineStrings.T("export.printView.log.noFolder"), "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                try { Directory.CreateDirectory(OutputFolder); }
                catch (Exception ex)
                {
                    pushLog(LemoineStrings.T("export.printView.log.folderFail", ex.Message), "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                var element = doc.GetElement(ViewId);
                if (element == null)
                {
                    pushLog(LemoineStrings.T("export.printView.log.viewGone"), "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                if (!ExportPdf && !ExportDwg && !ExportNwc && !ExportIfc)
                {
                    pushLog(LemoineStrings.T("export.printView.log.noFormat"), "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                string filename = ResolveFilename(element);
                var    ids      = new List<ElementId> { element.Id };

                // ── PDF ───────────────────────────────────────────────────────
                if (ExportPdf)
                {
                    try
                    {
                        var opts = ExportOptionsFactory.BuildPdfOptions(
                            filename, combine: false, placement: "Offset from Corner",
                            ColorDepth, RasterQuality, ZoomSetting, ZoomPercent,
                            ViewLinksInBlue, ReplaceHalftoneWithThinLines);

                        pushLog(LemoineStrings.T("export.printView.log.pdfExporting", filename), "info");
                        if (doc.Export(OutputFolder, ids, opts))
                        {
                            pass++;
                            pushLog(LemoineStrings.T("export.printView.log.pdfOk", filename), "pass");
                        }
                        else
                        {
                            fail++;
                            pushLog(LemoineStrings.T("export.printView.log.pdfFalse", filename), "fail");
                            LemoineLog.Warn("PrintView", $"PDF doc.Export returned false for view {ViewId.Value}");
                        }
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        pushLog(LemoineStrings.T("export.printView.log.pdfFail", filename, ex.Message), "fail");
                        LemoineLog.Error("PrintView.PDF", ex);
                    }
                }

                // ── DWG ───────────────────────────────────────────────────────
                if (ExportDwg)
                {
                    try
                    {
                        var dwgOpts = ExportOptionsFactory.BuildDwgOptions(doc, DwgSetupName);
                        if (dwgOpts == null)
                        {
                            skip++;
                            pushLog(LemoineStrings.T("export.printView.log.dwgNoSetup", DwgSetupName), "warn");
                        }
                        else if (doc.Export(OutputFolder, filename, ids, dwgOpts))
                        {
                            pass++;
                            pushLog(LemoineStrings.T("export.printView.log.dwgOk", filename), "pass");
                        }
                        else
                        {
                            fail++;
                            pushLog(LemoineStrings.T("export.printView.log.dwgFalse", filename), "fail");
                            LemoineLog.Warn("PrintView", $"DWG doc.Export returned false for view {ViewId.Value}");
                        }
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        pushLog(LemoineStrings.T("export.printView.log.dwgFail", filename, ex.Message), "fail");
                        LemoineLog.Error("PrintView.DWG", ex);
                    }
                }

                // ── NWC ───────────────────────────────────────────────────────
                // 3D views only; Navisworks exporter must be loaded.
                if (ExportNwc)
                {
                    try
                    {
                        if (!(element is View3D view3d))
                        {
                            skip++;
                            pushLog(LemoineStrings.T("export.printView.log.nwcNot3d", filename), "warn");
                        }
                        else if (!OptionalFunctionalityUtils.IsNavisworksExporterAvailable())
                        {
                            skip++;
                            pushLog(LemoineStrings.T("export.printView.log.nwcNoExporter"), "warn");
                            LemoineLog.Warn("PrintView", "NWC skipped — IsNavisworksExporterAvailable() returned false.");
                        }
                        else
                        {
                            var nwc  = BuildNwcOptionSet();
                            var opts = ExportOptionsFactory.BuildNwcOptions(nwc, view3d.Id, pushLog);
                            doc.Export(OutputFolder, filename, opts);
                            pass++;
                            pushLog(LemoineStrings.T("export.printView.log.nwcOk", filename), "pass");
                        }
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        pushLog(LemoineStrings.T("export.printView.log.nwcFail", filename, ex.Message), "fail");
                        LemoineLog.Error("PrintView.NWC", ex);
                    }
                }

                // ── IFC ───────────────────────────────────────────────────────
                // 3D views only.
                if (ExportIfc)
                {
                    try
                    {
                        if (!(element is View3D))
                        {
                            skip++;
                            pushLog(LemoineStrings.T("export.printView.log.ifcNot3d", filename), "warn");
                        }
                        else
                        {
                            var opts = ExportOptionsFactory.BuildIfcOptions(IfcVersion, element.Id);

                            // IFC export writes IFC-specific data to the document and requires a transaction
                            using (var t = new Transaction(doc, "Print View — IFC Export"))
                            {
                                t.Start();
                                doc.Export(OutputFolder, filename, opts);
                                t.Commit();
                            }
                            pass++;
                            pushLog(LemoineStrings.T("export.printView.log.ifcOk", filename), "pass");
                        }
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        pushLog(LemoineStrings.T("export.printView.log.ifcFail", filename, ex.Message), "fail");
                        LemoineLog.Error("PrintView.IFC", ex);
                    }
                }

                onProgress(100, pass, fail, skip);
                onComplete(pass, fail, skip);
            }
            catch (Exception ex)
            {
                LemoineLog.Error("PrintView.Execute", ex);
                pushLog(LemoineStrings.T("export.printView.log.printError", ex.Message), "fail");
                onComplete(pass, fail == 0 ? 1 : fail, skip);
            }
            finally
            {
                // Static handler lives for the whole Revit session — drop the run's payload.
                ViewId  = ElementId.InvalidElementId;
                OutputFolder = "";
            }
        }

        private NwcOptionSet BuildNwcOptionSet() => new NwcOptionSet
        {
            Coordinates           = NwcCoordinates,
            Parameters            = NwcParameters,
            ConvertElementProps   = NwcConvertElementProps,
            DivideByLevel         = NwcDivideByLevel,
            ExportLinks           = NwcExportLinks,
            ExportParts           = NwcExportParts,
            ExportElementIds      = NwcExportElementIds,
            ExportUrls            = NwcExportUrls,
            FindMissingMaterials  = NwcFindMissingMaterials,
            ExportRoomGeometry    = NwcExportRoomGeometry,
            ExportRoomAsAttribute = NwcExportRoomAsAttribute,
            ConvertLights         = NwcConvertLights,
            ConvertLinkedCad      = NwcConvertLinkedCad,
            FacetingFactor        = NwcFacetingFactor,
        };

        private static string ResolveFilename(Element element)
        {
            string raw = element is ViewSheet sheet
                ? $"{sheet.SheetNumber}-{sheet.Name}"
                : element.Name;
            return ExportOptionsFactory.SanitizeFilename(raw);
        }
    }
}
