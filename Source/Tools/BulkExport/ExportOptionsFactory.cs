using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.BulkExport
{
    /// <summary>
    /// Builds the Revit export-options objects for every supported format.
    /// Shared by <see cref="BulkExportEventHandler"/> and <see cref="PrintViewEventHandler"/>
    /// so both tools produce identical PDF/DWG/NWC/IFC output from a single source of truth.
    /// Pure option construction only — the actual <c>doc.Export(...)</c> call, transactions,
    /// and progress reporting stay in the calling handler.
    /// </summary>
    internal static class ExportOptionsFactory
    {
        // ── PDF ───────────────────────────────────────────────────────────────
        public static PDFExportOptions BuildPdfOptions(
            string fileName,
            bool   combine,
            string placement,       // "Center" | "Offset from Corner"
            string colorDepth,
            string rasterQuality,
            string zoomSetting,     // "Scale %" | "Fit to Page"
            int    zoomPercent,
            bool   viewLinksInBlue,
            bool   replaceHalftone)
        {
            var opts = new PDFExportOptions
            {
                FileName                     = fileName,
                Combine                      = combine,
                PaperPlacement               = placement == "Center"
                                                   ? PaperPlacementType.Center
                                                   : PaperPlacementType.LowerLeft,
                ColorDepth                   = MapColorDepth(colorDepth),
                RasterQuality                = MapRasterQuality(rasterQuality),
                ViewLinksInBlue              = viewLinksInBlue,
                ReplaceHalftoneWithThinLines = replaceHalftone,
            };

            if (zoomSetting == "Scale %")
            {
                opts.ZoomType       = ZoomType.Zoom;
                opts.ZoomPercentage = zoomPercent;
            }
            else
            {
                opts.ZoomType = ZoomType.FitToPage;
            }

            return opts;
        }

        // ── DWG ───────────────────────────────────────────────────────────────
        // Returns null when a named setup is specified but does not exist in the document
        // — the caller must skip-and-log rather than export with a wrong/default setup.
        public static DWGExportOptions? BuildDwgOptions(Document doc, string setupName)
        {
            if (!string.IsNullOrEmpty(setupName))
            {
                bool found = new FilteredElementCollector(doc)
                    .OfClass(typeof(ExportDWGSettings))
                    .Cast<ExportDWGSettings>()
                    .Any(s => s.Name == setupName);
                if (!found) return null;
            }
            return new DWGExportOptions { MergedViews = true };
        }

        // ── NWC ───────────────────────────────────────────────────────────────
        // The exporter must be available and the source must be a 3D view — both are
        // the caller's responsibility to check before building these options.
        public static NavisworksExportOptions BuildNwcOptions(
            NwcOptionSet n, ElementId viewId, Action<string, string>? pushLog)
        {
            var opts = new NavisworksExportOptions
            {
                ExportScope = NavisworksExportScope.View,
                ViewId      = viewId,
                Coordinates = n.Coordinates == "Internal"
                                  ? NavisworksCoordinates.Internal
                                  : NavisworksCoordinates.Shared,
            };

            opts.Parameters = n.Parameters switch
            {
                "Elements" => NavisworksParameters.Elements,
                "None"     => NavisworksParameters.None,
                _          => NavisworksParameters.All,
            };

            opts.ConvertElementProperties = n.ConvertElementProps;
            opts.DivideFileIntoLevels     = n.DivideByLevel;
            opts.ExportLinks              = n.ExportLinks;
            opts.ExportParts              = n.ExportParts;
            opts.ExportElementIds         = n.ExportElementIds;
            opts.ExportUrls               = n.ExportUrls;
            opts.FindMissingMaterials     = n.FindMissingMaterials;
            opts.ExportRoomGeometry       = n.ExportRoomGeometry;
            opts.ExportRoomAsAttribute    = n.ExportRoomAsAttribute;
            opts.ConvertLights            = n.ConvertLights;
            opts.FacetingFactor           = n.FacetingFactor;

            // ConvertLinkedCADFormats was added mid-cycle — guard for older API versions.
            // Only warn the user if they actually had it enabled, but always record it.
            try { opts.ConvertLinkedCADFormats = n.ConvertLinkedCad; }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("Export: NWC ConvertLinkedCADFormats unsupported by this API", ex);
                if (n.ConvertLinkedCad)
                    pushLog?.Invoke("NWC: ConvertLinkedCADFormats is not supported by this Revit/Navisworks version — setting ignored.", "warn");
            }

            return opts;
        }

        // ── IFC ───────────────────────────────────────────────────────────────
        public static IFCExportOptions BuildIfcOptions(string version, ElementId filterViewId)
            => new IFCExportOptions
            {
                FileVersion  = version == "IFC4" ? IFCVersion.IFC4 : IFCVersion.IFC2x3,
                FilterViewId = filterViewId,
            };

        // ── Enum mappers ──────────────────────────────────────────────────────
        public static ColorDepthType MapColorDepth(string value)
        {
            switch (value)
            {
                case "Grayscale":     return ColorDepthType.GrayScale;
                case "Black & White": return ColorDepthType.BlackLine;
                default:              return ColorDepthType.Color;
            }
        }

        public static RasterQualityType MapRasterQuality(string value)
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

        // ── Filename sanitiser ────────────────────────────────────────────────
        public static string SanitizeFilename(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim();
            return name.Length > 0 ? name : "export";
        }
    }

    /// <summary>
    /// Carrier for the 14 Navisworks export settings, so the NWC builder takes one
    /// argument instead of fourteen. Populated from the tool's persisted settings.
    /// </summary>
    internal sealed class NwcOptionSet
    {
        public string Coordinates           = "Shared";   // "Shared" | "Internal"
        public string Parameters            = "All";      // "All" | "Elements" | "None"
        public bool   ConvertElementProps   = true;
        public bool   DivideByLevel         = false;
        public bool   ExportLinks           = true;
        public bool   ExportParts           = false;
        public bool   ExportElementIds      = true;
        public bool   ExportUrls            = false;
        public bool   FindMissingMaterials  = false;
        public bool   ExportRoomGeometry    = false;
        public bool   ExportRoomAsAttribute = false;
        public bool   ConvertLights         = false;
        public bool   ConvertLinkedCad      = false;
        public double FacetingFactor        = 1.0;        // 0.5–5.0
    }
}
