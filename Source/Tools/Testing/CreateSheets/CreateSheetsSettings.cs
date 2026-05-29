using System;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Testing
{
    /// <summary>
    /// Persistent settings for the Create Sheets tool.
    /// Saved to %AppData%\LemoineTools\CreateSheetsSettings.xml.
    /// </summary>
    [XmlRoot("CreateSheetsSettings")]
    public sealed class CreateSheetsSettings
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        private static readonly Lazy<CreateSheetsSettings> _lazy =
            new Lazy<CreateSheetsSettings>(Load);

        public static CreateSheetsSettings Instance => _lazy.Value;

        // Required by XmlSerializer
        public CreateSheetsSettings() { }

        // ── Settings ──────────────────────────────────────────────────────────
        public string DefaultTitleblockName  { get; set; } = "";
        public string DefaultNamingScheme    { get; set; } = "{LevelName}";
        public int    DefaultStartingNumber  { get; set; } = 1;

        // ── Persistence ───────────────────────────────────────────────────────
        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                try { Directory.CreateDirectory(dir); } catch (Exception __lex) { LemoineLog.Swallowed("CreateSheetsSettings: create config directory", __lex); }
                return Path.Combine(dir, "CreateSheetsSettings.xml");
            }
        }

        public void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(CreateSheetsSettings));
                using (var w = new StreamWriter(FilePath))
                    xs.Serialize(w, this);
            }
            catch (Exception __lex) { LemoineLog.Swallowed("CreateSheetsSettings.Save", __lex); }
        }

        private static CreateSheetsSettings Load()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    var xs = new XmlSerializer(typeof(CreateSheetsSettings));
                    using (var r = new StreamReader(path))
                        return (CreateSheetsSettings)xs.Deserialize(r)!;
                }
            }
            catch (Exception __lex) { LemoineLog.Swallowed("CreateSheetsSettings.Load", __lex); }
            return new CreateSheetsSettings();
        }
    }
}
