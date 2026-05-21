using System;
using System.IO;
using System.Xml.Serialization;
using System.Collections.Generic;
using System.Linq;

namespace LemoineTools.Tools.LinkViews
{
    [XmlRoot("LinkViewsDisciplineSettings")]
    public sealed class LinkViewsDisciplineSettings
    {
        private static readonly Lazy<LinkViewsDisciplineSettings> _lazy =
            new Lazy<LinkViewsDisciplineSettings>(Load);
        public static LinkViewsDisciplineSettings Instance => _lazy.Value;

        public LinkViewsDisciplineSettings() { }

        /// <summary>
        /// Comma-separated list of discipline codes that receive a combined
        /// view (union of all links in that discipline).
        /// Default: ARCH and OTHER.
        /// </summary>
        public string CombinedDisciplinesRaw { get; set; } = "ARCH,OTHER";

        /// <summary>
        /// Additional discipline codes beyond the built-in set
        /// (ARCH, MEP, STRUCT, OTHER).  SKIP is always appended last.
        /// </summary>
        public string CustomDisciplinesRaw { get; set; } = "";

        /// <summary>Section box expansion buffer in feet applied when creating per-link and combined views.</summary>
        public double SectionBoxBuffer { get; set; } = 3.0;

        public HashSet<string> CombinedDisciplines =>
            new HashSet<string>(
                CombinedDisciplinesRaw
                    .Split(',')
                    .Select(s => s.Trim().ToUpperInvariant())
                    .Where(s => s.Length > 0),
                StringComparer.OrdinalIgnoreCase);

        /// <summary>Returns custom discipline codes excluding built-ins and SKIP.</summary>
        public List<string> CustomDisciplinesList =>
            (CustomDisciplinesRaw ?? "")
                .Split(',')
                .Select(s => s.Trim().ToUpperInvariant())
                .Where(s => s.Length > 0
                         && s != "ARCH" && s != "MEP"
                         && s != "STRUCT" && s != "OTHER" && s != "SKIP")
                .Distinct()
                .ToList();

        /// <summary>Full ordered discipline list: built-ins + custom + SKIP last.</summary>
        public List<string> AllDisciplines =>
            new[] { "ARCH", "MEP", "STRUCT", "OTHER" }
                .Concat(CustomDisciplinesList)
                .Concat(new[] { "SKIP" })
                .ToList();

        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                try { Directory.CreateDirectory(dir); } catch { }
                return Path.Combine(dir, "LinkViewsDisciplineSettings.xml");
            }
        }

        public void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(LinkViewsDisciplineSettings));
                using (var w = new StreamWriter(FilePath))
                    xs.Serialize(w, this);
            }
            catch { }
        }

        private static LinkViewsDisciplineSettings Load()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    var xs = new XmlSerializer(typeof(LinkViewsDisciplineSettings));
                    using (var r = new StreamReader(path))
                        return (LinkViewsDisciplineSettings)xs.Deserialize(r)!;
                }
            }
            catch { }
            return new LinkViewsDisciplineSettings();
        }
    }
}
