using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace LemoineTools.Framework.Zones
{
    // =========================================================================
    // ZoneStore — the zone library inside the .rvt.
    //
    // WHY THIS IS A SEPARATE SCHEMA rather than a fourth field on
    // ProjectLibraryStore's:
    //
    //   An extensible-storage schema's field list is fixed the moment its GUID is
    //   registered in a Revit session, and a document that already carries the
    //   filter/legend/clash libraries registers that 3-field schema as soon as it
    //   is read. Adding a "Zones" field to it would mean Schema.Lookup returns the
    //   OLD 3-field definition, every entity.Set("Zones", …) throws, the whole
    //   Write fails, the transaction rolls back — and saving ANY of the three
    //   existing libraries would silently stop working on every project that has
    //   ever used them.
    //
    //   A separate GUID and a separate DataStorage holder cannot regress anything
    //   that already exists, and needs no migration.
    //
    // Threading:
    //   Read  — main thread, no transaction. Done at command launch.
    //   Write — main thread, INSIDE a transaction. Done from an ExternalEvent.
    //
    // The GUID is HARDCODED forever — regenerating it orphans every project's zones.
    // =========================================================================
    public static class ZoneStore
    {
        /// <summary>Stable, constant schema GUID. NEVER regenerate.</summary>
        public static readonly Guid SchemaGuid = new Guid("C6E1A94D-73B2-4F08-8A5E-2B7D40F91C63");

        public const int CurrentVersion = 1;

        private const string SchemaName  = "LemoineProjectZones";
        private const string ElementName = "Lemoine Project Zones";
        private const string FieldVersion = "Version";
        private const string FieldZones   = "Zones";

        // NOTE: SetVendorId is deliberately not called, matching every other schema in this
        // repo. Whether Revit requires it is still unconfirmed on Windows — if it does, every
        // stamp and store in this plugin is already failing silently, and this one with them.
        public static Schema? GetOrCreate()
        {
            try
            {
                var existing = Schema.Lookup(SchemaGuid);
                if (existing != null) return existing;

                var sb = new SchemaBuilder(SchemaGuid);
                sb.SetSchemaName(SchemaName);
                sb.SetReadAccessLevel(AccessLevel.Public);
                sb.SetWriteAccessLevel(AccessLevel.Public);
                sb.AddSimpleField(FieldVersion, typeof(int));
                sb.AddSimpleField(FieldZones,   typeof(string));
                return sb.Finish();
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneStore: create schema", ex);
                return null;
            }
        }

        /// <summary>
        /// Reads the zone library XML out of the document. Read-only, no transaction — safe at
        /// command launch on the Revit main thread. Returns "" when the document has never
        /// carried zones, which the loader treats as an empty library.
        /// </summary>
        public static string Read(Document? doc)
        {
            if (doc == null) return "";

            var schema = GetOrCreate();
            if (schema == null) return "";

            try
            {
                var holder = FindHolder(doc);
                if (holder == null)
                {
                    DiagnosticsLog.Info("ZoneStore", "No zones stored in this document yet.");
                    return "";
                }

                var entity = holder.GetEntity(schema);
                if (entity == null || !entity.IsValid()) return "";

                string xml = entity.Get<string>(FieldZones) ?? "";
                DiagnosticsLog.Info("ZoneStore", $"Read zone library ({xml.Length} chars).");
                return xml;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneStore: read", ex);
                return "";
            }
        }

        /// <summary>
        /// Writes the zone library into the document. MUST run inside an open transaction on
        /// the Revit main thread. Returns true when the write succeeded.
        /// </summary>
        public static bool Write(Document? doc, string? xml)
        {
            if (doc == null) return false;

            var schema = GetOrCreate();
            if (schema == null) return false;

            try
            {
                var holder = FindHolder(doc) ?? CreateHolder(doc);
                if (holder == null) return false;

                var entity = holder.GetEntity(schema);
                if (entity == null || !entity.IsValid()) entity = new Entity(schema);

                entity.Set(FieldVersion, CurrentVersion);
                entity.Set(FieldZones,   xml ?? "");
                holder.SetEntity(entity);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneStore: write", ex);
                return false;
            }
        }

        private static Element? FindHolder(Document doc)
        {
            try
            {
                return new FilteredElementCollector(doc)
                    .OfClass(typeof(DataStorage))
                    .WherePasses(new ExtensibleStorageFilter(SchemaGuid))
                    .FirstElement();
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("ZoneStore: find holder", ex);
                return null;
            }
        }

        /// <summary>Creates the holder. Requires an open transaction.</summary>
        private static Element? CreateHolder(Document doc)
        {
            try
            {
                var ds = DataStorage.Create(doc);
                // Named so it is identifiable in a worksharing "who owns this element" prompt.
                try { ds.Name = ElementName; }
                catch (Exception ex) { DiagnosticsLog.Swallowed("ZoneStore: name holder", ex); }
                return ds;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneStore: create holder", ex);
                return null;
            }
        }

        /// <summary>
        /// True when this user can currently write zones. In a workshared model the holder is
        /// owned by whoever last edited it until they synchronise; writing then fails, so the
        /// caller reports it rather than appearing to save.
        /// </summary>
        public static bool CanWrite(Document? doc, out string? reason)
        {
            reason = null;
            if (doc == null) { reason = "no document"; return false; }
            if (doc.IsReadOnly) { reason = "the document is read-only"; return false; }
            if (doc.IsFamilyDocument) { reason = "family documents have no zones"; return false; }

            try
            {
                if (!doc.IsWorkshared) return true;
                var holder = FindHolder(doc);
                if (holder == null) return true;   // not created yet; creation is fine

                var status = WorksharingUtils.GetCheckoutStatus(doc, holder.Id);
                if (status == CheckoutStatus.OwnedByOtherUser)
                {
                    reason = "another user currently owns the project zones element — " +
                             "they need to synchronise before your changes can be saved";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("ZoneStore: checkout probe", ex);
                return true;   // advisory only; let the write attempt report the real failure
            }
        }
    }
}
