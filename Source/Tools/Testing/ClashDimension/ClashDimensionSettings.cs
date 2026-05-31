using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Lemoine;

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
        // Max difference (mm) in distance-to-edge for clashes to share one grouped
        // dimension. 0 disables grouping (one dimension per clash, as before).
        public double GroupToleranceMm  { get; set; } = 50.0;
        public string DimTarget         { get; set; } = "Edge";   // "Edge" | "Centre"
        public string FillStyle         { get; set; } = "Solid";  // "Solid" | "Outline"
        public string CrossLineTypeName { get; set; } = "";
        public bool   ClearPrevious     { get; set; } = true;
        public int    MaxClashes        { get; set; } = 500;
        public bool   ShowAllDocuments  { get; set; } = false;

        [XmlArray("Group1RuleKeys")] [XmlArrayItem("Key")]
        public List<string> Group1RuleKeys { get; set; } = new List<string>();

        [XmlArray("Group2RuleKeys")] [XmlArrayItem("Key")]
        public List<string> Group2RuleKeys { get; set; } = new List<string>();

        // ── Per-group definition mode + selections (Rules | Categories | Elements) ──
        public string Group1Mode { get; set; } = "Rules";
        public string Group2Mode { get; set; } = "Rules";

        [XmlArray("Group1Categories")] [XmlArrayItem("Cat")]
        public List<string> Group1Categories { get; set; } = new List<string>();
        [XmlArray("Group2Categories")] [XmlArrayItem("Cat")]
        public List<string> Group2Categories { get; set; } = new List<string>();

        [XmlArray("Group1ElemIds")] [XmlArrayItem("Id")]
        public List<long> Group1ElemIds { get; set; } = new List<long>();
        [XmlArray("Group1ElemLinkIds")] [XmlArrayItem("Id")]
        public List<long> Group1ElemLinkIds { get; set; } = new List<long>();
        [XmlArray("Group2ElemIds")] [XmlArrayItem("Id")]
        public List<long> Group2ElemIds { get; set; } = new List<long>();
        [XmlArray("Group2ElemLinkIds")] [XmlArrayItem("Id")]
        public List<long> Group2ElemLinkIds { get; set; } = new List<long>();

        [XmlArray("Group1SourceLinkIds")] [XmlArrayItem("Id")]
        public List<long> Group1SourceLinkIds { get; set; } = new List<long>();
        [XmlArray("Group2SourceLinkIds")] [XmlArrayItem("Id")]
        public List<long> Group2SourceLinkIds { get; set; } = new List<long>();

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
                try { Directory.CreateDirectory(dir); } catch (Exception __lex) { LemoineLog.Swallowed("ClashDimensionSettings: ensure settings directory exists", __lex); }
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
            catch (Exception __lex) { LemoineLog.Error("ClashDimensionSettings: save settings", __lex); }
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
            catch (Exception __lex) { LemoineLog.Swallowed("ClashDimensionSettings: load settings (using defaults)", __lex); }
            return new ClashDimensionSettings();
        }
    }
}
