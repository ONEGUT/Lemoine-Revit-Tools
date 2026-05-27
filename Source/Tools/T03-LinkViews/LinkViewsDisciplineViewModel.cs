using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

using WpfGrid    = System.Windows.Controls.Grid;
using WpfPoint   = System.Windows.Point;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace LemoineTools.Tools.LinkViews
{
    public class LinkViewsDisciplineViewModel : ILemoineTool, ILemoineToolSettings
    {
        // ── Identity ──────────────────────────────────────────────────
        public string Title    => "Link Views — Discipline";
        public string RunLabel => "Create Views in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Assign Disciplines", required: true),
            new StepDefinition("S2", "Review & Run",       required: false),
        };

        // ── Data type passed in from Command ──────────────────────────
        public sealed class LinkEntry
        {
            public ElementId LinkInstId { get; set; } = null!;
            public string    Name       { get; set; } = null!;
        }

        // ── State ──────────────────────────────────────────────────────
        private readonly List<LinkEntry> _links;
        private readonly Dictionary<long, string> _assignments;
        private string _subDisc = "";

        // ── ExternalEvent wiring ───────────────────────────────────────
        private readonly LinkViewsDisciplineRunHandler _runHandler;
        private readonly Autodesk.Revit.UI.ExternalEvent _runEvent;

        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public LinkViewsDisciplineViewModel(
            LinkViewsDisciplineRunHandler runHandler,
            Autodesk.Revit.UI.ExternalEvent runEvent,
            List<LinkEntry> links)
        {
            _runHandler  = runHandler;
            _runEvent    = runEvent;
            _links       = links ?? new List<LinkEntry>();
            _assignments = new Dictionary<long, string>();
            foreach (var l in _links)
                _assignments[l.LinkInstId.Value] = "SKIP";
        }

        // ═══════════════════════════════════════════════════════════════
        // GetStepContent
        // ═══════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "S1") return BuildS1();
            if (stepId == "S2") return BuildReview();
            return null;
        }

        // ── S1: Discipline Assignment ──────────────────────────────────
        private FrameworkElement BuildS1()
        {
            if (_links.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text         = "No loaded Revit link instances found in the current project.",
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle    = FontStyles.Italic,
                    Margin       = new Thickness(0, 4, 0, 0),
                };
                empty.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                empty.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                empty.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                return empty;
            }

            // Live container — rebuilds when custom disciplines change via settings
            string lastDiscKey = null;
            var host = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };

            void Rebuild()
            {
                string discKey = string.Join(",", LinkViewsDisciplineSettings.Instance.AllDisciplines);
                if (discKey == lastDiscKey) return;
                lastDiscKey = discKey;
                host.Content = BuildChipGrid(LinkViewsDisciplineSettings.Instance.AllDisciplines);
            }

            Rebuild();
            ValidationChanged += (_, __) => Rebuild();
            return host;
        }

        private StackPanel BuildChipGrid(List<string> disciplines)
        {
            var sv = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 420,
            };
            var sp = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
            foreach (var link in _links)
                sp.Children.Add(BuildLinkRow(link, disciplines));
            sv.Content = sp;

            var wrapper = new StackPanel();
            wrapper.Children.Add(sv);

            // ── Sub Discipline input ───────────────────────────────────
            wrapper.Children.Add(new FrameworkElement { Height = 10 });
            var subDiscHeader = new TextBlock { Text = "SUB DISCIPLINE",
                                                Margin = new Thickness(0, 0, 0, 6) };
            subDiscHeader.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            subDiscHeader.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            subDiscHeader.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            wrapper.Children.Add(subDiscHeader);

            var tb = new WpfTextBox { Text = _subDisc, Width = 200 };
            tb.SetResourceReference(FrameworkElement.HeightProperty,                 "LemoineH_Input");
            tb.SetResourceReference(System.Windows.Controls.Control.PaddingProperty, "LemoineTh_InputPad");
            tb.SetResourceReference(WpfTextBox.ForegroundProperty,  "LemoineText");
            tb.SetResourceReference(WpfTextBox.BackgroundProperty,  "LemoineSelectBg");
            tb.SetResourceReference(WpfTextBox.FontSizeProperty,    "LemoineFS_SM");
            tb.SetResourceReference(WpfTextBox.FontFamilyProperty,  "LemoineMonoFont");
            tb.SetResourceReference(WpfTextBox.BorderBrushProperty, "LemoineBorder");
            tb.TextChanged += (s, e) => _subDisc = tb.Text;
            wrapper.Children.Add(tb);

            return wrapper;
        }

        private UIElement BuildLinkRow(LinkEntry link, List<string> disciplines)
        {
            long linkVal     = link.LinkInstId.Value;
            var  chipBorders = new Dictionary<string, Border>(StringComparer.Ordinal);

            if (!_assignments.ContainsKey(linkVal))
                _assignments[linkVal] = "SKIP";

            void UpdateChips()
            {
                string current = _assignments.TryGetValue(linkVal, out var d) ? d : "SKIP";
                foreach (var kv in chipBorders)
                {
                    bool active = kv.Key == current;
                    var  chip   = kv.Value;
                    var  tb     = chip.Child as TextBlock;
                    if (active)
                    {
                        chip.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                        chip.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                        if (tb != null) tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
                    }
                    else
                    {
                        chip.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                        chip.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                        if (tb != null) tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                    }
                }
            }

            var chipPanel = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (string d in disciplines)
            {
                string disc   = d;
                double chipW  = Math.Max(46, disc.Length * 7.5 + 16);
                var    tb     = new TextBlock { Text = disc, TextAlignment = TextAlignment.Center,
                                               Margin = new Thickness(0, 3, 0, 3) };
                tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

                var chip = new Border
                {
                    Child = tb, Width = chipW,
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(3),
                    Margin          = new Thickness(0, 0, 4, 0),
                    Cursor          = Cursors.Hand,
                };
                chip.MouseLeftButtonDown += (s, e) =>
                {
                    _assignments[linkVal] = disc;
                    UpdateChips();
                    OnValidationChanged();
                };
                chipBorders[disc] = chip;
                chipPanel.Children.Add(chip);
            }

            UpdateChips();

            var nameTb = new TextBlock
            {
                Text              = link.Name,
                TextTrimming      = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 8, 0),
            };
            nameTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            nameTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            nameTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
            DockPanel.SetDock(chipPanel, Dock.Right);
            row.Children.Add(chipPanel);
            row.Children.Add(nameTb);
            return row;
        }

        // ── S2: Review & Run ───────────────────────────────────────────
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

            var cards = new[]
            {
                new CardDef("Links to Process",
                    () => { int n = _assignments.Values.Count(v => v != "SKIP");
                            return n > 0 ? $"{n} link(s)" : "—"; }, 0, 0),
                new CardDef("Links Skipped",
                    () => $"{_assignments.Values.Count(v => v == "SKIP")}", 0, 1),
                new CardDef("Combined Views",
                    () =>
                    {
                        var combined = LinkViewsDisciplineSettings.Instance.CombinedDisciplines;
                        int count    = _assignments.Values
                            .Where(v => v != "SKIP")
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count(v => combined.Contains(v));
                        return count > 0 ? $"{count} discipline(s)" : "None";
                    }, 1, 0),
                new CardDef("Per-Link Views",
                    () => $"{_assignments.Values.Count(v => v != "SKIP")}", 1, 1),
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
                sp.Children.Add(lbl); sp.Children.Add(valText);
                card.Child = sp;
                WpfGrid.SetRow(card, c.Row); WpfGrid.SetColumn(card, c.Col);
                grid.Children.Add(card);
            }

            outer.Children.Add(grid);

            var desc = new TextBlock
            {
                Text = "Creates one 3D view per link with a tight section box and other links hidden. " +
                       "Disciplines configured for combined views also get a merged view covering all " +
                       "links in that discipline.",
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

        // ═══════════════════════════════════════════════════════════════
        // IsValid / SummaryFor / Run
        // ═══════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _assignments.Values.Any(v => v != "SKIP");
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1")
            {
                int active = _assignments.Values.Count(v => v != "SKIP");
                return active > 0 ? $"{active} link(s) assigned" : "All links SKIP";
            }
            if (stepId == "S2") return "Ready to run";
            return "—";
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            var assignments = _links
                .Select(l => new DisciplineAssignment
                {
                    LinkInstId = l.LinkInstId,
                    LinkName   = l.Name,
                    Discipline = _assignments.TryGetValue(l.LinkInstId.Value, out var d) ? d : "SKIP",
                })
                .ToList();

            _runHandler.Assignments = assignments;
            _runHandler.SubDisc     = _subDisc;
            _runHandler.PushLog     = pushLog;
            _runHandler.OnProgress  = onProgress;
            _runHandler.OnComplete  = onComplete;

            pushLog("Raising Revit ExternalEvent…", "info");
            _runEvent.Raise();
        }

        // ═══════════════════════════════════════════════════════════════
        // ILemoineToolSettings — declarative spec
        // ═══════════════════════════════════════════════════════════════
        public LemoineToolSettingsSpec GetSettingsSpec()
        {
            var s = LinkViewsDisciplineSettings.Instance;
            var combined = s.CombinedDisciplines;
            var builtIn  = new[] { "ARCH", "MEP", "STRUCT", "OTHER" };

            var toggleItems = builtIn.Select(d => new ToggleItem
            {
                Id        = d,
                Label     = d,
                Desc      = $"Create a combined view merging all {d} links",
                DefaultOn = combined.Contains(d),
            }).ToList();

            var initState = builtIn.ToDictionary(d => d, d => combined.Contains(d));

            return new LemoineToolSettingsSpec
            {
                Id          = "T04b",
                Label       = "Link Views \u2014 Discipline",
                Icon        = "04",
                Description = "Combined view and custom discipline settings for Link Views Discipline.",
                Groups = new List<LemoineSettingsGroup>
                {
                    new LemoineSettingsGroup
                    {
                        Id = "G1", Title = "Combined Views", OpenByDefault = true,
                        Hint = "Selected disciplines generate a merged view covering all their links.",
                        Settings = new List<LemoineSettingDef>
                        {
                            new LemoineSettingDef { Id="combinedDiscs", Kind="toggles",
                                Label="Disciplines with combined views",
                                Options=new TogglesOpts { Items = toggleItems },
                                Default=initState },
                        }
                    },
                    new LemoineSettingsGroup
                    {
                        Id = "G2", Title = "Custom Disciplines",
                        Hint = "Additional discipline codes beyond ARCH, MEP, STRUCT, OTHER.",
                        Settings = new List<LemoineSettingDef>
                        {
                            new LemoineSettingDef { Id="customDiscs", Kind="text",
                                Label="Custom discipline codes",
                                Hint="Comma-separated codes, e.g. CIVIL,LANDSCAPE. SKIP is always appended last.",
                                Options=new TextOpts { Placeholder="e.g. CIVIL,LANDSCAPE", Mono=true },
                                Default=s.CustomDisciplinesRaw },
                        }
                    }
                }
            };
        }

        public void ApplySettings(string groupId, string settingId, object value)
        {
            var s = LinkViewsDisciplineSettings.Instance;
            if (groupId == "G1" && settingId == "combinedDiscs")
            {
                if (value is System.Collections.Generic.Dictionary<string, bool> toggles)
                {
                    var active = toggles.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
                    s.CombinedDisciplinesRaw = string.Join(",", active);
                }
            }
            else if (groupId == "G2" && settingId == "customDiscs")
            {
                s.CustomDisciplinesRaw = value as string ?? "";
            }
            s.Save();
            // Trigger live rebuild of the S1 discipline chip grid
            OnValidationChanged();
        }

    }
}
