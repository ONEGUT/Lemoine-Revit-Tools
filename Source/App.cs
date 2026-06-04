using System.Reflection;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.AutoFilters;
using LemoineTools.Tools.Ceilings;
using LemoineTools.Tools.LinkViews;
using LemoineTools.Tools.ModifyElements;
using LemoineTools.Tools.BulkExport;
using LemoineTools.Tools.Clash;
using LemoineTools.Tools.Testing.LegendCreator;
using LemoineTools.Tools.Testing;
using System;

namespace LemoineTools
{
    public class App : IExternalApplication
    {
        internal static CeilingGridEventHandler? ProjectHandler   { get; private set; }
        internal static ExternalEvent?           ProjectEvent     { get; private set; }
        internal static CeilingGridEventHandler? ReprojectHandler { get; private set; }
        internal static ExternalEvent?           ReprojectEvent   { get; private set; }
        internal static CeilingHeatmapEventHandler? CeilingHeatmapHandler { get; private set; }
        internal static ExternalEvent?              CeilingHeatmapEvent   { get; private set; }
        internal static CeilingHeatmapDebugHandler? CeilingHeatmapDebugHandler { get; private set; }
        internal static ExternalEvent?              CeilingHeatmapDebugEvent   { get; private set; }

        // ── Make Ceiling Grids ──────────────────────────────────────────────────
        internal static MakeCeilingGridsPhase1Handler? MakeCeilingGridsPhase1Handler { get; private set; }
        internal static ExternalEvent?                 MakeCeilingGridsPhase1Event   { get; private set; }
        internal static MakeCeilingGridsRunHandler?    MakeCeilingGridsRunHandler    { get; private set; }
        internal static ExternalEvent?                 MakeCeilingGridsRunEvent      { get; private set; }

        // ── Discover Rules ──────────────────────────────────────────────────────────
        internal static DiscoverEventHandler? DiscoverHandler { get; private set; }
        internal static ExternalEvent?        DiscoverEvent   { get; private set; }

        // ── Auto Filters ────────────────────────────────────────────────────────────
        internal static AutoFiltersEventHandler?              AutoFiltersHandler              { get; private set; }
        internal static ExternalEvent?                        AutoFiltersEvent                { get; private set; }
        internal static AutoFiltersLegendEventHandler?        AutoFiltersLegendHandler        { get; private set; }
        internal static ExternalEvent?                        AutoFiltersLegendEvent          { get; private set; }
        internal static LegendCreatorEventHandler?            LegendCreatorHandler            { get; private set; }
        internal static ExternalEvent?                        LegendCreatorEvent              { get; private set; }
        internal static DeleteFiltersEventHandler?            DeleteFiltersHandler            { get; private set; }
        internal static ExternalEvent?                        DeleteFiltersEvent              { get; private set; }
        internal static ApplyFiltersToViewsEventHandler?      ApplyFiltersToViewsHandler      { get; private set; }
        internal static ExternalEvent?                        ApplyFiltersToViewsEvent        { get; private set; }
        internal static DeleteFiltersFromProjectEventHandler? DeleteFiltersFromProjectHandler { get; private set; }
        internal static ExternalEvent?                        DeleteFiltersFromProjectEvent   { get; private set; }

        // ── Link Views — Level ──────────────────────────────────────────────────────
        internal static LinkViewsLevelPhase1Handler? LinkViewsLevelPhase1Handler { get; private set; }
        internal static ExternalEvent?               LinkViewsLevelPhase1Event   { get; private set; }
        internal static LinkViewsLevelRunHandler?    LinkViewsLevelRunHandler    { get; private set; }
        internal static ExternalEvent?               LinkViewsLevelRunEvent      { get; private set; }

        // ── Link Views — Discipline ─────────────────────────────────────────────────
        internal static LinkViewsDisciplineRunHandler? LinkViewsDisciplineRunHandler { get; private set; }
        internal static ExternalEvent?                 LinkViewsDisciplineRunEvent   { get; private set; }

        // ── Replicate Dependent Views ───────────────────────────────────────────────
        internal static ReplicateDependentViewsRunHandler? ReplicateDependentViewsRunHandler { get; private set; }
        internal static ExternalEvent?                     ReplicateDependentViewsRunEvent   { get; private set; }

        // ── T03 — Bulk Export ───────────────────────────────────────────────────────
        internal static BulkExportEventHandler?   BulkExportHandler   { get; private set; }
        internal static ExternalEvent?             BulkExportEvent     { get; private set; }

        // ── T05 — Clash (Definitions + Finder & Dimensioning) ───────────────────────
        internal static ClashPickEventHandler?      ClashPickHandler      { get; private set; }
        internal static ExternalEvent?              ClashPickEvent        { get; private set; }
        internal static ClashFinderEventHandler?    ClashFinderHandler    { get; private set; }
        internal static ExternalEvent?              ClashFinderEvent      { get; private set; }
        internal static ClashElevationFinderEventHandler? ClashElevationFinderHandler { get; private set; }
        internal static ExternalEvent?              ClashElevationFinderEvent   { get; private set; }
        internal static LemoineTools.Tools.Clash.AutoDimension.SlabPickEventHandler? SlabPickHandler { get; private set; }
        internal static ExternalEvent?              SlabPickEvent         { get; private set; }

        // ── Testing — Create Sheets ─────────────────────────────────────────────────
        internal static CreateSheetsEventHandler?  CreateSheetsHandler  { get; private set; }
        internal static ExternalEvent?             CreateSheetsEvent    { get; private set; }


        // ── Modify Elements ─────────────────────────────────────────────────────────
        internal static SplitByLevelEventHandler?          SplitByLevelHandler          { get; private set; }
        internal static ExternalEvent?                     SplitByLevelEvent            { get; private set; }
        internal static SplitByGridEventHandler?           SplitByGridHandler           { get; private set; }
        internal static ExternalEvent?                     SplitByGridEvent             { get; private set; }
        internal static SplitByCellEventHandler?           SplitByCellHandler           { get; private set; }
        internal static ExternalEvent?                     SplitByCellEvent             { get; private set; }
        internal static SplitByReferencePlaneEventHandler? SplitByReferencePlaneHandler { get; private set; }
        internal static ExternalEvent?                     SplitByReferencePlaneEvent   { get; private set; }
        internal static ExtendWallsEventHandler?           ExtendWallsHandler           { get; private set; }
        internal static ExternalEvent?                     ExtendWallsEvent             { get; private set; }

        // Global settings window — singleton, stays open across tool windows
        internal static GlobalSettingsWindow? GlobalSettings { get; set; }

        public Result OnStartup(UIControlledApplication application)
        {
            ProjectHandler   = new CeilingGridEventHandler();
            ProjectEvent     = ExternalEvent.Create(ProjectHandler);
            ReprojectHandler = new CeilingGridEventHandler();
            ReprojectEvent   = ExternalEvent.Create(ReprojectHandler);
            CeilingHeatmapHandler = new CeilingHeatmapEventHandler();
            CeilingHeatmapEvent   = ExternalEvent.Create(CeilingHeatmapHandler);
            CeilingHeatmapDebugHandler = new CeilingHeatmapDebugHandler();
            CeilingHeatmapDebugEvent   = ExternalEvent.Create(CeilingHeatmapDebugHandler);
            CeilingHeatmapViewModel.RegisterDebugEvent(CeilingHeatmapDebugHandler, CeilingHeatmapDebugEvent);

            // ── Make Ceiling Grids ────────────────────────────────────────────
            MakeCeilingGridsPhase1Handler = new MakeCeilingGridsPhase1Handler();
            MakeCeilingGridsPhase1Event   = ExternalEvent.Create(MakeCeilingGridsPhase1Handler);
            MakeCeilingGridsRunHandler    = new MakeCeilingGridsRunHandler();
            MakeCeilingGridsRunEvent      = ExternalEvent.Create(MakeCeilingGridsRunHandler);

            // ── Discover Rules ────────────────────────────────────────────────
            DiscoverHandler = new DiscoverEventHandler();
            DiscoverEvent   = ExternalEvent.Create(DiscoverHandler);

            // ── Auto Filters suite ────────────────────────────────────────────
            AutoFiltersHandler              = new AutoFiltersEventHandler();
            AutoFiltersEvent                = ExternalEvent.Create(AutoFiltersHandler);
            AutoFiltersLegendHandler        = new AutoFiltersLegendEventHandler();
            AutoFiltersLegendEvent          = ExternalEvent.Create(AutoFiltersLegendHandler);
            LegendCreatorHandler            = new LegendCreatorEventHandler();
            LegendCreatorEvent              = ExternalEvent.Create(LegendCreatorHandler);
            DeleteFiltersHandler            = new DeleteFiltersEventHandler();
            DeleteFiltersEvent              = ExternalEvent.Create(DeleteFiltersHandler);
            ApplyFiltersToViewsHandler      = new ApplyFiltersToViewsEventHandler();
            ApplyFiltersToViewsEvent        = ExternalEvent.Create(ApplyFiltersToViewsHandler);
            DeleteFiltersFromProjectHandler = new DeleteFiltersFromProjectEventHandler();
            DeleteFiltersFromProjectEvent   = ExternalEvent.Create(DeleteFiltersFromProjectHandler);

            // ── Link Views — Level ────────────────────────────────────────────
            LinkViewsLevelPhase1Handler = new LinkViewsLevelPhase1Handler();
            LinkViewsLevelPhase1Event   = ExternalEvent.Create(LinkViewsLevelPhase1Handler);
            LinkViewsLevelRunHandler    = new LinkViewsLevelRunHandler();
            LinkViewsLevelRunEvent      = ExternalEvent.Create(LinkViewsLevelRunHandler);

            // ── Link Views — Discipline ───────────────────────────────────────
            LinkViewsDisciplineRunHandler = new LinkViewsDisciplineRunHandler();
            LinkViewsDisciplineRunEvent   = ExternalEvent.Create(LinkViewsDisciplineRunHandler);

            // ── Replicate Dependent Views ─────────────────────────────────────
            ReplicateDependentViewsRunHandler = new ReplicateDependentViewsRunHandler();
            ReplicateDependentViewsRunEvent   = ExternalEvent.Create(ReplicateDependentViewsRunHandler);

            // ── Testing — new tools ───────────────────────────────────────────
            BulkExportHandler   = new BulkExportEventHandler();
            BulkExportEvent     = ExternalEvent.Create(BulkExportHandler);
            ClashPickHandler      = new ClashPickEventHandler();
            ClashPickEvent        = ExternalEvent.Create(ClashPickHandler);
            ClashFinderHandler    = new ClashFinderEventHandler();
            ClashFinderEvent      = ExternalEvent.Create(ClashFinderHandler);
            ClashElevationFinderHandler = new ClashElevationFinderEventHandler();
            ClashElevationFinderEvent   = ExternalEvent.Create(ClashElevationFinderHandler);
            SlabPickHandler       = new LemoineTools.Tools.Clash.AutoDimension.SlabPickEventHandler();
            SlabPickEvent         = ExternalEvent.Create(SlabPickHandler);
            CreateSheetsHandler  = new CreateSheetsEventHandler();
            CreateSheetsEvent    = ExternalEvent.Create(CreateSheetsHandler);

            // ── Modify Elements ───────────────────────────────────────────────
            SplitByLevelHandler          = new SplitByLevelEventHandler();
            SplitByLevelEvent            = ExternalEvent.Create(SplitByLevelHandler);
            SplitByGridHandler           = new SplitByGridEventHandler();
            SplitByGridEvent             = ExternalEvent.Create(SplitByGridHandler);
            SplitByCellHandler           = new SplitByCellEventHandler();
            SplitByCellEvent             = ExternalEvent.Create(SplitByCellHandler);
            SplitByReferencePlaneHandler = new SplitByReferencePlaneEventHandler();
            SplitByReferencePlaneEvent   = ExternalEvent.Create(SplitByReferencePlaneHandler);
            ExtendWallsHandler           = new ExtendWallsEventHandler();
            ExtendWallsEvent             = ExternalEvent.Create(ExtendWallsHandler);

            try { application.CreateRibbonTab("Lemoine Tools"); } catch (Exception __lex) { LemoineLog.Swallowed("App: create ribbon tab", __lex); }
            var dll = Assembly.GetExecutingAssembly().Location;

            // Local helper — avoids repeating the dll path and namespace prefix on every button.
            PushButtonData Btn(string id, string label, string cmd, string tip, string? glyph = null)
            {
                var d = new PushButtonData(id, label, dll, "LemoineTools.Commands." + cmd) { ToolTip = tip };
                if (glyph != null) { d.LargeImage = CreateGlyphBitmap(32, glyph);
                                     d.Image      = CreateGlyphBitmap(16, glyph); }
                return d;
            }

            // ── T01 — Filters ─────────────────────────────────────────────────
            // Large:    Discover Rules
            // Large:    Auto Filters
            // SplitButton: Apply to Views | Remove from View | Delete from Project
            var filtersPanel = application.CreateRibbonPanel("Lemoine Tools", "T01  Filters");

            filtersPanel.AddItem(Btn(
                "LT_DiscoverRules", "Discover\nRules", "DiscoverLaunchCommand",
                "Scan loaded Revit links for unique parameter values and propose colour-coded filter rules.",
                "\uE773"));  // Segoe MDL2: Search

            filtersPanel.AddItem(Btn(
                "LT_AutoFilters", "Auto\nFilters", "OpenFiltersSettingsCommand",
                "Open the Auto Filters window to configure and create view filters.",
                "\uE713"));  // Segoe MDL2: Settings gear

            var splitData = new SplitButtonData("LT_FilterActions", "Filter\nActions")
            {
                LargeImage = CreateGlyphBitmap(32, "\uE700"),
                Image      = CreateGlyphBitmap(16, "\uE700"),
            };
            var split = (SplitButton)filtersPanel.AddItem(splitData);
            split.AddPushButton(Btn(
                "LT_ApplyFiltersToViews", "Apply to\nViews", "ApplyFiltersToViewsLaunchCommand",
                "Apply existing project filters to multiple views at once, with optional color overrides.",
                "\uE710"));  // Segoe MDL2: Add/Plus
            split.AddPushButton(Btn(
                "LT_DeleteFiltersFromView", "Remove\nfrom View", "DeleteFiltersLaunchCommand",
                "Remove selected filters from the active view (filters are kept in the project).",
                "\uE738"));  // Segoe MDL2: Remove
            split.AddPushButton(Btn(
                "LT_DeleteFiltersFromProject", "Delete from\nProject", "DeleteFiltersFromProjectLaunchCommand",
                "Permanently delete selected ParameterFilterElements from the project.",
                "\uE74d"));  // Segoe MDL2: Delete

            // Legend Creation — lives in T01, furthest right (opens the window
            // where legends are built, created, and updated).
            filtersPanel.AddItem(Btn(
                "LT_LegendSettings", "Legend\nCreation", "OpenLegendSettingsCommand",
                "Open the Legend Creation window to build, create, and update Legend views.",
                "\uE713"));  // Segoe MDL2: Settings gear

            // ── T02 — Ceilings ────────────────────────────────────────────────
            // Large:   Ceiling Heatmap
            // Pulldown: Ceiling Grids → Make / Project / Reproject
            var ceilingsPanel = application.CreateRibbonPanel("Lemoine Tools", "T02  Ceilings");

            ceilingsPanel.AddItem(Btn(
                "LT_CeilingHeatmap", "Ceiling\nHeatmap", "CeilingHeatmapCommand",
                "Color-code ceiling elevations by height using view filter overrides.", "\uE81D"));

            var ceilingGridsPulldown = new PulldownButtonData("LT_CeilingGrids", "Ceiling\nGrids")
            {
                ToolTip    = "Create, project, and reproject ceiling grid DWG exports.",
                LargeImage = CreateGlyphBitmap(32, "\uE80A"),
                Image      = CreateGlyphBitmap(16, "\uE80A"),
            };

            var ceilingGridsBtn = ceilingsPanel.AddItem(ceilingGridsPulldown) as PulldownButton;

            ceilingGridsBtn?.AddPushButton(new PushButtonData(
                "LT_MakeCeilingGrids", "Make Ceiling Grids", dll,
                "LemoineTools.Commands.MakeCeilingGridsCommand")
            {
                ToolTip    = "Create RCP views for each level showing only ceilings and export them as DWG files.",
                LargeImage = CreateGlyphBitmap(32, "\uE80A"),
                Image      = CreateGlyphBitmap(16, "\uE80A"),
            });

            ceilingGridsBtn?.AddPushButton(new PushButtonData(
                "LT_ProjectCeilingGrids", "Project Grids", dll,
                "LemoineTools.Commands.ProjectedCeilingGridsCommand")
            {
                ToolTip    = "Import a DWG ceiling plan and project its lines onto ceiling soffit faces.",
                LargeImage = CreateGlyphBitmap(32, "\uE896"),
                Image      = CreateGlyphBitmap(16, "\uE896"),
            });

            ceilingGridsBtn?.AddPushButton(new PushButtonData(
                "LT_ReprojectCeilingGrids", "Reproject Grids", dll,
                "LemoineTools.Commands.ReprojectCeilingGridsCommand")
            {
                ToolTip    = "Reproject existing ceiling grid ModelCurves to updated ceiling elevations.",
                LargeImage = CreateGlyphBitmap(32, "\uE895"),
                Image      = CreateGlyphBitmap(16, "\uE895"),
            });

            // ── T03 — Bulk Views ──────────────────────────────────────────────
            // Large: Bulk Views by Level
            // Large: Replicate Dep. Views
            var linkViewsPanel = application.CreateRibbonPanel("Lemoine Tools", "T03  Bulk Views");

            linkViewsPanel.AddItem(Btn(
                "LT_LinkViewsLevel", "Bulk Views\nby Level", "LinkViewsLevelCommand",
                "Create cropped 3D, floor plan, and ceiling plan views per level and building cluster.",
                "\uE8B7"));  // Segoe MDL2: Layers

            linkViewsPanel.AddItem(Btn(
                "LT_ReplicateDependentViews", "Replicate\nDep. Views", "ReplicateDependentViewsCommand",
                "Copy dependent views and their crop regions from a source view onto one or more target views.",
                "\uE8C8"));  // Segoe MDL2: Copy

            linkViewsPanel.AddItem(Btn(
                "LT_BulkExport", "Bulk\nExport", "BulkExportCommand",
                "Export sheets and views to PDF, DWG, NWC, or IFC in bulk with token-based filenames.",
                char.ConvertFromUtf32(0xEDE1)));  // Segoe MDL2: Share / Export

            // ── T04 — Modify Elements ─────────────────────────────────────────
            // Pulldown: Split Elements (4 sub-commands)
            // Large:    Extend Walls
            var modifyPanel = application.CreateRibbonPanel("Lemoine Tools", "T04  Modify");

            // Icons used here:  (Cut),  (RowsGroup),  (GridView),
            //                   (Trim/plane),  (Table) — none overlap other panels.
            var splitPulldownData = new PulldownButtonData("LT_SplitElements", "Split\nElements")
            {
                ToolTip    = "Split elements at level elevations, grid planes, reference planes, or a regular cell grid.",
                LargeImage = CreateGlyphBitmap(32, ""),
                Image      = CreateGlyphBitmap(16, ""),
            };
            var splitBtn = modifyPanel.AddItem(splitPulldownData) as PulldownButton;

            splitBtn?.AddPushButton(new PushButtonData(
                "LT_SplitByLevel", "Split by Levels", dll, "LemoineTools.Commands.SplitByLevelCommand")
            {
                ToolTip    = "Split walls, columns, and MEP curves at selected level elevations.",
                LargeImage = CreateGlyphBitmap(32, ""),
                Image      = CreateGlyphBitmap(16, ""),
            });

            splitBtn?.AddPushButton(new PushButtonData(
                "LT_SplitByGrid", "Split by Grid Lines", dll, "LemoineTools.Commands.SplitByGridCommand")
            {
                ToolTip    = "Split walls and MEP curves at selected grid plane intersections.",
                LargeImage = CreateGlyphBitmap(32, ""),
                Image      = CreateGlyphBitmap(16, ""),
            });

            splitBtn?.AddPushButton(new PushButtonData(
                "LT_SplitByReferencePlane", "Split by\nRef Plane", dll, "LemoineTools.Commands.SplitByReferencePlaneCommand")
            {
                ToolTip    = "Split walls and MEP curves at selected reference plane intersections.",
                LargeImage = CreateGlyphBitmap(32, ""),
                Image      = CreateGlyphBitmap(16, ""),
            });

            splitBtn?.AddPushButton(new PushButtonData(
                "LT_SplitByCell", "Split by Cell", dll, "LemoineTools.Commands.SplitByCellCommand")
            {
                ToolTip    = "Split floors, ceilings, and filled regions into a regular grid of cells.",
                LargeImage = CreateGlyphBitmap(32, ""),
                Image      = CreateGlyphBitmap(16, ""),
            });

            modifyPanel.AddItem(Btn(
                "LT_ExtendWalls", "Extend\nWalls", "ExtendWallsCommand",
                "Set the top constraint of walls that extend above the ceiling to the next level up.",
                "\uE898"));  // Segoe MDL2: Sort Ascending / Up

            // ── T05 — Clash ───────────────────────────────────────────────────
            var clashPanel = application.CreateRibbonPanel("Lemoine Tools", "T05  Clash");

            clashPanel.AddItem(Btn(
                "LT_ClashDefinitions", "Clash\nDefinitions", "OpenClashDefinitionsCommand",
                "Build and manage a library of named clash definitions (two element groups plus marking settings).",
                char.ConvertFromUtf32(0xE71C)));  // Segoe MDL2: Filter

            clashPanel.AddItem(Btn(
                "LT_ClashFinder", "Clash Finder\n& Dimension", "ClashFinderCommand",
                "Run saved clash definitions across selected views: detect clashes, place coloured tagged markers, and dimension them out to grids or slab edges.",
                char.ConvertFromUtf32(0xE721)));  // Segoe MDL2: Zoom (find)

            clashPanel.AddItem(Btn(
                "LT_ClashElevationFinder", "Clash Finder\n& Elevation", "ClashElevationFinderCommand",
                "Run saved clash definitions across selected sections and elevations: detect clashes, place coloured round markers, and tag each with a spot elevation at the top, centre, or bottom of the round.",
                char.ConvertFromUtf32(0xE898)));  // Segoe MDL2: Up (elevation/height)

            // ── Testing ───────────────────────────────────────────────────────
            var testingPanel = application.CreateRibbonPanel("Lemoine Tools", "Testing");

            testingPanel.AddStackedItems(
                Btn("LT_CreateSheets",        "Create Sheets", "CreateSheetsCommand",
                    "Generate sheets from levels, rooms, scope boxes, or a CSV file."),
                Btn("LT_LinkViewsDiscipline", "By Discipline", "LinkViewsDisciplineCommand",
                    "Create one 3D view per link with a section box, with optional combined views per discipline."));

            // ── Settings / Developer — two large buttons ──────────────
            var settingsPanel = application.CreateRibbonPanel("Lemoine Tools", "Settings");

            settingsPanel.AddItem(
                new PushButtonData("LT_OpenSettings", "Settings", dll,
                    "LemoineTools.Commands.OpenSettingsCommand")
                {
                    ToolTip    = "Open Lemoine Tools global settings — themes, UI size, and per-tool options.",
                    LargeImage = CreateGearBitmap(32),
                    Image      = CreateGearBitmap(16),
                });

            settingsPanel.AddItem(
                Btn("LT_DebugTool", "UI\nDebug", "DebugToolCommand",
                    "Exercise every Lemoine input control and UI element.", "\uE7B3"));

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

        // ── Simple programmatic gear bitmap for the ribbon button ─────────────
        private static System.Windows.Media.Imaging.BitmapSource CreateGlyphBitmap(int size, string glyph)
        {
            var tb = new System.Windows.Controls.TextBlock
            {
                Text       = glyph,
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize   = size * 0.72,
                Foreground = System.Windows.Media.Brushes.White,
                Width = size, Height = size,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment   = System.Windows.VerticalAlignment.Center,
                TextAlignment       = System.Windows.TextAlignment.Center,
            };
            tb.Measure(new System.Windows.Size(size, size));
            tb.Arrange(new System.Windows.Rect(0, 0, size, size));
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                size, size, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(tb);
            rtb.Freeze();
            return rtb;
        }
        private static System.Windows.Media.Imaging.BitmapSource CreateGearBitmap(int size)
        {
            // Render the ⚙ glyph from Segoe UI Symbol into a bitmap for the Revit ribbon.
            var tb = new System.Windows.Controls.TextBlock
            {
                Text       = "\u2699",
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI Symbol"),
                FontSize   = size * 0.75,
                Foreground = System.Windows.Media.Brushes.White,
                Width      = size, Height = size,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment   = System.Windows.VerticalAlignment.Center,
                TextAlignment       = System.Windows.TextAlignment.Center,
            };
            tb.Measure(new System.Windows.Size(size, size));
            tb.Arrange(new System.Windows.Rect(0, 0, size, size));

            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                size, size, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(tb);
            rtb.Freeze();
            return rtb;
        }
    }
}
