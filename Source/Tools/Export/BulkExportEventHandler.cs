using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.BulkExport
{
    /// <summary>
    /// Executes the bulk export on the Revit API thread.
    /// Set all properties before calling ExternalEvent.Raise().
    /// </summary>
    public class BulkExportEventHandler : IExternalEventHandler
    {
        // ── Inputs (set by ViewModel before Raise) ────────────────────────────
        public List<ElementId>        SelectedIds                  { get; set; } = new List<ElementId>();
        public string                 ExportMode                   { get; set; } = "Sheets";
        public string                 FilenamePattern              { get; set; } = "{SheetNumber}-{SheetName}";
        public string                 OutputFolder                 { get; set; } = "";
        public bool                   SplitByFormat                { get; set; } = true;
        public bool                   ExportPdf                    { get; set; } = true;
        public bool                   ExportDwg                    { get; set; } = false;
        public bool                   CombinePdf                   { get; set; } = true;
        public string                 DwgSetupName                 { get; set; } = "";
        public string                 PdfPlacement                 { get; set; } = "Offset from Corner";
        public string                 HiddenLines                  { get; set; } = "Vector Processing";
        public string                 ColorDepth                   { get; set; } = "Color";
        public string                 RasterQuality                { get; set; } = "High";
        public string                 ZoomSetting                  { get; set; } = "Fit to Page";   // "Fit to Page" | "Scale %"
        public int                    ZoomPercent                  { get; set; } = 100;
        public bool                   ViewLinksInBlue              { get; set; } = false;
        public bool                   ReplaceHalftoneWithThinLines { get; set; } = false;
        public List<SheetPackLayout>  Packs                        { get; set; } = new List<SheetPackLayout>();

        // ── Format flags ──────────────────────────────────────────────────────
        public bool                   ExportNwc                    { get; set; } = false;
        public bool                   ExportIfc                    { get; set; } = false;

        // ── NWC options (all NavisworksExportOptions properties) ──────────────
        public string                 NwcCoordinates               { get; set; } = "Shared";
        public string                 NwcParameters                { get; set; } = "All";
        public bool                   NwcConvertElementProps       { get; set; } = true;
        public bool                   NwcDivideByLevel             { get; set; } = false;
        public bool                   NwcExportLinks               { get; set; } = true;
        public bool                   NwcExportParts               { get; set; } = false;
        public bool                   NwcExportElementIds          { get; set; } = true;
        public bool                   NwcExportUrls                { get; set; } = false;
        public bool                   NwcFindMissingMaterials      { get; set; } = false;
        public bool                   NwcExportRoomGeometry        { get; set; } = false;
        public bool                   NwcExportRoomAsAttribute     { get; set; } = false;
        public bool                   NwcConvertLights             { get; set; } = false;
        public bool                   NwcConvertLinkedCad          { get; set; } = false;
        public double                 NwcFacetingFactor            { get; set; } = 1.0;

        // ── IFC options ───────────────────────────────────────────────────────
        public string                 IfcVersion                   { get; set; } = "IFC2x3";

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }
        public Action<IReadOnlyList<ResultChip>>? OnResultChips { get; set; }

        // Per-format file tallies surfaced as result chips ("30 PDF · 30 DWG · …").
        private int _pdf, _dwg, _nwc, _ifc;

        public string GetName() => "BulkExport";

        public void Execute(UIApplication app)
        {
            var pushLog    = PushLog    ?? ((m, s) => { });
            var onProgress = OnProgress ?? ((p, a, b, c) => { });
            var onComplete = OnComplete ?? ((a, b, c) => { });

            int pass = 0, fail = 0, skip = 0;
            _pdf = _dwg = _nwc = _ifc = 0;

            // TEMPORARY DEBUG — remove before release once NWC/IFC export is stable
            using var debug = new BulkExportDebugLogger(OutputFolder);

            try
            {
                // Guard: no active document
                if (app.ActiveUIDocument == null)
                {
                    pushLog(LemoineStrings.T("export.bulkExport.log.noDoc"), "fail");
                    debug.Log("INIT-ERROR", "app.ActiveUIDocument is null");
                    onComplete(0, 1, 0);
                    return;
                }

                var doc = app.ActiveUIDocument.Document;

                debug.Log("INIT", $"ExportMode={ExportMode}  PDF={ExportPdf}  DWG={ExportDwg}  NWC={ExportNwc}  IFC={ExportIfc}  IFCVersion={IfcVersion}");
                debug.Log("INIT", $"OutputFolder={OutputFolder}  SplitByFormat={SplitByFormat}  SelectedCount={SelectedIds.Count}");
                debug.Log("INIT", $"RevitDoc={doc.PathName}  Title={doc.Title}");

                // Guard: output folder
                if (string.IsNullOrEmpty(OutputFolder))
                {
                    pushLog(LemoineStrings.T("export.bulkExport.log.noFolder"), "fail");
                    onComplete(0, 1, 0);
                    return;
                }
                try { Directory.CreateDirectory(OutputFolder); }
                catch (Exception ex)
                {
                    pushLog(LemoineStrings.T("export.bulkExport.log.folderFail", ex.Message), "fail");
                    debug.Log("INIT-ERROR", "CreateDirectory failed", $"{ex.GetType().Name}: {ex.Message}");
                    onComplete(0, 1, 0);
                    return;
                }

                // Collect elements — log any that could not be resolved
                var elements = SelectedIds
                    .Select(id => doc.GetElement(id))
                    .Where(e => e != null)
                    .ToList();

                int dropped = SelectedIds.Count - elements.Count;
                if (dropped > 0)
                {
                    pushLog(LemoineStrings.T("export.bulkExport.log.droppedWarn", dropped), "warn");
                    debug.Log("INIT", $"{dropped} element ID(s) dropped — GetElement returned null.");
                }

                if (elements.Count == 0)
                {
                    pushLog(LemoineStrings.T("export.bulkExport.log.noElements"), "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                string projNumber = doc.ProjectInformation?.get_Parameter(BuiltInParameter.PROJECT_NUMBER)?.AsString() ?? "";
                string projName   = doc.ProjectInformation?.get_Parameter(BuiltInParameter.PROJECT_NAME)?.AsString()   ?? "";

                // Pre-flight: warn on sheets missing a titleblock
                if (ExportMode == "Sheets")
                {
                    foreach (var sheet in elements.OfType<ViewSheet>())
                    {
                        bool has = new FilteredElementCollector(doc, sheet.Id)
                            .OfCategory(BuiltInCategory.OST_TitleBlocks)
                            .Any();
                        if (!has)
                            pushLog(LemoineStrings.T("export.bulkExport.log.noTitleblock", sheet.SheetNumber), "warn");
                    }
                }

                // ── NWC pre-flight ────────────────────────────────────────────
                bool nwcReady = false;
                if (ExportNwc)
                {
                    if (ExportMode == "Sheets")
                    {
                        pushLog(LemoineStrings.T("export.bulkExport.log.nwcNeedsViews"), "warn");
                        debug.Log("NWC", "Export blocked — ExportMode is Sheets.");
                        skip++;
                    }
                    else
                    {
                        // TEMPORARY DEBUG — scan AppDomain for Navisworks assemblies
                        debug.Log("NWC-SCAN", "Scanning AppDomain for Navisworks-related assemblies...");
                        int nwcFound = 0;
                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            string asmName = asm.GetName().Name ?? "";
                            if (asmName.IndexOf("navisworks", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                asmName.IndexOf("nwexport",   StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                debug.Log("NWC-SCAN", $"  {asmName}",
                                    $"v{asm.GetName().Version}  {asm.Location}");
                                nwcFound++;
                            }
                        }
                        debug.Log("NWC-SCAN", $"Scan complete — {nwcFound} Navisworks assembly/ies found.");

                        nwcReady = OptionalFunctionalityUtils.IsNavisworksExporterAvailable();
                        debug.Log("NWC", $"IsNavisworksExporterAvailable() = {nwcReady}");

                        if (!nwcReady)
                        {
                            pushLog(LemoineStrings.T("export.bulkExport.log.nwcNotAvail"), "fail");
                            debug.Log("NWC", "NWC export disabled for this run — exporter not registered in Revit.");
                        }
                        else
                        {
                            debug.Log("NWC", $"Settings: Coords={NwcCoordinates}  Params={NwcParameters}  FacetingFactor={NwcFacetingFactor}");
                            debug.Log("NWC", $"          ConvertElementProps={NwcConvertElementProps}  DivideByLevel={NwcDivideByLevel}  ExportLinks={NwcExportLinks}");
                            debug.Log("NWC", $"          ExportParts={NwcExportParts}  ExportElementIds={NwcExportElementIds}  ExportUrls={NwcExportUrls}");
                            debug.Log("NWC", $"          FindMissingMaterials={NwcFindMissingMaterials}  RoomGeometry={NwcExportRoomGeometry}  RoomAsAttr={NwcExportRoomAsAttribute}");
                            debug.Log("NWC", $"          ConvertLights={NwcConvertLights}  ConvertLinkedCad={NwcConvertLinkedCad}");
                        }
                    }
                }

                // ── IFC pre-flight ────────────────────────────────────────────
                if (ExportIfc && ExportMode == "Sheets")
                {
                    pushLog(LemoineStrings.T("export.bulkExport.log.ifcNeedsViews"), "warn");
                    debug.Log("IFC", "Export blocked — ExportMode is Sheets.");
                    skip++;
                }

                if (Packs.Count > 0)
                {
                    // Packs only combine PDF and order DWG. NWC/IFC are inherently per-view
                    // and cannot be packed — report this rather than dropping them silently.
                    if (ExportNwc)
                        { pushLog(LemoineStrings.T("export.bulkExport.log.nwcInPack"), "warn"); skip++; }
                    if (ExportIfc && ExportMode != "Sheets")
                        { pushLog(LemoineStrings.T("export.bulkExport.log.ifcInPack"), "warn"); skip++; }

                    ExportPackMode(doc, elements, projNumber, projName, pushLog, onProgress, ref pass, ref fail, ref skip);
                }
                else
                    ExportIndividualMode(doc, elements, projNumber, projName, pushLog, onProgress, ref pass, ref fail, ref skip, nwcReady);

                debug.Log("COMPLETE", $"Pass={pass}  Fail={fail}  Skip={skip}");
                onProgress(100, pass, fail, skip);
                ReportResultChips(fail, skip);
                onComplete(pass, fail, skip);
            }
            catch (Exception ex)
            {
                debug.Log("FATAL", "Unhandled exception in Execute()", $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                pushLog(LemoineStrings.T("export.bulkExport.log.fatalError", ex.Message), "fail");
                onComplete(pass, 1, skip);
            }
            finally
            {
                // Session-long static handler (App.BulkExportHandler) — drop the run's payload.
                SelectedIds = new List<ElementId>();
                Packs       = new List<SheetPackLayout>();
            }
        }

        // Builds the result-strip chips from the per-format file tallies. Only formats that
        // actually produced files appear, so the breakdown stays uncluttered.
        private void ReportResultChips(int fail, int skip)
        {
            if (OnResultChips == null) return;
            var chips = new List<ResultChip>();
            if (_pdf > 0) chips.Add(new ResultChip("PDF", _pdf, "LemoineGreen"));
            if (_dwg > 0) chips.Add(new ResultChip("DWG", _dwg, "LemoineGreen"));
            if (_nwc > 0) chips.Add(new ResultChip("NWC", _nwc, "LemoineGreen"));
            if (_ifc > 0) chips.Add(new ResultChip("IFC", _ifc, "LemoineGreen"));
            chips.Add(new ResultChip("failed",  fail, "LemoineRed"));
            chips.Add(new ResultChip("skipped", skip, "LemoineTextDim"));
            OnResultChips(chips);
        }

        // ── Pack mode ─────────────────────────────────────────────────────────

        private void ExportPackMode(
            Document doc, List<Element> elements,
            string projNumber, string projName,
            Action<string, string> pushLog,
            Action<int, int, int, int> onProgress,
            ref int pass, ref int fail, ref int skip)
        {
            // Pack keys are sheet numbers (Sheets mode) or view names (Views mode), matching
            // how the ViewModel builds the pack editor. Duplicate keys can occur (placeholder
            // sheets, identically-named views); group and keep the first id so a dup doesn't
            // abort the whole export.
            var keyToId = elements
                .Select(e => new { Key = e is ViewSheet vs ? vs.SheetNumber : e.Name, e.Id })
                .Where(x => !string.IsNullOrEmpty(x.Key))
                .GroupBy(x => x.Key)
                .ToDictionary(g => g.Key, g => g.First().Id);

            // Pre-build DWG options once — setup lookup is constant across packs
            DWGExportOptions? dwgOpts     = null;
            bool              dwgResolved = false;

            int totalPdf = ExportPdf ? Packs.Count : 0;
            int totalDwg = ExportDwg ? Packs.Sum(p => p.SheetNumbers.Count) : 0;
            int total    = totalPdf + totalDwg;
            int done     = 0;

            foreach (var pack in Packs)
            {
                if (LemoineRun.CancelRequested)
                {
                    pushLog(LemoineStrings.T("export.bulkExport.log.stoppedExport", pass), "warn");
                    break;
                }
                var packIds = pack.SheetNumbers
                    .Where(n => keyToId.ContainsKey(n))
                    .Select(n => keyToId[n])
                    .ToList();

                if (packIds.Count == 0)
                {
                    pushLog(LemoineStrings.T("export.bulkExport.log.packNoItems", pack.PackName), "fail");
                    skip++;
                    // Advance done for the pre-counted operations so progress stays accurate
                    if (ExportPdf) done++;
                    if (ExportDwg) done += pack.SheetNumbers.Count;
                    onProgress(total > 0 ? (int)(done * 90.0 / total) : 90, pass, fail, skip);
                    continue;
                }

                // PDF: one combined call per pack, named after the pack
                if (ExportPdf)
                {
                    try
                    {
                        string outDir   = SplitByFormat ? EnsureSubfolder(OutputFolder, "PDF") : OutputFolder;
                        string packName = SanitizeFilename(pack.PackName);
                        var opts        = BuildPdfOptions(packName, combine: true);
                        bool ok         = doc.Export(outDir, packIds, opts);
                        if (ok)
                        {
                            pass++; _pdf++;
                            pushLog(LemoineStrings.T("export.bulkExport.log.pdfPackOk", packName, packIds.Count), "pass");
                        }
                        else
                        {
                            fail++;
                            pushLog(LemoineStrings.T("export.bulkExport.log.pdfPackFalse", pack.PackName), "fail");
                        }
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        pushLog(LemoineStrings.T("export.bulkExport.log.pdfPackFail", pack.PackName, ex.Message), "fail");
                    }
                    done++;
                    onProgress(total > 0 ? (int)(done * 90.0 / total) : 90, pass, fail, skip);
                }

                // DWG: individual export per sheet in the pack
                if (ExportDwg)
                {
                    if (!dwgResolved)
                    {
                        dwgOpts     = BuildDwgOptions(doc);
                        dwgResolved = true;
                    }

                    if (dwgOpts == null)
                    {
                        // Log once per pack — no point repeating per-sheet
                        pushLog(LemoineStrings.T("export.bulkExport.log.dwgPackNoSetup", DwgSetupName, pack.PackName), "fail");
                        skip += packIds.Count;
                        done += packIds.Count;
                        onProgress(total > 0 ? (int)(done * 90.0 / total) : 90, pass, fail, skip);
                    }
                    else
                    {
                        foreach (var sheetId in packIds)
                        {
                            if (LemoineRun.CancelRequested)
                            {
                                pushLog(LemoineStrings.T("export.bulkExport.log.stoppedExport", pass), "warn");
                                return;
                            }
                            var sheet   = doc.GetElement(sheetId);
                            string safeName = ResolveExportName(sheet, projNumber, projName, "DWG", pushLog);

                            try
                            {
                                string outDir = SplitByFormat ? EnsureSubfolder(OutputFolder, "DWG") : OutputFolder;
                                bool ok = doc.Export(outDir, safeName, new List<ElementId> { sheetId }, dwgOpts);
                                if (ok)
                                {
                                    pass++; _dwg++;
                                    pushLog(LemoineStrings.T("export.bulkExport.log.dwgOk", safeName), "pass");
                                }
                                else
                                {
                                    fail++;
                                    pushLog(LemoineStrings.T("export.bulkExport.log.dwgFalse", safeName), "fail");
                                }
                            }
                            catch (Exception ex)
                            {
                                fail++;
                                pushLog(LemoineStrings.T("export.bulkExport.log.dwgFail", safeName, ex.Message), "fail");
                            }
                            done++;
                            onProgress(total > 0 ? (int)(done * 90.0 / total) : 90, pass, fail, skip);
                        }
                    }
                }
            }
        }

        // ── Individual mode ───────────────────────────────────────────────────

        private void ExportIndividualMode(
            Document doc, List<Element> elements,
            string projNumber, string projName,
            Action<string, string> pushLog,
            Action<int, int, int, int> onProgress,
            ref int pass, ref int fail, ref int skip,
            bool nwcReady)
        {
            // Effective flags — incorporate all pre-flight results
            bool nwcEffective = ExportNwc && ExportMode != "Sheets" && nwcReady;
            bool ifcEffective = ExportIfc && ExportMode != "Sheets";

            // When CombinePdf is on, all sheets go in one Export call → counts as 1 PDF op
            int pdfOps = ExportPdf ? (CombinePdf ? 1 : elements.Count) : 0;
            int dwgOps = ExportDwg ? elements.Count : 0;
            int nwcOps = nwcEffective ? elements.Count : 0;
            int ifcOps = ifcEffective ? elements.Count : 0;
            int total  = pdfOps + dwgOps + nwcOps + ifcOps;
            int done   = 0;

            // ── PDF ───────────────────────────────────────────────────────────
            if (ExportPdf)
            {
                if (CombinePdf)
                {
                    // One combined call with all IDs; filename from first element's tokens
                    try
                    {
                        string outDir    = SplitByFormat ? EnsureSubfolder(OutputFolder, "PDF") : OutputFolder;
                        string safeName  = ResolveExportName(elements[0], projNumber, projName, "PDF", pushLog);
                        var allIds       = elements.Select(e => e.Id).ToList();
                        var opts         = BuildPdfOptions(safeName, combine: true);
                        bool ok          = doc.Export(outDir, allIds, opts);
                        if (ok)
                        {
                            pass++; _pdf++;
                            pushLog(LemoineStrings.T("export.bulkExport.log.pdfCombinedOk", safeName, allIds.Count), "pass");
                        }
                        else
                        {
                            fail++;
                            pushLog(LemoineStrings.T("export.bulkExport.log.pdfCombinedFalse"), "fail");
                        }
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        pushLog(LemoineStrings.T("export.bulkExport.log.pdfCombinedFail", ex.Message), "fail");
                    }
                    done++;
                    onProgress(total > 0 ? (int)(done * 90.0 / total) : 90, pass, fail, skip);
                }
                else
                {
                    // One call per element
                    foreach (var element in elements)
                    {
                        if (LemoineRun.CancelRequested)
                        {
                            pushLog(LemoineStrings.T("export.bulkExport.log.stoppedExport", pass), "warn");
                            return;
                        }
                        string safeName = ResolveExportName(element, projNumber, projName, "PDF", pushLog);

                        try
                        {
                            string outDir = SplitByFormat ? EnsureSubfolder(OutputFolder, "PDF") : OutputFolder;
                            var opts      = BuildPdfOptions(safeName, combine: false);
                            bool ok       = doc.Export(outDir, new List<ElementId> { element.Id }, opts);
                            if (ok)
                            {
                                pass++; _pdf++;
                                pushLog(LemoineStrings.T("export.bulkExport.log.pdfOk", safeName), "pass");
                            }
                            else
                            {
                                fail++;
                                pushLog(LemoineStrings.T("export.bulkExport.log.pdfFalse", safeName), "fail");
                            }
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            pushLog(LemoineStrings.T("export.bulkExport.log.pdfFail", safeName, ex.Message), "fail");
                        }
                        done++;
                        onProgress(total > 0 ? (int)(done * 90.0 / total) : 90, pass, fail, skip);
                    }
                }
            }

            // ── DWG ───────────────────────────────────────────────────────────
            if (ExportDwg)
            {
                var dwgOpts = BuildDwgOptions(doc);

                if (dwgOpts == null)
                {
                    pushLog(LemoineStrings.T("export.bulkExport.log.dwgNoSetupAll", DwgSetupName), "fail");
                    skip += elements.Count;
                    done += elements.Count;
                    onProgress(total > 0 ? (int)(done * 90.0 / total) : 90, pass, fail, skip);
                }
                else
                {
                    foreach (var element in elements)
                    {
                        if (LemoineRun.CancelRequested)
                        {
                            pushLog(LemoineStrings.T("export.bulkExport.log.stoppedExport", pass), "warn");
                            return;
                        }
                        string safeName = ResolveExportName(element, projNumber, projName, "DWG", pushLog);

                        try
                        {
                            string outDir = SplitByFormat ? EnsureSubfolder(OutputFolder, "DWG") : OutputFolder;
                            bool ok = doc.Export(outDir, safeName, new List<ElementId> { element.Id }, dwgOpts);
                            if (ok)
                            {
                                pass++; _dwg++;
                                pushLog(LemoineStrings.T("export.bulkExport.log.dwgOk", safeName), "pass");
                            }
                            else
                            {
                                fail++;
                                pushLog(LemoineStrings.T("export.bulkExport.log.dwgFalse", safeName), "fail");
                            }
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            pushLog(LemoineStrings.T("export.bulkExport.log.dwgFail", safeName, ex.Message), "fail");
                        }
                        done++;
                        onProgress(total > 0 ? (int)(done * 90.0 / total) : 90, pass, fail, skip);
                    }
                }
            }

            // ── NWC ───────────────────────────────────────────────────────────
            // 3D views only. Sheets mode and unavailable exporter are blocked in pre-flight.
            if (nwcEffective)
            {
                foreach (var element in elements)
                {
                    if (LemoineRun.CancelRequested)
                    {
                        pushLog(LemoineStrings.T("export.bulkExport.log.stoppedExport", pass), "warn");
                        return;
                    }
                    string safeName = ResolveExportName(element, projNumber, projName, "NWC", pushLog);

                    try
                    {
                        if (!(element is View3D view3d))
                        {
                            skip++;
                            pushLog(LemoineStrings.T("export.bulkExport.log.nwcNot3d", safeName), "warn");
                        }
                        else
                        {
                            string outDir = SplitByFormat ? EnsureSubfolder(OutputFolder, "NWC") : OutputFolder;

                            var opts = ExportOptionsFactory.BuildNwcOptions(BuildNwcOptionSet(), view3d.Id, pushLog);

                            // NWC export uses the 3-parameter overload — ViewId is set on the options object
                            doc.Export(outDir, safeName, opts);
                            pass++; _nwc++;
                            pushLog(LemoineStrings.T("export.bulkExport.log.nwcOk", safeName), "pass");
                        }
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        pushLog(LemoineStrings.T("export.bulkExport.log.nwcFail", safeName, ex.Message), "fail");
                    }
                    done++;
                    onProgress(total > 0 ? (int)(done * 90.0 / total) : 90, pass, fail, skip);
                }
            }

            // ── IFC ───────────────────────────────────────────────────────────
            // 3D views only. Sheets mode is blocked in pre-flight.
            if (ifcEffective)
            {
                foreach (var element in elements)
                {
                    if (LemoineRun.CancelRequested)
                    {
                        pushLog(LemoineStrings.T("export.bulkExport.log.stoppedExport", pass), "warn");
                        return;
                    }
                    string safeName = ResolveExportName(element, projNumber, projName, "IFC", pushLog);

                    try
                    {
                        if (!(element is View3D))
                        {
                            skip++;
                            pushLog(LemoineStrings.T("export.bulkExport.log.ifcNot3d", safeName), "warn");
                        }
                        else
                        {
                            string outDir = SplitByFormat ? EnsureSubfolder(OutputFolder, "IFC") : OutputFolder;

                            var opts = ExportOptionsFactory.BuildIfcOptions(IfcVersion, element.Id);

                            // IFC export writes IFC-specific data to the document and requires a transaction
                            using (var t = new Transaction(doc, "Batch IFC Export"))
                            {
                                t.Start();
                                doc.Export(outDir, safeName, opts);
                                t.Commit();
                            }
                            pass++; _ifc++;
                            pushLog(LemoineStrings.T("export.bulkExport.log.ifcOk", safeName), "pass");
                        }
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        pushLog(LemoineStrings.T("export.bulkExport.log.ifcFail", safeName, ex.Message), "fail");
                    }
                    done++;
                    onProgress(total > 0 ? (int)(done * 90.0 / total) : 90, pass, fail, skip);
                }
            }
        }

        // ── Builders ──────────────────────────────────────────────────────────

        // Option construction is delegated to ExportOptionsFactory so Bulk Export and
        // Print View build identical PDF/DWG output.
        private PDFExportOptions BuildPdfOptions(string fileName, bool combine)
            => ExportOptionsFactory.BuildPdfOptions(
                   fileName, combine, PdfPlacement, ColorDepth, RasterQuality,
                   ZoomSetting, ZoomPercent, ViewLinksInBlue, ReplaceHalftoneWithThinLines);

        // Returns null when a named setup is specified but does not exist in the document.
        private DWGExportOptions? BuildDwgOptions(Document doc)
            => ExportOptionsFactory.BuildDwgOptions(doc, DwgSetupName);

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

        // ── Helpers ───────────────────────────────────────────────────────────

        // Tokens are resolved against whatever is being exported. A ViewSheet exposes the
        // sheet parameters; any other View has none of them, so its name/type drive the
        // tokens and the sheet tokens fall back to the view name (so a stray sheet pattern
        // still yields a non-empty name instead of silently producing "-").
        private static Dictionary<string, string> BuildTokens(Element? element, string projNumber, string projName)
        {
            var tokens = new Dictionary<string, string>
            {
                ["ProjectNumber"] = projNumber,
                ["ProjectName"]   = projName,
                ["Year"]          = DateTime.Now.Year.ToString(),
                ["Month"]         = DateTime.Now.Month.ToString("D2"),
                ["Day"]           = DateTime.Now.Day.ToString("D2"),
            };

            if (element is ViewSheet sheet)
            {
                tokens["SheetNumber"] = sheet.SheetNumber ?? "";
                tokens["SheetName"]   = sheet.Name ?? "";
                tokens["Revision"]    = element.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION)?.AsString() ?? "";
                tokens["IssueDate"]   = element.get_Parameter(BuiltInParameter.SHEET_ISSUE_DATE)?.AsString()       ?? "";
                tokens["ViewName"]    = sheet.Name ?? "";
                tokens["ViewType"]    = "Sheet";
            }
            else if (element is View view)
            {
                string viewName = view.Name ?? "";
                tokens["ViewName"]    = viewName;
                tokens["ViewType"]    = view.ViewType.ToString();
                tokens["SheetName"]   = viewName;   // fallback so a sheet pattern still resolves
                tokens["SheetNumber"] = "";
                tokens["Revision"]    = "";
                tokens["IssueDate"]   = "";
            }

            return tokens;
        }

        // Resolves the export filename and never silently emits a junk name. If the pattern
        // resolves to something with no usable character (e.g. an all-empty-token pattern
        // collapsing to "-"), the failure is reported to the run log AND diagnostics.log,
        // and a deterministic fallback (element name, else element id) is used instead.
        private string ResolveExportName(Element? element, string projNumber, string projName,
                                         string fmt, Action<string, string> pushLog)
        {
            var    tokens   = BuildTokens(element, projNumber, projName);
            string resolved = LemoineTokenInput.Resolve(FilenamePattern, tokens);

            if (resolved.Any(char.IsLetterOrDigit))
                return SanitizeFilename(resolved);

            // Degenerate — report loudly and fall back.
            string label    = element?.Name ?? "";
            string fallback = SanitizeFilename(label);
            if (!fallback.Any(char.IsLetterOrDigit))
                fallback = "export-" + (element?.Id.Value.ToString() ?? "0");

            pushLog(LemoineStrings.T("export.bulkExport.log.nameFallback", fmt, FilenamePattern, label, (element is ViewSheet ? LemoineStrings.T("export.bulkExport.words.sheet") : LemoineStrings.T("export.bulkExport.words.view")), fallback), "warn");
            LemoineLog.Warn("BulkExport.ResolveExportName",
                $"Degenerate filename. fmt={fmt} pattern='{FilenamePattern}' resolved='{resolved}' " +
                $"element={element?.Id} name='{label}' fallback='{fallback}'");
            return fallback;
        }

        private static string SanitizeFilename(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim();
            // Guard against an entirely-illegal pattern resolving to an empty string
            return name.Length > 0 ? name : "export";
        }

        private static string EnsureSubfolder(string parent, string sub)
        {
            string dir = Path.Combine(parent, sub);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
