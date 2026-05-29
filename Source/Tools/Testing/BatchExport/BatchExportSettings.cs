using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Testing
{
    [XmlRoot("BatchExportSettings")]
    public sealed class BatchExportSettings
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        private static readonly Lazy<BatchExportSettings> _lazy =
            new Lazy<BatchExportSettings>(Load);

        public static BatchExportSettings Instance => _lazy.Value;

        public BatchExportSettings() { }

        // ── Output ────────────────────────────────────────────────────────────
        public string OutputFolder  { get; set; } = "";
        public bool   SplitByFormat { get; set; } = true;

        // ── Filename ──────────────────────────────────────────────────────────
        public string FilenamePattern { get; set; } = "{SheetNumber}-{SheetName}";

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

        // ── Saved packs ───────────────────────────────────────────────────────
        [XmlArray("SavedPacks")]
        [XmlArrayItem("Pack")]
        public List<SheetPackLayout> SavedPacks { get; set; } = new List<SheetPackLayout>();

        // ── Persistence ───────────────────────────────────────────────────────
        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                try { Directory.CreateDirectory(dir); } catch (Exception __lex) { LemoineLog.Swallowed("BatchExportSettings: create config directory", __lex); }
                return Path.Combine(dir, "BatchExportSettings.xml");
            }
        }

        public void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(BatchExportSettings));
                using (var w = new StreamWriter(FilePath))
                    xs.Serialize(w, this);
            }
            catch (Exception __lex) { LemoineLog.Swallowed("BatchExportSettings.Save", __lex); }
        }

        private static BatchExportSettings Load()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    var xs = new XmlSerializer(typeof(BatchExportSettings));
                    using (var r = new StreamReader(path))
                        return (BatchExportSettings)xs.Deserialize(r)!;
                }
            }
            catch (Exception __lex) { LemoineLog.Swallowed("BatchExportSettings.Load", __lex); }
            return new BatchExportSettings();
        }
    }
}
