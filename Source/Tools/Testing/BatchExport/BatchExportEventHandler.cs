using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.Testing
{
    /// <summary>
    /// Executes the bulk export on the Revit API thread.
    /// Set all properties before calling ExternalEvent.Raise().
    /// </summary>
    public class BatchExportEventHandler : IExternalEventHandler
    {
        // ── Inputs set by ViewModel before Raise ──────────────────────────────
        public List<ElementId>             SelectedIds     { get; set; } = new List<ElementId>();
        public string                      ExportMode      { get; set; } = "Sheets"; // "Sheets" | "Views"
        public string                      FilenamePattern { get; set; } = "{SheetNumber}-{SheetName}";
        public string                      OutputFolder    { get; set; } = "";
        public bool                        SplitByFormat   { get; set; } = false;

        // ── Format flags ──────────────────────────────────────────────────────
        public bool                        ExportPdf       { get; set; } = true;
        public bool                        ExportDwg       { get; set; } = false;
        public bool                        ExportNwc       { get; set; } = false;
        public bool                        ExportIfc       { get; set; } = false;

        // ── PDF options ───────────────────────────────────────────────────────
        public bool                        CombinePdf      { get; set; } = false;
        public string                      PdfPlacement    { get; set; } = "Center";
        public string                      HiddenLines     { get; set; } = "Vector Processing";

        // ── DWG options ───────────────────────────────────────────────────────
        public string                      DwgSetupName    { get; set; } = "";

        // ── NWC options (all NavisworksExportOptions properties) ──────────────
        public string                      NwcCoordinates        { get; set; } = "Shared";
        public string                      NwcParameters         { get; set; } = "All";
        public bool                        NwcConvertElementProps{ get; set; } = true;
        public bool                        NwcDivideByLevel      { get; set; } = false;
        public bool                        NwcExportLinks        { get; set; } = true;
        public bool                        NwcExportParts        { get; set; } = false;
        public bool                        NwcExportElementIds   { get; set; } = true;
        public bool                        NwcExportUrls         { get; set; } = false;
        public bool                        NwcFindMissingMaterials { get; set; } = false;
        public bool                        NwcExportRoomGeometry { get; set; } = false;
        public bool                        NwcExportRoomAsAttribute { get; set; } = false;
        public bool                        NwcConvertLights      { get; set; } = false;
        public bool                        NwcConvertLinkedCad   { get; set; } = false;
        public double                      NwcFacetingFactor     { get; set; } = 1.0;

        // ── IFC options ───────────────────────────────────────────────────────
        public string                      IfcVersion      { get; set; } = "IFC2x3";

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "BatchExport";

        public void Execute(UIApplication app)
        {
            var pushLog    = PushLog    ?? ((m, s) => { });
            var onProgress = OnProgress ?? ((p, a, b, c) => { });
            var onComplete = OnComplete ?? ((a, b, c) => { });

            int pass = 0, fail = 0, skip = 0;

            // TEMPORARY DEBUG — remove before release once NWC/IFC export is stable
            using var debug = new BatchExportDebugLogger(OutputFolder);

            try
            {
                var doc = app.ActiveUIDocument.Document;

                debug.Log("INIT", $"ExportMode={ExportMode}  PDF={ExportPdf}  DWG={ExportDwg}  NWC={ExportNwc}  IFC={ExportIfc}  IFCVersion={IfcVersion}");
                debug.Log("INIT", $"OutputFolder={OutputFolder}  SplitByFormat={SplitByFormat}  SelectedCount={SelectedIds.Count}");
                debug.Log("INIT", $"RevitDoc={doc.PathName}  Title={doc.Title}");

                // Ensure output folder exists
                if (string.IsNullOrEmpty(OutputFolder))
                {
                    pushLog("Output folder not specified.", "fail");
                    onComplete(0, 1, 0);
                    return;
                }
                try { Directory.CreateDirectory(OutputFolder); }
                catch (Exception ex)
                {
                    pushLog($"Cannot create output folder: {ex.Message}", "fail");
                    debug.Log("INIT-ERROR", "CreateDirectory failed", $"{ex.GetType().Name}: {ex.Message}");
                    onComplete(0, 1, 0);
                    return;
                }

                // Collect elements
                var elements = SelectedIds
                    .Select(id => doc.GetElement(id))
                    .Where(e => e != null)
                    .ToList();

                if (elements.Count == 0)
                {
                    pushLog("No elements found for the selected IDs.", "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                // ── NWC pre-flight ────────────────────────────────────────────
                // Check Sheets mode block and Navisworks availability before touching the loop
                bool nwcReady = false;
                if (ExportNwc)
                {
                    if (ExportMode == "Sheets")
                    {
                        pushLog("NWC: Skipped — NWC requires Views mode (3D views only). Switch to Views in Step 1.", "fail");
                        debug.Log("NWC", "Export blocked — ExportMode is Sheets.");
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
                            pushLog("NWC: Navisworks Exporter not available — is Navisworks Manage installed and loaded in this session?", "fail");
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

                // Effective format flags — NWC/IFC only run in Views mode with NWC available
                bool nwcEffective = ExportNwc && ExportMode != "Sheets" && nwcReady;
                bool ifcEffective = ExportIfc && ExportMode != "Sheets";

                int total = elements.Count * (ExportPdf     ? 1 : 0)
                          + elements.Count * (ExportDwg     ? 1 : 0)
                          + elements.Count * (nwcEffective  ? 1 : 0)
                          + elements.Count * (ifcEffective  ? 1 : 0);
                int done  = 0;

                // Resolve project-level tokens once
                string projNumber = doc.ProjectInformation?.get_Parameter(BuiltInParameter.PROJECT_NUMBER)?.AsString() ?? "";
                string projName   = doc.ProjectInformation?.get_Parameter(BuiltInParameter.PROJECT_NAME)?.AsString()   ?? "";

                foreach (var element in elements)
                {
                    var tokens = new Dictionary<string, string>
                    {
                        ["SheetNumber"]   = element.get_Parameter(BuiltInParameter.SHEET_NUMBER)?.AsString()           ?? "",
                        ["SheetName"]     = element.get_Parameter(BuiltInParameter.SHEET_NAME)?.AsString()             ?? "",
                        ["Revision"]      = element.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION)?.AsString() ?? "",
                        ["IssueDate"]     = element.get_Parameter(BuiltInParameter.SHEET_ISSUE_DATE)?.AsString()       ?? "",
                        ["ProjectNumber"] = projNumber,
                        ["ProjectName"]   = projName,
                        ["Year"]          = DateTime.Now.Year.ToString(),
                        ["Month"]         = DateTime.Now.Month.ToString("D2"),
                        ["Day"]           = DateTime.Now.Day.ToString("D2"),
                    };

                    string resolved = LemoineTokenInput.Resolve(FilenamePattern, tokens);
                    string safeName = SanitizeFilename(resolved);

                    debug.Log("ELEMENT", $"Id={element.Id}  Type={element.GetType().Name}  Name={safeName}");

                    // ── PDF ───────────────────────────────────────────────────
                    if (ExportPdf)
                    {
                        try
                        {
                            string outDir = SplitByFormat ? EnsureSubfolder(OutputFolder, "PDF") : OutputFolder;
                            var ids  = new List<ElementId> { element.Id };
                            var opts = new PDFExportOptions
                            {
                                FileName       = safeName,
                                Combine        = CombinePdf,
                                PaperPlacement = PdfPlacement == "Center"
                                    ? PaperPlacementType.Center
                                    : PaperPlacementType.LowerLeft,
                            };
                            doc.Export(outDir, ids, opts);
                            pass++;
                            pushLog($"PDF: {safeName}.pdf", "pass");
                            debug.Log("PDF", $"OK: {safeName}.pdf");
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            pushLog($"PDF failed — {safeName}: {ex.Message}", "fail");
                            debug.Log("PDF-ERROR", $"Failed: {safeName}", $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                        }
                        done++;
                        onProgress(total > 0 ? (int)(done * 90.0 / total) : 90, pass, fail, skip);
                    }

                    // ── DWG ───────────────────────────────────────────────────
                    if (ExportDwg)
                    {
                        try
                        {
                            string outDir = SplitByFormat ? EnsureSubfolder(OutputFolder, "DWG") : OutputFolder;

                            ExportDWGSettings? setup = null;
                            if (!string.IsNullOrEmpty(DwgSetupName))
                            {
                                foreach (var s in new FilteredElementCollector(doc)
                                    .OfClass(typeof(ExportDWGSettings))
                                    .Cast<ExportDWGSettings>())
                                {
                                    if (s.Name == DwgSetupName) { setup = s; break; }
                                }
                            }

                            if (setup == null && !string.IsNullOrEmpty(DwgSetupName))
                            {
                                pushLog($"DWG setup '{DwgSetupName}' not found — skipped {safeName}.", "fail");
                                debug.Log("DWG", $"Setup '{DwgSetupName}' not found — skip {safeName}");
                                skip++;
                            }
                            else
                            {
                                var dwgOpts = new DWGExportOptions { MergedViews = true };
                                doc.Export(outDir, safeName, new List<ElementId> { element.Id }, dwgOpts);
                                pass++;
                                pushLog($"DWG: {safeName}.dwg", "pass");
                                debug.Log("DWG", $"OK: {safeName}.dwg");
                            }
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            pushLog($"DWG failed — {safeName}: {ex.Message}", "fail");
                            debug.Log("DWG-ERROR", $"Failed: {safeName}", $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                        }
                        done++;
                        onProgress(total > 0 ? (int)(done * 90.0 / total) : 90, pass, fail, skip);
                    }

                    // ── NWC ───────────────────────────────────────────────────
                    // 3D views only. Sheets mode and unavailable exporter are blocked above.
                    if (nwcEffective)
                    {
                        try
                        {
                            if (!(element is View3D view3d))
                            {
                                skip++;
                                pushLog($"NWC: Skipped {safeName} — not a 3D view.", "warn");
                                debug.Log("NWC", $"Skipped {safeName} — type={element.GetType().Name}");
                            }
                            else
                            {
                                string outDir = SplitByFormat ? EnsureSubfolder(OutputFolder, "NWC") : OutputFolder;

                                var opts = new NavisworksExportOptions();

                                // Scope: always View for per-view batch — ViewId drives which view exports
                                opts.ExportScope              = NavisworksExportScope.View;
                                opts.ViewId                   = view3d.Id;

                                // Coordinates
                                opts.Coordinates = NwcCoordinates == "Internal"
                                    ? NavisworksCoordinates.Internal
                                    : NavisworksCoordinates.Shared;

                                // Parameters
                                opts.Parameters = NwcParameters switch
                                {
                                    "Elements" => NavisworksParameters.Elements,
                                    "None"     => NavisworksParameters.None,
                                    _          => NavisworksParameters.All,
                                };

                                opts.ConvertElementProperties = NwcConvertElementProps;
                                opts.DivideFileIntoLevels     = NwcDivideByLevel;
                                opts.ExportLinks              = NwcExportLinks;
                                opts.ExportParts              = NwcExportParts;
                                opts.ExportElementIds         = NwcExportElementIds;
                                opts.ExportUrls               = NwcExportUrls;
                                opts.FindMissingMaterials     = NwcFindMissingMaterials;
                                opts.ExportRoomGeometry       = NwcExportRoomGeometry;
                                opts.ExportRoomAsAttribute    = NwcExportRoomAsAttribute;
                                opts.ConvertLights            = NwcConvertLights;
                                opts.FacetingFactor           = NwcFacetingFactor;

                                // ConvertLinkedCADFormats was added mid-cycle — guard for older API versions
                                // TEMPORARY DEBUG — remove try/catch once minimum API version confirmed
                                try { opts.ConvertLinkedCADFormats = NwcConvertLinkedCad; }
                                catch (Exception ex) { debug.Log("NWC", "ConvertLinkedCADFormats not available in this API build", ex.Message); }

                                debug.Log("NWC", $"Calling doc.Export({outDir}, {safeName}, opts) [3-param NWC overload]  ViewId={view3d.Id}");

                                // NWC export uses the 3-parameter overload — ViewId is set on the options object
                                doc.Export(outDir, safeName, opts);
                                pass++;
                                pushLog($"NWC: {safeName}.nwc", "pass");
                                debug.Log("NWC", $"OK: {safeName}.nwc");
                            }
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            pushLog($"NWC failed — {safeName}: {ex.Message}", "fail");
                            debug.Log("NWC-ERROR", $"Failed: {safeName}", $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                        }
                        done++;
                        onProgress(total > 0 ? (int)(done * 90.0 / total) : 90, pass, fail, skip);
                    }

                    // ── IFC ───────────────────────────────────────────────────
                    // 3D views only. Sheets mode is blocked by ifcEffective.
                    if (ifcEffective)
                    {
                        try
                        {
                            if (!(element is View3D))
                            {
                                skip++;
                                pushLog($"IFC: Skipped {safeName} — not a 3D view.", "warn");
                                debug.Log("IFC", $"Skipped {safeName} — type={element.GetType().Name}");
                            }
                            else
                            {
                                string outDir = SplitByFormat ? EnsureSubfolder(OutputFolder, "IFC") : OutputFolder;

                                var opts = new IFCExportOptions();
                                opts.FileVersion  = IfcVersion == "IFC4" ? IFCVersion.IFC4 : IFCVersion.IFC2x3;
                                opts.FilterViewId = element.Id;

                                debug.Log("IFC", $"Calling doc.Export({outDir}, {safeName}, opts)  Version={opts.FileVersion}  FilterViewId={opts.FilterViewId}");

                                doc.Export(outDir, safeName, opts);
                                pass++;
                                pushLog($"IFC: {safeName}.ifc", "pass");
                                debug.Log("IFC", $"OK: {safeName}.ifc");
                            }
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            pushLog($"IFC failed — {safeName}: {ex.Message}", "fail");
                            debug.Log("IFC-ERROR", $"Failed: {safeName}", $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                        }
                        done++;
                        onProgress(total > 0 ? (int)(done * 90.0 / total) : 90, pass, fail, skip);
                    }
                }

                debug.Log("COMPLETE", $"Pass={pass}  Fail={fail}  Skip={skip}");
                onProgress(100, pass, fail, skip);
                onComplete(pass, fail, skip);
            }
            catch (Exception ex)
            {
                debug.Log("FATAL", "Unhandled exception in Execute()", $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                pushLog($"Batch Export error: {ex.Message}", "fail");
                onComplete(pass, 1, skip);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string SanitizeFilename(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }

        private static string EnsureSubfolder(string parent, string sub)
        {
            string dir = Path.Combine(parent, sub);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
