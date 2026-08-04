using System;
using System.Collections.Generic;
using System.Xml.Serialization;
using LemoineTools.Framework;

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

        // ── Project-scoped selections ────────────────────────────────────────
        //
        // Everything below names elements, links or worksets INSIDE one specific model,
        // so it is stored per document rather than once for the whole library. A clash
        // definition is meant to be reusable; its picks are not. Previously an ElementId
        // picked in project A was replayed in project B, where it resolved either to
        // nothing or — worse — to an unrelated element carrying the same number.
        //
        // The properties keep their names and shapes, so the editor and the scan engine
        // are unchanged; only the storage behind them is keyed by document.

        [XmlArray("DocScopes"), XmlArrayItem("Doc")]
        public List<ClashGroupDocScope> DocScopes { get; set; } = new List<ClashGroupDocScope>();

        /// <summary>Bucket for the active document, created on first touch.</summary>
        private ClashGroupDocScope Scope()
        {
            // A hand-edited or truncated settings file can deserialize this as null; every
            // accessor below depends on it, so repair rather than throw out of a property.
            if (DocScopes == null) DocScopes = new List<ClashGroupDocScope>();

            string k = DocumentKey.Current ?? "";
            foreach (var d in DocScopes)
                if (d != null && string.Equals(d.Key, k, StringComparison.OrdinalIgnoreCase))
                {
                    d.Touched = DateTime.UtcNow.Ticks;
                    return d;
                }

            var made = new ClashGroupDocScope { Key = k, Touched = DateTime.UtcNow.Ticks };
            DocScopes.Add(made);

            while (DocScopes.Count > DocScoped.MaxDocuments)
            {
                int oldest = 0;
                for (int i = 1; i < DocScopes.Count; i++)
                    if (DocScopes[i].Touched < DocScopes[oldest].Touched) oldest = i;
                DocScopes.RemoveAt(oldest);
            }
            return made;
        }

        /// <summary>Directly-picked element ids — used in Elements mode (parallel to ElemLinkIds).</summary>
        [XmlIgnore]
        public List<long> ElemIds
        {
            get => Scope().ElemIds;
            set => Scope().ElemIds = value ?? new List<long>();
        }

        /// <summary>Link-instance ids parallel to ElemIds (0 = host document).</summary>
        [XmlIgnore]
        public List<long> ElemLinkIds
        {
            get => Scope().ElemLinkIds;
            set => Scope().ElemLinkIds = value ?? new List<long>();
        }

        /// <summary>
        /// Link-instance ids of the documents this group scans (0 = host).
        /// Used in Rules and Categories modes. When <see cref="SourcesExplicit"/> is false
        /// (the default, and every definition saved before the flag existed), an empty list
        /// means "scan every available document, including links added later".
        /// </summary>
        [XmlIgnore]
        public List<long> SourceLinkIds
        {
            get => Scope().SourceLinkIds;
            set => Scope().SourceLinkIds = value ?? new List<long>();
        }

        /// <summary>
        /// True when <see cref="SourceLinkIds"/> is the authoritative selection — including an
        /// EMPTY list meaning "scan nothing". Without this flag, unchecking every source
        /// document in the editor saved an empty list, which the scanner read as "scan ALL
        /// documents" — the exact opposite of what the UI showed. The editor writes
        /// false + empty when every document is checked (preserving the future-links-included
        /// semantics), true otherwise.
        ///
        /// Document-scoped along with SourceLinkIds: it answers "did the user tick every link
        /// in THIS model?", so keeping it library-wide would let one project's link count
        /// decide another project's scan semantics. A model with no bucket yet reports false,
        /// which is the "scan everything available" default rather than "scan nothing".
        /// </summary>
        [XmlIgnore]
        public bool SourcesExplicit
        {
            get => Scope().SourcesExplicit;
            set => Scope().SourcesExplicit = value;
        }

        /// <summary>
        /// Per-source-document workset exclusions (one entry per document that has any
        /// unchecked workset). Stored as EXCLUSIONS so the default — an empty list — means
        /// "include every workset", leaving existing saved definitions unchanged. Applies to
        /// Rules and Categories modes (mirrors <see cref="SourceLinkIds"/>); Elements mode picks
        /// exact elements and ignores it.
        ///
        /// Document-scoped: it carries link-instance ids AND workset ids, both of which are
        /// per-model integers. Replayed in another project they excluded whatever worksets
        /// happened to hold those numbers — and an over-broad exclusion is invisible, because
        /// the elements simply never appear in the scan.
        /// </summary>
        [XmlIgnore]
        public List<ClashWorksetFilter> WorksetFilters
        {
            get => Scope().WorksetFilters;
            set => Scope().WorksetFilters = value ?? new List<ClashWorksetFilter>();
        }
    }

    /// <summary>
    /// One document's picks for a clash group. Public for XmlSerializer — a non-public
    /// root type throws at serializer construction and, because that call sits in a
    /// try/catch, fails silently and strands every setting on its default (see CLAUDE.md).
    /// </summary>
    public sealed class ClashGroupDocScope
    {
        /// <summary>Document identity from <see cref="DocumentKey"/>. Empty = no-document slot.</summary>
        [XmlAttribute] public string Key { get; set; } = "";

        /// <summary>Ticks at last touch, for least-recently-used eviction.</summary>
        [XmlAttribute] public long Touched { get; set; }

        [XmlAttribute] public bool SourcesExplicit { get; set; }

        [XmlArray("ElemIds"), XmlArrayItem("Id")]
        public List<long> ElemIds { get; set; } = new List<long>();

        [XmlArray("ElemLinkIds"), XmlArrayItem("Id")]
        public List<long> ElemLinkIds { get; set; } = new List<long>();

        [XmlArray("SourceLinkIds"), XmlArrayItem("Id")]
        public List<long> SourceLinkIds { get; set; } = new List<long>();

        [XmlArray("WorksetFilters"), XmlArrayItem("Filter")]
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
