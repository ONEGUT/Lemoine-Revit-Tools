using System.Collections.Generic;
using System.Xml.Serialization;

namespace LemoineTools.Tools.Testing
{
    /// <summary>
    /// Represents a named issue pack — an ordered list of sheet numbers
    /// grouped under one package name (e.g. "For Construction", "Planning Submission").
    /// </summary>
    [XmlRoot("SheetPackLayout")]
    public class SheetPackLayout
    {
        /// <summary>Display name for this pack, e.g. "For Construction".</summary>
        [XmlAttribute]
        public string PackName { get; set; } = "New Pack";

        /// <summary>Purpose text written to the IssuePurpose sheet parameter (if it exists).</summary>
        [XmlAttribute]
        public string IssuePurpose { get; set; } = "";

        /// <summary>Revision code written to the Revision sheet parameter (if it exists).</summary>
        [XmlAttribute]
        public string RevisionCode { get; set; } = "";

        /// <summary>
        /// Ordered list of sheet numbers to include in this pack.
        /// Order determines print/export sequence.
        /// </summary>
        [XmlArray("Sheets")]
        [XmlArrayItem("Sheet")]
        public List<string> SheetNumbers { get; set; } = new List<string>();

        public SheetPackLayout() { }

        public SheetPackLayout(string packName)
        {
            PackName = packName;
        }

        /// <summary>Returns a deep copy of this layout.</summary>
        public SheetPackLayout Clone()
        {
            return new SheetPackLayout
            {
                PackName     = PackName,
                IssuePurpose = IssuePurpose,
                RevisionCode = RevisionCode,
                SheetNumbers = new List<string>(SheetNumbers),
            };
        }
    }
}
