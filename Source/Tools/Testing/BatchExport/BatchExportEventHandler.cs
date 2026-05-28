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
                            pushLog($"Warning: sheet {sheet.SheetNumber} has no titleblock — paper size may be incorrect.", "warn");
                    }
                }

                if (Packs.Count > 0 && ExportMode == "Sheets")
                    ExportPackMode(doc, elements, projNumber, projName, pushLog, onProgress, ref pass, ref fail, ref skip);
                else
                    ExportIndividualMode(doc, elements, projNumber, projName, pushLog, onProgress, ref pass, ref fail, ref skip);

                onProgress(100, pass, fail, skip);
                onComplete(pass, fail, skip);
            }
            catch (Exception ex)
            {
                pushLog($"Batch Export error: {ex.Message}", "fail");
                onComplete(pass, 1, skip);
            }
        }

        // ── Pack mode ─────────────────────────────────────────────────────────

        private void ExportPackMode(
            Document doc, List<Element> elements,
            string projNumber, string projName,
            Action<string, string> pushLog,
            Action<int, int, int, int> onProgress,
            ref int pass, ref int fail, ref int skip)
        {
            var sheetNumToId = elements.OfType<ViewSheet>()
                .ToDictionary(s => s.SheetNumber, s => s.Id);

            // Pre-build DWG options once — setup lookup is constant across packs
            DWGExportOptions? dwgOpts     = null;
            bool              dwgResolved = false;

            int totalPdf = ExportPdf ? Packs.Count : 0;
            int totalDwg = ExportDwg ? Packs.Sum(p => p.SheetNumbers.Count) : 0;
            int total    = totalPdf + totalDwg;
            int done     = 0;

            foreach (var pack in Packs)
            {
                var packIds = pack.SheetNumbers
                    .Where(n => sheetNumToId.ContainsKey(n))
                    .Select(n => sheetNumToId[n])
                    .ToList();

                if (packIds.Count == 0)
                {
                    pushLog($"Pack '{pack.PackName}': no matching sheets in selection — skipped.", "fail");
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
                            pass++;
                            pushLog($"PDF (pack): {packName}.pdf [{packIds.Count} sheets]", "pass");
                        }
                        else
                        {
                            fail++;
                            pushLog($"PDF failed — pack '{pack.PackName}': export returned false.", "fail");
                        }
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        pushLog($"PDF failed — pack '{pack.PackName}': {ex.Message}", "fail");
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
                        pushLog($"DWG setup '{DwgSetupName}' not found — pack '{pack.PackName}' DWG skipped.", "fail");
                        skip += packIds.Count;
                        done += packIds.Count;
                        onProgress(total > 0 ? (int)(done * 90.0 / total) : 90, pass, fail, skip);
                    }
                    else
                    {
                        foreach (var sheetId in packIds)
                        {
                            var sheet   = doc.GetElement(sheetId);
                            var tokens  = BuildTokens(sheet, projNumber, projName);
                            string safeName = SanitizeFilename(LemoineTokenInput.Resolve(FilenamePattern, tokens));

                            try
                            {
                                string outDir = SplitByFormat ? EnsureSubfolder(OutputFolder, "DWG") : OutputFolder;
                                bool ok = doc.Export(outDir, safeName, new List<ElementId> { sheetId }, dwgOpts);
                                if (ok)
                                {
                                    pass++;
                                    pushLog($"DWG: {safeName}.dwg", "pass");
                                }
                                else
                                {
                                    fail++;
                                    pushLog($"DWG failed — {safeName}: export returned false.", "fail");
                                }
                            }
                            catch (Exception ex)
                            {
                                fail++;
                                pushLog($"DWG failed — {safeName}: {ex.Message}", "fail");
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
            ref int pass, ref int fail, ref int skip)
        {
            // When CombinePdf is on, all sheets go in one Export call → counts as 1 PDF op
            int pdfOps = ExportPdf ? (CombinePdf ? 1 : elements.Count) : 0;
            int dwgOps = ExportDwg ? elements.Count : 0;
            int total  = pdfOps + dwgOps;
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
                        var firstTokens  = BuildTokens(elements[0], projNumber, projName);
                        string safeName  = SanitizeFilename(LemoineTokenInput.Resolve(FilenamePattern, firstTokens));
                        var allIds       = elements.Select(e => e.Id).ToList();
                        var opts         = BuildPdfOptions(safeName, combine: true);
                        bool ok          = doc.Export(outDir, allIds, opts);
                        if (ok)
                        {
                            pass++;
                            pushLog($"PDF (combined): {safeName}.pdf [{allIds.Count} items]", "pass");
                        }
                        else
                        {
                            fail++;
                            pushLog("PDF failed — combined export returned false.", "fail");
                        }
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        pushLog($"PDF failed — combined: {ex.Message}", "fail");
                    }
                    done++;
                    onProgress(total > 0 ? (int)(done * 90.0 / total) : 90, pass, fail, skip);
                }
                else
                {
                    // One call per element
                    foreach (var element in elements)
                    {
                        var tokens  = BuildTokens(element, projNumber, projName);
                        string safeName = SanitizeFilename(LemoineTokenInput.Resolve(FilenamePattern, tokens));

                        try
                        {
                            string outDir = SplitByFormat ? EnsureSubfolder(OutputFolder, "PDF") : OutputFolder;
                            var opts      = BuildPdfOptions(safeName, combine: false);
                            bool ok       = doc.Export(outDir, new List<ElementId> { element.Id }, opts);
                            if (ok)
                            {
                                pass++;
                                pushLog($"PDF: {safeName}.pdf", "pass");
                            }
                            else
                            {
                                fail++;
                                pushLog($"PDF failed — {safeName}: export returned false.", "fail");
                            }
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            pushLog($"PDF failed — {safeName}: {ex.Message}", "fail");
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
                    pushLog($"DWG setup '{DwgSetupName}' not found — all DWG exports skipped.", "fail");
                    skip += elements.Count;
                    done += elements.Count;
                    onProgress(total > 0 ? (int)(done * 90.0 / total) : 90, pass, fail, skip);
                }
                else
                {
                    foreach (var element in elements)
                    {
                        var tokens  = BuildTokens(element, projNumber, projName);
                        string safeName = SanitizeFilename(LemoineTokenInput.Resolve(FilenamePattern, tokens));

                        try
                        {
                            string outDir = SplitByFormat ? EnsureSubfolder(OutputFolder, "DWG") : OutputFolder;
                            bool ok = doc.Export(outDir, safeName, new List<ElementId> { element.Id }, dwgOpts);
                            if (ok)
                            {
                                pass++;
                                pushLog($"DWG: {safeName}.dwg", "pass");
                            }
                            else
                            {
                                fail++;
                                pushLog($"DWG failed — {safeName}: export returned false.", "fail");
                            }
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            pushLog($"DWG failed — {safeName}: {ex.Message}", "fail");
                        }
                        done++;
                        onProgress(total > 0 ? (int)(done * 90.0 / total) : 90, pass, fail, skip);
                    }
                }
            }
        }

        // ── Builders ──────────────────────────────────────────────────────────

        private PDFExportOptions BuildPdfOptions(string fileName, bool combine)
        {
            var opts = new PDFExportOptions
            {
                FileName                     = fileName,
                Combine                      = combine,
                PaperPlacement               = PdfPlacement == "Center"
                                                   ? PaperPlacementType.Center
                                                   : PaperPlacementType.LowerLeft,
                ColorDepth                   = MapColorDepth(ColorDepth),
                RasterQuality                = MapRasterQuality(RasterQuality),
                ViewLinksInBlue              = ViewLinksInBlue,
                ReplaceHalftoneWithThinLines = ReplaceHalftoneWithThinLines,
            };

            if (ZoomSetting == "Scale %")
            {
                opts.ZoomType       = ZoomType.Zoom;
                opts.ZoomPercentage = ZoomPercent;
            }
            else
            {
                opts.ZoomType = ZoomType.FitToPage;
            }

            return opts;
        }

        // Returns null when a named setup is specified but does not exist in the document.
        private DWGExportOptions? BuildDwgOptions(Document doc)
        {
            if (!string.IsNullOrEmpty(DwgSetupName))
            {
                bool found = false;
                foreach (var s in new FilteredElementCollector(doc)
                    .OfClass(typeof(ExportDWGSettings))
                    .Cast<ExportDWGSettings>())
                {
                    if (s.Name == DwgSetupName) { found = true; break; }
                }
                if (!found) return null;
            }
            return new DWGExportOptions { MergedViews = true };
        }

        // ── Enum mappers ──────────────────────────────────────────────────────

        private static ColorDepthType MapColorDepth(string value)
        {
            switch (value)
            {
                case "Grayscale":     return ColorDepthType.GrayScale;
                case "Black & White": return ColorDepthType.BlackLine;
                default:              return ColorDepthType.Color;
            }
        }

        private static RasterQualityType MapRasterQuality(string value)
        {
            switch (value)
            {
                case "Draft":        return RasterQualityType.Low;   // Draft removed in Revit 2024; map to Low
                case "Low":          return RasterQualityType.Low;
                case "Medium":       return RasterQualityType.Medium;
                case "Presentation": return RasterQualityType.Presentation;
                default:             return RasterQualityType.High;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static Dictionary<string, string> BuildTokens(Element? element, string projNumber, string projName)
        {
            return new Dictionary<string, string>
            {
                ["SheetNumber"]   = element?.get_Parameter(BuiltInParameter.SHEET_NUMBER)?.AsString()           ?? "",
                ["SheetName"]     = element?.get_Parameter(BuiltInParameter.SHEET_NAME)?.AsString()             ?? "",
                ["Revision"]      = element?.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION)?.AsString() ?? "",
                ["IssueDate"]     = element?.get_Parameter(BuiltInParameter.SHEET_ISSUE_DATE)?.AsString()       ?? "",
                ["ProjectNumber"] = projNumber,
                ["ProjectName"]   = projName,
                ["Year"]          = DateTime.Now.Year.ToString(),
                ["Month"]         = DateTime.Now.Month.ToString("D2"),
                ["Day"]           = DateTime.Now.Day.ToString("D2"),
            };
        }

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
