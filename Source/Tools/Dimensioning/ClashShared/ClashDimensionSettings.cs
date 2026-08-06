using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Dimensioning
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
        // Colour shown for clashes that match no Auto Filter rule (hex, with hatch fill).
        public string FallbackColorHex  { get; set; } = "#FF00FF";
        // Overlap avoidance for placed dimensions (keeps them off each other, the clash
        // markers, and optionally pre-existing annotations).
        public bool   AvoidOverlaps     { get; set; } = true;
        public string OverlapMode       { get; set; } = "Stagger"; // "Stagger" | "Probe"
        public double StaggerFactor     { get; set; } = 2.5;
        public int    MaxLanes          { get; set; } = 6;
        public bool   AvoidExisting     { get; set; } = true;
        public string DimTarget         { get; set; } = "Edge";   // "Edge" | "Centre"
        public string FillStyle         { get; set; } = "Solid";  // "Solid" | "Outline"
        public string CrossLineTypeName { get; set; } = "";
        public bool   ClearPrevious     { get; set; } = true;
        public int    MaxClashes        { get; set; } = 500;
        // Depth below a level still counted as that level's storey when assigning clashes to
        // plan views (mm). Settings-only — edited in Settings → Dimensions, no per-run override.
        public double StoreyMarginMm    { get; set; } = 609.6;

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

        // NOTE: this type deliberately carries NO raw Revit ElementIds.
        // Ten id lists (Group1/2ElemIds, Group1/2ElemLinkIds, Group1/2SourceLinkIds,
        // GridIds, FloorIds, GridLinkIds, FloorLinkIds) were removed: nothing ever wrote
        // them, and an ElementId is meaningless outside the document it came from — this
        // file is machine-wide and shared by every project. Element picks live on
        // ClashGroupSpec, keyed per document. Do not reintroduce ids here.

        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                try { Directory.CreateDirectory(dir); } catch (Exception __lex) { DiagnosticsLog.Swallowed("ClashDimensionSettings: ensure settings directory exists", __lex); }
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
            catch (Exception __lex) { DiagnosticsLog.Error("ClashDimensionSettings: save settings", __lex); }
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
            catch (Exception __lex) { DiagnosticsLog.Swallowed("ClashDimensionSettings: load settings (using defaults)", __lex); }
            return new ClashDimensionSettings();
        }
    }
}
