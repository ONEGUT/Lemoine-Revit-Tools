using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using LemoineTools.Framework;

namespace LemoineTools.Tools.AutoFilters
{
    // =========================================================================
    // AutoFilterOwnerSchema — ownership stamp on every ParameterFilterElement
    // the Auto Filters engine creates.
    //
    // Replaces the old machine-wide CreatedFilterNames manifest, which recorded
    // "which filters do I own?" in %AppData% — a per-DOCUMENT fact stored in a
    // per-USER file. Consequences of that design, both real:
    //
    //   • Run in project A, open project B, run again: B's orphan pass walked A's
    //     manifest and deleted any filter in B whose name matched. Model elements
    //     deleted on the strength of another project's state.
    //   • A's manifest was overwritten by B's run, so A's real orphans were never
    //     cleaned up again.
    //
    // The stamp records the OWNING trade + rule, not just a name, which is what
    // makes it safe between users of a shared model: a filter stamped for a trade
    // that is not in the running user's library is not theirs to judge, and is
    // left alone. A name-only comparison would still let user B delete user A's
    // filters — see IsOrphan below.
    //
    // The GUID is HARDCODED forever — regenerating it would orphan every stamp.
    // =========================================================================
    public static class AutoFilterOwnerSchema
    {
        /// <summary>Stable, constant schema GUID. NEVER regenerate.</summary>
        public static readonly Guid SchemaGuid = new Guid("5A4B8D31-6E27-4C0F-9B73-1D8E5F26A409");

        public const int CurrentVersion = 1;

        private const string SchemaName   = "LemoineAutoFilterOwner";
        private const string FieldVersion = "Version";
        private const string FieldTradeId = "TradeId";
        private const string FieldRuleId  = "RuleId";

        // NOTE: deliberately mirrors the five existing Lemoine schemas — GUID, name,
        // read/write access, simple fields. In particular SetVendorId is NOT called,
        // because none of the proven in-repo schemas call it and an unregistered
        // vendor id would fail SchemaBuilder validation outright. See the plan's
        // verification note: whether Revit requires a vendor id is unconfirmed on
        // Windows, and if it does, every existing stamp in this plugin is already
        // failing silently.
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
                sb.AddSimpleField(FieldTradeId, typeof(string));
                sb.AddSimpleField(FieldRuleId,  typeof(string));
                return sb.Finish();
            }
            catch (Exception ex)
            {
                // A null schema disables ownership tracking for the run. Callers must
                // treat that as "own nothing" rather than "own everything" — deleting
                // filters we can no longer prove we created would be destructive.
                DiagnosticsLog.Error("AutoFilterOwnerSchema: create schema", ex);
                return null;
            }
        }

        /// <summary>Composite ownership key for a trade + rule pair.</summary>
        public static string OwnerKey(string? tradeId, string? ruleId)
            => (tradeId ?? "") + "::" + (ruleId ?? "");

        /// <summary>
        /// Stamps a filter this tool created. Must run inside an open transaction.
        /// Returns false (and logs) on failure — the filter still exists and works,
        /// it simply will not be recognised as ours on the next run.
        /// </summary>
        public static bool Stamp(Element filter, string? tradeId, string? ruleId)
        {
            if (filter == null) return false;
            var schema = GetOrCreate();
            if (schema == null) return false;
            try
            {
                var entity = new Entity(schema);
                entity.Set(FieldVersion, CurrentVersion);
                entity.Set(FieldTradeId, tradeId ?? "");
                entity.Set(FieldRuleId,  ruleId  ?? "");
                filter.SetEntity(entity);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error($"AutoFilterOwnerSchema: stamp filter '{filter.Name}'", ex);
                return false;
            }
        }

        /// <summary>One stamped filter and the ownership it carries.</summary>
        public sealed class OwnerRecord
        {
            public ElementId ElementId = ElementId.InvalidElementId;
            public string    Name      = "";
            public string    TradeId   = "";
            public string    RuleId    = "";
            public string    Key       => OwnerKey(TradeId, RuleId);
        }

        /// <summary>
        /// Reads the ownership stamp off a single element, or null when unstamped.
        /// </summary>
        public static OwnerRecord? TryRead(Element el)
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
                    TradeId   = entity.Get<string>(FieldTradeId) ?? "",
                    RuleId    = entity.Get<string>(FieldRuleId)  ?? "",
                };
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"AutoFilterOwnerSchema: read stamp from '{el.Id}'", ex);
                return null;
            }
        }

        /// <summary>
        /// Every stamped ParameterFilterElement in the document, in one quick-filtered
        /// pass. Read-only; no transaction required. Returns an empty list (never null)
        /// when the schema is unavailable or the document holds none.
        /// </summary>
        public static List<OwnerRecord> ReadAll(Document doc)
        {
            var list = new List<OwnerRecord>();
            if (doc == null) return list;
            var schema = GetOrCreate();
            if (schema == null) return list;

            IEnumerable<Element> stamped;
            try
            {
                stamped = new FilteredElementCollector(doc)
                    .OfClass(typeof(ParameterFilterElement))
                    .WherePasses(new ExtensibleStorageFilter(SchemaGuid))
                    .ToElements();
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("AutoFilterOwnerSchema: collect stamped filters", ex);
                return list;
            }

            foreach (var el in stamped)
            {
                var rec = TryRead(el);
                if (rec != null) list.Add(rec);
            }
            return list;
        }

        /// <summary>
        /// Adopts filters created before ownership stamping existed: any unstamped
        /// ParameterFilterElement whose name matches one this library expects is
        /// stamped with that owner. Without this, every filter made by an earlier
        /// build becomes permanently unmanaged and can never be cleaned up.
        /// Must run inside an open transaction. Returns the number adopted.
        /// </summary>
        public static int AdoptUnstamped(
            Document doc,
            IReadOnlyDictionary<string, string> expectedNameToOwnerKey,
            IReadOnlyDictionary<string, ParameterFilterElement> existingFilters)
        {
            if (doc == null || expectedNameToOwnerKey == null || existingFilters == null) return 0;
            var schema = GetOrCreate();
            if (schema == null) return 0;

            int adopted = 0;
            foreach (var kv in expectedNameToOwnerKey)
            {
                if (!existingFilters.TryGetValue(kv.Key, out var pfe) || pfe == null) continue;
                if (TryRead(pfe) != null) continue;   // already stamped

                int sep = kv.Value.IndexOf("::", StringComparison.Ordinal);
                if (sep < 0) continue;
                string tradeId = kv.Value.Substring(0, sep);
                string ruleId  = kv.Value.Substring(sep + 2);
                if (Stamp(pfe, tradeId, ruleId)) adopted++;
            }
            return adopted;
        }
    }
}
