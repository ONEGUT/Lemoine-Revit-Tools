using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace LemoineTools.Framework
{
    // =========================================================================
    // DocScopedValue / DocScoped — one settings value held per document.
    //
    // XmlSerializer cannot serialize a Dictionary, so a per-document value is a
    // plain list of key/value pairs with helpers over it. Public type: an
    // internal one makes XmlSerializer throw "only public types can be
    // processed" at construction, and because that call sits in a try/catch it
    // fails SILENTLY, leaving every setting stuck on defaults (see CLAUDE.md).
    // =========================================================================

    /// <summary>One document's value for a setting. Public for XmlSerializer.</summary>
    public sealed class DocScopedValue
    {
        /// <summary>Document identity from <see cref="DocumentKey"/>. Empty = the
        /// no-document fallback slot.</summary>
        [XmlAttribute] public string Key { get; set; } = "";

        [XmlAttribute] public string Value { get; set; } = "";

        /// <summary>Ticks at last write, used to evict the least-recently-used entry.</summary>
        [XmlAttribute] public long Touched { get; set; }
    }

    /// <summary>Lookup/update helpers over a <see cref="DocScopedValue"/> list.</summary>
    public static class DocScoped
    {
        /// <summary>Documents remembered per setting before the oldest is evicted.</summary>
        public const int MaxDocuments = 50;

        /// <summary>
        /// This document's value, or empty when it has none. A null key (no document open,
        /// or an unsaved one) reads the shared fallback slot, which is what the setting
        /// behaved like before it became document-scoped.
        /// </summary>
        public static string Get(List<DocScopedValue>? entries, string? docKey)
        {
            if (entries == null) return "";
            string k = docKey ?? "";
            foreach (var e in entries)
                if (e != null && string.Equals(e.Key, k, StringComparison.OrdinalIgnoreCase))
                    return e.Value ?? "";
            return "";
        }

        /// <summary>
        /// Records this document's value, evicting the least-recently-used document once
        /// the cap is reached so the settings file stays bounded.
        /// </summary>
        public static void Set(List<DocScopedValue> entries, string? docKey, string? value)
        {
            if (entries == null) return;
            string k = docKey ?? "";
            string v = value ?? "";

            foreach (var e in entries)
            {
                if (e == null || !string.Equals(e.Key, k, StringComparison.OrdinalIgnoreCase)) continue;
                e.Value   = v;
                e.Touched = DateTime.UtcNow.Ticks;
                return;
            }

            entries.Add(new DocScopedValue { Key = k, Value = v, Touched = DateTime.UtcNow.Ticks });

            while (entries.Count > MaxDocuments)
            {
                int oldest = 0;
                for (int i = 1; i < entries.Count; i++)
                    if (entries[i].Touched < entries[oldest].Touched) oldest = i;
                entries.RemoveAt(oldest);
            }
        }
    }
}
