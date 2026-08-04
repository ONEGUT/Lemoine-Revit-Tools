using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using LemoineTools.Framework;

namespace LemoineTools.Tools.FiltersLegends.LegendCreator
{
    // =========================================================================
    // LegendLinkSchema — binds a Revit legend view back to the LegendEntry that
    // generated it, by stamping the view itself.
    //
    // Replaces LegendEntry.RevitViewId, which stored a raw ElementId in a
    // machine-wide settings file. An ElementId means nothing outside its own
    // document, so opening a second project left the entry claiming a legend
    // that either did not exist or — worse — was an unrelated element that
    // happened to share the number. The window read "already created" and
    // offered Update, targeting that id.
    //
    // Stamping inverts the lookup: the DOCUMENT says which of its legends belong
    // to which entry, so a project that has never been touched by this tool
    // simply reports nothing and the entry correctly offers Create.
    //
    // The GUID is HARDCODED forever — regenerating it would orphan every stamp.
    // =========================================================================
    public static class LegendLinkSchema
    {
        /// <summary>Stable, constant schema GUID. NEVER regenerate.</summary>
        public static readonly Guid SchemaGuid = new Guid("2F6D9C48-31A5-4E7B-8C10-63B4A9E5D072");

        public const int CurrentVersion = 1;

        private const string SchemaName   = "LemoineLegendLink";
        private const string FieldVersion = "Version";
        private const string FieldEntryId = "EntryId";

        // Mirrors the five long-standing Lemoine schemas exactly (GUID, name, access
        // levels, simple fields). SetVendorId is deliberately not called — see
        // AutoFilterOwnerSchema for why.
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
                sb.AddSimpleField(FieldEntryId, typeof(string));
                return sb.Finish();
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("LegendLinkSchema: create schema", ex);
                return null;
            }
        }

        /// <summary>
        /// Binds a created legend view to its entry. Must run inside an open transaction.
        /// A failure is logged, not swallowed: the legend still exists, but the next
        /// session would offer Create rather than Update for it.
        /// </summary>
        public static bool Stamp(Element legendView, string? entryId)
        {
            if (legendView == null || string.IsNullOrEmpty(entryId)) return false;
            var schema = GetOrCreate();
            if (schema == null) return false;
            try
            {
                var entity = new Entity(schema);
                entity.Set(FieldVersion, CurrentVersion);
                entity.Set(FieldEntryId, entryId);
                legendView.SetEntity(entity);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error($"LegendLinkSchema: stamp legend view {legendView.Id}", ex);
                return false;
            }
        }

        /// <summary>
        /// entryId → legend view ElementId value, for every stamped legend in the document.
        /// Read-only; no transaction required. Safe to call on the Revit main thread at
        /// command launch. Returns an empty map (never null) when none are found.
        ///
        /// A legend that was stamped and later deleted simply does not appear, which is
        /// exactly the reconciliation the old stored-id design lacked.
        /// </summary>
        public static Dictionary<string, long> ReadLinks(Document doc)
        {
            var map = new Dictionary<string, long>(StringComparer.Ordinal);
            if (doc == null) return map;
            var schema = GetOrCreate();
            if (schema == null) return map;

            IEnumerable<Element> stamped;
            try
            {
                stamped = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .WherePasses(new ExtensibleStorageFilter(SchemaGuid))
                    .ToElements();
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("LegendLinkSchema: collect stamped legends", ex);
                return map;
            }

            foreach (var el in stamped)
            {
                try
                {
                    if (!(el is View v) || v.ViewType != ViewType.Legend) continue;
                    var entity = el.GetEntity(schema);
                    if (entity == null || !entity.IsValid()) continue;
                    string entryId = entity.Get<string>(FieldEntryId) ?? "";
                    if (entryId.Length > 0) map[entryId] = el.Id.Value;
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed($"LegendLinkSchema: read stamp from {el.Id}", ex);
                }
            }
            return map;
        }
    }
}
