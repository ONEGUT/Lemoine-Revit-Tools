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
            if (doc == null) return map;

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
