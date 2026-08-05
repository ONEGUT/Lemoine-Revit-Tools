using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Dimensioning
{
    /// <summary>
    /// Singleton, XML-backed library of saved <see cref="ClashDefinition"/>s.
    /// Stored in <c>%AppData%\LemoineTools\ClashDefinitions.xml</c>.
    /// Mirrors the AutoFiltersSettings pattern (lazy singleton, Save/Load, DeepCopy,
    /// Duplicate/Delete/Move, Export/Import).
    /// </summary>
    [XmlRoot("ClashDefinitions")]
    public sealed class ClashDefinitionsSettings
    {
        private static readonly Lazy<ClashDefinitionsSettings> _lazy =
            new Lazy<ClashDefinitionsSettings>(Load);
        public static ClashDefinitionsSettings Instance => _lazy.Value;

        /// <summary>Parameterless ctor required by <see cref="XmlSerializer"/>.</summary>
        public ClashDefinitionsSettings() { }

        // ── Clash library: one per project, never seeded ─────────────────────
        //
        // A clash definition names the elements, links and worksets of one specific model:
        // which two groups to test, which documents to scan, which worksets to exclude. None
        // of that is portable, so the whole library is per project and a new project starts
        // EMPTY. There is deliberately no seed — unlike trades and legends there is no
        // meaningful office-standard clash definition to start from, and inheriting one would
        // silently point a new project's scan at another project's selections.

        [XmlArray("DefinitionDocScopes"), XmlArrayItem("Doc")]
        public List<ClashDefinitionDocScope> DefinitionDocScopes { get; set; } =
            new List<ClashDefinitionDocScope>();

        /// <summary>Definition bucket for the active document, created empty on first touch.</summary>
        private ClashDefinitionDocScope Scope()
        {
            if (DefinitionDocScopes == null) DefinitionDocScopes = new List<ClashDefinitionDocScope>();

            string k = DocumentKey.Current ?? "";
            foreach (var d in DefinitionDocScopes)
                if (d != null && string.Equals(d.Key, k, StringComparison.OrdinalIgnoreCase))
                {
                    d.Touched = DateTime.UtcNow.Ticks;
                    return d;
                }

            var made = new ClashDefinitionDocScope { Key = k, Touched = DateTime.UtcNow.Ticks };
            DefinitionDocScopes.Add(made);

            while (DefinitionDocScopes.Count > DocScoped.MaxDocuments)
            {
                int oldest = 0;
                for (int i = 1; i < DefinitionDocScopes.Count; i++)
                    if (DefinitionDocScopes[i].Touched < DefinitionDocScopes[oldest].Touched) oldest = i;
                DefinitionDocScopes.RemoveAt(oldest);
            }
            return made;
        }

        /// <summary>This project's clash definitions. Keeps its name and shape for call sites.</summary>
        [XmlIgnore]
        public List<ClashDefinition> Definitions
        {
            get => Scope().Definitions;
            set => Scope().Definitions = value ?? new List<ClashDefinition>();
        }

        // ── Library operations ────────────────────────────────────────────────

        /// <summary>Appends a deep copy of <paramref name="def"/> with a fresh id and "(copy)" name.</summary>
        public ClashDefinition Duplicate(ClashDefinition def)
        {
            var copy = DeepCopy(def);
            copy.Id   = "C" + Guid.NewGuid().ToString("N").Substring(0, 7);
            copy.Name = string.IsNullOrWhiteSpace(def.Name) ? "Definition (copy)" : def.Name + " (copy)";
            Definitions.Add(copy);
            return copy;
        }

        /// <summary>Removes the definition with the given id, if present.</summary>
        public void Delete(string id)
        {
            Definitions.RemoveAll(d => d.Id == id);
        }

        /// <summary>Moves the definition at <paramref name="from"/> to <paramref name="to"/> (clamped).</summary>
        public void Move(int from, int to)
        {
            if (from < 0 || from >= Definitions.Count) return;
            to = Math.Max(0, Math.Min(Definitions.Count - 1, to));
            if (to == from) return;
            var item = Definitions[from];
            Definitions.RemoveAt(from);
            Definitions.Insert(to, item);
        }

        // ── Deep copy via XML round-trip (safe for live editing) ──────────────

        /// <summary>Deep-copies one definition through an XML round-trip.</summary>
        public static ClashDefinition DeepCopy(ClashDefinition src)
        {
            if (src == null) return ClashDefinition.NewBlank();
            var xs = new XmlSerializer(typeof(ClashDefinition));
            using (var ms = new MemoryStream())
            {
                xs.Serialize(ms, src);
                ms.Position = 0;
                return (ClashDefinition)xs.Deserialize(ms)!;
            }
        }

        /// <summary>Deep-copies a whole definition list through an XML round-trip.</summary>
        public static List<ClashDefinition> DeepCopy(List<ClashDefinition> src)
        {
            if (src == null || src.Count == 0) return new List<ClashDefinition>();
            // Serialize the LIST, not the settings root: round-tripping the root would mint a
            // document bucket on the way out and read one back on the way in, making a plain
            // copy depend on which document happens to be active.
            var xs = new XmlSerializer(typeof(List<ClashDefinition>));
            using (var ms = new MemoryStream())
            {
                xs.Serialize(ms, src);
                ms.Position = 0;
                return (List<ClashDefinition>)xs.Deserialize(ms)! ?? new List<ClashDefinition>();
            }
        }

        // ── Persistence ───────────────────────────────────────────────────────

        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("ClashDefinitionsSettings: ensure settings directory", ex); }
                return Path.Combine(dir, "ClashDefinitions.xml");
            }
        }

        public void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(ClashDefinitionsSettings));
                using (var w = new StreamWriter(FilePath))
                    xs.Serialize(w, this);
            }
            catch (Exception ex) { DiagnosticsLog.Error("ClashDefinitionsSettings: save", ex); }
        }

        private static ClashDefinitionsSettings Load()
        {
            string path = FilePath;

            if (File.Exists(path))
            {
                try
                {
                    var xs = new XmlSerializer(typeof(ClashDefinitionsSettings));
                    using (var r = new StreamReader(path))
                    {
                        var s = (ClashDefinitionsSettings)xs.Deserialize(r)!;
                        if (s.Definitions == null) s.Definitions = new List<ClashDefinition>();
                        return s;
                    }
                }
                catch (Exception ex)
                {
                    // The file EXISTS but won't parse. Falling through to the first-run
                    // seed path would return a one-item library that the next Save()
                    // writes over the (possibly recoverable) file — destroying every
                    // saved definition. Instead back the file up, surface the failure,
                    // and start empty without seeding so nothing is silently replaced.
                    DiagnosticsLog.Error(
                        "ClashDefinitions: settings file is corrupt — backed up and starting empty (existing data NOT overwritten)",
                        ex);
                    TryBackupCorruptFile(path);
                    return new ClashDefinitionsSettings();
                }
            }

            // No file yet: start empty. Definitions are per project and never seeded — see
            // the note on DefinitionDocScopes.
            return new ClashDefinitionsSettings();
        }

        // Copies an unreadable settings file aside so a parse failure never costs the
        // user their saved clash definitions — they can recover the .bak by hand.
        private static void TryBackupCorruptFile(string path)
        {
            try
            {
                string backup = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
                File.Copy(path, backup, overwrite: true);
                DiagnosticsLog.Info("ClashDefinitions", $"Corrupt settings backed up to {backup}");
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("ClashDefinitions: backup corrupt settings", ex); }
        }

        // Export/import was removed as dead code: it had no call sites, and its import was a
        // full library REPLACE — wiring it to a button later would have silently destroyed the
        // user's saved definitions. Re-add as merge-by-id if the feature is ever wanted.
    }

    /// <summary>
    /// One project's clash definitions. Public for XmlSerializer — a non-public root type
    /// throws at serializer construction and fails silently inside the surrounding try/catch,
    /// stranding every setting on its default (see CLAUDE.md).
    /// </summary>
    public sealed class ClashDefinitionDocScope
    {
        /// <summary>Document identity from <see cref="LemoineTools.Framework.DocumentKey"/>.
        /// Empty = the no-document slot.</summary>
        [XmlAttribute] public string Key { get; set; } = "";

        /// <summary>Ticks at last touch, for least-recently-used eviction.</summary>
        [XmlAttribute] public long Touched { get; set; }

        [XmlArray("Definitions"), XmlArrayItem("Definition")]
        public List<ClashDefinition> Definitions { get; set; } = new List<ClashDefinition>();
    }
}
