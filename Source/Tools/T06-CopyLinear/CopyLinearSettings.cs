using System;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.CopyLinear
{
    /// <summary>
    /// Persisted defaults for the Copy Linear Elements tool. Public + parameterless-constructable
    /// so <see cref="XmlSerializer"/> accepts it (an internal root throws "only public types can be
    /// processed" and silently resets every field — see CLAUDE.md).
    /// </summary>
    [XmlRoot("CopyLinearSettings")]
    public sealed class CopyLinearSettings
    {
        private static readonly Lazy<CopyLinearSettings> _lazy = new Lazy<CopyLinearSettings>(Load);
        public static CopyLinearSettings Instance => _lazy.Value;

        public CopyLinearSettings() { }

        // Operation
        public string Mode              { get; set; } = "Split";  // "Split" | "Replace"

        // Split mode
        public double SegmentLengthFeet { get; set; } = 20.0;
        public double GapInches         { get; set; } = 0.0;
        public bool   KeepRemainder     { get; set; } = true;

        // Replace mode
        public double IntervalFeet      { get; set; } = 10.0;
        public double ExtraSpacingInches{ get; set; } = 0.0;
        public bool   AlignToSource     { get; set; } = true;
        public string LengthParamName   { get; set; } = "";
        public string FamilyKey         { get; set; } = "";  // "Category — Family: Type"

        // Manual placement override (used when AlignToSource is off) — source-run-frame
        // offsets applied identically to every placed instance.
        public double ManualOffsetXInches   { get; set; } = 0.0;  // along the run
        public double ManualOffsetYInches   { get; set; } = 0.0;  // sideways
        public double ManualOffsetZInches   { get; set; } = 0.0;  // up

        // Extra placement rotation (degrees) about each source run's own axes, applied to every
        // instance in both align and manual modes: X = about the run, Y = side, Z = up.
        public double RotationXDegrees      { get; set; } = 0.0;
        public double RotationYDegrees      { get; set; } = 0.0;
        public double RotationZDegrees      { get; set; } = 0.0;

        // Change detection
        public bool   DeletePrevious    { get; set; } = false;
        public bool   OnlyChanged       { get; set; } = false;
        public bool   DeleteOrphans     { get; set; } = true;

        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LemoineTools");
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { LemoineLog.Swallowed("CopyLinearSettings: create config directory", ex); }
                return Path.Combine(dir, "CopyLinearSettings.xml");
            }
        }

        public void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(CopyLinearSettings));
                using (var w = new StreamWriter(FilePath)) xs.Serialize(w, this);
            }
            catch (Exception ex) { LemoineLog.Swallowed("CopyLinearSettings.Save", ex); }
        }

        private static CopyLinearSettings Load()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    var xs = new XmlSerializer(typeof(CopyLinearSettings));
                    using (var r = new StreamReader(path)) return (CopyLinearSettings)xs.Deserialize(r)!;
                }
            }
            catch (Exception ex) { LemoineLog.Swallowed("CopyLinearSettings.Load", ex); }
            return new CopyLinearSettings();
        }
    }
}
