using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

using WpfComboBox   = System.Windows.Controls.ComboBox;
using WpfVisibility = System.Windows.Visibility;
using WpfBrushes    = System.Windows.Media.Brushes;

namespace LemoineTools.Tools.BulkExport
{
    public class PrintViewViewModel : ILemoineTool, ILemoineReviewable, ILemoineToolCleanup
    {
        // ── ILemoineTool ──────────────────────────────────────────────────────
        public string Title    => "Print View";
        public string RunLabel => "Print in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "PDF Settings", required: false),
            new StepDefinition("S2", "Output",       required: true),
            new StepDefinition("S3", "Review & Run", required: false),
        };

        public event EventHandler? ValidationChanged;
        private void Fire() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── View info (set from command, immutable) ────────────────────────────
        private readonly ElementId _viewId;
        private readonly string    _viewDisplayName;

        // ── S1 state — PDF settings ───────────────────────────────────────────
        private string _zoomSetting     = BulkExportSettings.Instance.ZoomSetting;
        private int    _zoomPct         = BulkExportSettings.Instance.ZoomPercent;
        private string _colorDepth      = BulkExportSettings.Instance.ColorDepth;
        private string _rasterQuality   = BulkExportSettings.Instance.RasterQuality;
        private bool   _viewLinksBlue   = BulkExportSettings.Instance.ViewLinksInBlue;
        private bool   _replaceHalftone = BulkExportSettings.Instance.ReplaceHalftoneWithThinLines;

        // ── S2 state — output folder ──────────────────────────────────────────
        private string _outputFolder = BulkExportSettings.Instance.OutputFolder;

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly PrintViewEventHandler? _handler;
        private readonly ExternalEvent?          _event;

        // ── Constructor ───────────────────────────────────────────────────────
        public PrintViewViewModel(
            PrintViewEventHandler? handler,
            ExternalEvent?          externalEvent,
            ElementId               viewId,
            string                  viewDisplayName)
        {
            _handler         = handler;
            _event           = externalEvent;
            _viewId          = viewId;
            _viewDisplayName = viewDisplayName;
        }

        // ── ILemoineToolCleanup ───────────────────────────────────────────────
        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.PushLog    = null;
            _handler.OnProgress = null;
            _handler.OnComplete = null;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  GetStepContent
        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildS1PdfSettings();
                case "S2": return BuildS2Output();
                default:   return null;
            }
        }

        // ── S1 — PDF Settings ─────────────────────────────────────────────────
        private FrameworkElement BuildS1PdfSettings()
        {
            var outer = new StackPanel();

            // PAGE SETUP ──────────────────────────────────────────────────────
            AddSectionLabel(outer, "PAGE SETUP");

            AddSmallLabel(outer, "Zoom");
            var fitBtn   = BuildModeButton("Fit to Page", _zoomSetting == "Fit to Page");
            var scaleBtn = BuildModeButton("Scale %",     _zoomSetting == "Scale %");
            var zoomRow  = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
            zoomRow.Children.Add(fitBtn);
            zoomRow.Children.Add(scaleBtn);
            outer.Children.Add(zoomRow);

            // Zoom stepper — visible only when "Scale %" is active
            var stepperRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 4, 0, 8),
                Visibility  = _zoomSetting == "Scale %" ? WpfVisibility.Visible : WpfVisibility.Collapsed,
            };
            var stepper = new LemoineInlineStepper
            {
                Value      = _zoomPct,
                MinValue   = 10,
                MaxValue   = 500,
                Step       = 5,
                Decimals   = 0,
                ValueWidth = 48,
            };
            stepper.ValueChanged += (s, v) => { _zoomPct = (int)v; Fire(); };
            var pctLabel = new TextBlock
            {
                Text              = "%",
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(6, 0, 0, 0),
            };
            pctLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            pctLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            pctLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            stepperRow.Children.Add(stepper);
            stepperRow.Children.Add(pctLabel);
            outer.Children.Add(stepperRow);

            fitBtn.Click += (s, e) =>
            {
                _zoomSetting = "Fit to Page";
                RefreshModeButtons(fitBtn, scaleBtn, true);
                stepperRow.Visibility = WpfVisibility.Collapsed;
                Fire();
            };
            scaleBtn.Click += (s, e) =>
            {
                _zoomSetting = "Scale %";
                RefreshModeButtons(fitBtn, scaleBtn, false);
                stepperRow.Visibility = WpfVisibility.Visible;
                Fire();
            };

            AddDivider(outer);

            // OUTPUT QUALITY ──────────────────────────────────────────────────
            AddSectionLabel(outer, "OUTPUT QUALITY");

            AddLabeledComboBox(outer, "Color Depth",
                new[] { "Color", "Grayscale", "Black & White" },
                GetIndex(new[] { "Color", "Grayscale", "Black & White" }, _colorDepth),
                val => { _colorDepth = val; Fire(); });

            AddLabeledComboBox(outer, "Raster Quality",
                new[] { "Low", "Medium", "High", "Presentation" },
                GetIndex(new[] { "Low", "Medium", "High", "Presentation" }, _rasterQuality),
                val => { _rasterQuality = val; Fire(); });

            AddDivider(outer);

            // ADVANCED ────────────────────────────────────────────────────────
            AddSectionLabel(outer, "ADVANCED");

            var advToggles = new LemoineToggleSwitches();
            advToggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "viewlinks",       Label = "View links in blue",              DefaultOn = _viewLinksBlue   },
                new ToggleItem { Id = "replacehalftone", Label = "Replace halftone with thin lines", DefaultOn = _replaceHalftone },
            });
            advToggles.StateChanged += state =>
            {
                state.TryGetValue("viewlinks",       out _viewLinksBlue);
                state.TryGetValue("replacehalftone", out _replaceHalftone);
                Fire();
            };
            outer.Children.Add(advToggles);

            return outer;
        }

        // ── S2 — Output ───────────────────────────────────────────────────────
        private FrameworkElement BuildS2Output()
        {
            var outer = new StackPanel();
            AddSectionLabel(outer, "OUTPUT FOLDER");

            var folder = new LemoineFolderBrowser
            {
                Path        = _outputFolder,
                DialogTitle = "Select output folder for PDF",
            };
            folder.PathChanged += p => { _outputFolder = p; Fire(); };
            outer.Children.Add(folder);

            return outer;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  ILemoineReviewable
        // ═════════════════════════════════════════════════════════════════════
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("view",    "View"),
            ("quality", "Quality"),
            ("folder",  "Output Folder"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["view"]    = _viewDisplayName,
            ["quality"] = $"{_colorDepth} · {_rasterQuality} · {_zoomSetting}",
            ["folder"]  = string.IsNullOrEmpty(_outputFolder) ? "—"
                : _outputFolder.Length > 40 ? "…" + _outputFolder.Substring(_outputFolder.Length - 37)
                : _outputFolder,
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => null;
        public string?        ReviewWarning => null;

        // ═════════════════════════════════════════════════════════════════════
        //  IsValid / SummaryFor / Run
        // ═════════════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return true;
                case "S2": return !string.IsNullOrWhiteSpace(_outputFolder);
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return $"{_colorDepth} · {_rasterQuality} · {_zoomSetting}";
                case "S2": return string.IsNullOrEmpty(_outputFolder) ? "No output folder" : _outputFolder;
                default:   return "—";
            }
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            if (_handler == null || _event == null) return;

            // Persist PDF settings back to BulkExportSettings so both tools share quality prefs
            var s = BulkExportSettings.Instance;
            s.ZoomSetting                  = _zoomSetting;
            s.ZoomPercent                  = _zoomPct;
            s.ColorDepth                   = _colorDepth;
            s.RasterQuality                = _rasterQuality;
            s.ViewLinksInBlue              = _viewLinksBlue;
            s.ReplaceHalftoneWithThinLines = _replaceHalftone;
            s.OutputFolder                 = _outputFolder;
            s.Save();

            _handler.ViewId                      = _viewId;
            _handler.OutputFolder                = _outputFolder;
            _handler.ZoomSetting                 = _zoomSetting;
            _handler.ZoomPercent                 = _zoomPct;
            _handler.ColorDepth                  = _colorDepth;
            _handler.RasterQuality               = _rasterQuality;
            _handler.ViewLinksInBlue             = _viewLinksBlue;
            _handler.ReplaceHalftoneWithThinLines = _replaceHalftone;
            _handler.PushLog                     = pushLog;
            _handler.OnProgress                  = onProgress;
            _handler.OnComplete                  = onComplete;

            _event.Raise();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Shared UI helpers (mirrors BulkExportViewModel's private helpers)
        // ═════════════════════════════════════════════════════════════════════

        private Button BuildModeButton(string label, bool active)
        {
            var b = new Button
            {
                Content         = label,
                Margin          = new Thickness(0, 0, 4, 0),
                BorderThickness = new Thickness(1),
                Template        = LemoineControlStyles.BuildFlatButtonTemplate(),
                Cursor          = Cursors.Hand,
            };
            b.SetResourceReference(Button.MinHeightProperty,  "LemoineH_BtnMin");
            b.SetResourceReference(Button.PaddingProperty,    "LemoineTh_BtnPad");
            b.SetResourceReference(Button.FontSizeProperty,   "LemoineFS_MD");
            b.SetResourceReference(Button.FontFamilyProperty, "LemoineUiFont");
            ApplyModeButtonStyle(b, active);
            return b;
        }

        private static void ApplyModeButtonStyle(Button b, bool active)
        {
            if (active)
            {
                b.SetResourceReference(Button.BackgroundProperty,  "LemoineAccentDim");
                b.SetResourceReference(Button.BorderBrushProperty, "LemoineAccent");
                b.SetResourceReference(Button.ForegroundProperty,  "LemoineAccent");
            }
            else
            {
                b.Background = WpfBrushes.Transparent; // ⚠ direct assignment — "Transparent" is not a resource key
                b.SetResourceReference(Button.BorderBrushProperty, "LemoineBorder");
                b.SetResourceReference(Button.ForegroundProperty,  "LemoineText");
            }
        }

        private static void RefreshModeButtons(Button a, Button b, bool aActive)
        {
            ApplyModeButtonStyle(a, aActive);
            ApplyModeButtonStyle(b, !aActive);
        }

        private static void AddSectionLabel(System.Windows.Controls.Panel parent, string text)
        {
            var lbl = new TextBlock
            {
                Text         = text,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, 0, 0, 4),
                TextWrapping = TextWrapping.Wrap,
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            parent.Children.Add(lbl);
        }

        private static void AddSmallLabel(System.Windows.Controls.Panel parent, string text)
        {
            var lbl = new TextBlock
            {
                Text   = text,
                Margin = new Thickness(0, 0, 0, 2),
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            parent.Children.Add(lbl);
        }

        private static void AddDivider(System.Windows.Controls.Panel parent)
        {
            var sep = new System.Windows.Shapes.Rectangle { Height = 1, Margin = new Thickness(0, 10, 0, 10) };
            sep.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "LemoineBorder");
            parent.Children.Add(sep);
        }

        private static void AddLabeledComboBox(
            System.Windows.Controls.Panel parent,
            string label,
            string[] items,
            int selectedIndex,
            Action<string> onChange)
        {
            var lbl = new TextBlock { Text = label, Margin = new Thickness(0, 4, 0, 2) };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            parent.Children.Add(lbl);

            var combo = new WpfComboBox
            {
                ItemsSource       = items,
                SelectedIndex     = Math.Max(0, Math.Min(selectedIndex, items.Length - 1)),
                IsEditable        = false,
                MaxDropDownHeight = 200,
                Margin            = new Thickness(0, 0, 0, 4),
            };
            combo.SetResourceReference(WpfComboBox.BackgroundProperty,  "LemoineSelectBg");
            combo.SetResourceReference(WpfComboBox.ForegroundProperty,  "LemoineText");
            combo.SetResourceReference(WpfComboBox.FontFamilyProperty,  "LemoineUiFont");
            combo.SetResourceReference(WpfComboBox.FontSizeProperty,    "LemoineFS_MD");
            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedItem is string val) onChange(val);
            };
            parent.Children.Add(combo);
        }

        private static int GetIndex(string[] items, string value)
        {
            int idx = Array.IndexOf(items, value);
            return idx >= 0 ? idx : 0;
        }
    }
}
