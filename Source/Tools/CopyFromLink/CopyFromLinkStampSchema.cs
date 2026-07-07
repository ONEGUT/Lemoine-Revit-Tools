using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using LemoineTools.Framework;

namespace LemoineTools.Tools.CopyFromLink
{
    /// <summary>
    /// Ownership + provenance stamp written onto every host element the Copy Elements from Link tool
    /// creates, so a later "only changed" re-run can reconcile the host against the link without any
    /// external database (same discipline as <see cref="CopyLinear.CopyLinearStampSchema"/>):
    ///
    ///   • source key present, hash matches → unchanged, skip
    ///   • source key present, hash differs → changed, delete old outputs and re-copy
    ///   • source key absent                → new source, copy
    ///   • stamped output whose source key is gone → source deleted, delete the orphaned outputs
    ///
    /// The GUID is HARDCODED forever — regenerating it would orphan every stamped element.
    /// </summary>
    public static class CopyFromLinkStampSchema
    {
        /// <summary>Stable, constant schema GUID. NEVER regenerate.</summary>
        public static readonly Guid SchemaGuid = new Guid("3C7A1E92-5D64-49B8-9F2A-8B14C6E0D573");

        public const int CurrentVersion = 1;

        private const string SchemaName   = "LemoineCopyFromLinkStamp";
        private const string FieldVersion = "Version";
        private const string FieldKey     = "SourceKey";
        private const string FieldHash    = "GeoHash";
        private const string FieldRunId   = "RunId";

        public static Schema GetOrCreate()
        {
            var existing = Schema.Lookup(SchemaGuid);
            if (existing != null) return existing;

            var sb = new SchemaBuilder(SchemaGuid);
            sb.SetSchemaName(SchemaName);
            sb.SetReadAccessLevel(AccessLevel.Public);
            sb.SetWriteAccessLevel(AccessLevel.Public);
            sb.AddSimpleField(FieldVersion, typeof(int));
            sb.AddSimpleField(FieldKey,     typeof(string));
            sb.AddSimpleField(FieldHash,    typeof(string));
            sb.AddSimpleField(FieldRunId,   typeof(string));
            return sb.Finish();
        }

        /// <summary>Stamps a created element. Must run inside an open transaction.</summary>
        public static bool Stamp(Element element, string sourceKey, string geoHash, string runId)
        {
            if (element == null) return false;
            try
            {
                var entity = new Entity(GetOrCreate());
                entity.Set(FieldVersion, CurrentVersion);
                entity.Set(FieldKey,  sourceKey ?? "");
                entity.Set(FieldHash, geoHash ?? "");
                entity.Set(FieldRunId, runId ?? "");
                element.SetEntity(entity);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("CopyFromLinkStampSchema: stamp element", ex);
                return false;
            }
        }

        /// <summary>One stamped host output: its element id and the provenance it carries.</summary>
        public sealed class StampRecord
        {
            public ElementId ElementId = ElementId.InvalidElementId;
            public string    SourceKey = "";
            public string    GeoHash   = "";
        }

        /// <summary>
        /// Every stamped host element in the document, read in one quick-filtered pass via
        /// <see cref="ExtensibleStorageFilter"/> (no full-document scan). Read-only.
        /// </summary>
        public static List<StampRecord> ReadAll(Document doc)
        {
            var list = new List<StampRecord>();
            if (doc == null) return list;
            Schema schema;
            try { schema = GetOrCreate(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("CopyFromLinkStampSchema: get schema", ex); return list; }

            IEnumerable<Element> stamped;
            try
            {
                stamped = new FilteredElementCollector(doc)
                    .WherePasses(new ExtensibleStorageFilter(SchemaGuid))
                    .WhereElementIsNotElementType()
                    .ToElements();
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("CopyFromLinkStampSchema: collect stamped elements", ex);
                return list;
            }

            foreach (var el in stamped)
            {
                try
                {
                    var entity = el.GetEntity(schema);
                    if (entity == null || !entity.IsValid()) continue;
                    list.Add(new StampRecord
                    {
                        ElementId = el.Id,
                        SourceKey = entity.Get<string>(FieldKey)  ?? "",
                        GeoHash   = entity.Get<string>(FieldHash) ?? "",
                    });
                }
                catch (Exception ex) { DiagnosticsLog.Swallowed("CopyFromLinkStampSchema: read entity", ex); }
            }
            return list;
        }
    }
}
