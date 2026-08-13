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

        /// <summary>
        /// When true the legend draws only the colours actually used in the view(s) it
        /// serves, instead of every filter in the library. See <see cref="SmartLegendScope"/>.
        /// </summary>
        [XmlAttribute] public bool SmartFilterEnabled { get; set; } = false;

        /// <summary>
        /// Explicit target views for smart filtering, by NAME. Empty means auto-detect from
        /// the sheet(s) the legend view is placed on.
        ///
        /// Names for the same reason the text styles above are names: this entry is
        /// serialized into the .rvt and into the shared seed library, and an ElementId means
        /// nothing outside its own document. Revit enforces View.Name uniqueness, so a name
        /// resolves deterministically within a document and travels between them.
        /// </summary>
        [XmlArray("SmartTargetViews"), XmlArrayItem("View")]
        public List<string> SmartTargetViewNames { get; set; } = new List<string>();

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
            SmartFilterEnabled   = SmartFilterEnabled,
            SmartTargetViewNames = new List<string>(SmartTargetViewNames ?? new List<string>()),
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
        // ── Legend library: one per project ──────────────────────────────────
        //
        // Legends describe what a MODEL should carry, so they belong to that model. Held once
        // for the whole install, every project showed the legends built for the last one.
        //
        // A project's first touch seeds from the static seed library (SeedLibrary); with no
        // seed it starts with a single blank legend, because this window's own invariant is
        // "never leave the window with zero legends". After that the project owns its copy.
        //
        // The Legends property keeps its name and shape, so all 20 call sites are unchanged.

        [XmlArray("LegendDocScopes"), XmlArrayItem("Doc")]
        public List<LegendDocScope> LegendDocScopes { get; set; } = new List<LegendDocScope>();

        /// <summary>
        /// Replaces the active document's legend library with what is stored IN THE DOCUMENT.
        /// An empty payload means the document has never carried one, so the bucket is left to
        /// seed itself — "" means "seed me", not "empty library".
        /// </summary>
        public static void LoadProjectLibrary(string? xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return;
            try
            {
                var xs = new XmlSerializer(typeof(LegendLibraryDto));
                using (var sr = new StringReader(xml))
                {
                    var dto = xs.Deserialize(sr) as LegendLibraryDto;
                    var legends = dto?.Legends ?? new List<LegendEntry>();
                    foreach (var e in legends)
                        if (e != null && string.IsNullOrEmpty(e.Id)) e.Id = LegendIdGen.New("legend");
                    // The window's invariant is never zero legends.
                    if (legends.Count == 0) legends.Add(BlankEntry());
                    var bucket = Instance.LegendScope();
                    bucket.Legends = legends;
                    bucket.Seeded  = true;
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("LegendCreatorSettings: read project legend library", ex);
            }
        }

        /// <summary>Serializes the active document's legend library for storage in the document.</summary>
        public static string SerializeProjectLibrary()
        {
            try
            {
                var xs = new XmlSerializer(typeof(LegendLibraryDto));
                using (var sw = new StringWriter())
                {
                    xs.Serialize(sw, new LegendLibraryDto { Legends = Instance.Legends });
                    return sw.ToString();
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("LegendCreatorSettings: serialize project legend library", ex);
                return "";
            }
        }

        /// <summary>Legend bucket for the active document, seeded on first touch.</summary>
        internal LegendDocScope LegendScope()
        {
            if (LegendDocScopes == null) LegendDocScopes = new List<LegendDocScope>();

            string k = DocumentKey.Current ?? "";
            foreach (var d in LegendDocScopes)
                if (d != null && string.Equals(d.Key, k, StringComparison.OrdinalIgnoreCase))
                {
                    d.Touched = DateTime.UtcNow.Ticks;
                    return d;
                }

            var made = new LegendDocScope { Key = k, Touched = DateTime.UtcNow.Ticks, Seeded = true };

            // Seed ONCE, on creation — re-seeding would overwrite the project's own edits.
            var seed = SeedLibrary.TryLoad<LegendLibraryDto>(
                SeedLibrary.LegendSeedFile, LegendLibraryDto.RootElement);
            var seeded = seed?.Legends;

            // Clone per entry: there is no DeepCopy overload for a list of entries, and the
            // seed must never be aliased into a project's own library.
            made.Legends = seeded != null && seeded.Count > 0
                ? seeded.ConvertAll(e => e.Clone())
                : new List<LegendEntry> { BlankEntry() };

            // Seed ids are PRESERVED, not re-minted. They key the legend-view stamp inside a
            // model, so two people working the same model from the same standard must agree
            // on them — re-minting per project would make each of them see the other's legend
            // as missing and create a duplicate. Only a seed entry with no id gets one.
            foreach (var e in made.Legends)
                if (e != null && string.IsNullOrEmpty(e.Id)) e.Id = LegendIdGen.New("legend");

            LegendDocScopes.Add(made);

            while (LegendDocScopes.Count > DocScoped.MaxDocuments)
            {
                int oldest = 0;
                for (int i = 1; i < LegendDocScopes.Count; i++)
                    if (LegendDocScopes[i].Touched < LegendDocScopes[oldest].Touched) oldest = i;
                LegendDocScopes.RemoveAt(oldest);
            }
            return made;
        }

        /// <summary>An empty legend slot — the blank starting point when there is no seed.</summary>
        internal static LegendEntry BlankEntry() => new LegendEntry
        {
            Id             = LegendIdGen.New("legend"),
            Layout         = new LegendLayoutConfig(),
            Rows           = new List<LegendRowConfig>(),
            PreviewVisible = true,
        };

        [XmlIgnore]
        public List<LegendEntry> Legends
        {
            get => LegendScope().Legends;
            set => LegendScope().Legends = value ?? new List<LegendEntry>();
        }

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

        /// <summary>
        /// Repairs every project's library after load. Walks the buckets directly rather than
        /// the Legends accessor: the accessor resolves (and would seed) only the active
        /// document, while these repairs must reach every project.
        ///
        /// The old top-level Layout + Rows migration is gone with the machine-wide library —
        /// legends are per project now, and a project is seeded from SeedLibrary on first touch.
        /// </summary>
        private void Normalize()
        {
            if (LegendDocScopes == null) LegendDocScopes = new List<LegendDocScope>();

            int reIded = 0;
            foreach (var bucket in LegendDocScopes)
            {
                if (bucket?.Legends == null) continue;
                foreach (var e in bucket.Legends)
                {
                    if (e == null) continue;
                    e.Layout?.Normalize();

                    // Re-mint ids from the old generator (and the hardcoded "legend_seed_1"
                    // that shipped identically to every install). These ids key legends
                    // stamped inside a shared model, so a value identical across installs
                    // would make two users resolve to each other's views.
                    if (LegendIdGen.IsLegacyId(e.Id))
                    {
                        e.Id = LegendIdGen.New("legend");
                        reIded++;
                    }
                }
            }
            if (reIded > 0)
                DiagnosticsLog.Info("LegendCreatorSettings",
                    $"Re-minted {reIded} legend entry id(s) from the pre-GUID scheme.");
        }

        /// <summary>
        /// A fresh settings file. Deliberately EMPTY: legends are per project, and each project
        /// is seeded from SeedLibrary — or a single blank legend — on first touch. Returning a
        /// populated library here would put the same legends into every project, which is the
        /// leak the per-project rework removes.
        /// </summary>
        public static LegendCreatorSettings DefaultSeed() => new LegendCreatorSettings();

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

    /// <summary>
    /// One project's legend library. Public for XmlSerializer — a non-public root type throws
    /// at serializer construction and fails silently inside the surrounding try/catch,
    /// stranding every setting on its default (see CLAUDE.md).
    /// </summary>
    /// <summary>
    /// A legend library with no document scoping: the format used for the static seed file.
    /// Keeping it separate from <see cref="LegendCreatorSettings"/> is what stops a shared
    /// seed carrying one machine's document keys. Public for XmlSerializer (a non-public
    /// root fails silently — see CLAUDE.md).
    /// </summary>
    [XmlRoot(LegendLibraryDto.RootElement)]
    public sealed class LegendLibraryDto
    {
        public const string RootElement = "LemoineLegendLibrary";

        [XmlArray("Legends"), XmlArrayItem("Legend")]
        public List<LegendEntry> Legends { get; set; } = new List<LegendEntry>();
    }

    public sealed class LegendDocScope
    {
        /// <summary>Document identity from <see cref="LemoineTools.Framework.DocumentKey"/>.
        /// Empty = the no-document slot.</summary>
        [XmlAttribute] public string Key { get; set; } = "";

        /// <summary>Ticks at last touch, for least-recently-used eviction.</summary>
        [XmlAttribute] public long Touched { get; set; }

        /// <summary>True once the seed has been applied, so it is never re-applied over edits.</summary>
        [XmlAttribute] public bool Seeded { get; set; }

        [XmlArray("Legends"), XmlArrayItem("Legend")]
        public List<LegendEntry> Legends { get; set; } = new List<LegendEntry>();
    }
}
