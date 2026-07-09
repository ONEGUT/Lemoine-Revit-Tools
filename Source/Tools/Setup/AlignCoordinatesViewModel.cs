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
    /// overridden per link). Repositions the host's copy of each link only — use the separate
    /// "Push Coordinates to Links" tool to commit the correction into the linked files.
    /// </summary>
    public class AlignCoordinatesViewModel : IStepFlowTool, IReviewableTool, IToolCleanup
    {
        private const string AnchorInternalOriginLabel   = "Internal Origin (default)";
        private const string AnchorGridIntersectionLabel = "Grid Intersection";
        private const string ZInternalOriginLabel        = "Internal Origin (Z = 0)";
        private const string ZMatchedLevelLabel           = "Matched Level";

        public string Title    => "Align Coordinates";
        public string RunLabel => "Align in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("host",  "Alignment Method", required: true),
            new StepDefinition("links", "Links to Align",   required: false),
            new StepDefinition("run",   "Review & Run",     required: false),
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

            outer.Children.Add(Label("Alignment method"));
            var anchorSel = new SingleSelect
            {
                Items          = new List<string> { AnchorInternalOriginLabel, AnchorGridIntersectionLabel },
                SelectedItem   = _hostAnchorSource == AnchorSource.GridIntersection ? AnchorGridIntersectionLabel : AnchorInternalOriginLabel,
                AccessibleName = "Alignment method",
            };
            outer.Children.Add(anchorSel);
            outer.Children.Add(Dim("Anchors the host and every link to their own Internal Origin — no picking needed when the project was modeled the normal way."));

            _hostGridFieldsPanel = new StackPanel();
            outer.Children.Add(_hostGridFieldsPanel);

            outer.Children.Add(Label("Elevation (Z) method"));
            var zSel = new SingleSelect
            {
                Items          = new List<string> { ZInternalOriginLabel, ZMatchedLevelLabel },
                SelectedItem   = _hostZSource == ZSource.MatchedLevel ? ZMatchedLevelLabel : ZInternalOriginLabel,
                AccessibleName = "Elevation method",
            };
            outer.Children.Add(zSel);

            _hostLevelFieldsPanel = new StackPanel();
            outer.Children.Add(_hostLevelFieldsPanel);

            anchorSel.SelectionChanged += v =>
            {
                _hostAnchorSource = v == AnchorGridIntersectionLabel ? AnchorSource.GridIntersection : AnchorSource.InternalOrigin;
                RebuildHostGridFields();
                Changed();
            };
            zSel.SelectionChanged += v =>
            {
                _hostZSource = v == ZMatchedLevelLabel ? ZSource.MatchedLevel : ZSource.InternalOriginZ;
                RebuildHostLevelFields();
                Changed();
            };

            Divider(outer);
            outer.Children.Add(Label("Move which host point(s)"));
            var toggles = new ToggleSwitches { AccessibleName = "Host points to move" };
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "survey", Label = "Survey Point",       DefaultOn = _moveSurvey },
                new ToggleItem { Id = "pbp",    Label = "Project Base Point", DefaultOn = _movePbp },
            });
            toggles.StateChanged += st =>
            {
                _moveSurvey = st.TryGetValue("survey", out var s) && s;
                _movePbp    = st.TryGetValue("pbp",    out var p) && p;
                Changed();
            };
            outer.Children.Add(toggles);
            outer.Children.Add(Dim("Moves the chosen point(s) to the resolved host anchor."));

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
                _hostGridFieldsPanel.Children.Add(Dim("This document needs at least two grids to define an intersection."));
                return;
            }

            if (_hostGrid1 == null) _hostGrid1 = _data.HostGridNames[0];

            var wrap = SubFieldsBox();
            var inner = (StackPanel)wrap.Child;

            inner.Children.Add(Label("Grid 1"));
            var g1 = new SingleSelect { Items = _data.HostGridNames, SelectedItem = _hostGrid1, AccessibleName = "Grid 1" };
            inner.Children.Add(g1);

            var g2Label = Label("Grid 2");
            inner.Children.Add(g2Label);
            var g2Container = new StackPanel();
            inner.Children.Add(g2Container);

            void RebuildGrid2()
            {
                g2Container.Children.Clear();
                var candidates = CrossingGridNames(_data.HostGrids, _hostGrid1);
                if (candidates.Count == 0)
                {
                    g2Container.Children.Add(Dim($"No grids cross '{_hostGrid1}' — pick a different Grid 1."));
                    _hostGrid2 = null;
                    return;
                }
                if (_hostGrid2 == null || !candidates.Contains(_hostGrid2))
                    _hostGrid2 = candidates[0];

                var g2 = new SingleSelect { Items = candidates, SelectedItem = _hostGrid2, AccessibleName = "Grid 2" };
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
                _hostLevelFieldsPanel.Children.Add(Dim("This document has no levels."));
                return;
            }

            if (_hostLevel == null) _hostLevel = _data.HostLevelNames[0];

            var wrap = SubFieldsBox();
            var inner = (StackPanel)wrap.Child;

            inner.Children.Add(Label("Level"));
            var lvl = new SingleSelect { Items = _data.HostLevelNames, SelectedItem = _hostLevel, AccessibleName = "Level" };
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

            outer.Children.Add(Label($"Links ({_data.Links.Count} loaded)"));

            if (_data.Links.Count == 0)
            {
                outer.Children.Add(Dim("No loaded links found."));
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
                outer.Children.Add(Dim("Every loaded link is listed — uncheck a link to leave it out of this run."));
            }

            Divider(outer);
            var toggles = new ToggleSwitches { AccessibleName = "Alignment options" };
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "rotate",  Label = "Rotate to align orientation",
                    Desc = "Turn each link so its reference direction matches the host's.", DefaultOn = _rotate },
            });
            toggles.StateChanged += st =>
            {
                _rotate = st.TryGetValue("rotate", out var r) && r;
                Changed();
            };
            outer.Children.Add(toggles);
            outer.Children.Add(Dim("Use the separate \"Push Coordinates to Links\" tool to commit this into the linked files."));

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
                    badgeText.Text  = "Grid Intersection";
                    actionLink.Text = "Use default";
                }
                else
                {
                    badge.SetResourceReference(Border.BackgroundProperty,   "LemoineRaised");
                    badge.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorder");
                    badgeText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
                    badgeText.Text  = "Internal Origin";
                    actionLink.Text = "Override…";
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
                    overridePanel.Children.Add(Dim($"{info.Name} needs at least two grids for a Grid Intersection override."));
                    return;
                }

                if (string.IsNullOrEmpty(spec.Grid1Name)) spec.Grid1Name = grids[0];

                var fieldsGrid = new Grid();
                fieldsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                fieldsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
                fieldsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var col1 = new StackPanel();
                Grid.SetColumn(col1, 0);
                col1.Children.Add(Label("Grid 1"));
                var g1Sel = new SingleSelect { Items = grids, SelectedItem = spec.Grid1Name, AccessibleName = $"{info.Name} Grid 1" };
                fieldsGrid.Children.Add(col1);

                var col2 = new StackPanel();
                Grid.SetColumn(col2, 2);
                col2.Children.Add(Label("Grid 2"));
                var g2Container = new StackPanel();
                col2.Children.Add(g2Container);
                fieldsGrid.Children.Add(col2);

                void RebuildG2()
                {
                    g2Container.Children.Clear();
                    var candidates = CrossingGridNames(info.Grids, spec.Grid1Name);
                    if (candidates.Count == 0)
                    {
                        g2Container.Children.Add(Dim($"No grids in {info.Name} cross '{spec.Grid1Name}' — pick a different Grid 1."));
                        spec.Grid2Name = "";
                        return;
                    }
                    if (string.IsNullOrEmpty(spec.Grid2Name) || !candidates.Contains(spec.Grid2Name))
                        spec.Grid2Name = candidates[0];

                    var g2Sel = new SingleSelect { Items = candidates, SelectedItem = spec.Grid2Name, AccessibleName = $"{info.Name} Grid 2" };
                    g2Sel.SelectionChanged += v => { spec.Grid2Name = v ?? ""; Changed(); };
                    g2Container.Children.Add(g2Sel);
                }

                g1Sel.SelectionChanged += v => { spec.Grid1Name = v ?? ""; RebuildG2(); Changed(); };
                RebuildG2();

                overridePanel.Children.Add(fieldsGrid);
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

            return row;
        }

        // ── Review ──────────────────────────────────────────────────────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("anchor", "Alignment Method"), ("points", "Host Points"), ("links", "Links"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["anchor"] = AnchorSummary(),
            ["points"] = PointsSummary(),
            ["links"]  = LinksSummary(),
        };

        public IList<string>? ReviewChips => new[]
        {
            _rotate ? "rotate ✓" : "rotate ✗",
        };
        public string? ReviewNote => "Links are repositioned in the host only — use \"Push Coordinates to Links\" to commit this into the linked files.";

        public string? ReviewWarning
        {
            get
            {
                if (_hostAnchorSource == AnchorSource.GridIntersection && _hostGrid1 != null && _hostGrid1 == _hostGrid2)
                    return "Host Grid 1 and Grid 2 are the same — pick two different grids.";

                foreach (var spec in _linkSpecs.Values)
                    if (spec.Selected && spec.Overridden && !string.IsNullOrEmpty(spec.Grid1Name) && spec.Grid1Name == spec.Grid2Name)
                        return $"{spec.LinkName}: Grid 1 and Grid 2 are the same — pick two different grids.";

                return null;
            }
        }

        private string AnchorSummary()
        {
            string xy = _hostAnchorSource == AnchorSource.GridIntersection
                ? $"{_hostGrid1 ?? "—"} × {_hostGrid2 ?? "—"}"
                : "Internal Origin";
            string z = _hostZSource == ZSource.MatchedLevel
                ? $"Level '{_hostLevel ?? "—"}'"
                : "Internal Origin (Z = 0)";
            return $"{xy} · Z: {z}";
        }

        private string PointsSummary()
        {
            if (_moveSurvey && _movePbp) return "Survey + Project Base";
            if (_moveSurvey) return "Survey Point";
            if (_movePbp)    return "Project Base Point";
            return "None";
        }

        private string LinksSummary()
        {
            var selected = _linkSpecs.Values.Where(s => s.Selected).ToList();
            if (selected.Count == 0) return "None";
            int overridden = selected.Count(s => s.Overridden);
            return overridden == 0
                ? $"{selected.Count} link(s) · Internal Origin"
                : $"{selected.Count} link(s) · {overridden} overridden to Grid Intersection";
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
                    return $"{AnchorSummary()} · {PointsSummary()}";
                case "links":
                    var selected = _linkSpecs.Values.Where(s => s.Selected).ToList();
                    return selected.Count == 0 ? "—"
                        : $"{LinksSummary()} · rotate {(_rotate ? "✓" : "✗")}";
                case "run": return "Ready to run";
                default:    return "—";
            }
        }

        public void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null) { pushLog("Run handler not registered.", "fail"); onComplete(0, 1, 0); return; }

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
            }).ToList();
            _runHandler.PushLog     = pushLog;
            _runHandler.OnProgress  = onProgress;
            _runHandler.OnComplete  = onComplete;

            pushLog("Raising Revit ExternalEvent…", "info");
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
