using System;
using System.IO;
using System.Xml.Serialization;

namespace LemoineTools.Framework.Zones
{
    // =========================================================================
    // ZoneSettings — the live zone library for the document currently open.
    //
    // Deliberately has NO %AppData% file of any kind. The other project libraries
    // (filters, legends, clash) keep a machine-wide XML with per-document buckets
    // as a cache in front of the document storage, for historical reasons. Zones
    // are Extensible Storage ONLY: the .rvt is the single source of truth, so
    // there is nothing to keep in sync and nothing that can leak between machines.
    //
    // That choice makes one hazard sharper, and it is handled here explicitly:
    //
    //   Because this holds ONE library rather than a per-document bucket, an
    //   EMPTY payload must CLEAR it. If "" were treated as "leave what you have"
    //   — which is exactly what the other three libraries do, because for them ""
    //   means "seed me" — then opening project B after project A would show A's
    //   zones, and the first save would write A's zones into B. That is the
    //   settings-leak bug class this repo has already shipped four times.
    //
    // So: "" means EMPTY LIBRARY here, not "seed me". Zones have no seed.
    //
    // Threading: LoadProjectLibrary runs on the Revit main thread at command
    // launch (via ProjectLibraries.LoadForDocument). The tool windows run on
    // their own STA threads and read/mutate Library from there, so access is
    // guarded — a window can still be editing while a new command loads.
    // =========================================================================
    public sealed class ZoneSettings
    {
        private static readonly Lazy<ZoneSettings> _lazy = new Lazy<ZoneSettings>(() => new ZoneSettings());
        public static ZoneSettings Instance => _lazy.Value;

        private readonly object _gate = new object();
        private ZoneLibrary _library = new ZoneLibrary();
        private string      _loadedForKey = "";

        private ZoneSettings() { }

        /// <summary>
        /// The active document's zone library. Never null — an unzoned project holds an empty
        /// one, which is a real state and not an error.
        /// </summary>
        public ZoneLibrary Library
        {
            get { lock (_gate) return _library; }
            set { lock (_gate) _library = value ?? new ZoneLibrary(); }
        }

        /// <summary>Document key the current library was loaded for. Diagnostics only.</summary>
        public string LoadedForKey
        {
            get { lock (_gate) return _loadedForKey; }
        }

        /// <summary>
        /// Replaces the library with what is stored IN THE DOCUMENT. Called at command launch
        /// with the XML read by <c>ProjectLibraryStore</c>.
        ///
        /// An empty payload CLEARS the library — see the note at the top of this file. A
        /// payload that fails to parse does NOT clear it and does not substitute an empty one:
        /// that would present a real project's zones as absent, and the next save would then
        /// destroy them. It reports and leaves the previous state alone.
        /// </summary>
        public static void LoadProjectLibrary(string? xml)
        {
            string key = DocumentKey.Current ?? "";

            if (string.IsNullOrWhiteSpace(xml))
            {
                lock (Instance._gate)
                {
                    Instance._library      = new ZoneLibrary();
                    Instance._loadedForKey = key;
                }
                DiagnosticsLog.Info("ZoneSettings",
                    $"No zone library stored in this document (key '{key}') — starting empty.");
                return;
            }

            try
            {
                var xs = new XmlSerializer(typeof(ZoneLibrary));
                using (var sr = new StringReader(xml))
                {
                    var lib = xs.Deserialize(sr) as ZoneLibrary;
                    if (lib == null)
                    {
                        DiagnosticsLog.Warn("ZoneSettings",
                            "Zone library payload deserialized to null; keeping the previous library rather than blanking it.");
                        return;
                    }

                    Normalize(lib);

                    lock (Instance._gate)
                    {
                        Instance._library      = lib;
                        Instance._loadedForKey = key;
                    }

                    DiagnosticsLog.Info("ZoneSettings",
                        $"Loaded zone library for '{key}': {lib.Buildings.Count} building(s), " +
                        $"{lib.Levels.Count} level(s), {lib.Areas.Count} area(s), " +
                        $"{lib.Recipes.Count} recipe(s), {lib.Layouts.Count} layout(s), " +
                        $"{lib.Placements.Count} placement(s).");
                }
            }
            catch (Exception ex)
            {
                // Deliberately NOT falling back to an empty library: presenting a real project's
                // zones as absent would let the next save wipe them.
                DiagnosticsLog.Error("ZoneSettings: read project zone library", ex);
            }
        }

        /// <summary>Serializes the active document's zone library for storage in the document.</summary>
        public static string SerializeProjectLibrary()
        {
            try
            {
                var lib = Instance.Library;
                var xs  = new XmlSerializer(typeof(ZoneLibrary));
                using (var sw = new StringWriter())
                {
                    xs.Serialize(sw, lib);
                    return sw.ToString();
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneSettings: serialize project zone library", ex);
                return "";
            }
        }

        /// <summary>
        /// Saves the library into the document, but only when it differs from what was loaded.
        /// Safe to call from a tool window's OnClosed on its own STA thread — the write itself
        /// is marshalled onto Revit's main thread by ProjectLibrarySaveHandler.
        /// </summary>
        public static void Save()
        {
            try
            {
                Project.ProjectLibraries.Save(
                    Project.ProjectLibraryStore.SectionZones,
                    SerializeProjectLibrary());
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneSettings: save project zone library", ex);
            }
        }

        /// <summary>
        /// Repairs anything a hand-edited or older payload could be missing. Never throws —
        /// a library that loads with gaps is far better than one that refuses to load.
        /// </summary>
        private static void Normalize(ZoneLibrary lib)
        {
            if (lib.Buildings  == null) lib.Buildings  = new System.Collections.Generic.List<ZoneBuilding>();
            if (lib.Levels     == null) lib.Levels     = new System.Collections.Generic.List<ZoneLevel>();
            if (lib.Areas      == null) lib.Areas      = new System.Collections.Generic.List<ZoneArea>();
            if (lib.Cells      == null) lib.Cells      = new System.Collections.Generic.List<ZoneCell>();
            if (lib.Recipes    == null) lib.Recipes    = new System.Collections.Generic.List<ZoneViewRecipe>();
            if (lib.Layouts    == null) lib.Layouts    = new System.Collections.Generic.List<ZoneSheetLayout>();
            if (lib.Placements == null) lib.Placements = new System.Collections.Generic.List<ZoneSheetPlacement>();

            foreach (var b in lib.Buildings) if (b != null && string.IsNullOrEmpty(b.Id)) b.Id = ZoneId.New();
            foreach (var l in lib.Levels)    if (l != null && string.IsNullOrEmpty(l.Id)) l.Id = ZoneId.New();
            foreach (var a in lib.Areas)     if (a != null && string.IsNullOrEmpty(a.Id)) a.Id = ZoneId.New();
            foreach (var r in lib.Recipes)   if (r != null && string.IsNullOrEmpty(r.Id)) r.Id = ZoneId.New();
            foreach (var y in lib.Layouts)
            {
                if (y == null) continue;
                if (string.IsNullOrEmpty(y.Id)) y.Id = ZoneId.New();
                if (y.Groups == null) y.Groups = new System.Collections.Generic.List<ZoneSheetGroup>();
                foreach (var g in y.Groups)
                {
                    if (g == null) continue;
                    if (string.IsNullOrEmpty(g.Id)) g.Id = ZoneId.New();
                    if (g.AreaIds == null) g.AreaIds = new System.Collections.Generic.List<string>();
                }
            }
            foreach (var a in lib.Areas)
                if (a != null && a.AppliesToLevelIds == null)
                    a.AppliesToLevelIds = new System.Collections.Generic.List<string>();
        }
    }

    /// <summary>
    /// Ids for zone records.
    ///
    /// GUIDs, always. A counter-based or tick-based scheme is fine while ids only index one
    /// user's own file and destructive the moment they key elements inside a shared model —
    /// that exact mistake shipped once in this repo (LegendIdGen), where a hardcoded seed id
    /// was identical on every install.
    /// </summary>
    public static class ZoneId
    {
        public static string New() => Guid.NewGuid().ToString("N");
    }
}
