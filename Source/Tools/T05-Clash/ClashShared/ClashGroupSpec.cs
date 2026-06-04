using System.Collections.Generic;

namespace LemoineTools.Tools.Testing
{
    /// <summary>
    /// Defines how one clash group (Group 1 or Group 2) selects its elements.
    /// Mode is one of "Rules" | "Categories" | "Elements".
    /// </summary>
    public sealed class ClashGroupSpec
    {
        public string Mode { get; set; } = "Rules";   // "Rules" | "Categories" | "Elements"

        /// <summary>AutoFilters persist keys ("{tradeId}::{ruleId}") — used in Rules mode.</summary>
        public List<string> RuleKeys { get; set; } = new List<string>();

        /// <summary>OST_* BuiltInCategory strings — used in Categories mode.</summary>
        public List<string> Categories { get; set; } = new List<string>();

        /// <summary>Directly-picked element ids — used in Elements mode (parallel to ElemLinkIds).</summary>
        public List<long> ElemIds { get; set; } = new List<long>();

        /// <summary>Link-instance ids parallel to ElemIds (0 = host document).</summary>
        public List<long> ElemLinkIds { get; set; } = new List<long>();

        /// <summary>
        /// Link-instance ids of the documents this group scans (0 = host).
        /// Empty = scan every available document. Used in Rules and Categories modes.
        /// </summary>
        public List<long> SourceLinkIds { get; set; } = new List<long>();
    }

    /// <summary>One selectable source document for the per-group source picker.</summary>
    public sealed class ClashDocInfo
    {
        public string Name       { get; set; } = "";
        public long   LinkInstId { get; set; }   // 0 = host document
    }
}
