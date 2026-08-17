using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Zones;

namespace LemoineTools.Tools.Zones
{
    /// <summary>
    /// Build Sheets from Zones — the end of the chain.
    ///
    /// Pick levels, pick sheet sizes, pick recipes; each (level × sheet size × group)
    /// becomes one sheet with its views already in their recorded positions.
    /// </summary>
    public sealed class ZoneSheetsViewModel : IStepFlowTool, IStepAware, IReviewableTool, IRunResult, IToolCleanup
    {
        public string? ResultNoun => "sheets";
        public IReadOnlyList<ResultChip>? ResultChips => null;

        public string Title    => AppStrings.T("zones.sheets.title");
        public string RunLabel => AppStrings.T("zones.sheets.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", AppStrings.T("zones.sheets.steps.S1"), required: true),
            new StepDefinition("S2", AppStrings.T("zones.sheets.steps.S2"), required: true),
            new StepDefinition("S3", AppStrings.T("zones.sheets.steps.S3"), required: true),
            new StepDefinition("S4", AppStrings.T("zones.sheets.steps.S4"), required: false),
        };

        private readonly ZoneSheetsRunHandler? _handler;
        private readonly ExternalEvent?        _event;

        private readonly HashSet<string> _levelIds  = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _layoutIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _recipeIds = new HashSet<string>(StringComparer.Ordinal);

        private Action<string>? _refreshStep;

        public event EventHandler? ValidationChanged;
        private void Fire() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        private ZoneLibrary Lib => ZoneSettings.Instance.Library;

        public ZoneSheetsViewModel(ZoneSheetsRunHandler? handler, ExternalEvent? externalEvent)
        {
            _handler = handler;
            _event   = externalEvent;
        }

        public void SetContentRefreshCallback(Action<string> refresh) => _refreshStep = refresh;

        public void OnStepActivated(string stepId)
        {
            // S4 summarises S1-S3, so it must rebuild on activation rather than render the
            // empty state it was constructed with.
            if (stepId == "S4") _refreshStep?.Invoke("S4");
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
                case "S1": return CheckList(Lib.Levels.OrderBy(l => l.SortIndex).ThenBy(l => l.ElevationFt)
                                               .Select(l => (l.Id, l.Name, "")).ToList(),
                                            _levelIds, AppStrings.T("zones.sheets.noLevels"));
                case "S2": return CheckList(Lib.Layouts.OrderBy(l => l.SortIndex)
                                               .Select(l => (l.Id, l.Name, l.TitleBlockTypeName)).ToList(),
                                            _layoutIds, AppStrings.T("zones.sheets.noLayouts"));
                case "S3": return CheckList(Lib.Recipes.OrderBy(r => r.SortIndex)
                                               .Select(r => (r.Id, r.Name, r.Kind)).ToList(),
                                            _recipeIds, AppStrings.T("zones.sheets.noRecipes"));
                case "S4": return BuildReview();
                default:   return null;
            }
        }

        private FrameworkElement CheckList(List<(string Id, string Name, string Meta)> items,
                                           HashSet<string> selection, string emptyText)
        {
            var panel = new StackPanel();
            if (items.Count == 0) { panel.Children.Add(Note(emptyText)); return panel; }

            foreach (var it in items)
            {
                var cb = new CheckBox
                {
                    Content = string.IsNullOrEmpty(it.Meta) ? it.Name : $"{it.Name}   ({it.Meta})",
                    IsChecked = selection.Contains(it.Id),
                    Margin = new Thickness(0, 3, 0, 3),
                };
                cb.SetResourceReference(CheckBox.FontSizeProperty, "LemoineFS_SM");
                string id = it.Id;
                cb.Click += (s, e) =>
                {
                    if (cb.IsChecked == true) selection.Add(id); else selection.Remove(id);
                    Fire();
                };
                panel.Children.Add(cb);
            }
            return panel;
        }

        private FrameworkElement BuildReview()
        {
            var panel = new StackPanel();
            panel.Children.Add(Note(AppStrings.T("zones.sheets.plannedCount", PlannedSheetCount())));

            // A layout with no groups produces no sheets at all, which is silent otherwise.
            var emptyLayouts = Lib.Layouts
                .Where(l => _layoutIds.Contains(l.Id) && (l.Groups == null || l.Groups.Count == 0))
                .Select(l => l.Name).ToList();
            if (emptyLayouts.Count > 0)
                panel.Children.Add(Warn(AppStrings.T("zones.sheets.noGroupsWarn", string.Join(", ", emptyLayouts))));

            // Placements are what put views in the right spot; without them Revit centres
            // whatever it likes and the sheet looks plausible but is not to standard.
            int missing = 0;
            foreach (var lay in Lib.Layouts.Where(l => _layoutIds.Contains(l.Id)))
            foreach (var g in lay.Groups ?? new List<ZoneSheetGroup>())
            foreach (var aid in g.AreaIds ?? new List<string>())
            {
                string key = (g.AreaIds!.Count > 1) ? g.Id : "";
                if (Lib.Placement(aid, lay.TitleBlockTypeName, key) == null) missing++;
            }
            if (missing > 0)
                panel.Children.Add(Warn(AppStrings.T("zones.sheets.noPlacementWarn", missing)));

            return panel;
        }

        private int PlannedSheetCount()
        {
            int n = 0;
            foreach (var lay in Lib.Layouts.Where(l => _layoutIds.Contains(l.Id)))
            {
                int groups = (lay.Groups ?? new List<ZoneSheetGroup>()).Count;
                n += groups * _levelIds.Count;
            }
            return n;
        }

        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _levelIds.Count  > 0;
                case "S2": return _layoutIds.Count > 0;
                case "S3": return _recipeIds.Count > 0;
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return AppStrings.T("zones.sheets.summary.levels",  _levelIds.Count);
                case "S2": return AppStrings.T("zones.sheets.summary.layouts", _layoutIds.Count);
                case "S3": return AppStrings.T("zones.sheets.summary.recipes", _recipeIds.Count);
                default:   return AppStrings.T("zones.sheets.summary.planned", PlannedSheetCount());
            }
        }

        public IList<(string id, string label)> ReviewItems => new List<(string, string)>
        {
            ("levels",  AppStrings.T("zones.sheets.steps.S1")),
            ("layouts", AppStrings.T("zones.sheets.steps.S2")),
            ("recipes", AppStrings.T("zones.sheets.steps.S3")),
            ("planned", AppStrings.T("zones.sheets.steps.S4")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["levels"]  = SummaryFor("S1"),
            ["layouts"] = SummaryFor("S2"),
            ["recipes"] = SummaryFor("S3"),
            ["planned"] = SummaryFor("S4"),
        };

        public IList<string>? ReviewChips => null;
        public string? ReviewNote => AppStrings.T("zones.sheets.reviewNote");

        public void Run(Action<string, string> pushLog,
                        Action<int, int, int, int> onProgress,
                        Action<int, int, int> onComplete)
        {
            if (_handler == null || _event == null)
            {
                pushLog(AppStrings.T("zones.sheets.noHandler"), "fail");
                onComplete(0, 1, 0);
                return;
            }

            _handler.LevelIds   = _levelIds.ToList();
            _handler.LayoutIds  = _layoutIds.ToList();
            _handler.RecipeIds  = _recipeIds.ToList();
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
