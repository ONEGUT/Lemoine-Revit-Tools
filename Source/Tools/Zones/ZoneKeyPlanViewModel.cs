using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;
using LemoineTools.Framework.Zones;

// Autodesk.Revit.UI also defines ComboBox, so the WPF one is aliased (CLAUDE.md).
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfGrid     = System.Windows.Controls.Grid;

namespace LemoineTools.Tools.Zones
{
    /// <summary>
    /// Key Plans from Zones — one locator legend per area, with the building outline from slab
    /// edges and the area highlighted.
    ///
    /// A legend is required as a seed because Revit exposes no way to create one; this tool
    /// duplicates it, exactly as the Scope Box Creator duplicates a seed scope box.
    /// </summary>
    public sealed class ZoneKeyPlanViewModel : IStepFlowTool, IStepAware, IReviewableTool, IRunResult, IToolCleanup
    {
        public string? ResultNoun => "key plans";
        public IReadOnlyList<ResultChip>? ResultChips => null;

        public string Title    => AppStrings.T("zones.keyplan.title");
        public string RunLabel => AppStrings.T("zones.keyplan.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", AppStrings.T("zones.keyplan.steps.S1"), required: true),
            new StepDefinition("S2", AppStrings.T("zones.keyplan.steps.S2"), required: true),
            new StepDefinition("S3", AppStrings.T("zones.keyplan.steps.S3"), required: true),
            new StepDefinition("S4", AppStrings.T("zones.keyplan.steps.S4"), required: false),
        };

        public sealed class NamedId
        {
            public ElementId Id { get; set; } = ElementId.InvalidElementId;
            public string Name { get; set; } = "";
        }

        private readonly ZoneKeyPlanRunHandler? _handler;
        private readonly ExternalEvent?         _event;
        private readonly List<NamedId> _legends;
        private readonly List<NamedId> _links;
        private readonly List<string>  _fillPatterns;

        private ElementId _seedId  = ElementId.InvalidElementId;
        private ElementId _linkId  = ElementId.InvalidElementId;
        private string    _levelId = "";
        private readonly HashSet<string> _areaIds = new HashSet<string>(StringComparer.Ordinal);

        // ── Graphics, one override for the whole run ─────────────────────────
        /// <summary>Empty means the solid fill, which is the default.</summary>
        private string _fillPattern = "";
        private string _fillHex     = ZoneKeyPlanRunHandler.DefaultFillHex;
        private int    _scale       = 96;

        private bool _trimToOutline    = true;
        private bool _useMatchlines;
        private ElementId _matchlineLinkId = ElementId.InvalidElementId;
        private bool _showMatchlines   = true;
        private bool _trimToMatchlines = true;

        private Action<string>? _refreshStep;

        public event EventHandler? ValidationChanged;
        private void Fire() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        private ZoneLibrary Lib => ZoneSettings.Instance.Library;

        public ZoneKeyPlanViewModel(ZoneKeyPlanRunHandler? handler, ExternalEvent? externalEvent,
                                    List<NamedId>? legends, List<NamedId>? links,
                                    List<string>? fillPatterns = null)
        {
            _handler      = handler;
            _event        = externalEvent;
            _legends      = legends      ?? new List<NamedId>();
            _links        = links        ?? new List<NamedId>();
            _fillPatterns = fillPatterns ?? new List<string>();
            if (_legends.Count == 1) _seedId = _legends[0].Id;
        }

        public void SetContentRefreshCallback(Action<string> refresh) => _refreshStep = refresh;
        public void OnStepActivated(string stepId)
        {
            // S4 summarises every earlier step, and S2's matchline rows depend on the source
            // picked in S1 — step content is built eagerly, so both must rebuild here.
            if (stepId == "S2" || stepId == "S4") _refreshStep?.Invoke(stepId);
        }

        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.PushLog    = null;
            _handler.OnProgress = null;
            _handler.OnComplete = null;
        }

        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildSourceStep();
                case "S2": return BuildGraphicsStep();
                case "S3": return BuildAreaStep();
                case "S4": return BuildReviewStep();
                default:   return null;
            }
        }

        private FrameworkElement BuildSourceStep()
        {
            var panel = new StackPanel();

            panel.Children.Add(Note(AppStrings.T("zones.keyplan.seedHint")));
            if (_legends.Count == 0)
            {
                panel.Children.Add(Warn(AppStrings.T("zones.keyplan.noLegends")));
            }
            else
            {
                var combo = Combo(_legends.Select(l => l.Name).ToList(),
                                  _legends.FirstOrDefault(l => l.Id == _seedId)?.Name ?? "",
                                  v => { _seedId = _legends.FirstOrDefault(l => l.Name == v)?.Id ?? ElementId.InvalidElementId; Fire(); });
                panel.Children.Add(combo);
            }

            panel.Children.Add(Note(AppStrings.T("zones.keyplan.sourceHint")));
            var linkNames = new List<string> { AppStrings.T("zones.keyplan.hostDoc") };
            linkNames.AddRange(_links.Select(l => l.Name));
            panel.Children.Add(Combo(linkNames, linkNames[0], v =>
            {
                _linkId = _links.FirstOrDefault(l => l.Name == v)?.Id ?? ElementId.InvalidElementId;
                Fire();
            }));

            panel.Children.Add(Note(AppStrings.T("zones.keyplan.levelHint")));
            if (Lib.Levels.Count == 0)
            {
                panel.Children.Add(Warn(AppStrings.T("zones.keyplan.noLevels")));
            }
            else
            {
                var levels = Lib.Levels.OrderBy(l => l.SortIndex).ThenBy(l => l.ElevationFt).ToList();
                if (string.IsNullOrEmpty(_levelId)) _levelId = levels[0].Id;
                panel.Children.Add(Combo(levels.Select(l => l.Name).ToList(),
                                         levels.First(l => l.Id == _levelId).Name,
                                         v => { _levelId = levels.FirstOrDefault(l => l.Name == v)?.Id ?? ""; Fire(); }));
            }

            // The scale is what decides whether the key plan fits the corner of the sheet it
            // is for, so it is a choice here rather than whatever the seed legend happened to be.
            panel.Children.Add(Note(AppStrings.T("zones.keyplan.scaleHint")));
            var scaleLabels = ZoneScaleFit.DefaultLadder.Select(ZoneScaleFit.Label).ToList();
            panel.Children.Add(Combo(scaleLabels, ZoneScaleFit.Label(_scale), v =>
            {
                int match = ZoneScaleFit.DefaultLadder.FirstOrDefault(d => ZoneScaleFit.Label(d) == v);
                if (match > 0) _scale = match;
                Fire();
            }));

            return panel;
        }

        // ── S2 — Graphics ─────────────────────────────────────────────────────

        /// <summary>
        /// The fill pattern, colour and trimming. ONE setting for the whole run, written onto a
        /// filled region type the tool owns, so every key plan it makes looks the same and no
        /// project type is restyled behind the user's back.
        /// </summary>
        private FrameworkElement BuildGraphicsStep()
        {
            var panel = new StackPanel();

            panel.Children.Add(Note(AppStrings.T("zones.keyplan.fillHint")));

            // Pattern. The blank entry IS the solid fill, which is the default and the common
            // answer — it is named rather than left as an empty row.
            var patternLabels = new List<string> { AppStrings.T("zones.keyplan.fillSolid") };
            patternLabels.AddRange(_fillPatterns);
            string currentPattern = string.IsNullOrEmpty(_fillPattern)
                ? patternLabels[0]
                : (_fillPatterns.Contains(_fillPattern) ? _fillPattern : patternLabels[0]);

            panel.Children.Add(LabelledRow(
                AppStrings.T("zones.keyplan.fillPattern"),
                Combo(patternLabels, currentPattern, v =>
                {
                    _fillPattern = string.Equals(v, patternLabels[0], StringComparison.Ordinal) ? "" : v;
                    Fire();
                })));

            // Colour, via the house swatch picker.
            panel.Children.Add(LabelledRow(
                AppStrings.T("zones.keyplan.fillColour"),
                ColorPickerWindow.BuildColorPickerSwatch(
                    getHex: () => _fillHex,
                    setHex: h => { _fillHex = h; Fire(); })));

            if (_fillPatterns.Count == 0)
                panel.Children.Add(Note(AppStrings.T("zones.keyplan.noPatterns")));

            // ── Trimming ─────────────────────────────────────────────────────
            panel.Children.Add(Note(AppStrings.T("zones.keyplan.trimHint")));
            panel.Children.Add(Toggle(AppStrings.T("zones.keyplan.trimToOutline"), _trimToOutline,
                v => { _trimToOutline = v; Fire(); }));

            panel.Children.Add(Toggle(AppStrings.T("zones.keyplan.useMatchlines"), _useMatchlines,
                v => { _useMatchlines = v; Fire(); _refreshStep?.Invoke("S2"); }));

            if (_useMatchlines)
            {
                var mlNames = new List<string> { AppStrings.T("zones.keyplan.hostDoc") };
                mlNames.AddRange(_links.Select(l => l.Name));
                string currentMl = _links.FirstOrDefault(l => l.Id == _matchlineLinkId)?.Name ?? mlNames[0];

                panel.Children.Add(LabelledRow(
                    AppStrings.T("zones.keyplan.matchlineSource"),
                    Combo(mlNames, currentMl, v =>
                    {
                        _matchlineLinkId = _links.FirstOrDefault(l => l.Name == v)?.Id ?? ElementId.InvalidElementId;
                        Fire();
                    })));

                panel.Children.Add(Toggle(AppStrings.T("zones.keyplan.showMatchlines"), _showMatchlines,
                    v => { _showMatchlines = v; Fire(); }));
                panel.Children.Add(Toggle(AppStrings.T("zones.keyplan.trimToMatchlines"), _trimToMatchlines,
                    v => { _trimToMatchlines = v; Fire(); }));
                panel.Children.Add(Note(AppStrings.T("zones.keyplan.matchlineLimit")));
            }

            return panel;
        }

        private FrameworkElement BuildAreaStep()
        {
            var panel = new StackPanel();
            var areas = Lib.Areas.OrderBy(a => a.SortIndex)
                                 .ThenBy(a => a.Name, NaturalOrderComparer.OrdinalIgnoreCase).ToList();
            if (areas.Count == 0) { panel.Children.Add(Warn(AppStrings.T("zones.keyplan.noAreas"))); return panel; }

            foreach (var a in areas)
            {
                var cb = new CheckBox
                {
                    Content = a.HasExtents ? a.Name : $"{a.Name}   ({AppStrings.T("zones.picker.noExtents")})",
                    IsChecked = _areaIds.Contains(a.Id),
                    Margin = new Thickness(0, 3, 0, 3),
                };
                cb.SetResourceReference(CheckBox.FontSizeProperty, "LemoineFS_SM");
                string id = a.Id;
                cb.Click += (s, e) =>
                {
                    if (cb.IsChecked == true) _areaIds.Add(id); else _areaIds.Remove(id);
                    Fire();
                };
                panel.Children.Add(cb);
            }
            return panel;
        }

        private FrameworkElement BuildReviewStep()
        {
            var panel = new StackPanel();
            panel.Children.Add(Note(AppStrings.T("zones.keyplan.plannedCount", _areaIds.Count)));

            var noExtents = _areaIds.Select(id => Lib.Area(id))
                                    .Where(a => a != null && !a.HasExtents)
                                    .Select(a => a!.Name).ToList();
            if (noExtents.Count > 0)
                panel.Children.Add(Warn(AppStrings.T("zones.keyplan.noExtentsWarn", string.Join(", ", noExtents))));

            return panel;
        }

        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _seedId != ElementId.InvalidElementId && !string.IsNullOrEmpty(_levelId);
                // Graphics always has an answer — solid grey at the seed's scale is a valid run.
                case "S2": return true;
                case "S3": return _areaIds.Count > 0;
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1":
                    return _seedId == ElementId.InvalidElementId
                        ? AppStrings.T("zones.keyplan.noSeedPicked")
                        : AppStrings.T("zones.keyplan.summary.source",
                                       _legends.FirstOrDefault(l => l.Id == _seedId)?.Name ?? "",
                                       Lib.Level(_levelId)?.Name ?? "",
                                       ZoneScaleFit.Label(_scale));
                case "S2":
                {
                    string pattern = string.IsNullOrEmpty(_fillPattern)
                        ? AppStrings.T("zones.keyplan.fillSolid")
                        : _fillPattern;
                    var parts = new List<string> { $"{pattern} {_fillHex}" };
                    if (_trimToOutline)   parts.Add(AppStrings.T("zones.keyplan.summary.trimOutline"));
                    if (_useMatchlines)   parts.Add(AppStrings.T("zones.keyplan.summary.matchlines"));
                    return string.Join(" · ", parts);
                }
                case "S3": return AppStrings.T("zones.keyplan.summary.areas", _areaIds.Count);
                default:   return AppStrings.T("zones.keyplan.plannedCount", _areaIds.Count);
            }
        }

        public IList<(string id, string label)> ReviewItems => new List<(string, string)>
        {
            ("source",   AppStrings.T("zones.keyplan.steps.S1")),
            ("graphics", AppStrings.T("zones.keyplan.steps.S2")),
            ("areas",    AppStrings.T("zones.keyplan.steps.S3")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["source"]   = SummaryFor("S1"),
            ["graphics"] = SummaryFor("S2"),
            ["areas"]    = SummaryFor("S3"),
        };

        public IList<string>? ReviewChips => null;
        public string? ReviewNote => AppStrings.T("zones.keyplan.reviewNote");

        /// <summary>An area with no extents produces a key plan with nothing highlighted.</summary>
        public string? ReviewWarning
        {
            get
            {
                var noExtents = _areaIds.Select(id => Lib.Area(id))
                                        .Where(a => a != null && !a.HasExtents)
                                        .Select(a => a!.Name).ToList();
                return noExtents.Count > 0
                    ? AppStrings.T("zones.keyplan.noExtentsWarn", string.Join(", ", noExtents))
                    : null;
            }
        }

        public void Run(Action<string, string> pushLog,
                        Action<int, int, int, int> onProgress,
                        Action<int, int, int> onComplete)
        {
            if (_handler == null || _event == null)
            {
                pushLog(AppStrings.T("zones.keyplan.noHandler"), "fail");
                onComplete(0, 1, 0);
                return;
            }

            _handler.SeedLegendId   = _seedId;
            _handler.SourceLinkId   = _linkId;
            _handler.OutlineLevelId = _levelId;
            _handler.AreaIds        = _areaIds.ToList();

            _handler.FillPatternName  = _fillPattern;
            _handler.FillColorHex     = _fillHex;
            _handler.LegendScale      = _scale;
            _handler.TrimToOutline    = _trimToOutline;
            _handler.UseMatchlines    = _useMatchlines;
            _handler.MatchlineLinkId  = _matchlineLinkId;
            _handler.ShowMatchlines   = _showMatchlines;
            _handler.TrimToMatchlines = _trimToMatchlines;

            _handler.PushLog        = pushLog;
            _handler.OnProgress     = onProgress;
            _handler.OnComplete     = onComplete;
            _event.Raise();
        }

        private static WpfComboBox Combo(List<string> options, string current, Action<string> onChange)
        {
            var c = new WpfComboBox
            {
                Margin = new Thickness(0, 2, 0, 8),
                MaxWidth = 320,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            c.SetResourceReference(WpfComboBox.FontSizeProperty, "LemoineFS_SM");
            foreach (var o in options) c.Items.Add(o);
            c.SelectedItem = options.Contains(current) ? current : options.FirstOrDefault();
            c.SelectionChanged += (s, e) =>
            {
                try { onChange(c.SelectedItem as string ?? ""); }
                catch (Exception ex) { DiagnosticsLog.Error("ZoneKeyPlan: choice change", ex); }
            };
            return c;
        }

        /// <summary>A fixed-width label beside a control, so the graphics rows line up.</summary>
        private static FrameworkElement LabelledRow(string label, FrameworkElement control)
        {
            var g = new WpfGrid { Margin = new Thickness(0, 2, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var t = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            WpfGrid.SetColumn(t, 0);
            g.Children.Add(t);

            control.HorizontalAlignment = HorizontalAlignment.Left;
            control.VerticalAlignment   = VerticalAlignment.Center;
            control.Margin              = new Thickness(0);
            WpfGrid.SetColumn(control, 1);
            g.Children.Add(control);
            return g;
        }

        private static CheckBox Toggle(string label, bool value, Action<bool> onChange)
        {
            var cb = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 3, 0, 3) };
            cb.SetResourceReference(CheckBox.FontSizeProperty, "LemoineFS_SM");
            cb.Click += (s, e) =>
            {
                try { onChange(cb.IsChecked == true); }
                catch (Exception ex) { DiagnosticsLog.Error("ZoneKeyPlan: toggle", ex); }
            };
            return cb;
        }

        private static TextBlock Note(string text)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 4, 0, 4) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return t;
        }

        private static TextBlock Warn(string text)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return t;
        }
    }
}
