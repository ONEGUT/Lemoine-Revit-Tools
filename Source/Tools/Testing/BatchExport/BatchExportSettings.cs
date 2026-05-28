using System;
using System.IO;
using System.Xml.Serialization;

namespace LemoineTools.Tools.Testing
{
    /// <summary>
    /// Persistent settings for the Batch Export tool.
    /// Saved to %AppData%\LemoineTools\BatchExportSettings.xml.
    /// </summary>
    [XmlRoot("BatchExportSettings")]
    public sealed class BatchExportSettings
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        private static readonly Lazy<BatchExportSettings> _lazy =
            new Lazy<BatchExportSettings>(Load);

        public static BatchExportSettings Instance => _lazy.Value;

        // Required by XmlSerializer
        public BatchExportSettings() { }

        // ── Output ────────────────────────────────────────────────────────────
        public string OutputFolder  { get; set; } = "";
        public bool   SplitByFormat { get; set; } = false;

        // ── Filename ──────────────────────────────────────────────────────────
        public string FilenamePattern { get; set; } = "{SheetNumber}-{SheetName}";

        // ── Formats ───────────────────────────────────────────────────────────
        public bool ExportPdf { get; set; } = true;
        public bool ExportDwg { get; set; } = false;

        // ── PDF options ───────────────────────────────────────────────────────
        public bool   CombinePdf        { get; set; } = false;
        public string PdfPaperPlacement { get; set; } = "Center";        // "Center" | "Offset"
        public bool   HiddenLinesVector { get; set; } = true;

        // ── DWG options ───────────────────────────────────────────────────────
        public string DwgExportSetupName { get; set; } = "";

        // ── NWC options ───────────────────────────────────────────────────────
        public bool ExportNwc { get; set; } = false;

        // ── IFC options ───────────────────────────────────────────────────────
        public bool   ExportIfc  { get; set; } = false;
        public string IfcVersion { get; set; } = "IFC2x3"; // "IFC2x3" | "IFC4"

        // ── Persistence ───────────────────────────────────────────────────────
        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                try { Directory.CreateDirectory(dir); } catch { }
                return Path.Combine(dir, "BatchExportSettings.xml");
            }
        }

        /// <summary>Persist current values to disk. Silent on failure.</summary>
        public void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(BatchExportSettings));
                using (var w = new StreamWriter(FilePath))
                    xs.Serialize(w, this);
            }
            catch { /* never crash the UI over a settings write */ }
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
            catch { }
            return new BatchExportSettings();
        }
    }
}
