using System.IO;
using System.Xml.Serialization;

namespace LemoineTools.Tools.Ceilings
{
    [XmlRoot("CeilingColorRamp")]
    public sealed class CeilingColorRamp
    {
        public string Low  { get; set; } = "#0000FF";
        public string Mid  { get; set; } = "#00FF00";
        public string High { get; set; } = "#FF0000";

        public void SaveTo(string path)
        {
            var xs = new XmlSerializer(typeof(CeilingColorRamp));
            using (var w = new StreamWriter(path))
                xs.Serialize(w, this);
        }

        public static CeilingColorRamp? LoadFrom(string path)
        {
            try
            {
                var xs = new XmlSerializer(typeof(CeilingColorRamp));
                using (var r = new StreamReader(path))
                    return xs.Deserialize(r) as CeilingColorRamp;
            }
            catch { return null; }
        }
    }
}
