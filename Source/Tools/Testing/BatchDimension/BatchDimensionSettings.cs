using System;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Testing
{
    /// <summary>
    /// Persistent settings for the Batch Dimension tool.
    /// Saved to %AppData%\LemoineTools\BatchDimensionSettings.xml.
    /// </summary>
    [XmlRoot("BatchDimensionSettings")]
    public sealed class BatchDimensionSettings
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        private static readonly Lazy<BatchDimensionSettings> _lazy =
            new Lazy<BatchDimensionSettings>(Load);

        public static BatchDimensionSettings Instance => _lazy.Value;

        // Required by XmlSerializer
        public BatchDimensionSettings() { }

        // ── Settings ──────────────────────────────────────────────────────────
        public string DefaultDimStyleName   { get; set; } = "";
        public string DefaultReferencePlane { get; set; } = "Face of Core Exterior";
        public string DefaultTieCondition   { get; set; } = "Tie to Nearest Grid";
        public double DefaultOffset         { get; set; } = 10.0;  // project units (mm)

        // ── Persistence ───────────────────────────────────────────────────────
        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                try { Directory.CreateDirectory(dir); } catch (Exception __lex) { LemoineLog.Swallowed("BatchDimensionSettings: create config directory", __lex); }
                return Path.Combine(dir, "BatchDimensionSettings.xml");
            }
        }

        /// <summary>Persist current values to disk. Silent on failure.</summary>
        public void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(BatchDimensionSettings));
                using (var w = new StreamWriter(FilePath))
                    xs.Serialize(w, this);
            }
            catch { /* never crash the UI over a settings write */ }
        }

        private static BatchDimensionSettings Load()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    var xs = new XmlSerializer(typeof(BatchDimensionSettings));
                    using (var r = new StreamReader(path))
                        return (BatchDimensionSettings)xs.Deserialize(r)!;
                }
            }
            catch (Exception __lex) { LemoineLog.Swallowed("BatchDimensionSettings.Load", __lex); }
            return new BatchDimensionSettings();
        }
    }

    // ── DimCategoryConfig — per-category dimension settings ───────────────────

    /// <summary>
    /// Serialisable class representing one category's dimension settings.
    /// </summary>
    public class DimCategoryConfig
    {
        public string CategoryName  { get; set; } = "Walls";
        public string BuiltInCatOST { get; set; } = "OST_Walls";
        public string ReferencePlane { get; set; } = "Face of Core Exterior";
        public string TieCondition   { get; set; } = "Tie to Nearest Grid";
        public double Offset         { get; set; } = 10.0;  // project units (mm)
    }
}
