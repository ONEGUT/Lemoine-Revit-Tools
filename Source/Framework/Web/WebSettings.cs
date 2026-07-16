using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LemoineTools.Tools.Dimensioning;               // ClashDimensionSettings
using LemoineTools.Tools.Dimensioning.AutoDimension;  // AutoDimensionConfig
using LemoineTools.Tools.Setup;                       // UpgradeLinksSettings / UpgradeDestination / UpgradeLinksViewModel
using LemoineTools.Tools.Ceilings;                    // CeilingHeatmapSettings / MakeCeilingGridsSettings
using LemoineTools.Tools.ScopeBoxes;                  // ScopeBoxSettings
using LemoineTools.Tools.BulkExport;                  // BulkExportSettings
using LemoineTools.Tools.CopyFromLink;                // CopyLinearSettings / CopyFromLinkSettings

namespace LemoineTools.Framework.Web
{
    /// <summary>
    /// Builds the serializable payload for the HTML Global Settings window (settings.html).
    /// Only the General tab (theme / UI size / language / diagnostics) is migrated to the web
    /// surface so far — the other ribbon-group tabs still render "managed in the classic
    /// window" placeholders. Theme / size / language changes go through the same
    /// <see cref="AppSettings"/> setters as the WPF window, so they persist and propagate live
    /// to every open web window via ThemeChanged / UiSizeChanged.
    /// </summary>
    public static class WebSettings
    {
        // Tab ids mirror GlobalSettingsWindow._navDefs; only "general" carries web content today.
        private static readonly (string Id, string LabelKey)[] Tabs =
        {
            ("general",      "globalSettings.nav.general"),
            ("naming",       "globalSettings.nav.naming"),
            ("setup",        "globalSettings.nav.setup"),
            ("copy",         "globalSettings.nav.copy"),
            ("ceilings",     "globalSettings.nav.ceilings"),
            ("views",        "globalSettings.nav.views"),
            ("filters",      "globalSettings.nav.filters"),
            ("dimensioning", "globalSettings.nav.dimensioning"),
            ("export",       "globalSettings.nav.export"),
        };

        // Tabs backed by the spec model (§ BuildTab) below — a tab is an ordered list of WebInput
        // rows the page renders with the shared lemoine.js factories, each auto-saving on change.
        // "naming" and "filters" still render the placeholder (bespoke editors, ported separately).
        private static readonly HashSet<string> SpecTabs =
            new HashSet<string>(StringComparer.Ordinal) { "setup", "copy", "ceilings", "views", "dimensioning", "export" };

        public static Dictionary<string, object?> BuildPayload(string activeTabId)
        {
            var payload = new Dictionary<string, object?>
            {
                ["title"]   = AppStrings.T("globalSettings.window.title"),
                ["close"]   = AppStrings.T("globalSettings.window.close"),
                ["build"]   = BuildInfo.Summary,
                ["active"]  = activeTabId,
                ["tabs"]    = Tabs.Select(t => new Dictionary<string, object?>
                {
                    ["id"]       = t.Id,
                    ["label"]    = AppStrings.T(t.LabelKey),
                    // "naming" is web-backed too, but its payload (parameter snapshot) is merged
                    // in by WebSettingsWindow, which owns the snapshot — not built here.
                    ["migrated"] = t.Id == "general" || t.Id == "naming" || SpecTabs.Contains(t.Id),
                }).ToList(),
                ["general"] = BuildGeneral(),
                ["notMigrated"] = AppStrings.T("globalSettings.web.notMigrated"),
            };

            // A spec tab ships its rows; the General tab keeps its bespoke payload above.
            if (SpecTabs.Contains(activeTabId))
            {
                var spec = BuildTab(activeTabId);
                payload["tab"] = new Dictionary<string, object?>
                {
                    ["id"]     = activeTabId,
                    ["header"] = spec.Header,
                    ["note"]   = spec.Note,
                    ["rows"]   = spec.Rows,
                };
            }

            return payload;
        }

        private static Dictionary<string, object?> BuildGeneral()
        {
            var s = AppSettings.Instance;

            var themes = ThemePalette.All.Select(t => new Dictionary<string, object?>
            {
                ["name"]   = t.Name,
                ["active"] = t == s.ActiveTheme,
                ["dark"]   = IsDark(t),
                ["bg"]     = Hex(t.Bg),
                ["raised"] = Hex(t.Raised),
                ["border"] = Hex(t.Border),
                ["text"]   = Hex(t.Text),
                ["accent"] = Hex(t.Accent),
                ["green"]  = Hex(t.Green),
                ["red"]    = Hex(t.Red),
            }).ToList();

            var sizes = Enum.GetValues(typeof(UiSize)).Cast<UiSize>().Select(sz => new Dictionary<string, object?>
            {
                ["id"]     = sz.ToString(),
                ["name"]   = sz.ToString(),
                ["desc"]   = SizeDesc(sz),
                ["active"] = sz == s.UiSize,
            }).ToList();

            var cultures = AppStrings.AvailableCultures();
            if (cultures.Count == 0) cultures = new List<string> { s.Language };
            var languages = cultures.Select(c => new Dictionary<string, object?>
            {
                ["culture"] = c,
                ["name"]    = LanguageDisplayName(c),
                ["active"]  = c == s.Language,
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["header"]      = AppStrings.T("globalSettings.general.header"),
                ["themeLabel"]  = AppStrings.T("globalSettings.general.colorTheme"),
                ["darkLabel"]   = AppStrings.T("globalSettings.general.dark"),
                ["lightLabel"]  = AppStrings.T("globalSettings.general.light"),
                ["sizeLabel"]   = AppStrings.T("globalSettings.general.uiSize"),
                ["langLabel"]   = AppStrings.T("globalSettings.general.language"),
                ["langHint"]    = AppStrings.T("globalSettings.general.languageHint"),
                ["diagLabel"]   = AppStrings.T("globalSettings.general.diagnostics"),
                ["diagDesc"]    = AppStrings.T("globalSettings.general.diagnosticsDesc"),
                ["logPath"]     = DiagnosticsLog.LogFilePath,
                ["openLog"]     = AppStrings.T("globalSettings.general.openLog"),
                ["activeBadge"] = AppStrings.T("globalSettings.general.active"),
                ["themes"]      = themes,
                ["sizes"]       = sizes,
                ["languages"]   = languages,
            };
        }

        private static string SizeDesc(UiSize sz) =>
            sz == UiSize.Small      ? AppStrings.T("globalSettings.general.sizeSmall") :
            sz == UiSize.Large      ? AppStrings.T("globalSettings.general.sizeLarge") :
            sz == UiSize.ExtraLarge ? AppStrings.T("globalSettings.general.sizeExtraLarge") :
                                      AppStrings.T("globalSettings.general.sizeMedium");

        private static bool IsDark(ThemePalette t)
        {
            var c = t.Bg.Color;
            return 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B < 128;
        }

        private static string Hex(System.Windows.Media.SolidColorBrush brush)
        {
            var c = brush.Color;
            return "#" + c.R.ToString("x2", CultureInfo.InvariantCulture)
                       + c.G.ToString("x2", CultureInfo.InvariantCulture)
                       + c.B.ToString("x2", CultureInfo.InvariantCulture);
        }

        private static string LanguageDisplayName(string culture)
        {
            try { return new CultureInfo(culture).NativeName; }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"WebSettings: resolve culture display name for '{culture}'", ex);
                return culture;
            }
        }

        // ── Actions from the page ─────────────────────────────────────────────

        public static void ApplyTheme(string name)
        {
            var theme = ThemePalette.All.FirstOrDefault(t => t.Name == name);
            if (theme != null) AppSettings.Instance.SetTheme(theme);
        }

        public static void ApplySize(string id)
        {
            if (Enum.TryParse<UiSize>(id, out var sz)) AppSettings.Instance.SetUiSize(sz);
        }

        public static void ApplyLanguage(string culture)
        {
            if (!string.IsNullOrWhiteSpace(culture)) AppSettings.Instance.SetLanguage(culture);
        }

        public static void OpenLog() => DiagnosticsLog.OpenInDefaultViewer();

        /// <summary>Apply one spec-tab field change from the page. Rebuilds the tab (cheap — reads
        /// the tool settings singletons) to resolve the field's applier, then invokes it. A field id
        /// with no applier (a header/separator, or a stale message) is logged, never a silent no-op.</summary>
        public static void SetField(string tabId, string fieldId, object? value)
        {
            if (!SpecTabs.Contains(tabId)) { DiagnosticsLog.Warn("WebSettings: setField for non-spec tab", tabId); return; }
            var spec = BuildTab(tabId);
            if (spec.Appliers.TryGetValue(fieldId, out var apply))
            {
                try { apply(value); }
                catch (Exception ex) { DiagnosticsLog.Error($"WebSettings: apply '{tabId}/{fieldId}'", ex); }
            }
            else DiagnosticsLog.Warn("WebSettings: setField unknown field", $"{tabId}/{fieldId}");
        }

        // ═══ Spec-tab model ═══════════════════════════════════════════════════════
        // A settings tab = an ordered list of WebInput rows + a per-field applier. Mirrors the WPF
        // GlobalSettingsWindow group/Dimensions tabs one-to-one (same AppStrings keys, same settings
        // singletons, same value transforms), auto-saving on every change — no Apply button.

        private static TabSpec BuildTab(string id)
        {
            var t = new TabSpec();
            switch (id)
            {
                case "dimensioning": BuildDimensioningTab(t); break;
                case "setup":        BuildSetupTab(t);        break;
                case "ceilings":     BuildCeilingsTab(t);     break;
                case "views":        BuildViewsTab(t);        break;
                case "export":       BuildExportTab(t);       break;
                case "copy":         BuildCopyTab(t);         break;
            }
            return t;
        }

        // ── Dimensioning (mirrors GlobalSettingsWindow.Dimensions.cs) ──────────────
        private static void BuildDimensioningTab(TabSpec t)
        {
            var cfg   = AutoDimensionConfig.Instance;
            var clash = ClashDimensionSettings.Instance;
            t.Header = AppStrings.T("globalSettings.dim.header");
            t.Note   = AppStrings.T("globalSettings.dim.intro");

            string grid   = AppStrings.T("globalSettings.dim.destGrid");
            string slab   = AppStrings.T("globalSettings.dim.destSlab");
            string manual = AppStrings.T("globalSettings.dim.destManual");

            t.Section(AppStrings.T("globalSettings.dim.runDefaults"));
            t.Select("dest", AppStrings.T("globalSettings.dim.defaultDest"), new[] { grid, slab, manual },
                cfg.TargetType == "SlabEdge" ? slab : cfg.TargetType == "ManualDatum" ? manual : grid,
                sel => { cfg.TargetType = sel == slab ? "SlabEdge" : sel == manual ? "ManualDatum" : "Grid"; cfg.Save(); });

            t.Toggle("chain",    AppStrings.T("globalSettings.dim.chainLabel"),    AppStrings.T("globalSettings.dim.chainDesc"),    cfg.ChainAligned,        v => { cfg.ChainAligned = v; cfg.Save(); });
            t.Toggle("density",  AppStrings.T("globalSettings.dim.densityLabel"),  AppStrings.T("globalSettings.dim.densityDesc"),  cfg.DensityChaining,     v => { cfg.DensityChaining = v; cfg.Save(); });
            t.Toggle("callouts", AppStrings.T("globalSettings.dim.calloutsLabel"), AppStrings.T("globalSettings.dim.calloutsDesc"), cfg.DenseCalloutsEnabled, v => { cfg.DenseCalloutsEnabled = v; cfg.Save(); });
            t.Toggle("bothAxes", AppStrings.T("globalSettings.dim.bothAxesLabel"), AppStrings.T("globalSettings.dim.bothAxesDesc"), cfg.DimensionBothAxes,   v => { cfg.DimensionBothAxes = v; cfg.Save(); });
            t.Toggle("links",    AppStrings.T("globalSettings.dim.linksLabel"),    AppStrings.T("globalSettings.dim.linksDesc"),    cfg.IncludeLinks,        v => { cfg.IncludeLinks = v; cfg.Save(); });
            t.Toggle("slabDiag", AppStrings.T("globalSettings.dim.slabDiagLabel"), AppStrings.T("globalSettings.dim.slabDiagDesc"), cfg.DiagnoseSlabEdge,    v => { cfg.DiagnoseSlabEdge = v; cfg.Save(); });

            t.Stepper("clusterDist", AppStrings.T("globalSettings.dim.clusterDist"), AppStrings.T("globalSettings.dim.clusterDistDesc"),
                cfg.ClusterLinkPaperIn, 0, 4, 0.0625, 4, v => { cfg.ClusterLinkPaperIn = v; cfg.Save(); });
            t.Stepper("runLineTol", AppStrings.T("globalSettings.dim.runLineTol"), AppStrings.T("globalSettings.dim.runLineTolDesc"),
                cfg.RunCrossPaperIn, 0, 1, 0.03125, 5, v => { cfg.RunCrossPaperIn = v; cfg.Save(); });
            t.Stepper("calloutMin", AppStrings.T("globalSettings.dim.calloutMin"), AppStrings.T("globalSettings.dim.calloutMinDesc"),
                cfg.CalloutMinClashes, 2, 50, 1, 0, v => { cfg.CalloutMinClashes = (int)Math.Round(v); cfg.Save(); });
            t.Stepper("storeyMargin", AppStrings.T("globalSettings.dim.storeyMargin"), AppStrings.T("globalSettings.dim.storeyMarginDesc"),
                clash.StoreyMarginMm / 304.8, 0, 12, 0.5, 2, v => { clash.StoreyMarginMm = v * 304.8; clash.Save(); });

            t.Sep();
            t.Section(AppStrings.T("globalSettings.dim.targetRes"));
            t.Stepper("maxDist", AppStrings.T("globalSettings.dim.maxDist"), AppStrings.T("globalSettings.dim.maxDistDesc"),
                cfg.MaxDistanceFt, 1, 200, 1, 1, v => { cfg.MaxDistanceFt = v; cfg.Save(); });
            t.Stepper("axisTol", AppStrings.T("globalSettings.dim.axisTol"), AppStrings.T("globalSettings.dim.axisTolDesc"),
                cfg.AxisToleranceDeg, 0, 45, 1, 1, v => { cfg.AxisToleranceDeg = v; cfg.Save(); });
            t.Stepper("ambiguity", AppStrings.T("globalSettings.dim.ambiguity"), AppStrings.T("globalSettings.dim.ambiguityDesc"),
                cfg.AmbiguityThresholdFt * 12.0, 0, 12, 0.25, 2, v => { cfg.AmbiguityThresholdFt = v / 12.0; cfg.Save(); });
            t.Stepper("slabAxisW", AppStrings.T("globalSettings.dim.slabAxisW"), AppStrings.T("globalSettings.dim.slabAxisWDesc"),
                cfg.SlabAxisWeight, 0, 1, 0.01, 2, v => { cfg.SlabAxisWeight = v; cfg.Save(); });
            t.Stepper("slabLenW", AppStrings.T("globalSettings.dim.slabLenW"), AppStrings.T("globalSettings.dim.slabLenWDesc"),
                cfg.SlabLengthWeight, 0, 1, 0.01, 2, v => { cfg.SlabLengthWeight = v; cfg.Save(); });
            t.Text("dimTypeName", AppStrings.T("globalSettings.dim.dimTypeName"), cfg.DimensionTypeName,
                v => { cfg.DimensionTypeName = v ?? ""; cfg.Save(); });

            t.Sep();
            t.Section(AppStrings.T("globalSettings.dim.layoutSpacing"));
            t.Stepper("firstOffset", AppStrings.T("globalSettings.dim.firstOffset"), AppStrings.T("globalSettings.dim.firstOffsetDesc"),
                cfg.Layout.FirstOffsetFt * 12.0, 0, 2, 0.0625, 4, v => { cfg.Layout.FirstOffsetFt = v / 12.0; cfg.Save(); });
            t.Stepper("stringSpacing", AppStrings.T("globalSettings.dim.stringSpacing"), AppStrings.T("globalSettings.dim.stringSpacingDesc"),
                cfg.Layout.StringSpacingFt * 12.0, 0, 2, 0.0625, 4, v => { cfg.Layout.StringSpacingFt = v / 12.0; cfg.Save(); });
            t.Stepper("roundingPrec", AppStrings.T("globalSettings.dim.roundingPrec"), AppStrings.T("globalSettings.dim.roundingPrecDesc"),
                cfg.Layout.PrecisionFt * 12.0, 0, 2, 0.0625, 4, v => { cfg.Layout.PrecisionFt = v / 12.0; cfg.Save(); });
            t.Stepper("textHeight", AppStrings.T("globalSettings.dim.textHeight"), AppStrings.T("globalSettings.dim.textHeightDesc"),
                cfg.Layout.TextHeightFt * 12.0, 0.03125, 1, 0.015625, 4, v => { cfg.Layout.TextHeightFt = v / 12.0; cfg.Save(); });

            t.Sep();
            t.Section(AppStrings.T("globalSettings.dim.advLayout"));
            t.Toggle("alignRows", AppStrings.T("globalSettings.dim.alignRowsLabel"), AppStrings.T("globalSettings.dim.alignRowsDesc"), cfg.Layout.AlignSharedRows,    v => { cfg.Layout.AlignSharedRows = v; cfg.Save(); });
            t.Toggle("stagger",   AppStrings.T("globalSettings.dim.staggerLabel"),   AppStrings.T("globalSettings.dim.staggerDesc"),   cfg.Layout.StaggerStackedText, v => { cfg.Layout.StaggerStackedText = v; cfg.Save(); });
            t.Toggle("snapshot",  AppStrings.T("globalSettings.dim.snapshotLabel"),  AppStrings.T("globalSettings.dim.snapshotDesc"),  cfg.DumpLayoutSnapshots,       v => { cfg.DumpLayoutSnapshots = v; cfg.Save(); });
            t.Stepper("maxStackedRows", AppStrings.T("globalSettings.dim.maxStackedRows"), AppStrings.T("globalSettings.dim.maxStackedRowsDesc"),
                cfg.Layout.MaxOffsetSteps, 1, 20, 1, 0, v => { cfg.Layout.MaxOffsetSteps = (int)Math.Round(v); cfg.Save(); });
            t.Stepper("repairPasses", AppStrings.T("globalSettings.dim.repairPasses"), AppStrings.T("globalSettings.dim.repairPassesDesc"),
                cfg.Layout.MaxRepairPasses, 0, 10, 1, 0, v => { cfg.Layout.MaxRepairPasses = (int)Math.Round(v); cfg.Save(); });
        }

        // ── Setup (mirrors BuildSetupGroupContent) ─────────────────────────────────
        private static void BuildSetupTab(TabSpec t)
        {
            var ul = UpgradeLinksSettings.Instance;
            t.Header = AppStrings.T("globalSettings.groups.hdrSetup");
            t.Section(AppStrings.T("globalSettings.groups.setupSub"));

            t.Select("placement", AppStrings.T("globalSettings.groups.defaultPlacement"),
                UpgradeLinksViewModel.PlacementLabels(), UpgradeLinksViewModel.PlacementLabel(ul.DefaultPlacement),
                sel => { if (UpgradeLinksViewModel.TryLabelToPlacement(sel, out var p)) { ul.DefaultPlacement = p; ul.Save(); } });

            string cur = AppStrings.T("globalSettings.groups.destCurrentLocation");
            string fld = AppStrings.T("globalSettings.groups.destSelectedFolder");
            t.Select("destination", AppStrings.T("globalSettings.groups.defaultDestination"), new[] { cur, fld },
                ul.Destination == UpgradeDestination.SelectedFolder ? fld : cur,
                sel => { ul.Destination = sel == fld ? UpgradeDestination.SelectedFolder : UpgradeDestination.CurrentLocation; ul.Save(); });

            t.Toggle("audit",  AppStrings.T("globalSettings.groups.auditOnOpen"),    AppStrings.T("globalSettings.groups.auditOnOpenDesc"),    ul.AuditOnOpen,    v => { ul.AuditOnOpen = v; ul.Save(); });
            t.Toggle("reload", AppStrings.T("globalSettings.groups.reloadExisting"), AppStrings.T("globalSettings.groups.reloadExistingDesc"), ul.ReloadExisting, v => { ul.ReloadExisting = v; ul.Save(); });
        }

        // ── Ceilings (mirrors BuildCeilingsGroupContent) ───────────────────────────
        private static void BuildCeilingsTab(TabSpec t)
        {
            t.Header = AppStrings.T("globalSettings.groups.hdrCeilings");

            var hm = CeilingHeatmapSettings.Instance;
            t.Section(AppStrings.T("globalSettings.groups.hmSub"));
            t.Color("hmLow",  AppStrings.T("globalSettings.groups.lowColor"),  hm.ColorLow,  v => { hm.ColorLow  = v; hm.Save(); });
            t.Color("hmMid",  AppStrings.T("globalSettings.groups.midColor"),  hm.ColorMid,  v => { hm.ColorMid  = v; hm.Save(); });
            t.Color("hmHigh", AppStrings.T("globalSettings.groups.highColor"), hm.ColorHigh, v => { hm.ColorHigh = v; hm.Save(); });
            t.Stepper("elevTol", AppStrings.T("globalSettings.groups.elevTol"), AppStrings.T("globalSettings.groups.elevTolDesc"),
                hm.ElevTolerance * 12.0, 0, 12, 0.0625, 4, v => { hm.ElevTolerance = v / 12.0; hm.Save(); });
            t.Toggle("hmTags", AppStrings.T("globalSettings.groups.tagsLabel"), AppStrings.T("globalSettings.groups.tagsDesc"), hm.PlaceTags, v => { hm.PlaceTags = v; hm.Save(); });

            var mg = MakeCeilingGridsSettings.Instance;
            t.Section(AppStrings.T("globalSettings.groups.mgSub"));
            t.Text("mgFolder", AppStrings.T("globalSettings.groups.dwgFolder"), mg.OutputFolder, v => { mg.OutputFolder = v ?? ""; mg.Save(); });
            t.Toggle("mgSub", AppStrings.T("globalSettings.groups.subLabel"), AppStrings.T("globalSettings.groups.subDesc"), mg.UseCeilingGridsSubfolder, v => { mg.UseCeilingGridsSubfolder = v; mg.Save(); });
        }

        // ── Views / Scope Boxes (mirrors BuildViewsGroupContent) ───────────────────
        private static void BuildViewsTab(TabSpec t)
        {
            var sb = ScopeBoxSettings.Instance;
            t.Header = AppStrings.T("globalSettings.groups.hdrViews");
            t.Section(AppStrings.T("globalSettings.groups.scopeBoxSub"));
            t.Stepper("cropBuffer", AppStrings.T("globalSettings.groups.cropBuffer"), AppStrings.T("globalSettings.groups.cropBufferDesc"),
                sb.BufferXY, 0, 100, 1, 1, v => { sb.BufferXY = v; sb.Save(); });
            t.Stepper("clusterThreshold", AppStrings.T("globalSettings.groups.clusterThreshold"), AppStrings.T("globalSettings.groups.clusterThresholdDesc"),
                sb.ClusterThreshold, 0, 500, 1, 1, v => { sb.ClusterThreshold = v; sb.Save(); });
            t.Stepper("topLevelHeight", AppStrings.T("globalSettings.groups.topLevelHeight"), AppStrings.T("globalSettings.groups.topLevelHeightDesc"),
                sb.TopLevelHeight, 1, 100, 1, 1, v => { sb.TopLevelHeight = v; sb.Save(); });
            t.Note = AppStrings.T("globalSettings.groups.noteViews");
        }

        // ── Export / Bulk Export (mirrors BuildExportGroupContent) ─────────────────
        private static void BuildExportTab(TabSpec t)
        {
            var be = BulkExportSettings.Instance;
            t.Header = AppStrings.T("globalSettings.groups.hdrExport");
            t.Section(AppStrings.T("globalSettings.groups.bulkSub"));
            t.Text("outputFolder", AppStrings.T("globalSettings.groups.outputFolder"), be.OutputFolder,         v => { be.OutputFolder = v ?? ""; be.Save(); });
            t.Text("sheetPattern", AppStrings.T("globalSettings.groups.sheetPattern"), be.FilenamePattern,       v => { be.FilenamePattern = v ?? ""; be.Save(); });
            t.Text("viewPattern",  AppStrings.T("globalSettings.groups.viewPattern"),  be.ViewFilenamePattern,   v => { be.ViewFilenamePattern = v ?? ""; be.Save(); });

            t.Section(AppStrings.T("globalSettings.groups.defaultFormats"));
            // "PDF"/"DWG"/"NWC"/"IFC" are format tokens — intentionally not externalized.
            t.Toggle("pdf",     "PDF", AppStrings.T("globalSettings.groups.pdfDesc"), be.ExportPdf,     v => { be.ExportPdf = v; be.Save(); });
            t.Toggle("dwg",     "DWG", AppStrings.T("globalSettings.groups.dwgDesc"), be.ExportDwg,     v => { be.ExportDwg = v; be.Save(); });
            t.Toggle("nwc",     "NWC", AppStrings.T("globalSettings.groups.nwcDesc"), be.ExportNwc,     v => { be.ExportNwc = v; be.Save(); });
            t.Toggle("ifc",     "IFC", AppStrings.T("globalSettings.groups.ifcDesc"), be.ExportIfc,     v => { be.ExportIfc = v; be.Save(); });
            t.Toggle("combine", AppStrings.T("globalSettings.groups.combineLabel"), AppStrings.T("globalSettings.groups.combineDesc"), be.CombinePdf,    v => { be.CombinePdf = v; be.Save(); });
            t.Toggle("split",   AppStrings.T("globalSettings.groups.splitLabel"),   AppStrings.T("globalSettings.groups.splitDesc"),   be.SplitByFormat, v => { be.SplitByFormat = v; be.Save(); });
        }

        // ── Copy from Link (mirrors BuildCopyGroupContent) ─────────────────────────
        private static void BuildCopyTab(TabSpec t)
        {
            t.Header = AppStrings.T("globalSettings.groups.hdrCopy");

            var cl = CopyLinearSettings.Instance;
            t.Section(AppStrings.T("globalSettings.groups.linearSub"));
            // "Split"/"Replace" are logic tokens compared with == — intentionally not externalized.
            t.Select("mode", AppStrings.T("globalSettings.groups.defaultMode"), new[] { "Split", "Replace" },
                cl.Mode == "Replace" ? "Replace" : "Split", sel => { cl.Mode = sel == "Replace" ? "Replace" : "Split"; cl.Save(); });
            t.Stepper("segLen", AppStrings.T("globalSettings.groups.segLen"), AppStrings.T("globalSettings.groups.segLenDesc"),
                cl.SegmentLengthFeet, 0.5, 200, 0.5, 2, v => { cl.SegmentLengthFeet = v; cl.Save(); });
            t.Stepper("gap", AppStrings.T("globalSettings.groups.gap"), AppStrings.T("globalSettings.groups.gapDesc"),
                cl.GapInches, 0, 24, 0.125, 3, v => { cl.GapInches = v; cl.Save(); });
            t.Stepper("interval", AppStrings.T("globalSettings.groups.interval"), AppStrings.T("globalSettings.groups.intervalDesc"),
                cl.IntervalFeet, 0.5, 200, 0.5, 2, v => { cl.IntervalFeet = v; cl.Save(); });
            t.Toggle("remainder", AppStrings.T("globalSettings.groups.keepRemainder"), AppStrings.T("globalSettings.groups.keepRemainderDesc"),        cl.KeepRemainder,  v => { cl.KeepRemainder = v; cl.Save(); });
            t.Toggle("align",     AppStrings.T("globalSettings.groups.alignSource"),   AppStrings.T("globalSettings.groups.alignSourceDesc"),          cl.AlignToSource,  v => { cl.AlignToSource = v; cl.Save(); });
            t.Toggle("delprev",   AppStrings.T("globalSettings.groups.delPrev"),       AppStrings.T("globalSettings.groups.delPrevDesc"),              cl.DeletePrevious, v => { cl.DeletePrevious = v; cl.Save(); });
            t.Toggle("changed",   AppStrings.T("globalSettings.groups.onlyChanged"),   AppStrings.T("globalSettings.groups.onlyChangedDescLinear"),    cl.OnlyChanged,    v => { cl.OnlyChanged = v; cl.Save(); });
            t.Toggle("orphans",   AppStrings.T("globalSettings.groups.delOrphans"),    AppStrings.T("globalSettings.groups.delOrphansDesc"),           cl.DeleteOrphans,  v => { cl.DeleteOrphans = v; cl.Save(); });

            var cf = CopyFromLinkSettings.Instance;
            t.Section(AppStrings.T("globalSettings.groups.elementsSub"));
            t.Toggle("cfDelprev", AppStrings.T("globalSettings.groups.delPrev"),     AppStrings.T("globalSettings.groups.delPrevDesc"),            cf.DeletePrevious, v => { cf.DeletePrevious = v; cf.Save(); });
            t.Toggle("cfChanged", AppStrings.T("globalSettings.groups.onlyChanged"), AppStrings.T("globalSettings.groups.onlyChangedDescElements"), cf.OnlyChanged,    v => { cf.OnlyChanged = v; cf.Save(); });
            t.Toggle("cfOrphans", AppStrings.T("globalSettings.groups.delOrphans"),  AppStrings.T("globalSettings.groups.delOrphansDesc"),          cf.DeleteOrphans,  v => { cf.DeleteOrphans = v; cf.Save(); });
        }

        // ── Spec-tab builder: rows for the page + a per-field applier map ──────────
        private sealed class TabSpec
        {
            public string  Header = "";
            public string? Note;
            public readonly List<Dictionary<string, object?>> Rows = new List<Dictionary<string, object?>>();
            public readonly Dictionary<string, Action<object?>> Appliers =
                new Dictionary<string, Action<object?>>(StringComparer.Ordinal);

            public void Section(string text) => Rows.Add(new Dictionary<string, object?> { ["kind"] = "settingsSection", ["text"] = text });
            public void Note_(string text)   => Rows.Add(new Dictionary<string, object?> { ["kind"] = "hint", ["text"] = text });
            public void Sep()                => Rows.Add(new Dictionary<string, object?> { ["kind"] = "settingsSep" });

            public void Toggle(string id, string label, string? desc, bool value, Action<bool> apply)
            {
                Rows.Add(WebInput.Toggle(id, label, value).ToPayload());
                if (!string.IsNullOrEmpty(desc)) Note_(desc!);
                Appliers[id] = v => apply(ToBool(v));
            }
            public void Stepper(string id, string label, string? desc, double value,
                double min, double max, double step, int decimals, Action<double> apply)
            {
                Rows.Add(WebInput.Stepper(id, label, value, min, max, step, decimals).ToPayload());
                if (!string.IsNullOrEmpty(desc)) Note_(desc!);
                Appliers[id] = v => apply(ToDouble(v));
            }
            public void Select(string id, string label, IEnumerable<string> options, string value, Action<string> apply)
            {
                Rows.Add(WebInput.SingleSelect(id, label, value, options.Select(o => new WebOption(o, o))).ToPayload());
                Appliers[id] = v => apply(ToStr(v));
            }
            public void Text(string id, string label, string value, Action<string> apply)
            {
                Rows.Add(WebInput.TextField(id, label, value).ToPayload());
                Appliers[id] = v => apply(ToStr(v));
            }
            public void Color(string id, string label, string value, Action<string> apply)
            {
                Rows.Add(WebInput.Color(id, label, value).ToPayload());
                Appliers[id] = v => apply(ToStr(v));
            }
        }

        // Bridge values arrive as MiniJson primitives (double / bool / string / long).
        private static bool ToBool(object? v) =>
            v is bool b ? b : (v is string s && bool.TryParse(s, out var r) && r);

        private static double ToDouble(object? v) =>
            v is double d ? d :
            v is long l   ? l :
            v is int i    ? i :
            v is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : 0.0;

        private static string ToStr(object? v) => v as string ?? v?.ToString() ?? "";
    }
}
