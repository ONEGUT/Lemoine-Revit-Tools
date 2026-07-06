using System;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.UpgradeLinks
{
    /// <summary>
    /// Persisted defaults for the Upgrade &amp; Link Models tool. Public + parameterless so
    /// <see cref="XmlSerializer"/> accepts it (an internal root throws "only public types can be
    /// processed" and silently resets every field — see CLAUDE.md).
    /// </summary>
    [XmlRoot("UpgradeLinksSettings")]
    public sealed class UpgradeLinksSettings
    {
        private static readonly Lazy<UpgradeLinksSettings> _lazy = new Lazy<UpgradeLinksSettings>(Load);
        public static UpgradeLinksSettings Instance => _lazy.Value;

        public UpgradeLinksSettings() { }

        // Last folder picked for the "Selected folder" destination — remembered generally
        // (not per-project), same convention as other tools' remembered output folders.
        public string             LastSelectedFolder { get; set; } = "";
        public UpgradePlacement   DefaultPlacement { get; set; } = UpgradePlacement.OriginToOrigin;
        public UpgradeDestination Destination      { get; set; } = UpgradeDestination.CurrentLocation;
        public bool               AuditOnOpen      { get; set; } = false;
        public bool               ReloadExisting   { get; set; } = true;

        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LemoineTools");
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { LemoineLog.Swallowed("UpgradeLinksSettings: create config directory", ex); }
                return Path.Combine(dir, "UpgradeLinksSettings.xml");
            }
        }

        public void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(UpgradeLinksSettings));
                using (var w = new StreamWriter(FilePath)) xs.Serialize(w, this);
            }
            catch (Exception ex) { LemoineLog.Swallowed("UpgradeLinksSettings.Save", ex); }
        }

        private static UpgradeLinksSettings Load()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    var xs = new XmlSerializer(typeof(UpgradeLinksSettings));
                    using (var r = new StreamReader(path)) return (UpgradeLinksSettings)xs.Deserialize(r)!;
                }
            }
            catch (Exception ex) { LemoineLog.Swallowed("UpgradeLinksSettings.Load", ex); }
            return new UpgradeLinksSettings();
        }
    }
}
