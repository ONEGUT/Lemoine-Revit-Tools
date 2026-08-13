using System;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Ceilings.CeilingTags
{
    /// <summary>
    /// Persistent settings for the Tag Ceilings tool, saved to
    /// %AppData%\LemoineTools\CeilingTagSettings.xml.
    ///
    /// Machine-wide and shared by every project, so nothing here may name something inside one
    /// specific model. That is why the tag type is remembered by NAME rather than ElementId —
    /// an id from another project would either resolve to an unrelated element or to nothing.
    /// Must stay public: XmlSerializer rejects a non-public root type at construction, and
    /// because that call sits in a try/catch every save and load would fail silently.
    /// </summary>
    [XmlRoot("CeilingTagSettings")]
    public sealed class CeilingTagSettings
    {
        private static readonly Lazy<CeilingTagSettings> _lazy = new Lazy<CeilingTagSettings>(Load);
        public static CeilingTagSettings Instance => _lazy.Value;

        // Required by XmlSerializer
        public CeilingTagSettings() { }

        /// <summary>A corridor stretch longer than this gets evenly spaced extra tags. Feet.</summary>
        public double MaxTagSpacingFt { get; set; } = 30.0;

        /// <summary>Delete the view's existing ceiling tags before placing new ones.</summary>
        public bool ReplaceExisting { get; set; } = true;

        /// <summary>Treat a ceiling covered by a lower ceiling as only its visible part.</summary>
        public bool AccountForCovered { get; set; } = true;

        /// <summary>Last-used tag type, as "Family : Type". Resolved against the open project at
        /// run time — never an ElementId (see the class remarks).</summary>
        public string LastTagTypeName { get; set; } = "";

        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("CeilingTagSettings: create config directory", ex); }
                return Path.Combine(dir, "CeilingTagSettings.xml");
            }
        }

        public void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(CeilingTagSettings));
                using (var w = new StreamWriter(FilePath)) xs.Serialize(w, this);
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("CeilingTagSettings.Save", ex); }
        }

        private static CeilingTagSettings Load()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    var xs = new XmlSerializer(typeof(CeilingTagSettings));
                    using (var r = new StreamReader(path))
                        return (CeilingTagSettings)xs.Deserialize(r)!;
                }
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("CeilingTagSettings.Load", ex); }
            return new CeilingTagSettings();
        }
    }
}
