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
                Intro = "Set up the view filters and legend the rest of the workflow leans on. The trades you define "
                      + "here get reused by the view, ceiling and clash tools, so this is usually the first stop.",
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = "Auto Filters", Glyph = G(0xE71C),
                        Blurb = "Scans the linked models for the systems in use, builds one view filter per trade, and "
                              + "applies its color across the views you choose. Explode View by Trade, the clash tools and "
                              + "Ceiling Heatmap all reuse these trades instead of setting them up again.",
                        FedBy = new[] { "Discover (auto-detect rules)", "Apply to Views" },
                        Feeds = new[] { "Explode View by Trade", "Clash Definitions", "Ceiling Heatmap", "Legend Creation" },
                        Example = "\"Chilled Water Supply\" becomes a blue filter, reused as a clash group and a legend row",
                    },
                    new OverviewTool
                    {
                        Name = "Legend Creation", Glyph = G(0xE713),
                        Blurb = "Builds and updates a Legend view from the same filters, so the legend on the sheet always "
                              + "matches the colors on the views.",
                        FedBy = new[] { "Auto Filters" },
                        Example = "add a trade in Auto Filters, run this again, and the new swatch turns up in the legend",
                    },
                },
            },

            new OverviewCategory
            {
                Id = "ceilings", Name = "Ceilings", Glyph = G(0xE81D),
                Intro = "Color ceilings by height, and build, project or refresh the DWG ceiling grids.",
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = "Ceiling Heatmap", Glyph = G(0xE81D),
                        Blurb = "Colors each ceiling by its height using view-filter overrides, so high and low areas read "
                              + "at a glance. Because it works through filters, ceilings inside links set to \"By Host View\" "
                              + "get colored too.",
                        FedBy = new[] { "Auto Filters (filter mechanism)" },
                        Example = "ceilings at 2700, 3000 and 3300 mm fall into three color bands",
                    },
                    new OverviewTool
                    {
                        Name = "Make Ceiling Grids", Glyph = G(0xE80A),
                        Blurb = "Builds a reflected ceiling plan per level that shows only the ceilings, then exports each "
                              + "one to DWG. This is the first half of the ceiling-grid round trip.",
                        Feeds = new[] { "Project Grids" },
                        Example = "the Level 2 RCP exports to L02-Ceilings.dwg",
                    },
                    new OverviewTool
                    {
                        Name = "Project Grids", Glyph = G(0xE896),
                        Blurb = "Imports a ceiling-grid DWG and lays its lines onto the actual ceiling soffit faces as model curves.",
                        FedBy = new[] { "Make Ceiling Grids" },
                        Feeds = new[] { "Reproject Grids" },
                        Example = "an imported grid lands on the real soffit instead of a flat plane",
                    },
                    new OverviewTool
                    {
                        Name = "Reproject Grids", Glyph = G(0xE895),
                        Blurb = "Moves the existing grid curves onto the current ceiling heights after the design changes, "
                              + "so you don't have to import the DWG again.",
                        FedBy = new[] { "Project Grids" },
                        Example = "raise a ceiling 150 mm and its grid lines follow it up",
                    },
                },
            },

            new OverviewCategory
            {
                Id = "views", Name = "Views", Glyph = G(0xE8B7),
                Intro = "Create and duplicate views in bulk: per level, across templates, as dependents, or one per trade.",
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = "Bulk Views by Level", Glyph = G(0xE8B7),
                        Blurb = "Creates cropped 3D, floor plan and ceiling plan views for every level and building cluster "
                              + "in one go.",
                        Feeds = new[] { "Place Dependent Views", "Sheets" },
                        Example = "5 levels across 2 clusters gives 30 cropped views",
                    },
                    new OverviewTool
                    {
                        Name = "Bulk Duplicate", Glyph = G(0xE8C8),
                        Blurb = "Duplicates the views you pick (plain, with detailing, or as a dependent) and names the "
                              + "copies from a token pattern.",
                        Example = "{ViewName} - COORD names every copy in one pass",
                    },
                    new OverviewTool
                    {
                        Name = "Bulk Views by Template", Glyph = G(0xE8A9),
                        Blurb = "Duplicates each selected view once for every view template you pick, naming each result "
                              + "from a token pattern.",
                        Example = "3 views across 4 templates gives 12 named views",
                    },
                    new OverviewTool
                    {
                        Name = "Bulk Dependent Views", Glyph = G(0xE71B),
                        Blurb = "Copies a view's dependents and their crop regions onto other views, so you set a crop "
                              + "layout up once and reuse it instead of redrawing it.",
                        Example = "clone a source view's 4 dependent crops onto each target",
                    },
                    new OverviewTool
                    {
                        Name = "Explode View by Trade", Glyph = G(0xE8A9),
                        Blurb = "Copies a 3D view once per trade at the same camera angle and section box, isolates each "
                              + "copy to its trade using the Auto Filters filters, and stacks the copies by elevation.",
                        FedBy = new[] { "Auto Filters (trades)" },
                        Example = "one coordination view becomes a separate 3D for each trade",
                    },
                },
            },

            new OverviewCategory
            {
                Id = "sheets", Name = "Sheets", Glyph = G(0xE7C3),
                Intro = "Get views onto sheets, line up matching viewports, and rename sheets or views in bulk.",
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = "Place Dependent Views", Glyph = G(0xE7C3),
                        Blurb = "Makes one sheet per view and drops that view's dependents onto it, each trimmed to its "
                              + "crop and packed so nothing overlaps.",
                        FedBy = new[] { "Views" },
                        Feeds = new[] { "Align Sheet Views", "Bulk Export" },
                        Example = "a parent view and its 6 dependents land on a single packed sheet",
                    },
                    new OverviewTool
                    {
                        Name = "Align Sheet Views", Glyph = G(0xE744),
                        Blurb = "Lines the viewports on your sheets up against a reference sheet so matching views sit "
                              + "exactly on top of each other. It pairs views by scope box, or by crop overlap when there "
                              + "isn't one, and can inherit the scope box, grid extents and crop.",
                        FedBy = new[] { "Place Dependent Views" },
                        Example = "snap the MEP sheet's viewports to match the architectural reference",
                    },
                    new OverviewTool
                    {
                        Name = "Bulk Rename", Glyph = G(0xE8AC),
                        Blurb = "Renames sheets or views in bulk by find-and-replace, a prefix or suffix, a running number, "
                              + "or a token pattern. It checks for duplicate numbers and names and skips the clashes.",
                        Example = "turn A-101, A-102... into M-101, M-102... in one pass",
                    },
                },
            },

            new OverviewCategory
            {
                Id = "export", Name = "Export", Glyph = G(0xEDE1),
                Intro = "Send finished sheets and views out to PDF, DWG, NWC or IFC.",
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = "Bulk Export", Glyph = G(0xEDE1),
                        Blurb = "Exports the sheets and views you pick to PDF, DWG, NWC or IFC, naming each file from a "
                              + "token pattern. It only offers the formats that suit your selection, since NWC and IFC need "
                              + "a 3D view.",
                        FedBy = new[] { "Sheets" },
                        Example = "every sheet exports as {SheetNumber}-{SheetName}.pdf",
                    },
                    new OverviewTool
                    {
                        Name = "Print View", Glyph = G(0xE749),
                        Blurb = "Exports just the active view or sheet to PDF using the same settings as Bulk Export. One "
                              + "click, no picker.",
                        Example = "the active sheet becomes a single PDF",
                    },
                },
            },

            new OverviewCategory
            {
                Id = "modify", Name = "Modify", Glyph = G(0xE8C6),
                Intro = "Tidy up host geometry: split elements along levels, grids, reference planes or a cell grid, and push walls up to the next level.",
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = "Split by Levels", Glyph = G(0xE8C6),
                        Blurb = "Cuts walls, columns and MEP runs at the levels you choose.",
                        FedBy = new[] { "Copy from Link (host geometry)" },
                        Example = "a three-story wall becomes one wall per level",
                    },
                    new OverviewTool
                    {
                        Name = "Split by Grid Lines", Glyph = G(0xE80A),
                        Blurb = "Cuts walls and MEP runs where they cross the grid planes you pick.",
                        Example = "a run crossing grids 1 to 4 becomes four pieces",
                    },
                    new OverviewTool
                    {
                        Name = "Split by Ref Plane", Glyph = G(0xE8C6),
                        Blurb = "Cuts walls and MEP runs at the reference planes you choose.",
                        Example = "split a duct right on a reference plane",
                    },
                    new OverviewTool
                    {
                        Name = "Split by Cell", Glyph = G(0xE80A),
                        Blurb = "Cuts floors, ceilings and filled regions into an even grid of cells.",
                        Example = "one floor becomes a 2 m by 2 m grid of cells",
                    },
                    new OverviewTool
                    {
                        Name = "Extend Walls", Glyph = G(0xE898),
                        Blurb = "Finds walls that run past the ceiling and sets their top constraint to the next level up.",
                        Example = "walls poking through the ceiling get re-hosted to the level above",
                    },
                },
            },

            new OverviewCategory
            {
                Id = "clash", Name = "Clash", Glyph = G(0xE8FD),
                Intro = "Find where two groups of elements run into each other, mark and color them in plan or section, "
                      + "and dimension each one to the nearest grid or slab edge. Definitions hold the rules, the finders "
                      + "run them, and Refine just redoes the dimensions.",
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = "Clash Definitions", Glyph = G(0xE8FD),
                        Blurb = "Where you build the named clash rules: two groups of elements, by category or trade, plus "
                              + "how the clash gets marked. Nothing runs here. The finders read the definitions you save.",
                        FedBy = new[] { "Auto Filters (trades)" },
                        Feeds = new[] { "Clash Finder & Dimension", "Clash Finder & Elevation" },
                        Example = "Duct vs Structure pairs ducts against beams and floors with a red marker",
                    },
                    new OverviewTool
                    {
                        Name = "Clash Finder & Dimension", Glyph = G(0xE721),
                        Blurb = "Runs your saved definitions across the plan views you pick: finds the clashes, drops a "
                              + "colored tagged marker on each, and dimensions them to the nearest grid or slab edge. It can "
                              + "survey busy areas and rescale views to fit.",
                        FedBy = new[] { "Clash Definitions" },
                        Feeds = new[] { "Refine Dimensions", "Sheets" },
                        Example = "each plan view comes back with tagged markers and dimensions",
                    },
                    new OverviewTool
                    {
                        Name = "Clash Finder & Elevation", Glyph = G(0xE898),
                        Blurb = "The same detection for sections and elevations. It marks each clash with a round marker and "
                              + "tags it with a spot elevation at the top, middle or bottom of the round.",
                        FedBy = new[] { "Clash Definitions" },
                        Example = "a section comes back with round markers and spot-elevation tags",
                    },
                    new OverviewTool
                    {
                        Name = "Refine Dimensions", Glyph = G(0xE70F),
                        Blurb = "Takes views that already have clash markers and redoes the dimensions to the nearest grid "
                              + "or slab edge. It never finds clashes, adds callouts or changes the view scale.",
                        FedBy = new[] { "Clash Finder & Dimension (existing markers)" },
                        Example = "use it when the markers are fine but the grid moved",
                    },
                },
            },

            new OverviewCategory
            {
                Id = "copy", Name = "Copy from Link", Glyph = G(0xE71B),
                Intro = "Pull elements out of a linked model into the host so the other tools can work on real host "
                      + "geometry. Run any of these again and only what changed gets redone.",
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = "Copy Linear", Glyph = G(0xE71B),
                        Blurb = "Copies linear runs out of a link into the host and either splits them into standard lengths "
                              + "or swaps in a family at set spacing. Run it again and only the changed runs get redone.",
                        Feeds = new[] { "Modify", "Clash" },
                        Example = "a linked pipe run comes in as host pipes in 6 m lengths",
                    },
                    new OverviewTool
                    {
                        Name = "Copy Grids", Glyph = G(0xE80A),
                        Blurb = "Copies grids from a link into the host. Any grid whose name already exists is skipped and "
                              + "noted, since grid names have to be unique.",
                        Example = "link grids A to F come across, and existing names are left alone",
                    },
                    new OverviewTool
                    {
                        Name = "Copy Elements", Glyph = G(0xE8B3),
                        Blurb = "Copies elements from a link into the host: pick the categories, then tick the family types "
                              + "you want. Run it again and only the changed elements get redone.",
                        Feeds = new[] { "Modify", "Clash" },
                        Example = "pick Mechanical Equipment, then tick the AHU types to bring in",
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
