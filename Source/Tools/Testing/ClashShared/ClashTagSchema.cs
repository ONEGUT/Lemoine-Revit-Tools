using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Testing
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
        /// Stamps an element as a Lemoine clash marker. Must be called inside an open
        /// transaction. Failures are logged (not thrown) so one un-taggable element never
        /// aborts a marking run.
        /// </summary>
        public static void StampTag(Element element)
        {
            if (element == null) return;
            try
            {
                var schema = GetOrCreateTagSchema();
                var field  = ResolveTagField(schema);
                if (field == null) { LemoineLog.Error("ClashTagSchema: stamp clash tag", new InvalidOperationException("schema has no usable string field")); return; }
                var entity = new Entity(schema);
                entity.Set(field, TagValue);
                element.SetEntity(entity);
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashTagSchema: stamp clash tag", ex); }
        }

        /// <summary>
        /// True if the element carries our clash tag. Read-only — safe outside a transaction.
        /// Returns false when the schema has never been created in this session.
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
                return entity.Get<string>(field) == TagValue;
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashTagSchema: read clash tag", ex); return false; }
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
