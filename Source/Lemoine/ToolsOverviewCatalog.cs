using System;
using System.Linq;

namespace LemoineTools.Lemoine
{
    // ─────────────────────────────────────────────────────────────────────────
    // Static, Revit-free catalogue that drives the Tools Overview window.
    //
    // This is the single source of truth for the on-screen guide: every tool's
    // blurb and its "feeds → / fed by ←" relationships live here, grouped into
    // the eight ribbon categories and the six workflow stages. The window
    // (ToolsOverviewWindow) only renders this data — it holds no copy of its own.
    //
    // Glyphs are Segoe MDL2 Assets codepoints, built with char.ConvertFromUtf32
    // so the source stays plain ASCII (Edit-tool safe — see CLAUDE.md).
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>One tool entry shown as a card in the overview.</summary>
    public sealed class OverviewTool
    {
        public string   Name    { get; set; } = "";
        public string   Glyph   { get; set; } = "";
        public string   Blurb   { get; set; } = "";
        /// <summary>Downstream tools/artifacts this one produces or enables.</summary>
        public string[] Feeds   { get; set; } = Array.Empty<string>();
        /// <summary>Upstream tools this one depends on / is paired with.</summary>
        public string[] FedBy   { get; set; } = Array.Empty<string>();
        /// <summary>A short concrete example, shown monospaced under the card.</summary>
        public string   Example { get; set; } = "";
    }

    /// <summary>One ribbon category (left-rail entry) holding its tools.</summary>
    public sealed class OverviewCategory
    {
        public string         Id    { get; set; } = "";
        public string         Name  { get; set; } = "";
        public string         Glyph { get; set; } = "";
        /// <summary>One-line intro shown above the cards.</summary>
        public string         Intro { get; set; } = "";
        public OverviewTool[] Tools { get; set; } = Array.Empty<OverviewTool>();
    }

    /// <summary>One workflow stage (top strip) spanning one or more categories.</summary>
    public sealed class OverviewStage
    {
        public string   Number      { get; set; } = "";
        public string   Name        { get; set; } = "";
        public string   Tagline     { get; set; } = "";
        /// <summary>Category ids that belong to this stage, in display order.</summary>
        public string[] CategoryIds { get; set; } = Array.Empty<string>();
    }

    public static class ToolsOverviewCatalog
    {
        private static string G(int cp) => char.ConvertFromUtf32(cp);

        // ── Categories (left rail order = ribbon panel order) ─────────────────
        public static readonly OverviewCategory[] Categories =
        {
            new OverviewCategory
            {
                Id = "filters", Name = "Filters & Legends", Glyph = G(0xE71C),
                Intro = "Define the trades/systems in your model as reusable view filters and a matching legend. "
                      + "These trades are the backbone several later tools build on, so this is usually run first.",
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = "Auto Filters", Glyph = G(0xE71C),
                        Blurb = "Discovers the trades in your model and generates one view filter per trade, then applies "
                              + "colour overrides across selected views. The trades it defines are reused — not re-typed — "
                              + "by Explode View by Trade, Clash and the legend.",
                        FedBy = new[] { "Discover (auto-detect rules)", "Apply to Views" },
                        Feeds = new[] { "Explode View by Trade", "Clash Definitions", "Ceiling Heatmap", "Legend Creation" },
                        Example = "\"Chilled Water Supply\" trade -> blue filter, reused as a clash group & legend swatch",
                    },
                    new OverviewTool
                    {
                        Name = "Legend Creation", Glyph = G(0xE713),
                        Blurb = "Builds, creates, and updates Legend views from the filter trades and colours, so the legend "
                              + "always matches exactly what the filters draw on the views.",
                        FedBy = new[] { "Auto Filters" },
                        Example = "regenerate a legend after adding a trade -> new swatch row appears automatically",
                    },
                },
            },

            new OverviewCategory
            {
                Id = "ceilings", Name = "Ceilings", Glyph = G(0xE81D),
                Intro = "Colour ceilings by height and build / project / refresh ceiling-grid DWGs.",
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = "Ceiling Heatmap", Glyph = G(0xE81D),
                        Blurb = "Colour-codes ceiling elevations by height using view-filter overrides — the same "
                              + "filter mechanism Auto Filters uses, so links set to \"By Host View\" are coloured too.",
                        FedBy = new[] { "Auto Filters (filter mechanism)" },
                        Example = "ceilings at 2700 / 3000 / 3300 mm -> three distinct colour bands",
                    },
                    new OverviewTool
                    {
                        Name = "Make Ceiling Grids", Glyph = G(0xE80A),
                        Blurb = "Creates an RCP view per level showing only ceilings and exports each as a DWG file — "
                              + "the starting point of the ceiling-grid round-trip.",
                        Feeds = new[] { "Project Grids" },
                        Example = "Level 02 RCP -> L02-Ceilings.dwg",
                    },
                    new OverviewTool
                    {
                        Name = "Project Grids", Glyph = G(0xE896),
                        Blurb = "Imports a DWG ceiling plan and projects its lines onto the ceiling soffit faces as model curves.",
                        FedBy = new[] { "Make Ceiling Grids" },
                        Feeds = new[] { "Reproject Grids" },
                        Example = "imported grid DWG -> model curves laid on the real soffit",
                    },
                    new OverviewTool
                    {
                        Name = "Reproject Grids", Glyph = G(0xE895),
                        Blurb = "Reprojects existing ceiling-grid model curves onto updated ceiling elevations after the "
                              + "design moves — no need to re-import.",
                        FedBy = new[] { "Project Grids" },
                        Example = "ceiling raised 150 mm -> existing grid lines follow it up",
                    },
                },
            },

            new OverviewCategory
            {
                Id = "views", Name = "Views", Glyph = G(0xE8B7),
                Intro = "Bulk-create and duplicate views — per level, across templates, as dependents, or one per trade.",
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = "Bulk Views by Level", Glyph = G(0xE8B7),
                        Blurb = "Creates cropped 3D, floor-plan, and ceiling-plan views for each level and building cluster "
                              + "in one pass.",
                        Feeds = new[] { "Place Dependent Views", "Sheets" },
                        Example = "5 levels x 2 clusters -> 30 cropped views",
                    },
                    new OverviewTool
                    {
                        Name = "Bulk Duplicate", Glyph = G(0xE8C8),
                        Blurb = "Duplicates selected views (Duplicate, With Detailing, or As Dependent) with token-based naming.",
                        Example = "{ViewName}-COORD -> one renamed copy per source view",
                    },
                    new OverviewTool
                    {
                        Name = "Bulk Views by Template", Glyph = G(0xE8A9),
                        Blurb = "Duplicates selected views across selected view templates, naming each from a token pattern — "
                              + "one view per template combination.",
                        Example = "3 views x 4 templates -> 12 named views",
                    },
                    new OverviewTool
                    {
                        Name = "Bulk Dependent Views", Glyph = G(0xE71B),
                        Blurb = "Copies dependent views and their crop regions from a source view onto one or more target views, "
                              + "so a crop layout is replicated rather than redrawn.",
                        Example = "source's 4 dependent crops -> cloned onto each target view",
                    },
                    new OverviewTool
                    {
                        Name = "Explode View by Trade", Glyph = G(0xE8A9),
                        Blurb = "Duplicates a 3D view once per Auto Filters trade at the same camera and section box, isolating "
                              + "each trade by its view filters and stacking the views by element elevation.",
                        FedBy = new[] { "Auto Filters (trades)" },
                        Example = "1 coordinated 3D view -> one isolated 3D per trade",
                    },
                },
            },

            new OverviewCategory
            {
                Id = "sheets", Name = "Sheets", Glyph = G(0xE7C3),
                Intro = "Place views on sheets, align matching viewports, and bulk-rename sheets or views.",
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = "Place Dependent Views", Glyph = G(0xE7C3),
                        Blurb = "Creates one sheet per view and places that view's dependents on it, each annotation-trimmed "
                              + "to its crop and packed without overlap.",
                        FedBy = new[] { "Views" },
                        Feeds = new[] { "Align Sheet Views", "Bulk Export" },
                        Example = "parent view + 6 dependents -> one sheet, packed",
                    },
                    new OverviewTool
                    {
                        Name = "Align Sheet Views", Glyph = G(0xE744),
                        Blurb = "Aligns the viewports on target sheets to the best-matching reference sheet so matching views "
                              + "overlay exactly — pairs by scope box (crop overlap otherwise) and can inherit scope box, "
                              + "grid extents, and crop.",
                        FedBy = new[] { "Place Dependent Views" },
                        Example = "MEP sheet viewports snapped to match the architectural reference",
                    },
                    new OverviewTool
                    {
                        Name = "Bulk Rename", Glyph = G(0xE8AC),
                        Blurb = "Bulk-renames sheets or views via find & replace, prefix/suffix, sequential numbering, or a "
                              + "token pattern — with duplicate-number/name pre-checks.",
                        Example = "A-101, A-102 ... -> M-101, M-102 ... in one pass",
                    },
                },
            },

            new OverviewCategory
            {
                Id = "export", Name = "Export", Glyph = G(0xEDE1),
                Intro = "Push finished sheets and views out to PDF, DWG, NWC, or IFC.",
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = "Bulk Export", Glyph = G(0xEDE1),
                        Blurb = "Exports sheets and views to PDF, DWG, NWC, or IFC in bulk with token-based filenames; only "
                              + "shows the formats valid for the chosen input (NWC/IFC need a 3D view).",
                        FedBy = new[] { "Sheets" },
                        Example = "{SheetNumber}-{SheetName}.pdf for every selected sheet",
                    },
                    new OverviewTool
                    {
                        Name = "Print View", Glyph = G(0xE749),
                        Blurb = "Exports just the active view or sheet to PDF using the same PDF settings as Bulk Export — a "
                              + "one-click single-output shortcut.",
                        Example = "active sheet -> one PDF, no picker",
                    },
                },
            },

            new OverviewCategory
            {
                Id = "modify", Name = "Modify", Glyph = G(0xE8C6),
                Intro = "Clean up host geometry — split elements along levels, grids, planes, or a cell grid, and extend walls.",
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = "Split by Levels", Glyph = G(0xE8C6),
                        Blurb = "Splits walls, columns, and MEP curves at selected level elevations.",
                        FedBy = new[] { "Copy from Link (host geometry)" },
                        Example = "a 3-storey wall -> one wall per level",
                    },
                    new OverviewTool
                    {
                        Name = "Split by Grid Lines", Glyph = G(0xE80A),
                        Blurb = "Splits walls and MEP curves at selected grid-plane intersections.",
                        Example = "a run crossing grids 1-4 -> four segments",
                    },
                    new OverviewTool
                    {
                        Name = "Split by Ref Plane", Glyph = G(0xE8C6),
                        Blurb = "Splits walls and MEP curves at selected reference-plane intersections.",
                        Example = "split a duct exactly at a reference plane",
                    },
                    new OverviewTool
                    {
                        Name = "Split by Cell", Glyph = G(0xE80A),
                        Blurb = "Splits floors, ceilings, and filled regions into a regular grid of cells.",
                        Example = "one floor -> a 2 m x 2 m grid of floor cells",
                    },
                    new OverviewTool
                    {
                        Name = "Extend Walls", Glyph = G(0xE898),
                        Blurb = "Sets the top constraint of walls that extend above the ceiling to the next level up.",
                        Example = "walls poking past the ceiling -> re-hosted to Level Above",
                    },
                },
            },

            new OverviewCategory
            {
                Id = "clash", Name = "Clash", Glyph = G(0xE8FD),
                Intro = "Detect interferences between two element groups, mark and colour them in plan or elevation, and "
                      + "dimension each clash to the nearest grid or slab edge. Definitions are the reusable library; the "
                      + "finders run them; Refine re-dimensions existing markers.",
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = "Clash Definitions", Glyph = G(0xE8FD),
                        Blurb = "Builds the reusable library: named clash definitions, each two element groups (by category / "
                              + "trade) plus marker colour and tag settings. Nothing runs here — it is the saved recipe every "
                              + "finder consumes.",
                        FedBy = new[] { "Auto Filters (trades)" },
                        Feeds = new[] { "Clash Finder & Dimension", "Clash Finder & Elevation" },
                        Example = "\"Duct vs Structure\" = { Ducts } x { Beams, Floors }, red round marker",
                    },
                    new OverviewTool
                    {
                        Name = "Clash Finder & Dimension", Glyph = G(0xE721),
                        Blurb = "Runs saved definitions across selected plan views: detects clashes, places coloured tagged "
                              + "markers, then dimensions each out to grids or slab edges. Surveys dense areas & user callouts "
                              + "and can change view scale to fit.",
                        FedBy = new[] { "Clash Definitions" },
                        Feeds = new[] { "Refine Dimensions", "Sheets" },
                        Example = "output: tagged markers + dimensions on each selected plan view",
                    },
                    new OverviewTool
                    {
                        Name = "Clash Finder & Elevation", Glyph = G(0xE898),
                        Blurb = "Same detection, but for sections and elevations: places round markers and tags each with a spot "
                              + "elevation at the top, centre, or bottom of the round — for vertical coordination views.",
                        FedBy = new[] { "Clash Definitions" },
                        Example = "output: round markers + spot-elevation tags in a section",
                    },
                    new OverviewTool
                    {
                        Name = "Refine Dimensions", Glyph = G(0xE70F),
                        Blurb = "For views that already carry clash markers: re-dimensions each marker to the nearest grid or "
                              + "slab edge. Never detects clashes, makes callouts, or changes view scale.",
                        FedBy = new[] { "Clash Finder & Dimension (existing markers)" },
                        Example = "use when markers exist but grids moved / dimensions need a redo",
                    },
                },
            },

            new OverviewCategory
            {
                Id = "copy", Name = "Copy from Link", Glyph = G(0xE71B),
                Intro = "Pull elements out of a linked model into the host so the downstream tools can act on real host "
                      + "geometry. Re-runs are idempotent — only changed elements are reprocessed.",
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = "Copy Linear", Glyph = G(0xE71B),
                        Blurb = "Copies linear linked runs into the host and splits them into standard lengths, or replaces them "
                              + "with a picked family at set intervals. Re-runs process only changed elements.",
                        Feeds = new[] { "Modify", "Clash" },
                        Example = "a linked pipe run -> host pipes in 6 m lengths, re-runnable",
                    },
                    new OverviewTool
                    {
                        Name = "Copy Grids", Glyph = G(0xE80A),
                        Blurb = "Copies grids from a linked model into the host. Grids whose name already exists in the host are "
                              + "skipped and logged (grid names must be unique).",
                        Example = "link grids A-F -> host, existing names skipped",
                    },
                    new OverviewTool
                    {
                        Name = "Copy Elements", Glyph = G(0xE8B3),
                        Blurb = "Copies elements from a linked model into the host: pick categories, then tick which family types "
                              + "within them to copy. Re-runs process only changed elements.",
                        Feeds = new[] { "Modify", "Clash" },
                        Example = "pick Mechanical Equipment -> tick the AHU types to pull in",
                    },
                },
            },
        };

        // ── Workflow stages (top strip order) ─────────────────────────────────
        public static readonly OverviewStage[] Stages =
        {
            new OverviewStage { Number = "01", Name = "Set Up",        Tagline = "Filters · Copy from Link", CategoryIds = new[] { "filters", "copy" } },
            new OverviewStage { Number = "02", Name = "Prep Geometry", Tagline = "Modify",                        CategoryIds = new[] { "modify" } },
            new OverviewStage { Number = "03", Name = "Build Views",   Tagline = "Views · Ceilings",         CategoryIds = new[] { "views", "ceilings" } },
            new OverviewStage { Number = "04", Name = "Coordinate",    Tagline = "Clash",                         CategoryIds = new[] { "clash" } },
            new OverviewStage { Number = "05", Name = "Document",      Tagline = "Sheets",                        CategoryIds = new[] { "sheets" } },
            new OverviewStage { Number = "06", Name = "Output",        Tagline = "Export",                        CategoryIds = new[] { "export" } },
        };

        /// <summary>The category for an id, or null if unknown.</summary>
        public static OverviewCategory? FindCategory(string id) =>
            Categories.FirstOrDefault(c => c.Id == id);

        /// <summary>The stage that owns a given category, or null.</summary>
        public static OverviewStage? StageForCategory(string categoryId) =>
            Stages.FirstOrDefault(s => s.CategoryIds.Contains(categoryId));
    }
}
