using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Tools.Clash;
using LemoineTools.Tools.Clash.AutoDimension;

namespace LemoineTools.Lemoine
{
    /// <summary>
    /// "Dimensions" settings tab — edits the persisted auto-dimension defaults
    /// (<see cref="AutoDimensionConfig"/>, stored in <c>%AppData%\LemoineTools\AutoDimension.xml</c>).
    /// These are the run defaults the Clash Finder &amp; Dimension wizard seeds from; the wizard never
    /// writes back, so this tab is the single source of the persisted defaults. Every control auto-saves
    /// on change (no Apply button), matching the rest of the settings window.
    /// </summary>
    public partial class GlobalSettingsWindow
    {
        // Destination display ↔ stored TargetType, shared with the wizard's mapping.
        private static readonly string DimGridDisplay   = LemoineStrings.T("globalSettings.dim.destGrid");
        private static readonly string DimSlabDisplay   = LemoineStrings.T("globalSettings.dim.destSlab");
        private static readonly string DimManualDisplay = LemoineStrings.T("globalSettings.dim.destManual");

        private UIElement BuildDimensionsContent()
        {
            var cfg = AutoDimensionConfig.Instance;

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };

            panel.Children.Add(ContentHeader(LemoineStrings.T("globalSettings.dim.header")));
            panel.Children.Add(SubLabel(LemoineStrings.T("globalSettings.dim.finderSub")));
            panel.Children.Add(DimIntro(LemoineStrings.T("globalSettings.dim.intro")));

            // ── Run defaults ─────────────────────────────────────────────────
            panel.Children.Add(SubLabel(LemoineStrings.T("globalSettings.dim.runDefaults")));

            var destPicker = new LemoineSingleSelect
            {
                Label        = LemoineStrings.T("globalSettings.dim.defaultDest"),
                Items        = new List<string> { DimGridDisplay, DimSlabDisplay, DimManualDisplay },
                SelectedItem = cfg.TargetType == "SlabEdge" ? DimSlabDisplay
                             : cfg.TargetType == "ManualDatum" ? DimManualDisplay : DimGridDisplay,
            };
            destPicker.SelectionChanged += sel =>
            {
                cfg.TargetType = sel == DimSlabDisplay ? "SlabEdge"
                               : sel == DimManualDisplay ? "ManualDatum" : "Grid";
                cfg.Save();
            };
            panel.Children.Add(destPicker);
            panel.Children.Add(new FrameworkElement { Height = 8 });

            var runToggles = new LemoineToggleSwitches();
            runToggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "chain", Label = LemoineStrings.T("globalSettings.dim.chainLabel"),
                                 Desc = LemoineStrings.T("globalSettings.dim.chainDesc"),
                                 DefaultOn = cfg.ChainAligned },
                new ToggleItem { Id = "density", Label = LemoineStrings.T("globalSettings.dim.densityLabel"),
                                 Desc = LemoineStrings.T("globalSettings.dim.densityDesc"),
                                 DefaultOn = cfg.DensityChaining },
                new ToggleItem { Id = "callouts", Label = LemoineStrings.T("globalSettings.dim.calloutsLabel"),
                                 Desc = LemoineStrings.T("globalSettings.dim.calloutsDesc"),
                                 DefaultOn = cfg.DenseCalloutsEnabled },
                new ToggleItem { Id = "bothAxes", Label = LemoineStrings.T("globalSettings.dim.bothAxesLabel"),
                                 Desc = LemoineStrings.T("globalSettings.dim.bothAxesDesc"),
                                 DefaultOn = cfg.DimensionBothAxes },
                new ToggleItem { Id = "links", Label = LemoineStrings.T("globalSettings.dim.linksLabel"),
                                 Desc = LemoineStrings.T("globalSettings.dim.linksDesc"),
                                 DefaultOn = cfg.IncludeLinks },
                new ToggleItem { Id = "slabDiag", Label = LemoineStrings.T("globalSettings.dim.slabDiagLabel"),
                                 Desc = LemoineStrings.T("globalSettings.dim.slabDiagDesc"),
                                 DefaultOn = cfg.DiagnoseSlabEdge },
            });
            runToggles.StateChanged += state =>
            {
                if (state.TryGetValue("chain",    out var c)) cfg.ChainAligned      = c;
                if (state.TryGetValue("density",  out var dc)) cfg.DensityChaining  = dc;
                if (state.TryGetValue("callouts", out var co)) cfg.DenseCalloutsEnabled = co;
                if (state.TryGetValue("bothAxes", out var b)) cfg.DimensionBothAxes = b;
                if (state.TryGetValue("links",    out var l)) cfg.IncludeLinks      = l;
                if (state.TryGetValue("slabDiag", out var d)) cfg.DiagnoseSlabEdge  = d;
                cfg.Save();
            };
            panel.Children.Add(runToggles);

            AddCfgStepper(panel, LemoineStrings.T("globalSettings.dim.clusterDist"),
                LemoineStrings.T("globalSettings.dim.clusterDistDesc"),
                cfg.ClusterLinkPaperIn, 0, 4, 0.0625, 4,
                v => { cfg.ClusterLinkPaperIn = v; cfg.Save(); });
            AddCfgStepper(panel, LemoineStrings.T("globalSettings.dim.runLineTol"),
                LemoineStrings.T("globalSettings.dim.runLineTolDesc"),
                cfg.RunCrossPaperIn, 0, 1, 0.03125, 5,
                v => { cfg.RunCrossPaperIn = v; cfg.Save(); });
            AddCfgStepper(panel, LemoineStrings.T("globalSettings.dim.calloutMin"),
                LemoineStrings.T("globalSettings.dim.calloutMinDesc"),
                cfg.CalloutMinClashes, 2, 50, 1, 0,
                v => { cfg.CalloutMinClashes = (int)Math.Round(v); cfg.Save(); });

            var clashSettings = ClashDimensionSettings.Instance;
            AddCfgStepper(panel, LemoineStrings.T("globalSettings.dim.storeyMargin"),
                LemoineStrings.T("globalSettings.dim.storeyMarginDesc"),
                clashSettings.StoreyMarginMm / 304.8, 0, 12, 0.5, 2,
                v => { clashSettings.StoreyMarginMm = v * 304.8; clashSettings.Save(); });

            panel.Children.Add(HSep(12));

            // ── Target resolution ────────────────────────────────────────────
            panel.Children.Add(SubLabel(LemoineStrings.T("globalSettings.dim.targetRes")));

            AddCfgStepper(panel, LemoineStrings.T("globalSettings.dim.maxDist"),
                LemoineStrings.T("globalSettings.dim.maxDistDesc"),
                cfg.MaxDistanceFt, 1, 200, 1, 1,
                v => { cfg.MaxDistanceFt = v; cfg.Save(); });
            AddCfgStepper(panel, LemoineStrings.T("globalSettings.dim.axisTol"),
                LemoineStrings.T("globalSettings.dim.axisTolDesc"),
                cfg.AxisToleranceDeg, 0, 45, 1, 1,
                v => { cfg.AxisToleranceDeg = v; cfg.Save(); });
            AddCfgStepper(panel, LemoineStrings.T("globalSettings.dim.ambiguity"),
                LemoineStrings.T("globalSettings.dim.ambiguityDesc"),
                cfg.AmbiguityThresholdFt * 12.0, 0, 12, 0.25, 2,
                v => { cfg.AmbiguityThresholdFt = v / 12.0; cfg.Save(); });
            AddCfgStepper(panel, LemoineStrings.T("globalSettings.dim.slabAxisW"),
                LemoineStrings.T("globalSettings.dim.slabAxisWDesc"),
                cfg.SlabAxisWeight, 0, 1, 0.01, 2,
                v => { cfg.SlabAxisWeight = v; cfg.Save(); });
            AddCfgStepper(panel, LemoineStrings.T("globalSettings.dim.slabLenW"),
                LemoineStrings.T("globalSettings.dim.slabLenWDesc"),
                cfg.SlabLengthWeight, 0, 1, 0.01, 2,
                v => { cfg.SlabLengthWeight = v; cfg.Save(); });

            panel.Children.Add(DimRowLabel(LemoineStrings.T("globalSettings.dim.dimTypeName")));
            var typeBox = new TextBox { Text = cfg.DimensionTypeName, Width = 240,
                                        HorizontalAlignment = HorizontalAlignment.Left };
            typeBox.SetResourceReference(FrameworkElement.HeightProperty,                 "LemoineH_Input");
            typeBox.SetResourceReference(System.Windows.Controls.Control.PaddingProperty, "LemoineTh_InputPad");
            typeBox.SetResourceReference(TextBox.ForegroundProperty,  "LemoineText");
            typeBox.SetResourceReference(TextBox.BackgroundProperty,  "LemoineSelectBg");
            typeBox.SetResourceReference(TextBox.FontSizeProperty,    "LemoineFS_SM");
            typeBox.SetResourceReference(TextBox.FontFamilyProperty,  "LemoineMonoFont");
            typeBox.SetResourceReference(TextBox.BorderBrushProperty, "LemoineBorder");
            typeBox.TextChanged += (s, e) => { cfg.DimensionTypeName = typeBox.Text ?? ""; cfg.Save(); };
            panel.Children.Add(typeBox);

            panel.Children.Add(HSep(12));

            // ── Layout spacing (paper-space, inches) ─────────────────────────
            panel.Children.Add(SubLabel(LemoineStrings.T("globalSettings.dim.layoutSpacing")));

            AddCfgStepper(panel, LemoineStrings.T("globalSettings.dim.firstOffset"),
                LemoineStrings.T("globalSettings.dim.firstOffsetDesc"),
                cfg.Layout.FirstOffsetFt * 12.0, 0, 2, 0.0625, 4,
                v => { cfg.Layout.FirstOffsetFt = v / 12.0; cfg.Save(); });
            AddCfgStepper(panel, LemoineStrings.T("globalSettings.dim.stringSpacing"),
                LemoineStrings.T("globalSettings.dim.stringSpacingDesc"),
                cfg.Layout.StringSpacingFt * 12.0, 0, 2, 0.0625, 4,
                v => { cfg.Layout.StringSpacingFt = v / 12.0; cfg.Save(); });
            AddCfgStepper(panel, LemoineStrings.T("globalSettings.dim.roundingPrec"),
                LemoineStrings.T("globalSettings.dim.roundingPrecDesc"),
                cfg.Layout.PrecisionFt * 12.0, 0, 2, 0.0625, 4,
                v => { cfg.Layout.PrecisionFt = v / 12.0; cfg.Save(); });
            AddCfgStepper(panel, LemoineStrings.T("globalSettings.dim.textHeight"),
                LemoineStrings.T("globalSettings.dim.textHeightDesc"),
                cfg.Layout.TextHeightFt * 12.0, 0.03125, 1, 0.015625, 4,
                v => { cfg.Layout.TextHeightFt = v / 12.0; cfg.Save(); });

            panel.Children.Add(HSep(12));

            // ── Advanced layout ──────────────────────────────────────────────
            panel.Children.Add(SubLabel(LemoineStrings.T("globalSettings.dim.advLayout")));

            var layoutToggles = new LemoineToggleSwitches();
            layoutToggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "alignRows", Label = LemoineStrings.T("globalSettings.dim.alignRowsLabel"),
                                 Desc = LemoineStrings.T("globalSettings.dim.alignRowsDesc"),
                                 DefaultOn = cfg.Layout.AlignSharedRows },
                new ToggleItem { Id = "stagger", Label = LemoineStrings.T("globalSettings.dim.staggerLabel"),
                                 Desc = LemoineStrings.T("globalSettings.dim.staggerDesc"),
                                 DefaultOn = cfg.Layout.StaggerStackedText },
                new ToggleItem { Id = "snapshot", Label = LemoineStrings.T("globalSettings.dim.snapshotLabel"),
                                 Desc = LemoineStrings.T("globalSettings.dim.snapshotDesc"),
                                 DefaultOn = cfg.DumpLayoutSnapshots },
            });
            layoutToggles.StateChanged += state =>
            {
                if (state.TryGetValue("alignRows", out var a)) cfg.Layout.AlignSharedRows    = a;
                if (state.TryGetValue("stagger",   out var s)) cfg.Layout.StaggerStackedText = s;
                if (state.TryGetValue("snapshot",  out var d)) cfg.DumpLayoutSnapshots       = d;
                cfg.Save();
            };
            panel.Children.Add(layoutToggles);

            AddCfgStepper(panel, LemoineStrings.T("globalSettings.dim.maxStackedRows"),
                LemoineStrings.T("globalSettings.dim.maxStackedRowsDesc"),
                cfg.Layout.MaxOffsetSteps, 1, 20, 1, 0,
                v => { cfg.Layout.MaxOffsetSteps = (int)Math.Round(v); cfg.Save(); });
            AddCfgStepper(panel, LemoineStrings.T("globalSettings.dim.repairPasses"),
                LemoineStrings.T("globalSettings.dim.repairPassesDesc"),
                cfg.Layout.MaxRepairPasses, 0, 10, 1, 0,
                v => { cfg.Layout.MaxRepairPasses = (int)Math.Round(v); cfg.Save(); });

            scroll.Content = panel;
            return scroll;
        }

        // ── Local helpers ─────────────────────────────────────────────────────
        private static TextBlock DimIntro(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap,
                                     FontStyle = FontStyles.Italic, Margin = new Thickness(0, 0, 0, 14) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static TextBlock DimRowLabel(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap,
                                     Margin = new Thickness(0, 0, 0, 4) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static void AddCfgStepper(StackPanel parent, string label, string hint,
            double value, double min, double max, double step, int decimals, Action<double> onChange)
        {
            parent.Children.Add(DimRowLabel(label));

            var stepper = new LemoineInlineStepper
            {
                Value               = value,
                MinValue            = min,
                MaxValue            = max,
                Step                = step,
                Decimals            = decimals,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin              = new Thickness(0, 0, 0, 2),
                ToolTip             = hint,
            };
            stepper.ValueChanged += (s, v) => onChange(v);
            parent.Children.Add(stepper);

            var dim = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap,
                                      Margin = new Thickness(0, 0, 0, 10) };
            dim.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            dim.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            dim.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            parent.Children.Add(dim);
        }
    }
}
