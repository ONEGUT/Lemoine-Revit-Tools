using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;

namespace LemoineTools.Tools.Setup
{
    /// <summary>
    /// Align Coordinates — move the host Survey Point and/or Project Base Point to a resolved
    /// anchor (Internal Origin by default, or a picked grid intersection + level), then
    /// rotate/translate every selected link so its own resolved reference point coincides
    /// (that link's own Internal Origin by default, or its own named grid intersection when
    /// overridden per link). With the Matched Level Z method, each link carries its own
    /// "level to move" pick — that level is what lands on the host target elevation.
    /// Repositions the host's copy of each link only — use the separate
    /// "Push Coordinates to Links" tool to commit the correction into the linked files.
    /// </summary>
    public class AlignCoordinatesViewModel : IStepFlowTool, IReviewableTool, IStepAware, IToolCleanup
    {
        // SingleSelect item values double as the comparison tokens in SelectionChanged. They are
        // instance fields (not statics) so each window resolves them in its own active culture —
        // a static would freeze the first-touched language (see UpgradeLinks' placement-label fix).
        private readonly string _anchorInternalOriginLabel   = AppStrings.T("setup.alignCoordinates.labels.anchorInternalOrigin");
        private readonly string _anchorGridIntersectionLabel = AppStrings.T("setup.alignCoordinates.labels.anchorGridIntersection");
        private readonly string _zInternalOriginLabel        = AppStrings.T("setup.alignCoordinates.labels.zInternalOrigin");
        private readonly string _zMatchedLevelLabel          = AppStrings.T("setup.alignCoordinates.labels.zMatchedLevel");

        public string Title    => AppStrings.T("setup.alignCoordinates.title");
        public string RunLabel => AppStrings.T("setup.alignCoordinates.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("host",  AppStrings.T("setup.alignCoordinates.steps.host"),  required: true),
            new StepDefinition("links", AppStrings.T("setup.alignCoordinates.steps.links"), required: false),
            new StepDefinition("run",   AppStrings.T("setup.alignCoordinates.steps.run"),   required: false),
        };

        private readonly AlignCoordinatesRunHandler? _runHandler;
        private readonly ExternalEvent?              _runEvent;
        private readonly AlignCoordinatesData        _data;

        // ── host anchor state ──────────────────────────────────────────────────
        private AnchorSource _hostAnchorSource = AnchorSource.InternalOrigin;
        private string? _hostGrid1;
        private string? _hostGrid2;
        private ZSource _hostZSource = ZSource.InternalOriginZ;
        private string? _hostLevel;
        private bool _moveSurvey = true;
        private bool _movePbp    = true;
        private bool _rotate     = true;

        // ── per-link state, keyed by link instance id ──────────────────────────
        private readonly Dictionary<long, LinkAlignSpec> _linkSpecs = new Dictionary<long, LinkAlignSpec>();

        private StackPanel? _hostGridFieldsPanel;
        private StackPanel? _hostLevelFieldsPanel;
        private Action<string>? _refreshStep;

        public event EventHandler? ValidationChanged;
        private void Changed() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public AlignCoordinatesViewModel(AlignCoordinatesRunHandler? runHandler, ExternalEvent? runEvent, AlignCoordinatesData? data)
        {
            _runHandler = runHandler;
            _runEvent   = runEvent;
            _data       = data ?? new AlignCoordinatesData();

            if (_data.HostGridNames.Count >= 1) _hostGrid1 = _data.HostGridNames[0];
            if (_data.HostGridNames.Count >= 2) _hostGrid2 = _data.HostGridNames[1];
            if (_data.HostLevelNames.Count >= 1) _hostLevel = _data.HostLevelNames[0];

            foreach (var l in _data.Links)
                _linkSpecs[l.LinkInstId] = new LinkAlignSpec { LinkInstId = l.LinkInstId, LinkName = l.Name };
        }

        public void OnWindowClosed()
        {
            if (_runHandler == null) return;
            _runHandler.PushLog    = null;
            _runHandler.OnProgress = null;
            _runHandler.OnComplete = null;
        }

        // ── IStepAware — the links step re-renders when the host Z method changes ──
        public void SetContentRefreshCallback(Action<string> rebuildStepContent) => _refreshStep = rebuildStepContent;
        public void OnStepActivated(string stepId) { }

        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "host":  return BuildHostStep();
                case "links": return BuildLinksStep();
                default:      return null;   // "run" is rendered by IReviewableTool
            }
        }

        // ── Step 1: alignment method ────────────────────────────────────────────
        private FrameworkElement BuildHostStep()
        {
            var outer = new StackPanel();

            outer.Children.Add(Label(AppStrings.T("setup.alignCoordinates.labels.alignMethod")));
            var anchorSel = new SingleSelect
            {
                Items          = new List<string> { _anchorInternalOriginLabel, _anchorGridIntersectionLabel },
                SelectedItem   = _hostAnchorSource == AnchorSource.GridIntersection ? _anchorGridIntersectionLabel : _anchorInternalOriginLabel,
                AccessibleName = AppStrings.T("setup.alignCoordinates.labels.alignMethod"),
            };
            outer.Children.Add(anchorSel);
            outer.Children.Add(Dim(AppStrings.T("setup.alignCoordinates.labels.anchorHint")));

            _hostGridFieldsPanel = new StackPanel();
            outer.Children.Add(_hostGridFieldsPanel);

            outer.Children.Add(Label(AppStrings.T("setup.alignCoordinates.labels.zMethod")));
            var zSel = new SingleSelect
            {
                Items          = new List<string> { _zInternalOriginLabel, _zMatchedLevelLabel },
                SelectedItem   = _hostZSource == ZSource.MatchedLevel ? _zMatchedLevelLabel : _zInternalOriginLabel,
                AccessibleName = AppStrings.T("setup.alignCoordinates.labels.zMethod"),
            };
            outer.Children.Add(zSel);

            _hostLevelFieldsPanel = new StackPanel();
            outer.Children.Add(_hostLevelFieldsPanel);

            anchorSel.SelectionChanged += v =>
            {
                _hostAnchorSource = v == _anchorGridIntersectionLabel ? AnchorSource.GridIntersection : AnchorSource.InternalOrigin;
                RebuildHostGridFields();
                Changed();
            };
            zSel.SelectionChanged += v =>
            {
                _hostZSource = v == _zMatchedLevelLabel ? ZSource.MatchedLevel : ZSource.InternalOriginZ;
                RebuildHostLevelFields();
                // The links step shows a per-link "level to move" picker only in Matched Level
                // mode — rebuild its content so the pickers appear/disappear with this choice.
                _refreshStep?.Invoke("links");
                Changed();
            };

            Divider(outer);
            outer.Children.Add(Label(AppStrings.T("setup.alignCoordinates.labels.hostPoints")));
            var toggles = new ToggleSwitches { AccessibleName = AppStrings.T("setup.alignCoordinates.labels.hostPoints") };
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "survey", Label = AppStrings.T("setup.alignCoordinates.labels.surveyPoint"),      DefaultOn = _moveSurvey },
                new ToggleItem { Id = "pbp",    Label = AppStrings.T("setup.alignCoordinates.labels.projectBasePoint"), DefaultOn = _movePbp },
            });
            toggles.StateChanged += st =>
            {
                _moveSurvey = st.TryGetValue("survey", out var s) && s;
                _movePbp    = st.TryGetValue("pbp",    out var p) && p;
                Changed();
            };
            outer.Children.Add(toggles);
            outer.Children.Add(Dim(AppStrings.T("setup.alignCoordinates.labels.hostPointsHint")));

            RebuildHostGridFields();
            RebuildHostLevelFields();

            return outer;
        }

        private void RebuildHostGridFields()
        {
            if (_hostGridFieldsPanel == null) return;
            _hostGridFieldsPanel.Children.Clear();
            if (_hostAnchorSource != AnchorSource.GridIntersection) return;

            if (_data.HostGridNames.Count < 2)
            {
                _hostGridFieldsPanel.Children.Add(Dim(AppStrings.T("setup.alignCoordinates.labels.needTwoGrids")));
                return;
            }

            if (_hostGrid1 == null) _hostGrid1 = _data.HostGridNames[0];

            var wrap = SubFieldsBox();
            var inner = (StackPanel)wrap.Child;

            inner.Children.Add(Label(AppStrings.T("setup.alignCoordinates.labels.grid1")));
            var g1 = new SingleSelect { Items = _data.HostGridNames, SelectedItem = _hostGrid1, AccessibleName = AppStrings.T("setup.alignCoordinates.labels.grid1") };
            inner.Children.Add(g1);

            var g2Label = Label(AppStrings.T("setup.alignCoordinates.labels.grid2"));
            inner.Children.Add(g2Label);
            var g2Container = new StackPanel();
            inner.Children.Add(g2Container);

            void RebuildGrid2()
            {
                g2Container.Children.Clear();
                var candidates = CrossingGridNames(_data.HostGrids, _hostGrid1);
                if (candidates.Count == 0)
                {
                    g2Container.Children.Add(Dim(AppStrings.T("setup.alignCoordinates.labels.noCrossing", _hostGrid1 ?? "")));
                    _hostGrid2 = null;
                    return;
                }
                if (_hostGrid2 == null || !candidates.Contains(_hostGrid2))
                    _hostGrid2 = candidates[0];

                var g2 = new SingleSelect { Items = candidates, SelectedItem = _hostGrid2, AccessibleName = AppStrings.T("setup.alignCoordinates.labels.grid2") };
                g2.SelectionChanged += v => { _hostGrid2 = v; Changed(); };
                g2Container.Children.Add(g2);
            }

            g1.SelectionChanged += v => { _hostGrid1 = v; RebuildGrid2(); Changed(); };
            RebuildGrid2();

            _hostGridFieldsPanel.Children.Add(wrap);
        }

        // Grids that cross the named grid within the SAME document (host grids against host
        // grids). A grid whose curve isn't a straight Line is never filtered out (IsLine=false
        // is treated as "always crosses" — see GridGeom's doc comment).
        private static List<string> CrossingGridNames(List<GridGeom> grids, string? againstName)
        {
            var against = grids.FirstOrDefault(g => g.Name == againstName);
            if (against == null) return grids.Select(g => g.Name).Where(n => n != againstName).ToList();
            return grids.Where(g => g.Name != againstName && GridsCross(against, g))
                        .Select(g => g.Name).ToList();
        }

        // Segment/segment intersection test with each segment extended slightly (1 ft) at both
        // ends so grids that meet exactly at their drawn extents still count as crossing.
        // Non-Line grids (arcs/splines) are treated as always crossing.
        private static bool GridsCross(GridGeom a, GridGeom b)
        {
            if (!a.IsLine || !b.IsLine) return true;

            const double ext = 1.0;
            (double x0, double y0, double x1, double y1) Extend(GridGeom g)
            {
                double dx = g.X1 - g.X0, dy = g.Y1 - g.Y0;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-9) return (g.X0, g.Y0, g.X1, g.Y1);
                double ux = dx / len, uy = dy / len;
                return (g.X0 - ux * ext, g.Y0 - uy * ext, g.X1 + ux * ext, g.Y1 + uy * ext);
            }

            var (ax0, ay0, ax1, ay1) = Extend(a);
            var (bx0, by0, bx1, by1) = Extend(b);

            double d1x = ax1 - ax0, d1y = ay1 - ay0;
            double d2x = bx1 - bx0, d2y = by1 - by0;
            double denom = d1x * d2y - d1y * d2x;
            if (Math.Abs(denom) < 1e-9) return false; // parallel (or coincident) — never crosses

            double t = ((bx0 - ax0) * d2y - (by0 - ay0) * d2x) / denom;
            double u = ((bx0 - ax0) * d1y - (by0 - ay0) * d1x) / denom;
            return t >= 0 && t <= 1 && u >= 0 && u <= 1;
        }

        private void RebuildHostLevelFields()
        {
            if (_hostLevelFieldsPanel == null) return;
            _hostLevelFieldsPanel.Children.Clear();
            if (_hostZSource != ZSource.MatchedLevel) return;

            if (_data.HostLevelNames.Count == 0)
            {
                _hostLevelFieldsPanel.Children.Add(Dim(AppStrings.T("setup.alignCoordinates.labels.noLevels")));
                return;
            }

            if (_hostLevel == null) _hostLevel = _data.HostLevelNames[0];

            var wrap = SubFieldsBox();
            var inner = (StackPanel)wrap.Child;

            inner.Children.Add(Label(AppStrings.T("setup.alignCoordinates.labels.level")));
            var lvl = new SingleSelect { Items = _data.HostLevelNames, SelectedItem = _hostLevel, AccessibleName = AppStrings.T("setup.alignCoordinates.labels.level") };
            lvl.SelectionChanged += v => { _hostLevel = v; Changed(); };
            inner.Children.Add(lvl);

            _hostLevelFieldsPanel.Children.Add(wrap);
        }

        private static Border SubFieldsBox()
        {
            var wrap = new Border { Padding = new Thickness(10), Margin = new Thickness(0, 4, 0, 4) };
            wrap.SetResourceReference(Border.BackgroundProperty,   "LemoineRaised");
            wrap.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_MD");
            wrap.Child = new StackPanel();
            return wrap;
        }

        // ── Step 2: links ─────────────────────────────────────────────────────────
        private FrameworkElement BuildLinksStep()
        {
            var outer = new StackPanel();

            outer.Children.Add(Label(AppStrings.T("setup.alignCoordinates.labels.linksCount", _data.Links.Count)));

            if (_data.Links.Count == 0)
            {
                outer.Children.Add(Dim(AppStrings.T("setup.alignCoordinates.labels.noLinks")));
            }
            else
            {
                var listBorder = new Border { BorderThickness = new Thickness(1) };
                listBorder.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorderMid");
                listBorder.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_MD");

                var list = new StackPanel();
                foreach (var info in _data.Links) list.Children.Add(BuildLinkRow(info));
                listBorder.Child = list;

                outer.Children.Add(listBorder);
                outer.Children.Add(Dim(AppStrings.T("setup.alignCoordinates.labels.linksHint")));
                if (_hostZSource == ZSource.MatchedLevel)
                    outer.Children.Add(Dim(AppStrings.T("setup.alignCoordinates.labels.levelPickHint")));
            }

            Divider(outer);
            var toggles = new ToggleSwitches { AccessibleName = AppStrings.T("setup.alignCoordinates.labels.rotateLabel") };
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "rotate",  Label = AppStrings.T("setup.alignCoordinates.labels.rotateLabel"),
                    Desc = AppStrings.T("setup.alignCoordinates.labels.rotateDesc"), DefaultOn = _rotate },
            });
            toggles.StateChanged += st =>
            {
                _rotate = st.TryGetValue("rotate", out var r) && r;
                Changed();
            };
            outer.Children.Add(toggles);
            outer.Children.Add(Dim(AppStrings.T("setup.alignCoordinates.labels.pushNote")));

            return outer;
        }

        private FrameworkElement BuildLinkRow(AlignLinkInfo info)
        {
            var spec = _linkSpecs[info.LinkInstId];

            var row = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(10, 8, 10, 8) };
            row.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var rowStack = new StackPanel();
            row.Child = rowStack;

            var header = new DockPanel();
            rowStack.Children.Add(header);

            var overridePanel = new StackPanel { Margin = new Thickness(25, 8, 0, 0) };
            rowStack.Children.Add(overridePanel);

            var levelPanel = new StackPanel { Margin = new Thickness(25, 8, 0, 0) };
            rowStack.Children.Add(levelPanel);

            var cb = new CheckBox { IsChecked = spec.Selected, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            DockPanel.SetDock(cb, Dock.Left);
            header.Children.Add(cb);

            var actionLink = new TextBlock
            {
                Cursor              = Cursors.Hand,
                VerticalAlignment   = VerticalAlignment.Center,
                TextDecorations     = TextDecorations.Underline,
                Background          = Brushes.Transparent,
                Margin              = new Thickness(10, 0, 0, 0),
            };
            actionLink.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            actionLink.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            actionLink.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            DockPanel.SetDock(actionLink, Dock.Right);
            header.Children.Add(actionLink);

            var badgeText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            badgeText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            badgeText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            var badge = new Border
            {
                BorderThickness    = new Thickness(1),
                Padding            = new Thickness(8, 2, 8, 2),
                VerticalAlignment  = VerticalAlignment.Center,
                Child              = badgeText,
            };
            badge.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Chip");
            DockPanel.SetDock(badge, Dock.Right);
            header.Children.Add(badge);

            var name = new TextBlock
            {
                Text                = info.Name,
                VerticalAlignment   = VerticalAlignment.Center,
                TextTrimming        = TextTrimming.CharacterEllipsis,
            };
            name.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            name.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            name.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            header.Children.Add(name);   // last child — fills remaining space (DockPanel.LastChildFill)

            void UpdateBadge()
            {
                if (spec.Overridden)
                {
                    badge.SetResourceReference(Border.BackgroundProperty,   "LemoineAccentDim");
                    badge.SetResourceReference(Border.BorderBrushProperty,  "LemoineAccent");
                    badgeText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
                    badgeText.Text  = AppStrings.T("setup.alignCoordinates.labels.badgeGridIntersection");
                    actionLink.Text = AppStrings.T("setup.alignCoordinates.labels.useDefault");
                }
                else
                {
                    badge.SetResourceReference(Border.BackgroundProperty,   "LemoineRaised");
                    badge.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorder");
                    badgeText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
                    badgeText.Text  = AppStrings.T("setup.alignCoordinates.labels.badgeInternalOrigin");
                    actionLink.Text = AppStrings.T("setup.alignCoordinates.labels.override");
                }
            }

            void RebuildOverridePanel()
            {
                overridePanel.Children.Clear();
                overridePanel.Visibility = spec.Overridden ? Visibility.Visible : Visibility.Collapsed;
                if (!spec.Overridden) return;

                var grids = info.GridNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                if (grids.Count < 2)
                {
                    overridePanel.Children.Add(Dim(AppStrings.T("setup.alignCoordinates.labels.linkNeedsTwoGrids", info.Name)));
                    return;
                }

                if (string.IsNullOrEmpty(spec.Grid1Name)) spec.Grid1Name = grids[0];

                var fieldsGrid = new Grid();
                fieldsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                fieldsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
                fieldsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var col1 = new StackPanel();
                Grid.SetColumn(col1, 0);
                col1.Children.Add(Label(AppStrings.T("setup.alignCoordinates.labels.grid1")));
                var g1Sel = new SingleSelect { Items = grids, SelectedItem = spec.Grid1Name, AccessibleName = $"{info.Name} {AppStrings.T("setup.alignCoordinates.labels.grid1")}" };
                col1.Children.Add(g1Sel);
                fieldsGrid.Children.Add(col1);

                var col2 = new StackPanel();
                Grid.SetColumn(col2, 2);
                col2.Children.Add(Label(AppStrings.T("setup.alignCoordinates.labels.grid2")));
                var g2Container = new StackPanel();
                col2.Children.Add(g2Container);
                fieldsGrid.Children.Add(col2);

                void RebuildG2()
                {
                    g2Container.Children.Clear();
                    var candidates = CrossingGridNames(info.Grids, spec.Grid1Name);
                    if (candidates.Count == 0)
                    {
                        g2Container.Children.Add(Dim(AppStrings.T("setup.alignCoordinates.labels.linkNoCrossing", info.Name, spec.Grid1Name)));
                        spec.Grid2Name = "";
                        return;
                    }
                    if (string.IsNullOrEmpty(spec.Grid2Name) || !candidates.Contains(spec.Grid2Name))
                        spec.Grid2Name = candidates[0];

                    var g2Sel = new SingleSelect { Items = candidates, SelectedItem = spec.Grid2Name, AccessibleName = $"{info.Name} {AppStrings.T("setup.alignCoordinates.labels.grid2")}" };
                    g2Sel.SelectionChanged += v => { spec.Grid2Name = v ?? ""; Changed(); };
                    g2Container.Children.Add(g2Sel);
                }

                g1Sel.SelectionChanged += v => { spec.Grid1Name = v ?? ""; RebuildG2(); Changed(); };
                RebuildG2();

                overridePanel.Children.Add(fieldsGrid);
            }

            // The per-link "level to move" pick — only meaningful when the host Z method is
            // Matched Level. The whole links step is refreshed when that choice changes (see
            // the zSel handler in BuildHostStep), so this only renders the current mode.
            void RebuildLevelPanel()
            {
                levelPanel.Children.Clear();
                bool show = _hostZSource == ZSource.MatchedLevel;
                levelPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (!show) return;

                if (info.LevelNames.Count == 0)
                {
                    spec.LevelName = "";
                    levelPanel.Children.Add(Dim(AppStrings.T("setup.alignCoordinates.labels.linkNoLevels", info.Name)));
                    return;
                }

                if (string.IsNullOrEmpty(spec.LevelName) ||
                    !info.LevelNames.Any(n => string.Equals(n, spec.LevelName, StringComparison.OrdinalIgnoreCase)))
                {
                    spec.LevelName = DefaultLevelFor(info);
                }

                levelPanel.Children.Add(Label(AppStrings.T("setup.alignCoordinates.labels.linkLevel")));
                var lvlSel = new SingleSelect { Items = info.LevelNames, SelectedItem = spec.LevelName, AccessibleName = $"{info.Name} {AppStrings.T("setup.alignCoordinates.labels.linkLevel")}" };
                lvlSel.SelectionChanged += v => { spec.LevelName = v ?? ""; Changed(); };
                levelPanel.Children.Add(lvlSel);
            }

            cb.Checked   += (s, e) => { spec.Selected = true;  Changed(); };
            cb.Unchecked += (s, e) => { spec.Selected = false; Changed(); };

            actionLink.MouseLeftButtonDown += (s, e) =>
            {
                spec.Overridden   = !spec.Overridden;
                spec.AnchorSource = spec.Overridden ? AnchorSource.GridIntersection : AnchorSource.InternalOrigin;
                UpdateBadge();
                RebuildOverridePanel();
                Changed();
            };

            UpdateBadge();
            RebuildOverridePanel();
            RebuildLevelPanel();

            return row;
        }

        // The link level offered by default: the one sharing the host target level's name, else
        // the link's lowest level.
        private string DefaultLevelFor(AlignLinkInfo info)
        {
            var match = info.LevelNames.FirstOrDefault(n => string.Equals(n, _hostLevel, StringComparison.OrdinalIgnoreCase));
            return match ?? info.LevelNames[0];
        }

        // ── Review ──────────────────────────────────────────────────────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("anchor", AppStrings.T("setup.alignCoordinates.review.itemAnchor")),
            ("points", AppStrings.T("setup.alignCoordinates.review.itemPoints")),
            ("links",  AppStrings.T("setup.alignCoordinates.review.itemLinks")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["anchor"] = AnchorSummary(),
            ["points"] = PointsSummary(),
            ["links"]  = LinksSummary(),
        };

        public IList<string>? ReviewChips => new[]
        {
            AppStrings.T("setup.alignCoordinates.review.chipRotate") + (_rotate ? " ✓" : " ✗"),
        };
        public string? ReviewNote => AppStrings.T("setup.alignCoordinates.labels.pushNote");

        public string? ReviewWarning
        {
            get
            {
                if (_hostAnchorSource == AnchorSource.GridIntersection && _hostGrid1 != null && _hostGrid1 == _hostGrid2)
                    return AppStrings.T("setup.alignCoordinates.review.warnSameGrids");

                foreach (var spec in _linkSpecs.Values)
                    if (spec.Selected && spec.Overridden && !string.IsNullOrEmpty(spec.Grid1Name) && spec.Grid1Name == spec.Grid2Name)
                        return AppStrings.T("setup.alignCoordinates.review.warnLinkSameGrids", spec.LinkName);

                return null;
            }
        }

        private string AnchorSummary()
        {
            string xy = _hostAnchorSource == AnchorSource.GridIntersection
                ? AppStrings.T("setup.alignCoordinates.review.gridPair", _hostGrid1 ?? "—", _hostGrid2 ?? "—")
                : AppStrings.T("setup.alignCoordinates.review.anchorOrigin");
            string z = _hostZSource == ZSource.MatchedLevel
                ? AppStrings.T("setup.alignCoordinates.review.zLevel", _hostLevel ?? "—")
                : AppStrings.T("setup.alignCoordinates.review.zOrigin");
            return AppStrings.T("setup.alignCoordinates.review.anchorValue", xy, z);
        }

        private string PointsSummary()
        {
            if (_moveSurvey && _movePbp) return AppStrings.T("setup.alignCoordinates.review.pointsBoth");
            if (_moveSurvey) return AppStrings.T("setup.alignCoordinates.review.pointsSurvey");
            if (_movePbp)    return AppStrings.T("setup.alignCoordinates.review.pointsPbp");
            return AppStrings.T("setup.alignCoordinates.review.pointsNone");
        }

        private string LinksSummary()
        {
            var selected = _linkSpecs.Values.Where(s => s.Selected).ToList();
            if (selected.Count == 0) return AppStrings.T("setup.alignCoordinates.review.linksNone");
            int overridden = selected.Count(s => s.Overridden);
            return overridden == 0
                ? AppStrings.T("setup.alignCoordinates.review.linksValue", selected.Count)
                : AppStrings.T("setup.alignCoordinates.review.linksOverridden", selected.Count, overridden);
        }

        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "host":
                    if (!(_moveSurvey || _movePbp)) return false;
                    if (_hostAnchorSource == AnchorSource.GridIntersection &&
                        (_hostGrid1 == null || _hostGrid2 == null || _hostGrid1 == _hostGrid2))
                        return false;
                    if (_hostZSource == ZSource.MatchedLevel && string.IsNullOrEmpty(_hostLevel))
                        return false;
                    return true;

                case "links":
                    var selected = _linkSpecs.Values.Where(s => s.Selected).ToList();
                    if (selected.Count == 0) return false;
                    foreach (var s in selected)
                    {
                        if (s.Overridden && (string.IsNullOrEmpty(s.Grid1Name) || string.IsNullOrEmpty(s.Grid2Name) || s.Grid1Name == s.Grid2Name))
                            return false;
                    }
                    return true;

                default:
                    return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "host":
                    return AppStrings.T("setup.alignCoordinates.summaries.host", AnchorSummary(), PointsSummary());
                case "links":
                    var selected = _linkSpecs.Values.Where(s => s.Selected).ToList();
                    return selected.Count == 0 ? "—"
                        : AppStrings.T("setup.alignCoordinates.summaries.links", LinksSummary(), _rotate ? "✓" : "✗");
                case "run": return AppStrings.T("setup.alignCoordinates.summaries.run");
                default:    return "—";
            }
        }

        public void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null)
            {
                pushLog(AppStrings.T("setup.alignCoordinates.log.handlerMissing"), "fail");
                onComplete(0, 1, 0);
                return;
            }

            _runHandler.HostAnchorSource = _hostAnchorSource;
            _runHandler.HostGrid1Name    = _hostGrid1 ?? "";
            _runHandler.HostGrid2Name    = _hostGrid2 ?? "";
            _runHandler.HostZSource      = _hostZSource;
            _runHandler.HostLevelName    = _hostLevel ?? "";
            _runHandler.MoveSurvey       = _moveSurvey;
            _runHandler.MovePbp          = _movePbp;
            _runHandler.Rotate           = _rotate;
            _runHandler.LinkSpecs        = _linkSpecs.Values.Select(s => new LinkAlignSpec
            {
                LinkInstId   = s.LinkInstId,
                LinkName     = s.LinkName,
                Selected     = s.Selected,
                Overridden   = s.Overridden,
                AnchorSource = s.AnchorSource,
                Grid1Name    = s.Grid1Name,
                Grid2Name    = s.Grid2Name,
                LevelName    = s.LevelName,
            }).ToList();
            _runHandler.PushLog     = pushLog;
            _runHandler.OnProgress  = onProgress;
            _runHandler.OnComplete  = onComplete;

            pushLog(AppStrings.T("setup.alignCoordinates.log.starting"), "info");
            _runEvent.Raise();
        }

        // ── helpers ──────────────────────────────────────────────────────────────
        private static TextBlock Label(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 6, 0, 4) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static TextBlock Dim(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static void Divider(StackPanel parent)
        {
            var sep = new System.Windows.Shapes.Rectangle { Height = 1, Margin = new Thickness(0, 8, 0, 8) };
            sep.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "LemoineBorder");
            parent.Children.Add(sep);
        }
    }
}
