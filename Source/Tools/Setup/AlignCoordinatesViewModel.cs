using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;

namespace LemoineTools.Tools.Setup
{
    /// <summary>
    /// Align Coordinates — pick a host reference feature (Internal Origin, Project Base Point,
    /// Survey Point, or a grid intersection + level), optionally move the host Survey Point and/or
    /// Project Base Point onto it, then for each selected link pick which of ITS features
    /// (the same four choices) is moved onto that host reference — rotating the link to match
    /// when both sides carry a direction (Grid Intersection or Survey Point). Repositions the
    /// host's copy of each link only — use the separate "Push Coordinates to Links" tool to
    /// commit the correction into the linked files.
    /// </summary>
    public class AlignCoordinatesViewModel : IStepFlowTool, IReviewableTool, IStepAware, IToolCleanup
    {
        // Anchor picker item values double as the comparison tokens. Instance fields (not statics)
        // so each window resolves them in its own active culture — a static would freeze the
        // first-touched language (see UpgradeLinks' placement-label fix).
        private readonly string _anchorInternalOrigin   = AppStrings.T("setup.alignCoordinates.anchor.internalOrigin");
        private readonly string _anchorProjectBasePoint = AppStrings.T("setup.alignCoordinates.anchor.projectBasePoint");
        private readonly string _anchorSurveyPoint      = AppStrings.T("setup.alignCoordinates.anchor.surveyPoint");
        private readonly string _anchorGridIntersection = AppStrings.T("setup.alignCoordinates.anchor.gridIntersection");

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

        // ── host reference state ────────────────────────────────────────────────
        private AnchorSource _hostAnchorSource = AnchorSource.InternalOrigin;
        private string? _hostGrid1;
        private string? _hostGrid2;
        private string? _hostLevel;
        private bool _moveSurvey = true;   // user intent — effective move excludes the reference point
        private bool _movePbp    = true;
        private bool _rotate     = true;

        // ── per-link state, keyed by link instance id ──────────────────────────
        private readonly Dictionary<long, LinkAlignSpec> _linkSpecs = new Dictionary<long, LinkAlignSpec>();

        private StackPanel? _hostFieldsPanel;
        private StackPanel? _hostPointsPanel;

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

        // IStepAware is implemented for interface parity with the framework; no cross-step
        // content rebuild is needed now that each link carries its own independent anchor.
        public void SetContentRefreshCallback(Action<string> rebuildStepContent) { }
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

        // ── anchor label ↔ enum (per instance) ──────────────────────────────────
        private static readonly AnchorSource[] AnchorOrder =
        {
            AnchorSource.InternalOrigin, AnchorSource.ProjectBasePoint,
            AnchorSource.SurveyPoint,    AnchorSource.GridIntersection,
        };

        private string AnchorLabel(AnchorSource s)
        {
            switch (s)
            {
                case AnchorSource.ProjectBasePoint: return _anchorProjectBasePoint;
                case AnchorSource.SurveyPoint:      return _anchorSurveyPoint;
                case AnchorSource.GridIntersection: return _anchorGridIntersection;
                default:                            return _anchorInternalOrigin;
            }
        }

        private List<string> AnchorLabels() => AnchorOrder.Select(AnchorLabel).ToList();

        private bool TryAnchor(string? label, out AnchorSource src)
        {
            foreach (var s in AnchorOrder)
            {
                if (string.Equals(AnchorLabel(s), label, StringComparison.Ordinal)) { src = s; return true; }
            }
            src = AnchorSource.InternalOrigin;
            return false;
        }

        private bool EffectiveMoveSurvey() => _moveSurvey && _hostAnchorSource != AnchorSource.SurveyPoint;
        private bool EffectiveMovePbp()    => _movePbp    && _hostAnchorSource != AnchorSource.ProjectBasePoint;

        // ── Step 1: host reference ──────────────────────────────────────────────
        private FrameworkElement BuildHostStep()
        {
            var outer = new StackPanel();

            outer.Children.Add(Label(AppStrings.T("setup.alignCoordinates.labels.hostReference")));
            var anchorSel = new SingleSelect
            {
                Items          = AnchorLabels(),
                SelectedItem   = AnchorLabel(_hostAnchorSource),
                AccessibleName = AppStrings.T("setup.alignCoordinates.labels.hostReference"),
            };
            outer.Children.Add(anchorSel);
            outer.Children.Add(Dim(AppStrings.T("setup.alignCoordinates.labels.hostReferenceHint")));

            _hostFieldsPanel = new StackPanel();
            outer.Children.Add(_hostFieldsPanel);

            Divider(outer);
            outer.Children.Add(Label(AppStrings.T("setup.alignCoordinates.labels.hostPoints")));
            _hostPointsPanel = new StackPanel();
            outer.Children.Add(_hostPointsPanel);
            outer.Children.Add(Dim(AppStrings.T("setup.alignCoordinates.labels.hostPointsHint")));

            anchorSel.SelectionChanged += v =>
            {
                if (!TryAnchor(v, out var src)) return;
                _hostAnchorSource = src;
                RebuildHostFields();
                RebuildHostPoints();
                Changed();
            };

            RebuildHostFields();
            RebuildHostPoints();

            return outer;
        }

        private void RebuildHostFields()
        {
            if (_hostFieldsPanel == null) return;
            _hostFieldsPanel.Children.Clear();
            if (_hostAnchorSource != AnchorSource.GridIntersection) return;

            if (_data.HostGridNames.Count < 2)
            {
                _hostFieldsPanel.Children.Add(Dim(AppStrings.T("setup.alignCoordinates.labels.needTwoGrids")));
                return;
            }

            if (_hostGrid1 == null) _hostGrid1 = _data.HostGridNames[0];

            var wrap = SubFieldsBox();
            var inner = (StackPanel)wrap.Child;

            inner.Children.Add(Label(AppStrings.T("setup.alignCoordinates.labels.grid1")));
            var g1 = new SingleSelect { Items = _data.HostGridNames, SelectedItem = _hostGrid1, AccessibleName = AppStrings.T("setup.alignCoordinates.labels.grid1") };
            inner.Children.Add(g1);

            inner.Children.Add(Label(AppStrings.T("setup.alignCoordinates.labels.grid2")));
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

            // Level (elevation) for the grid intersection.
            if (_data.HostLevelNames.Count == 0)
            {
                inner.Children.Add(Dim(AppStrings.T("setup.alignCoordinates.labels.noLevels")));
                _hostLevel = null;
            }
            else
            {
                if (_hostLevel == null) _hostLevel = _data.HostLevelNames[0];
                inner.Children.Add(Label(AppStrings.T("setup.alignCoordinates.labels.level")));
                var lvl = new SingleSelect { Items = _data.HostLevelNames, SelectedItem = _hostLevel, AccessibleName = AppStrings.T("setup.alignCoordinates.labels.level") };
                lvl.SelectionChanged += v => { _hostLevel = v; Changed(); };
                inner.Children.Add(lvl);
            }

            _hostFieldsPanel.Children.Add(wrap);
        }

        // Shows a move toggle only for the point(s) that are NOT the chosen reference. The
        // reference point stays put (moving it onto itself is meaningless), so its toggle is
        // withheld and a short note explains why. Intent (_moveSurvey/_movePbp) is preserved so
        // the toggle returns to its prior state when the reference changes away from that point.
        private void RebuildHostPoints()
        {
            if (_hostPointsPanel == null) return;
            _hostPointsPanel.Children.Clear();

            bool surveyIsRef = _hostAnchorSource == AnchorSource.SurveyPoint;
            bool pbpIsRef    = _hostAnchorSource == AnchorSource.ProjectBasePoint;

            var items = new List<ToggleItem>();
            if (!surveyIsRef) items.Add(new ToggleItem { Id = "survey", Label = AppStrings.T("setup.alignCoordinates.labels.surveyPoint"),      DefaultOn = _moveSurvey });
            if (!pbpIsRef)    items.Add(new ToggleItem { Id = "pbp",    Label = AppStrings.T("setup.alignCoordinates.labels.projectBasePoint"), DefaultOn = _movePbp });

            if (items.Count > 0)
            {
                var toggles = new ToggleSwitches { AccessibleName = AppStrings.T("setup.alignCoordinates.labels.hostPoints") };
                toggles.SetItems(items);
                toggles.StateChanged += st =>
                {
                    if (st.TryGetValue("survey", out var s)) _moveSurvey = s;
                    if (st.TryGetValue("pbp",    out var p)) _movePbp    = p;
                    Changed();
                };
                _hostPointsPanel.Children.Add(toggles);
            }

            if (surveyIsRef) _hostPointsPanel.Children.Add(Dim(AppStrings.T("setup.alignCoordinates.labels.anchorIsReference", AppStrings.T("setup.alignCoordinates.labels.surveyPoint"))));
            if (pbpIsRef)    _hostPointsPanel.Children.Add(Dim(AppStrings.T("setup.alignCoordinates.labels.anchorIsReference", AppStrings.T("setup.alignCoordinates.labels.projectBasePoint"))));
        }

        // Grids that cross the named grid within the SAME document. A grid whose curve isn't a
        // straight Line is never filtered out (IsLine=false is treated as "always crosses").
        private static List<string> CrossingGridNames(List<GridGeom> grids, string? againstName)
        {
            var against = grids.FirstOrDefault(g => g.Name == againstName);
            if (against == null) return grids.Select(g => g.Name).Where(n => n != againstName).ToList();
            return grids.Where(g => g.Name != againstName && GridsCross(against, g))
                        .Select(g => g.Name).ToList();
        }

        // Segment/segment intersection with each segment extended 1 ft at both ends so grids that
        // meet exactly at their drawn extents still count as crossing. Non-Line grids always cross.
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
            if (Math.Abs(denom) < 1e-9) return false; // parallel — never crosses

            double t = ((bx0 - ax0) * d2y - (by0 - ay0) * d2x) / denom;
            double u = ((bx0 - ax0) * d1y - (by0 - ay0) * d1x) / denom;
            return t >= 0 && t <= 1 && u >= 0 && u <= 1;
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

            // Header: checkbox + link name.
            var header = new DockPanel();
            rowStack.Children.Add(header);

            var cb = new CheckBox { IsChecked = spec.Selected, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            DockPanel.SetDock(cb, Dock.Left);
            header.Children.Add(cb);

            var name = new TextBlock
            {
                Text                = info.Name,
                VerticalAlignment   = VerticalAlignment.Center,
                TextTrimming        = TextTrimming.CharacterEllipsis,
            };
            name.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            name.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            name.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            header.Children.Add(name);

            // "Align by" dropdown.
            var alignRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(25, 8, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var alignLbl = Label(AppStrings.T("setup.alignCoordinates.labels.alignBy"));
            alignLbl.VerticalAlignment = VerticalAlignment.Center;
            alignLbl.Margin = new Thickness(0, 0, 8, 0);
            alignRow.Children.Add(alignLbl);
            var anchorSel = new SingleSelect { Width = 180, Items = AnchorLabels(), SelectedItem = AnchorLabel(spec.AnchorSource), AccessibleName = $"{info.Name} {AppStrings.T("setup.alignCoordinates.labels.alignBy")}" };
            alignRow.Children.Add(anchorSel);
            rowStack.Children.Add(alignRow);

            // Grid sub-fields (only when this link aligns by Grid Intersection).
            var subPanel = new StackPanel { Margin = new Thickness(25, 8, 0, 0) };
            rowStack.Children.Add(subPanel);

            void RebuildSub()
            {
                subPanel.Children.Clear();
                subPanel.Visibility = spec.AnchorSource == AnchorSource.GridIntersection ? Visibility.Visible : Visibility.Collapsed;
                if (spec.AnchorSource != AnchorSource.GridIntersection) return;

                var grids = info.GridNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                if (grids.Count < 2)
                {
                    subPanel.Children.Add(Dim(AppStrings.T("setup.alignCoordinates.labels.linkNeedsTwoGrids", info.Name)));
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

                subPanel.Children.Add(fieldsGrid);

                // Level (elevation) for this link's grid intersection.
                if (info.LevelNames.Count == 0)
                {
                    spec.LevelName = "";
                    subPanel.Children.Add(Dim(AppStrings.T("setup.alignCoordinates.labels.linkNoLevels", info.Name)));
                }
                else
                {
                    if (string.IsNullOrEmpty(spec.LevelName) ||
                        !info.LevelNames.Any(n => string.Equals(n, spec.LevelName, StringComparison.OrdinalIgnoreCase)))
                        spec.LevelName = DefaultLevelFor(info);

                    subPanel.Children.Add(Label(AppStrings.T("setup.alignCoordinates.labels.level")));
                    var lvlSel = new SingleSelect { Items = info.LevelNames, SelectedItem = spec.LevelName, AccessibleName = $"{info.Name} {AppStrings.T("setup.alignCoordinates.labels.level")}" };
                    lvlSel.SelectionChanged += v => { spec.LevelName = v ?? ""; Changed(); };
                    subPanel.Children.Add(lvlSel);
                }
            }

            cb.Checked   += (s, e) => { spec.Selected = true;  Changed(); };
            cb.Unchecked += (s, e) => { spec.Selected = false; Changed(); };

            anchorSel.SelectionChanged += v =>
            {
                if (TryAnchor(v, out var src)) { spec.AnchorSource = src; RebuildSub(); Changed(); }
            };

            RebuildSub();

            return row;
        }

        // The link level offered by default: the one sharing the host level's name, else the
        // link's lowest level.
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
                    if (spec.Selected && spec.AnchorSource == AnchorSource.GridIntersection &&
                        !string.IsNullOrEmpty(spec.Grid1Name) && spec.Grid1Name == spec.Grid2Name)
                        return AppStrings.T("setup.alignCoordinates.review.warnLinkSameGrids", spec.LinkName);

                if (!EffectiveMoveSurvey() && !EffectiveMovePbp() && !_linkSpecs.Values.Any(s => s.Selected))
                    return AppStrings.T("setup.alignCoordinates.review.warnNothing");

                return null;
            }
        }

        private string AnchorSummary()
        {
            if (_hostAnchorSource == AnchorSource.GridIntersection)
                return AppStrings.T("setup.alignCoordinates.review.gridPair", _hostGrid1 ?? "—", _hostGrid2 ?? "—", _hostLevel ?? "Z = 0");
            return AnchorLabel(_hostAnchorSource);
        }

        private string PointsSummary()
        {
            bool s = EffectiveMoveSurvey(), p = EffectiveMovePbp();
            if (s && p) return AppStrings.T("setup.alignCoordinates.review.pointsBoth");
            if (s) return AppStrings.T("setup.alignCoordinates.review.pointsSurvey");
            if (p) return AppStrings.T("setup.alignCoordinates.review.pointsPbp");
            return AppStrings.T("setup.alignCoordinates.review.pointsNone");
        }

        private string LinksSummary()
        {
            int selected = _linkSpecs.Values.Count(s => s.Selected);
            return selected == 0
                ? AppStrings.T("setup.alignCoordinates.review.linksNone")
                : AppStrings.T("setup.alignCoordinates.review.linksValue", selected);
        }

        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "host":
                    if (_hostAnchorSource == AnchorSource.GridIntersection)
                    {
                        if (_data.HostGridNames.Count < 2) return false;
                        if (_hostGrid1 == null || _hostGrid2 == null || _hostGrid1 == _hostGrid2) return false;
                    }
                    return true;

                case "links":
                    var selected = _linkSpecs.Values.Where(s => s.Selected).ToList();
                    if (selected.Count == 0) return false;
                    foreach (var s in selected)
                    {
                        if (s.AnchorSource == AnchorSource.GridIntersection &&
                            (string.IsNullOrEmpty(s.Grid1Name) || string.IsNullOrEmpty(s.Grid2Name) || s.Grid1Name == s.Grid2Name))
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
                    int selected = _linkSpecs.Values.Count(s => s.Selected);
                    return selected == 0 ? "—"
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
            _runHandler.HostLevelName    = _hostLevel ?? "";
            _runHandler.MoveSurvey       = EffectiveMoveSurvey();
            _runHandler.MovePbp          = EffectiveMovePbp();
            _runHandler.Rotate           = _rotate;
            _runHandler.LinkSpecs        = _linkSpecs.Values.Select(s => new LinkAlignSpec
            {
                LinkInstId   = s.LinkInstId,
                LinkName     = s.LinkName,
                Selected     = s.Selected,
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
