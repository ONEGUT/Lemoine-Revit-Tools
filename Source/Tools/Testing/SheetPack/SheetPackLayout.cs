using System.Collections.Generic;
using System.Xml.Serialization;

namespace LemoineTools.Tools.Testing
{
    /// <summary>
    /// A named pack — an ordered list of sheet numbers exported together as one combined PDF.
    /// </summary>
    [XmlRoot("SheetPackLayout")]
    public class SheetPackLayout
    {
        [XmlAttribute]
        public string PackName { get; set; } = "New Pack";

        [XmlArray("Sheets")]
        [XmlArrayItem("Sheet")]
        public List<string> SheetNumbers { get; set; } = new List<string>();

        public SheetPackLayout() { }

        public SheetPackLayout(string packName)
        {
            PackName = packName;
        }

        public SheetPackLayout Clone()
        {
            return new SheetPackLayout
            {
                PackName     = PackName,
                SheetNumbers = new List<string>(SheetNumbers),
            };
        }
    }
}
