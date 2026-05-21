using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfGrid   = System.Windows.Controls.Grid;
using WpfPoint  = System.Windows.Point;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.Ceilings
{
    public class CeilingHeatmapViewModel : ILemoineTool, ILemoineToolSettings
    {
        // ── Identity ─────────────────────────────────────────────────────────────
        public string Title    => "Ceiling Elevation Heatmap";
        public string RunLabel => "Apply Heatmap →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Ceiling Plan Views", required: true),
            new StepDefinition("S2", "Run Options",               required: false),
            new StepDefinition("S3", "Review & Run",              required: false),
        };

        // ── State ────────────────────────────────────────────────────────────────
        private List<string>                         _selectedViewNames = new List<string>();
        private readonly Dictionary<string, ElementId>     _viewNameToId;
        private readonly Dictionary<string, List<string>>  _viewsByLevel;
        private bool _deleteExisting = true;
        private bool _placeTags      = CeilingHeatmapSettings.Instance.PlaceTags;

        // ── Debug wiring (registered once at plugin startup) ─────────────────────
        private static CeilingHeatmapDebugHandler _debugHandler;
        private static ExternalEvent              _debugEvent;

        /// <summary>
        /// Called by the plugin's app/command class after creating the debug
        /// ExternalEvent so the settings panel button can raise it.
        /// </summary>
        public static void RegisterDebugEvent(
            CeilingHeatmapDebugHandler handler, ExternalEvent evt)
        {
            _debugHandler = handler;
            _debugEvent   = evt;
        }

        // ── Validation ───────────────────────────────────────────────────────────
        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── Revit wiring ─────────────────────────────────────────────────────────
        private readonly CeilingHeatmapEventHandler? _handler;
        private readonly ExternalEvent?              _event;

        public CeilingHeatmapViewModel(
            CeilingHeatmapEventHandler? handler,
            ExternalEvent?              externalEvent,
            Dictionary<string, List<(string Name, ElementId Id)>>? viewsByLevel = null)
        {
            _handler = handler;
            _event   = externalEvent;

            _viewNameToId = new Dictionary<string, ElementId>();
            _viewsByLevel = new Dictionary<string, List<string>>();

            if (viewsByLevel != null)
            {
                foreach (var kvp in viewsByLevel)
                {
                    var names = new List<string>();
                    foreach (var (n, id) in kvp.Value)
                    {
                        _viewNameToId[n] = id;
                        names.Add(n);
                    }
                    _viewsByLevel[kvp.Key] = names;
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════════
        // GetStepContent
        // ═════════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildS1();
                case "S2": return BuildS2();
                case "S3": return BuildReview();
                default:   return null;
            }
        }

        // ── S1: View selection grouped by level ──────────────────────────────────
        private FrameworkElement BuildS1()
        {
            if (_viewsByLevel.Count == 0)
            {
                var msg = new TextBlock
                {
                    Text         = "No reflected ceiling plan views found in this document.",
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle    = FontStyles.Italic,
                };
                msg.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                msg.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                msg.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                return msg;
            }

            var tabs = new LemoineMultiSelectTabs();
            tabs.SetGroups(_viewsByLevel);
            tabs.SelectionChanged += selected =>
            {
                _selectedViewNames = new List<string>(selected);
                OnValidationChanged();
            };
            return tabs;
        }

        // ── S2: Run options ──────────────────────────────────────────────────────
        private FrameworkElement BuildS2()
        {
            var tog = new LemoineToggleSwitches();
            tog.SetItems(new List<ToggleItem>
            {
                new ToggleItem
                {
                    Id        = "delete",
                    Label     = "Delete existing heatmap filters",
                    Desc      = "Remove all \"Ceiling Heatmap\" view filters from the project before applying the new set.",
                    DefaultOn = true,
                },
                new ToggleItem
                {
                    Id        = "tags",
                    Label     = "Place ceiling tags",
                    Desc      = "Place a Ceiling_Tag family instance at the centroid of each ceiling in the selected views. Ceilings that already carry a tag are skipped.",
                    DefaultOn = CeilingHeatmapSettings.Instance.PlaceTags,
                },
            });
            tog.StateChanged += state =>
            {
                _deleteExisting = state.TryGetValue("delete", out bool dv) && dv;
                _placeTags      = state.TryGetValue("tags",   out bool tv) && tv;
                CeilingHeatmapSettings.Instance.PlaceTags = _placeTags;
                CeilingHeatmapSettings.Instance.Save();
                OnValidationChanged();
            };
            return tog;
        }

        // ── S3: Review ───────────────────────────────────────────────────────────
        private struct CardDef
        {
            public string Label; public Func<string> Val; public int Row; public int Col;
            public CardDef(string l, Func<string> v, int r, int c)
            { Label = l; Val = v; Row = r; Col = c; }
        }

        private FrameworkElement BuildReview()
        {
            var outer = new StackPanel();
            var grid  = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var cards = new[]
            {
                new CardDef("Views Selected",
                    () => _selectedViewNames.Count == 0 ? "—"
                        : $"{_selectedViewNames.Count} view{(_selectedViewNames.Count == 1 ? "" : "s")}",
                    0, 0),
                new CardDef("Color Ramp",
                    () => "Low → Mid → High",
                    0, 1),
                new CardDef("Delete Existing Filters",
                    () => _deleteExisting ? "Yes" : "No",
                    1, 0),
                new CardDef("Include Linked Ceilings",
                    () => CeilingHeatmapSettings.Instance.IncludeLinks ? "Yes" : "No",
                    1, 1),
                new CardDef("Place Ceiling Tags",
                    () => _placeTags ? "Yes" : "No",
                    2, 0),
            };

            foreach (var c in cards)
            {
                var card = new Border
                {
                    Margin          = new Thickness(c.Col == 0 ? 0 : 4, c.Row == 0 ? 0 : 4, 0, 0),
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(3),
                };
                card.SetResourceReference(Border.PaddingProperty,    "LemoineTh_CardPad");
                card.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

                var lbl = new TextBlock { Text = c.Label.ToUpper(), Margin = new Thickness(0, 0, 0, 2) };
                lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

                var capturedVal = c.Val;
                var valText = new TextBlock { FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap };
                valText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                valText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                valText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                valText.Text = capturedVal();
                ValidationChanged += (s, e) => valText.Text = capturedVal();

                var sp = new StackPanel();
                sp.Children.Add(lbl);
                sp.Children.Add(valText);
                card.Child = sp;
                WpfGrid.SetRow(card, c.Row);
                WpfGrid.SetColumn(card, c.Col);
                grid.Children.Add(card);
            }
            outer.Children.Add(grid);

            var desc = new TextBlock
            {
                Text         = "Selected ceiling plan views will be scanned for \"Height Offset From Level\" data. One view filter is created per distinct height offset bucket and applied to every selected view as a solid surface color override.",
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, 8, 0, 0),
            };
            desc.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            desc.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            desc.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(desc);

            return outer;
        }

        // ═════════════════════════════════════════════════════════════════════════
        // IsValid / SummaryFor / Run
        // ═════════════════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _selectedViewNames.Count > 0;
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1")
                return _selectedViewNames.Count == 0 ? "—"
                    : $"{_selectedViewNames.Count} view{(_selectedViewNames.Count == 1 ? "" : "s")} selected";
            if (stepId == "S2")
            {
                var parts = new List<string>();
                if (_deleteExisting) parts.Add("Delete existing: on");
                if (_placeTags)      parts.Add("Place tags: on");
                return parts.Count > 0 ? string.Join(", ", parts) : "No options enabled";
            }
            if (stepId == "S3")
                return "Ready to run";
            return "—";
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _handler!.SelectedViewIds = _selectedViewNames
                .Where(n => _viewNameToId.ContainsKey(n))
                .Select(n => _viewNameToId[n])
                .ToList();
            _handler.DeleteExisting  = _deleteExisting;
            _handler.PlaceTags       = _placeTags;
            _handler.IncludeLinks    = CeilingHeatmapSettings.Instance.IncludeLinks;
            _handler.ElevTolerance   = CeilingHeatmapSettings.Instance.ElevTolerance;
            _handler.ColorLow        = ParseRevitColor(CeilingHeatmapSettings.Instance.ColorLow,  0,   0, 255);
            _handler.ColorMid        = ParseRevitColor(CeilingHeatmapSettings.Instance.ColorMid,  0, 255,   0);
            _handler.ColorHigh       = ParseRevitColor(CeilingHeatmapSettings.Instance.ColorHigh, 255,  0,   0);
            _handler.PushLog         = pushLog;
            _handler.OnProgress      = onProgress;
            _handler.OnComplete      = onComplete;
            pushLog("Starting Ceiling Elevation Heatmap…", "info");
            _event!.Raise();
        }


        // ═════════════════════════════════════════════════════════════════════════
        // ILemoineToolSettings — declarative spec
        // ═════════════════════════════════════════════════════════════════════════

        public LemoineToolSettingsSpec GetSettingsSpec()
        {
            var s = CeilingHeatmapSettings.Instance;
            return new LemoineToolSettingsSpec
            {
                Id          = "T03",
                Label       = "Ceiling Heatmap",
                Icon        = "03",
                Description = "Color ramp, elevation tolerance, and tag settings for the ceiling heatmap.",
                Groups = new List<LemoineSettingsGroup>
                {
                    new LemoineSettingsGroup
                    {
                        Id = "G1", Title = "Color Ramp", OpenByDefault = true,
                        Hint = "Three-stop gradient from lowest to highest ceiling elevation offset.",
                        Settings = new List<LemoineSettingDef>
                        {
                            new LemoineSettingDef { Id="colorLow",  Kind="color", Label="Low color",
                                Hint="Applied to ceilings with the smallest height offset from level.",
                                Default=s.ColorLow  },
                            new LemoineSettingDef { Id="colorMid",  Kind="color", Label="Mid color",
                                Default=s.ColorMid  },
                            new LemoineSettingDef { Id="colorHigh", Kind="color", Label="High color",
                                Hint="Applied to ceilings with the largest height offset from level.",
                                Default=s.ColorHigh },
                        }
                    },
                    new LemoineSettingsGroup
                    {
                        Id = "G2", Title = "Detection",
                        Settings = new List<LemoineSettingDef>
                        {
                            new LemoineSettingDef { Id="elevTolerance", Kind="number",
                                Label="Elevation tolerance",
                                Hint="Ceilings within this tolerance (inches) share one filter bucket.",
                                Options=new NumberOpts { Unit="in", Min=0.01, Max=12.0, Step=0.01 },
                                Default=Math.Round(s.ElevTolerance * 12.0, 4) },
                            new LemoineSettingDef { Id="includeLinks", Kind="toggle",
                                Label="Include linked ceilings",
                                Hint="Scan Revit link instances visible in each selected view.",
                                Default=s.IncludeLinks },
                            new LemoineSettingDef { Id="placeTags", Kind="toggle",
                                Label="Place ceiling tags",
                                Hint="Place a Ceiling Tag at the centroid of each ceiling after applying the heatmap.",
                                Default=s.PlaceTags },
                        }
                    },
                    new LemoineSettingsGroup
                    {
                        Id = "G3", Title = "Diagnostics",
                        Settings = new List<LemoineSettingDef>
                        {
                            new LemoineSettingDef { Id="diagInfo", Kind="info",
                                Label="Run diagnostics",
                                Options=new InfoOpts { Text =
                                    "To run crop-box and linked ceiling diagnostics, use the " +
                                    "'Diagnostics' button in the Developer panel on the Revit ribbon. " +
                                    "Results open in Notepad." } },
                        }
                    }
                }
            };
        }

        public void ApplySettings(string groupId, string settingId, object value)
        {
            var s = CeilingHeatmapSettings.Instance;
            if (groupId == "G1")
            {
                if (settingId == "colorLow")  s.ColorLow  = value as string ?? s.ColorLow;
                if (settingId == "colorMid")  s.ColorMid  = value as string ?? s.ColorMid;
                if (settingId == "colorHigh") s.ColorHigh = value as string ?? s.ColorHigh;
            }
            else if (groupId == "G2")
            {
                if (settingId == "elevTolerance" && value is double tol)
                    s.ElevTolerance = tol / 12.0;
                if (settingId == "includeLinks" && value is bool links)
                    s.IncludeLinks = links;
                if (settingId == "placeTags" && value is bool tags)
                    s.PlaceTags = tags;
            }
            s.Save();
        }

        private static void ParseRGB(string hex, byte defR, byte defG, byte defB,
            out byte r, out byte g, out byte b)
        {
            if (TryParseHex(hex, out r, out g, out b)) return;
            r = defR; g = defG; b = defB;
        }

        private static bool TryParseHex(string hex, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            hex = hex.TrimStart('#');
            if (hex.Length == 6
                && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int v))
            {
                r = (byte)((v >> 16) & 0xFF);
                g = (byte)((v >>  8) & 0xFF);
                b = (byte)( v        & 0xFF);
                return true;
            }
            return false;
        }

        private static System.Windows.Media.Color ParseMediaColor(string hex, byte defR, byte defG, byte defB)
        {
            ParseRGB(hex, defR, defG, defB, out byte r, out byte g, out byte b);
            return System.Windows.Media.Color.FromRgb(r, g, b);
        }

        private static System.Windows.Media.Color HexToMediaColor(string hex)
        {
            ParseRGB(hex, 0, 0, 0, out byte r, out byte g, out byte b);
            return System.Windows.Media.Color.FromRgb(r, g, b);
        }

        internal static Autodesk.Revit.DB.Color ParseRevitColor(string hex, byte defR, byte defG, byte defB)
        {
            ParseRGB(hex, defR, defG, defB, out byte r, out byte g, out byte b);
            return new Autodesk.Revit.DB.Color(r, g, b);
        }
    }
}
