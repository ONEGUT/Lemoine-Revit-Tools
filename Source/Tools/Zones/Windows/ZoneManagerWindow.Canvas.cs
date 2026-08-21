using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LemoineTools.Framework;
using LemoineTools.Framework.Zones;

using WpfGrid       = System.Windows.Controls.Grid;
using WpfVisibility = System.Windows.Visibility;

namespace LemoineTools.Tools.Zones.Windows
{
    // =========================================================================
    // The canvas column — breadcrumb, Plan|Sheet toggle, the drawing, and the
    // overlays that sit on top of it.
    //
    // The canvas is READ-ONLY. One click selects; it never edits geometry.
    // Everything it draws comes from the launch snapshot plus the zone library
    // — there is no further model access, and there cannot be.
    // =========================================================================
    public partial class ZoneManagerWindow
    {
        private WpfGrid?  _emptyStateHost;
        private Border?   _legendBox;
        private StackPanel? _chipRow;

        /// <summary>Tolerance for calling two area edges "the same edge", in model feet.</summary>
        private const double MatchlineTolFt = 0.5;

        // ── Host ─────────────────────────────────────────────────────────────

        private void BuildCanvas()
        {
            _canvas = new ZonePlanCanvas();
            _canvas.ItemClicked += OnCanvasItemClicked;
            _planHost.Children.Add(_canvas);

            // Overlays, painted over the drawing in this order: legend (bottom-left),
            // chips (top-right), empty state (centre, only when there is nothing to draw).
            _legendBox = new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
            _planHost.Children.Add(_legendBox);

            _chipRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Top,
            };
            _planHost.Children.Add(_chipRow);

            _emptyStateHost = new WpfGrid { Visibility = WpfVisibility.Collapsed };
            _planHost.Children.Add(_emptyStateHost);
        }

        /// <summary>A click on the plan selects, exactly as a click on the navigator row does.</summary>
        private void OnCanvasItemClicked(string hitId)
        {
            if (string.IsNullOrEmpty(hitId)) return;
            Select(hitId);
        }

        // ── Repaint ──────────────────────────────────────────────────────────

        /// <summary>
        /// Rebuilds the breadcrumb, the drawing and the overlays for the current selection.
        /// Called from every path that changes the selection, the mode or the library.
        /// </summary>
        private void RepaintCanvas()
        {
            if (_canvas == null) return;

            BuildBreadcrumb();

            bool empty = Lib.Areas.Count == 0 && Lib.SheetSets.Count == 0;
            ShowEmptyState(empty);

            if (empty)
            {
                _canvas.SetScene(null);
                if (_legendBox != null) _legendBox.Visibility = WpfVisibility.Collapsed;
                if (_chipRow   != null) _chipRow.Visibility   = WpfVisibility.Collapsed;
                return;
            }

            if (_legendBox != null) _legendBox.Visibility = WpfVisibility.Visible;
            if (_chipRow   != null) _chipRow.Visibility   = WpfVisibility.Visible;

            if (_mode == CanvasMode.Sheet) PaintSheet();
            else                           PaintPlan();
        }

        private void PaintPlan()
        {
            var level = ResolveActiveLevel();
            var scene = BuildPlanScene(level);
            _canvas!.SetScene(scene);

            BuildLegend(plan: true);
            BuildPlanChips(level);
        }

        /// <summary>
        /// The level the canvas is drawing: the active one, else the level of whatever is
        /// selected, else the first that has areas.
        /// </summary>
        private ZoneLevel? ResolveActiveLevel()
        {
            var lv = string.IsNullOrEmpty(_activeLevelId) ? null : Lib.Level(_activeLevelId);
            if (lv != null) return lv;

            var building = ActiveBuilding();
            return Lib.Levels
                      .Where(l => building == null || (l.BuildingId ?? "") == building.Id)
                      .OrderByDescending(l => l.ElevationFt)
                      .FirstOrDefault();
        }

        // ── Plan scene ───────────────────────────────────────────────────────

        /// <summary>
        /// Builds the plan drawing for one level: the slab outline, the areas over it, their
        /// scope boxes, anchors, labels, the matchlines where they meet, and selection handles.
        ///
        /// A level with NO captured outline still draws its areas — the snapshot is a contract,
        /// not a precondition, and an empty surface with the right rectangles on it beats a
        /// blank pane that says nothing.
        /// </summary>
        private ZoneCanvasScene BuildPlanScene(ZoneLevel? level)
        {
            var scene = new ZoneCanvasScene { InsetPx = AppSettings.Instance.S(14) };
            if (level == null) return scene;

            var st = AppSettings.Instance;
            var areas = AreasOn(level).Where(a => a.HasExtents).ToList();

            // ── Building outline: ONE closed ring per loop, so an L-shaped floor reads as a
            // single slab rather than as two overlapping boxes.
            var outline = _snapshot.ForLevel(level.Id);
            if (outline != null && outline.HasOutline)
            {
                var poly = new ZoneCanvasPoly
                {
                    Style = ZoneCanvasStyle.Filled(ZoneCanvasInk.Surface, 1.0, ZoneCanvasInk.Sub, 1.0, st.S(2.0)),
                };
                foreach (var ring in outline.Rings)
                {
                    if (ring.Count < 3) continue;
                    var pts = ring.Select(p => new ZoneCanvasPoint(p.X, p.Y)).ToList();
                    poly.Rings.Add(pts);
                    foreach (var p in pts) scene.Include(p.X, p.Y);
                }
                if (poly.Rings.Count > 0) scene.Add(poly);
            }

            // ── Scope boxes, under the areas: the extents an area was cut from.
            foreach (var a in areas)
            {
                if (a.Definition != ZoneExtentMode.ScopeBox || string.IsNullOrEmpty(a.ScopeBoxName)) continue;

                var box = _scopeBoxes.FirstOrDefault(
                    b => b.HasBounds && string.Equals(b.Name, a.ScopeBoxName, StringComparison.OrdinalIgnoreCase));
                if (box == null) continue;

                scene.Add(new ZoneCanvasRect
                {
                    MinX = box.MinX, MinY = box.MinY, MaxX = box.MaxX, MaxY = box.MaxY,
                    Style = ZoneCanvasStyle.Dashed(ZoneCanvasInk.Dim, st.S(1.0),
                                                   new[] { st.S(4), st.S(3) }),
                });
                scene.IncludeRect(box.MinX, box.MinY, box.MaxX, box.MaxY);
            }

            // ── Areas.
            foreach (var a in areas)
            {
                bool sel = _selected == Key(KindArea, a.Id);
                string dims = AppStrings.T("zones.manager.canvas.dims",
                                           $"{a.WidthFt:0.#}", $"{a.DepthFt:0.#}");

                scene.Add(new ZoneCanvasRect
                {
                    MinX = a.MinX, MinY = a.MinY, MaxX = a.MaxX, MaxY = a.MaxY,
                    Style = sel
                        ? ZoneCanvasStyle.Filled(ZoneCanvasInk.Accent, 0.20, ZoneCanvasInk.Accent, 1.00, st.S(1.6))
                        : ZoneCanvasStyle.Filled(ZoneCanvasInk.Accent, 0.07, ZoneCanvasInk.Accent, 0.55, st.S(1.0)),
                    HitId     = Key(KindArea, a.Id),
                    HoverName = a.Name,
                    HoverDims = dims,
                });
                scene.IncludeRect(a.MinX, a.MinY, a.MaxX, a.MaxY);

                scene.Add(new ZoneCanvasText
                {
                    X = a.MinX, Y = a.MaxY, OffsetXPx = st.S(6), OffsetYPx = st.S(4),
                    Text = a.Name, FontSizePx = st.S(7.5), Bold = sel,
                    Ink = ZoneCanvasInk.Text,
                });
                scene.Add(new ZoneCanvasText
                {
                    X = a.MinX, Y = a.MaxY, OffsetXPx = st.S(6), OffsetYPx = st.S(13),
                    Text = dims, FontSizePx = st.S(6),
                    Ink = sel ? ZoneCanvasInk.Accent : ZoneCanvasInk.Dim,
                });

                double ax = a.HasAnchor ? a.AnchorX : (a.MinX + a.MaxX) / 2.0;
                double ay = a.HasAnchor ? a.AnchorY : (a.MinY + a.MaxY) / 2.0;
                scene.Add(new ZoneCanvasCross
                {
                    X = ax, Y = ay, ArmPx = st.S(3),
                    Style = ZoneCanvasStyle.Solid(sel ? ZoneCanvasInk.Accent : ZoneCanvasInk.Dim,
                                                  sel ? st.S(1.0) : st.S(0.8)),
                });
            }

            // ── Matchlines where two areas share an edge.
            foreach (var seg in SharedEdges(areas))
                scene.Add(seg);

            // ── Selection handles last, so they are never drawn under another area.
            var selArea = areas.FirstOrDefault(a => _selected == Key(KindArea, a.Id));
            if (selArea != null)
            {
                foreach (var (hx, hy) in new[]
                {
                    (selArea.MinX, selArea.MinY), (selArea.MaxX, selArea.MinY),
                    (selArea.MinX, selArea.MaxY), (selArea.MaxX, selArea.MaxY),
                })
                {
                    scene.Add(new ZoneCanvasHandle
                    {
                        X = hx, Y = hy, SizePx = st.S(5),
                        Style = new ZoneCanvasStyle { Fill = ZoneCanvasInk.Accent, FillAlpha = 1.0 },
                    });
                }
            }

            return scene;
        }

        /// <summary>
        /// Matchlines, derived rather than read: two areas that share an edge are drawn as one
        /// dashed line along the overlap. The Revit-side ZoneMatchlines collector needs a
        /// Document, which this window does not have — but the library already says where every
        /// area's boundary is, and that is what a matchline follows.
        /// </summary>
        private IEnumerable<ZoneCanvasLine> SharedEdges(List<ZoneArea> areas)
        {
            var st = AppSettings.Instance;

            // 7-3-2-3 at 1.2px, the design's chain-dash. Absolute px here; the renderer divides
            // by thickness for WPF's multiples-of-thickness dash array.
            var style = ZoneCanvasStyle.Dashed(ZoneCanvasInk.Green, st.S(1.2),
                                               new[] { st.S(7), st.S(3), st.S(2), st.S(3) });

            for (int i = 0; i < areas.Count; i++)
            for (int j = i + 1; j < areas.Count; j++)
            {
                var a = areas[i];
                var b = areas[j];

                // Vertical shared edge: a's right meets b's left (or the reverse).
                foreach (double x in new[] { a.MaxX, a.MinX })
                {
                    if (Math.Abs(x - b.MinX) > MatchlineTolFt && Math.Abs(x - b.MaxX) > MatchlineTolFt) continue;

                    double y0 = Math.Max(a.MinY, b.MinY);
                    double y1 = Math.Min(a.MaxY, b.MaxY);
                    if (y1 - y0 <= MatchlineTolFt) continue;

                    yield return new ZoneCanvasLine { X0 = x, Y0 = y0, X1 = x, Y1 = y1, Style = style };
                    break;
                }

                // Horizontal shared edge.
                foreach (double y in new[] { a.MaxY, a.MinY })
                {
                    if (Math.Abs(y - b.MinY) > MatchlineTolFt && Math.Abs(y - b.MaxY) > MatchlineTolFt) continue;

                    double x0 = Math.Max(a.MinX, b.MinX);
                    double x1 = Math.Min(a.MaxX, b.MaxX);
                    if (x1 - x0 <= MatchlineTolFt) continue;

                    yield return new ZoneCanvasLine { X0 = x0, Y0 = y, X1 = x1, Y1 = y, Style = style };
                    break;
                }
            }
        }

        // ── Breadcrumb ───────────────────────────────────────────────────────

        private void BuildBreadcrumb()
        {
            var st = AppSettings.Instance;
            _crumbPanel.Children.Clear();
            _crumbRight.Children.Clear();
            _crumbBorder.Padding = st.Th(12, 0, 12, 0);

            bool empty = Lib.Areas.Count == 0 && Lib.SheetSets.Count == 0;

            if (empty)
            {
                // Ghost crumbs — the shape of the path the user is about to create.
                AddCrumb(AppStrings.T("zones.manager.node.building"), ghost: true, last: false, onClick: null);
                AddCrumbSep(ghost: true);
                AddCrumb(AppStrings.T("zones.manager.node.level"), ghost: true, last: false, onClick: null);
                AddCrumbSep(ghost: true);
                AddCrumb(AppStrings.T("zones.manager.node.area"), ghost: true, last: false, onClick: null);
            }
            else if (_mode == CanvasMode.Sheet)
            {
                var set   = ResolveActiveSheetSet();
                var group = ResolveActiveGroup(set);

                AddCrumb(AppStrings.T("zones.manager.nav.sheets"), ghost: false, last: false, onClick: null);
                if (set != null)
                {
                    AddCrumbSep(ghost: false);
                    AddCrumb(set.Name, ghost: false, last: group == null,
                             onClick: () => Select(Key(KindSheetSet, set.Id)), menu: true);
                }
                if (set != null && group != null)
                {
                    AddCrumbSep(ghost: false);
                    AddCrumb(GroupLabel(set, group), ghost: false, last: true,
                             onClick: () => Select(Key(KindSheetGroup, set.Id, group.Id)), menu: true);
                }

                // The governing level, so a sheet is never read without knowing what it shows.
                var lvl = ResolveActiveLevel();
                if (lvl != null) AddCrumbRightLabel(lvl.Name);
            }
            else
            {
                var building = ActiveBuilding();
                var level    = ResolveActiveLevel();
                var area     = SelectedArea();

                if (building != null)
                    AddCrumb(building.Name, ghost: false, last: level == null,
                             onClick: () => Select(Key(KindBuilding, building.Id)));

                if (level != null)
                {
                    if (building != null) AddCrumbSep(ghost: false);
                    AddCrumb(level.Name, ghost: false, last: area == null,
                             onClick: () => Select(Key(KindLevel, level.Id)), menu: true);
                }

                if (area != null)
                {
                    AddCrumbSep(ghost: false);
                    AddCrumb(area.Name, ghost: false, last: true,
                             onClick: () => Select(Key(KindArea, area.Id)), menu: true);
                }
            }

            BuildModeToggle(disabled: empty);
        }

        private ZoneArea? SelectedArea()
        {
            if (!_selected.StartsWith(KindArea + ":", StringComparison.Ordinal)) return null;
            return Lib.Area(_selected.Substring(KindArea.Length + 1));
        }

        private void AddCrumb(string text, bool ghost, bool last, Action? onClick, bool menu = false)
        {
            var st = AppSettings.Instance;

            var tb = new TextBlock
            {
                Text = menu && !ghost ? text + " " + char.ConvertFromUtf32(0x02DC) : text,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
            };
            tb.FontFamily = st.ActiveTheme.MonoFont;
            tb.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_SM");

            // The palette carries no "disabled" tier, so a ghost crumb is the dim ink faded —
            // the same idiom the design uses for every other inert control in this window.
            if (ghost)     { tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                             tb.Opacity = 0.55; }
            else if (last) { tb.FontWeight = FontWeights.SemiBold;
                             tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText"); }
            else            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");

            if (onClick != null)
            {
                tb.Cursor = Cursors.Hand;
                tb.MouseLeftButtonUp += (s, e) =>
                {
                    try { onClick(); }
                    catch (Exception ex) { DiagnosticsLog.Error("ZoneManagerWindow: breadcrumb", ex); }
                    e.Handled = true;
                };
            }
            _crumbPanel.Children.Add(tb);
        }

        private void AddCrumbSep(bool ghost)
        {
            var st = AppSettings.Instance;
            var tb = new TextBlock
            {
                Text = char.ConvertFromUtf32(0x203A),   // single right angle quote
                Margin = st.Th(7, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            tb.FontFamily = st.ActiveTheme.MonoFont;
            tb.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineBorder");
            if (ghost) tb.Opacity = 0.55;
            _crumbPanel.Children.Add(tb);
        }

        private void AddCrumbRightLabel(string text)
        {
            var st = AppSettings.Instance;
            var tb = new TextBlock
            {
                Text = text,
                Margin = st.Th(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            tb.FontFamily = st.ActiveTheme.MonoFont;
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_Meta");
            _crumbRight.Children.Add(tb);
        }

        /// <summary>The Plan | Sheet segmented control at the right of the breadcrumb.</summary>
        private void BuildModeToggle(bool disabled)
        {
            var st = AppSettings.Instance;

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(ModeSegment(AppStrings.T("zones.manager.canvas.plan"),
                                         active: _mode == CanvasMode.Plan, first: true,
                                         onClick: () => SetMode(CanvasMode.Plan)));
            row.Children.Add(ModeSegment(AppStrings.T("zones.manager.canvas.sheet"),
                                         active: _mode == CanvasMode.Sheet, first: false,
                                         onClick: () => SetMode(CanvasMode.Sheet)));

            var box = new Border
            {
                BorderThickness = new Thickness(1),
                Child = row,
                Opacity = disabled ? 0.45 : 1.0,
                IsEnabled = !disabled,
            };
            box.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_SM");
            box.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorder");
            _crumbRight.Children.Add(box);
        }

        private UIElement ModeSegment(string label, bool active, bool first, Action onClick)
        {
            var st = AppSettings.Instance;

            var tb = new TextBlock { Text = label, Background = Brushes.Transparent };
            tb.FontFamily = st.ActiveTheme.MonoFont;
            tb.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_SM");
            if (active) tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            else        tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");

            var b = new Border
            {
                Padding = st.Th(10, 3, 10, 3),
                // The 1px rule BETWEEN the segments, on the second one only.
                BorderThickness = first ? new Thickness(0) : new Thickness(1, 0, 0, 0),
                Cursor = Cursors.Hand,
                Child = tb,
                Background = Brushes.Transparent,
            };
            if (!first) b.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            if (active) b.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");

            b.MouseLeftButtonUp += (s, e) =>
            {
                try { onClick(); }
                catch (Exception ex) { DiagnosticsLog.Error("ZoneManagerWindow: canvas mode", ex); }
                e.Handled = true;
            };
            return b;
        }

        private void SetMode(CanvasMode mode)
        {
            if (_mode == mode) return;
            _mode = mode;
            RepaintCanvas();
        }

        // ── Overlays ─────────────────────────────────────────────────────────

        private void BuildLegend(bool plan)
        {
            var st = AppSettings.Instance;
            if (_legendBox == null) return;

            var rows = new StackPanel();

            void Row(ZoneCanvasInk ink, double thickness, double[]? dash, bool cross, string label)
            {
                var r = new StackPanel { Orientation = Orientation.Horizontal, Margin = st.Th(0, 2, 0, 2) };

                if (cross)
                {
                    var plus = new TextBlock
                    {
                        Text = "+",
                        Width = st.S(14),
                        TextAlignment = TextAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    plus.FontFamily = st.ActiveTheme.MonoFont;
                    plus.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                    plus.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_Meta");
                    r.Children.Add(plus);
                }
                else
                {
                    var line = new System.Windows.Shapes.Line
                    {
                        X1 = 0, Y1 = 0, X2 = st.S(14), Y2 = 0,
                        StrokeThickness = thickness,
                        Stroke = ZoneCanvasStyle.Resolve(ink, 1.0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = st.Th(0, 0, 0, 0),
                    };
                    if (dash != null)
                    {
                        var dc = new DoubleCollection();
                        foreach (double d in dash) dc.Add(d / Math.Max(0.01, thickness));
                        line.StrokeDashArray = dc;
                    }
                    var holder = new WpfGrid { Width = st.S(14), VerticalAlignment = VerticalAlignment.Center };
                    holder.Children.Add(line);
                    r.Children.Add(holder);
                }

                var t = new TextBlock { Text = label, Margin = st.Th(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                t.FontFamily = st.ActiveTheme.MonoFont;
                t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
                t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_Meta");
                r.Children.Add(t);

                rows.Children.Add(r);
            }

            if (plan)
            {
                Row(ZoneCanvasInk.Sub,    st.S(2), null, false, AppStrings.T("zones.manager.legend.outline"));
                Row(ZoneCanvasInk.Accent, st.S(2), null, false, AppStrings.T("zones.manager.legend.extents"));
                Row(ZoneCanvasInk.Dim,    st.S(1), new[] { st.S(4), st.S(3) }, false, AppStrings.T("zones.manager.legend.scopeBox"));
                Row(ZoneCanvasInk.Green,  st.S(2), new[] { st.S(6), st.S(3) }, false, AppStrings.T("zones.manager.legend.matchline"));
                Row(ZoneCanvasInk.Dim,    0, null, true, AppStrings.T("zones.manager.legend.anchor"));
            }
            else
            {
                Row(ZoneCanvasInk.Accent, st.S(2), null, false, AppStrings.T("zones.manager.legend.placedView"));
                Row(ZoneCanvasInk.Border, st.S(1), new[] { st.S(5), st.S(4) }, false, AppStrings.T("zones.manager.legend.drawingArea"));
                Row(ZoneCanvasInk.Green,  st.S(2), new[] { st.S(6), st.S(3) }, false, AppStrings.T("zones.manager.legend.matchline"));
                Row(ZoneCanvasInk.Dim,    0, null, true, AppStrings.T("zones.manager.legend.anchor"));
            }

            _legendBox.Child   = rows;
            _legendBox.Margin  = st.Th(14, 0, 0, 12);
            _legendBox.Padding = st.Th(10, 8, 10, 8);
            _legendBox.BorderThickness = new Thickness(1);
            _legendBox.Opacity = 0.8;   // the design's ~80% alpha panel
            _legendBox.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Chip");
            _legendBox.SetResourceReference(Border.BackgroundProperty,   "LemoineBg");
            _legendBox.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorder");
        }

        private void BuildPlanChips(ZoneLevel? level)
        {
            if (_chipRow == null) return;
            var st = AppSettings.Instance;

            _chipRow.Children.Clear();
            _chipRow.Margin = st.Th(0, 12, 14, 0);

            string levelName = level?.Name ?? "";
            int scale = level?.ViewDefs?.FirstOrDefault()?.Scale ?? 0;

            string text = scale > 0
                ? AppStrings.T("zones.manager.canvas.scaleChip", scale, levelName)
                : levelName;

            if (!string.IsNullOrEmpty(text))
                _chipRow.Children.Add(CanvasChip(text, "LemoineTextDim", "LemoineBorder"));

            // A level whose outline could not be captured draws its areas over an empty
            // surface — by design. But the user must be told WHY the building vanished, or an
            // unreadable level is indistinguishable from a level with nothing on it. The
            // reason is already in diagnostics; this is the same fact, on screen.
            if (level != null)
            {
                var outline = _snapshot.ForLevel(level.Id);
                if (outline == null || !outline.HasOutline)
                    _chipRow.Children.Add(CanvasChip(AppStrings.T("zones.manager.canvas.noOutline"),
                                                     "LemoineTextDim", "LemoineBorder"));
            }
        }

        /// <summary>A translucent capsule over the drawing — scale, level, fit verdict.</summary>
        private Border CanvasChip(string text, string fgKey, string borderKey)
        {
            var st = AppSettings.Instance;

            var t = new TextBlock { Text = text, Background = Brushes.Transparent };
            t.FontFamily = st.ActiveTheme.MonoFont;
            t.SetResourceReference(TextBlock.ForegroundProperty, fgKey);
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_Meta");

            var b = new Border
            {
                Padding = st.Th(9, 3, 9, 3),
                Margin  = st.Th(5, 0, 0, 0),
                BorderThickness = new Thickness(1),
                Opacity = 0.8,
                Child = t,
            };
            b.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Chip");
            b.SetResourceReference(Border.BackgroundProperty,   "LemoineBg");
            b.SetResourceReference(Border.BorderBrushProperty,  borderKey);
            return b;
        }

        // ── Empty state ──────────────────────────────────────────────────────

        /// <summary>
        /// What opens on a project that has never run Discover. The canvas carries the first
        /// move rather than leaving the user to find a toolbar button.
        /// </summary>
        private void ShowEmptyState(bool show)
        {
            if (_emptyStateHost == null) return;

            _emptyStateHost.Visibility = show ? WpfVisibility.Visible : WpfVisibility.Collapsed;
            if (!show) { _emptyStateHost.Children.Clear(); return; }
            if (_emptyStateHost.Children.Count > 0) return;   // already built

            var st = AppSettings.Instance;

            var col = new StackPanel
            {
                MaxWidth = st.S(404),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };

            // A dashed sketch of a building with an anchor cross — drawn, never an image asset.
            col.Children.Add(BuildEmptySketch());

            var title = new TextBlock
            {
                Text = AppStrings.T("zones.manager.empty.title"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = st.Th(0, 0, 0, 8),
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            title.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_LG");
            col.Children.Add(title);

            var body = new TextBlock
            {
                Text = AppStrings.T("zones.manager.empty.body"),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                LineHeight = st.S(20),
                Margin = st.Th(0, 0, 0, 20),
            };
            body.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            body.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            col.Children.Add(body);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            buttons.Children.Add(EmptyStateButton(
                AppStrings.T("zones.manager.empty.discover") + " " + char.ConvertFromUtf32(0x2197),
                accent: true, onClick: OnDiscover));
            buttons.Children.Add(EmptyStateButton(
                char.ConvertFromUtf32(0xFF0B) + " " + AppStrings.T("zones.manager.empty.byHand"),
                accent: false, onClick: AddBuilding));
            col.Children.Add(buttons);

            var rule = new Border { Height = 1, Margin = st.Th(0, 24, 0, 16) };
            rule.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
            col.Children.Add(rule);

            var foot = new TextBlock
            {
                Text = AppStrings.T("zones.manager.empty.footnote"),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                FontStyle = FontStyles.Italic,
            };
            foot.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            foot.Opacity = 0.55;
            foot.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            col.Children.Add(foot);

            _emptyStateHost.Children.Add(col);
        }

        private UIElement BuildEmptySketch()
        {
            var st = AppSettings.Instance;
            double w = st.S(160), h = st.S(100);

            var canvas = new Canvas
            {
                Width = w, Height = h,
                Margin = st.Th(0, 0, 0, 22),
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            // The same L-shaped ring the real plan draws, at sketch scale.
            var figure = new PathFigure
            {
                StartPoint = new Point(w * 0.09, h * 0.14),
                IsClosed = true,
            };
            foreach (var p in new[]
            {
                new Point(w * 0.91, h * 0.14), new Point(w * 0.91, h * 0.56),
                new Point(w * 0.59, h * 0.56), new Point(w * 0.59, h * 0.88),
                new Point(w * 0.09, h * 0.88),
            })
                figure.Segments.Add(new LineSegment(p, true));

            var geo = new PathGeometry();
            geo.Figures.Add(figure);

            var outline = new System.Windows.Shapes.Path
            {
                Data = geo,
                StrokeThickness = st.S(1.6),
                StrokeDashArray = new DoubleCollection { 6 / 1.6, 5 / 1.6 },
                StrokeLineJoin = PenLineJoin.Round,
            };
            outline.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "LemoineBorder");
            outline.SetResourceReference(System.Windows.Shapes.Path.FillProperty,   "LemoineBg");
            outline.Opacity = 0.6;
            canvas.Children.Add(outline);

            var cross = new GeometryGroup();
            cross.Children.Add(new LineGeometry(new Point(w * 0.46, h * 0.40), new Point(w * 0.54, h * 0.40)));
            cross.Children.Add(new LineGeometry(new Point(w * 0.50, h * 0.34), new Point(w * 0.50, h * 0.46)));

            var crossPath = new System.Windows.Shapes.Path { Data = cross, StrokeThickness = st.S(1.2) };
            crossPath.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "LemoineBorder");
            crossPath.Opacity = 0.6;
            canvas.Children.Add(crossPath);

            return canvas;
        }

        private UIElement EmptyStateButton(string label, bool accent, Action onClick)
        {
            var st = AppSettings.Instance;

            var t = new TextBlock { Text = label, Background = Brushes.Transparent };
            t.FontFamily = st.ActiveTheme.MonoFont;
            t.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_MD");
            if (accent) t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            else        t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");

            var b = new Border
            {
                Padding = st.Th(16, 8, 16, 8),
                Margin  = st.Th(4, 0, 4, 0),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child = t,
                Background = Brushes.Transparent,
            };
            b.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Chip");
            if (accent)
            {
                b.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                b.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
            }
            else b.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            b.MouseLeftButtonUp += (s, e) =>
            {
                try { onClick(); }
                catch (Exception ex) { DiagnosticsLog.Error($"ZoneManagerWindow: empty state '{label}'", ex); }
                e.Handled = true;
            };
            return b;
        }
    }
}
