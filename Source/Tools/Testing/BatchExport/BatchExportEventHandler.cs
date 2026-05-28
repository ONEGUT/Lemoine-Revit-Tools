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
        public bool                        ExportPdf       { get; set; } = true;
        public bool                        ExportDwg       { get; set; } = false;
        public bool                        ExportNwc       { get; set; } = false;
        public bool                        ExportIfc       { get; set; } = false;
        public bool                        CombinePdf      { get; set; } = false;
        public string                      DwgSetupName    { get; set; } = "";
        public string                      PdfPlacement    { get; set; } = "Center";
        public string                      HiddenLines     { get; set; } = "Vector Processing";
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

                // TEMPORARY DEBUG — log Navisworks assemblies present in AppDomain
                if (ExportNwc)
                {
                    debug.Log("NWC-SCAN", "Scanning AppDomain for Navisworks-related assemblies...");
                    int nwcFound = 0;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        string name = asm.GetName().Name ?? "";
                        if (name.IndexOf("navisworks", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("nwexport",   StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            debug.Log("NWC-SCAN", $"  Found: {name}",
                                $"Version={asm.GetName().Version}  Location={asm.Location}");
                            nwcFound++;
                        }
                    }
                    debug.Log("NWC-SCAN", $"Scan complete — {nwcFound} Navisworks assembly/ies in AppDomain.");

                    if (ExportMode == "Sheets")
                    {
                        pushLog("NWC: Skipped — NWC requires Views mode (3D views only). Switch to Views in Step 1.", "fail");
                        debug.Log("NWC", "Export blocked — ExportMode is Sheets, NWC only supports 3D Views.");
                        skip += elements.Count;
                    }
                }

                // Effective NWC/IFC: blocked entirely when in Sheets mode
                bool nwcEffective = ExportNwc && ExportMode != "Sheets";
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
                    // Build token dictionary for this element
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

                    debug.Log("ELEMENT", $"Id={element.Id}  Type={element.GetType().Name}  SafeName={safeName}");

                    // ── PDF ───────────────────────────────────────────────────
                    if (ExportPdf)
                    {
                        try
                        {
                            string outDir = SplitByFormat
                                ? EnsureSubfolder(OutputFolder, "PDF")
                                : OutputFolder;

                            var ids  = new List<ElementId> { element.Id };
                            var opts = new PDFExportOptions
                            {
                                FileName = safeName,
                                Combine  = CombinePdf,
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
                            string outDir = SplitByFormat
                                ? EnsureSubfolder(OutputFolder, "DWG")
                                : OutputFolder;

                            // Locate export setup by name
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
                    // NWC is 3D-views-only; Sheets mode is blocked before the loop.
                    if (nwcEffective)
                    {
                        try
                        {
                            if (!(element is View3D view3d))
                            {
                                skip++;
                                pushLog($"NWC: Skipped {safeName} — not a 3D view.", "warn");
                                debug.Log("NWC", $"Skipped {safeName} — element type={element.GetType().Name}");
                            }
                            else
                            {
                                string outDir = SplitByFormat
                                    ? EnsureSubfolder(OutputFolder, "NWC")
                                    : OutputFolder;

                                // TEMPORARY DEBUG — log the options type we're using
                                var opts = new NavisworksExportOptions();
                                debug.Log("NWC", $"NavisworksExportOptions type: {opts.GetType().FullName}");
                                debug.Log("NWC", $"Assembly: {opts.GetType().Assembly.GetName().Name}  v{opts.GetType().Assembly.GetName().Version}");
                                debug.Log("NWC", $"Calling doc.Export({outDir}, {safeName}, [{view3d.Id}], opts)");

                                doc.Export(outDir, safeName, new List<ElementId> { view3d.Id }, opts);
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
                    // IFC is 3D-views-only; Sheets mode is blocked by ifcEffective.
                    if (ifcEffective)
                    {
                        try
                        {
                            if (!(element is View3D))
                            {
                                skip++;
                                pushLog($"IFC: Skipped {safeName} — not a 3D view.", "warn");
                                debug.Log("IFC", $"Skipped {safeName} — element type={element.GetType().Name}");
                            }
                            else
                            {
                                string outDir = SplitByFormat
                                    ? EnsureSubfolder(OutputFolder, "IFC")
                                    : OutputFolder;

                                var opts = new IFCExportOptions();
                                opts.FileVersion  = IfcVersion == "IFC4" ? IFCVersion.IFC4 : IFCVersion.IFC2x3;
                                opts.FilterViewId = element.Id;

                                debug.Log("IFC", $"IFCExportOptions type: {opts.GetType().FullName}");
                                debug.Log("IFC", $"FileVersion={opts.FileVersion}  FilterViewId={opts.FilterViewId}");
                                debug.Log("IFC", $"Calling doc.Export({outDir}, {safeName}, opts)");

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
