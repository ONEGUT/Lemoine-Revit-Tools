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
    public class LinkViewsDisciplineViewModel : ILemoineTool, ILemoineToolSettings, ILemoineReviewable
    {
        // ── Identity ──────────────────────────────────────────────────
        public string Title    => "Link Views — Discipline";
        public string RunLabel => "Create Views in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Assign Disciplines", required: true),
            new StepDefinition("S2", "Review & Run",       required: false),
        };

        // ── Data types passed in from Command ─────────────────────────
        public sealed class LinkEntry
        {
            public ElementId LinkInstId { get; set; } = null!;
            public string    Name       { get; set; } = null!;
        }

        public sealed class ViewTemplateEntry
        {
            public ElementId Id   { get; set; }
            public string    Name { get; set; }
        }

        // ── State ──────────────────────────────────────────────────────
        private readonly List<LinkEntry> _links;
        private readonly Dictionary<long, string> _assignments;
        private string   _subDisc     = "";
        private ElementId _template3DId = ElementId.InvalidElementId;

        // ── Available view templates ───────────────────────────────────
        private readonly List<ViewTemplateEntry> _templates3D;

        // ── ExternalEvent wiring ───────────────────────────────────────
        private readonly LinkViewsDisciplineRunHandler _runHandler;
        private readonly Autodesk.Revit.UI.ExternalEvent _runEvent;

        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public LinkViewsDisciplineViewModel(
            LinkViewsDisciplineRunHandler runHandler,
            Autodesk.Revit.UI.ExternalEvent runEvent,
            List<LinkEntry> links,
            List<ViewTemplateEntry>? templates3D = null)
        {
            _runHandler  = runHandler;
            _runEvent    = runEvent;
            _links       = links ?? new List<LinkEntry>();
            _assignments = new Dictionary<long, string>();
            _templates3D = templates3D ?? new List<ViewTemplateEntry>();
            foreach (var l in _links)
                _assignments[l.LinkInstId.Value] = "SKIP";
        }

        // ═══════════════════════════════════════════════════════════════
        // GetStepContent
        // ═══════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "S1") return BuildRevit2025Notice();
            if (stepId == "S2") return null; // framework renders review (ILemoineReviewable)
            return null;
        }

        // ── Revit 2025 gate ────────────────────────────────────────────
        // This tool currently only works on Revit 2025. Show a notice on the
        // first step and keep its Continue button disabled (IsValid("S1") => false).
        private FrameworkElement BuildRevit2025Notice()
        {
            var notice = new TextBlock
            {
                Text         = "This tool only works on Revit 2025.",
                TextWrapping = TextWrapping.Wrap,
                FontWeight   = FontWeights.SemiBold,
                Margin       = new Thickness(0, 4, 0, 0),
            };
            notice.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            notice.SetResourceReference(TextBlock.ForegroundProperty, "LemoineWarning");
            notice.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return notice;
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

            // ── VIEW OPTIONS: Sub Discipline + View Template ───────────
            wrapper.Children.Add(new FrameworkElement { Height = 10 });
            var viewOptionsHeader = new TextBlock { Text = "VIEW OPTIONS",
                                                    Margin = new Thickness(0, 0, 0, 4) };
            viewOptionsHeader.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            viewOptionsHeader.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            viewOptionsHeader.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            wrapper.Children.Add(viewOptionsHeader);

            var colHeader = new StackPanel { Orientation = Orientation.Horizontal,
                                              Margin = new Thickness(40, 0, 0, 4) };
            var colSubDisc = new TextBlock { Text = "Sub Discipline", Width = 120 };
            colSubDisc.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            colSubDisc.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            colSubDisc.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            var colTemplate = new TextBlock { Text = "View Template", Width = 150,
                                              Margin = new Thickness(8, 0, 0, 0) };
            colTemplate.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            colTemplate.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            colTemplate.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            colHeader.Children.Add(colSubDisc);
            colHeader.Children.Add(colTemplate);
            wrapper.Children.Add(colHeader);

            wrapper.Children.Add(BuildViewTypeRow());

            return wrapper;
        }

        private FrameworkElement BuildViewTypeRow()
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 0, 0, 4),
            };

            var lbl = new TextBlock
            {
                Text              = "3D",
                Width             = 40,
                VerticalAlignment = VerticalAlignment.Center,
            };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            row.Children.Add(lbl);

            var tb = new WpfTextBox { Text = _subDisc, Width = 120 };
            tb.SetResourceReference(FrameworkElement.HeightProperty,                 "LemoineH_Input");
            tb.SetResourceReference(System.Windows.Controls.Control.PaddingProperty, "LemoineTh_InputPad");
            tb.SetResourceReference(WpfTextBox.ForegroundProperty,  "LemoineText");
            tb.SetResourceReference(WpfTextBox.BackgroundProperty,  "LemoineSelectBg");
            tb.SetResourceReference(WpfTextBox.FontSizeProperty,    "LemoineFS_SM");
            tb.SetResourceReference(WpfTextBox.FontFamilyProperty,  "LemoineMonoFont");
            tb.SetResourceReference(WpfTextBox.BorderBrushProperty, "LemoineBorder");
            tb.TextChanged += (s, e) => _subDisc = tb.Text;
            row.Children.Add(tb);

            row.Children.Add(new FrameworkElement { Width = 8 });

            var templateNames = new List<string>(new[] { "(none)" }
                .Concat(_templates3D.Select(t => t.Name)));
            string selectedName = _templates3D.FirstOrDefault(
                t => t.Id != null && t.Id.Value == _template3DId.Value)?.Name ?? "(none)";

            var templateSelect = new LemoineSingleSelect
            {
                Width        = 150,
                Items        = templateNames,
                SelectedItem = selectedName,
            };
            templateSelect.SelectionChanged += name =>
            {
                var entry = _templates3D.FirstOrDefault(t => t.Name == name);
                _template3DId = entry?.Id ?? ElementId.InvalidElementId;
            };
            row.Children.Add(templateSelect);

            return row;
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
        // ── ILemoineReviewable (P3) — framework renders the review step ───────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("process",  "Links to Process"),
            ("skipped",  "Links Skipped"),
            ("combined", "Combined Views"),
            ("perlink",  "Per-Link Views"),
        };

        public IDictionary<string, string> ReviewValues
        {
            get
            {
                int process = _assignments.Values.Count(v => v != "SKIP");
                var combined = LinkViewsDisciplineSettings.Instance.CombinedDisciplines;
                int combinedCount = _assignments.Values
                    .Where(v => v != "SKIP")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(v => combined.Contains(v));
                return new Dictionary<string, string>
                {
                    ["process"]  = process > 0 ? $"{process} link(s)" : "—",
                    ["skipped"]  = $"{_assignments.Values.Count(v => v == "SKIP")}",
                    ["combined"] = combinedCount > 0 ? $"{combinedCount} discipline(s)" : "None",
                    ["perlink"]  = $"{process}",
                };
            }
        }

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => "Creates one 3D view per link with a tight section box and other " +
            "links hidden. Disciplines configured for combined views also get a merged view covering all links in " +
            "that discipline.";
        public string?        ReviewWarning => null;

        // ═══════════════════════════════════════════════════════════════
        // IsValid / SummaryFor / Run
        // ═══════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            // Revit 2025 gate: keep the first step's Continue button disabled.
            if (stepId == "S1") return false;
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
            _runHandler.Template3D  = _template3DId;
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
