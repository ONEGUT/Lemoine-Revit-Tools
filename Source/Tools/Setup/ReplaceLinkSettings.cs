using System;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Setup
{
    /// <summary>
    /// Persisted defaults for the Replace Link tool. Public + parameterless so
    /// <see cref="XmlSerializer"/> accepts it (an internal root throws "only public types can be
    /// processed" and silently resets every field — see CLAUDE.md).
    /// </summary>
    [XmlRoot("ReplaceLinkSettings")]
    public sealed class ReplaceLinkSettings
    {
        private static readonly Lazy<ReplaceLinkSettings> _lazy = new Lazy<ReplaceLinkSettings>(Load);
        public static ReplaceLinkSettings Instance => _lazy.Value;

        public ReplaceLinkSettings() { }

        public ReplaceDestination Destination    { get; set; } = ReplaceDestination.OverwriteLinkedFile;
        public ReplacePosition    Position       { get; set; } = ReplacePosition.KeepPlacement;
        /// <summary>Defaults ON — overwriting the file a live link points at is otherwise
        /// irreversible.</summary>
        public bool               BackupOriginal { get; set; } = true;
        public bool               AuditOnOpen    { get; set; } = false;
        public bool               ReportMovement { get; set; } = true;
        public string             LastSelectedFolder { get; set; } = "";

        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LemoineTools");
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("ReplaceLinkSettings: create config directory", ex); }
                return Path.Combine(dir, "ReplaceLinkSettings.xml");
            }
        }

        public void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(ReplaceLinkSettings));
                using (var w = new StreamWriter(FilePath)) xs.Serialize(w, this);
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("ReplaceLinkSettings.Save", ex); }
        }

        private static ReplaceLinkSettings Load()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    var xs = new XmlSerializer(typeof(ReplaceLinkSettings));
                    using (var r = new StreamReader(path)) return (ReplaceLinkSettings)xs.Deserialize(r)!;
                }
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("ReplaceLinkSettings.Load", ex); }
            return new ReplaceLinkSettings();
        }
    }
}
