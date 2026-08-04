using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Framework.Templates;
using LemoineTools.Framework;

namespace LemoineTools.Tools.FiltersLegends.LegendCreator
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
    //           └── *TypeName   — per-role TextNoteType NAMES (portable between projects)
    //
    // Which Revit legend view an entry created is NOT stored here — it is stamped into
    // the document itself (LegendLinkSchema), keyed by the entry's Id. This file is
    // machine-wide and shared by every project, so an ElementId kept here claimed a
    // legend in projects that had never seen one.
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

        // Spacing (paper inches). The old single "Gap" meant the vertical row gap in the
        // preview but the horizontal swatch→label gap in the output — the same control
        // changed two different dimensions. These three name each dimension explicitly.
        /// <summary>Vertical gap between stacked rows of groups.</summary>
        [XmlAttribute] public double RowGap        { get; set; } = 0.30;
        /// <summary>Horizontal gap between adjacent group columns within a row.</summary>
        [XmlAttribute] public double ColGap        { get; set; } = 0.30;
        /// <summary>Horizontal gap between a swatch and its label.</summary>
        [XmlAttribute] public double SwatchLabelGap { get; set; } = 0.08;

        // Legacy single gap: read from old XML for migration, never written back.
        [XmlAttribute("Gap")] public double LegacyGap { get; set; } = double.NaN;
        public bool ShouldSerializeLegacyGap() => false;

        public void Normalize()
        {
            // Migrate an old file's single Gap into the swatch→label gap (its output meaning),
            // seeding the new vertical/horizontal gaps with sensible defaults.
            if (!double.IsNaN(LegacyGap))
            {
                SwatchLabelGap = LegacyGap;
                if (RowGap <= 0) RowGap = 0.30;
                if (ColGap <= 0) ColGap = 0.30;
                LegacyGap = double.NaN;
            }

            if (SwatchW > 5 || SwatchH > 5) { SwatchW = 0.25; SwatchH = 0.13; }
            if (SwatchLabelGap > 3 || SwatchLabelGap < 0) SwatchLabelGap = 0.08;
            if (RowGap > 5 || RowGap <= 0) RowGap = 0.30;
            if (ColGap > 5 || ColGap <= 0) ColGap = 0.30;
            if (ViewScale <= 0) ViewScale = 48;
        }

        public LegendLayoutConfig Clone() => new LegendLayoutConfig
        {
            Title = Title, Subtitle = Subtitle,
            ViewScale = ViewScale,
            SwatchW = SwatchW, SwatchH = SwatchH,
            FontPt = FontPt,
            RowGap = RowGap, ColGap = ColGap, SwatchLabelGap = SwatchLabelGap,
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

        // NOTE: there is deliberately no RevitViewId here, and the text styles below are
        // NAMES, not ElementIds. Both used to be raw ElementIds in this machine-wide file,
        // which meant a legend created in one project was claimed by every other project —
        // the window offered "Update Legend" and targeted an id belonging to some unrelated
        // element. Which legend belongs to an entry is now recorded in the document itself
        // (LegendLinkSchema stamps the created legend view). Do not reintroduce ids here.

        /// <summary>
        /// Per-role TextNoteType NAME (empty = use project default). A name is portable
        /// between projects; an ElementId is not. Resolved against the active document
        /// when the window opens, and reported when it cannot be resolved.
        /// </summary>
        [XmlAttribute] public string TitleTypeName       { get; set; } = "";
        [XmlAttribute] public string SubtitleTypeName    { get; set; } = "";
        [XmlAttribute] public string GroupHeaderTypeName { get; set; } = "";
        [XmlAttribute] public string LabelTypeName       { get; set; } = "";

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
            Id                  = Id,
            DisplayName         = DisplayName,
            TitleTypeName       = TitleTypeName,
            SubtitleTypeName    = SubtitleTypeName,
            GroupHeaderTypeName = GroupHeaderTypeName,
            LabelTypeName       = LabelTypeName,
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
                try { Directory.CreateDirectory(dir); } catch (Exception __lex) { DiagnosticsLog.Swallowed("LegendCreatorSettings: create config directory", __lex); }
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
                DiagnosticsLog.Error("LegendCreatorSettings: save failed", ex);
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
                DiagnosticsLog.Error("LegendCreatorSettings: load failed", ex);
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

            // Re-mint entry ids left over from the old generator (and the hardcoded
            // "legend_seed_1" that shipped identically to every install). These ids are now
            // the key binding an entry to a legend stamped inside a shared model, so a value
            // that is identical across installs would make two users resolve to each other's
            // views. Safe to do unconditionally: an entry carrying a legacy id predates
            // stamping, so no document link can be broken by re-minting it.
            int reIded = 0;
            foreach (var e in Legends)
            {
                if (e == null || !LegendIdGen.IsLegacyId(e.Id)) continue;
                e.Id = LegendIdGen.New("legend");
                reIded++;
            }
            if (reIded > 0)
                DiagnosticsLog.Info("LegendCreatorSettings",
                    $"Re-minted {reIded} legend entry id(s) from the pre-GUID scheme.");
        }

        public static LegendCreatorSettings DefaultSeed() => new LegendCreatorSettings
        {
            Legends = new List<LegendEntry>
            {
                new LegendEntry
                {
                    // Minted, never hardcoded: this id binds the entry to a legend stamped
                    // inside a shared model, so a constant would be identical on every install.
                    Id = LegendIdGen.New("legend"),
                    Layout = new LegendLayoutConfig
                    {
                        Title    = "Filter Legend",
                        Subtitle = "",
                        ViewScale = 48,
                        SwatchW = 0.25, SwatchH = 0.13, FontPt = 9,
                        RowGap = 0.30, ColGap = 0.30, SwatchLabelGap = 0.08,
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

        private static TemplateStore<LegendCreatorSettings>? _templateStore;
        public static TemplateStore<LegendCreatorSettings> Templates =>
            _templateStore ?? (_templateStore = new TemplateStore<LegendCreatorSettings>(
                toolId:      "LegendCreator",
                serialize:   (data, path) => ExportTo(path, data),
                deserialize: path => TryLoad(path)));
    }

    // =========================================================================
    // Id helper
    // =========================================================================
    internal static class LegendIdGen
    {
        /// <summary>
        /// Mints a globally unique id for a legend entry, group, row or block.
        ///
        /// Previously this was <c>{prefix}_{Ticks % 100000}_{n}</c> off a counter that reset
        /// to zero on every Revit restart — so the first id of every session, on every
        /// machine, was drawn from a 100 000-value space and always ended <c>_1</c>. That was
        /// harmless while ids only ever indexed one user's own settings file, but these ids
        /// now bind entries to legends stamped inside a SHARED model: two users colliding on
        /// an id would resolve to each other's Revit views. A full GUID makes collision
        /// impossible rather than unlikely.
        /// </summary>
        public static string New(string prefix)
            => $"{prefix}_{Guid.NewGuid():N}";

        /// <summary>
        /// True for an id minted by the old scheme (or the hardcoded <c>legend_seed_1</c>
        /// that shipped identically to every install), which must be re-minted before it
        /// can be trusted as a document-wide key.
        /// </summary>
        public static bool IsLegacyId(string? id)
        {
            if (string.IsNullOrEmpty(id)) return true;
            int us = id!.LastIndexOf('_');
            if (us < 0) return true;
            // New ids carry a 32-char hex GUID after the final underscore.
            string tail = id.Substring(us + 1);
            if (tail.Length == 32) return false;
            return true;
        }
    }
}
