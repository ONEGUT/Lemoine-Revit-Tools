using System;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Framework;

namespace LemoineTools.Tools.ScopeBoxes
{
    /// <summary>
    /// Persisted defaults for the Scope Box Creator. Public type — XmlSerializer
    /// silently fails to construct for non-public roots (see CLAUDE.md).
    /// </summary>
    [XmlRoot("ScopeBoxSettings")]
    public sealed class ScopeBoxSettings
    {
        private static readonly Lazy<ScopeBoxSettings> _lazy =
            new Lazy<ScopeBoxSettings>(Load);
        public static ScopeBoxSettings Instance => _lazy.Value;

        public ScopeBoxSettings() { }

        /// <summary>XY margin added around each cluster bounding box (feet).</summary>
        public double BufferXY         { get; set; } = 10.0;
        /// <summary>Max room-edge gap for union-find cluster merging (feet).</summary>
        public double ClusterThreshold { get; set; } = 20.0;
        /// <summary>Fallback height above the topmost level when no level exists above it (feet).</summary>
        public double TopLevelHeight   { get; set; } = 12.0;

        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                try { Directory.CreateDirectory(dir); } catch (Exception __lex) { DiagnosticsLog.Swallowed("ScopeBoxSettings: create config directory", __lex); }
                return Path.Combine(dir, "ScopeBoxSettings.xml");
            }
        }

        public void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(ScopeBoxSettings));
                using (var w = new StreamWriter(FilePath))
                    xs.Serialize(w, this);
            }
            catch (Exception __lex) { DiagnosticsLog.Swallowed("ScopeBoxSettings.Save", __lex); }
        }

        private static ScopeBoxSettings Load()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    var xs = new XmlSerializer(typeof(ScopeBoxSettings));
                    using (var r = new StreamReader(path))
                        return (ScopeBoxSettings)xs.Deserialize(r)!;
                }
            }
            catch (Exception __lex) { DiagnosticsLog.Swallowed("ScopeBoxSettings.Load", __lex); }
            return new ScopeBoxSettings();
        }
    }
}
