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
        public List<ElementId>          SelectedIds     { get; set; } = new List<ElementId>();
        public string                   ExportMode      { get; set; } = "Sheets"; // "Sheets" | "Views"
        public string                   FilenamePattern { get; set; } = "{SheetNumber}-{SheetName}";
        public string                   OutputFolder    { get; set; } = "";
        public bool                     SplitByFormat   { get; set; } = false;
        public bool                     ExportPdf       { get; set; } = true;
        public bool                     ExportDwg       { get; set; } = false;
        public bool                     CombinePdf      { get; set; } = false;
        public string                   DwgSetupName    { get; set; } = "";
        public string                   PdfPlacement    { get; set; } = "Center";
        public string                   HiddenLines     { get; set; } = "Vector Processing";

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?    PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "BatchExport";

        public void Execute(UIApplication app)
        {
            var pushLog    = PushLog    ?? ((m, s) => { });
            var onProgress = OnProgress ?? ((p, a, b, c) => { });
            var onComplete = OnComplete ?? ((a, b, c) => { });

            int pass = 0, fail = 0, skip = 0;

            try
            {
                var doc = app.ActiveUIDocument.Document;

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

                int total = elements.Count * (ExportPdf ? 1 : 0) + elements.Count * (ExportDwg ? 1 : 0);
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

                    // ── PDF ───────────────────────────────────────────────────
                    if (ExportPdf)
                    {
                        try
                        {
                            string outDir = SplitByFormat
                                ? EnsureSubfolder(OutputFolder, "PDF")
                                : OutputFolder;

                            var ids = new List<ElementId> { element.Id };
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
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            pushLog($"PDF failed — {safeName}: {ex.Message}", "fail");
                        }
                        done++;
                        int pct = total > 0 ? (int)(done * 90.0 / total) : 90;
                        onProgress(pct, pass, fail, skip);
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
                                skip++;
                            }
                            else
                            {
                                var dwgOpts = new DWGExportOptions { MergedViews = true };

                                doc.Export(outDir, safeName, new List<ElementId> { element.Id }, dwgOpts);
                                pass++;
                                pushLog($"DWG: {safeName}.dwg", "pass");
                            }
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            pushLog($"DWG failed — {safeName}: {ex.Message}", "fail");
                        }
                        done++;
                        int pct = total > 0 ? (int)(done * 90.0 / total) : 90;
                        onProgress(pct, pass, fail, skip);
                    }
                }

                onProgress(100, pass, fail, skip);
                onComplete(pass, fail, skip);
            }
            catch (Exception ex)
            {
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
