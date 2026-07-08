using System.Reflection;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Tools.AutoFilters;
using LemoineTools.Tools.Ceilings;
using LemoineTools.Tools.LinkViews;
using LemoineTools.Tools.ModifyElements;
using LemoineTools.Tools.BulkExport;
using LemoineTools.Tools.Dimensioning;
using LemoineTools.Tools.ExplodeViews;
using LemoineTools.Tools.FiltersLegends.LegendCreator;
using System;
using L = LemoineTools.Framework.AppStrings;

namespace LemoineTools
{
    public class App : IExternalApplication
    {
        // Link Audit and Compare Grids buttons are deactivated on the ribbon (deliberately
        // left in the codebase) — flip to true to bring them back without re-adding code.
        private const bool ShowRetiredSetupTools = false;

        // Shared main-thread reload event for StepFlowWindow's "Reload" action (re-captures a
        // tool's document-derived options). Tool-agnostic, so one instance serves every window.
        internal static Framework.ReloadHandler? ReloadHandler { get; private set; }
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
        internal static LegendCreatorEventHandler?            LegendCreatorHandler            { get; private set; }
        internal static ExternalEvent?                        LegendCreatorEvent              { get; private set; }
        internal static DeleteFiltersEventHandler?            DeleteFiltersHandler            { get; private set; }
        internal static ExternalEvent?                        DeleteFiltersEvent              { get; private set; }
        internal static DeleteFiltersFromProjectEventHandler? DeleteFiltersFromProjectHandler { get; private set; }
        internal static ExternalEvent?                        DeleteFiltersFromProjectEvent   { get; private set; }
        // Opens the Delete-from-Project window from inside the Auto Filters window (main-thread setup).
        internal static OpenDeleteFromProjectEventHandler?    OpenDeleteFromProjectHandler    { get; private set; }
        internal static ExternalEvent?                        OpenDeleteFromProjectEvent      { get; private set; }

        // ── Link Views — Level ──────────────────────────────────────────────────────
        internal static LinkViewsLevelRunHandler?    LinkViewsLevelRunHandler    { get; private set; }
        internal static ExternalEvent?               LinkViewsLevelRunEvent      { get; private set; }

        // ── Bulk Views — By Link (merged into the Bulk Views tool) ──────────────────
        internal static LemoineTools.Tools.LinkViews.ViewsByLinkRunHandler? ViewsByLinkRunHandler { get; private set; }
        internal static ExternalEvent?               ViewsByLinkRunEvent         { get; private set; }

        // ── T10 — Scope Boxes ───────────────────────────────────────────────────────
        internal static LemoineTools.Tools.ScopeBoxes.ScopeBoxCreatorScanHandler? ScopeBoxCreatorScanHandler { get; private set; }
        internal static ExternalEvent?               ScopeBoxCreatorScanEvent    { get; private set; }
        internal static LemoineTools.Tools.ScopeBoxes.ScopeBoxCreatorRunHandler?  ScopeBoxCreatorRunHandler  { get; private set; }
        internal static ExternalEvent?               ScopeBoxCreatorRunEvent     { get; private set; }
        internal static LemoineTools.Tools.ScopeBoxes.ScopeBoxManagerScanHandler? ScopeBoxManagerScanHandler { get; private set; }
        internal static ExternalEvent?               ScopeBoxManagerScanEvent    { get; private set; }
        internal static LemoineTools.Tools.ScopeBoxes.ScopeBoxManagerRunHandler?  ScopeBoxManagerRunHandler  { get; private set; }
        internal static ExternalEvent?               ScopeBoxManagerRunEvent     { get; private set; }

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
        internal static LemoineTools.Tools.BulkExport.BulkExportPrintSetHandler? BulkExportPrintSetHandler { get; private set; }
        internal static ExternalEvent?             BulkExportPrintSetEvent { get; private set; }
        internal static PrintViewEventHandler?     PrintViewHandler    { get; private set; }
        internal static ExternalEvent?             PrintViewEvent      { get; private set; }

        // ── T05 — Clash (Definitions + Finder & Dimensioning) ───────────────────────
        internal static ClashPickEventHandler?      ClashPickHandler      { get; private set; }
        internal static ExternalEvent?              ClashPickEvent        { get; private set; }
        internal static ClashFinderEventHandler?    ClashFinderHandler    { get; private set; }
        internal static ExternalEvent?              ClashFinderEvent      { get; private set; }
        internal static ClashElevationFinderEventHandler? ClashElevationFinderHandler { get; private set; }
        internal static ExternalEvent?              ClashElevationFinderEvent   { get; private set; }
        internal static LemoineTools.Tools.Dimensioning.AutoDimension.SlabPickEventHandler? SlabPickHandler { get; private set; }
        internal static ExternalEvent?              SlabPickEvent         { get; private set; }
        internal static LemoineTools.Tools.Dimensioning.AutoDimension.Refine.RefineDimensionsEventHandler? RefineDimensionsHandler { get; private set; }
        internal static ExternalEvent?              RefineDimensionsEvent { get; private set; }

        // ── T06 — Views (Explode 3D View by Trade) ──────────────────────────────────
        internal static ExplodeViewByTradeEventHandler? ExplodeViewByTradeHandler { get; private set; }
        internal static ExternalEvent?                  ExplodeViewByTradeEvent   { get; private set; }

        // ── Testing — Place Dependent Views ─────────────────────────────────────────
        internal static LemoineTools.Tools.Sheets.PlaceDependentViews.PlaceDependentViewsEventHandler? PlaceDependentViewsHandler { get; private set; }
        internal static ExternalEvent?             PlaceDependentViewsEvent { get; private set; }

        // ── Testing — Align Sheet Views ─────────────────────────────────────────────
        internal static LemoineTools.Tools.Sheets.AlignSheetViews.AlignSheetViewsEventHandler? AlignSheetViewsHandler { get; private set; }
        internal static ExternalEvent?             AlignSheetViewsEvent { get; private set; }

        // ── Testing — Copy Linear Elements ──────────────────────────────────────────
        internal static LemoineTools.Tools.CopyFromLink.CopyLinearScanHandler? CopyLinearScanHandler { get; private set; }
        internal static ExternalEvent?             CopyLinearScanEvent  { get; private set; }
        internal static LemoineTools.Tools.CopyFromLink.CopyLinearRunHandler?  CopyLinearRunHandler  { get; private set; }
        internal static ExternalEvent?             CopyLinearRunEvent   { get; private set; }

        // ── Testing — Copy Datums from Link ─────────────────────────────────────────
        internal static LemoineTools.Tools.CopyFromLink.CopyDatumsRunHandler? CopyDatumsRunHandler { get; private set; }
        internal static ExternalEvent?             CopyDatumsRunEvent   { get; private set; }

        // ── Testing — Copy Elements from Link ───────────────────────────────────────
        internal static LemoineTools.Tools.CopyFromLink.CopyFromLinkScanHandler? CopyFromLinkScanHandler { get; private set; }
        internal static ExternalEvent?             CopyFromLinkScanEvent { get; private set; }
        internal static LemoineTools.Tools.CopyFromLink.CopyFromLinkRunHandler?  CopyFromLinkRunHandler  { get; private set; }
        internal static ExternalEvent?             CopyFromLinkRunEvent  { get; private set; }

        // ── Upgrade & Link Models ────────────────────────────────────────────────────
        internal static LemoineTools.Tools.Setup.UpgradeLinksScanHandler? UpgradeLinksScanHandler { get; private set; }
        internal static ExternalEvent?             UpgradeLinksScanEvent { get; private set; }
        internal static LemoineTools.Tools.Setup.UpgradeLinksRunHandler?  UpgradeLinksRunHandler  { get; private set; }
        internal static ExternalEvent?             UpgradeLinksRunEvent  { get; private set; }

        // ── Setup — Align Coordinates / Compare Grids / Push Coordinates to Links ───
        internal static LemoineTools.Tools.Setup.AlignCoordinatesRunHandler? AlignCoordinatesRunHandler { get; private set; }
        internal static ExternalEvent?             AlignCoordinatesRunEvent { get; private set; }
        internal static LemoineTools.Tools.Setup.CompareGridsRunHandler?     CompareGridsRunHandler     { get; private set; }
        internal static ExternalEvent?             CompareGridsRunEvent     { get; private set; }
        internal static LemoineTools.Tools.Setup.PushCoordinatesToLinksRunHandler? PushCoordinatesRunHandler { get; private set; }
        internal static ExternalEvent?             PushCoordinatesRunEvent  { get; private set; }


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
        internal static Framework.ToolsOverviewWindow? Overview { get; set; }

        // Link Audit window — read-only report, singleton like GlobalSettings/Overview
        internal static Framework.LinkAuditWindow? LinkAudit { get; set; }

        public Result OnStartup(UIControlledApplication application)
        {
            // Load user-facing text for the saved language (English fallback) before any window opens.
            AppStrings.Load(AppSettings.Instance.Language);

            // Surface Revit's transaction failures and modal dialogs into the running tool's
            // Output log (and suppress warning dialogs). Both handlers no-op unless a Lemoine
            // run is active, so non-Lemoine work is never affected.
            application.ControlledApplication.FailuresProcessing += RevitFailureCapture.OnFailuresProcessing;
            application.DialogBoxShowing                         += RevitFailureCapture.OnDialogBoxShowing;

            ReloadHandler = new Framework.ReloadHandler();
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
            LegendCreatorHandler            = new LegendCreatorEventHandler();
            LegendCreatorEvent              = ExternalEvent.Create(LegendCreatorHandler);
            DeleteFiltersHandler            = new DeleteFiltersEventHandler();
            DeleteFiltersEvent              = ExternalEvent.Create(DeleteFiltersHandler);
            DeleteFiltersFromProjectHandler = new DeleteFiltersFromProjectEventHandler();
            DeleteFiltersFromProjectEvent   = ExternalEvent.Create(DeleteFiltersFromProjectHandler);
            OpenDeleteFromProjectHandler    = new OpenDeleteFromProjectEventHandler();
            OpenDeleteFromProjectEvent      = ExternalEvent.Create(OpenDeleteFromProjectHandler);

            // ── Link Views — Level ────────────────────────────────────────────
            LinkViewsLevelRunHandler    = new LinkViewsLevelRunHandler();
            LinkViewsLevelRunEvent      = ExternalEvent.Create(LinkViewsLevelRunHandler);
            ViewsByLinkRunHandler       = new LemoineTools.Tools.LinkViews.ViewsByLinkRunHandler();
            ViewsByLinkRunEvent         = ExternalEvent.Create(ViewsByLinkRunHandler);
            ViewsByTemplateRunHandler   = new ViewsByTemplateRunHandler();
            ViewsByTemplateRunEvent     = ExternalEvent.Create(ViewsByTemplateRunHandler);
            ViewsBulkDuplicateRunHandler = new ViewsBulkDuplicateRunHandler();
            ViewsBulkDuplicateRunEvent   = ExternalEvent.Create(ViewsBulkDuplicateRunHandler);
            BulkRenameRunHandler = new LemoineTools.Tools.LinkViews.BulkRename.BulkRenameRunHandler();
            BulkRenameRunEvent   = ExternalEvent.Create(BulkRenameRunHandler);

            // ── Replicate Dependent Views ─────────────────────────────────────
            ReplicateDependentViewsRunHandler = new ReplicateDependentViewsRunHandler();
            ReplicateDependentViewsRunEvent   = ExternalEvent.Create(ReplicateDependentViewsRunHandler);

            // ── T10 — Scope Boxes ─────────────────────────────────────────────
            ScopeBoxCreatorScanHandler = new LemoineTools.Tools.ScopeBoxes.ScopeBoxCreatorScanHandler();
            ScopeBoxCreatorScanEvent   = ExternalEvent.Create(ScopeBoxCreatorScanHandler);
            ScopeBoxCreatorRunHandler  = new LemoineTools.Tools.ScopeBoxes.ScopeBoxCreatorRunHandler();
            ScopeBoxCreatorRunEvent    = ExternalEvent.Create(ScopeBoxCreatorRunHandler);
            ScopeBoxManagerScanHandler = new LemoineTools.Tools.ScopeBoxes.ScopeBoxManagerScanHandler();
            ScopeBoxManagerScanEvent   = ExternalEvent.Create(ScopeBoxManagerScanHandler);
            ScopeBoxManagerRunHandler  = new LemoineTools.Tools.ScopeBoxes.ScopeBoxManagerRunHandler();
            ScopeBoxManagerRunEvent    = ExternalEvent.Create(ScopeBoxManagerRunHandler);

            // ── Testing — new tools ───────────────────────────────────────────
            BulkExportHandler   = new BulkExportEventHandler();
            BulkExportEvent     = ExternalEvent.Create(BulkExportHandler);
            BulkExportPrintSetHandler = new LemoineTools.Tools.BulkExport.BulkExportPrintSetHandler();
            BulkExportPrintSetEvent   = ExternalEvent.Create(BulkExportPrintSetHandler);
            PrintViewHandler    = new PrintViewEventHandler();
            PrintViewEvent      = ExternalEvent.Create(PrintViewHandler);
            ClashPickHandler      = new ClashPickEventHandler();
            ClashPickEvent        = ExternalEvent.Create(ClashPickHandler);
            ClashFinderHandler    = new ClashFinderEventHandler();
            ClashFinderEvent      = ExternalEvent.Create(ClashFinderHandler);
            ClashElevationFinderHandler = new ClashElevationFinderEventHandler();
            ClashElevationFinderEvent   = ExternalEvent.Create(ClashElevationFinderHandler);
            SlabPickHandler       = new LemoineTools.Tools.Dimensioning.AutoDimension.SlabPickEventHandler();
            SlabPickEvent         = ExternalEvent.Create(SlabPickHandler);
            RefineDimensionsHandler = new LemoineTools.Tools.Dimensioning.AutoDimension.Refine.RefineDimensionsEventHandler();
            RefineDimensionsEvent   = ExternalEvent.Create(RefineDimensionsHandler);

            // ── T06 — Views ───────────────────────────────────────────────────
            ExplodeViewByTradeHandler = new ExplodeViewByTradeEventHandler();
            ExplodeViewByTradeEvent   = ExternalEvent.Create(ExplodeViewByTradeHandler);

            PlaceDependentViewsHandler = new LemoineTools.Tools.Sheets.PlaceDependentViews.PlaceDependentViewsEventHandler();
            PlaceDependentViewsEvent   = ExternalEvent.Create(PlaceDependentViewsHandler);
            AlignSheetViewsHandler     = new LemoineTools.Tools.Sheets.AlignSheetViews.AlignSheetViewsEventHandler();
            AlignSheetViewsEvent       = ExternalEvent.Create(AlignSheetViewsHandler);
            CopyLinearScanHandler = new LemoineTools.Tools.CopyFromLink.CopyLinearScanHandler();
            CopyLinearScanEvent   = ExternalEvent.Create(CopyLinearScanHandler);
            CopyLinearRunHandler  = new LemoineTools.Tools.CopyFromLink.CopyLinearRunHandler();
            CopyLinearRunEvent    = ExternalEvent.Create(CopyLinearRunHandler);
            CopyDatumsRunHandler  = new LemoineTools.Tools.CopyFromLink.CopyDatumsRunHandler();
            CopyDatumsRunEvent    = ExternalEvent.Create(CopyDatumsRunHandler);
            CopyFromLinkScanHandler = new LemoineTools.Tools.CopyFromLink.CopyFromLinkScanHandler();
            CopyFromLinkScanEvent   = ExternalEvent.Create(CopyFromLinkScanHandler);
            CopyFromLinkRunHandler  = new LemoineTools.Tools.CopyFromLink.CopyFromLinkRunHandler();
            CopyFromLinkRunEvent    = ExternalEvent.Create(CopyFromLinkRunHandler);

            UpgradeLinksScanHandler = new LemoineTools.Tools.Setup.UpgradeLinksScanHandler();
            UpgradeLinksScanEvent   = ExternalEvent.Create(UpgradeLinksScanHandler);
            UpgradeLinksRunHandler  = new LemoineTools.Tools.Setup.UpgradeLinksRunHandler();
            UpgradeLinksRunEvent    = ExternalEvent.Create(UpgradeLinksRunHandler);

            AlignCoordinatesRunHandler = new LemoineTools.Tools.Setup.AlignCoordinatesRunHandler();
            AlignCoordinatesRunEvent   = ExternalEvent.Create(AlignCoordinatesRunHandler);
            CompareGridsRunHandler     = new LemoineTools.Tools.Setup.CompareGridsRunHandler();
            CompareGridsRunEvent       = ExternalEvent.Create(CompareGridsRunHandler);
            PushCoordinatesRunHandler  = new LemoineTools.Tools.Setup.PushCoordinatesToLinksRunHandler();
            PushCoordinatesRunEvent    = ExternalEvent.Create(PushCoordinatesRunHandler);

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

            try { application.CreateRibbonTab("Lemoine Tools"); } catch (Exception __lex) { DiagnosticsLog.Swallowed("App: create ribbon tab", __lex); }
            var dll = Assembly.GetExecutingAssembly().Location;

            // Local helper — avoids repeating the dll path and namespace prefix on every button.
            PushButtonData Btn(string id, string label, string cmd, string tip, string? glyph = null)
            {
                var d = new PushButtonData(id, label, dll, "LemoineTools.Commands." + cmd) { ToolTip = tip };
                if (glyph != null) { d.LargeImage = CreateGlyphBitmap(32, glyph);
                                     d.Image      = CreateGlyphBitmap(16, glyph); }
                return d;
            }

            // Ribbon panels are built in coordination-lifecycle order: Setup (get links in and
            // aligned) → Copy from Link (pull reference geometry into the host) → Modify (prep
            // elements) → Ceilings → Views (build the working-view structure) → Filters &
            // Legends (graphics once views exist) → Dimensioning (the coordination loop) →
            // Sheets (document it) → Export (publish) → Settings → Developer.

            // ── Setup ─────────────────────────────────────────────────────────
            // Upgrade & link models, then align/verify/publish shared coordinates.
            var setupPanel = application.CreateRibbonPanel("Lemoine Tools", L.T("ribbon.panels.setup"));

            setupPanel.AddItem(Btn(
                "LT_UpgradeLinks", L.T("ribbon.buttons.upgradeLinks.label"), "UpgradeLinksCommand",
                L.T("ribbon.buttons.upgradeLinks.tip"),
                char.ConvertFromUtf32(0xE896)));  // Download / Upgrade

            // Link Audit and Compare Grids are deactivated (not deleted) — flip
            // ShowRetiredSetupTools to true to bring them back on the ribbon.
            if (ShowRetiredSetupTools)
            {
                setupPanel.AddItem(Btn(
                    "LT_LinkAudit", L.T("ribbon.buttons.linkAudit.label"), "LinkAuditCommand",
                    L.T("ribbon.buttons.linkAudit.tip"),
                    char.ConvertFromUtf32(0xE9D9)));  // Diagnostic
            }

            setupPanel.AddItem(Btn(
                "LT_AlignCoordinates", L.T("ribbon.buttons.alignCoordinates.label"), "AlignCoordinatesCommand",
                L.T("ribbon.buttons.alignCoordinates.tip"),
                char.ConvertFromUtf32(0xE809)));  // MapPin

            if (ShowRetiredSetupTools)
            {
                setupPanel.AddItem(Btn(
                    "LT_CompareGrids", L.T("ribbon.buttons.compareGrids.label"), "CompareGridsCommand",
                    L.T("ribbon.buttons.compareGrids.tip"),
                    char.ConvertFromUtf32(0xE80A)));  // GridView
            }

            setupPanel.AddItem(Btn(
                "LT_PushCoordinates", L.T("ribbon.buttons.pushCoordinates.label"), "PushCoordinatesToLinksCommand",
                L.T("ribbon.buttons.pushCoordinates.tip"),
                char.ConvertFromUtf32(0xE896)));  // Download / Upgrade

            // ── Copy from Link ────────────────────────────────────────────────
            // Pull reference geometry out of trusted links into the host.
            var copyPanel = application.CreateRibbonPanel("Lemoine Tools", L.T("ribbon.panels.copyFromLink"));

            copyPanel.AddItem(Btn(
                "LT_CopyDatums", L.T("ribbon.buttons.copyDatums.label"), "CopyDatumsCommand",
                L.T("ribbon.buttons.copyDatums.tip"),
                char.ConvertFromUtf32(0xE80A)));  // GridView

            copyPanel.AddItem(Btn(
                "LT_CopyLinear", L.T("ribbon.buttons.copyLinear.label"), "CopyLinearCommand",
                L.T("ribbon.buttons.copyLinear.tip"),
                char.ConvertFromUtf32(0xE71B)));  // Link

            copyPanel.AddItem(Btn(
                "LT_CopyFromLink", L.T("ribbon.buttons.copyFromLink.label"), "CopyFromLinkCommand",
                L.T("ribbon.buttons.copyFromLink.tip"),
                char.ConvertFromUtf32(0xE8B3)));  // SelectAll

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
            // Pulldown: Scope Boxes (Creator | Manager) — moved to front, scope boxes
            //           come before the views that use them.
            // Large:    Bulk Views by Level
            // Pulldown: Duplicate Views (Bulk Duplicate | By Template | Dependent)
            // Large:    Explode View by Trade
            var viewsPanel = application.CreateRibbonPanel("Lemoine Tools", L.T("ribbon.panels.views"));

            // Scope Boxes pulldown — Creator now; the Manager joins here when it lands.
            var scopeBoxPulldown = new PulldownButtonData("LT_ScopeBoxes", L.T("ribbon.buttons.scopeBoxes.label"))
            {
                ToolTip    = L.T("ribbon.buttons.scopeBoxes.tip"),
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE7B8)),  // CubeShape
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE7B8)),
            };
            var scopeBoxBtn = viewsPanel.AddItem(scopeBoxPulldown) as PulldownButton;

            scopeBoxBtn?.AddPushButton(new PushButtonData(
                "LT_ScopeBoxCreator", L.T("ribbon.buttons.scopeBoxCreator.label"), dll, "LemoineTools.Commands.ScopeBoxCreatorCommand")
            {
                ToolTip    = L.T("ribbon.buttons.scopeBoxCreator.tip"),
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE7B8)),
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE7B8)),
            });

            scopeBoxBtn?.AddPushButton(new PushButtonData(
                "LT_ScopeBoxManager", L.T("ribbon.buttons.scopeBoxManager.label"), dll, "LemoineTools.Commands.ScopeBoxManagerCommand")
            {
                ToolTip    = L.T("ribbon.buttons.scopeBoxManager.tip"),
                LargeImage = CreateGlyphBitmap(32, char.ConvertFromUtf32(0xE8FD)),  // ListView / manage
                Image      = CreateGlyphBitmap(16, char.ConvertFromUtf32(0xE8FD)),
            });

            viewsPanel.AddItem(Btn(
                "LT_BulkViews", L.T("ribbon.buttons.bulkViews.label"), "BulkViewsCommand",
                L.T("ribbon.buttons.bulkViews.tip"),
                char.ConvertFromUtf32(0xE8A9)));  // ViewAll

            viewsPanel.AddItem(Btn(
                "LT_ExplodeViewByTrade", L.T("ribbon.buttons.explodeViewByTrade.label"), "ExplodeViewByTradeCommand",
                L.T("ribbon.buttons.explodeViewByTrade.tip"),
                char.ConvertFromUtf32(0xE8A9)));  // ViewAll

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

            // ── Dimensioning ──────────────────────────────────────────────────
            var dimensioningPanel = application.CreateRibbonPanel("Lemoine Tools", L.T("ribbon.panels.dimensioning"));

            dimensioningPanel.AddItem(Btn(
                "LT_ClashDefinitions", L.T("ribbon.buttons.clashDefinitions.label"), "OpenClashDefinitionsCommand",
                L.T("ribbon.buttons.clashDefinitions.tip"),
                char.ConvertFromUtf32(0xE8FD)));  // List

            dimensioningPanel.AddItem(Btn(
                "LT_ClashFinder", L.T("ribbon.buttons.clashFinder.label"), "ClashFinderCommand",
                L.T("ribbon.buttons.clashFinder.tip"),
                char.ConvertFromUtf32(0xE721)));  // Zoom (find)

            dimensioningPanel.AddItem(Btn(
                "LT_ClashElevationFinder", L.T("ribbon.buttons.clashElevationFinder.label"), "ClashElevationFinderCommand",
                L.T("ribbon.buttons.clashElevationFinder.tip"),
                char.ConvertFromUtf32(0xE898)));  // Up (elevation/height)

            dimensioningPanel.AddItem(Btn(
                "LT_RefineDimensions", L.T("ribbon.buttons.refineDimensions.label"), "RefineDimensionsCommand",
                L.T("ribbon.buttons.refineDimensions.tip"),
                char.ConvertFromUtf32(0xE70F)));  // Edit (pencil)

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

            // Developer panel: created on demand for future debug harnesses (see
            // CLAUDE.md "Crashes & Large Ambiguous Issues"). None are active right now —
            // the Scope Box Probe that lived here has been removed (its findings are
            // captured in CLAUDE.md and in ScopeBoxCreatorRunHandler's comments).

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.ControlledApplication.FailuresProcessing -= RevitFailureCapture.OnFailuresProcessing;
            application.DialogBoxShowing                         -= RevitFailureCapture.OnDialogBoxShowing;
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
                Text       = "⚙",
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
