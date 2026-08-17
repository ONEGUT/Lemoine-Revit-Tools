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
    /// Create Views from Zones — pick zone cells, pick recipes, get views.
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
        private readonly HashSet<string> _recipeIds = new HashSet<string>(StringComparer.Ordinal);
        private string _layoutId = "";

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
            if (stepId == "S3") _refreshStep?.Invoke("S3");
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
                case "S2": return BuildRecipeStep();
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

        private FrameworkElement BuildRecipeStep()
        {
            var panel = new StackPanel();

            if (Lib.Recipes.Count == 0)
            {
                panel.Children.Add(Note(AppStrings.T("zones.views.noRecipes")));
                return panel;
            }

            foreach (var r in Lib.Recipes.OrderBy(x => x.SortIndex))
            {
                var cb = new CheckBox
                {
                    Content = $"{r.Name}  ({r.Kind})",
                    IsChecked = _recipeIds.Contains(r.Id),
                    Margin = new Thickness(0, 3, 0, 3),
                };
                cb.SetResourceReference(CheckBox.FontSizeProperty, "LemoineFS_SM");
                string id = r.Id;
                cb.Click += (s, e) =>
                {
                    if (cb.IsChecked == true) _recipeIds.Add(id); else _recipeIds.Remove(id);
                    Fire();
                };
                panel.Children.Add(cb);
            }

            // Choosing a sheet size lets the run reuse the scale already solved into that
            // layout's placements, instead of falling back to the recipe default.
            if (Lib.Layouts.Count > 0)
            {
                panel.Children.Add(Note(AppStrings.T("zones.views.layoutHint")));

                var options = new List<string> { AppStrings.T("zones.views.noLayout") };
                options.AddRange(Lib.Layouts.Select(l => l.Name));

                var combo = new WpfComboBox { Margin = new Thickness(0, 4, 0, 0), MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
                combo.SetResourceReference(WpfComboBox.FontSizeProperty, "LemoineFS_SM");
                foreach (var o in options) combo.Items.Add(o);
                combo.SelectedIndex = 0;
                combo.SelectionChanged += (s, e) =>
                {
                    string v = combo.SelectedItem as string ?? "";
                    var picked = Lib.Layouts.FirstOrDefault(l => l.Name == v);
                    _layoutId = picked?.Id ?? "";
                    Fire();
                };
                panel.Children.Add(combo);
            }

            return panel;
        }

        private FrameworkElement BuildSummaryStep()
        {
            var panel = new StackPanel();
            panel.Children.Add(Note(AppStrings.T("zones.views.plannedCount",
                                                 _cells.Count * Math.Max(_recipeIds.Count, 0))));

            // Naming across several sheet sizes is the one way this run can fail wholesale,
            // so it is checked here rather than discovered at run time.
            var selected = Lib.Recipes.Where(r => _recipeIds.Contains(r.Id)).ToList();
            if (!string.IsNullOrEmpty(_layoutId))
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
                case "S2": return _recipeIds.Count > 0;
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return AppStrings.T("zones.views.summary.zones", _cells.Count);
                case "S2": return AppStrings.T("zones.views.summary.recipes", _recipeIds.Count);
                default:   return AppStrings.T("zones.views.summary.planned",
                                               _cells.Count * Math.Max(_recipeIds.Count, 0));
            }
        }

        public IList<(string id, string label)> ReviewItems => new List<(string, string)>
        {
            ("zones",   AppStrings.T("zones.views.steps.S1")),
            ("recipes", AppStrings.T("zones.views.steps.S2")),
            ("planned", AppStrings.T("zones.views.steps.S3")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["zones"]   = SummaryFor("S1"),
            ["recipes"] = SummaryFor("S2"),
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
                if (string.IsNullOrEmpty(_layoutId)) return null;
                var risky = Lib.Recipes
                    .Where(r => _recipeIds.Contains(r.Id) && !ZoneNamingTokens.VariesByLayout(r.NamePattern))
                    .Select(r => r.Name).ToList();
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
            _handler.RecipeIds  = _recipeIds.ToList();
            _handler.LayoutId   = _layoutId;
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
