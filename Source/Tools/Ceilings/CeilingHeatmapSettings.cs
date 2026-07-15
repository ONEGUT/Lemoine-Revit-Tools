using System;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Ceilings
{
    /// <summary>
    /// Persistent settings for the Ceiling Heatmap tool.
    /// Saved to %AppData%\LemoineTools\CeilingHeatmapSettings.xml on each change.
    /// </summary>
    [XmlRoot("CeilingHeatmapSettings")]
    public sealed class CeilingHeatmapSettings
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        private static readonly Lazy<CeilingHeatmapSettings> _lazy =
            new Lazy<CeilingHeatmapSettings>(Load);

        public static CeilingHeatmapSettings Instance => _lazy.Value;

        // Required by XmlSerializer
        public CeilingHeatmapSettings() { }

        // ── Settings ──────────────────────────────────────────────────────────
        /// <summary>Low-end color as #RRGGBB hex string. Default: deep blue.</summary>
        public string ColorLow      { get; set; } = "#0000FF";

        /// <summary>Mid-point color as #RRGGBB hex string. Default: green.</summary>
        public string ColorMid      { get; set; } = "#00FF00";

        /// <summary>High-end color as #RRGGBB hex string. Default: red.</summary>
        public string ColorHigh     { get; set; } = "#FF0000";

        /// <summary>
        /// Snap tolerance in Revit internal units (feet).
        /// Default: 1/8 inch ≈ 0.01042 ft.
        /// </summary>
        public double ElevTolerance { get; set; } = 1.0 / 96.0;

        /// <summary>Whether to place a ceiling tag at the centroid of each ceiling.</summary>
        public bool   PlaceTags     { get; set; } = false;

        /// <summary>Whether to delete the project's existing heatmap filters before applying
        /// the new set. Default: true.</summary>
        public bool   DeleteExisting { get; set; } = true;

        // ── Persistence ───────────────────────────────────────────────────────
        private static string FilePath
        {
            get
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                try { Directory.CreateDirectory(dir); } catch (Exception __lex) { DiagnosticsLog.Swallowed("CeilingHeatmapSettings: create config directory", __lex); }
                return System.IO.Path.Combine(dir, "CeilingHeatmapSettings.xml");
            }
        }

        /// <summary>Persist current values to disk. Silent on failure.</summary>
        public void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(CeilingHeatmapSettings));
                using (var w = new StreamWriter(FilePath))
                    xs.Serialize(w, this);
            }
            catch (Exception __lex) { DiagnosticsLog.Swallowed("CeilingHeatmapSettings.Save", __lex); }
        }

        private static CeilingHeatmapSettings Load()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    var xs = new XmlSerializer(typeof(CeilingHeatmapSettings));
                    using (var r = new StreamReader(path))
                        return (CeilingHeatmapSettings)xs.Deserialize(r)!;
                }
            }
            catch (Exception __lex) { DiagnosticsLog.Swallowed("CeilingHeatmapSettings.Load", __lex); }
            return new CeilingHeatmapSettings();
        }
    }
}
