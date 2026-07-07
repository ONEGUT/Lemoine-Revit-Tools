using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.CopyFromLink
{
    /// <summary>
    /// Ownership + provenance stamp written onto every host element the Copy Linear tool creates
    /// (split segments or placed family instances). It records which source run produced the
    /// element and that source's identity hash, so a later "only process changed elements" run can
    /// reconcile the host model against the linked source without any external database:
    ///
    ///   • source key present, hash matches  → unchanged, skip the source
    ///   • source key present, hash differs  → changed, delete the old outputs and rebuild
    ///   • source key absent                 → new source, build
    ///   • stamped output whose source key is gone → source deleted, delete the orphaned outputs
    ///
    /// The GUID is HARDCODED forever — regenerating it would orphan every stamped element and break
    /// idempotent re-runs (same discipline as <c>AutoDimOwnerSchema</c>).
    /// </summary>
    public static class CopyLinearStampSchema
    {
        /// <summary>Stable, constant schema GUID. NEVER regenerate.</summary>
        public static readonly Guid SchemaGuid = new Guid("9E2C5A41-7B3F-4D18-A6C2-0F5B8D31E794");

        public const int CurrentVersion = 1;

        private const string SchemaName   = "LemoineCopyLinearStamp";
        private const string FieldVersion = "Version";
        private const string FieldKey     = "SourceKey";
        private const string FieldHash    = "GeoHash";
        private const string FieldRunId   = "RunId";
        private const string FieldMode    = "Mode";

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
            sb.AddSimpleField(FieldMode,    typeof(string));
            return sb.Finish();
        }

        /// <summary>Stamps a created element. Must run inside an open transaction.</summary>
        public static bool Stamp(Element element, string sourceKey, string geoHash, string runId, string mode)
        {
            if (element == null) return false;
            try
            {
                var entity = new Entity(GetOrCreate());
                entity.Set(FieldVersion, CurrentVersion);
                entity.Set(FieldKey,  sourceKey ?? "");
                entity.Set(FieldHash, geoHash ?? "");
                entity.Set(FieldRunId, runId ?? "");
                entity.Set(FieldMode,  mode ?? "");
                element.SetEntity(entity);
                return true;
            }
            catch (Exception ex)
            {
                LemoineLog.Error("CopyLinearStampSchema: stamp element", ex);
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
            catch (Exception ex) { LemoineLog.Swallowed("CopyLinearStampSchema: get schema", ex); return list; }

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
                LemoineLog.Swallowed("CopyLinearStampSchema: collect stamped elements", ex);
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
                catch (Exception ex) { LemoineLog.Swallowed("CopyLinearStampSchema: read entity", ex); }
            }
            return list;
        }
    }
}
