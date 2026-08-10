using System;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Framework;

namespace LemoineTools.Tools.ModifyElements
{
    /// <summary>
    /// Persisted defaults for the Split by Length tool.
    ///
    /// Public + parameterless-constructable so <see cref="XmlSerializer"/> accepts it — an internal
    /// root throws "only public types can be processed" inside the try/catch and every save and
    /// load then fails silently, leaving the tool stuck on defaults (see CLAUDE.md).
    ///
    /// Everything here is machine-wide and project-neutral: a length, a gap and a mode name none
    /// of which identify anything inside a specific model, so this tier is the right one.
    /// </summary>
    [XmlRoot("SplitByLengthSettings")]
    public sealed class SplitByLengthSettings
    {
        private static readonly Lazy<SplitByLengthSettings> _lazy = new Lazy<SplitByLengthSettings>(Load);

        /// <summary>The machine-wide settings instance, loaded on first use.</summary>
        public static SplitByLengthSettings Instance => _lazy.Value;

        public SplitByLengthSettings() { }

        /// <summary>Length of each piece, in feet. 10 ft is the common shipping/joint length.</summary>
        public double SegmentLengthFeet { get; set; } = 10.0;

        /// <summary>Gap between consecutive pieces, in inches. Zero keeps the run connected.</summary>
        public double GapInches { get; set; } = 0.0;

        /// <summary>
        /// False = exact segment lengths with an offcut at the end; true = equal pieces that all
        /// stay at or under the segment length.
        /// </summary>
        public bool EvenLengths { get; set; } = false;

        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LemoineTools");
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("SplitByLengthSettings: create config directory", ex); }
                return Path.Combine(dir, "SplitByLengthSettings.xml");
            }
        }

        /// <summary>Writes the current values to <c>%AppData%\LemoineTools\SplitByLengthSettings.xml</c>.</summary>
        public void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(SplitByLengthSettings));
                using (var w = new StreamWriter(FilePath)) xs.Serialize(w, this);
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("SplitByLengthSettings.Save", ex); }
        }

        private static SplitByLengthSettings Load()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    var xs = new XmlSerializer(typeof(SplitByLengthSettings));
                    using (var r = new StreamReader(path)) return (SplitByLengthSettings)xs.Deserialize(r)!;
                }
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("SplitByLengthSettings.Load", ex); }
            return new SplitByLengthSettings();
        }
    }
}
