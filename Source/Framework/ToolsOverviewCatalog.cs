using System;
using System.Linq;

namespace LemoineTools.Framework
{
    // ─────────────────────────────────────────────────────────────────────────
    // Static, Revit-free catalogue that drives the Tools Overview window.
    //
    // This is the single source of truth for the on-screen guide: every tool's
    // blurb and its "feeds → / fed by ←" relationships live here, grouped into
    // the nine ribbon categories, in ribbon order (see App.cs's panel build
    // order). The window (ToolsOverviewWindow) only renders this data — it
    // holds no copy of its own.
    //
    // Glyphs are Segoe MDL2 Assets codepoints, built with char.ConvertFromUtf32
    // so the source stays plain ASCII (Edit-tool safe — see CLAUDE.md).
    //
    // A prior "workflow stage" grouping (six stages spanning the categories,
    // shown as a strip above a left category rail) was removed — the categories
    // already are the ribbon panels in ribbon order, so the stage layer was a
    // redundant indirection once the window collapsed to a single tab strip
    // driven directly by Categories (WS-11).
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

    /// <summary>One ribbon category (tab strip entry) holding its tools.</summary>
    public sealed class OverviewCategory
    {
        public string         Id    { get; set; } = "";
        public string         Name  { get; set; } = "";
        public string         Glyph { get; set; } = "";
        /// <summary>One-line intro shown above the cards.</summary>
        public string         Intro { get; set; } = "";
        public OverviewTool[] Tools { get; set; } = Array.Empty<OverviewTool>();
    }

    public static class ToolsOverviewCatalog
    {
        private static string G(int cp) => char.ConvertFromUtf32(cp);

        // ── Categories (tab order = ribbon panel order) ────────────────────────
        public static readonly OverviewCategory[] Categories =
        {
            new OverviewCategory
            {
                Id = "setup", Name = AppStrings.T("overview.cat.setup.name"), Glyph = G(0xE809),
                Intro = AppStrings.T("overview.cat.setup.intro"),
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.setup.tools.upgradeLinks.name"), Glyph = G(0xE896),
                        Blurb = AppStrings.T("overview.cat.setup.tools.upgradeLinks.blurb"),
                        Feeds = new[] { "Align Coordinates" },
                        Example = AppStrings.T("overview.cat.setup.tools.upgradeLinks.example"),
                    },
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.setup.tools.alignCoordinates.name"), Glyph = G(0xE809),
                        Blurb = AppStrings.T("overview.cat.setup.tools.alignCoordinates.blurb"),
                        FedBy = new[] { "Upgrade & Link Models" },
                        Feeds = new[] { "Push Coordinates to Links", "Copy from Link", "Dimensioning" },
                        Example = AppStrings.T("overview.cat.setup.tools.alignCoordinates.example"),
                    },
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.setup.tools.pushCoordinates.name"), Glyph = G(0xE896),
                        Blurb = AppStrings.T("overview.cat.setup.tools.pushCoordinates.blurb"),
                        FedBy = new[] { "Align Coordinates" },
                        Example = AppStrings.T("overview.cat.setup.tools.pushCoordinates.example"),
                    },
                },
            },

            new OverviewCategory
            {
                Id = "copy", Name = AppStrings.T("overview.cat.copy.name"), Glyph = G(0xE71B),
                Intro = AppStrings.T("overview.cat.copy.intro"),
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.copy.tools.linear.name"), Glyph = G(0xE71B),
                        Blurb = AppStrings.T("overview.cat.copy.tools.linear.blurb"),
                        Feeds = new[] { "Modify", "Dimensioning" },
                        Example = AppStrings.T("overview.cat.copy.tools.linear.example"),
                    },
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.copy.tools.grids.name"), Glyph = G(0xE80A),
                        Blurb = AppStrings.T("overview.cat.copy.tools.grids.blurb"),
                        Example = AppStrings.T("overview.cat.copy.tools.grids.example"),
                    },
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.copy.tools.elements.name"), Glyph = G(0xE8B3),
                        Blurb = AppStrings.T("overview.cat.copy.tools.elements.blurb"),
                        Feeds = new[] { "Modify", "Dimensioning" },
                        Example = AppStrings.T("overview.cat.copy.tools.elements.example"),
                    },
                },
            },

            new OverviewCategory
            {
                Id = "modify", Name = AppStrings.T("overview.cat.modify.name"), Glyph = G(0xE8C6),
                Intro = AppStrings.T("overview.cat.modify.intro"),
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.modify.tools.splitLevels.name"), Glyph = G(0xE8C6),
                        Blurb = AppStrings.T("overview.cat.modify.tools.splitLevels.blurb"),
                        FedBy = new[] { "Copy from Link (host geometry)" },
                        Example = AppStrings.T("overview.cat.modify.tools.splitLevels.example"),
                    },
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.modify.tools.splitGrid.name"), Glyph = G(0xE80A),
                        Blurb = AppStrings.T("overview.cat.modify.tools.splitGrid.blurb"),
                        Example = AppStrings.T("overview.cat.modify.tools.splitGrid.example"),
                    },
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.modify.tools.splitRefPlane.name"), Glyph = G(0xE8C6),
                        Blurb = AppStrings.T("overview.cat.modify.tools.splitRefPlane.blurb"),
                        Example = AppStrings.T("overview.cat.modify.tools.splitRefPlane.example"),
                    },
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.modify.tools.splitCell.name"), Glyph = G(0xE80A),
                        Blurb = AppStrings.T("overview.cat.modify.tools.splitCell.blurb"),
                        Example = AppStrings.T("overview.cat.modify.tools.splitCell.example"),
                    },
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.modify.tools.extendWalls.name"), Glyph = G(0xE898),
                        Blurb = AppStrings.T("overview.cat.modify.tools.extendWalls.blurb"),
                        Example = AppStrings.T("overview.cat.modify.tools.extendWalls.example"),
                    },
                },
            },

            new OverviewCategory
            {
                Id = "ceilings", Name = AppStrings.T("overview.cat.ceilings.name"), Glyph = G(0xE81D),
                Intro = AppStrings.T("overview.cat.ceilings.intro"),
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.ceilings.tools.heatmap.name"), Glyph = G(0xE81D),
                        Blurb = AppStrings.T("overview.cat.ceilings.tools.heatmap.blurb"),
                        FedBy = new[] { "Auto Filters (filter mechanism)" },
                        Example = AppStrings.T("overview.cat.ceilings.tools.heatmap.example"),
                    },
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.ceilings.tools.makeGrids.name"), Glyph = G(0xE80A),
                        Blurb = AppStrings.T("overview.cat.ceilings.tools.makeGrids.blurb"),
                        Feeds = new[] { "Project Grids" },
                        Example = AppStrings.T("overview.cat.ceilings.tools.makeGrids.example"),
                    },
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.ceilings.tools.projectGrids.name"), Glyph = G(0xE896),
                        Blurb = AppStrings.T("overview.cat.ceilings.tools.projectGrids.blurb"),
                        FedBy = new[] { "Make Ceiling Grids" },
                        Feeds = new[] { "Reproject Grids" },
                        Example = AppStrings.T("overview.cat.ceilings.tools.projectGrids.example"),
                    },
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.ceilings.tools.reprojectGrids.name"), Glyph = G(0xE895),
                        Blurb = AppStrings.T("overview.cat.ceilings.tools.reprojectGrids.blurb"),
                        FedBy = new[] { "Project Grids" },
                        Example = AppStrings.T("overview.cat.ceilings.tools.reprojectGrids.example"),
                    },
                },
            },

            new OverviewCategory
            {
                Id = "views", Name = AppStrings.T("overview.cat.views.name"), Glyph = G(0xE8B7),
                Intro = AppStrings.T("overview.cat.views.intro"),
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.views.tools.scopeBoxCreator.name"), Glyph = G(0xE7B8),
                        Blurb = AppStrings.T("overview.cat.views.tools.scopeBoxCreator.blurb"),
                        Feeds = new[] { "Bulk Views", "Scope Box Manager" },
                        Example = AppStrings.T("overview.cat.views.tools.scopeBoxCreator.example"),
                    },
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.views.tools.scopeBoxManager.name"), Glyph = G(0xE8FD),
                        Blurb = AppStrings.T("overview.cat.views.tools.scopeBoxManager.blurb"),
                        FedBy = new[] { "Scope Box Creator" },
                        Example = AppStrings.T("overview.cat.views.tools.scopeBoxManager.example"),
                    },
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.views.tools.bulkViews.name"), Glyph = G(0xE8A9),
                        Blurb = AppStrings.T("overview.cat.views.tools.bulkViews.blurb"),
                        FedBy = new[] { "Scope Box Creator" },
                        Feeds = new[] { "Place Dependent Views", "Sheets" },
                        Example = AppStrings.T("overview.cat.views.tools.bulkViews.example"),
                    },
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.views.tools.explode.name"), Glyph = G(0xE8A9),
                        Blurb = AppStrings.T("overview.cat.views.tools.explode.blurb"),
                        FedBy = new[] { "Auto Filters (trades)" },
                        Example = AppStrings.T("overview.cat.views.tools.explode.example"),
                    },
                },
            },

            new OverviewCategory
            {
                Id = "filters", Name = AppStrings.T("overview.cat.filters.name"), Glyph = G(0xE71C),
                Intro = AppStrings.T("overview.cat.filters.intro"),
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.filters.tools.autoFilters.name"), Glyph = G(0xE71C),
                        Blurb = AppStrings.T("overview.cat.filters.tools.autoFilters.blurb"),
                        FedBy = new[] { "Discover (auto-detect rules)", "Apply to Views" },
                        Feeds = new[] { "Explode View by Trade", "Clash Definitions", "Ceiling Heatmap", "Legend Creation" },
                        Example = AppStrings.T("overview.cat.filters.tools.autoFilters.example"),
                    },
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.filters.tools.legendCreation.name"), Glyph = G(0xE713),
                        Blurb = AppStrings.T("overview.cat.filters.tools.legendCreation.blurb"),
                        FedBy = new[] { "Auto Filters" },
                        Example = AppStrings.T("overview.cat.filters.tools.legendCreation.example"),
                    },
                },
            },

            new OverviewCategory
            {
                Id = "dimensioning", Name = AppStrings.T("overview.cat.dimensioning.name"), Glyph = G(0xE8FD),
                Intro = AppStrings.T("overview.cat.dimensioning.intro"),
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.dimensioning.tools.definitions.name"), Glyph = G(0xE8FD),
                        Blurb = AppStrings.T("overview.cat.dimensioning.tools.definitions.blurb"),
                        FedBy = new[] { "Auto Filters (trades)" },
                        Feeds = new[] { "Clash Finder & Dimension", "Clash Finder & Elevation" },
                        Example = AppStrings.T("overview.cat.dimensioning.tools.definitions.example"),
                    },
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.dimensioning.tools.finderDim.name"), Glyph = G(0xE721),
                        Blurb = AppStrings.T("overview.cat.dimensioning.tools.finderDim.blurb"),
                        FedBy = new[] { "Clash Definitions" },
                        Feeds = new[] { "Refine Dimensions", "Sheets" },
                        Example = AppStrings.T("overview.cat.dimensioning.tools.finderDim.example"),
                    },
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.dimensioning.tools.finderElev.name"), Glyph = G(0xE898),
                        Blurb = AppStrings.T("overview.cat.dimensioning.tools.finderElev.blurb"),
                        FedBy = new[] { "Clash Definitions" },
                        Example = AppStrings.T("overview.cat.dimensioning.tools.finderElev.example"),
                    },
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.dimensioning.tools.refine.name"), Glyph = G(0xE70F),
                        Blurb = AppStrings.T("overview.cat.dimensioning.tools.refine.blurb"),
                        FedBy = new[] { "Clash Finder & Dimension (existing markers)" },
                        Example = AppStrings.T("overview.cat.dimensioning.tools.refine.example"),
                    },
                },
            },

            new OverviewCategory
            {
                Id = "sheets", Name = AppStrings.T("overview.cat.sheets.name"), Glyph = G(0xE7C3),
                Intro = AppStrings.T("overview.cat.sheets.intro"),
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.sheets.tools.placeDependent.name"), Glyph = G(0xE7C3),
                        Blurb = AppStrings.T("overview.cat.sheets.tools.placeDependent.blurb"),
                        FedBy = new[] { "Bulk Views" },
                        Feeds = new[] { "Align Sheet Views", "Bulk Export" },
                        Example = AppStrings.T("overview.cat.sheets.tools.placeDependent.example"),
                    },
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.sheets.tools.alignViews.name"), Glyph = G(0xE744),
                        Blurb = AppStrings.T("overview.cat.sheets.tools.alignViews.blurb"),
                        FedBy = new[] { "Place Dependent Views" },
                        Example = AppStrings.T("overview.cat.sheets.tools.alignViews.example"),
                    },
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.sheets.tools.rename.name"), Glyph = G(0xE8AC),
                        Blurb = AppStrings.T("overview.cat.sheets.tools.rename.blurb"),
                        Example = AppStrings.T("overview.cat.sheets.tools.rename.example"),
                    },
                },
            },

            new OverviewCategory
            {
                Id = "export", Name = AppStrings.T("overview.cat.export.name"), Glyph = G(0xEDE1),
                Intro = AppStrings.T("overview.cat.export.intro"),
                Tools = new[]
                {
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.export.tools.bulkExport.name"), Glyph = G(0xEDE1),
                        Blurb = AppStrings.T("overview.cat.export.tools.bulkExport.blurb"),
                        FedBy = new[] { "Sheets" },
                        Example = AppStrings.T("overview.cat.export.tools.bulkExport.example"),
                    },
                    new OverviewTool
                    {
                        Name = AppStrings.T("overview.cat.export.tools.printView.name"), Glyph = G(0xE749),
                        Blurb = AppStrings.T("overview.cat.export.tools.printView.blurb"),
                        Example = AppStrings.T("overview.cat.export.tools.printView.example"),
                    },
                },
            },
        };

        /// <summary>The category for an id, or null if unknown.</summary>
        public static OverviewCategory? FindCategory(string id) =>
            Categories.FirstOrDefault(c => c.Id == id);
    }
}
