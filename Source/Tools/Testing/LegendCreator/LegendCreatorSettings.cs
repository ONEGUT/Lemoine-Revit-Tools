using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Lemoine.Templates;

namespace LemoineTools.Tools.Testing.LegendCreator
{
    // =========================================================================
    // T08 — Legend Creator domain model
    //
    // Stored as XML at  %AppData%\LemoineTools\LegendCreatorSettings.xml
    //
    // Hierarchy:
    //   LegendCreatorSettings
    //     ├── Layout            — Title / Subtitle / dims / font / gap
    //     ├── PreviewVisible    — side-panel preview toggle (persisted)
    //     └── Rows[]            — vertical rows
    //           └── Groups[]    — groups inside a row
    //                 └── Blocks[]  — single entries (one swatch+label per row)
    //
    // Palette is a *live* mirror of AutoFiltersSettings.Trades — categories =
    // Trades, palette filters = Rules. Nothing about the palette is stored
    // here; only the user's chosen rows/groups/blocks.
    // =========================================================================

    /// <summary>
    /// Layout-bar settings: titles + swatch dims + font / gap.
    /// </summary>
    public sealed class LegendLayoutConfig
    {
        /// <summary>Main heading displayed at the top of the rendered legend.</summary>
        [XmlAttribute] public string Title    { get; set; } = "Filter Legend";

        /// <summary>Optional secondary line shown beneath <see cref="Title"/>. Empty by default.</summary>
        [XmlAttribute] public string Subtitle { get; set; } = "";

        /// <summary>Revit view scale denominator (1 : ViewScale). 48 = 1/4" = 1'-0".</summary>
        [XmlAttribute] public int    ViewScale { get; set; } = 48;

        /// <summary>Swatch rectangle width in inches (paper/sheet at the configured view scale).</summary>
        [XmlAttribute] public double SwatchW  { get; set; } = 0.25;

        /// <summary>Swatch rectangle height in inches (paper/sheet at the configured view scale).</summary>
        [XmlAttribute] public double SwatchH  { get; set; } = 0.13;

        /// <summary>Label font size in points.</summary>
        [XmlAttribute] public int    FontPt   { get; set; } = 9;

        /// <summary>Horizontal gap in inches between the swatch and its label.</summary>
        [XmlAttribute] public double Gap      { get; set; } = 0.08;

        /// <summary>
        /// Migrates from old pixel-based values (SwatchW=22, SwatchH=14, Gap=6) to the
        /// new inch-based system. Called by <see cref="LegendCreatorSettings"/> after
        /// deserialising so existing save files silently reset to the new defaults.
        /// </summary>
        public void Normalize()
        {
            if (SwatchW > 5 || SwatchH > 5 || Gap > 3)
            {
                SwatchW = 0.25; SwatchH = 0.13; Gap = 0.08;
            }
            if (ViewScale <= 0) ViewScale = 48;
        }

        /// <summary>Legend view scale denominator (e.g. 48 = 1:48).</summary>
        [XmlAttribute] public int    ViewScale { get; set; } = 48;

        /// <summary>Returns a shallow clone of this layout configuration.</summary>
        public LegendLayoutConfig Clone() => new LegendLayoutConfig
        {
            Title = Title, Subtitle = Subtitle,
            ViewScale = ViewScale,
            SwatchW = SwatchW, SwatchH = SwatchH,
            FontPt = FontPt, Gap = Gap,
            ViewScale = ViewScale,
        };
    }

    /// <summary>
    /// A single horizontal band of groups.
    /// </summary>
    public sealed class LegendRowConfig
    {
        /// <summary>Stable runtime identifier for this row.</summary>
        [XmlAttribute] public string Id { get; set; } = "";

        /// <summary>Ordered list of category groups displayed left-to-right within this row.</summary>
        [XmlArray("Groups"), XmlArrayItem("Group")]
        public List<LegendGroupConfig> Groups { get; set; } = new List<LegendGroupConfig>();

        /// <summary>Returns a deep clone of this row, including all child groups and blocks.</summary>
        public LegendRowConfig Clone() => new LegendRowConfig
        {
            Id     = Id,
            Groups = Groups.ConvertAll(g => g.Clone()),
        };
    }

    /// <summary>
    /// A category card inside a row. Color is *not* user-overridable — it's
    /// derived from the source Trade's Color (or a neutral default when Custom).
    /// </summary>
    public sealed class LegendGroupConfig
    {
        /// <summary>Stable runtime identifier for this group.</summary>
        [XmlAttribute] public string Id            { get; set; } = "";

        /// <summary>Display heading shown at the top of the group card.</summary>
        [XmlAttribute] public string Title         { get; set; } = "";

        /// <summary>
        /// Id of the AutoFilters Trade that this group mirrors.
        /// Empty string indicates a custom (user-authored) group with no palette source.
        /// </summary>
        [XmlAttribute] public string SourceTradeId { get; set; } = "";   // "" = Custom group

        /// <summary>When <see langword="true"/> the group's block list is hidden in the UI.</summary>
        [XmlAttribute] public bool   Collapsed     { get; set; } = false;

        /// <summary>Ordered list of legend entries (swatches) inside this group.</summary>
        [XmlArray("Blocks"), XmlArrayItem("Block")]
        public List<LegendBlockConfig> Blocks { get; set; } = new List<LegendBlockConfig>();

        /// <summary>Returns a deep clone of this group, including all child blocks.</summary>
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
    /// Color/Name pull from the source Rule unless explicitly overridden.
    /// </summary>
    public sealed class LegendBlockConfig
    {
        /// <summary>Stable runtime identifier for this block.</summary>
        [XmlAttribute] public string Id            { get; set; } = "";

        /// <summary>Display label for this legend entry. Pulled from the source rule when <see cref="NameOverride"/> is <see langword="false"/>.</summary>
        [XmlAttribute] public string Name          { get; set; } = "";

        /// <summary>When <see langword="true"/>, <see cref="Name"/> is user-supplied rather than mirrored from the source rule.</summary>
        [XmlAttribute] public bool   NameOverride  { get; set; } = false;

        /// <summary>Id of the AutoFilters Trade that is the source of this block. Empty when <see cref="Custom"/> is <see langword="true"/>.</summary>
        [XmlAttribute] public string SourceTradeId { get; set; } = ""; // "" if Custom

        /// <summary>Id of the AutoFilters Rule that this block represents. Empty when <see cref="Custom"/> is <see langword="true"/>.</summary>
        [XmlAttribute] public string SourceRuleId  { get; set; } = ""; // "" if Custom

        /// <summary>Swatch fill color as a hex string (#RRGGBB). Defaults to neutral grey for custom blocks.</summary>
        [XmlAttribute] public string Color         { get; set; } = "#888888";

        /// <summary>When <see langword="true"/>, <see cref="Color"/> is user-supplied rather than derived from the source rule.</summary>
        [XmlAttribute] public bool   ColorOverride { get; set; } = false;

        /// <summary>Swatch fill pattern: <c>solid</c>, <c>hatch</c>, or <c>dots</c>.</summary>
        [XmlAttribute] public string Fill          { get; set; } = "solid";   // solid|hatch|dots

        /// <summary>Swatch shape: <c>square</c>, <c>circle</c>, <c>tri</c>, <c>line</c>, or <c>dash</c>.</summary>
        [XmlAttribute] public string Kind          { get; set; } = "square";  // square|circle|tri|line|dash

        /// <summary>When <see langword="false"/>, the block is hidden in the rendered legend.</summary>
        [XmlAttribute] public bool   Visible       { get; set; } = true;

        /// <summary>When <see langword="true"/>, this block was created by the user and has no palette source.</summary>
        [XmlAttribute] public bool   Custom        { get; set; } = false;

        /// <summary>Returns a shallow clone of this block configuration.</summary>
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
    // Singleton — XML-backed
    // =========================================================================

    /// <summary>
    /// XML-backed singleton that stores the full Legend Creator configuration,
    /// including layout options and the user-arranged row/group/block hierarchy.
    /// Persisted to <c>%AppData%\LemoineTools\LegendCreatorSettings.xml</c>.
    /// </summary>
    [XmlRoot("LegendCreatorSettings")]
    public sealed class LegendCreatorSettings
    {
        // ── Persisted ────────────────────────────────────────────────────────

        /// <summary>Title, subtitle, swatch dimensions, font size, and gap for the legend layout bar.</summary>
        [XmlElement("Layout")]
        public LegendLayoutConfig Layout { get; set; } = new LegendLayoutConfig();

        /// <summary>Whether the live preview panel is currently visible. Persisted across sessions.</summary>
        [XmlAttribute] public bool PreviewVisible { get; set; } = true;

        /// <summary>Ordered list of horizontal row bands that make up the legend canvas.</summary>
        [XmlArray("Rows"), XmlArrayItem("Row")]
        public List<LegendRowConfig> Rows { get; set; } = new List<LegendRowConfig>();

        // ── Lazy singleton ──────────────────────────────────────────────────
        private static readonly Lazy<LegendCreatorSettings> _lazy =
            new Lazy<LegendCreatorSettings>(LoadFromDisk);

        /// <summary>Gets the process-wide singleton, loading from disk on first access.</summary>
        public static LegendCreatorSettings Instance => _lazy.Value;

        /// <summary>Parameterless constructor required by <see cref="System.Xml.Serialization.XmlSerializer"/> and <see cref="DefaultSeed"/>.</summary>
        public LegendCreatorSettings() { }

        // ── Events ──────────────────────────────────────────────────────────
        /// <summary>Raised after Save() persists. Subscribers can refresh views.</summary>
        public static event Action? Saved;

        // ─────────────────────────────────────────────────────────────────────
        // Persistence
        // ─────────────────────────────────────────────────────────────────────
        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                try { Directory.CreateDirectory(dir); } catch { }
                return Path.Combine(dir, "LegendCreatorSettings.xml");
            }
        }

        /// <summary>
        /// Serializes the current settings to disk and fires <see cref="Saved"/>.
        /// Failures are logged to the debug output and silently swallowed so the UI is never blocked.
        /// </summary>
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
                        result.Layout?.Normalize(); // migrate old pixel-based values
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

        /// <summary>
        /// First-run / fallback layout — a single row with one placeholder group
        /// containing three example blocks. Replaced as soon as the user drags
        /// anything from the palette.
        /// </summary>
        public static LegendCreatorSettings DefaultSeed() => new LegendCreatorSettings
        {
            Layout = new LegendLayoutConfig
            {
                Title    = "Filter Legend",
                Subtitle = "",
                ViewScale = 48,
                SwatchW  = 0.25, SwatchH = 0.13, FontPt = 9, Gap = 0.08,
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
                                new LegendBlockConfig
                                {
                                    Id = "b_seed_1", Name = "Example 1",
                                    Color = "#8c8c8c", Kind = "square", Fill = "solid",
                                    Custom = true, Visible = true,
                                },
                                new LegendBlockConfig
                                {
                                    Id = "b_seed_2", Name = "Example 2",
                                    Color = "#8c8c8c", Kind = "square", Fill = "hatch",
                                    Custom = true, Visible = true,
                                },
                                new LegendBlockConfig
                                {
                                    Id = "b_seed_3", Name = "Example 3",
                                    Color = "#8c8c8c", Kind = "square", Fill = "dots",
                                    Custom = true, Visible = true,
                                },
                            },
                        },
                    },
                },
            },
        };

        // ─────────────────────────────────────────────────────────────────────
        // DeepCopy helpers — used by tab content to build an editing buffer
        // that the user can mutate, then commit on Apply.
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Returns a deep copy of <paramref name="rows"/>, cloning each row and all of its
        /// descendant groups and blocks. Used by tab content to build an editing buffer.
        /// </summary>
        /// <param name="rows">The source row list to copy.</param>
        /// <returns>A new list containing independent clones of every row.</returns>
        public static List<LegendRowConfig> DeepCopy(List<LegendRowConfig> rows)
        {
            var list = new List<LegendRowConfig>(rows.Count);
            for (int i = 0; i < rows.Count; i++) list.Add(rows[i].Clone());
            return list;
        }

        /// <summary>
        /// Returns a deep copy of <paramref name="layout"/>. Used alongside
        /// <see cref="DeepCopy(List{LegendRowConfig})"/> to snapshot the full settings state
        /// before the user begins editing.
        /// </summary>
        /// <param name="layout">The layout configuration to clone.</param>
        /// <returns>An independent clone of <paramref name="layout"/>.</returns>
        public static LegendLayoutConfig DeepCopy(LegendLayoutConfig layout) => layout.Clone();

        // ─────────────────────────────────────────────────────────────────────
        // Template store
        // ─────────────────────────────────────────────────────────────────────
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
                    return (LegendCreatorSettings)xs.Deserialize(r)!;
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
    // Id helper — short stable ids for runtime use
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
