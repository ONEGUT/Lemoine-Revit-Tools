using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Tools.Ceilings;
using LemoineTools.Tools.LinkViews;
using LemoineTools.Tools.BulkExport;
using LemoineTools.Tools.CopyLinear;
using LemoineTools.Tools.CopyFromLink;

namespace LemoineTools.Lemoine
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
            var swatch = LemoineColorPickerWindow.BuildColorPickerSwatch(
                getHex: () => cur,
                setHex: h => { cur = h; set(h); });
            swatch.HorizontalAlignment = HorizontalAlignment.Left;
            swatch.Margin = new Thickness(0, 0, 0, 10);
            panel.Children.Add(swatch);
        }

        // ═════════════════════════════════════════════════════════════════════
        // Filters & Legends — both tools open their own windows
        // ═════════════════════════════════════════════════════════════════════
        private UIElement BuildFiltersGroupContent()
        {
            var scroll = NewGroupPanel(LemoineStrings.T("globalSettings.groups.hdrFilters"), out var panel);
            AddGroupNote(panel, LemoineStrings.T("globalSettings.groups.noteFilters"));
            return scroll;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Ceilings — Ceiling Heatmap + Make Ceiling Grids
        // ═════════════════════════════════════════════════════════════════════
        private UIElement BuildCeilingsGroupContent()
        {
            var scroll = NewGroupPanel(LemoineStrings.T("globalSettings.groups.hdrCeilings"), out var panel);

            // ── Ceiling Heatmap ──────────────────────────────────────────────
            var hm = CeilingHeatmapSettings.Instance;
            panel.Children.Add(SubLabel(LemoineStrings.T("globalSettings.groups.hmSub")));
            AddColorRow(panel, LemoineStrings.T("globalSettings.groups.lowColor"),    () => hm.ColorLow,  v => { hm.ColorLow  = v; hm.Save(); });
            AddColorRow(panel, LemoineStrings.T("globalSettings.groups.midColor"),    () => hm.ColorMid,  v => { hm.ColorMid  = v; hm.Save(); });
            AddColorRow(panel, LemoineStrings.T("globalSettings.groups.highColor"),   () => hm.ColorHigh, v => { hm.ColorHigh = v; hm.Save(); });
            AddCfgStepper(panel, LemoineStrings.T("globalSettings.groups.elevTol"),
                LemoineStrings.T("globalSettings.groups.elevTolDesc"),
                hm.ElevTolerance * 12.0, 0, 12, 0.0625, 4,
                v => { hm.ElevTolerance = v / 12.0; hm.Save(); });

            var hmToggles = new LemoineToggleSwitches();
            hmToggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "tags", Label = LemoineStrings.T("globalSettings.groups.tagsLabel"),
                                 Desc = LemoineStrings.T("globalSettings.groups.tagsDesc"),
                                 DefaultOn = hm.PlaceTags },
            });
            hmToggles.StateChanged += state =>
            {
                if (state.TryGetValue("tags", out var t)) { hm.PlaceTags = t; hm.Save(); }
            };
            panel.Children.Add(hmToggles);

            panel.Children.Add(HSep(12));

            // ── Make Ceiling Grids ───────────────────────────────────────────
            var mg = MakeCeilingGridsSettings.Instance;
            panel.Children.Add(SubLabel(LemoineStrings.T("globalSettings.groups.mgSub")));
            AddTextRow(panel, LemoineStrings.T("globalSettings.groups.dwgFolder"), mg.OutputFolder,
                v => { mg.OutputFolder = v; mg.Save(); }, mono: true);

            var mgToggles = new LemoineToggleSwitches();
            mgToggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "sub", Label = LemoineStrings.T("globalSettings.groups.subLabel"),
                                 Desc = LemoineStrings.T("globalSettings.groups.subDesc"),
                                 DefaultOn = mg.UseCeilingGridsSubfolder },
            });
            mgToggles.StateChanged += state =>
            {
                if (state.TryGetValue("sub", out var s)) { mg.UseCeilingGridsSubfolder = s; mg.Save(); }
            };
            panel.Children.Add(mgToggles);

            return scroll;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Views — Bulk Views by Level
        // ═════════════════════════════════════════════════════════════════════
        private UIElement BuildViewsGroupContent()
        {
            var scroll = NewGroupPanel(LemoineStrings.T("globalSettings.groups.hdrViews"), out var panel);

            var lv = LinkViewsLevelSettings.Instance;
            panel.Children.Add(SubLabel(LemoineStrings.T("globalSettings.groups.byLevelSub")));
            AddCfgStepper(panel, LemoineStrings.T("globalSettings.groups.cropBuffer"),
                LemoineStrings.T("globalSettings.groups.cropBufferDesc"),
                lv.BufferXY, 0, 100, 1, 1, v => { lv.BufferXY = v; lv.Save(); });
            AddCfgStepper(panel, LemoineStrings.T("globalSettings.groups.clusterThreshold"),
                LemoineStrings.T("globalSettings.groups.clusterThresholdDesc"),
                lv.ClusterThreshold, 0, 500, 1, 1, v => { lv.ClusterThreshold = v; lv.Save(); });
            AddCfgStepper(panel, LemoineStrings.T("globalSettings.groups.cutPlane"),
                LemoineStrings.T("globalSettings.groups.cutPlaneDesc"),
                lv.CutOffset, 0, 50, 0.5, 1, v => { lv.CutOffset = v; lv.Save(); });

            return scroll;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Sheets — no saved defaults yet
        // ═════════════════════════════════════════════════════════════════════
        private UIElement BuildSheetsGroupContent()
        {
            var scroll = NewGroupPanel(LemoineStrings.T("globalSettings.groups.hdrSheets"), out var panel);
            AddGroupNote(panel, LemoineStrings.T("globalSettings.groups.noteSheets"));
            return scroll;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Export — Bulk Export
        // ═════════════════════════════════════════════════════════════════════
        private UIElement BuildExportGroupContent()
        {
            var scroll = NewGroupPanel(LemoineStrings.T("globalSettings.groups.hdrExport"), out var panel);

            var be = BulkExportSettings.Instance;
            panel.Children.Add(SubLabel(LemoineStrings.T("globalSettings.groups.bulkSub")));
            AddTextRow(panel, LemoineStrings.T("globalSettings.groups.outputFolder"), be.OutputFolder,
                v => { be.OutputFolder = v; be.Save(); }, mono: true);
            AddTextRow(panel, LemoineStrings.T("globalSettings.groups.sheetPattern"), be.FilenamePattern,
                v => { be.FilenamePattern = v; be.Save(); }, mono: true);
            AddTextRow(panel, LemoineStrings.T("globalSettings.groups.viewPattern"), be.ViewFilenamePattern,
                v => { be.ViewFilenamePattern = v; be.Save(); }, mono: true);

            panel.Children.Add(SubLabel(LemoineStrings.T("globalSettings.groups.defaultFormats")));
            var beToggles = new LemoineToggleSwitches();
            beToggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "pdf",   Label = "PDF",  Desc = LemoineStrings.T("globalSettings.groups.pdfDesc"),  DefaultOn = be.ExportPdf },
                new ToggleItem { Id = "dwg",   Label = "DWG",  Desc = LemoineStrings.T("globalSettings.groups.dwgDesc"),  DefaultOn = be.ExportDwg },
                new ToggleItem { Id = "nwc",   Label = "NWC",  Desc = LemoineStrings.T("globalSettings.groups.nwcDesc"), DefaultOn = be.ExportNwc },
                new ToggleItem { Id = "ifc",   Label = "IFC",  Desc = LemoineStrings.T("globalSettings.groups.ifcDesc"), DefaultOn = be.ExportIfc },
                new ToggleItem { Id = "combine", Label = LemoineStrings.T("globalSettings.groups.combineLabel"),
                                 Desc = LemoineStrings.T("globalSettings.groups.combineDesc"), DefaultOn = be.CombinePdf },
                new ToggleItem { Id = "split",   Label = LemoineStrings.T("globalSettings.groups.splitLabel"),
                                 Desc = LemoineStrings.T("globalSettings.groups.splitDesc"), DefaultOn = be.SplitByFormat },
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
            panel.Children.Add(beToggles);

            return scroll;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Modify — no saved defaults yet
        // ═════════════════════════════════════════════════════════════════════
        private UIElement BuildModifyGroupContent()
        {
            var scroll = NewGroupPanel(LemoineStrings.T("globalSettings.groups.hdrModify"), out var panel);
            AddGroupNote(panel, LemoineStrings.T("globalSettings.groups.noteModify"));
            return scroll;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Copy from Link — Copy Linear + Copy Elements
        // ═════════════════════════════════════════════════════════════════════
        private UIElement BuildCopyGroupContent()
        {
            var scroll = NewGroupPanel(LemoineStrings.T("globalSettings.groups.hdrCopy"), out var panel);

            // ── Copy Linear ──────────────────────────────────────────────────
            var cl = CopyLinearSettings.Instance;
            panel.Children.Add(SubLabel(LemoineStrings.T("globalSettings.groups.linearSub")));

            var modePicker = new LemoineSingleSelect
            {
                Label        = LemoineStrings.T("globalSettings.groups.defaultMode"),
                Items        = new List<string> { "Split", "Replace" },
                SelectedItem = cl.Mode == "Replace" ? "Replace" : "Split",
            };
            modePicker.SelectionChanged += sel =>
            {
                cl.Mode = sel == "Replace" ? "Replace" : "Split";
                cl.Save();
            };
            panel.Children.Add(modePicker);
            panel.Children.Add(new FrameworkElement { Height = 8 });

            AddCfgStepper(panel, LemoineStrings.T("globalSettings.groups.segLen"),
                LemoineStrings.T("globalSettings.groups.segLenDesc"),
                cl.SegmentLengthFeet, 0.5, 200, 0.5, 2, v => { cl.SegmentLengthFeet = v; cl.Save(); });
            AddCfgStepper(panel, LemoineStrings.T("globalSettings.groups.gap"),
                LemoineStrings.T("globalSettings.groups.gapDesc"),
                cl.GapInches, 0, 24, 0.125, 3, v => { cl.GapInches = v; cl.Save(); });
            AddCfgStepper(panel, LemoineStrings.T("globalSettings.groups.interval"),
                LemoineStrings.T("globalSettings.groups.intervalDesc"),
                cl.IntervalFeet, 0.5, 200, 0.5, 2, v => { cl.IntervalFeet = v; cl.Save(); });

            var clToggles = new LemoineToggleSwitches();
            clToggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "remainder", Label = LemoineStrings.T("globalSettings.groups.keepRemainder"),
                                 Desc = LemoineStrings.T("globalSettings.groups.keepRemainderDesc"), DefaultOn = cl.KeepRemainder },
                new ToggleItem { Id = "align", Label = LemoineStrings.T("globalSettings.groups.alignSource"),
                                 Desc = LemoineStrings.T("globalSettings.groups.alignSourceDesc"), DefaultOn = cl.AlignToSource },
                new ToggleItem { Id = "delprev", Label = LemoineStrings.T("globalSettings.groups.delPrev"),
                                 Desc = LemoineStrings.T("globalSettings.groups.delPrevDesc"), DefaultOn = cl.DeletePrevious },
                new ToggleItem { Id = "changed", Label = LemoineStrings.T("globalSettings.groups.onlyChanged"),
                                 Desc = LemoineStrings.T("globalSettings.groups.onlyChangedDescLinear"), DefaultOn = cl.OnlyChanged },
                new ToggleItem { Id = "orphans", Label = LemoineStrings.T("globalSettings.groups.delOrphans"),
                                 Desc = LemoineStrings.T("globalSettings.groups.delOrphansDesc"), DefaultOn = cl.DeleteOrphans },
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
            panel.Children.Add(clToggles);

            panel.Children.Add(HSep(12));

            // ── Copy Elements ────────────────────────────────────────────────
            var cf = CopyFromLinkSettings.Instance;
            panel.Children.Add(SubLabel(LemoineStrings.T("globalSettings.groups.elementsSub")));
            var cfToggles = new LemoineToggleSwitches();
            cfToggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "delprev", Label = LemoineStrings.T("globalSettings.groups.delPrev"),
                                 Desc = LemoineStrings.T("globalSettings.groups.delPrevDesc"), DefaultOn = cf.DeletePrevious },
                new ToggleItem { Id = "changed", Label = LemoineStrings.T("globalSettings.groups.onlyChanged"),
                                 Desc = LemoineStrings.T("globalSettings.groups.onlyChangedDescElements"), DefaultOn = cf.OnlyChanged },
                new ToggleItem { Id = "orphans", Label = LemoineStrings.T("globalSettings.groups.delOrphans"),
                                 Desc = LemoineStrings.T("globalSettings.groups.delOrphansDesc"), DefaultOn = cf.DeleteOrphans },
            });
            cfToggles.StateChanged += state =>
            {
                if (state.TryGetValue("delprev", out var d)) cf.DeletePrevious = d;
                if (state.TryGetValue("changed", out var c)) cf.OnlyChanged    = c;
                if (state.TryGetValue("orphans", out var o)) cf.DeleteOrphans  = o;
                cf.Save();
            };
            panel.Children.Add(cfToggles);

            return scroll;
        }
    }
}
