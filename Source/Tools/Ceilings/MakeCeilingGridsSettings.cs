using System;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Ceilings
{
    [XmlRoot("MakeCeilingGridsSettings")]
    public sealed class MakeCeilingGridsSettings
    {
        private static readonly Lazy<MakeCeilingGridsSettings> _lazy =
            new Lazy<MakeCeilingGridsSettings>(Load);

        public static MakeCeilingGridsSettings Instance => _lazy.Value;

        public MakeCeilingGridsSettings() { }

        public string OutputFolder             { get; set; } = "";
        public bool   UseCeilingGridsSubfolder { get; set; } = false;

        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                try { Directory.CreateDirectory(dir); } catch (Exception __lex) { LemoineLog.Swallowed("MakeCeilingGridsSettings: create config directory", __lex); }
                return Path.Combine(dir, "MakeCeilingGridsSettings.xml");
            }
        }

        public void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(MakeCeilingGridsSettings));
                using (var w = new StreamWriter(FilePath))
                    xs.Serialize(w, this);
            }
            catch (Exception __lex) { LemoineLog.Swallowed("MakeCeilingGridsSettings.Save", __lex); }
        }

        private static MakeCeilingGridsSettings Load()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    var xs = new XmlSerializer(typeof(MakeCeilingGridsSettings));
                    using (var r = new StreamReader(path))
                        return (MakeCeilingGridsSettings)xs.Deserialize(r)!;
                }
            }
            catch (Exception __lex) { LemoineLog.Swallowed("MakeCeilingGridsSettings.Load", __lex); }
            return new MakeCeilingGridsSettings();
        }
    }
}
