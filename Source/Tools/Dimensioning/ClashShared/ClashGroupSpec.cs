using System.Collections.Generic;
using System.Xml.Serialization;

namespace LemoineTools.Tools.Dimensioning
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

        /// <summary>
        /// Per-source-document workset exclusions (one entry per document that has any
        /// unchecked workset). Stored as EXCLUSIONS so the default — an empty list — means
        /// "include every workset", leaving existing saved definitions unchanged. Applies to
        /// Rules and Categories modes (mirrors <see cref="SourceLinkIds"/>); Elements mode picks
        /// exact elements and ignores it.
        /// </summary>
        public List<ClashWorksetFilter> WorksetFilters { get; set; } = new List<ClashWorksetFilter>();
    }

    /// <summary>Unchecked (excluded) worksets for one source document of a clash group.</summary>
    public sealed class ClashWorksetFilter
    {
        /// <summary>Link-instance id of the document (0 = host).</summary>
        [XmlAttribute] public long LinkInstId { get; set; }

        /// <summary>Workset ids (within that document) excluded from the scan.</summary>
        public List<int> ExcludedWorksetIds { get; set; } = new List<int>();
    }

    /// <summary>One selectable source document for the per-group source picker.</summary>
    public sealed class ClashDocInfo
    {
        public string Name       { get; set; } = "";
        public long   LinkInstId { get; set; }   // 0 = host document

        /// <summary>User worksets in this document (empty when it is not workshared).
        /// UI-side only — drives the per-document workset checklist; not persisted in the definition.</summary>
        public List<ClashWorksetInfo> Worksets { get; set; } = new List<ClashWorksetInfo>();
    }

    /// <summary>One user workset of a source document (UI-side; not persisted).</summary>
    public sealed class ClashWorksetInfo
    {
        public int    Id   { get; set; }
        public string Name { get; set; } = "";
    }
}
