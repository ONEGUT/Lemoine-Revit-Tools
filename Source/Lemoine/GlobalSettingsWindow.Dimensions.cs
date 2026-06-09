using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using LemoineTools.Lemoine.Controls;
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
        private const double DimMmPerFoot = 304.8;

        // Destination display ↔ stored TargetType, shared with the wizard's mapping.
        private const string DimGridDisplay   = "To Grid";
        private const string DimSlabDisplay   = "To Slab Edge";
        private const string DimManualDisplay = "To Picked Edge";

        private UIElement BuildDimensionsContent()
        {
            var cfg = AutoDimensionConfig.Instance;

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };

            panel.Children.Add(ContentHeader("Dimensions"));
            panel.Children.Add(DimIntro(
                "Defaults for the auto-dimension engine. The Clash Finder & Dimension wizard starts "
              + "each run from these values; changes there apply to that run only. Edit here to change "
              + "the saved defaults. Saved automatically."));

            // ── Run defaults ─────────────────────────────────────────────────
            panel.Children.Add(SubLabel("Run defaults"));

            var destPicker = new LemoineSingleSelect
            {
                Label        = "Default destination",
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
                new ToggleItem { Id = "chain", Label = "Chain aligned clash dimensions",
                                 Desc = "Merge collinear clashes that sit close together into one multi-segment string.",
                                 DefaultOn = cfg.ChainAligned },
                new ToggleItem { Id = "bothAxes", Label = "Dimension both axes",
                                 Desc = "Measure each clash in the view's X and Y directions (two dimensions per clash).",
                                 DefaultOn = cfg.DimensionBothAxes },
                new ToggleItem { Id = "links", Label = "Include linked models as targets",
                                 Desc = "Use loaded Revit links as target sources (coordination slabs / grids).",
                                 DefaultOn = cfg.IncludeLinks },
                new ToggleItem { Id = "slabDiag", Label = "Diagnose slab edge (log only)",
                                 Desc = "Logs a per-source face tally (host vs each link) and per-clash candidate ranking during a To Slab Edge run, to pinpoint why auto mode misses a linked slab. No effect on placement.",
                                 DefaultOn = cfg.DiagnoseSlabEdge },
            });
            runToggles.StateChanged += state =>
            {
                if (state.TryGetValue("chain",    out var c)) cfg.ChainAligned     = c;
                if (state.TryGetValue("bothAxes", out var b)) cfg.DimensionBothAxes = b;
                if (state.TryGetValue("links",    out var l)) cfg.IncludeLinks      = l;
                if (state.TryGetValue("slabDiag", out var d)) cfg.DiagnoseSlabEdge  = d;
                cfg.Save();
            };
            panel.Children.Add(runToggles);

            AddCfgStepper(panel, "Run gap (ft)",
                "Clashes farther apart than this along a run start a separate run (and a separate dimension).",
                cfg.RunGapMm / DimMmPerFoot, 0, 40, 0.5, 2,
                v => { cfg.RunGapMm = v * DimMmPerFoot; cfg.Save(); });
            AddCfgStepper(panel, "Run cross tolerance (ft)",
                "How far a clash may sit off the run line and still belong to it. Also the across-run snap.",
                cfg.RunCrossToleranceMm / DimMmPerFoot, 0, 8, 0.25, 2,
                v => { cfg.RunCrossToleranceMm = v * DimMmPerFoot; cfg.Save(); });

            panel.Children.Add(HSep(12));

            // ── Target resolution ────────────────────────────────────────────
            panel.Children.Add(SubLabel("Target resolution"));

            AddCfgStepper(panel, "Max target distance (ft)",
                "Reject targets whose projected horizontal distance exceeds this.",
                cfg.MaxDistanceFt, 1, 200, 1, 1,
                v => { cfg.MaxDistanceFt = v; cfg.Save(); });
            AddCfgStepper(panel, "Axis tolerance (deg)",
                "Keep faces / grids within this many degrees of the measurement axis.",
                cfg.AxisToleranceDeg, 0, 45, 1, 1,
                v => { cfg.AxisToleranceDeg = v; cfg.Save(); });
            AddCfgStepper(panel, "Ambiguity threshold (in)",
                "If the top two slab faces score within this, the pick is flagged ambiguous rather than guessed.",
                cfg.AmbiguityThresholdFt * 12.0, 0, 12, 0.25, 2,
                v => { cfg.AmbiguityThresholdFt = v / 12.0; cfg.Save(); });
            AddCfgStepper(panel, "Slab axis weight",
                "Slab-face scoring weight on axis deviation (degrees → feet-equivalent).",
                cfg.SlabAxisWeight, 0, 1, 0.01, 2,
                v => { cfg.SlabAxisWeight = v; cfg.Save(); });
            AddCfgStepper(panel, "Slab length weight",
                "Slab-face scoring credit for a larger / more primary boundary face.",
                cfg.SlabLengthWeight, 0, 1, 0.01, 2,
                v => { cfg.SlabLengthWeight = v; cfg.Save(); });

            panel.Children.Add(DimRowLabel("Dimension type name (blank = document default)"));
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
            panel.Children.Add(SubLabel("Layout spacing (paper-space, inches)"));

            AddCfgStepper(panel, "First string offset (in)",
                "How far the first dimension string sits off the source run, on paper.",
                cfg.Layout.FirstOffsetFt * 12.0, 0, 2, 0.0625, 4,
                v => { cfg.Layout.FirstOffsetFt = v / 12.0; cfg.Save(); });
            AddCfgStepper(panel, "String spacing (in)",
                "Paper-space gap between stacked dimension strings.",
                cfg.Layout.StringSpacingFt * 12.0, 0, 2, 0.0625, 4,
                v => { cfg.Layout.StringSpacingFt = v / 12.0; cfg.Save(); });
            AddCfgStepper(panel, "Rounding precision (in)",
                "Rounding applied to displayed dimension values.",
                cfg.Layout.PrecisionFt * 12.0, 0, 2, 0.0625, 4,
                v => { cfg.Layout.PrecisionFt = v / 12.0; cfg.Save(); });
            AddCfgStepper(panel, "Text height (in)",
                "Paper-space text height used to size collision bands.",
                cfg.Layout.TextHeightFt * 12.0, 0.03125, 1, 0.015625, 4,
                v => { cfg.Layout.TextHeightFt = v / 12.0; cfg.Save(); });

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
