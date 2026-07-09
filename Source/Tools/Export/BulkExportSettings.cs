using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Framework;

namespace LemoineTools.Tools.BulkExport
{
    [XmlRoot("BulkExportSettings")]
    public sealed class BulkExportSettings
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        private static readonly Lazy<BulkExportSettings> _lazy =
            new Lazy<BulkExportSettings>(Load);

        public static BulkExportSettings Instance => _lazy.Value;

        public BulkExportSettings() { }

        // ── Output ────────────────────────────────────────────────────────────
        public string OutputFolder  { get; set; } = "";
        public bool   SplitByFormat { get; set; } = true;

        // ── Filename ──────────────────────────────────────────────────────────
        // Separate patterns per export mode so each defaults to tokens valid for its
        // own elements (sheets have sheet number/name; views have view name/type).
        public string FilenamePattern     { get; set; } = "{SheetNumber}-{SheetName}";
        public string ViewFilenamePattern { get; set; } = "{ViewName}";

        // ── Formats ───────────────────────────────────────────────────────────
        public bool ExportPdf { get; set; } = true;
        public bool ExportDwg { get; set; } = false;

        // ── PDF — page setup ──────────────────────────────────────────────────
        public string PdfPaperPlacement { get; set; } = "Offset from Corner";
        public string ZoomSetting       { get; set; } = "Fit to Page";   // "Fit to Page" | "Scale %"
        public int    ZoomPercent       { get; set; } = 100;

        // ── PDF — output quality ──────────────────────────────────────────────
        public string ColorDepth    { get; set; } = "Color";             // "Color" | "Grayscale" | "Black & White"
        public string RasterQuality { get; set; } = "High";              // "Draft"|"Low"|"Medium"|"High"|"Presentation"
        public bool   HiddenLinesVector { get; set; } = true;

        // ── PDF — combine ─────────────────────────────────────────────────────
        public bool CombinePdf { get; set; } = true;

        // ── PDF — advanced ────────────────────────────────────────────────────
        public bool ViewLinksInBlue             { get; set; } = false;
        public bool ReplaceHalftoneWithThinLines { get; set; } = false;

        // ── DWG ───────────────────────────────────────────────────────────────
        public string DwgExportSetupName { get; set; } = "";

        // ── NWC options ───────────────────────────────────────────────────────
        public bool ExportNwc { get; set; } = false;

        // All NavisworksExportOptions properties — persisted so they survive Revit sessions
        public string NwcCoordinates           { get; set; } = "Shared";   // "Shared" | "Internal"
        public string NwcParameters            { get; set; } = "All";      // "All" | "Elements" | "None"
        public bool   NwcConvertElementProps   { get; set; } = true;
        public bool   NwcDivideByLevel         { get; set; } = false;
        public bool   NwcExportLinks           { get; set; } = true;
        public bool   NwcExportParts           { get; set; } = false;
        public bool   NwcExportElementIds      { get; set; } = true;
        public bool   NwcExportUrls            { get; set; } = false;
        public bool   NwcFindMissingMaterials  { get; set; } = false;
        public bool   NwcExportRoomGeometry    { get; set; } = false;
        public bool   NwcExportRoomAsAttribute { get; set; } = false;
        public bool   NwcConvertLights         { get; set; } = false;
        public bool   NwcConvertLinkedCad      { get; set; } = false;
        public double NwcFacetingFactor        { get; set; } = 1.0;        // 0.5–5.0

        // ── IFC options ───────────────────────────────────────────────────────
        public bool   ExportIfc  { get; set; } = false;
        public string IfcVersion { get; set; } = "IFC2x3"; // "IFC2x3" | "IFC4"

        // ── Persistence ───────────────────────────────────────────────────────
        // Print sets are Revit ViewSheetSet elements now — they live in the document itself,
        // not in this settings file (the old SavedPacks list was removed with the pack UI).
        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                try { Directory.CreateDirectory(dir); } catch (Exception __lex) { DiagnosticsLog.Swallowed("BulkExportSettings: create config directory", __lex); }
                return Path.Combine(dir, "BulkExportSettings.xml");
            }
        }

        public void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(BulkExportSettings));
                using (var w = new StreamWriter(FilePath))
                    xs.Serialize(w, this);
            }
            catch (Exception __lex) { DiagnosticsLog.Swallowed("BulkExportSettings.Save", __lex); }
        }

        // Legacy file written before this tool graduated from "Batch Export".
        private static string LegacyFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LemoineTools", "BatchExportSettings.xml");

        // One-time migration: read the old BatchExportSettings.xml (root element
        // "BatchExportSettings") into this type, then persist under the new name so
        // users keep their saved packs and preferences after the rename.
        private static BulkExportSettings? MigrateFromLegacy()
        {
            try
            {
                if (!File.Exists(LegacyFilePath)) return null;
                var xs = new XmlSerializer(typeof(BulkExportSettings),
                                           new XmlRootAttribute("BatchExportSettings"));
                BulkExportSettings migrated;
                using (var r = new StreamReader(LegacyFilePath))
                    migrated = (BulkExportSettings)xs.Deserialize(r)!;
                migrated.Save();   // write to the new BulkExportSettings.xml
                return migrated;
            }
            catch (Exception __lex)
            {
                DiagnosticsLog.Swallowed("BulkExportSettings.MigrateFromLegacy", __lex);
                return null;
            }
        }

        private static BulkExportSettings Load()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    var xs = new XmlSerializer(typeof(BulkExportSettings));
                    using (var r = new StreamReader(path))
                        return (BulkExportSettings)xs.Deserialize(r)!;
                }

                // No new-format file yet — migrate the legacy Batch Export file if present.
                var migrated = MigrateFromLegacy();
                if (migrated != null) return migrated;
            }
            catch (Exception __lex) { DiagnosticsLog.Swallowed("BulkExportSettings.Load", __lex); }
            return new BulkExportSettings();
        }
    }
}
