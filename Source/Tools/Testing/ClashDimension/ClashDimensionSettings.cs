using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace LemoineTools.Tools.Testing
{
    [XmlRoot("ClashDimensionSettings")]
    public sealed class ClashDimensionSettings
    {
        private static readonly Lazy<ClashDimensionSettings> _lazy =
            new Lazy<ClashDimensionSettings>(Load);
        public static ClashDimensionSettings Instance => _lazy.Value;
        public ClashDimensionSettings() { }

        public double ToleranceMm       { get; set; } = 25.4;
        public string DimStyleName      { get; set; } = "";
        public double DimLineOffsetMm   { get; set; } = 100.0;
        public string DimTarget         { get; set; } = "Edge";   // "Edge" | "Centre"
        public string FillStyle         { get; set; } = "Solid";  // "Solid" | "Outline"
        public string CrossLineTypeName { get; set; } = "";
        public bool   ClearPrevious     { get; set; } = true;
        public int    MaxClashes        { get; set; } = 500;

        [XmlArray("Group1RuleKeys")] [XmlArrayItem("Key")]
        public List<string> Group1RuleKeys { get; set; } = new List<string>();

        [XmlArray("Group2RuleKeys")] [XmlArrayItem("Key")]
        public List<string> Group2RuleKeys { get; set; } = new List<string>();

        [XmlArray("GridIds")] [XmlArrayItem("Id")]
        public List<long> GridIds { get; set; } = new List<long>();

        [XmlArray("FloorIds")] [XmlArrayItem("Id")]
        public List<long> FloorIds { get; set; } = new List<long>();

        // Parallel link-instance ID for each GridId/FloorId entry.
        // 0 = host document; >0 = RevitLinkInstance.Id.Value from the host doc.
        [XmlArray("GridLinkIds")] [XmlArrayItem("Id")]
        public List<long> GridLinkIds { get; set; } = new List<long>();

        [XmlArray("FloorLinkIds")] [XmlArrayItem("Id")]
        public List<long> FloorLinkIds { get; set; } = new List<long>();

        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                try { Directory.CreateDirectory(dir); } catch { }
                return Path.Combine(dir, "ClashDimensionSettings.xml");
            }
        }

        public void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(ClashDimensionSettings));
                using (var w = new StreamWriter(FilePath))
                    xs.Serialize(w, this);
            }
            catch { }
        }

        private static ClashDimensionSettings Load()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    var xs = new XmlSerializer(typeof(ClashDimensionSettings));
                    using (var r = new StreamReader(path))
                        return (ClashDimensionSettings)xs.Deserialize(r)!;
                }
            }
            catch { }
            return new ClashDimensionSettings();
        }
    }
}
