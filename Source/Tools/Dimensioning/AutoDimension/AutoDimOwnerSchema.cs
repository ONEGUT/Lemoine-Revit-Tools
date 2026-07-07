using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Dimensioning.AutoDimension
{
    /// <summary>
    /// Ownership marker for dimensions the auto-dimension engine places. Distinct from
    /// <c>ClashTagSchema</c> (which tags clash markers) so the two tools never delete or
    /// modify each other's elements.
    ///
    /// The GUID is HARDCODED and constant for all time — regenerating it would orphan every
    /// previously-stamped dimension, breaking idempotent re-runs. The schema carries a
    /// version int, the run id, and the resolved target key used for stale-target cleanup.
    /// Stored via an <see cref="Entity"/> attached directly to each placed dimension (never
    /// the user-visible Comments field).
    /// </summary>
    public static class AutoDimOwnerSchema
    {
        /// <summary>Stable, constant schema GUID. NEVER regenerate.</summary>
        public static readonly Guid SchemaGuid = new Guid("1B7E4C90-6A3D-4F2A-9C58-7E0D2B41A6F3");

        /// <summary>Current owner-entity version. Bump only with a migration plan.</summary>
        public const int CurrentVersion = 1;

        private const string SchemaName    = "LemoineAutoDimOwner";
        private const string FieldVersion  = "Version";
        private const string FieldRunId    = "RunId";
        private const string FieldTargetKey = "TargetKey";

        /// <summary>
        /// Returns the owner schema, creating it in this session if absent. Safe to call
        /// repeatedly; <see cref="Schema.Lookup"/> short-circuits after the first build and
        /// lets entities written in a previous session stay readable.
        /// </summary>
        public static Schema GetOrCreate()
        {
            var existing = Schema.Lookup(SchemaGuid);
            if (existing != null) return existing;

            var sb = new SchemaBuilder(SchemaGuid);
            sb.SetSchemaName(SchemaName);
            sb.SetReadAccessLevel(AccessLevel.Public);
            sb.SetWriteAccessLevel(AccessLevel.Public);
            sb.AddSimpleField(FieldVersion, typeof(int));
            sb.AddSimpleField(FieldRunId, typeof(string));
            sb.AddSimpleField(FieldTargetKey, typeof(string));
            return sb.Finish();
        }

        /// <summary>
        /// Stamps a placed dimension as engine-owned. Must run inside an open transaction.
        /// Failures are logged, not thrown, so one un-taggable element never aborts the run —
        /// but an un-stamped dimension would not be reclaimed next run, so the caller should
        /// treat a false return as a placement failure.
        /// </summary>
        public static bool Stamp(Element element, string runId, string targetKey)
        {
            if (element == null) return false;
            try
            {
                var schema = GetOrCreate();
                var entity = new Entity(schema);
                entity.Set(FieldVersion, CurrentVersion);
                entity.Set(FieldRunId, runId ?? "");
                entity.Set(FieldTargetKey, targetKey ?? "");
                element.SetEntity(entity);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("AutoDimOwnerSchema: stamp owner entity", ex);
                return false;
            }
        }

        /// <summary>True if the element carries our owner entity. Read-only — no transaction needed.</summary>
        public static bool IsOwned(Element element)
        {
            if (element == null) return false;
            try
            {
                var schema = GetOrCreate();
                if (schema == null) return false;
                var entity = element.GetEntity(schema);
                return entity != null && entity.IsValid();
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("AutoDimOwnerSchema: read owner entity", ex);
                return false;
            }
        }

        /// <summary>Reads the stored target key, or "" if the element is not owned / unreadable.</summary>
        public static string ReadTargetKey(Element element)
        {
            if (element == null) return "";
            try
            {
                var schema = GetOrCreate();
                if (schema == null) return "";
                var entity = element.GetEntity(schema);
                if (entity == null || !entity.IsValid()) return "";
                return entity.Get<string>(FieldTargetKey) ?? "";
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("AutoDimOwnerSchema: read target key", ex);
                return "";
            }
        }
    }
}
