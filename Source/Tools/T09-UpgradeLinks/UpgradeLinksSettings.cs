using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.UpgradeLinks
{
    /// <summary>One remembered "save subfolder files here" folder for a cloud-hosted project,
    /// keyed by the host's cloud model GUID (<see cref="ModelPath.GetModelGUID"/>) so the user
    /// isn't asked again next time they open the tool on the same cloud model.</summary>
    public sealed class CloudHostFolderEntry
    {
        [XmlAttribute] public string ModelGuid { get; set; } = "";
        [XmlAttribute] public string Folder    { get; set; } = "";
    }

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

        public string             SubfolderName    { get; set; } = "Upgraded Links";
        public UpgradePlacement   DefaultPlacement { get; set; } = UpgradePlacement.OriginToOrigin;
        public UpgradeDestination Destination      { get; set; } = UpgradeDestination.Subfolder;
        public bool               AuditOnOpen      { get; set; } = false;
        public bool               ReloadExisting   { get; set; } = true;

        [XmlArray("CloudHostFolders"), XmlArrayItem("Entry")]
        public List<CloudHostFolderEntry> CloudHostFolders { get; set; } = new List<CloudHostFolderEntry>();

        /// <summary>The folder the user previously picked for this cloud model's host, or null.</summary>
        public string? GetCloudHostFolder(string modelGuid)
        {
            if (string.IsNullOrEmpty(modelGuid)) return null;
            return CloudHostFolders
                .FirstOrDefault(e => string.Equals(e.ModelGuid, modelGuid, StringComparison.OrdinalIgnoreCase))
                ?.Folder;
        }

        /// <summary>Remembers <paramref name="folder"/> for this cloud model's host and saves immediately.</summary>
        public void SetCloudHostFolder(string modelGuid, string folder)
        {
            if (string.IsNullOrEmpty(modelGuid) || string.IsNullOrEmpty(folder)) return;
            var existing = CloudHostFolders
                .FirstOrDefault(e => string.Equals(e.ModelGuid, modelGuid, StringComparison.OrdinalIgnoreCase));
            if (existing != null) existing.Folder = folder;
            else CloudHostFolders.Add(new CloudHostFolderEntry { ModelGuid = modelGuid, Folder = folder });
            Save();
        }

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
