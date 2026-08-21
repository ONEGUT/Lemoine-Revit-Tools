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
    // The navigator — the 180px left column.
    //
    // TWO LEVELS DEEP, deliberately: levels with their areas, sheet sizes with
    // their groups. View definitions are NOT in this tree; they live in the
    // properties pane. The old window nested buildings → levels → areas → view
    // definitions → per-area overrides, which meant the thing you were looking
    // for was usually behind two carets.
    //
    // Only the ACTIVE level expands, and only the ACTIVE sheet size — this is a
    // navigator, not a tree, so there is no per-node expansion set to keep in
    // step with the selection.
    // =========================================================================
    public partial class ZoneManagerWindow
    {
        // Node keys are "kind:id" / "kind:parent/child" — logic tokens, never externalized.
        private static string Key(string kind, string id) => kind + ":" + id;
        private static string Key(string kind, string parentId, string childId)
            => kind + ":" + parentId + "/" + childId;

        /// <summary>The building whose levels the navigator lists — the active one, else the first.</summary>
        private ZoneBuilding? ActiveBuilding()
        {
            if (!string.IsNullOrEmpty(_activeBuildingId))
            {
                var b = Lib.Building(_activeBuildingId);
                if (b != null) return b;
            }
            return Lib.Buildings.OrderBy(x => x.SortIndex).FirstOrDefault();
        }

        // ── Build ────────────────────────────────────────────────────────────

        /// <summary>Rebuilds the whole navigator column. Named RebuildTree because every
        /// edit path in this window already calls it after changing the library.</summary>
        private void RebuildTree()
        {
            _listStack.Children.Clear();

            var building = ActiveBuilding();
            _activeBuildingId = building?.Id ?? "";

            BuildBuildingSelector(building);

            // ── LEVELS ──
            _listStack.Children.Add(NavSectionHeader(AppStrings.T("zones.manager.nav.levels")));

            var levels = Lib.Levels
                .Where(l => building == null || (l.BuildingId ?? "") == building.Id)
                .OrderByDescending(l => l.ElevationFt)
                .ThenBy(l => l.Name, NaturalOrderComparer.OrdinalIgnoreCase)
                .ToList();

            if (levels.Count == 0)
            {
                _listStack.Children.Add(NavNote(AppStrings.T("zones.manager.nav.noZones")));
            }
            else
            {
                foreach (var lv in levels)
                {
                    _listStack.Children.Add(NavParentRow(
                        Key(KindLevel, lv.Id), lv.Name, FormatElevation(lv.ElevationFt),
                        () => { _activeLevelId = lv.Id; _mode = CanvasMode.Plan; Select(Key(KindLevel, lv.Id)); }));

                    if (lv.Id != _activeLevelId) continue;

                    foreach (var area in AreasOn(lv))
                    {
                        _listStack.Children.Add(NavChildRow(
                            Key(KindArea, area.Id), area.Name,
                            problem: AreaHasProblem(area), ok: false,
                            onClick: () => { _mode = CanvasMode.Plan; Select(Key(KindArea, area.Id)); }));
                    }

                    _listStack.Children.Add(NavAddButton(
                        AppStrings.T("zones.manager.actions.addArea"), indent: true, onAdd: () => AddArea(lv)));
                }
            }

            _listStack.Children.Add(NavAddButton(
                Lib.Buildings.Count == 0
                    ? AppStrings.T("zones.manager.actions.addBuilding")
                    : AppStrings.T("zones.manager.actions.addLevel"),
                indent: false,
                onAdd: () =>
                {
                    if (Lib.Buildings.Count == 0) AddBuilding();
                    else AddLevel(_activeBuildingId);
                }));

            // ── SHEETS ──
            _listStack.Children.Add(NavDivider());
            _listStack.Children.Add(NavSectionHeader(AppStrings.T("zones.manager.nav.sheets")));

            if (Lib.SheetSets.Count == 0)
            {
                _listStack.Children.Add(NavNote(AppStrings.T("zones.manager.nav.noSheetSizes")));
            }
            else
            {
                foreach (var set in Lib.SheetSets.OrderBy(y => y.SortIndex))
                {
                    _listStack.Children.Add(NavParentRow(
                        Key(KindSheetSet, set.Id), set.Name, (set.Groups?.Count ?? 0).ToString(),
                        () => { _activeSheetSetId = set.Id; _mode = CanvasMode.Sheet; Select(Key(KindSheetSet, set.Id)); }));

                    if (set.Id != _activeSheetSetId) continue;

                    foreach (var g in (set.Groups ?? new List<ZoneSheetGroup>()).OrderBy(g => g.SortIndex))
                    {
                        var solved = SolveGroupPreview(set, g);
                        bool fits = solved != null && solved.Fits;

                        _listStack.Children.Add(NavChildRow(
                            Key(KindSheetGroup, set.Id, g.Id), GroupLabel(set, g),
                            problem: solved != null && !fits, ok: fits,
                            onClick: () => { _mode = CanvasMode.Sheet; Select(Key(KindSheetGroup, set.Id, g.Id)); }));
                    }

                    _listStack.Children.Add(NavAddButton(
                        AppStrings.T("zones.manager.actions.addGroup"), indent: true, onAdd: () => AddGroup(set)));
                }
            }

            _listStack.Children.Add(NavAddButton(
                AppStrings.T("zones.manager.actions.addSheetSet"), indent: false, onAdd: AddSheetSet));
        }

        /// <summary>Areas that exist on a level, in the navigator's order.</summary>
        private List<ZoneArea> AreasOn(ZoneLevel lv)
            => Lib.Areas
                  .Where(a => a.AppliesToLevelIds == null || a.AppliesToLevelIds.Count == 0
                              || a.AppliesToLevelIds.Contains(lv.Id))
                  .Where(a => string.IsNullOrEmpty(a.BuildingId)
                              || string.IsNullOrEmpty(lv.BuildingId)
                              || a.BuildingId == lv.BuildingId)
                  .OrderBy(a => a.SortIndex)
                  .ThenBy(a => a.Name, NaturalOrderComparer.OrdinalIgnoreCase)
                  .ToList();

        /// <summary>
        /// Whether an area gets the red dot: no resolved extents, a scope box this document
        /// does not have, or a box that was resized under it since it was adopted.
        /// </summary>
        private bool AreaHasProblem(ZoneArea a)
            => a != null && (!a.HasExtents || _missingBoxAreas.Contains(a.Id) || _boxDrift.ContainsKey(a.Id));

        private string GroupLabel(ZoneSheetSet set, ZoneSheetGroup g)
            => string.IsNullOrEmpty(g.Suffix)
                 ? AppStrings.T("zones.manager.nav.groupUnnamed")
                 : AppStrings.T("zones.manager.nav.groupLabel", g.Suffix);

        /// <summary>Sets the one selection field and repaints everything that reads it.</summary>
        private void Select(string key)
        {
            _selected = key ?? "";
            RebuildTree();
            RebuildDetail();
            RepaintCanvas();
        }

        private static string FormatElevation(double ft) => $"{ft:0.##}'";

        // ── Row builders ─────────────────────────────────────────────────────

        /// <summary>The building capsule at the top of the navigator. Inert when there is none.</summary>
        private void BuildBuildingSelector(ZoneBuilding? building)
        {
            var st = AppSettings.Instance;

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = Brushes.Transparent,
            };

            var name = new TextBlock
            {
                Text = building?.Name ?? AppStrings.T("zones.manager.nav.noBuilding"),
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
            };
            name.FontFamily = st.ActiveTheme.MonoFont;
            name.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_SM");
            if (building != null) name.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            else                  name.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            row.Children.Add(name);

            if (building != null)
            {
                var chev = new TextBlock
                {
                    Text = char.ConvertFromUtf32(0x02DC),   // small tilde — the house "opens a menu" mark
                    Margin = st.Th(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = Brushes.Transparent,
                };
                chev.FontFamily = st.ActiveTheme.MonoFont;
                chev.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                chev.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                row.Children.Add(chev);
            }

            var capsule = new Border
            {
                Margin  = st.Th(8, 8, 8, 4),
                Padding = st.Th(10, 5, 10, 5),
                BorderThickness = new Thickness(1),
                Child = row,
                // 45% when there is no building to pick — inert, but still visibly the control
                // that would pick one.
                Opacity = building != null ? 1.0 : 0.45,
            };
            capsule.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Chip");
            capsule.SetResourceReference(Border.BackgroundProperty,   "LemoineRaised");
            capsule.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorder");

            if (building != null)
            {
                capsule.Cursor = Cursors.Hand;
                capsule.MouseLeftButtonUp += (s, e) => { ShowBuildingMenu(); e.Handled = true; };
            }

            _buildingSlot.Content = capsule;
        }

        /// <summary>
        /// Cycles to the next building. A single-building project has nothing to switch to, so
        /// the capsule reports that rather than opening an empty menu that looks broken.
        /// </summary>
        private void ShowBuildingMenu()
        {
            var all = Lib.Buildings.OrderBy(b => b.SortIndex).ToList();
            if (all.Count <= 1)
            {
                FlashStatus(AppStrings.T("zones.manager.status.oneBuilding"));
                return;
            }

            int i = all.FindIndex(b => b.Id == _activeBuildingId);
            var next = all[(i + 1) % all.Count];

            _activeBuildingId = next.Id;
            _activeLevelId    = "";
            Select(Key(KindBuilding, next.Id));
        }

        private UIElement NavSectionHeader(string text)
        {
            var st = AppSettings.Instance;
            var tb = new TextBlock
            {
                Text = text.ToUpperInvariant(),
                Margin = st.Th(10, 4, 10, 3),
                FontWeight = FontWeights.SemiBold,
                // NOTE: the design specifies letter-spacing .04em on section headers.
                // TextBlock.LetterSpacing does not exist in .NET Framework 4.8 WPF, so this is
                // the one metric from the board that cannot be reproduced. Nothing else changes.
            };
            tb.FontFamily = st.ActiveTheme.MonoFont;
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return tb;
        }

        private UIElement NavDivider()
        {
            var b = new Border { Height = 1, Margin = AppSettings.Instance.Th(0, 6, 0, 6) };
            b.SetResourceReference(Border.BackgroundProperty, "LemoineBorder");
            return b;
        }

        private UIElement NavNote(string text)
        {
            var st = AppSettings.Instance;
            var tb = new TextBlock
            {
                Text = text,
                Margin = st.Th(12, 6, 12, 6),
                TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyles.Italic,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return tb;
        }

        /// <summary>A level or sheet-size row: name filling the width, meta right-aligned.</summary>
        private UIElement NavParentRow(string key, string name, string meta, Action onClick)
        {
            var st = AppSettings.Instance;
            bool sel = _selected == key;

            var g = new WpfGrid { Background = Brushes.Transparent };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var t = new TextBlock
            {
                Text = name,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
            };
            t.FontFamily = st.ActiveTheme.MonoFont;
            t.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_SM");
            if (sel)
            {
                t.FontWeight = FontWeights.SemiBold;
                t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            }
            else t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            WpfGrid.SetColumn(t, 0);
            g.Children.Add(t);

            if (!string.IsNullOrEmpty(meta))
            {
                var m = new TextBlock
                {
                    Text = meta,
                    Margin = st.Th(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = Brushes.Transparent,
                };
                m.FontFamily = st.ActiveTheme.MonoFont;
                m.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_Meta");
                if (sel) m.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
                else     m.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                WpfGrid.SetColumn(m, 1);
                g.Children.Add(m);
            }

            var b = new Border
            {
                Padding = st.Th(8, 5, 10, 5),
                // 2px left rule, transparent until selected — so rows never shift sideways
                // when the selection moves.
                BorderThickness = new Thickness(2, 0, 0, 0),
                Cursor = Cursors.Hand,
                Child = g,
                // Without this only the glyphs are hit-testable and most of the row ignores clicks.
                Background = Brushes.Transparent,
            };
            if (sel)
            {
                b.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                b.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
            }
            else b.BorderBrush = Brushes.Transparent;

            b.MouseLeftButtonUp += (s, e) =>
            {
                try { onClick(); }
                catch (Exception ex) { DiagnosticsLog.Error($"ZoneManagerWindow: select '{key}'", ex); }
                e.Handled = true;
            };
            return b;
        }

        /// <summary>An area or group row: swatch, name, and a state glyph when there is one.</summary>
        private UIElement NavChildRow(string key, string name, bool problem, bool ok, Action onClick)
        {
            var st = AppSettings.Instance;
            bool sel = _selected == key;

            var row = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };

            var swatch = new Border
            {
                Width = st.S(6), Height = st.S(6),
                CornerRadius = new CornerRadius(1),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = st.Th(0, 0, 6, 0),
                // Unselected areas keep the accent at 45% — the same hue reads as the same
                // kind of thing, the alpha says which one is current.
                Opacity = sel ? 1.0 : 0.45,
            };
            swatch.SetResourceReference(Border.BackgroundProperty, "LemoineAccent");
            row.Children.Add(swatch);

            var t = new TextBlock
            {
                Text = name,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
            };
            t.FontFamily = st.ActiveTheme.MonoFont;
            t.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_SM");
            if (sel)
            {
                t.FontWeight = FontWeights.SemiBold;
                t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            }
            else t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");

            var g = new WpfGrid { Background = Brushes.Transparent };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            WpfGrid.SetColumn(row, 0); g.Children.Add(row);
            WpfGrid.SetColumn(t, 1);   g.Children.Add(t);

            if (problem || ok)
            {
                var mark = new TextBlock
                {
                    Text = problem ? char.ConvertFromUtf32(0x25CF)    // filled circle
                                   : char.ConvertFromUtf32(0x2713),   // check
                    Margin = st.Th(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = Brushes.Transparent,
                };
                mark.FontFamily = st.ActiveTheme.MonoFont;
                mark.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_Meta");
                if (problem) mark.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
                else         mark.SetResourceReference(TextBlock.ForegroundProperty, "LemoineGreen");
                WpfGrid.SetColumn(mark, 2);
                g.Children.Add(mark);
            }

            var b = new Border
            {
                Padding = st.Th(22, 4, 10, 4),
                Cursor  = Cursors.Hand,
                Child   = g,
                Background = Brushes.Transparent,
            };
            if (sel) b.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");

            b.MouseLeftButtonUp += (s, e) =>
            {
                try { onClick(); }
                catch (Exception ex) { DiagnosticsLog.Error($"ZoneManagerWindow: select '{key}'", ex); }
                e.Handled = true;
            };
            return b;
        }

        /// <summary>A capsule "＋ X" button at the end of a section or a parent's children.</summary>
        private UIElement NavAddButton(string label, bool indent, Action onAdd)
        {
            var st = AppSettings.Instance;

            var t = new TextBlock
            {
                Text = char.ConvertFromUtf32(0xFF0B) + " " + label,   // fullwidth plus
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = Brushes.Transparent,
            };
            t.FontFamily = st.ActiveTheme.MonoFont;
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");

            var b = new Border
            {
                Margin  = indent ? st.Th(20, 4, 6, 4) : st.Th(6, 4, 6, 4),
                Padding = indent ? st.Th(10, 6, 10, 6) : st.Th(10, 7, 10, 7),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child  = t,
                Background = Brushes.Transparent,
            };
            b.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Chip");
            b.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorder");

            b.MouseLeftButtonUp += (s, e) =>
            {
                try { onAdd(); }
                catch (Exception ex) { DiagnosticsLog.Error($"ZoneManagerWindow: add '{label}'", ex); }
                e.Handled = true;
            };
            return b;
        }

        // ── Docked footer ────────────────────────────────────────────────────

        /// <summary>
        /// Re-solve plus delete, docked at the bottom of the navigator.
        ///
        /// Re-solve takes the accent variant whenever any placement is stale, so the one action
        /// that would fix what the warning cards are complaining about is the one that stands out.
        /// </summary>
        private void BuildNavFooter()
        {
            var st = AppSettings.Instance;

            _navFooter.Children.Clear();
            _navFooterBorder.Padding = st.Th(8, 7, 8, 7);

            bool stale  = HasStalePlacements();
            bool anyLib = Lib.Areas.Count > 0 || Lib.SheetSets.Count > 0;

            var resolve = MakeFooterButton(AppStrings.T("zones.manager.actions.resolvePlacements"),
                                           OnResolvePlacements, accent: stale);
            // Fills the width beside the fixed-size delete button.
            resolve.Width = Math.Max(0, st.S(180) - st.S(8) * 2 - st.S(30) - st.S(5));
            _navFooter.Children.Add(resolve);

            var del = MakeFooterButton(char.ConvertFromUtf32(0x232B), DeleteSelected, accent: false);
            del.Width  = st.S(30);
            del.Margin = st.Th(5, 0, 0, 0);
            del.ToolTip = AppStrings.T("zones.manager.actions.delete");
            _navFooter.Children.Add(del);

            // 45% while there is nothing to act on — present, so its place is learnable, but
            // plainly not offering to do anything yet.
            _navFooterBorder.Opacity = anyLib ? 1.0 : 0.45;
            _navFooter.IsEnabled     = anyLib;
        }

        /// <summary>True when any stored placement no longer matches what a solve would produce.</summary>
        private bool HasStalePlacements()
        {
            if (Lib.Placements == null || Lib.Placements.Count == 0) return false;

            // An area whose scope box moved under it has, by definition, placements that no
            // longer sit where the solver would put them.
            return Lib.Placements.Any(p => p != null && _boxDrift.ContainsKey(p.AreaId ?? ""));
        }

        private Border MakeFooterButton(string label, Action onClick, bool accent)
        {
            var st = AppSettings.Instance;

            var t = new TextBlock
            {
                Text = label,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Background = Brushes.Transparent,
            };
            t.FontFamily = st.ActiveTheme.MonoFont;
            t.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_SM");
            if (accent) t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            else        t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");

            var b = new Border
            {
                Height = st.S(26),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child  = t,
                Background = Brushes.Transparent,
            };
            b.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_SM");
            if (accent)
            {
                b.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                b.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
            }
            else b.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            b.MouseLeftButtonUp += (s, e) =>
            {
                try { onClick(); }
                catch (Exception ex) { DiagnosticsLog.Error($"ZoneManagerWindow: '{label}'", ex); }
                e.Handled = true;
            };
            return b;
        }
    }
}
