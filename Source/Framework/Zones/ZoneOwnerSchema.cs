using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace LemoineTools.Framework.Zones
{
    // =========================================================================
    // ZoneOwnerSchema — ownership stamp on every scope box a zone creates.
    //
    // Why stamp rather than keep a list: the document becomes authoritative.
    // Lookup runs document → library, so a box the user deletes simply stops
    // appearing, with no orphan sweep and nothing to reconcile. A list of names
    // held anywhere else would go stale the moment someone renames a box in the
    // Project Browser, and a list held in %AppData% would be a per-user file
    // describing a per-document fact — the mistake that once let one project's
    // run delete another project's elements.
    //
    // The stamp records the OWNING AREA'S GUID, not its name. A name comparison
    // would let a rename orphan the box, or worse, let two users of a shared
    // model each claim the other's boxes.
    //
    // A stamp is only ever written for a box this tool CREATED. An adopted box —
    // one the user drew and the zone merely points at — is deliberately left
    // unstamped, so the tool never claims ownership of, or deletes, something it
    // did not make.
    //
    // The GUID is HARDCODED forever — regenerating it orphans every stamp.
    // =========================================================================
    public static class ZoneOwnerSchema
    {
        /// <summary>Stable, constant schema GUID. NEVER regenerate.</summary>
        public static readonly Guid SchemaGuid = new Guid("3F82C6A1-9D40-4B7E-B215-8E6C0A5D71F4");

        public const int CurrentVersion = 1;

        private const string SchemaName   = "LemoineZoneOwner";
        private const string FieldVersion = "Version";
        private const string FieldAreaId  = "AreaId";
        private const string FieldLevelId = "LevelId";

        // NOTE: SetVendorId is deliberately not called, matching every other schema in this
        // repo. See CLAUDE.md — whether Revit requires one is unconfirmed on Windows, and if
        // it does then every stamp in this plugin is already failing silently.
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
                sb.AddSimpleField(FieldAreaId,  typeof(string));
                sb.AddSimpleField(FieldLevelId, typeof(string));
                return sb.Finish();
            }
            catch (Exception ex)
            {
                // A null schema disables ownership tracking. Callers must read that as
                // "own nothing", never "own everything" — deleting boxes we cannot prove
                // we created would be destructive.
                DiagnosticsLog.Error("ZoneOwnerSchema: create schema", ex);
                return null;
            }
        }

        /// <summary>
        /// Stamps a scope box this tool created. Must run inside an open transaction, and
        /// inside the run's EXISTING transaction rather than one of its own.
        ///
        /// Returns false (and logs) on failure — the box still exists and works, it simply
        /// will not be recognised as ours next time.
        /// </summary>
        public static bool Stamp(Element? box, string? areaId, string? levelId)
        {
            if (box == null) return false;
            var schema = GetOrCreate();
            if (schema == null) return false;
            try
            {
                var entity = new Entity(schema);
                entity.Set(FieldVersion, CurrentVersion);
                entity.Set(FieldAreaId,  areaId  ?? "");
                entity.Set(FieldLevelId, levelId ?? "");
                box.SetEntity(entity);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error($"ZoneOwnerSchema: stamp scope box '{box.Name}'", ex);
                return false;
            }
        }

        /// <summary>One stamped scope box and the zone it belongs to.</summary>
        public sealed class OwnerRecord
        {
            public ElementId ElementId = ElementId.InvalidElementId;
            public string    Name      = "";
            public string    AreaId    = "";
            /// <summary>Empty when the box serves the area on every level.</summary>
            public string    LevelId   = "";
        }

        /// <summary>Reads the stamp off one element, or null when unstamped.</summary>
        public static OwnerRecord? TryRead(Element? el)
        {
            if (el == null) return null;
            var schema = GetOrCreate();
            if (schema == null) return null;
            try
            {
                var entity = el.GetEntity(schema);
                if (entity == null || !entity.IsValid()) return null;
                return new OwnerRecord
                {
                    ElementId = el.Id,
                    Name      = el.Name ?? "",
                    AreaId    = entity.Get<string>(FieldAreaId)  ?? "",
                    LevelId   = entity.Get<string>(FieldLevelId) ?? "",
                };
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"ZoneOwnerSchema: read stamp from element {el.Id}", ex);
                return null;
            }
        }

        /// <summary>
        /// Every stamped scope box in the document, in one quick-filtered pass. Read-only, no
        /// transaction. Returns an empty list (never null) when the schema is unavailable.
        /// </summary>
        public static List<OwnerRecord> ReadAll(Document? doc)
        {
            var list = new List<OwnerRecord>();
            if (doc == null) return list;
            var schema = GetOrCreate();
            if (schema == null) return list;

            try
            {
                foreach (var el in new FilteredElementCollector(doc)
                             .OfCategory(BuiltInCategory.OST_VolumeOfInterest)
                             .WhereElementIsNotElementType()
                             .WherePasses(new ExtensibleStorageFilter(SchemaGuid)))
                {
                    var rec = TryRead(el);
                    if (rec != null) list.Add(rec);
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneOwnerSchema: read all stamps", ex);
            }
            return list;
        }
    }
}
