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

        [XmlArray("Definitions")]
        [XmlArrayItem("Definition")]
        public List<ClashDefinition> Definitions { get; set; } = new List<ClashDefinition>();

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
            var xs = new XmlSerializer(typeof(ClashDefinitionsSettings));
            using (var ms = new MemoryStream())
            {
                xs.Serialize(ms, new ClashDefinitionsSettings { Definitions = src });
                ms.Position = 0;
                return ((ClashDefinitionsSettings)xs.Deserialize(ms)!).Definitions
                    ?? new List<ClashDefinition>();
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

            // True first run (no file yet): seed one definition from the old Clash
            // Dimension settings so the library isn't empty and existing group/marking
            // choices carry over.
            var seeded = new ClashDefinitionsSettings();
            try { seeded.Definitions.Add(SeedFromClashDimension()); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("ClashDefinitionsSettings: seed from ClashDimension", ex); }
            return seeded;
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

        /// <summary>
        /// Builds a starter definition from the last-used Clash Dimension settings so the
        /// library opens with something usable rather than empty.
        /// </summary>
        private static ClashDefinition SeedFromClashDimension()
        {
            var s = ClashDimensionSettings.Instance;

            ClashGroupSpec Group(string mode, List<string> rules, List<string> cats,
                                  List<long> elemIds, List<long> elemLinks, List<long> srcLinks) =>
                new ClashGroupSpec
                {
                    Mode          = mode,
                    RuleKeys      = new List<string>(rules     ?? new List<string>()),
                    Categories    = new List<string>(cats      ?? new List<string>()),
                    ElemIds       = new List<long>(elemIds     ?? new List<long>()),
                    ElemLinkIds   = new List<long>(elemLinks   ?? new List<long>()),
                    SourceLinkIds = new List<long>(srcLinks    ?? new List<long>()),
                };

            return new ClashDefinition
            {
                Id               = "C" + Guid.NewGuid().ToString("N").Substring(0, 7),
                Name             = "Imported from Clash Dimension",
                Group1           = Group(s.Group1Mode, s.Group1RuleKeys, s.Group1Categories,
                                         s.Group1ElemIds, s.Group1ElemLinkIds, s.Group1SourceLinkIds),
                Group2           = Group(s.Group2Mode, s.Group2RuleKeys, s.Group2Categories,
                                         s.Group2ElemIds, s.Group2ElemLinkIds, s.Group2SourceLinkIds),
                ToleranceMm      = s.ToleranceMm,
                FillStyle        = s.FillStyle,
                FallbackColorHex = s.FallbackColorHex,
                CrossLineTypeName = s.CrossLineTypeName,
                DimTarget        = s.DimTarget,
                ClearPrevious    = s.ClearPrevious,
                MaxClashes       = s.MaxClashes,
            };
        }

        // ── Export / Import ───────────────────────────────────────────────────

        public static void ExportTo(string path, List<ClashDefinition> definitions)
        {
            var s = new ClashDefinitionsSettings { Definitions = definitions };
            var xs = new XmlSerializer(typeof(ClashDefinitionsSettings));
            using (var w = new StreamWriter(path)) xs.Serialize(w, s);
        }

        public static bool TryImportFrom(string path, out string? error)
        {
            error = null;
            try
            {
                var xs = new XmlSerializer(typeof(ClashDefinitionsSettings));
                using (var r = new StreamReader(path))
                {
                    var s = (ClashDefinitionsSettings)xs.Deserialize(r)!;
                    Instance.Definitions = s.Definitions ?? new List<ClashDefinition>();
                    Instance.Save();
                }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
    }
}
