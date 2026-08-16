using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace LemoineTools.Framework.Project
{
    // =========================================================================
    // ProjectLibraryStore — trade, legend and clash libraries stored INSIDE the
    // .rvt, so they belong to the project rather than to the machine.
    //
    // Why this and not a per-document bucket in %AppData%:
    //   %AppData% is per WINDOWS USER. Keying it by document stops one project's
    //   library leaking into another on ONE machine, but a colleague opening the
    //   same model reads their own %AppData%, which knows nothing about it — so
    //   they would see the seed defaults, never the library the project actually
    //   uses. Nothing outside the file can satisfy "everyone on this project sees
    //   this project's filters".
    //
    // One DataStorage element per document holds all three libraries, each in its
    // OWN field. Separate fields matter: two tool windows can be open at once, and
    // a single blob would make whichever closed last overwrite the other's section.
    //
    // Threading:
    //   Read  — main thread, no transaction. Done at command launch.
    //   Write — main thread, INSIDE a transaction. Done from an ExternalEvent.
    //
    // The GUID is HARDCODED forever — regenerating it orphans every project's data.
    // =========================================================================
    public static class ProjectLibraryStore
    {
        /// <summary>Stable, constant schema GUID. NEVER regenerate.</summary>
        public static readonly Guid SchemaGuid = new Guid("8B41F70C-2D95-4E63-A17B-5C90D3E824F1");

        public const int CurrentVersion = 1;

        private const string SchemaName    = "LemoineProjectLibrary";
        private const string ElementName   = "Lemoine Project Libraries";
        private const string FieldVersion  = "Version";

        /// <summary>Library sections. Each is an independent field — see the note above.</summary>
        public const string SectionFilters = "Filters";
        public const string SectionLegends = "Legends";
        public const string SectionClash   = "Clash";
        /// <summary>
        /// Project zones. Handled by <see cref="Zones.ZoneStore"/> under its OWN schema GUID,
        /// NOT as a fourth field here — see the note below. The constant lives here so the
        /// staging/save plumbing addresses every library by section name.
        /// </summary>
        public const string SectionZones   = "Zones";

        // Fields of THIS schema. Zones is deliberately absent.
        //
        // An extensible-storage schema's fields are fixed the moment its GUID is registered,
        // and a document that already holds these libraries registers the 3-field version on
        // read. Adding a fourth field here would make Schema.Lookup return that 3-field schema
        // and every entity.Set("Zones", …) throw — failing the whole Write, rolling back the
        // transaction, and silently breaking saves for the other three libraries on every
        // project that has ever used them. Zones therefore gets its own schema and its own
        // holder element, which also means no existing document is touched at all.
        private static readonly string[] AllSections = { SectionFilters, SectionLegends, SectionClash };

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
                foreach (var f in AllSections) sb.AddSimpleField(f, typeof(string));
                return sb.Finish();
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ProjectLibraryStore: create schema", ex);
                return null;
            }
        }

        /// <summary>
        /// Reads every library section out of the document. Read-only, no transaction —
        /// safe at command launch on the Revit main thread. A missing section (or a document
        /// that has never used these tools) yields an empty string, which callers treat as
        /// "seed me", not as "the library is empty".
        /// </summary>
        public static Dictionary<string, string> Read(Document? doc)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in AllSections) map[f] = "";
            map[SectionZones] = "";
            if (doc == null) return map;

            // Zones live under their own schema — read them first so a failure in this
            // schema cannot cost the caller its zones, and vice versa.
            map[SectionZones] = Zones.ZoneStore.Read(doc);

            var schema = GetOrCreate();
            if (schema == null) return map;

            try
            {
                var holder = FindHolder(doc);
                if (holder == null)
                {
                    DiagnosticsLog.Info("ProjectLibraryStore",
                        "No Lemoine libraries stored in this document yet — it will be seeded.");
                    return map;
                }

                var entity = holder.GetEntity(schema);
                if (entity == null || !entity.IsValid()) return map;

                foreach (var f in AllSections)
                {
                    try { map[f] = entity.Get<string>(f) ?? ""; }
                    catch (Exception ex)
                    {
                        // A section added in a later schema version is absent here. Report it
                        // rather than silently presenting an empty library as the real one.
                        DiagnosticsLog.Swallowed($"ProjectLibraryStore: read section '{f}'", ex);
                    }
                }

                DiagnosticsLog.Info("ProjectLibraryStore",
                    "Loaded project libraries: " +
                    string.Join(", ", AllSections.Select(f => $"{f}={map[f].Length}ch")));
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ProjectLibraryStore: read", ex);
            }
            return map;
        }

        /// <summary>
        /// Writes the given sections into the document. MUST run inside an open transaction on
        /// the Revit main thread. Only the sections supplied are touched, so a tool that never
        /// opened cannot blank another tool's library. Returns true when anything was written.
        /// </summary>
        public static bool Write(Document? doc, IReadOnlyDictionary<string, string> sections)
        {
            if (doc == null || sections == null || sections.Count == 0) return false;

            // ── Zones — own schema, own holder ────────────────────────────────
            bool wroteZones = false;
            bool zonesFailed = false;
            if (sections.TryGetValue(SectionZones, out var zonesXml))
            {
                wroteZones = Zones.ZoneStore.Write(doc, zonesXml ?? "");
                if (!wroteZones)
                {
                    zonesFailed = true;
                    DiagnosticsLog.Warn("ProjectLibraryStore",
                        "The zone library could not be written — see the preceding ZoneStore error.");
                }
            }

            // Nothing else staged? Then don't touch this schema's holder at all. Carrying the
            // other three sections through would create an empty holder on a project that has
            // never used them, purely as a side effect of saving zones.
            bool anyOwnSection = false;
            foreach (var f in AllSections)
                if (sections.ContainsKey(f)) { anyOwnSection = true; break; }
            if (!anyOwnSection) return wroteZones && !zonesFailed;

            var schema = GetOrCreate();
            if (schema == null) return false;

            try
            {
                var holder = FindHolder(doc) ?? CreateHolder(doc);
                if (holder == null) return false;

                // Start from what is already stored so untouched sections survive.
                var entity = holder.GetEntity(schema);
                if (entity == null || !entity.IsValid()) entity = new Entity(schema);

                entity.Set(FieldVersion, CurrentVersion);
                foreach (var f in AllSections)
                {
                    string value;
                    if (sections.TryGetValue(f, out var incoming)) value = incoming ?? "";
                    else
                    {
                        // Carry an untouched section through unchanged. A read failure here is
                        // normal on a freshly created entity (the field has never been set),
                        // but it must not pass silently: on an EXISTING entity it would mean
                        // this write is about to blank a library it could not read.
                        try { value = entity.Get<string>(f) ?? ""; }
                        catch (Exception ex)
                        {
                            value = "";
                            DiagnosticsLog.Swallowed(
                                $"ProjectLibraryStore: carry-over read of untouched section '{f}' " +
                                "(normal for a new store; on an existing one this blanks it)", ex);
                        }
                    }
                    entity.Set(f, value);
                }

                holder.SetEntity(entity);

                // Commit what worked even if the zone write failed. Returning false here would
                // roll the transaction back and destroy a successful filter/legend/clash save
                // for an unrelated failure in a different schema. The zone failure is already
                // logged as an error by ZoneStore.Write and warned about above.
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ProjectLibraryStore: write", ex);
                return false;
            }
        }

        /// <summary>
        /// Stages a section and asks Revit to commit it. Call from a tool window's OnClosed.
        /// A no-op when the section is unchanged, so opening a tool and closing it never
        /// dirties the model.
        /// </summary>
        public static void SaveSection(string section, string xml, string? loadedXml)
        {
            if (string.Equals(xml ?? "", loadedXml ?? "", StringComparison.Ordinal)) return;

            ProjectLibrarySaveHandler.Stage(section, xml ?? "", DocumentKey.Current);
            try { App.ProjectLibraryEvent?.Raise(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed($"ProjectLibraryStore: raise save for '{section}'", ex); }
        }

        // ── Holder element ────────────────────────────────────────────────────

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
                DiagnosticsLog.Swallowed("ProjectLibraryStore: find holder", ex);
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
                catch (Exception ex) { DiagnosticsLog.Swallowed("ProjectLibraryStore: name holder", ex); }
                return ds;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ProjectLibraryStore: create holder", ex);
                return null;
            }
        }

        /// <summary>
        /// True when this user can currently write to the holder. In a workshared model the
        /// element is owned by whoever last edited it until they synchronise; writing then
        /// fails, so the caller reports it instead of appearing to save.
        /// </summary>
        public static bool CanWrite(Document? doc, out string? reason)
        {
            reason = null;
            if (doc == null) { reason = "no document"; return false; }
            if (doc.IsReadOnly) { reason = "the document is read-only"; return false; }
            if (doc.IsFamilyDocument) { reason = "family documents have no project libraries"; return false; }

            try
            {
                if (!doc.IsWorkshared) return true;

                // Zones live on their own holder element, which can be owned by a different
                // user than the one holding the other three libraries. Probing only this
                // schema's holder would report "writable" and then lose the zone save.
                if (!Zones.ZoneStore.CanWrite(doc, out string? zoneWhy) && zoneWhy != null)
                {
                    reason = zoneWhy;
                    return false;
                }

                var holder = FindHolder(doc);
                if (holder == null) return true;   // not created yet; creation is fine

                var status = WorksharingUtils.GetCheckoutStatus(doc, holder.Id);
                if (status == CheckoutStatus.OwnedByOtherUser)
                {
                    reason = "another user currently owns the project libraries element — " +
                             "they need to synchronise before your changes can be saved";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("ProjectLibraryStore: checkout probe", ex);
                return true;   // advisory only; let the write attempt report the real failure
            }
        }
    }
}
