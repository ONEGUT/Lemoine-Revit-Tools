using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Dimensioning
{
    /// <summary>
    /// Single source of truth for tagging clash markers (filled regions + cross lines)
    /// so the marking pass and the discovery / clear pass agree on what "ours" means.
    ///
    /// Markers are tagged with an Extensible Storage entity carrying <see cref="TagValue"/>.
    /// Both the Clash Finder's marking engine and its dimension/clear passes use these
    /// helpers, so the schema GUID + tag value are defined in exactly one place.
    /// </summary>
    public static class ClashTagSchema
    {
        /// <summary>Stable schema GUID shared by every tool that reads or writes clash tags.</summary>
        public static readonly Guid SchemaGuid = new Guid("7D1F3A52-9C84-4F6B-BF21-2E6A8C4D10B9");

        /// <summary>Tag value stored in the entity's single field.</summary>
        public const string TagValue = "LemoineCD";

        private const string SchemaName = "LemoineClashTag";
        private const string FieldName  = "Tag";

        /// <summary>
        /// Returns the clash-tag schema, creating it in this Revit session if it does not
        /// yet exist. Safe to call repeatedly; <see cref="Schema.Lookup"/> short-circuits
        /// after the first build.
        /// </summary>
        public static Schema GetOrCreateTagSchema()
        {
            var existing = Schema.Lookup(SchemaGuid);
            if (existing != null) return existing;

            var sb = new SchemaBuilder(SchemaGuid);
            sb.SetSchemaName(SchemaName);
            sb.SetReadAccessLevel(AccessLevel.Public);
            sb.SetWriteAccessLevel(AccessLevel.Public);
            sb.AddSimpleField(FieldName, typeof(string));
            return sb.Finish();
        }

        /// <summary>
        /// Stamps an element as a Lemoine clash marker, optionally with a per-clash
        /// <paramref name="group"/> id (so every line + region of one clash can be re-grouped later)
        /// and the exact Group 2 element it clashed (so the dimension pass can target that element's
        /// edge). Stored as pipe-delimited segments: <c>LemoineCD|&lt;group&gt;|&lt;linkId&gt;:&lt;elemId&gt;</c>
        /// (later segments omitted when absent; <c>linkId</c> 0 = host). Must be called inside an open
        /// transaction. Failures are logged (not thrown) so one un-taggable element never aborts a run.
        /// </summary>
        public static void StampTag(
            Element element, string? group = null,
            ElementId? targetLinkId = null, ElementId? targetElemId = null)
        {
            if (element == null) return;
            try
            {
                var schema = GetOrCreateTagSchema();
                var field  = ResolveTagField(schema);
                if (field == null) { LemoineLog.Error("ClashTagSchema: stamp clash tag", new InvalidOperationException("schema has no usable string field")); return; }

                string g = group ?? "";
                bool hasTarget = targetElemId != null && targetElemId != ElementId.InvalidElementId;
                string val = TagValue;
                if (!string.IsNullOrEmpty(g) || hasTarget) val += "|" + g;       // keep segment positions
                if (hasTarget)
                {
                    long linkRaw = (targetLinkId != null && targetLinkId != ElementId.InvalidElementId)
                        ? targetLinkId.Value : 0;
                    val += "|" + linkRaw + ":" + targetElemId!.Value;
                }

                var entity = new Entity(schema);
                entity.Set(field, val);
                element.SetEntity(entity);
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashTagSchema: stamp clash tag", ex); }
        }

        /// <summary>
        /// True if the element carries our clash tag (grouped or not). Read-only — safe outside a
        /// transaction. Returns false when the schema has never been created in this session.
        /// </summary>
        public static bool IsOurs(Element element)
        {
            if (element == null) return false;
            try
            {
                // Register (or rebuild) the schema so entities written in a previous Revit
                // session are still readable — Schema.Lookup alone returns null until the
                // schema is registered this session. Registration needs no transaction.
                var schema = GetOrCreateTagSchema();
                if (schema == null) return false;
                var field = ResolveTagField(schema);
                if (field == null) return false;
                var entity = element.GetEntity(schema);
                if (entity == null || !entity.IsValid()) return false;
                var v = entity.Get<string>(field);
                return v == TagValue || (v != null && v.StartsWith(TagValue + "|", StringComparison.Ordinal));
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashTagSchema: read clash tag", ex); return false; }
        }

        /// <summary>
        /// Per-clash group id this marker was stamped with, or "" when un-grouped (legacy markers,
        /// or markers from an older run). Read-only — safe outside a transaction.
        /// </summary>
        public static string ReadGroup(Element element)
        {
            if (element == null) return "";
            try
            {
                var schema = GetOrCreateTagSchema();
                var field  = ResolveTagField(schema);
                if (schema == null || field == null) return "";
                var entity = element.GetEntity(schema);
                if (entity == null || !entity.IsValid()) return "";
                var v = entity.Get<string>(field) ?? "";
                // Segments: [0]=TagValue, [1]=group, [2]=target. Take only the group segment so an
                // appended target segment never bleeds into the group key.
                var parts = v.Split('|');
                return parts.Length >= 2 ? parts[1] : "";
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashTagSchema: read clash group", ex); return ""; }
        }

        /// <summary>
        /// The exact Group 2 element this marker clashed, stamped at marking, or null when the marker
        /// carries no target (legacy markers, or non-clash tags). Returns the owning link instance id
        /// (<see cref="ElementId.InvalidElementId"/> for a host element) and the element id. Read-only.
        /// </summary>
        public static (ElementId linkInstanceId, ElementId elementId)? ReadTarget(Element element)
        {
            if (element == null) return null;
            try
            {
                var schema = GetOrCreateTagSchema();
                var field  = ResolveTagField(schema);
                if (schema == null || field == null) return null;
                var entity = element.GetEntity(schema);
                if (entity == null || !entity.IsValid()) return null;
                var v = entity.Get<string>(field) ?? "";
                var parts = v.Split('|');
                if (parts.Length < 3) return null;                       // no target segment
                int colon = parts[2].IndexOf(':');
                if (colon < 0) return null;
                if (!long.TryParse(parts[2].Substring(0, colon), out long linkRaw)) return null;
                if (!long.TryParse(parts[2].Substring(colon + 1), out long elemRaw) || elemRaw <= 0) return null;
                ElementId linkId = linkRaw > 0 ? new ElementId(linkRaw) : ElementId.InvalidElementId;
                return (linkId, new ElementId(elemRaw));
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashTagSchema: read clash target", ex); return null; }
        }

        /// <summary>
        /// Returns the string field to read/write the tag value through. Prefers the field named
        /// <see cref="FieldName"/>, but falls back to the schema's first string field — older builds
        /// stored this schema (same GUID) under a different field name (e.g. lowercase "tag"), and
        /// the stored definition wins on <see cref="Schema.Lookup"/>. Without this fallback, Set/Get
        /// against the hardcoded name throw and are swallowed, so tagging silently fails on any
        /// document that carries the legacy schema.
        /// </summary>
        private static Field? ResolveTagField(Schema? schema)
        {
            if (schema == null) return null;
            var named = schema.GetField(FieldName);
            if (named != null && named.ValueType == typeof(string)) return named;
            foreach (var f in schema.ListFields())
                if (f.ValueType == typeof(string)) return f;
            return null;
        }

        /// <summary>Alias for <see cref="IsOurs"/> — reads more naturally at some call sites.</summary>
        public static bool HasTag(Element element) => IsOurs(element);
    }
}
