using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;
using LemoineTools.Framework.Zones;

// Autodesk.Revit.UI also defines ComboBox, so the WPF one is aliased (CLAUDE.md).
using WpfComboBox = System.Windows.Controls.ComboBox;

namespace LemoineTools.Tools.Zones
{
    /// <summary>
    /// Create Views from Zones — pick zone cells, pick which of their levels' views to
    /// build, get views.
    ///
    /// This is the first consumer of the zone library, and the point at which a zone stops
    /// being a record and starts producing drawings.
    /// </summary>
    public sealed class ZoneViewsViewModel : IStepFlowTool, IStepAware, IReviewableTool, IRunResult, IToolCleanup
    {
        public string? ResultNoun => "views";
        public IReadOnlyList<ResultChip>? ResultChips => null;

        public string Title    => AppStrings.T("zones.views.title");
        public string RunLabel => AppStrings.T("zones.views.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", AppStrings.T("zones.views.steps.S1"), required: true),
            new StepDefinition("S2", AppStrings.T("zones.views.steps.S2"), required: true),
            new StepDefinition("S3", AppStrings.T("zones.views.steps.S3"), required: false),
        };

        private readonly ZoneViewsRunHandler? _handler;
        private readonly ExternalEvent?       _event;

        private readonly List<ZonePicker.Cell> _cells = new List<ZonePicker.Cell>();
        /// <summary>
        /// Which views to build, BY NAME. A view def belongs to a LEVEL, so "Floor Plan" is a
        /// different record with a different id on every level — selecting by id would build it
        /// on one level only.
        /// </summary>
        private readonly HashSet<string> _viewNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _sheetSetId = "";

        private ZonePicker? _picker;
        private Action<string>? _refreshStep;

        public event EventHandler? ValidationChanged;
        private void Fire() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        private ZoneLibrary Lib => ZoneSettings.Instance.Library;

        public ZoneViewsViewModel(ZoneViewsRunHandler? handler, ExternalEvent? externalEvent)
        {
            _handler = handler;
            _event   = externalEvent;
        }

        public void SetContentRefreshCallback(Action<string> refresh) => _refreshStep = refresh;

        public void OnStepActivated(string stepId)
        {
            // S3 summarises choices made on S1/S2, so it must rebuild on activation — step
            // content is built eagerly at construction and would otherwise render stale.
            // S2 lists the views defined on the levels chosen in S1, and S3 summarises both —
            // step content is built eagerly at construction, so both must rebuild here.
            if (stepId == "S2" || stepId == "S3") _refreshStep?.Invoke(stepId);
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
                case "S1": return BuildZoneStep();
                case "S2": return BuildViewStep();
                case "S3": return BuildSummaryStep();
                default:   return null;
            }
        }

        private FrameworkElement BuildZoneStep()
        {
            var panel = new StackPanel();

            if (Lib.IsEmpty)
            {
                panel.Children.Add(Note(AppStrings.T("zones.views.noLibrary")));
                return panel;
            }

            _picker = new ZonePicker { MaxHeight = 320 };
            // Subscribe BEFORE SetLibrary — that callback is the only thing that populates
            // the mirror list on initialisation (the MultiSelectTabs contract).
            _picker.SelectionChanged += cells =>
            {
                _cells.Clear();
                _cells.AddRange(cells);
                Fire();
            };
            _picker.SetLibrary(Lib);
            panel.Children.Add(_picker);

            return panel;
        }

        /// <summary>
        /// The views available across the levels selected on S1 — a level owns its view defs,
        /// so this is the union of their names rather than one global list. An area's per-field
        /// overrides do not change WHICH views exist, only what they contain.
        /// </summary>
        private List<string> AvailableViewNames()
        {
            var names = new List<string>();
            var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var levelId in _cells.Select(c => c.LevelId).Distinct(StringComparer.Ordinal))
            {
                var level = Lib.Level(levelId);
                if (level?.ViewDefs == null) continue;
                foreach (var v in level.ViewDefs.OrderBy(x => x.SortIndex))
                {
                    if (v == null || string.IsNullOrWhiteSpace(v.Name)) continue;
                    if (seen.Add(v.Name)) names.Add(v.Name);
                }
            }
            return names;
        }

        private FrameworkElement BuildViewStep()
        {
            var panel = new StackPanel();

            var available = AvailableViewNames();
            if (available.Count == 0)
            {
                // Stated explicitly: a level with no views defined produces nothing, and an
                // empty list here is otherwise indistinguishable from a broken lookup.
                panel.Children.Add(Note(AppStrings.T("zones.views.noViewsOnLevels")));
                return panel;
            }

            foreach (var name in available)
            {
                // The kinds this name resolves to across the chosen levels. Usually one; if a
                // level defines "Floor Plan" as a different kind, the row says so rather than
                // silently building two different things under one tick.
                var kinds = _cells.Select(c => Lib.Level(c.LevelId))
                    .Where(l => l?.ViewDefs != null)
                    .SelectMany(l => l!.ViewDefs)
                    .Where(v => v != null && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
                    .Select(v => v.Kind).Distinct(StringComparer.Ordinal).ToList();

                var cb = new CheckBox
                {
                    Content   = $"{name}  ({string.Join(" / ", kinds)})",
                    IsChecked = _viewNames.Contains(name),
                    Margin    = new Thickness(0, 3, 0, 3),
                };
                cb.SetResourceReference(CheckBox.FontSizeProperty, "LemoineFS_SM");
                string picked = name;
                cb.Click += (s, e) =>
                {
                    if (cb.IsChecked == true) _viewNames.Add(picked); else _viewNames.Remove(picked);
                    Fire();
                };
                panel.Children.Add(cb);
            }

            // Choosing a sheet size lets the run reuse the scale already solved into that
            // sheet set's placements, instead of falling back to the view def default.
            if (Lib.SheetSets.Count > 0)
            {
                panel.Children.Add(Note(AppStrings.T("zones.views.layoutHint")));

                var options = new List<string> { AppStrings.T("zones.views.noLayout") };
                options.AddRange(Lib.SheetSets.Select(l => l.Name));

                var combo = new WpfComboBox { Margin = new Thickness(0, 4, 0, 0), MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
                combo.SetResourceReference(WpfComboBox.FontSizeProperty, "LemoineFS_SM");
                foreach (var o in options) combo.Items.Add(o);
                combo.SelectedIndex = 0;
                combo.SelectionChanged += (s, e) =>
                {
                    string v = combo.SelectedItem as string ?? "";
                    var pick = Lib.SheetSets.FirstOrDefault(l => l.Name == v);
                    _sheetSetId = pick?.Id ?? "";
                    Fire();
                };
                panel.Children.Add(combo);
            }

            return panel;
        }

        /// <summary>
        /// The resolved view defs the run will actually build — every selected name, resolved
        /// per (level, area) so an area's overrides are reflected in the review exactly as the
        /// run will apply them.
        /// </summary>
        private List<ZoneViewDef> SelectedDefs()
        {
            var defs = new List<ZoneViewDef>();
            foreach (var c in _cells)
            {
                var level = Lib.Level(c.LevelId);
                var area  = Lib.Area(c.AreaId);
                if (level == null || area == null) continue;
                foreach (var d in Lib.ResolveViewDefs(level, area))
                    if (_viewNames.Contains(d.Name ?? "")) defs.Add(d);
            }
            return defs;
        }

        private FrameworkElement BuildSummaryStep()
        {
            var panel = new StackPanel();
            panel.Children.Add(Note(AppStrings.T("zones.views.plannedCount",
                                                 _cells.Count * Math.Max(_viewNames.Count, 0))));

            // Naming across several sheet sizes is the one way this run can fail wholesale,
            // so it is checked here rather than discovered at run time.
            var selected = SelectedDefs();
            if (!string.IsNullOrEmpty(_sheetSetId))
            {
                var risky = selected
                    .Where(r => !ZoneNamingTokens.VariesByLayout(r.NamePattern))
                    .Select(r => r.Name).ToList();
                if (risky.Count > 0)
                    panel.Children.Add(Warn(AppStrings.T("zones.views.patternWarn", string.Join(", ", risky))));
            }

            var noExtents = _cells
                .Select(c => Lib.Area(c.AreaId))
                .Where(a => a != null && !a.HasExtents)
                .Select(a => a!.Name).Distinct().ToList();
            if (noExtents.Count > 0)
                panel.Children.Add(Warn(AppStrings.T("zones.views.noExtentsWarn", string.Join(", ", noExtents))));

            return panel;
        }

        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _cells.Count > 0;
                case "S2": return _viewNames.Count > 0;
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return AppStrings.T("zones.views.summary.zones", _cells.Count);
                case "S2": return AppStrings.T("zones.views.summary.views", _viewNames.Count);
                default:   return AppStrings.T("zones.views.summary.planned",
                                               _cells.Count * Math.Max(_viewNames.Count, 0));
            }
        }

        public IList<(string id, string label)> ReviewItems => new List<(string, string)>
        {
            ("zones",   AppStrings.T("zones.views.steps.S1")),
            ("views",   AppStrings.T("zones.views.steps.S2")),
            ("planned", AppStrings.T("zones.views.steps.S3")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["zones"]   = SummaryFor("S1"),
            ["views"]   = SummaryFor("S2"),
            ["planned"] = SummaryFor("S3"),
        };

        public IList<string>? ReviewChips => null;
        public string? ReviewNote => AppStrings.T("zones.views.reviewNote");

        /// <summary>
        /// Generating for a sheet size with a name pattern that does not vary by layout is the
        /// one way this run fails wholesale, so it is the banner rather than a note.
        /// </summary>
        public string? ReviewWarning
        {
            get
            {
                if (string.IsNullOrEmpty(_sheetSetId)) return null;
                var risky = SelectedDefs()
                    .Where(r => !ZoneNamingTokens.VariesByLayout(r.NamePattern))
                    .Select(r => r.Name).Distinct().ToList();
                return risky.Count > 0
                    ? AppStrings.T("zones.views.patternWarn", string.Join(", ", risky))
                    : null;
            }
        }

        public void Run(Action<string, string> pushLog,
                        Action<int, int, int, int> onProgress,
                        Action<int, int, int> onComplete)
        {
            if (_handler == null || _event == null)
            {
                pushLog(AppStrings.T("zones.views.noHandler"), "fail");
                onComplete(0, 1, 0);
                return;
            }

            _handler.Cells = _cells
                .Select(c => new ZoneViewsRunHandler.CellRef { LevelId = c.LevelId, AreaId = c.AreaId })
                .ToList();
            _handler.ViewNames  = _viewNames.ToList();
            _handler.SheetSetId = _sheetSetId;
            _handler.PushLog    = pushLog;
            _handler.OnProgress = onProgress;
            _handler.OnComplete = onComplete;
            _event.Raise();
        }

        private static TextBlock Note(string text)
        {
            var t = new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyles.Italic, Margin = new Thickness(0, 4, 0, 6),
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return t;
        }

        private static TextBlock Warn(string text)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 2) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return t;
        }
    }
}
