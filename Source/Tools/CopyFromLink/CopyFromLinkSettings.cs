using System;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Framework;

namespace LemoineTools.Tools.CopyFromLink
{
    /// <summary>
    /// Persisted defaults for the Copy Elements from Link tool. Public + parameterless so
    /// <see cref="XmlSerializer"/> accepts it (an internal root throws "only public types can be
    /// processed" and silently resets every field — see CLAUDE.md).
    /// </summary>
    [XmlRoot("CopyFromLinkSettings")]
    public sealed class CopyFromLinkSettings
    {
        private static readonly Lazy<CopyFromLinkSettings> _lazy = new Lazy<CopyFromLinkSettings>(Load);
        public static CopyFromLinkSettings Instance => _lazy.Value;

        public CopyFromLinkSettings() { }

        // Change detection
        public bool DeletePrevious { get; set; } = false;
        public bool OnlyChanged    { get; set; } = false;
        public bool DeleteOrphans  { get; set; } = true;

        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LemoineTools");
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("CopyFromLinkSettings: create config directory", ex); }
                return Path.Combine(dir, "CopyFromLinkSettings.xml");
            }
        }

        public void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(CopyFromLinkSettings));
                using (var w = new StreamWriter(FilePath)) xs.Serialize(w, this);
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("CopyFromLinkSettings.Save", ex); }
        }

        private static CopyFromLinkSettings Load()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    var xs = new XmlSerializer(typeof(CopyFromLinkSettings));
                    using (var r = new StreamReader(path)) return (CopyFromLinkSettings)xs.Deserialize(r)!;
                }
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("CopyFromLinkSettings.Load", ex); }
            return new CopyFromLinkSettings();
        }
    }
}
