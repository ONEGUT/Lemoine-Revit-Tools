using System.Reflection;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.AutoFilters;
using LemoineTools.Tools.Ceilings;
using LemoineTools.Tools.LinkViews;
using LemoineTools.Tools.ModifyElements;
using LemoineTools.Tools.BulkExport;
using LemoineTools.Tools.Clash;
using LemoineTools.Tools.ExplodeViews;
using LemoineTools.Tools.Testing.LegendCreator;
using LemoineTools.Tools.Testing;
using System;

namespace LemoineTools
{
    public class App : IExternalApplication
    {
        // Shared main-thread reload event for StepFlowWindow's "Reload" action (re-captures a
        // tool's document-derived options). Tool-agnostic, so one instance serves every window.
        internal static Lemoine.LemoineReloadHandler? ReloadHandler { get; private set; }
        internal static ExternalEvent?                ReloadEvent   { get; private set; }

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
        internal static DiscoverEventHandler?     DiscoverHandler     { get; private set; }
        internal static ExternalEvent?            DiscoverEvent       { get; private set; }
        // Opens the Discover window from inside the Auto Filters window (main-thread setup).
        internal static OpenDiscoverEventHandler? OpenDiscoverHandler { get; private set; }
        internal static ExternalEvent?            OpenDiscoverEvent   { get; private set; }

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
        // Opens the Delete-from-Project window from inside the Auto Filters window (main-thread setup).
        internal static OpenDeleteFromProjectEventHandler?    OpenDeleteFromProjectHandler    { get; private set; }
        internal static ExternalEvent?                        OpenDeleteFromProjectEvent      { get; private set; }

        // ── Link Views — Level ──────────────────────────────────────────────────────
        internal static LinkViewsLevelPhase1Handler? LinkViewsLevelPhase1Handler { get; private set; }
        internal static ExternalEvent?               LinkViewsLevelPhase1Event   { get; private set; }
        internal static LinkViewsLevelRunHandler?    LinkViewsLevelRunHandler    { get; private set; }
        internal static ExternalEvent?               LinkViewsLevelRunEvent      { get; private set; }

        // ── Bulk Views by Template ──────────────────────────────────────────────────
        internal static ViewsByTemplateRunHandler?   ViewsByTemplateRunHandler   { get; private set; }
        internal static ExternalEvent?               ViewsByTemplateRunEvent     { get; private set; }

        // ── Bulk Duplicate Views ────────────────────────────────────────────────────
        internal static ViewsBulkDuplicateRunHandler? ViewsBulkDuplicateRunHandler { get; private set; }
        internal static ExternalEvent?                ViewsBulkDuplicateRunEvent   { get; private set; }

        // ── Bulk Rename (Sheets & Views) ─────────────────────────────────────────────
        internal static LemoineTools.Tools.LinkViews.BulkRename.BulkRenameRunHandler? BulkRenameRunHandler { get; private set; }
        internal static ExternalEvent?                BulkRenameRunEvent           { get; private set; }

        // ── Replicate Dependent Views ───────────────────────────────────────────────
        internal static ReplicateDependentViewsRunHandler? ReplicateDependentViewsRunHandler { get; private set; }
        internal static ExternalEvent?                     ReplicateDependentViewsRunEvent   { get; private set; }

        // ── T03 — Bulk Export ───────────────────────────────────────────────────────
        internal static BulkExportEventHandler?   BulkExportHandler   { get; private set; }
        internal static ExternalEvent?             BulkExportEvent     { get; private set; }
        internal static PrintViewEventHandler?     PrintViewHandler    { get; private set; }
        internal static ExternalEvent?             PrintViewEvent      { get; private set; }

        // ── T05 — Clash (Definitions + Finder & Dimensioning) ───────────────────────
        internal static ClashPickEventHandler?      ClashPickHandler      { get; private set; }
        internal static ExternalEvent?              ClashPickEvent        { get; private set; }
        internal static ClashFinderEventHandler?    ClashFinderHandler    { get; private set; }
        internal static ExternalEvent?              ClashFinderEvent      { get; private set; }
        internal static ClashElevationFinderEventHandler? ClashElevationFinderHandler { get; private set; }
        internal static ExternalEvent?              ClashElevationFinderEvent   { get; private set; }
        internal static LemoineTools.Tools.Clash.AutoDimension.SlabPickEventHandler? SlabPickHandler { get; private set; }
        internal static ExternalEvent?              SlabPickEvent         { get; private set; }
        internal static LemoineTools.Tools.Clash.AutoDimension.Refine.RefineDimensionsEventHandler? RefineDimensionsHandler { get; private set; }
        internal static ExternalEvent?              RefineDimensionsEvent { get; private set; }

        // ── T06 — Views (Explode 3D View by Trade) ──────────────────────────────────
        internal static ExplodeViewByTradeEventHandler? ExplodeViewByTradeHandler { get; private set; }
        internal static ExternalEvent?                  ExplodeViewByTradeEvent   { get; private set; }

        // ── Testing — Place Dependent Views ─────────────────────────────────────────
        internal static LemoineTools.Tools.Testing.PlaceDependentViews.PlaceDependentViewsEventHandler? PlaceDependentViewsHandler { get; private set; }
        internal static ExternalEvent?             PlaceDependentViewsEvent { get; private set; }

        // ── Testing — Align Sheet Views ─────────────────────────────────────────────
        internal static LemoineTools.Tools.Testing.AlignSheetViews.AlignSheetViewsEventHandler? AlignSheetViewsHandler { get; private set; }
        internal static ExternalEvent?             AlignSheetViewsEvent { get; private set; }

        // ── Testing — Copy Linear Elements ──────────────────────────────────────────
        internal static LemoineTools.Tools.CopyLinear.CopyLinearScanHandler? CopyLinearScanHandler { get; private set; }
        internal static ExternalEvent?             CopyLinearScanEvent  { get; private set; }
        internal static LemoineTools.Tools.CopyLinear.CopyLinearRunHandler?  CopyLinearRunHandler  { get; private set; }
        internal static ExternalEvent?             CopyLinearRunEvent   { get; private set; }

        // ── Testing — Copy Grids from Link ──────────────────────────────────────────
        internal static LemoineTools.Tools.CopyLinear.CopyGridsRunHandler? CopyGridsRunHandler { get; private set; }
        internal static ExternalEvent?             CopyGridsRunEvent    { get; private set; }

        // ── Testing — Copy Elements from Link ───────────────────────────────────────
        internal static LemoineTools.Tools.CopyFromLink.CopyFromLinkScanHandler? CopyFromLinkScanHandler { get; private set; }
        internal static ExternalEvent?             CopyFromLinkScanEvent { get; private set; }
        internal static LemoineTools.Tools.CopyFromLink.CopyFromLinkRunHandler?  CopyFromLinkRunHandler  { get; private set; }
        internal static ExternalEvent?             CopyFromLinkRunEvent  { get; private set; }

        // ── Coordination — Align Coordinates / Compare Grids ─────────────────────────
        internal static LemoineTools.Tools.Coordinates.AlignCoordinatesRunHandler? AlignCoordinatesRunHandler { get; private set; }
        internal static ExternalEvent?             AlignCoordinatesRunEvent { get; private set; }
        internal static LemoineTools.Tools.Coordinates.CompareGridsRunHandler?     CompareGridsRunHandler     { get; private set; }
        internal static ExternalEvent?             CompareGridsRunEvent     { get; private set; }


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

        // Tools Overview window — read-only guide, singleton like GlobalSettings
        internal static Lemoine.ToolsOverviewWindow? Overview { get; set; }

        public Result OnStartup(UIControlledApplication application)
        {
            // Surface Revit's transaction failures and modal dialogs into the running tool's
            // Output log (and suppress warning dialogs). Both handlers no-op unless a Lemoine
            // run is active, so non-Lemoine work is never affected.
            application.ControlledApplication.FailuresProcessing += LemoineFailureCapture.OnFailuresProcessing;
            application.DialogBoxShowing                         += LemoineFailureCapture.OnDialogBoxShowing;

            ReloadHandler = new Lemoine.LemoineReloadHandler();
            ReloadEvent   = ExternalEvent.Create(ReloadHandler);

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
            OpenDiscoverHandler = new OpenDiscoverEventHandler();
            OpenDiscoverEvent   = ExternalEvent.Create(OpenDiscoverHandler);

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
            OpenDeleteFromProjectHandler    = new OpenDeleteFromProjectEventHandler();
            OpenDeleteFromProjectEvent      = ExternalEvent.Create(OpenDeleteFromProjectHandler);

            // ── Link Views — Level ────────────────────────────────────────────
            LinkViewsLevelPhase1Handler = new LinkViewsLevelPhase1Handler();
            LinkViewsLevelPhase1Event   = ExternalEvent.Create(LinkViewsLevelPhase1Handler);
            LinkViewsLevelRunHandler    = new LinkViewsLevelRunHandler();
            LinkViewsLevelRunEvent      = ExternalEvent.Create(LinkViewsLevelRunHandler);
            ViewsByTemplateRunHandler   = new ViewsByTemplateRunHandler();
            ViewsByTemplateRunEvent     = ExternalEvent.Create(ViewsByTemplateRunHandler);
            ViewsBulkDuplicateRunHandler = new ViewsBulkDuplicateRunHandler();
            ViewsBulkDuplicateRunEvent   = ExternalEvent.Create(ViewsBulkDuplicateRunHandler);
            BulkRenameRunHandler = new LemoineTools.Tools.LinkViews.BulkRename.BulkRenameRunHandler();
            BulkRenameRunEvent   = ExternalEvent.Create(BulkRenameRunHandler);

            // ── Replicate Dependent Views ─────────────────────────────────────
            ReplicateDependentViewsRunHandler = new ReplicateDependentViewsRunHandler();
            ReplicateDependentViewsRunEvent   = ExternalEvent.Create(ReplicateDependentViewsRunHandler);

            // ── Testing — new tools ───────────────────────────────────────────
            BulkExportHandler   = new BulkExportEventHandler();
            BulkExportEvent     = ExternalEvent.Create(BulkExportHandler);
            PrintViewHandler    = new PrintViewEventHandler();
            PrintViewEvent      = ExternalEvent.Create(PrintViewHandler);
            ClashPickHandler      = new ClashPickEventHandler();
            ClashPickEvent        = ExternalEvent.Create(ClashPickHandler);
            ClashFinderHandler    = new ClashFinderEventHandler();
            ClashFinderEvent      = ExternalEvent.Create(ClashFinderHandler);
            ClashElevationFinderHandler = new ClashElevationFinderEventHandler();
            ClashElevationFinderEvent   = ExternalEvent.Create(ClashElevationFinderHandler);
            SlabPickHandler       = new LemoineTools.Tools.Clash.AutoDimension.SlabPickEventHandler();
            SlabPickEvent         = ExternalEvent.Create(SlabPickHandler);
            RefineDimensionsHandler = new LemoineTools.Tools.Clash.AutoDimension.Refine.RefineDimensionsEventHandler();
            RefineDimensionsEvent   = ExternalEvent.Create(RefineDimensionsHandler);

            // ── T06 — Views ───────────────────────────────────────────────────
            ExplodeViewByTradeHandler = new ExplodeViewByTradeEventHandler();
            ExplodeViewByTradeEvent   = ExternalEvent.Create(ExplodeViewByTradeHandler);

            PlaceDependentViewsHandler = new LemoineTools.Tools.Testing.PlaceDependentViews.PlaceDependentViewsEventHandler();
            PlaceDependentViewsEvent   = ExternalEvent.Create(PlaceDependentViewsHandler);
            AlignSheetViewsHandler     = new LemoineTools.Tools.Testing.AlignSheetViews.AlignSheetViewsEventHandler();
            AlignSheetViewsEvent       = ExternalEvent.Create(AlignSheetViewsHandler);
            CopyLinearScanHandler = new LemoineTools.Tools.CopyLinear.CopyLinearScanHandler();
            CopyLinearScanEvent   = ExternalEvent.Create(CopyLinearScanHandler);
            CopyLinearRunHandler  = new LemoineTools.Tools.CopyLinear.CopyLinearRunHandler();
            CopyLinearRunEvent    = ExternalEvent.Create(CopyLinearRunHandler);
            CopyGridsRunHandler   = new LemoineTools.Tools.CopyLinear.CopyGridsRunHandler();
            CopyGridsRunEvent     = ExternalEvent.Create(CopyGridsRunHandler);
            CopyFromLinkScanHandler = new LemoineTools.Tools.CopyFromLink.CopyFromLinkScanHandler();
            CopyFromLinkScanEvent   = ExternalEvent.Create(CopyFromLinkScanHandler);
            CopyFromLinkRunHandler  = new LemoineTools.Tools.CopyFromLink.CopyFromLinkRunHandler();
            CopyFromLinkRunEvent    = ExternalEvent.Create(CopyFromLinkRunHandler);

            AlignCoordinatesRunHandler = new LemoineTools.Tools.Coordinates.AlignCoordinatesRunHandler();
            AlignCoordinatesRunEvent   = ExternalEvent.Create(AlignCoordinatesRunHandler);
            CompareGridsRunHandler     = new LemoineTools.Tools.Coordinates.CompareGridsRunHandler();
            CompareGridsRunEvent       = ExternalEvent.Create(CompareGridsRunHandler);

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

            // ── Filters & Legends ─────────────────────────────────────────────
            // Auto Filters + Legend Creation. Both open their own configuration windows
            // (Discover, Remove-from-View, Delete-from-Project live inside Auto Filters).
            var filtersPanel = application.CreateRibbonPanel("Lemoine Tools", "Filters & Legends");

            filtersPanel.AddItem(Btn(
                "LT_AutoFilters", "Auto\nFilters", "OpenFiltersSettingsCommand",
                "Open the Auto Filters window to configure and create view filters.",
                char.ConvertFromUtf32(0xE71C)));  // Filter

            filtersPanel.AddItem(Btn(
                "LT_LegendSettings", "Legend\nCreation", "OpenLegendSettingsCommand",
                "Open the Legend Creation window to build, create, and update Legend views.",
                char.ConvertFromUtf32(0xE713)));  // Settings

            // ── Ceilings ──────────────────────────────────────────────────────
            // Large:    Ceiling Heatmap
            // Pulldown: Ceiling Grids → Make / Project / Reproject
            var ceilingsPanel = application.CreateRibbonPanel("Lemoine Tools", "Ceilings");

            ceilingsPanel.AddItem(Btn(
                "LT_CeilingHeatmap", "Ceiling\nHeatmap", "CeilingHeatmapCommand",
                "Color-code ceiling elevations by height using view filter overrides.",
                char.ConvertFromUtf32(0xE81D)));  // Map

            var ceilingGridsPulldown = new PulldownButtonData("LT_CeilingGrids", "Ceiling\nGrids")
            {
                ToolTip    = "Create, project, and reproject ceiling grid DWG exports.",
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE80A)),
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE80A)),
            };

            var ceilingGridsBtn = ceilingsPanel.AddItem(ceilingGridsPulldown) as PulldownButton;

            ceilingGridsBtn?.AddPushButton(new PushButtonData(
                "LT_MakeCeilingGrids", "Make Ceiling Grids", dll,
                "LemoineTools.Commands.MakeCeilingGridsCommand")
            {
                ToolTip    = "Create RCP views for each level showing only ceilings and export them as DWG files.",
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE80A)),
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE80A)),
            });

            ceilingGridsBtn?.AddPushButton(new PushButtonData(
                "LT_ProjectCeilingGrids", "Project Grids", dll,
                "LemoineTools.Commands.ProjectedCeilingGridsCommand")
            {
                ToolTip    = "Import a DWG ceiling plan and project its lines onto ceiling soffit faces.",
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE896)),
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE896)),
            });

            ceilingGridsBtn?.AddPushButton(new PushButtonData(
                "LT_ReprojectCeilingGrids", "Reproject Grids", dll,
                "LemoineTools.Commands.ReprojectCeilingGridsCommand")
            {
                ToolTip    = "Reproject existing ceiling grid ModelCurves to updated ceiling elevations.",
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE895)),
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE895)),
            });

            // ── Views ─────────────────────────────────────────────────────────
            // Large:    Bulk Views by Level
            // Pulldown: Duplicate Views (Bulk Duplicate | By Template | Dependent)
            // Large:    Explode View by Trade
            var viewsPanel = application.CreateRibbonPanel("Lemoine Tools", "Views");

            viewsPanel.AddItem(Btn(
                "LT_LinkViewsLevel", "Bulk Views\nby Level", "LinkViewsLevelCommand",
                "Create cropped 3D, floor plan, and ceiling plan views per level and building cluster.",
                char.ConvertFromUtf32(0xE8B7)));  // Layers

            var dupViewsPulldown = new PulldownButtonData("LT_DuplicateViews", "Duplicate\nViews")
            {
                ToolTip    = "Bulk-duplicate views: plain duplicate, duplicate across view templates, or duplicate as dependent.",
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE8C8)),  // Copy
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE8C8)),
            };
            var dupViewsBtn = viewsPanel.AddItem(dupViewsPulldown) as PulldownButton;

            dupViewsBtn?.AddPushButton(new PushButtonData(
                "LT_ViewsBulkDuplicate", "Bulk Duplicate", dll, "LemoineTools.Commands.ViewsBulkDuplicateCommand")
            {
                ToolTip    = "Duplicate selected views (Duplicate, With Detailing, or As Dependent) with token-based naming.",
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE8C8)),  // Copy
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE8C8)),
            });

            dupViewsBtn?.AddPushButton(new PushButtonData(
                "LT_ViewsByTemplate", "Bulk Views by Template", dll, "LemoineTools.Commands.ViewsByTemplateCommand")
            {
                ToolTip    = "Duplicate selected views across selected view templates, naming each from a token pattern.",
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE8A9)),  // ViewAll
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE8A9)),
            });

            dupViewsBtn?.AddPushButton(new PushButtonData(
                "LT_ReplicateDependentViews", "Bulk Dependent Views", dll, "LemoineTools.Commands.ReplicateDependentViewsCommand")
            {
                ToolTip    = "Copy dependent views and their crop regions from a source view onto one or more target views.",
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE71B)),  // Link
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE71B)),
            });

            viewsPanel.AddItem(Btn(
                "LT_ExplodeViewByTrade", "Explode View\nby Trade", "ExplodeViewByTradeCommand",
                "Duplicate a 3D view once per AutoFilters trade at the same camera angle and section box, "
                + "isolating each trade by its view filters and stacking the views by element elevation.",
                char.ConvertFromUtf32(0xE8A9)));  // ViewAll

            // ── Sheets ────────────────────────────────────────────────────────
            // Place Dependent Views + Align Sheet Views (promoted from Testing) + Bulk Rename.
            var sheetsPanel = application.CreateRibbonPanel("Lemoine Tools", "Sheets");

            sheetsPanel.AddItem(Btn(
                "LT_PlaceDepViews", "Place Dep.\nViews", "PlaceDependentViewsCommand",
                "Create one sheet per view and place its dependents, trimmed and packed without overlap.",
                char.ConvertFromUtf32(0xE7C3)));  // Page

            sheetsPanel.AddItem(Btn(
                "LT_AlignSheetViews", "Align\nSheet Views", "AlignSheetViewsCommand",
                "Align the viewports on target sheets to the best-matching reference sheet so matching views overlay exactly. Pairs views by shared scope box (crop overlap otherwise) and can inherit the scope box, grid extents, crop size and crop visibility. Missing or ambiguous counterparts are reported.",
                char.ConvertFromUtf32(0xE744)));  // FullScreen / fit

            sheetsPanel.AddItem(Btn(
                "LT_BulkRename", "Bulk\nRename", "BulkRenameCommand",
                "Bulk-rename sheets or views via find & replace, prefix/suffix, sequential numbering, or token pattern.",
                char.ConvertFromUtf32(0xE8AC)));  // Rename

            // ── Export ────────────────────────────────────────────────────────
            var exportPanel = application.CreateRibbonPanel("Lemoine Tools", "Export");

            exportPanel.AddItem(Btn(
                "LT_BulkExport", "Bulk\nExport", "BulkExportCommand",
                "Export sheets and views to PDF, DWG, NWC, or IFC in bulk with token-based filenames.",
                char.ConvertFromUtf32(0xEDE1)));  // Share / Export

            exportPanel.AddItem(Btn(
                "LT_PrintView", "Print\nView", "PrintViewCommand",
                "Export the active view or sheet to PDF using the same PDF settings as Bulk Export.",
                char.ConvertFromUtf32(0xE749)));  // Print

            // ── Modify ────────────────────────────────────────────────────────
            // Pulldown: Split Elements (4 sub-commands)
            // Large:    Extend Walls
            var modifyPanel = application.CreateRibbonPanel("Lemoine Tools", "Modify");

            var splitPulldownData = new PulldownButtonData("LT_SplitElements", "Split\nElements")
            {
                ToolTip    = "Split elements at level elevations, grid planes, reference planes, or a regular cell grid.",
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE8C6)),  // Cut
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE8C6)),
            };
            var splitBtn = modifyPanel.AddItem(splitPulldownData) as PulldownButton;

            splitBtn?.AddPushButton(new PushButtonData(
                "LT_SplitByLevel", "Split by Levels", dll, "LemoineTools.Commands.SplitByLevelCommand")
            {
                ToolTip    = "Split walls, columns, and MEP curves at selected level elevations.",
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE8C6)),
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE8C6)),
            });

            splitBtn?.AddPushButton(new PushButtonData(
                "LT_SplitByGrid", "Split by Grid Lines", dll, "LemoineTools.Commands.SplitByGridCommand")
            {
                ToolTip    = "Split walls and MEP curves at selected grid plane intersections.",
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE80A)),
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE80A)),
            });

            splitBtn?.AddPushButton(new PushButtonData(
                "LT_SplitByReferencePlane", "Split by\nRef Plane", dll, "LemoineTools.Commands.SplitByReferencePlaneCommand")
            {
                ToolTip    = "Split walls and MEP curves at selected reference plane intersections.",
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE8C6)),
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE8C6)),
            });

            splitBtn?.AddPushButton(new PushButtonData(
                "LT_SplitByCell", "Split by Cell", dll, "LemoineTools.Commands.SplitByCellCommand")
            {
                ToolTip    = "Split floors, ceilings, and filled regions into a regular grid of cells.",
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE80A)),
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE80A)),
            });

            modifyPanel.AddItem(Btn(
                "LT_ExtendWalls", "Extend\nWalls", "ExtendWallsCommand",
                "Set the top constraint of walls that extend above the ceiling to the next level up.",
                char.ConvertFromUtf32(0xE898)));  // Up

            // ── Clash ─────────────────────────────────────────────────────────
            var clashPanel = application.CreateRibbonPanel("Lemoine Tools", "Clash");

            clashPanel.AddItem(Btn(
                "LT_ClashDefinitions", "Clash\nDefinitions", "OpenClashDefinitionsCommand",
                "Build and manage a library of named clash definitions (two element groups plus marking settings).",
                char.ConvertFromUtf32(0xE8FD)));  // List

            clashPanel.AddItem(Btn(
                "LT_ClashFinder", "Clash Finder\n& Dimension", "ClashFinderCommand",
                "Run saved clash definitions across selected views: detect clashes, place coloured tagged markers, and dimension them out to grids or slab edges.",
                char.ConvertFromUtf32(0xE721)));  // Zoom (find)

            clashPanel.AddItem(Btn(
                "LT_ClashElevationFinder", "Clash Finder\n& Elevation", "ClashElevationFinderCommand",
                "Run saved clash definitions across selected sections and elevations: detect clashes, place coloured round markers, and tag each with a spot elevation at the top, centre, or bottom of the round.",
                char.ConvertFromUtf32(0xE898)));  // Up (elevation/height)

            clashPanel.AddItem(Btn(
                "LT_RefineDimensions", "Refine\nDimensions", "RefineDimensionsCommand",
                "Re-dimension views that already have clash markers: dimension each marker to the nearest grid or slab edge visible in the view. Never detects clashes, creates callouts, or changes a view's scale.",
                char.ConvertFromUtf32(0xE70F)));  // Edit (pencil)

            // ── Copy from Link ────────────────────────────────────────────────
            // Promoted from Testing: pull linked elements into the host.
            var copyPanel = application.CreateRibbonPanel("Lemoine Tools", "Copy from Link");

            copyPanel.AddItem(Btn(
                "LT_CopyLinear", "Copy\nLinear", "CopyLinearCommand",
                "Copy linear linked runs into the host and split them into standard lengths, or replace them with a picked family at set intervals. Re-runs can process only changed elements.",
                char.ConvertFromUtf32(0xE71B)));  // Link

            copyPanel.AddItem(Btn(
                "LT_CopyGrids", "Copy\nGrids", "CopyGridsCommand",
                "Copy grids from a linked model into the host. Grids whose name already exists in the host are skipped.",
                char.ConvertFromUtf32(0xE80A)));  // GridView

            copyPanel.AddItem(Btn(
                "LT_CopyFromLink", "Copy\nElements", "CopyFromLinkCommand",
                "Copy elements from a linked model into the host: pick categories, then tick which family types within them to copy. Re-runs can process only changed elements.",
                char.ConvertFromUtf32(0xE8B3)));  // SelectAll

            // ── Coordination ──────────────────────────────────────────────────
            var coordPanel = application.CreateRibbonPanel("Lemoine Tools", "Coordination");

            coordPanel.AddItem(Btn(
                "LT_AlignCoordinates", "Align\nCoordinates", "AlignCoordinatesCommand",
                "Move the host Survey Point and/or Project Base Point to a picked grid intersection + level, then rotate/translate every selected link so its same-named grid intersection coincides, and publish the host's shared coordinates to those links.",
                char.ConvertFromUtf32(0xE809)));  // MapPin

            coordPanel.AddItem(Btn(
                "LT_CompareGrids", "Compare\nGrids", "CompareGridsCommand",
                "Read-only audit: compare grid lines across the host and loaded links once aligned, flagging grids that are missing, offset, rotated, or present in only one file.",
                char.ConvertFromUtf32(0xE80A)));  // GridView

            // ── Settings ──────────────────────────────────────────────────────
            var settingsPanel = application.CreateRibbonPanel("Lemoine Tools", "Settings");

            settingsPanel.AddItem(
                new PushButtonData("LT_OpenSettings", "Settings", dll,
                    "LemoineTools.Commands.OpenSettingsCommand")
                {
                    ToolTip    = "Open Lemoine Tools global settings — themes, UI size, and per-tool default options.",
                    LargeImage = CreateGearBitmap(32),
                    Image      = CreateGearBitmap(16),
                });

            settingsPanel.AddItem(Btn(
                "LT_Overview", "Overview", "OpenOverviewCommand",
                "Open the Tools Overview — a visual guide to every tool in the plugin and how they tie together.",
                char.ConvertFromUtf32(0xE946)));  // Info

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.ControlledApplication.FailuresProcessing -= LemoineFailureCapture.OnFailuresProcessing;
            application.DialogBoxShowing                         -= LemoineFailureCapture.OnDialogBoxShowing;
            return Result.Succeeded;
        }

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
