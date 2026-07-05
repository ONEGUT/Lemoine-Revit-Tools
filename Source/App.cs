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
using L = LemoineTools.Lemoine.LemoineStrings;

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

        // ── Upgrade & Link Models ────────────────────────────────────────────────────
        internal static LemoineTools.Tools.UpgradeLinks.UpgradeLinksScanHandler? UpgradeLinksScanHandler { get; private set; }
        internal static ExternalEvent?             UpgradeLinksScanEvent { get; private set; }
        internal static LemoineTools.Tools.UpgradeLinks.UpgradeLinksRunHandler?  UpgradeLinksRunHandler  { get; private set; }
        internal static ExternalEvent?             UpgradeLinksRunEvent  { get; private set; }

        // ── PDF Tools — PDF Region Wand ──────────────────────────────────────────────
        internal static LemoineTools.Tools.Testing.PdfRegionWand.PdfRegionWandEventHandler? PdfRegionWandHandler { get; private set; }
        internal static ExternalEvent?             PdfRegionWandEvent { get; private set; }

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
            // Load user-facing text for the saved language (English fallback) before any window opens.
            LemoineStrings.Load(LemoineSettings.Instance.Language);

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

            UpgradeLinksScanHandler = new LemoineTools.Tools.UpgradeLinks.UpgradeLinksScanHandler();
            UpgradeLinksScanEvent   = ExternalEvent.Create(UpgradeLinksScanHandler);
            UpgradeLinksRunHandler  = new LemoineTools.Tools.UpgradeLinks.UpgradeLinksRunHandler();
            UpgradeLinksRunEvent    = ExternalEvent.Create(UpgradeLinksRunHandler);

            PdfRegionWandHandler = new LemoineTools.Tools.Testing.PdfRegionWand.PdfRegionWandEventHandler();
            PdfRegionWandEvent   = ExternalEvent.Create(PdfRegionWandHandler);

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
            var filtersPanel = application.CreateRibbonPanel("Lemoine Tools", L.T("ribbon.panels.filtersLegends"));

            filtersPanel.AddItem(Btn(
                "LT_AutoFilters", L.T("ribbon.buttons.autoFilters.label"), "OpenFiltersSettingsCommand",
                L.T("ribbon.buttons.autoFilters.tip"),
                char.ConvertFromUtf32(0xE71C)));  // Filter

            filtersPanel.AddItem(Btn(
                "LT_LegendSettings", L.T("ribbon.buttons.legendCreation.label"), "OpenLegendSettingsCommand",
                L.T("ribbon.buttons.legendCreation.tip"),
                char.ConvertFromUtf32(0xE713)));  // Settings

            // ── Ceilings ──────────────────────────────────────────────────────
            // Large:    Ceiling Heatmap
            // Pulldown: Ceiling Grids → Make / Project / Reproject
            var ceilingsPanel = application.CreateRibbonPanel("Lemoine Tools", L.T("ribbon.panels.ceilings"));

            ceilingsPanel.AddItem(Btn(
                "LT_CeilingHeatmap", L.T("ribbon.buttons.ceilingHeatmap.label"), "CeilingHeatmapCommand",
                L.T("ribbon.buttons.ceilingHeatmap.tip"),
                char.ConvertFromUtf32(0xE81D)));  // Map

            var ceilingGridsPulldown = new PulldownButtonData("LT_CeilingGrids", L.T("ribbon.buttons.ceilingGrids.label"))
            {
                ToolTip    = L.T("ribbon.buttons.ceilingGrids.tip"),
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE80A)),
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE80A)),
            };

            var ceilingGridsBtn = ceilingsPanel.AddItem(ceilingGridsPulldown) as PulldownButton;

            ceilingGridsBtn?.AddPushButton(new PushButtonData(
                "LT_MakeCeilingGrids", L.T("ribbon.buttons.makeCeilingGrids.label"), dll,
                "LemoineTools.Commands.MakeCeilingGridsCommand")
            {
                ToolTip    = L.T("ribbon.buttons.makeCeilingGrids.tip"),
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE80A)),
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE80A)),
            });

            ceilingGridsBtn?.AddPushButton(new PushButtonData(
                "LT_ProjectCeilingGrids", L.T("ribbon.buttons.projectCeilingGrids.label"), dll,
                "LemoineTools.Commands.ProjectedCeilingGridsCommand")
            {
                ToolTip    = L.T("ribbon.buttons.projectCeilingGrids.tip"),
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE896)),
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE896)),
            });

            ceilingGridsBtn?.AddPushButton(new PushButtonData(
                "LT_ReprojectCeilingGrids", L.T("ribbon.buttons.reprojectCeilingGrids.label"), dll,
                "LemoineTools.Commands.ReprojectCeilingGridsCommand")
            {
                ToolTip    = L.T("ribbon.buttons.reprojectCeilingGrids.tip"),
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE895)),
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE895)),
            });

            // ── Views ─────────────────────────────────────────────────────────
            // Large:    Bulk Views by Level
            // Pulldown: Duplicate Views (Bulk Duplicate | By Template | Dependent)
            // Large:    Explode View by Trade
            var viewsPanel = application.CreateRibbonPanel("Lemoine Tools", L.T("ribbon.panels.views"));

            viewsPanel.AddItem(Btn(
                "LT_LinkViewsLevel", L.T("ribbon.buttons.linkViewsLevel.label"), "LinkViewsLevelCommand",
                L.T("ribbon.buttons.linkViewsLevel.tip"),
                char.ConvertFromUtf32(0xE8B7)));  // Layers

            var dupViewsPulldown = new PulldownButtonData("LT_DuplicateViews", L.T("ribbon.buttons.duplicateViews.label"))
            {
                ToolTip    = L.T("ribbon.buttons.duplicateViews.tip"),
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE8C8)),  // Copy
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE8C8)),
            };
            var dupViewsBtn = viewsPanel.AddItem(dupViewsPulldown) as PulldownButton;

            dupViewsBtn?.AddPushButton(new PushButtonData(
                "LT_ViewsBulkDuplicate", L.T("ribbon.buttons.viewsBulkDuplicate.label"), dll, "LemoineTools.Commands.ViewsBulkDuplicateCommand")
            {
                ToolTip    = L.T("ribbon.buttons.viewsBulkDuplicate.tip"),
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE8C8)),  // Copy
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE8C8)),
            });

            dupViewsBtn?.AddPushButton(new PushButtonData(
                "LT_ViewsByTemplate", L.T("ribbon.buttons.viewsByTemplate.label"), dll, "LemoineTools.Commands.ViewsByTemplateCommand")
            {
                ToolTip    = L.T("ribbon.buttons.viewsByTemplate.tip"),
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE8A9)),  // ViewAll
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE8A9)),
            });

            dupViewsBtn?.AddPushButton(new PushButtonData(
                "LT_ReplicateDependentViews", L.T("ribbon.buttons.replicateDependentViews.label"), dll, "LemoineTools.Commands.ReplicateDependentViewsCommand")
            {
                ToolTip    = L.T("ribbon.buttons.replicateDependentViews.tip"),
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE71B)),  // Link
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE71B)),
            });

            viewsPanel.AddItem(Btn(
                "LT_ExplodeViewByTrade", L.T("ribbon.buttons.explodeViewByTrade.label"), "ExplodeViewByTradeCommand",
                L.T("ribbon.buttons.explodeViewByTrade.tip"),
                char.ConvertFromUtf32(0xE8A9)));  // ViewAll

            // ── Sheets ────────────────────────────────────────────────────────
            // Place Dependent Views + Align Sheet Views (promoted from Testing) + Bulk Rename.
            var sheetsPanel = application.CreateRibbonPanel("Lemoine Tools", L.T("ribbon.panels.sheets"));

            sheetsPanel.AddItem(Btn(
                "LT_PlaceDepViews", L.T("ribbon.buttons.placeDependentViews.label"), "PlaceDependentViewsCommand",
                L.T("ribbon.buttons.placeDependentViews.tip"),
                char.ConvertFromUtf32(0xE7C3)));  // Page

            sheetsPanel.AddItem(Btn(
                "LT_AlignSheetViews", L.T("ribbon.buttons.alignSheetViews.label"), "AlignSheetViewsCommand",
                L.T("ribbon.buttons.alignSheetViews.tip"),
                char.ConvertFromUtf32(0xE744)));  // FullScreen / fit

            sheetsPanel.AddItem(Btn(
                "LT_BulkRename", L.T("ribbon.buttons.bulkRename.label"), "BulkRenameCommand",
                L.T("ribbon.buttons.bulkRename.tip"),
                char.ConvertFromUtf32(0xE8AC)));  // Rename

            // ── Export ────────────────────────────────────────────────────────
            var exportPanel = application.CreateRibbonPanel("Lemoine Tools", L.T("ribbon.panels.export"));

            exportPanel.AddItem(Btn(
                "LT_BulkExport", L.T("ribbon.buttons.bulkExport.label"), "BulkExportCommand",
                L.T("ribbon.buttons.bulkExport.tip"),
                char.ConvertFromUtf32(0xEDE1)));  // Share / Export

            exportPanel.AddItem(Btn(
                "LT_PrintView", L.T("ribbon.buttons.printView.label"), "PrintViewCommand",
                L.T("ribbon.buttons.printView.tip"),
                char.ConvertFromUtf32(0xE749)));  // Print

            // ── Modify ────────────────────────────────────────────────────────
            // Pulldown: Split Elements (4 sub-commands)
            // Large:    Extend Walls
            var modifyPanel = application.CreateRibbonPanel("Lemoine Tools", L.T("ribbon.panels.modify"));

            var splitPulldownData = new PulldownButtonData("LT_SplitElements", L.T("ribbon.buttons.splitElements.label"))
            {
                ToolTip    = L.T("ribbon.buttons.splitElements.tip"),
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE8C6)),  // Cut
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE8C6)),
            };
            var splitBtn = modifyPanel.AddItem(splitPulldownData) as PulldownButton;

            splitBtn?.AddPushButton(new PushButtonData(
                "LT_SplitByLevel", L.T("ribbon.buttons.splitByLevel.label"), dll, "LemoineTools.Commands.SplitByLevelCommand")
            {
                ToolTip    = L.T("ribbon.buttons.splitByLevel.tip"),
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE8C6)),
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE8C6)),
            });

            splitBtn?.AddPushButton(new PushButtonData(
                "LT_SplitByGrid", L.T("ribbon.buttons.splitByGrid.label"), dll, "LemoineTools.Commands.SplitByGridCommand")
            {
                ToolTip    = L.T("ribbon.buttons.splitByGrid.tip"),
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE80A)),
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE80A)),
            });

            splitBtn?.AddPushButton(new PushButtonData(
                "LT_SplitByReferencePlane", L.T("ribbon.buttons.splitByReferencePlane.label"), dll, "LemoineTools.Commands.SplitByReferencePlaneCommand")
            {
                ToolTip    = L.T("ribbon.buttons.splitByReferencePlane.tip"),
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE8C6)),
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE8C6)),
            });

            splitBtn?.AddPushButton(new PushButtonData(
                "LT_SplitByCell", L.T("ribbon.buttons.splitByCell.label"), dll, "LemoineTools.Commands.SplitByCellCommand")
            {
                ToolTip    = L.T("ribbon.buttons.splitByCell.tip"),
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE80A)),
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE80A)),
            });

            modifyPanel.AddItem(Btn(
                "LT_ExtendWalls", L.T("ribbon.buttons.extendWalls.label"), "ExtendWallsCommand",
                L.T("ribbon.buttons.extendWalls.tip"),
                char.ConvertFromUtf32(0xE898)));  // Up

            // ── Clash ─────────────────────────────────────────────────────────
            var clashPanel = application.CreateRibbonPanel("Lemoine Tools", L.T("ribbon.panels.clash"));

            clashPanel.AddItem(Btn(
                "LT_ClashDefinitions", L.T("ribbon.buttons.clashDefinitions.label"), "OpenClashDefinitionsCommand",
                L.T("ribbon.buttons.clashDefinitions.tip"),
                char.ConvertFromUtf32(0xE8FD)));  // List

            clashPanel.AddItem(Btn(
                "LT_ClashFinder", L.T("ribbon.buttons.clashFinder.label"), "ClashFinderCommand",
                L.T("ribbon.buttons.clashFinder.tip"),
                char.ConvertFromUtf32(0xE721)));  // Zoom (find)

            clashPanel.AddItem(Btn(
                "LT_ClashElevationFinder", L.T("ribbon.buttons.clashElevationFinder.label"), "ClashElevationFinderCommand",
                L.T("ribbon.buttons.clashElevationFinder.tip"),
                char.ConvertFromUtf32(0xE898)));  // Up (elevation/height)

            clashPanel.AddItem(Btn(
                "LT_RefineDimensions", L.T("ribbon.buttons.refineDimensions.label"), "RefineDimensionsCommand",
                L.T("ribbon.buttons.refineDimensions.tip"),
                char.ConvertFromUtf32(0xE70F)));  // Edit (pencil)

            // ── Copy from Link ────────────────────────────────────────────────
            // Promoted from Testing: pull linked elements into the host.
            var copyPanel = application.CreateRibbonPanel("Lemoine Tools", L.T("ribbon.panels.copyFromLink"));

            copyPanel.AddItem(Btn(
                "LT_CopyLinear", L.T("ribbon.buttons.copyLinear.label"), "CopyLinearCommand",
                L.T("ribbon.buttons.copyLinear.tip"),
                char.ConvertFromUtf32(0xE71B)));  // Link

            copyPanel.AddItem(Btn(
                "LT_CopyGrids", L.T("ribbon.buttons.copyGrids.label"), "CopyGridsCommand",
                L.T("ribbon.buttons.copyGrids.tip"),
                char.ConvertFromUtf32(0xE80A)));  // GridView

            copyPanel.AddItem(Btn(
                "LT_CopyFromLink", L.T("ribbon.buttons.copyFromLink.label"), "CopyFromLinkCommand",
                L.T("ribbon.buttons.copyFromLink.tip"),
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

            coordPanel.AddItem(Btn(
                "LT_UpgradeLinks", "Upgrade &\nLink Models", "UpgradeLinksCommand",
                "Pick Revit files from any folder, choose each one's link placement, then upgrade every file to the current version, save it (into a subfolder next to this model or over the original), and link it into the active model. Files are processed one at a time to keep memory flat.",
                char.ConvertFromUtf32(0xE896)));  // Download / Upgrade

            // ── PDF Tools ─────────────────────────────────────────────────────
            var pdfPanel = application.CreateRibbonPanel("Lemoine Tools", L.T("ribbon.panels.pdfTools"));

            pdfPanel.AddItem(Btn(
                "LT_PdfRegionWand", L.T("ribbon.buttons.pdfRegionWand.label"), "PdfRegionWandCommand",
                L.T("ribbon.buttons.pdfRegionWand.tip"),
                char.ConvertFromUtf32(0xE790)));  // Color (wand)

            // ── Settings ──────────────────────────────────────────────────────
            var settingsPanel = application.CreateRibbonPanel("Lemoine Tools", L.T("ribbon.panels.settings"));

            settingsPanel.AddItem(
                new PushButtonData("LT_OpenSettings", L.T("ribbon.buttons.openSettings.label"), dll,
                    "LemoineTools.Commands.OpenSettingsCommand")
                {
                    ToolTip    = L.T("ribbon.buttons.openSettings.tip"),
                    LargeImage = CreateGearBitmap(32),
                    Image      = CreateGearBitmap(16),
                });

            settingsPanel.AddItem(Btn(
                "LT_Overview", L.T("ribbon.buttons.overview.label"), "OpenOverviewCommand",
                L.T("ribbon.buttons.overview.tip"),
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
