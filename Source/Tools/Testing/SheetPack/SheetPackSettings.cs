using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace LemoineTools.Tools.Testing
{
    /// <summary>
    /// Persistent settings for the Sheet Pack tool.
    /// Saved to %AppData%\LemoineTools\SheetPackSettings.xml.
    /// </summary>
    [XmlRoot("SheetPackSettings")]
    public sealed class SheetPackSettings
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        private static readonly Lazy<SheetPackSettings> _lazy =
            new Lazy<SheetPackSettings>(Load);

        public static SheetPackSettings Instance => _lazy.Value;

        // Required by XmlSerializer
        public SheetPackSettings() { }

        // ── Settings ──────────────────────────────────────────────────────────

        /// <summary>Default folder for pack export output.</summary>
        public string DefaultExportFolder { get; set; } = "";

        /// <summary>Default issue purpose text stamped on sheets.</summary>
        public string DefaultIssuePurpose { get; set; } = "";

        /// <summary>Default revision code stamped on sheets.</summary>
        public string DefaultRevisionCode { get; set; } = "";

        /// <summary>Name of the Revit shared/project parameter to write the pack name into (if present).</summary>
        public string PackNameParameter { get; set; } = "Issue Set";

        /// <summary>Name of the Revit shared/project parameter to write the issue purpose into (if present).</summary>
        public string IssuePurposeParameter { get; set; } = "Issue Purpose";

        /// <summary>When true, sheets are exported to PDF after pack metadata is written.</summary>
        public bool ExportPdfAfterStamp { get; set; } = false;

        /// <summary>Saved pack definitions from the most recent session.</summary>
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
                try { Directory.CreateDirectory(dir); } catch { }
                return Path.Combine(dir, "SheetPackSettings.xml");
            }
        }

        public void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(SheetPackSettings));
                using (var w = new StreamWriter(FilePath))
                    xs.Serialize(w, this);
            }
            catch { }
        }

        private static SheetPackSettings Load()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    var xs = new XmlSerializer(typeof(SheetPackSettings));
                    using (var r = new StreamReader(path))
                        return (SheetPackSettings)xs.Deserialize(r)!;
                }
            }
            catch { }
            return new SheetPackSettings();
        }
    }
}
