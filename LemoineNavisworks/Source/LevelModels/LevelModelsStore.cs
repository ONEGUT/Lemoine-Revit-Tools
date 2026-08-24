using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using LemoineTools.Framework;

namespace LemoineNavisworks.LevelModels
{
    // =========================================================================
    // Serialized shape for LevelModelsStore.
    //
    // TOP-LEVEL AND PUBLIC ON PURPOSE. These were nested inside the internal
    // LevelModelsStore, which makes their effective accessibility INTERNAL no
    // matter that each carried the `public` keyword — and XmlSerializer refuses a
    // type it cannot reach from outside the assembly. Because every save is inside
    // a try/catch, that threw after File.Create had already truncated the file,
    // leaving a ZERO-BYTE navis-level-models.xml behind: every later load then
    // failed with "Root element is missing" and the setup never came back. That is
    // CLAUDE.md's "XmlSerializer requires public types" rule, in its nested form.
    // =========================================================================

    [XmlRoot("NavisLevelModels")]
    public sealed class LevelModelsStoreDto
    {
        [XmlElement("Document")]
        public List<LevelModelsDocumentDto> Documents { get; set; } = new List<LevelModelsDocumentDto>();
    }

    public sealed class LevelModelsDocumentDto
    {
        [XmlAttribute("key")]      public string Key      { get; set; } = "";
        [XmlAttribute("modified")] public long   Modified  { get; set; }
        /// <summary>What the levels were read from: a grid-system name, or a model key on the
        /// tree fallback. Named "source" since before grids existed; the kind below says which.</summary>
        [XmlAttribute("source")]   public string SourceModel { get; set; } = "";
        /// <summary>"Grids" or "Tree". A logic token, deliberately not externalized. Missing
        /// (a setup saved before grids were read) deserializes to "", which the ViewModel
        /// treats as "whatever this document supports now".</summary>
        [XmlAttribute("sourceKind")] public string SourceKind { get; set; } = "";
        [XmlElement("Level")]      public List<LevelModelsLevelDto> Levels { get; set; } = new List<LevelModelsLevelDto>();

        /// <summary>Models that belong to every level (the every-level bucket).</summary>
        [XmlElement("EveryLevelModel")] public List<string> EveryLevelModels { get; set; } = new List<string>();

        // The OUTPUT settings. These were not stored at all, so every reopen threw away the
        // folder, the filename pattern and all four toggles while the level list came back —
        // which reads as "the tool didn't save my setup". Missing attributes deserialize to
        // these defaults, so a file written by the previous version still loads.
        [XmlAttribute("folder")]     public string OutputFolder { get; set; } = "";
        [XmlAttribute("pattern")]    public string Pattern      { get; set; } = "";
        [XmlAttribute("straddle")]   public string Straddle     { get; set; } = "";
        [XmlAttribute("viewpoints")] public bool   Viewpoints   { get; set; } = true;
        [XmlAttribute("clip")]       public bool   Clip         { get; set; } = true;
        [XmlAttribute("xrefs")]      public bool   EmbedXrefs   { get; set; } = true;
        [XmlAttribute("props")]      public bool   KeepProps    { get; set; } = true;
    }

    /// <summary>The output settings, carried between the store and the ViewModel as one value so
    /// adding another does not mean touching four signatures.</summary>
    public sealed class LevelModelsOutput
    {
        public string       Folder     = "";
        public string       Pattern    = "";
        public StraddleRule Straddle   = StraddleRule.KeepOverlapping;
        public bool         Viewpoints = true;
        public bool         Clip       = true;
        public bool         EmbedXrefs = true;
        public bool         KeepProps  = true;
    }

    public sealed class LevelModelsLevelDto
    {
        [XmlAttribute("name")]   public string Name   { get; set; } = "";
        [XmlAttribute("bottom")] public double Bottom { get; set; }
        [XmlAttribute("top")]    public double Top    { get; set; }
        [XmlAttribute("edited")] public bool   Edited { get; set; }
        [XmlElement("Model")]    public List<string> Models { get; set; } = new List<string>();
    }

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

        // ── API ───────────────────────────────────────────────────────────────

        /// <summary>Loads the saved setup for this document, or null when there is none. Order is
        /// preserved exactly as saved — the user's own ordering is part of the setup.</summary>
        public static (List<LevelDef> Levels, string SourceModel, string SourceKind,
                       List<string> EveryLevelModels, LevelModelsOutput Output)? Load(string documentKey)
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
            var output = new LevelModelsOutput
            {
                Folder     = doc.OutputFolder ?? "",
                Pattern    = doc.Pattern ?? "",
                // Persisted as a TOKEN, not the enum's numeric value: renaming or reordering
                // StraddleRule must never silently change what a saved setup means.
                Straddle   = string.Equals(doc.Straddle, StraddleCentroidToken, StringComparison.OrdinalIgnoreCase)
                                 ? StraddleRule.ByCentroid : StraddleRule.KeepOverlapping,
                Viewpoints = doc.Viewpoints,
                Clip       = doc.Clip,
                EmbedXrefs = doc.EmbedXrefs,
                KeepProps  = doc.KeepProps,
            };
            var everyLevel = new List<string>();
            foreach (var m in doc.EveryLevelModels.OrEmpty())
                if (!string.IsNullOrWhiteSpace(m) && !everyLevel.Contains(m)) everyLevel.Add(m);

            return (levels, doc.SourceModel ?? "", doc.SourceKind ?? "", everyLevel, output);
        }

        // Logic tokens, deliberately hardcoded (CLAUDE.md: persisted values compared with == are
        // never externalized).
        private const string StraddleCentroidToken = "ByCentroid";
        private const string StraddleKeepToken     = "KeepOverlapping";

        /// <summary>Writes this document's setup, replacing any previous one. Never throws — a
        /// failed save costs the remembered setup, and must not take the run with it.</summary>
        public static void Save(string documentKey, IReadOnlyList<LevelDef> levels, string sourceModel,
                                string sourceKind, IEnumerable<string>? everyLevelModels,
                                LevelModelsOutput? output)
        {
            if (string.IsNullOrWhiteSpace(documentKey) || levels == null) return;

            try
            {
                var store = ReadStore();
                store.Documents.RemoveAll(
                    d => string.Equals(d.Key, documentKey, StringComparison.OrdinalIgnoreCase));

                var o = output ?? new LevelModelsOutput();
                var dto = new LevelModelsDocumentDto
                {
                    Key          = documentKey,
                    Modified     = DateTime.UtcNow.Ticks,
                    SourceModel  = sourceModel ?? "",
                    SourceKind   = sourceKind ?? "",
                    EveryLevelModels = (everyLevelModels ?? new List<string>()).ToList(),
                    OutputFolder = o.Folder ?? "",
                    Pattern      = o.Pattern ?? "",
                    Straddle     = o.Straddle == StraddleRule.ByCentroid
                                       ? StraddleCentroidToken : StraddleKeepToken,
                    Viewpoints   = o.Viewpoints,
                    Clip         = o.Clip,
                    EmbedXrefs   = o.EmbedXrefs,
                    KeepProps    = o.KeepProps,
                };
                foreach (var lv in levels)
                {
                    dto.Levels.Add(new LevelModelsLevelDto
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

        private static LevelModelsStoreDto ReadStore()
        {
            string path = FilePath;
            try
            {
                if (!File.Exists(path)) return new LevelModelsStoreDto();

                // A zero-byte file is what a half-finished write leaves behind. It is "no settings
                // yet", not corruption, and warning about it every single load was noise on top of
                // the real bug rather than a report of it.
                if (new FileInfo(path).Length == 0) return new LevelModelsStoreDto();

                using (var fs = File.OpenRead(path))
                    return (LevelModelsStoreDto)new XmlSerializer(typeof(LevelModelsStoreDto)).Deserialize(fs)
                           ?? new LevelModelsStoreDto();
            }
            catch (Exception ex)
            {
                // A genuinely corrupt or older-shaped file must not block the tool — start clean,
                // and say so, because this is the path that silently loses a saved setup.
                DiagnosticsLog.Swallowed("LevelModelsStore: read (starting empty)", ex);
                return new LevelModelsStoreDto();
            }
        }

        /// <summary>Serialize to a TEMP file first, then move it into place.
        ///
        /// <para>Writing straight to the real path truncates it before serialization has produced a
        /// single byte, so anything that throws mid-write — or a Navisworks crash — leaves a
        /// zero-length file that every later load rejects. That is exactly what happened here.
        /// Serializing to a temp file means the real one is only ever replaced by a complete
        /// document.</para></summary>
        private static void WriteStore(LevelModelsStoreDto store)
        {
            string path = FilePath;
            string dir  = Path.GetDirectoryName(path) ?? "";
            if (dir.Length > 0) Directory.CreateDirectory(dir);

            string temp = path + ".tmp";
            using (var fs = File.Create(temp))
            {
                new XmlSerializer(typeof(LevelModelsStoreDto)).Serialize(fs, store);
                fs.Flush();
            }

            // File.Move cannot overwrite on .NET Framework, so clear the target first. The window
            // between the two is why the temp file exists at all: if the delete succeeds and the
            // move does not, the temp file is still a complete, recoverable document.
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
    }
}
