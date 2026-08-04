using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using LemoineTools.Framework;

namespace LemoineTools.Tools.BulkExport
{
    /// <summary>
    /// Persists Bulk Export's set layout inside the document via Extensible Storage, so the sets
    /// travel with the model and the whole team inherits them — a machine-local settings file
    /// could not do that.
    ///
    /// Shape: one <see cref="DataStorage"/> element carrying a single entity whose only field
    /// holds the whole layout serialized to XML. Extensible Storage's field types cannot express
    /// a list of sets each holding an ordered list of members; serializing to one string field
    /// sidesteps that entirely and makes a format change a matter of adding properties to a DTO.
    ///
    /// <see cref="Read"/> needs no transaction. <see cref="Write"/> MUST be called inside an open
    /// transaction — see <see cref="ExportSetStoreHandler"/>, which owns that.
    /// </summary>
    public static class ExportSetStore
    {
        /// <summary>Stable schema GUID. Never regenerate — it is the only handle on already-stored layouts.</summary>
        public static readonly Guid SchemaGuid = new Guid("B4E7C2A1-3D69-4E58-9A0F-7C51D8E3B26A");

        private const string SchemaName  = "LemoineBulkExportSets";
        private const string FieldLayout = "Layout";

        /// <summary>
        /// Returns the layout schema, creating it in this Revit session if it does not yet exist.
        /// Registration needs no transaction, and <see cref="Schema.Lookup"/> short-circuits after
        /// the first build. Must be called before any read too: Lookup returns null until the
        /// schema is registered this session, so entities written in a previous session are
        /// otherwise invisible.
        /// </summary>
        public static Schema GetOrCreateSchema()
        {
            var existing = Schema.Lookup(SchemaGuid);
            if (existing != null) return existing;

            var sb = new SchemaBuilder(SchemaGuid);
            sb.SetSchemaName(SchemaName);
            sb.SetReadAccessLevel(AccessLevel.Public);
            sb.SetWriteAccessLevel(AccessLevel.Public);
            sb.AddSimpleField(FieldLayout, typeof(string));
            return sb.Finish();
        }

        /// <summary>
        /// The string field to read/write the layout through. Prefers the field named
        /// <see cref="FieldLayout"/> and falls back to the schema's first string field — the
        /// stored schema definition wins on <see cref="Schema.Lookup"/>, so a field renamed
        /// between builds would otherwise make every Set/Get throw and persistence fail silently.
        /// (Same failure mode already documented on ClashTagSchema.ResolveTagField.)
        /// </summary>
        private static Field? ResolveLayoutField(Schema? schema)
        {
            if (schema == null) return null;
            var named = schema.GetField(FieldLayout);
            if (named != null && named.ValueType == typeof(string)) return named;
            foreach (var f in schema.ListFields())
                if (f.ValueType == typeof(string)) return f;
            return null;
        }

        /// <summary>The document's layout storage element, or null when none has been written yet.</summary>
        public static DataStorage? FindStorage(Document doc)
        {
            if (doc == null) return null;
            try
            {
                return new FilteredElementCollector(doc)
                    .OfClass(typeof(DataStorage))
                    .WherePasses(new ExtensibleStorageFilter(SchemaGuid))
                    .FirstOrDefault() as DataStorage;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("ExportSetStore: find layout storage", ex);
                return null;
            }
        }

        /// <summary>
        /// Reads the stored layout, or null when the document carries none. Read-only and safe
        /// outside a transaction. A corrupt or unreadable payload is logged and treated as
        /// "no layout" — it must never stop the tool from opening.
        /// </summary>
        public static ExportSetLayout? Read(Document doc)
        {
            if (doc == null) return null;
            try
            {
                var schema = GetOrCreateSchema();
                var field  = ResolveLayoutField(schema);
                if (field == null)
                {
                    DiagnosticsLog.Warn("ExportSetStore.Read", "layout schema has no usable string field");
                    return null;
                }

                var storage = FindStorage(doc);
                if (storage == null) return null;

                var entity = storage.GetEntity(schema);
                if (entity == null || !entity.IsValid()) return null;

                string xml = entity.Get<string>(field) ?? "";
                if (string.IsNullOrWhiteSpace(xml)) return null;

                var layout = Deserialize(xml);
                if (layout == null)
                {
                    DiagnosticsLog.Warn("ExportSetStore.Read",
                        $"stored layout did not deserialize ({xml.Length} chars) — treating as no layout");
                    return null;
                }
                if (layout.Version > ExportSetLayout.CurrentVersion)
                    DiagnosticsLog.Warn("ExportSetStore.Read",
                        $"stored layout version {layout.Version} is newer than this build " +
                        $"({ExportSetLayout.CurrentVersion}) — reading it anyway; unknown fields are dropped");
                return layout;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("ExportSetStore: read layout", ex);
                return null;
            }
        }

        /// <summary>
        /// Writes the layout into the document. **Must be called inside an open transaction.**
        /// Throws on failure rather than swallowing — the caller
        /// (<see cref="ExportSetStoreHandler"/>) owns reporting it to the user, and a save that
        /// silently did nothing is exactly the failure this tool must not have.
        /// </summary>
        public static void Write(Document doc, ExportSetLayout layout)
        {
            if (doc == null)    throw new ArgumentNullException(nameof(doc));
            if (layout == null) throw new ArgumentNullException(nameof(layout));

            var schema = GetOrCreateSchema();
            var field  = ResolveLayoutField(schema);
            if (field == null)
                throw new InvalidOperationException("Bulk Export layout schema has no usable string field.");

            layout.Version = ExportSetLayout.CurrentVersion;
            string xml = Serialize(layout);

            var storage = FindStorage(doc) ?? DataStorage.Create(doc);
            if (storage == null)
                throw new InvalidOperationException("Could not create the storage element for the set layout.");

            var entity = new Entity(schema);
            entity.Set(field, xml);
            storage.SetEntity(entity);
        }

        /// <summary>
        /// Deletes the stored layout element. Must be called inside an open transaction.
        /// Returns false when there was nothing to delete.
        /// </summary>
        public static bool Clear(Document doc)
        {
            var storage = FindStorage(doc);
            if (storage == null) return false;
            doc.Delete(storage.Id);
            return true;
        }

        /// <summary>
        /// Whether this document can carry a set layout at all. A read-only, linked or family
        /// document cannot — the tool stays fully usable in memory, it just cannot persist, and
        /// the reason is shown to the user rather than surfacing as a failed save.
        /// </summary>
        public static bool CanPersist(Document doc, out string reason)
        {
            reason = "";
            if (doc == null)             { reason = AppStrings.T("export.bulkExport.log.noDoc");        return false; }
            if (doc.IsFamilyDocument)    { reason = AppStrings.T("export.bulkExport.sets.noPersistFamily");   return false; }
            if (doc.IsLinked)            { reason = AppStrings.T("export.bulkExport.sets.noPersistLinked");   return false; }
            if (doc.IsReadOnly)          { reason = AppStrings.T("export.bulkExport.sets.noPersistReadOnly"); return false; }
            return true;
        }

        // ── Serialization ─────────────────────────────────────────────────────

        internal static string Serialize(ExportSetLayout layout)
        {
            var xs = new XmlSerializer(typeof(ExportSetLayout));
            var sb = new StringBuilder();
            // Omit the XML declaration and namespaces — every byte counts against whatever
            // ceiling an Extensible Storage string field has, and nothing outside this file
            // consumes the payload.
            var settings = new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false };
            var ns = new XmlSerializerNamespaces();
            ns.Add("", "");
            using (var writer = XmlWriter.Create(sb, settings))
                xs.Serialize(writer, layout, ns);
            return sb.ToString();
        }

        internal static ExportSetLayout? Deserialize(string xml)
        {
            try
            {
                var xs = new XmlSerializer(typeof(ExportSetLayout));
                using (var reader = new StringReader(xml))
                    return xs.Deserialize(reader) as ExportSetLayout;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("ExportSetStore: deserialize layout", ex);
                return null;
            }
        }
    }
}
