using System;
using System.Collections.Generic;
using System.Linq;
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
    public class PrintViewViewModel : ILemoineTool, ILemoineReviewable, ILemoineConditionalSteps, ILemoineToolCleanup
    {
        // ── ILemoineTool ──────────────────────────────────────────────────────
        public string Title    => LemoineStrings.T("export.printView.title");
        public string RunLabel => LemoineStrings.T("export.printView.runLabel");

        // Each format's settings live in its own step, shown only when that format is
        // enabled (ILemoineConditionalSteps). The settings steps are never last —
        // S6 (Output) and S7 (Review & Run) are always visible.
        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", LemoineStrings.T("export.printView.steps.S1"),       required: true),
            new StepDefinition("S2", LemoineStrings.T("export.printView.steps.S2"),  required: false),
            new StepDefinition("S3", LemoineStrings.T("export.printView.steps.S3"),  required: false),
            new StepDefinition("S4", LemoineStrings.T("export.printView.steps.S4"),  required: false),
            new StepDefinition("S5", LemoineStrings.T("export.printView.steps.S5"),  required: false),
            new StepDefinition("S6", LemoineStrings.T("export.printView.steps.S6"),        required: true),
            new StepDefinition("S7", LemoineStrings.T("export.printView.steps.S7"),  required: false),
        };

        // ── ILemoineConditionalSteps ──────────────────────────────────────────
        public bool IsStepVisible(string stepId)
        {
            switch (stepId)
            {
                case "S2": return _pdfOn;
                case "S3": return _dwgOn;
                case "S4": return _nwcOn;
                case "S5": return _ifcOn;
                default:   return true;
            }
        }

        public event EventHandler? ValidationChanged;
        private void Fire() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── View info (set from command, immutable) ────────────────────────────
        private readonly ElementId    _viewId;
        private readonly string       _viewDisplayName;
        private readonly bool          _isThreeD;        // active view is a 3D view → NWC/IFC eligible
        private readonly List<string> _dwgSetupNames;

        // ── NWC faceting presets (label→value) ────────────────────────────────
        private static readonly string[] NwcFacetingLabels = { "Low — 0.5", "Standard — 1.0", "High — 2.0", "Ultra — 5.0" };
        private static readonly double[] NwcFacetingValues = { 0.5, 1.0, 2.0, 5.0 };

        // ── S1 state — formats ────────────────────────────────────────────────
        // Default from the shared settings so the choice tracks Bulk Export; NWC/IFC are
        // forced off on a non-3D view regardless of the saved flag.
        private bool _pdfOn = BulkExportSettings.Instance.ExportPdf;
        private bool _dwgOn = BulkExportSettings.Instance.ExportDwg;
        private bool _nwcOn;
        private bool _ifcOn;

        // ── PDF settings state ────────────────────────────────────────────────
        private string _zoomSetting     = BulkExportSettings.Instance.ZoomSetting;
        private int    _zoomPct         = BulkExportSettings.Instance.ZoomPercent;
        private string _colorDepth      = BulkExportSettings.Instance.ColorDepth;
        private string _rasterQuality   = BulkExportSettings.Instance.RasterQuality;
        private bool   _viewLinksBlue   = BulkExportSettings.Instance.ViewLinksInBlue;
        private bool   _replaceHalftone = BulkExportSettings.Instance.ReplaceHalftoneWithThinLines;

        // ── DWG settings state ────────────────────────────────────────────────
        private string _dwgSetup        = BulkExportSettings.Instance.DwgExportSetupName;

        // ── NWC settings state (all NavisworksExportOptions properties) ───────
        private string _nwcCoordinates         = BulkExportSettings.Instance.NwcCoordinates;
        private string _nwcParameters          = BulkExportSettings.Instance.NwcParameters;
        private bool   _nwcConvertElementProps  = BulkExportSettings.Instance.NwcConvertElementProps;
        private bool   _nwcDivideByLevel        = BulkExportSettings.Instance.NwcDivideByLevel;
        private bool   _nwcExportLinks          = BulkExportSettings.Instance.NwcExportLinks;
        private bool   _nwcExportParts          = BulkExportSettings.Instance.NwcExportParts;
        private bool   _nwcExportElementIds     = BulkExportSettings.Instance.NwcExportElementIds;
        private bool   _nwcExportUrls           = BulkExportSettings.Instance.NwcExportUrls;
        private bool   _nwcFindMissingMaterials = BulkExportSettings.Instance.NwcFindMissingMaterials;
        private bool   _nwcExportRoomGeometry   = BulkExportSettings.Instance.NwcExportRoomGeometry;
        private bool   _nwcExportRoomAsAttr     = BulkExportSettings.Instance.NwcExportRoomAsAttribute;
        private bool   _nwcConvertLights        = BulkExportSettings.Instance.NwcConvertLights;
        private bool   _nwcConvertLinkedCad     = BulkExportSettings.Instance.NwcConvertLinkedCad;
        private double _nwcFacetingFactor       = BulkExportSettings.Instance.NwcFacetingFactor;

        // ── IFC settings state ────────────────────────────────────────────────
        private string _ifcVersion       = BulkExportSettings.Instance.IfcVersion;

        // ── Output state ──────────────────────────────────────────────────────
        private string _outputFolder = BulkExportSettings.Instance.OutputFolder;

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly PrintViewEventHandler? _handler;
        private readonly ExternalEvent?          _event;

        // ── Constructor ───────────────────────────────────────────────────────
        public PrintViewViewModel(
            PrintViewEventHandler? handler,
            ExternalEvent?          externalEvent,
            ElementId               viewId,
            string                  viewDisplayName,
            bool                    isThreeD,
            List<string>            dwgSetupNames)
        {
            _handler         = handler;
            _event           = externalEvent;
            _viewId          = viewId;
            _viewDisplayName = viewDisplayName;
            _isThreeD        = isThreeD;
            _dwgSetupNames   = dwgSetupNames ?? new List<string>();

            // 3D-only formats only matter for a 3D view; honour the saved flag there.
            if (_isThreeD)
            {
                _nwcOn = BulkExportSettings.Instance.ExportNwc;
                _ifcOn = BulkExportSettings.Instance.ExportIfc;
            }

            // Default the DWG setup to the first available so the combo's shown value
            // matches what actually gets used.
            if (string.IsNullOrEmpty(_dwgSetup) && _dwgSetupNames.Count > 0)
                _dwgSetup = _dwgSetupNames[0];
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
                case "S1": return BuildS1Formats();
                case "S2": return BuildS2PdfSettings();
                case "S3": return BuildS3Dwg();
                case "S4": return BuildS4Nwc();
                case "S5": return BuildS5Ifc();
                case "S6": return BuildS6Output();
                default:   return null;
            }
        }

        // ── S1 — Formats ──────────────────────────────────────────────────────
        private FrameworkElement BuildS1Formats()
        {
            var outer = new StackPanel();
            AddSectionLabel(outer, LemoineStrings.T("export.printView.labels.secFormats"));

            var items = new List<ToggleItem>
            {
                new ToggleItem { Id = "pdf", Label = "PDF", Desc = LemoineStrings.T("export.printView.labels.descPdf"),  DefaultOn = _pdfOn },
                new ToggleItem { Id = "dwg", Label = "DWG", Desc = LemoineStrings.T("export.printView.labels.descDwg"), DefaultOn = _dwgOn },
            };
            if (_isThreeD)
            {
                items.Add(new ToggleItem { Id = "nwc", Label = "NWC", Desc = LemoineStrings.T("export.printView.labels.descNwc"), DefaultOn = _nwcOn });
                items.Add(new ToggleItem { Id = "ifc", Label = "IFC", Desc = LemoineStrings.T("export.printView.labels.descIfc"),        DefaultOn = _ifcOn });
            }

            var formatToggles = new LemoineToggleSwitches();
            formatToggles.SetItems(items);
            formatToggles.StateChanged += state =>
            {
                _pdfOn = state.TryGetValue("pdf", out bool pdfVal) && pdfVal;
                _dwgOn = state.TryGetValue("dwg", out bool dwgVal) && dwgVal;
                _nwcOn = _isThreeD && state.TryGetValue("nwc", out bool nwcVal) && nwcVal;
                _ifcOn = _isThreeD && state.TryGetValue("ifc", out bool ifcVal) && ifcVal;
                Fire();
            };
            outer.Children.Add(formatToggles);

            // Tell the user why NWC/IFC aren't offered for a non-3D view, rather than
            // silently omitting them.
            if (!_isThreeD)
            {
                var hint = new TextBlock
                {
                    Text         = LemoineStrings.T("export.printView.labels.non3dHint"),
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle    = FontStyles.Italic,
                    Margin       = new Thickness(0, 8, 0, 0),
                };
                hint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                hint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                hint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                outer.Children.Add(hint);
            }

            return outer;
        }

        // ── S2 — PDF Settings ─────────────────────────────────────────────────
        private FrameworkElement BuildS2PdfSettings()
        {
            var outer = new StackPanel();

            // PAGE SETUP ──────────────────────────────────────────────────────
            AddSectionLabel(outer, LemoineStrings.T("export.printView.labels.secPageSetup"));

            AddSmallLabel(outer, LemoineStrings.T("export.printView.labels.lblZoom"));
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
                Text              = LemoineStrings.T("export.printView.labels.pctPercent"),
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
            AddSectionLabel(outer, LemoineStrings.T("export.printView.labels.secOutputQuality"));

            AddLabeledComboBox(outer, LemoineStrings.T("export.printView.labels.lblColorDepth"),
                new[] { "Color", "Grayscale", "Black & White" },
                GetIndex(new[] { "Color", "Grayscale", "Black & White" }, _colorDepth),
                val => { _colorDepth = val; Fire(); });

            AddLabeledComboBox(outer, LemoineStrings.T("export.printView.labels.lblRasterQuality"),
                new[] { "Low", "Medium", "High", "Presentation" },
                GetIndex(new[] { "Low", "Medium", "High", "Presentation" }, _rasterQuality),
                val => { _rasterQuality = val; Fire(); });

            AddDivider(outer);

            // ADVANCED ────────────────────────────────────────────────────────
            AddSectionLabel(outer, LemoineStrings.T("export.printView.labels.secAdvanced"));

            var advToggles = new LemoineToggleSwitches();
            advToggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "viewlinks",       Label = LemoineStrings.T("export.printView.labels.advViewLinks"),              DefaultOn = _viewLinksBlue   },
                new ToggleItem { Id = "replacehalftone", Label = LemoineStrings.T("export.printView.labels.advReplaceHalftone"), DefaultOn = _replaceHalftone },
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

        // ── S3 — DWG Settings ─────────────────────────────────────────────────
        private FrameworkElement BuildS3Dwg()
        {
            var outer = new StackPanel();
            AddSectionLabel(outer, LemoineStrings.T("export.printView.labels.secDwgOptions"));

            var setupNames = _dwgSetupNames.Count > 0
                ? _dwgSetupNames.ToArray()
                : new[] { LemoineStrings.T("export.printView.labels.dwgNoSetups") };
            int initIdx = setupNames.Contains(_dwgSetup) ? Array.IndexOf(setupNames, _dwgSetup) : 0;
            AddLabeledComboBox(outer, LemoineStrings.T("export.printView.labels.lblExportSetup"), setupNames, initIdx,
                val => { _dwgSetup = val; Fire(); });

            var note = new TextBlock
            {
                Text         = LemoineStrings.T("export.printView.labels.dwgNote"),
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, 6, 0, 0),
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            note.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            note.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            outer.Children.Add(note);

            return outer;
        }

        // ── S4 — NWC Settings ─────────────────────────────────────────────────
        private FrameworkElement BuildS4Nwc()
        {
            var outer = new StackPanel();
            AddSectionLabel(outer, LemoineStrings.T("export.printView.labels.secNwcOptions"));

            var note = new TextBlock
            {
                Text         = LemoineStrings.T("export.printView.labels.nwcNote"),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 4),
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            note.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            note.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            outer.Children.Add(note);

            AddDivider(outer);

            // ── Coordinates & Parameters ──────────────────────────────────────
            AddSectionLabel(outer, LemoineStrings.T("export.printView.labels.secCoordParams"));

            AddLabeledComboBox(outer, LemoineStrings.T("export.printView.labels.lblCoordSystem"),
                new[] { "Shared", "Internal" },
                _nwcCoordinates == "Internal" ? 1 : 0,
                val => { _nwcCoordinates = val; Fire(); });

            AddLabeledComboBox(outer, LemoineStrings.T("export.printView.labels.lblElementParams"),
                new[] { "All", "Elements", "None" },
                _nwcParameters == "Elements" ? 1 : _nwcParameters == "None" ? 2 : 0,
                val => { _nwcParameters = val; Fire(); });

            AddDivider(outer);

            // ── Geometry & Mesh ───────────────────────────────────────────────
            AddSectionLabel(outer, LemoineStrings.T("export.printView.labels.secGeomMesh"));

            int initFacetIdx = Array.IndexOf(NwcFacetingValues, _nwcFacetingFactor);
            if (initFacetIdx < 0) initFacetIdx = 1; // fallback to Standard

            AddLabeledComboBox(outer, LemoineStrings.T("export.printView.labels.lblMeshQuality"),
                NwcFacetingLabels, initFacetIdx,
                val =>
                {
                    int idx = Array.IndexOf(NwcFacetingLabels, val);
                    _nwcFacetingFactor = idx >= 0 ? NwcFacetingValues[idx] : 1.0;
                    Fire();
                });

            AddNwcCheckBox(outer, LemoineStrings.T("export.printView.labels.cbConvertProps"), _nwcConvertElementProps, v => _nwcConvertElementProps = v);
            AddNwcCheckBox(outer, LemoineStrings.T("export.printView.labels.cbConvertLights"),       _nwcConvertLights,       v => _nwcConvertLights       = v);
            AddNwcCheckBox(outer, LemoineStrings.T("export.printView.labels.cbConvertCad"),    _nwcConvertLinkedCad,    v => _nwcConvertLinkedCad    = v);

            AddDivider(outer);

            // ── Content to Include ────────────────────────────────────────────
            AddSectionLabel(outer, LemoineStrings.T("export.printView.labels.secContent"));

            AddNwcCheckBox(outer, LemoineStrings.T("export.printView.labels.cbDivideLevels"),                    _nwcDivideByLevel,        v => _nwcDivideByLevel        = v);
            AddNwcCheckBox(outer, LemoineStrings.T("export.printView.labels.cbLinkedRevit"),                _nwcExportLinks,          v => _nwcExportLinks          = v);
            AddNwcCheckBox(outer, LemoineStrings.T("export.printView.labels.cbParts"),                        _nwcExportParts,          v => _nwcExportParts          = v);
            AddNwcCheckBox(outer, LemoineStrings.T("export.printView.labels.cbElementIds"),     _nwcExportElementIds,     v => _nwcExportElementIds     = v);
            AddNwcCheckBox(outer, LemoineStrings.T("export.printView.labels.cbUrls"),                     _nwcExportUrls,           v => _nwcExportUrls           = v);
            AddNwcCheckBox(outer, LemoineStrings.T("export.printView.labels.cbMissingMats"),                     _nwcFindMissingMaterials, v => _nwcFindMissingMaterials = v);
            AddNwcCheckBox(outer, LemoineStrings.T("export.printView.labels.cbRoomGeom"), _nwcExportRoomGeometry, v => _nwcExportRoomGeometry = v);
            AddNwcCheckBox(outer, LemoineStrings.T("export.printView.labels.cbRoomAttr"),     _nwcExportRoomAsAttr,     v => _nwcExportRoomAsAttr     = v);

            return outer;
        }

        // ── S5 — IFC Settings ─────────────────────────────────────────────────
        private FrameworkElement BuildS5Ifc()
        {
            var outer = new StackPanel();
            AddSectionLabel(outer, LemoineStrings.T("export.printView.labels.secIfcOptions"));

            var note = new TextBlock
            {
                Text         = LemoineStrings.T("export.printView.labels.ifcNote"),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 4),
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            note.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            note.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            outer.Children.Add(note);

            AddLabeledComboBox(outer, LemoineStrings.T("export.printView.labels.lblIfcVersion"),
                new[] { "IFC2x3", "IFC4" },
                _ifcVersion == "IFC4" ? 1 : 0,
                val => { _ifcVersion = val; Fire(); });

            return outer;
        }

        // ── S6 — Output ───────────────────────────────────────────────────────
        private FrameworkElement BuildS6Output()
        {
            var outer = new StackPanel();
            AddSectionLabel(outer, LemoineStrings.T("export.printView.labels.secOutputFolder"));

            var folder = new LemoineFolderBrowser
            {
                Path        = _outputFolder,
                DialogTitle = LemoineStrings.T("export.printView.labels.folderDialog"),
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
            ("view",    LemoineStrings.T("export.printView.review.itemView")),
            ("formats", LemoineStrings.T("export.printView.review.itemFormats")),
            ("quality", LemoineStrings.T("export.printView.review.itemQuality")),
            ("folder",  LemoineStrings.T("export.printView.review.itemFolder")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["view"]    = _viewDisplayName,
            ["formats"] = FormatsSummary(),
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
                case "S1": return _pdfOn || _dwgOn || _nwcOn || _ifcOn;
                case "S6": return !string.IsNullOrWhiteSpace(_outputFolder);
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return FormatsSummary();
                case "S2": return $"{_colorDepth} · {_rasterQuality} · {_zoomSetting}";
                case "S3": return string.IsNullOrEmpty(_dwgSetup) ? LemoineStrings.T("export.printView.summaries.dwgDefault") : _dwgSetup;
                case "S4": return $"{_nwcCoordinates} · {_nwcParameters}";
                case "S5": return _ifcVersion;
                case "S6": return string.IsNullOrEmpty(_outputFolder) ? LemoineStrings.T("export.printView.summaries.noFolder") : _outputFolder;
                default:   return "—";
            }
        }

        private string FormatsSummary()
        {
            var on = new List<string>();
            if (_pdfOn) on.Add("PDF");
            if (_dwgOn) on.Add("DWG");
            if (_nwcOn) on.Add("NWC");
            if (_ifcOn) on.Add("IFC");
            return on.Count > 0 ? string.Join(" · ", on) : LemoineStrings.T("export.printView.summaries.formatsNone");
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            if (_handler == null || _event == null) return;

            // Persist shared quality/format settings so both tools track each other.
            // The format on/off flags are intentionally NOT written back — they depend on
            // the transient active-view type and would clobber Bulk Export's selection.
            var s = BulkExportSettings.Instance;
            s.ZoomSetting                  = _zoomSetting;
            s.ZoomPercent                  = _zoomPct;
            s.ColorDepth                   = _colorDepth;
            s.RasterQuality                = _rasterQuality;
            s.ViewLinksInBlue              = _viewLinksBlue;
            s.ReplaceHalftoneWithThinLines = _replaceHalftone;
            s.DwgExportSetupName           = _dwgSetup;
            s.NwcCoordinates               = _nwcCoordinates;
            s.NwcParameters                = _nwcParameters;
            s.NwcConvertElementProps       = _nwcConvertElementProps;
            s.NwcDivideByLevel             = _nwcDivideByLevel;
            s.NwcExportLinks               = _nwcExportLinks;
            s.NwcExportParts               = _nwcExportParts;
            s.NwcExportElementIds          = _nwcExportElementIds;
            s.NwcExportUrls                = _nwcExportUrls;
            s.NwcFindMissingMaterials      = _nwcFindMissingMaterials;
            s.NwcExportRoomGeometry        = _nwcExportRoomGeometry;
            s.NwcExportRoomAsAttribute     = _nwcExportRoomAsAttr;
            s.NwcConvertLights             = _nwcConvertLights;
            s.NwcConvertLinkedCad          = _nwcConvertLinkedCad;
            s.NwcFacetingFactor            = _nwcFacetingFactor;
            s.IfcVersion                   = _ifcVersion;
            s.OutputFolder                 = _outputFolder;
            s.Save();

            _handler.ViewId       = _viewId;
            _handler.OutputFolder = _outputFolder;

            _handler.ExportPdf = _pdfOn;
            _handler.ExportDwg = _dwgOn;
            _handler.ExportNwc = _nwcOn;
            _handler.ExportIfc = _ifcOn;

            _handler.ZoomSetting                  = _zoomSetting;
            _handler.ZoomPercent                  = _zoomPct;
            _handler.ColorDepth                   = _colorDepth;
            _handler.RasterQuality                = _rasterQuality;
            _handler.ViewLinksInBlue              = _viewLinksBlue;
            _handler.ReplaceHalftoneWithThinLines = _replaceHalftone;

            _handler.DwgSetupName = _dwgSetup;

            _handler.NwcCoordinates           = _nwcCoordinates;
            _handler.NwcParameters            = _nwcParameters;
            _handler.NwcConvertElementProps   = _nwcConvertElementProps;
            _handler.NwcDivideByLevel         = _nwcDivideByLevel;
            _handler.NwcExportLinks           = _nwcExportLinks;
            _handler.NwcExportParts           = _nwcExportParts;
            _handler.NwcExportElementIds      = _nwcExportElementIds;
            _handler.NwcExportUrls            = _nwcExportUrls;
            _handler.NwcFindMissingMaterials  = _nwcFindMissingMaterials;
            _handler.NwcExportRoomGeometry    = _nwcExportRoomGeometry;
            _handler.NwcExportRoomAsAttribute = _nwcExportRoomAsAttr;
            _handler.NwcConvertLights         = _nwcConvertLights;
            _handler.NwcConvertLinkedCad      = _nwcConvertLinkedCad;
            _handler.NwcFacetingFactor        = _nwcFacetingFactor;

            _handler.IfcVersion = _ifcVersion;

            _handler.PushLog    = pushLog;
            _handler.OnProgress = onProgress;
            _handler.OnComplete = onComplete;

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

        private void AddNwcCheckBox(System.Windows.Controls.Panel parent, string label, bool isChecked, Action<bool> onChange)
        {
            var cb = new CheckBox { Content = label, IsChecked = isChecked, Margin = new Thickness(0, 0, 0, 4) };
            cb.SetResourceReference(CheckBox.ForegroundProperty, "LemoineText");
            cb.SetResourceReference(CheckBox.FontFamilyProperty, "LemoineUiFont");
            cb.SetResourceReference(CheckBox.FontSizeProperty,   "LemoineFS_MD");
            cb.Checked   += (s, e) => { onChange(true);  Fire(); };
            cb.Unchecked += (s, e) => { onChange(false); Fire(); };
            parent.Children.Add(cb);
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
