using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.BulkExport
{
    public class PrintViewEventHandler : IExternalEventHandler
    {
        // ── Inputs (set by ViewModel before Raise) ────────────────────────────
        public ElementId ViewId        { get; set; } = ElementId.InvalidElementId;
        public string    OutputFolder  { get; set; } = "";
        public string    ZoomSetting   { get; set; } = "Fit to Page";
        public int       ZoomPercent   { get; set; } = 100;
        public string    ColorDepth    { get; set; } = "Color";
        public string    RasterQuality { get; set; } = "High";
        public bool      ViewLinksInBlue              { get; set; } = false;
        public bool      ReplaceHalftoneWithThinLines { get; set; } = false;

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

            int pass = 0, fail = 0;

            try
            {
                if (app.ActiveUIDocument == null)
                {
                    pushLog("No active Revit document.", "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                var doc = app.ActiveUIDocument.Document;

                if (string.IsNullOrWhiteSpace(OutputFolder))
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

                var element = doc.GetElement(ViewId);
                if (element == null)
                {
                    pushLog("The view could not be found — it may have been deleted since the window was opened.", "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                string filename = ResolveFilename(element);
                var opts = BuildPdfOptions(filename);
                var ids  = new List<ElementId> { element.Id };

                pushLog($"Exporting '{filename}' to PDF…", "info");

                bool ok = doc.Export(OutputFolder, ids, opts);

                if (ok)
                {
                    pass++;
                    pushLog($"Exported: {filename}.pdf", "pass");
                }
                else
                {
                    fail++;
                    pushLog($"Revit returned false for '{filename}' — check view visibility and titleblock.", "fail");
                    LemoineLog.Warn("PrintView", $"doc.Export returned false for view {ViewId.Value}");
                }

                onProgress(100, pass, fail, 0);
                onComplete(pass, fail, 0);
            }
            catch (Exception ex)
            {
                LemoineLog.Error("PrintView.Execute", ex);
                pushLog($"Print error: {ex.Message}", "fail");
                onComplete(pass, 1, 0);
            }
        }

        private static string ResolveFilename(Element element)
        {
            string raw;
            if (element is ViewSheet sheet)
                raw = $"{sheet.SheetNumber}-{sheet.Name}";
            else
                raw = element.Name;

            return SanitizeFilename(raw);
        }

        private static string SanitizeFilename(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var result  = new System.Text.StringBuilder();
            foreach (char c in name)
                result.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            string s = result.ToString().Trim();
            return string.IsNullOrEmpty(s) ? "PrintView" : s;
        }

        private PDFExportOptions BuildPdfOptions(string fileName)
        {
            var opts = new PDFExportOptions
            {
                FileName                     = fileName,
                Combine                      = false,
                PaperPlacement               = PaperPlacementType.LowerLeft,
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
                case "Draft":        return RasterQualityType.Low;  // Draft removed in Revit 2024; map to Low
                case "Low":          return RasterQualityType.Low;
                case "Medium":       return RasterQualityType.Medium;
                case "Presentation": return RasterQualityType.Presentation;
                default:             return RasterQualityType.High;
            }
        }
    }
}
