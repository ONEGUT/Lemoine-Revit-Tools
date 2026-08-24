using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using LemoineTools.Framework;

namespace LemoineNavisworks.LevelModels
{
    // =========================================================================
    // LevelModelsStore — remembers each document's level setup between sessions.
    //
    // WHY LOCAL AND NOT IN THE NWF. Navisworks exposes no way to attach custom
    // data to a file: the whole Autodesk.Navisworks.Api surface has no XData,
    // GetUserData or SetUserData member (verified by decoding the assembly with
    // devtools/navis_dump.py). Selection Sets are the only in-file structure that
    // could carry the grouping, and writing those would modify the user's model
    // and only persist once they saved it. So the setup lives beside the plugin's
    // other settings, keyed by the document it belongs to.
    //
    // This follows the repo's own storage rule (CLAUDE.md, "Settings Storage"):
    // %AppData% is machine-wide and shared by every project, so anything naming
    // something INSIDE one model — here, model keys and level names — must be
    // filed under a per-document bucket rather than written globally. The bucket
    // key is the document's own file path, and the buckets are capped LRU so the
    // file cannot grow without bound.
    // =========================================================================
    internal static class LevelModelsStore
    {
        private const int MaxDocuments = 40;   // LRU cap, same discipline as the Revit DocScoped stores

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LemoineTools", "navis-level-models.xml");

        // ── Serialized shape (public — XmlSerializer refuses non-public types) ─

        [XmlRoot("NavisLevelModels")]
        public sealed class StoreDto
        {
            [XmlElement("Document")]
            public List<DocumentDto> Documents { get; set; } = new List<DocumentDto>();
        }

        public sealed class DocumentDto
        {
            [XmlAttribute("key")]      public string Key      { get; set; } = "";
            [XmlAttribute("modified")] public long   Modified  { get; set; }
            [XmlAttribute("source")]   public string SourceModel { get; set; } = "";
            [XmlElement("Level")]      public List<LevelDto> Levels { get; set; } = new List<LevelDto>();
        }

        public sealed class LevelDto
        {
            [XmlAttribute("name")]   public string Name   { get; set; } = "";
            [XmlAttribute("bottom")] public double Bottom { get; set; }
            [XmlAttribute("top")]    public double Top    { get; set; }
            [XmlAttribute("edited")] public bool   Edited { get; set; }
            [XmlElement("Model")]    public List<string> Models { get; set; } = new List<string>();
        }

        // ── API ───────────────────────────────────────────────────────────────

        /// <summary>Loads the saved setup for this document, or null when there is none. Order is
        /// preserved exactly as saved — the user's own ordering is part of the setup.</summary>
        public static (List<LevelDef> Levels, string SourceModel)? Load(string documentKey)
        {
            if (string.IsNullOrWhiteSpace(documentKey)) return null;

            var store = ReadStore();
            var doc = store.Documents.FirstOrDefault(
                d => string.Equals(d.Key, documentKey, StringComparison.OrdinalIgnoreCase));
            if (doc == null || doc.Levels.Count == 0) return null;

            var levels = new List<LevelDef>();
            foreach (var l in doc.Levels)
            {
                var def = new LevelDef
                {
                    Name       = l.Name ?? "",
                    Bottom     = l.Bottom,
                    Top        = l.Top,
                    UserEdited = l.Edited,
                };
                foreach (var m in l.Models.OrEmpty())
                    if (!string.IsNullOrWhiteSpace(m) && !def.Models.Contains(m)) def.Models.Add(m);
                levels.Add(def);
            }
            return (levels, doc.SourceModel ?? "");
        }

        /// <summary>Writes this document's setup, replacing any previous one. Never throws — a
        /// failed save costs the remembered setup, and must not take the run with it.</summary>
        public static void Save(string documentKey, IReadOnlyList<LevelDef> levels, string sourceModel)
        {
            if (string.IsNullOrWhiteSpace(documentKey) || levels == null) return;

            try
            {
                var store = ReadStore();
                store.Documents.RemoveAll(
                    d => string.Equals(d.Key, documentKey, StringComparison.OrdinalIgnoreCase));

                var dto = new DocumentDto
                {
                    Key         = documentKey,
                    Modified    = DateTime.UtcNow.Ticks,
                    SourceModel = sourceModel ?? "",
                };
                foreach (var lv in levels)
                {
                    dto.Levels.Add(new LevelDto
                    {
                        Name   = lv.Name,
                        Bottom = lv.Bottom,
                        Top    = lv.Top,
                        Edited = lv.UserEdited,
                        Models = lv.Models.ToList(),
                    });
                }
                store.Documents.Add(dto);

                // Newest first, then trim — an old machine should not accumulate every model it
                // has ever opened.
                store.Documents = store.Documents
                    .OrderByDescending(d => d.Modified)
                    .Take(MaxDocuments)
                    .ToList();

                WriteStore(store);
            }
            catch (Exception ex) { DiagnosticsLog.Error("LevelModelsStore: save", ex); }
        }

        // ── File I/O ──────────────────────────────────────────────────────────

        private static StoreDto ReadStore()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return new StoreDto();
                using (var fs = File.OpenRead(path))
                    return (StoreDto)new XmlSerializer(typeof(StoreDto)).Deserialize(fs) ?? new StoreDto();
            }
            catch (Exception ex)
            {
                // A corrupt or older-shaped file must not block the tool — start clean and say so.
                DiagnosticsLog.Swallowed("LevelModelsStore: read (starting empty)", ex);
                return new StoreDto();
            }
        }

        private static void WriteStore(StoreDto store)
        {
            string path = FilePath;
            string dir  = Path.GetDirectoryName(path) ?? "";
            if (dir.Length > 0) Directory.CreateDirectory(dir);
            using (var fs = File.Create(path))
                new XmlSerializer(typeof(StoreDto)).Serialize(fs, store);
        }
    }
}
