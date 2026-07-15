using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using LemoineTools.Framework.Controls;
using LemoineTools.Tools.Ceilings;
using LemoineTools.Tools.BulkExport;
using LemoineTools.Tools.CopyFromLink;
using LemoineTools.Tools.Setup;

namespace LemoineTools.Framework
{
    /// <summary>
    /// Per-ribbon-group settings tabs. Each ribbon panel gets one tab; inside it,
    /// a section per tool that has persisted default settings (read/written through the
    /// tool's <c>*Settings.Instance</c> singleton, auto-saved on change — matching the
    /// Dimensions tab). Tools with no defaults are omitted; groups with no defaults show
    /// a short note.
    /// </summary>
    public partial class GlobalSettingsWindow
    {
        // ── Scaffold + small section helpers ─────────────────────────────────
        private ScrollViewer NewGroupPanel(string header, out StackPanel panel)
        {
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            panel.Children.Add(ContentHeader(header));
            scroll.Content = panel;
            return scroll;
        }

        private static void AddGroupNote(StackPanel panel, string text)
        {
            var tb = new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 0, 0, 12),
            };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            panel.Children.Add(tb);
        }

        private void AddTextRow(StackPanel panel, string label, string text, Action<string> set, bool mono = false)
        {
            panel.Children.Add(DimRowLabel(label));
            var tb = new TextBox
            {
                Text = text ?? "", Width = 320, HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 10),
            };
            tb.SetResourceReference(FrameworkElement.HeightProperty,                 "LemoineH_Input");
            tb.SetResourceReference(System.Windows.Controls.Control.PaddingProperty, "LemoineTh_InputPad");
            tb.SetResourceReference(TextBox.ForegroundProperty,  "LemoineText");
            tb.SetResourceReference(TextBox.BackgroundProperty,  "LemoineSelectBg");
            tb.SetResourceReference(TextBox.FontSizeProperty,    "LemoineFS_SM");
            tb.SetResourceReference(TextBox.FontFamilyProperty,  mono ? "LemoineMonoFont" : "LemoineUiFont");
            tb.SetResourceReference(TextBox.BorderBrushProperty, "LemoineBorder");
            tb.TextChanged += (s, e) => set(tb.Text ?? "");
            panel.Children.Add(tb);
        }

        private void AddColorRow(StackPanel panel, string label, Func<string> get, Action<string> set)
        {
            panel.Children.Add(DimRowLabel(label));
            string cur = get();
            var swatch = ColorPickerWindow.BuildColorPickerSwatch(
                getHex: () => cur,
                setHex: h => { cur = h; set(h); });
            swatch.HorizontalAlignment = HorizontalAlignment.Left;
            swatch.Margin = new Thickness(0, 0, 0, 10);
            panel.Children.Add(swatch);
        }

        // ═════════════════════════════════════════════════════════════════════
        // Setup — Upgrade & Link Models
        // ═════════════════════════════════════════════════════════════════════
        private UIElement BuildSetupGroupContent()
        {
            var scroll = NewGroupPanel(AppStrings.T("globalSettings.groups.hdrSetup"), out var panel);
            var ul = UpgradeLinksSettings.Instance;

            panel.Children.Add(ToolSection.Build(AppStrings.T("globalSettings.groups.setupSub"), body =>
            {
                var placementPicker = new SingleSelect
                {
                    Label        = AppStrings.T("globalSettings.groups.defaultPlacement"),
                    Items        = UpgradeLinksViewModel.PlacementLabels(),
                    SelectedItem = UpgradeLinksViewModel.PlacementLabel(ul.DefaultPlacement),
                };
                placementPicker.SelectionChanged += sel =>
                {
                    if (UpgradeLinksViewModel.TryLabelToPlacement(sel, out var p))
                    {
                        ul.DefaultPlacement = p;
                        ul.Save();
                    }
                };
                body.Children.Add(placementPicker);
                body.Children.Add(new FrameworkElement { Height = 8 });

                // Cloud is only offered when the host itself is a cloud model, so it's omitted
                // here — Current Location vs Selected Folder are the choices meaningful with no
                // document open. No folder-path field: the last folder used is remembered
                // per-run only, not set as a default (see CLAUDE.md WS-10 decision 4).
                var destLabels = new List<string>
                {
                    AppStrings.T("globalSettings.groups.destCurrentLocation"),
                    AppStrings.T("globalSettings.groups.destSelectedFolder"),
                };
                var destPicker = new SingleSelect
                {
                    Label        = AppStrings.T("globalSettings.groups.defaultDestination"),
                    Items        = destLabels,
                    SelectedItem = ul.Destination == UpgradeDestination.SelectedFolder ? destLabels[1] : destLabels[0],
                };
                destPicker.SelectionChanged += sel =>
                {
                    ul.Destination = sel == destLabels[1] ? UpgradeDestination.SelectedFolder : UpgradeDestination.CurrentLocation;
                    ul.Save();
                };
                body.Children.Add(destPicker);
                body.Children.Add(new FrameworkElement { Height = 8 });

                var ulToggles = new ToggleSwitches();
                ulToggles.SetItems(new List<ToggleItem>
                {
                    new ToggleItem { Id = "audit", Label = AppStrings.T("globalSettings.groups.auditOnOpen"),
                                     Desc = AppStrings.T("globalSettings.groups.auditOnOpenDesc"), DefaultOn = ul.AuditOnOpen },
                    new ToggleItem { Id = "reload", Label = AppStrings.T("globalSettings.groups.reloadExisting"),
                                     Desc = AppStrings.T("globalSettings.groups.reloadExistingDesc"), DefaultOn = ul.ReloadExisting },
                });
                ulToggles.StateChanged += state =>
                {
                    if (state.TryGetValue("audit",  out var a)) ul.AuditOnOpen    = a;
                    if (state.TryGetValue("reload", out var r)) ul.ReloadExisting = r;
                    ul.Save();
                };
                body.Children.Add(ulToggles);
            }));

            return scroll;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Filters & Legends — both tools open their own windows
        // ═════════════════════════════════════════════════════════════════════
        private UIElement BuildFiltersGroupContent()
        {
            var scroll = NewGroupPanel(AppStrings.T("globalSettings.groups.hdrFilters"), out var panel);
            AddGroupNote(panel, AppStrings.T("globalSettings.groups.noteFilters"));
            return scroll;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Ceilings — Ceiling Heatmap + Make Ceiling Grids
        // ═════════════════════════════════════════════════════════════════════
        private UIElement BuildCeilingsGroupContent()
        {
            var scroll = NewGroupPanel(AppStrings.T("globalSettings.groups.hdrCeilings"), out var panel);

            // ── Ceiling Heatmap ──────────────────────────────────────────────
            var hm = CeilingHeatmapSettings.Instance;
            panel.Children.Add(ToolSection.Build(AppStrings.T("globalSettings.groups.hmSub"), body =>
            {
                AddColorRow(body, AppStrings.T("globalSettings.groups.lowColor"),    () => hm.ColorLow,  v => { hm.ColorLow  = v; hm.Save(); });
                AddColorRow(body, AppStrings.T("globalSettings.groups.midColor"),    () => hm.ColorMid,  v => { hm.ColorMid  = v; hm.Save(); });
                AddColorRow(body, AppStrings.T("globalSettings.groups.highColor"),   () => hm.ColorHigh, v => { hm.ColorHigh = v; hm.Save(); });
                AddCfgStepper(body, AppStrings.T("globalSettings.groups.elevTol"),
                    AppStrings.T("globalSettings.groups.elevTolDesc"),
                    hm.ElevTolerance * 12.0, 0, 12, 0.0625, 4,
                    v => { hm.ElevTolerance = v / 12.0; hm.Save(); });

                var hmToggles = new ToggleSwitches();
                hmToggles.SetItems(new List<ToggleItem>
                {
                    new ToggleItem { Id = "tags", Label = AppStrings.T("globalSettings.groups.tagsLabel"),
                                     Desc = AppStrings.T("globalSettings.groups.tagsDesc"),
                                     DefaultOn = hm.PlaceTags },
                });
                hmToggles.StateChanged += state =>
                {
                    if (state.TryGetValue("tags", out var t)) { hm.PlaceTags = t; hm.Save(); }
                };
                body.Children.Add(hmToggles);
            }));

            // ── Make Ceiling Grids ───────────────────────────────────────────
            var mg = MakeCeilingGridsSettings.Instance;
            panel.Children.Add(ToolSection.Build(AppStrings.T("globalSettings.groups.mgSub"), body =>
            {
                AddTextRow(body, AppStrings.T("globalSettings.groups.dwgFolder"), mg.OutputFolder,
                    v => { mg.OutputFolder = v; mg.Save(); }, mono: true);

                var mgToggles = new ToggleSwitches();
                mgToggles.SetItems(new List<ToggleItem>
                {
                    new ToggleItem { Id = "sub", Label = AppStrings.T("globalSettings.groups.subLabel"),
                                     Desc = AppStrings.T("globalSettings.groups.subDesc"),
                                     DefaultOn = mg.UseCeilingGridsSubfolder },
                });
                mgToggles.StateChanged += state =>
                {
                    if (state.TryGetValue("sub", out var s)) { mg.UseCeilingGridsSubfolder = s; mg.Save(); }
                };
                body.Children.Add(mgToggles);
            }));

            return scroll;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Views — Scope Box Creator geometry (Bulk Views by Level no longer runs a
        // room search; the crop-buffer / cluster-threshold settings moved with the
        // search into the Scope Box Creator)
        // ═════════════════════════════════════════════════════════════════════
        private UIElement BuildViewsGroupContent()
        {
            var scroll = NewGroupPanel(AppStrings.T("globalSettings.groups.hdrViews"), out var panel);

            var sb = LemoineTools.Tools.ScopeBoxes.ScopeBoxSettings.Instance;
            panel.Children.Add(ToolSection.Build(AppStrings.T("globalSettings.groups.scopeBoxSub"), body =>
            {
                AddCfgStepper(body, AppStrings.T("globalSettings.groups.cropBuffer"),
                    AppStrings.T("globalSettings.groups.cropBufferDesc"),
                    sb.BufferXY, 0, 100, 1, 1, v => { sb.BufferXY = v; sb.Save(); });
                AddCfgStepper(body, AppStrings.T("globalSettings.groups.clusterThreshold"),
                    AppStrings.T("globalSettings.groups.clusterThresholdDesc"),
                    sb.ClusterThreshold, 0, 500, 1, 1, v => { sb.ClusterThreshold = v; sb.Save(); });
                AddCfgStepper(body, AppStrings.T("globalSettings.groups.topLevelHeight"),
                    AppStrings.T("globalSettings.groups.topLevelHeightDesc"),
                    sb.TopLevelHeight, 1, 100, 1, 1, v => { sb.TopLevelHeight = v; sb.Save(); });
            }));

            AddGroupNote(panel, AppStrings.T("globalSettings.groups.noteViews"));

            return scroll;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Export — Bulk Export
        // ═════════════════════════════════════════════════════════════════════
        private UIElement BuildExportGroupContent()
        {
            var scroll = NewGroupPanel(AppStrings.T("globalSettings.groups.hdrExport"), out var panel);

            var be = BulkExportSettings.Instance;
            panel.Children.Add(ToolSection.Build(AppStrings.T("globalSettings.groups.bulkSub"), body =>
            {
                AddTextRow(body, AppStrings.T("globalSettings.groups.outputFolder"), be.OutputFolder,
                    v => { be.OutputFolder = v; be.Save(); }, mono: true);
                AddTextRow(body, AppStrings.T("globalSettings.groups.sheetPattern"), be.FilenamePattern,
                    v => { be.FilenamePattern = v; be.Save(); }, mono: true);
                AddTextRow(body, AppStrings.T("globalSettings.groups.viewPattern"), be.ViewFilenamePattern,
                    v => { be.ViewFilenamePattern = v; be.Save(); }, mono: true);

                body.Children.Add(SubLabel(AppStrings.T("globalSettings.groups.defaultFormats")));
                var beToggles = new ToggleSwitches();
                beToggles.SetItems(new List<ToggleItem>
                {
                    new ToggleItem { Id = "pdf",   Label = "PDF",  Desc = AppStrings.T("globalSettings.groups.pdfDesc"),  DefaultOn = be.ExportPdf },
                    new ToggleItem { Id = "dwg",   Label = "DWG",  Desc = AppStrings.T("globalSettings.groups.dwgDesc"),  DefaultOn = be.ExportDwg },
                    new ToggleItem { Id = "nwc",   Label = "NWC",  Desc = AppStrings.T("globalSettings.groups.nwcDesc"), DefaultOn = be.ExportNwc },
                    new ToggleItem { Id = "ifc",   Label = "IFC",  Desc = AppStrings.T("globalSettings.groups.ifcDesc"), DefaultOn = be.ExportIfc },
                    new ToggleItem { Id = "combine", Label = AppStrings.T("globalSettings.groups.combineLabel"),
                                     Desc = AppStrings.T("globalSettings.groups.combineDesc"), DefaultOn = be.CombinePdf },
                    new ToggleItem { Id = "split",   Label = AppStrings.T("globalSettings.groups.splitLabel"),
                                     Desc = AppStrings.T("globalSettings.groups.splitDesc"), DefaultOn = be.SplitByFormat },
                });
                beToggles.StateChanged += state =>
                {
                    if (state.TryGetValue("pdf",     out var p)) be.ExportPdf     = p;
                    if (state.TryGetValue("dwg",     out var d)) be.ExportDwg     = d;
                    if (state.TryGetValue("nwc",     out var n)) be.ExportNwc     = n;
                    if (state.TryGetValue("ifc",     out var i)) be.ExportIfc     = i;
                    if (state.TryGetValue("combine", out var c)) be.CombinePdf    = c;
                    if (state.TryGetValue("split",   out var s)) be.SplitByFormat = s;
                    be.Save();
                };
                body.Children.Add(beToggles);
            }));

            return scroll;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Copy from Link — Copy Linear + Copy Elements
        // ═════════════════════════════════════════════════════════════════════
        private UIElement BuildCopyGroupContent()
        {
            var scroll = NewGroupPanel(AppStrings.T("globalSettings.groups.hdrCopy"), out var panel);

            // ── Copy Linear ──────────────────────────────────────────────────
            var cl = CopyLinearSettings.Instance;
            panel.Children.Add(ToolSection.Build(AppStrings.T("globalSettings.groups.linearSub"), body =>
            {
                var modePicker = new SingleSelect
                {
                    Label        = AppStrings.T("globalSettings.groups.defaultMode"),
                    Items        = new List<string> { "Split", "Replace" },
                    SelectedItem = cl.Mode == "Replace" ? "Replace" : "Split",
                };
                modePicker.SelectionChanged += sel =>
                {
                    cl.Mode = sel == "Replace" ? "Replace" : "Split";
                    cl.Save();
                };
                body.Children.Add(modePicker);
                body.Children.Add(new FrameworkElement { Height = 8 });

                AddCfgStepper(body, AppStrings.T("globalSettings.groups.segLen"),
                    AppStrings.T("globalSettings.groups.segLenDesc"),
                    cl.SegmentLengthFeet, 0.5, 200, 0.5, 2, v => { cl.SegmentLengthFeet = v; cl.Save(); });
                AddCfgStepper(body, AppStrings.T("globalSettings.groups.gap"),
                    AppStrings.T("globalSettings.groups.gapDesc"),
                    cl.GapInches, 0, 24, 0.125, 3, v => { cl.GapInches = v; cl.Save(); });
                AddCfgStepper(body, AppStrings.T("globalSettings.groups.interval"),
                    AppStrings.T("globalSettings.groups.intervalDesc"),
                    cl.IntervalFeet, 0.5, 200, 0.5, 2, v => { cl.IntervalFeet = v; cl.Save(); });

                var clToggles = new ToggleSwitches();
                clToggles.SetItems(new List<ToggleItem>
                {
                    new ToggleItem { Id = "remainder", Label = AppStrings.T("globalSettings.groups.keepRemainder"),
                                     Desc = AppStrings.T("globalSettings.groups.keepRemainderDesc"), DefaultOn = cl.KeepRemainder },
                    new ToggleItem { Id = "align", Label = AppStrings.T("globalSettings.groups.alignSource"),
                                     Desc = AppStrings.T("globalSettings.groups.alignSourceDesc"), DefaultOn = cl.AlignToSource },
                    new ToggleItem { Id = "delprev", Label = AppStrings.T("globalSettings.groups.delPrev"),
                                     Desc = AppStrings.T("globalSettings.groups.delPrevDesc"), DefaultOn = cl.DeletePrevious },
                    new ToggleItem { Id = "changed", Label = AppStrings.T("globalSettings.groups.onlyChanged"),
                                     Desc = AppStrings.T("globalSettings.groups.onlyChangedDescLinear"), DefaultOn = cl.OnlyChanged },
                    new ToggleItem { Id = "orphans", Label = AppStrings.T("globalSettings.groups.delOrphans"),
                                     Desc = AppStrings.T("globalSettings.groups.delOrphansDesc"), DefaultOn = cl.DeleteOrphans },
                });
                clToggles.StateChanged += state =>
                {
                    if (state.TryGetValue("remainder", out var r)) cl.KeepRemainder  = r;
                    if (state.TryGetValue("align",     out var a)) cl.AlignToSource  = a;
                    if (state.TryGetValue("delprev",   out var d)) cl.DeletePrevious = d;
                    if (state.TryGetValue("changed",   out var c)) cl.OnlyChanged    = c;
                    if (state.TryGetValue("orphans",   out var o)) cl.DeleteOrphans  = o;
                    cl.Save();
                };
                body.Children.Add(clToggles);
            }));

            // ── Copy Elements ────────────────────────────────────────────────
            var cf = CopyFromLinkSettings.Instance;
            panel.Children.Add(ToolSection.Build(AppStrings.T("globalSettings.groups.elementsSub"), body =>
            {
                var cfToggles = new ToggleSwitches();
                cfToggles.SetItems(new List<ToggleItem>
                {
                    new ToggleItem { Id = "delprev", Label = AppStrings.T("globalSettings.groups.delPrev"),
                                     Desc = AppStrings.T("globalSettings.groups.delPrevDesc"), DefaultOn = cf.DeletePrevious },
                    new ToggleItem { Id = "changed", Label = AppStrings.T("globalSettings.groups.onlyChanged"),
                                     Desc = AppStrings.T("globalSettings.groups.onlyChangedDescElements"), DefaultOn = cf.OnlyChanged },
                    new ToggleItem { Id = "orphans", Label = AppStrings.T("globalSettings.groups.delOrphans"),
                                     Desc = AppStrings.T("globalSettings.groups.delOrphansDesc"), DefaultOn = cf.DeleteOrphans },
                });
                cfToggles.StateChanged += state =>
                {
                    if (state.TryGetValue("delprev", out var d)) cf.DeletePrevious = d;
                    if (state.TryGetValue("changed", out var c)) cf.OnlyChanged    = c;
                    if (state.TryGetValue("orphans", out var o)) cf.DeleteOrphans  = o;
                    cf.Save();
                };
                body.Children.Add(cfToggles);
            }));

            return scroll;
        }
    }
}
