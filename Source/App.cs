using System.Reflection;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.AutoFilters;
using LemoineTools.Tools.Ceilings;
using LemoineTools.Tools.LinkViews;
using LemoineTools.Tools.ModifyElements;
using LemoineTools.Tools.Testing.CoordSet;
using LemoineTools.Tools.Testing.LegendCreator;
using LemoineTools.Tools.Testing;

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

        // ── Auto Filters ────────────────────────────────────────────────────────────
        internal static AutoFiltersEventHandler?              AutoFiltersHandler              { get; private set; }
        internal static ExternalEvent?                        AutoFiltersEvent                { get; private set; }
        internal static AutoFiltersLegendEventHandler?        AutoFiltersLegendHandler        { get; private set; }
        internal static ExternalEvent?                        AutoFiltersLegendEvent          { get; private set; }
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

        // ── Coordination Drawing Set ────────────────────────────────────────────────
        internal static CoordSetRunHandler? CoordSetRunHandler { get; private set; }
        internal static ExternalEvent?      CoordSetRunEvent   { get; private set; }

        // ── Testing — Batch Export ──────────────────────────────────────────────────
        internal static BatchExportEventHandler?   BatchExportHandler   { get; private set; }
        internal static ExternalEvent?             BatchExportEvent     { get; private set; }

        // ── Testing — Batch Dimension ───────────────────────────────────────────────
        internal static BatchDimensionEventHandler? BatchDimensionHandler { get; private set; }
        internal static ExternalEvent?              BatchDimensionEvent   { get; private set; }

        // ── Testing — Create Sheets ─────────────────────────────────────────────────
        internal static CreateSheetsEventHandler?  CreateSheetsHandler  { get; private set; }
        internal static ExternalEvent?             CreateSheetsEvent    { get; private set; }

        // ── Testing — Sheet Pack ────────────────────────────────────────────────────
        internal static SheetPackEventHandler?     SheetPackHandler     { get; private set; }
        internal static ExternalEvent?             SheetPackEvent       { get; private set; }

        // ── Legend Creator (T08) ─────────────────────────────────────────────────────
        internal static LegendCreatorEventHandler? LegendCreatorHandler { get; private set; }
        internal static ExternalEvent?             LegendCreatorEvent   { get; private set; }

        // ── Modify Elements ─────────────────────────────────────────────────────────
        internal static SplitByLevelEventHandler? SplitByLevelHandler { get; private set; }
        internal static ExternalEvent?            SplitByLevelEvent   { get; private set; }
        internal static SplitByGridEventHandler?  SplitByGridHandler  { get; private set; }
        internal static ExternalEvent?            SplitByGridEvent    { get; private set; }
        internal static SplitByCellEventHandler?  SplitByCellHandler  { get; private set; }
        internal static ExternalEvent?            SplitByCellEvent    { get; private set; }
        internal static ExtendWallsEventHandler?  ExtendWallsHandler  { get; private set; }
        internal static ExternalEvent?            ExtendWallsEvent    { get; private set; }

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

            // ── Auto Filters suite ────────────────────────────────────────────
            AutoFiltersHandler              = new AutoFiltersEventHandler();
            AutoFiltersEvent                = ExternalEvent.Create(AutoFiltersHandler);
            AutoFiltersLegendHandler        = new AutoFiltersLegendEventHandler();
            AutoFiltersLegendEvent          = ExternalEvent.Create(AutoFiltersLegendHandler);
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

            // ── Coordination Drawing Set ──────────────────────────────────────
            CoordSetRunHandler = new CoordSetRunHandler();
            CoordSetRunEvent   = ExternalEvent.Create(CoordSetRunHandler);

            // ── Testing — new tools ───────────────────────────────────────────
            BatchExportHandler   = new BatchExportEventHandler();
            BatchExportEvent     = ExternalEvent.Create(BatchExportHandler);
            BatchDimensionHandler = new BatchDimensionEventHandler();
            BatchDimensionEvent   = ExternalEvent.Create(BatchDimensionHandler);
            CreateSheetsHandler  = new CreateSheetsEventHandler();
            CreateSheetsEvent    = ExternalEvent.Create(CreateSheetsHandler);
            SheetPackHandler     = new SheetPackEventHandler();
            SheetPackEvent       = ExternalEvent.Create(SheetPackHandler);

            LegendCreatorHandler = new LegendCreatorEventHandler();
            LegendCreatorEvent   = ExternalEvent.Create(LegendCreatorHandler);

            // ── Modify Elements ───────────────────────────────────────────────
            SplitByLevelHandler = new SplitByLevelEventHandler();
            SplitByLevelEvent   = ExternalEvent.Create(SplitByLevelHandler);
            SplitByGridHandler  = new SplitByGridEventHandler();
            SplitByGridEvent    = ExternalEvent.Create(SplitByGridHandler);
            SplitByCellHandler  = new SplitByCellEventHandler();
            SplitByCellEvent    = ExternalEvent.Create(SplitByCellHandler);
            ExtendWallsHandler  = new ExtendWallsEventHandler();
            ExtendWallsEvent    = ExternalEvent.Create(ExtendWallsHandler);

            try { application.CreateRibbonTab("Lemoine Tools"); } catch { }
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
            // Large: Auto Filters
            // Large: Legend Creator
            // Large SplitButton: Apply to Views | Remove from View | Delete from Project
            var filtersPanel = application.CreateRibbonPanel("Lemoine Tools", "T01  Filters");

            filtersPanel.AddItem(Btn(
                "LT_AutoFilters", "Auto\nFilters", "AutoFiltersLaunchCommand",
                "Scan MEP elements and create view filters with automatic color overrides.",
                ""));  // Segoe MDL2: Filter (funnel)

            filtersPanel.AddItem(Btn(
                "LT_AutoFiltersLegend", "Legend\nCreation", "AutoFiltersLegendLaunchCommand",
                "Create or update a Legend view from the current Legend Creator settings.",
                "\uE8FD"));  // Segoe MDL2: ColorSolid (color swatch)

            var splitData = new SplitButtonData("LT_FilterActions", "Filter\nActions");
            var split = (SplitButton)filtersPanel.AddItem(splitData);
            split.AddPushButton(Btn(
                "LT_ApplyFiltersToViews", "Apply to\nViews", "ApplyFiltersToViewsLaunchCommand",
                "Apply existing project filters to multiple views at once, with optional color overrides.",
                ""));  // Segoe MDL2: Add
            split.AddPushButton(Btn(
                "LT_DeleteFiltersFromView", "Remove\nfrom View", "DeleteFiltersLaunchCommand",
                "Remove selected filters from the active view (filters are kept in the project).",
                ""));  // Segoe MDL2: Remove
            split.AddPushButton(Btn(
                "LT_DeleteFiltersFromProject", "Delete from\nProject", "DeleteFiltersFromProjectLaunchCommand",
                "Permanently delete selected ParameterFilterElements from the project.",
                ""));  // Segoe MDL2: Delete

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

            // ── T04 — Modify Elements ─────────────────────────────────────────
            // Stacked 3: Split by Level  |  Split by Grid  |  Split by Cell
            // Large:     Extend Walls
            var modifyPanel = application.CreateRibbonPanel("Lemoine Tools", "T04  Modify");

            modifyPanel.AddStackedItems(
                Btn("LT_SplitByLevel", "Split by Level", "SplitByLevelCommand",
                    "Split walls, columns, and MEP curves at selected level elevations."),
                Btn("LT_SplitByGrid",  "Split by Grid",  "SplitByGridCommand",
                    "Split walls and MEP curves at selected grid plane intersections."),
                Btn("LT_SplitByCell",  "Split by Cell",  "SplitByCellCommand",
                    "Split floors, ceilings, and filled regions into a regular grid of cells."));

            modifyPanel.AddItem(Btn(
                "LT_ExtendWalls", "Extend\nWalls", "ExtendWallsCommand",
                "Set the top constraint of walls that extend above the ceiling to the next level up.",
                "\uE898"));  // Segoe MDL2: Sort Ascending / Up

            // ── Testing ───────────────────────────────────────────────────────
            var testingPanel = application.CreateRibbonPanel("Lemoine Tools", "Testing");

            testingPanel.AddItem(Btn(
                "LT_CoordSet", "Coord\nDrawing Set", "CoordSetCommand",
                "Generate a complete coordination drawing set: filters, discipline views, legend, dependent views, and sheets.",
                "\uE7C3"));  // Segoe MDL2: Page

            testingPanel.AddStackedItems(
                Btn("LT_BatchExport",    "Batch Export",    "BatchExportCommand",
                    "Export sheets and views to PDF or DWG in bulk with token-based filenames."),
                Btn("LT_BatchDimension", "Batch Dimension", "BatchDimensionCommand",
                    "Apply dimension strings across multiple views at once."));

            testingPanel.AddStackedItems(
                Btn("LT_CreateSheets",        "Create Sheets", "CreateSheetsCommand",
                    "Generate sheets from levels, rooms, scope boxes, or a CSV file."),
                Btn("LT_SheetPack",            "Sheet Pack",    "SheetPackCommand",
                    "Organise sheets into named issue packages and stamp sheet parameters."),
                Btn("LT_LinkViewsDiscipline",  "By Discipline", "LinkViewsDisciplineCommand",
                    "Create one 3D view per link with a section box, with optional combined views per discipline."));

            // ── Settings / Developer — one compact stacked panel ──────────────
            var settingsPanel = application.CreateRibbonPanel("Lemoine Tools", "Settings");

            settingsPanel.AddStackedItems(
                new PushButtonData("LT_OpenSettings", "Settings", dll,
                    "LemoineTools.Commands.OpenSettingsCommand")
                {
                    ToolTip = "Open Lemoine Tools global settings — themes, UI size, and per-tool options.",
                    Image   = CreateGearBitmap(16),
                },
                Btn("LT_DebugTool", "UI Debug", "DebugToolCommand",
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
