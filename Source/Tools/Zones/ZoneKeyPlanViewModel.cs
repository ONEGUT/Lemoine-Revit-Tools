using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Zones;

// Autodesk.Revit.UI also defines ComboBox, so the WPF one is aliased (CLAUDE.md).
using WpfComboBox = System.Windows.Controls.ComboBox;

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
            new StepDefinition("S3", AppStrings.T("zones.keyplan.steps.S3"), required: false),
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

        private ElementId _seedId  = ElementId.InvalidElementId;
        private ElementId _linkId  = ElementId.InvalidElementId;
        private string    _levelId = "";
        private readonly HashSet<string> _areaIds = new HashSet<string>(StringComparer.Ordinal);

        private Action<string>? _refreshStep;

        public event EventHandler? ValidationChanged;
        private void Fire() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        private ZoneLibrary Lib => ZoneSettings.Instance.Library;

        public ZoneKeyPlanViewModel(ZoneKeyPlanRunHandler? handler, ExternalEvent? externalEvent,
                                    List<NamedId>? legends, List<NamedId>? links)
        {
            _handler = handler;
            _event   = externalEvent;
            _legends = legends ?? new List<NamedId>();
            _links   = links   ?? new List<NamedId>();
            if (_legends.Count == 1) _seedId = _legends[0].Id;
        }

        public void SetContentRefreshCallback(Action<string> refresh) => _refreshStep = refresh;
        public void OnStepActivated(string stepId) { if (stepId == "S3") _refreshStep?.Invoke("S3"); }

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
                case "S2": return BuildAreaStep();
                case "S3": return BuildReviewStep();
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
                case "S2": return _areaIds.Count > 0;
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
                                       Lib.Level(_levelId)?.Name ?? "");
                case "S2": return AppStrings.T("zones.keyplan.summary.areas", _areaIds.Count);
                default:   return AppStrings.T("zones.keyplan.plannedCount", _areaIds.Count);
            }
        }

        public IList<(string id, string label)> ReviewItems => new List<(string, string)>
        {
            ("source", AppStrings.T("zones.keyplan.steps.S1")),
            ("areas",  AppStrings.T("zones.keyplan.steps.S2")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["source"] = SummaryFor("S1"),
            ["areas"]  = SummaryFor("S2"),
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
