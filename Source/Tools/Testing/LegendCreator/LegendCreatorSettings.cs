using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Lemoine.Templates;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Testing.LegendCreator
{
    // =========================================================================
    // T08 — Legend Creator domain model
    //
    // Stored as XML at  %AppData%\LemoineTools\LegendCreatorSettings.xml
    //
    // Hierarchy:
    //   LegendCreatorSettings
    //     └── Legends[]         — list of named legend slots
    //           ├── Layout      — Title / Subtitle / dims / font / gap
    //           ├── Rows[]      — vertical rows
    //           └── RevitViewId — ElementId of the created Revit legend view (-1 = not yet created)
    // =========================================================================

    /// <summary>
    /// Layout-bar settings: titles + swatch dims + font / gap.
    /// </summary>
    public sealed class LegendLayoutConfig
    {
        [XmlAttribute] public string Title    { get; set; } = "Filter Legend";
        [XmlAttribute] public string Subtitle { get; set; } = "";
        [XmlAttribute] public int    ViewScale { get; set; } = 48;
        [XmlAttribute] public double SwatchW  { get; set; } = 0.25;
        [XmlAttribute] public double SwatchH  { get; set; } = 0.13;
        [XmlAttribute] public int    FontPt   { get; set; } = 9;
        [XmlAttribute] public double Gap      { get; set; } = 0.08;

        public void Normalize()
        {
            if (SwatchW > 5 || SwatchH > 5 || Gap > 3)
            {
                SwatchW = 0.25; SwatchH = 0.13; Gap = 0.08;
            }
            if (ViewScale <= 0) ViewScale = 48;
        }

        public LegendLayoutConfig Clone() => new LegendLayoutConfig
        {
            Title = Title, Subtitle = Subtitle,
            ViewScale = ViewScale,
            SwatchW = SwatchW, SwatchH = SwatchH,
            FontPt = FontPt, Gap = Gap,
        };
    }

    /// <summary>
    /// A single horizontal band of groups.
    /// </summary>
    public sealed class LegendRowConfig
    {
        [XmlAttribute] public string Id { get; set; } = "";

        [XmlArray("Groups"), XmlArrayItem("Group")]
        public List<LegendGroupConfig> Groups { get; set; } = new List<LegendGroupConfig>();

        public LegendRowConfig Clone() => new LegendRowConfig
        {
            Id     = Id,
            Groups = Groups.ConvertAll(g => g.Clone()),
        };
    }

    /// <summary>
    /// A category card inside a row.
    /// </summary>
    public sealed class LegendGroupConfig
    {
        [XmlAttribute] public string Id            { get; set; } = "";
        [XmlAttribute] public string Title         { get; set; } = "";
        [XmlAttribute] public string SourceTradeId { get; set; } = "";
        [XmlAttribute] public bool   Collapsed     { get; set; } = false;

        [XmlArray("Blocks"), XmlArrayItem("Block")]
        public List<LegendBlockConfig> Blocks { get; set; } = new List<LegendBlockConfig>();

        public LegendGroupConfig Clone() => new LegendGroupConfig
        {
            Id            = Id,
            Title         = Title,
            SourceTradeId = SourceTradeId,
            Collapsed     = Collapsed,
            Blocks        = Blocks.ConvertAll(b => b.Clone()),
        };
    }

    /// <summary>
    /// A single legend entry (one row in the rendered legend).
    /// </summary>
    public sealed class LegendBlockConfig
    {
        [XmlAttribute] public string Id            { get; set; } = "";
        [XmlAttribute] public string Name          { get; set; } = "";
        [XmlAttribute] public bool   NameOverride  { get; set; } = false;
        [XmlAttribute] public string SourceTradeId { get; set; } = "";
        [XmlAttribute] public string SourceRuleId  { get; set; } = "";
        [XmlAttribute] public string Color         { get; set; } = "#888888";
        [XmlAttribute] public bool   ColorOverride { get; set; } = false;
        [XmlAttribute] public string Fill          { get; set; } = "solid";
        [XmlAttribute] public string Kind          { get; set; } = "square";
        [XmlAttribute] public bool   Visible       { get; set; } = true;
        [XmlAttribute] public bool   Custom        { get; set; } = false;

        public LegendBlockConfig Clone() => new LegendBlockConfig
        {
            Id            = Id,
            Name          = Name,           NameOverride  = NameOverride,
            SourceTradeId = SourceTradeId,  SourceRuleId  = SourceRuleId,
            Color         = Color,          ColorOverride = ColorOverride,
            Fill          = Fill,           Kind          = Kind,
            Visible       = Visible,        Custom        = Custom,
        };
    }

    // =========================================================================
    // LegendEntry — a named legend slot
    // =========================================================================

    /// <summary>
    /// One legend slot: its layout, row/group/block tree, Revit view binding,
    /// and per-role TextNoteType selections. Multiple entries are managed in
    /// the sidebar of <c>LegendSettingsWindow</c>.
    /// </summary>
    public sealed class LegendEntry
    {
        /// <summary>Stable runtime identifier for this legend slot.</summary>
        [XmlAttribute] public string Id { get; set; } = "";

        /// <summary>
        /// User-overridden sidebar label. Null means auto-mirror <see cref="Layout"/>.Title.
        /// </summary>
        [XmlAttribute] public string? DisplayName { get; set; }

        /// <summary>
        /// <see cref="Autodesk.Revit.DB.ElementId.IntegerValue"/> of the legend view created
        /// in Revit. <c>-1</c> means the legend has not been created yet.
        /// </summary>
        [XmlAttribute] public long RevitViewId { get; set; } = -1;

        /// <summary>Per-role TextNoteType ElementId (-1 = use project default).</summary>
        [XmlAttribute] public long TitleTypeId       { get; set; } = -1;
        [XmlAttribute] public long SubtitleTypeId    { get; set; } = -1;
        [XmlAttribute] public long GroupHeaderTypeId { get; set; } = -1;
        [XmlAttribute] public long LabelTypeId       { get; set; } = -1;

        [XmlElement("Layout")]
        public LegendLayoutConfig Layout { get; set; } = new LegendLayoutConfig();

        [XmlArray("Rows"), XmlArrayItem("Row")]
        public List<LegendRowConfig> Rows { get; set; } = new List<LegendRowConfig>();

        [XmlAttribute] public bool PreviewVisible { get; set; }

        /// <summary>Returns the sidebar tab label for this entry.</summary>
        public string GetDisplayName() =>
            !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName! :
            !string.IsNullOrWhiteSpace(Layout?.Title) ? Layout!.Title : "Untitled";

        public LegendEntry Clone() => new LegendEntry
        {
            Id                = Id,
            DisplayName       = DisplayName,
            RevitViewId       = RevitViewId,
            TitleTypeId       = TitleTypeId,
            SubtitleTypeId    = SubtitleTypeId,
            GroupHeaderTypeId = GroupHeaderTypeId,
            LabelTypeId       = LabelTypeId,
            Layout            = LegendCreatorSettings.DeepCopy(Layout),
            Rows              = LegendCreatorSettings.DeepCopy(Rows),
            PreviewVisible    = PreviewVisible,
        };
    }

    // =========================================================================
    // Singleton — XML-backed
    // =========================================================================

    [XmlRoot("LegendCreatorSettings")]
    public sealed class LegendCreatorSettings
    {
        // ── New multi-legend storage ─────────────────────────────────────────
        [XmlArray("Legends"), XmlArrayItem("Legend")]
        public List<LegendEntry> Legends { get; set; } = new List<LegendEntry>();

        // ── Legacy single-legend fields (read from old XML, never written) ───
        // ShouldSerializeXxx() returning false prevents XmlSerializer from emitting
        // these on save; they are still deserialized from old files for migration.

        [XmlElement("Layout")]
        public LegendLayoutConfig? LegacyLayout { get; set; }
        public bool ShouldSerializeLegacyLayout() => false;

        [XmlArray("Rows"), XmlArrayItem("Row")]
        public List<LegendRowConfig>? LegacyRows { get; set; }
        public bool ShouldSerializeLegacyRows() => false;

        [XmlAttribute("PreviewVisible")]
        public bool LegacyPreviewVisible { get; set; } = true;
        public bool ShouldSerializeLegacyPreviewVisible() => false;

        // ── Singleton ────────────────────────────────────────────────────────
        private static readonly Lazy<LegendCreatorSettings> _lazy =
            new Lazy<LegendCreatorSettings>(LoadFromDisk);

        public static LegendCreatorSettings Instance => _lazy.Value;

        public LegendCreatorSettings() { }

        // ── Events ───────────────────────────────────────────────────────────
        public static event Action? Saved;

        // ── Persistence ──────────────────────────────────────────────────────
        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                try { Directory.CreateDirectory(dir); } catch (Exception __lex) { LemoineLog.Swallowed("LegendCreatorSettings: create config directory", __lex); }
                return Path.Combine(dir, "LegendCreatorSettings.xml");
            }
        }

        public void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(LegendCreatorSettings));
                using (var w = new StreamWriter(FilePath))
                    xs.Serialize(w, this);
                Saved?.Invoke();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[LemoineTools] LegendCreatorSettings save failed: {ex.Message}");
            }
        }

        private static LegendCreatorSettings LoadFromDisk()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    var xs = new XmlSerializer(typeof(LegendCreatorSettings));
                    using (var r = new StreamReader(path))
                    {
                        var result = (LegendCreatorSettings)xs.Deserialize(r)!;
                        result.Normalize();
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[LemoineTools] LegendCreatorSettings load failed: {ex.Message}");
            }
            return DefaultSeed();
        }

        // If no Legends list was found (old file format), migrate the top-level
        // Layout + Rows into a single LegendEntry.
        private void Normalize()
        {
            if (Legends.Count == 0)
            {
                var entry = new LegendEntry
                {
                    Id             = LegendIdGen.New("legend"),
                    Layout         = LegacyLayout ?? new LegendLayoutConfig(),
                    Rows           = LegacyRows   ?? new List<LegendRowConfig>(),
                    PreviewVisible = LegacyPreviewVisible,
                };
                entry.Layout.Normalize();
                Legends.Add(entry);
            }
            else
            {
                foreach (var e in Legends)
                    e.Layout?.Normalize();
            }
        }

        public static LegendCreatorSettings DefaultSeed() => new LegendCreatorSettings
        {
            Legends = new List<LegendEntry>
            {
                new LegendEntry
                {
                    Id = "legend_seed_1",
                    Layout = new LegendLayoutConfig
                    {
                        Title    = "Filter Legend",
                        Subtitle = "",
                        ViewScale = 48,
                        SwatchW = 0.25, SwatchH = 0.13, FontPt = 9, Gap = 0.08,
                    },
                    PreviewVisible = true,
                    Rows = new List<LegendRowConfig>
                    {
                        new LegendRowConfig
                        {
                            Id = "r_seed_1",
                            Groups = new List<LegendGroupConfig>
                            {
                                new LegendGroupConfig
                                {
                                    Id = "g_seed_1",
                                    Title = "ARCHITECTURAL",
                                    SourceTradeId = "",
                                    Blocks = new List<LegendBlockConfig>
                                    {
                                        new LegendBlockConfig { Id="b_seed_1", Name="Example 1", Color="#8c8c8c", Kind="square", Fill="solid",  Custom=true, Visible=true },
                                        new LegendBlockConfig { Id="b_seed_2", Name="Example 2", Color="#8c8c8c", Kind="square", Fill="hatch",  Custom=true, Visible=true },
                                        new LegendBlockConfig { Id="b_seed_3", Name="Example 3", Color="#8c8c8c", Kind="square", Fill="dots",   Custom=true, Visible=true },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        // ── DeepCopy helpers ─────────────────────────────────────────────────
        public static List<LegendRowConfig> DeepCopy(List<LegendRowConfig> rows)
        {
            var list = new List<LegendRowConfig>(rows.Count);
            for (int i = 0; i < rows.Count; i++) list.Add(rows[i].Clone());
            return list;
        }

        public static LegendLayoutConfig DeepCopy(LegendLayoutConfig layout) => layout.Clone();

        public static LegendEntry DeepCopy(LegendEntry entry) => entry.Clone();

        // ── Template store ───────────────────────────────────────────────────
        public static void ExportTo(string path, LegendCreatorSettings data)
        {
            var xs = new XmlSerializer(typeof(LegendCreatorSettings));
            using (var w = new StreamWriter(path)) xs.Serialize(w, data);
        }

        public static LegendCreatorSettings? TryLoad(string path)
        {
            try
            {
                var xs = new XmlSerializer(typeof(LegendCreatorSettings));
                using (var r = new StreamReader(path))
                {
                    var result = (LegendCreatorSettings)xs.Deserialize(r)!;
                    result.Normalize();
                    return result;
                }
            }
            catch { return null; }
        }

        private static LemoineTemplateStore<LegendCreatorSettings>? _templateStore;
        public static LemoineTemplateStore<LegendCreatorSettings> Templates =>
            _templateStore ?? (_templateStore = new LemoineTemplateStore<LegendCreatorSettings>(
                toolId:      "LegendCreator",
                serialize:   (data, path) => ExportTo(path, data),
                deserialize: path => TryLoad(path)));
    }

    // =========================================================================
    // Id helper
    // =========================================================================
    internal static class LegendIdGen
    {
        private static int _counter = 0;
        public static string New(string prefix)
        {
            int n = System.Threading.Interlocked.Increment(ref _counter);
            return $"{prefix}_{DateTime.UtcNow.Ticks % 100000}_{n}";
        }
    }
}
