using System;
using System.Collections.Generic;
using System.Linq;
using LemoineTools.Framework;
using LemoineTools.Framework.Zones;

namespace LemoineTools.Tools.Zones.Windows
{
    // =========================================================================
    // Sheet mode — the same canvas, drawing the sheet a group lands on.
    //
    // This is where the user confirms a group actually FITS its title block
    // before running Build Sheets. Everything here is the real solver's answer
    // (ZoneGroupSolver, via SolveGroupPreview), never an approximation drawn to
    // look plausible: the placed rectangles are its footprints, at its scale,
    // and the "fits" verdict is its own.
    //
    // World units are paper feet throughout, matching the solver.
    // =========================================================================
    public partial class ZoneManagerWindow
    {
        /// <summary>The sheet size being shown: the active one, else the one holding the selection.</summary>
        private ZoneSheetSet? ResolveActiveSheetSet()
        {
            if (!string.IsNullOrEmpty(_activeSheetSetId))
            {
                var y = Lib.SheetSet(_activeSheetSetId);
                if (y != null) return y;
            }
            return Lib.SheetSets.OrderBy(y => y.SortIndex).FirstOrDefault();
        }

        /// <summary>
        /// The group being shown. When an AREA is selected this resolves to the group that area
        /// sits on, so switching to Sheet from a plan selection lands somewhere meaningful
        /// rather than on nothing.
        /// </summary>
        private ZoneSheetGroup? ResolveActiveGroup(ZoneSheetSet? set)
        {
            if (set == null) return null;
            var groups = (set.Groups ?? new List<ZoneSheetGroup>()).OrderBy(g => g.SortIndex).ToList();
            if (groups.Count == 0) return null;

            string prefix = Key(KindSheetGroup, set.Id, "");
            if (_selected.StartsWith(prefix, StringComparison.Ordinal))
            {
                string gid = _selected.Substring(prefix.Length);
                var hit = groups.FirstOrDefault(g => g.Id == gid);
                if (hit != null) return hit;
            }

            var area = SelectedArea();
            if (area != null)
            {
                var owning = groups.FirstOrDefault(g => g.AreaIds != null && g.AreaIds.Contains(area.Id));
                if (owning != null) return owning;
            }

            return groups[0];
        }

        private void PaintSheet()
        {
            var set   = ResolveActiveSheetSet();
            var group = ResolveActiveGroup(set);

            var solved = (set != null && group != null) ? SolveGroupPreview(set, group) : null;
            var scene  = BuildSheetScene(set, group, solved);

            _canvas!.SetScene(scene);

            BuildLegend(plan: false);
            BuildSheetChips(solved);
        }

        /// <summary>
        /// Draws the sheet: paper, drawing area, title block strip with its key-plan slot, the
        /// placed views at the solved scale, the matchlines where they meet, and the slack left
        /// over. Returns an empty scene when there is nothing solvable — the chips then say why.
        /// </summary>
        private ZoneCanvasScene BuildSheetScene(ZoneSheetSet? set, ZoneSheetGroup? group,
                                                ZoneGroupSolver.Result? solved)
        {
            var st = AppSettings.Instance;
            var scene = new ZoneCanvasScene { InsetPx = st.S(16) };

            if (set == null) return scene;

            var tb = _titleBlocks.FirstOrDefault(t => t.Name == set.TitleBlockTypeName);
            if (tb == null || !tb.HasSize) return scene;

            double w = tb.WidthFt, h = tb.HeightFt;

            // ── Paper.
            scene.Add(new ZoneCanvasRect
            {
                MinX = 0, MinY = 0, MaxX = w, MaxY = h,
                Style = ZoneCanvasStyle.Filled(ZoneCanvasInk.Surface, 0.35, ZoneCanvasInk.Sub, 1.0, st.S(1.5)),
            });
            scene.IncludeRect(0, 0, w, h);

            // ── Drawing area, from this sheet size's own margins.
            double dxMin = set.MarginLeftFt;
            double dyMin = set.MarginBottomFt;
            double dxMax = w - set.MarginRightFt;
            double dyMax = h - set.MarginTopFt;

            if (dxMax > dxMin && dyMax > dyMin)
            {
                scene.Add(new ZoneCanvasRect
                {
                    MinX = dxMin, MinY = dyMin, MaxX = dxMax, MaxY = dyMax,
                    Style = ZoneCanvasStyle.Dashed(ZoneCanvasInk.Border, st.S(0.8),
                                                   new[] { st.S(5), st.S(4) }),
                });
                scene.Add(new ZoneCanvasText
                {
                    X = dxMin, Y = dyMax, OffsetXPx = 0, OffsetYPx = -st.S(11),
                    Text = AppStrings.T("zones.manager.sheet.drawingArea",
                                        FormatInches(dxMax - dxMin), FormatInches(dyMax - dyMin)),
                    FontSizePx = st.S(7), Ink = ZoneCanvasInk.Dim,
                });

                // ── Title block strip: the band the right margin reserves. Drawn only when
                // that margin is wide enough to be a strip rather than a hairline — a sheet
                // set with no right margin has nowhere to put one, and inventing one would
                // misrepresent the sheet.
                double strip = w - dxMax;
                if (strip > 0.15 * w)
                    AddTitleBlock(scene, dxMax, dyMin, w, dyMax, tb, set, group);
            }

            // ── Placed views, at the solved scale and true world offsets.
            if (solved != null && solved.Items != null)
            {
                foreach (var item in solved.Items)
                {
                    bool sel = _selected == Key(KindArea, item.AreaId);

                    scene.Add(new ZoneCanvasRect
                    {
                        MinX = item.FootMinX, MinY = item.FootMinY,
                        MaxX = item.FootMaxX, MaxY = item.FootMaxY,
                        Style = sel
                            ? ZoneCanvasStyle.Filled(ZoneCanvasInk.Accent, 0.20, ZoneCanvasInk.Accent, 1.00, st.S(1.7))
                            : ZoneCanvasStyle.Filled(ZoneCanvasInk.Accent, 0.07, ZoneCanvasInk.Accent, 0.50, st.S(1.0)),
                        HitId     = Key(KindArea, item.AreaId),
                        HoverName = item.Label,
                        HoverDims = AppStrings.T("zones.manager.sheet.at",
                                                 FormatInches(item.AnchorSheetX), FormatInches(item.AnchorSheetY)),
                    });
                    scene.IncludeRect(item.FootMinX, item.FootMinY, item.FootMaxX, item.FootMaxY);

                    scene.Add(new ZoneCanvasText
                    {
                        X = item.FootMinX, Y = item.FootMaxY, OffsetXPx = st.S(6), OffsetYPx = st.S(4),
                        Text = ViewLabelFor(item),
                        FontSizePx = st.S(7.5), Bold = sel,
                        Ink = sel ? ZoneCanvasInk.Text : ZoneCanvasInk.Dim,
                    });
                    scene.Add(new ZoneCanvasText
                    {
                        X = item.FootMinX, Y = item.FootMaxY, OffsetXPx = st.S(6), OffsetYPx = st.S(14),
                        Text = AppStrings.T("zones.manager.sheet.at",
                                            FormatInches(item.AnchorSheetX), FormatInches(item.AnchorSheetY)),
                        FontSizePx = st.S(6),
                        Ink = sel ? ZoneCanvasInk.Accent : ZoneCanvasInk.Dim,
                    });

                    scene.Add(new ZoneCanvasCross
                    {
                        X = item.AnchorSheetX, Y = item.AnchorSheetY, ArmPx = st.S(4),
                        Style = ZoneCanvasStyle.Solid(sel ? ZoneCanvasInk.Accent : ZoneCanvasInk.Dim,
                                                      sel ? st.S(1.0) : st.S(0.8)),
                    });

                    if (sel)
                    {
                        foreach (var (hx, hy) in new[]
                        {
                            (item.FootMinX, item.FootMinY), (item.FootMaxX, item.FootMinY),
                            (item.FootMinX, item.FootMaxY), (item.FootMaxX, item.FootMaxY),
                        })
                            scene.Add(new ZoneCanvasHandle
                            {
                                X = hx, Y = hy, SizePx = st.S(6),
                                Style = new ZoneCanvasStyle { Fill = ZoneCanvasInk.Accent, FillAlpha = 1.0 },
                            });
                    }
                }

                foreach (var line in SheetMatchlines(solved.Items)) scene.Add(line);

                // ── Slack: what is left of the drawing area once the arrangement is in it.
                if (solved.Fits && solved.SlackXFt > 0.01 && solved.Items.Count > 0 && dxMax > dxMin)
                {
                    double right = solved.Items.Max(i => i.FootMaxX);
                    double y     = dyMin + (dyMax - dyMin) * 0.04;
                    AddSlackDimension(scene, right, dxMax, y, solved.SlackXFt);
                }
            }

            return scene;
        }

        /// <summary>The title block strip, its internal rules, and the key-plan slot.</summary>
        private void AddTitleBlock(ZoneCanvasScene scene, double x0, double y0, double x1, double y1,
                                   ZoneTitleBlocks.TitleBlockType tb, ZoneSheetSet set, ZoneSheetGroup? group)
        {
            var st = AppSettings.Instance;

            scene.Add(new ZoneCanvasRect
            {
                MinX = x0, MinY = y0, MaxX = x1, MaxY = y1,
                Style = ZoneCanvasStyle.Filled(ZoneCanvasInk.Surface, 1.0, ZoneCanvasInk.Border, 1.0, st.S(0.8)),
            });

            double pad = (x1 - x0) * 0.06;
            var rule = ZoneCanvasStyle.Solid(ZoneCanvasInk.Border, st.S(0.5));

            scene.Add(new ZoneCanvasText
            {
                X = x0 + pad, Y = y1, OffsetXPx = 0, OffsetYPx = st.S(5),
                Text = tb.Name, FontSizePx = st.S(7), Ink = ZoneCanvasInk.Sub,
            });

            // Three rules near the top and one above the key plan — the strip's own furniture.
            foreach (double f in new[] { 0.94, 0.88, 0.82, 0.24 })
            {
                double y = y0 + (y1 - y0) * f;
                scene.Add(new ZoneCanvasLine { X0 = x0 + pad, Y0 = y, X1 = x1 - pad, Y1 = y, Style = rule });
            }

            if (group != null)
                scene.Add(new ZoneCanvasText
                {
                    X = x0 + pad, Y = y0 + (y1 - y0) * 0.24, OffsetXPx = 0, OffsetYPx = st.S(9),
                    Text = GroupLabel(set, group), FontSizePx = st.S(6), Ink = ZoneCanvasInk.Dim,
                });

            // ── Key-plan slot: the SAME building outline the plan mode draws, in miniature,
            // with this group's areas filled. Same source, so the sheet's key plan and the
            // plan on screen can never show different buildings.
            double kx0 = x0 + pad, kx1 = x1 - pad;
            double ky1 = y0 + (y1 - y0) * 0.20, ky0 = y0 + (y1 - y0) * 0.06;

            scene.Add(new ZoneCanvasRect
            {
                MinX = kx0, MinY = ky0, MaxX = kx1, MaxY = ky1,
                Style = ZoneCanvasStyle.Filled(ZoneCanvasInk.Page, 1.0, ZoneCanvasInk.Border, 1.0, st.S(0.6)),
            });
            scene.Add(new ZoneCanvasText
            {
                X = kx0, Y = ky0, OffsetXPx = 0, OffsetYPx = st.S(2),
                Text = AppStrings.T("zones.manager.sheet.keyPlan"), FontSizePx = st.S(5), Ink = ZoneCanvasInk.Dim,
            });

            AddKeyPlanMiniature(scene, kx0, ky0, kx1, ky1, group);
        }

        /// <summary>
        /// Fits the captured level outline into the key-plan slot and fills this group's areas.
        /// Silently draws nothing when the level has no outline — a slot with a box in it that
        /// is not the building would be worse than an empty slot.
        /// </summary>
        private void AddKeyPlanMiniature(ZoneCanvasScene scene, double x0, double y0, double x1, double y1,
                                         ZoneSheetGroup? group)
        {
            var st = AppSettings.Instance;

            var level = ResolveActiveLevel();
            var outline = level == null ? null : _snapshot.ForLevel(level.Id);
            if (outline == null || !outline.HasOutline) return;

            double ow = outline.WidthFt, oh = outline.DepthFt;
            if (ow <= 0 || oh <= 0) return;

            // Fit the outline's own box into the slot, aspect preserved, inset a little.
            double slotW = (x1 - x0) * 0.88, slotH = (y1 - y0) * 0.80;
            double k = Math.Min(slotW / ow, slotH / oh);
            double cx = (x0 + x1) / 2.0, cy = (y0 + y1) / 2.0;

            double MapKX(double x) => cx + (x - (outline.MinX + outline.MaxX) / 2.0) * k;
            double MapKY(double y) => cy + (y - (outline.MinY + outline.MaxY) / 2.0) * k;

            var poly = new ZoneCanvasPoly
            {
                Style = ZoneCanvasStyle.Filled(ZoneCanvasInk.Raised, 1.0, ZoneCanvasInk.Sub, 1.0, st.S(0.6)),
            };
            foreach (var ring in outline.Rings)
            {
                if (ring.Count < 3) continue;
                poly.Rings.Add(ring.Select(p => new ZoneCanvasPoint(MapKX(p.X), MapKY(p.Y))).ToList());
            }
            if (poly.Rings.Count > 0) scene.Add(poly);

            foreach (string id in group?.AreaIds ?? new List<string>())
            {
                var a = Lib.Area(id);
                if (a == null || !a.HasExtents) continue;

                scene.Add(new ZoneCanvasRect
                {
                    MinX = MapKX(a.MinX), MinY = MapKY(a.MinY),
                    MaxX = MapKX(a.MaxX), MaxY = MapKY(a.MaxY),
                    Style = new ZoneCanvasStyle { Fill = ZoneCanvasInk.Accent, FillAlpha = 0.45 },
                });
            }
        }

        /// <summary>A dimension line with end ticks, measuring the slack beside the arrangement.</summary>
        private void AddSlackDimension(ZoneCanvasScene scene, double x0, double x1, double y, double slackFt)
        {
            var st = AppSettings.Instance;
            var style = ZoneCanvasStyle.Solid(ZoneCanvasInk.Green, st.S(0.7));

            scene.Add(new ZoneCanvasLine { X0 = x0, Y0 = y, X1 = x1, Y1 = y, Style = style });

            double tick = (x1 - x0) * 0.12;
            scene.Add(new ZoneCanvasLine { X0 = x0, Y0 = y - tick, X1 = x0, Y1 = y + tick, Style = style });
            scene.Add(new ZoneCanvasLine { X0 = x1, Y0 = y - tick, X1 = x1, Y1 = y + tick, Style = style });

            scene.Add(new ZoneCanvasText
            {
                X = x0, Y = y, OffsetXPx = st.S(3), OffsetYPx = -st.S(11),
                Text = AppStrings.T("zones.manager.sheet.slack", FormatInches(slackFt)),
                FontSizePx = st.S(6), Ink = ZoneCanvasInk.Green,
            });
        }

        /// <summary>Matchlines between placed footprints that share an edge on the sheet.</summary>
        private IEnumerable<ZoneCanvasLine> SheetMatchlines(List<ZoneGroupSolver.Placed> items)
        {
            var st = AppSettings.Instance;
            var style = ZoneCanvasStyle.Dashed(ZoneCanvasInk.Green, st.S(1.3),
                                               new[] { st.S(8), st.S(3), st.S(2), st.S(3) });

            // Paper feet, so the tolerance is a paper hair rather than a model one.
            const double tol = 1.0 / 64.0;

            for (int i = 0; i < items.Count; i++)
            for (int j = i + 1; j < items.Count; j++)
            {
                var a = items[i];
                var b = items[j];

                foreach (double x in new[] { a.FootMaxX, a.FootMinX })
                {
                    if (Math.Abs(x - b.FootMinX) > tol && Math.Abs(x - b.FootMaxX) > tol) continue;
                    double y0 = Math.Max(a.FootMinY, b.FootMinY);
                    double y1 = Math.Min(a.FootMaxY, b.FootMaxY);
                    if (y1 - y0 <= tol) continue;
                    yield return new ZoneCanvasLine { X0 = x, Y0 = y0, X1 = x, Y1 = y1, Style = style };
                    break;
                }

                foreach (double y in new[] { a.FootMaxY, a.FootMinY })
                {
                    if (Math.Abs(y - b.FootMinY) > tol && Math.Abs(y - b.FootMaxY) > tol) continue;
                    double x0 = Math.Max(a.FootMinX, b.FootMinX);
                    double x1 = Math.Min(a.FootMaxX, b.FootMaxX);
                    if (x1 - x0 <= tol) continue;
                    yield return new ZoneCanvasLine { X0 = x0, Y0 = y, X1 = x1, Y1 = y, Style = style };
                    break;
                }
            }
        }

        /// <summary>"Area 1 — Floor Plan", using the view definition the area actually inherits.</summary>
        private string ViewLabelFor(ZoneGroupSolver.Placed item)
        {
            var level = ResolveActiveLevel();
            var def   = level?.ViewDefs?.OrderBy(v => v.SortIndex).FirstOrDefault();
            return def == null
                ? item.Label
                : AppStrings.T("zones.manager.sheet.viewLabel", item.Label, def.Name);
        }

        private void BuildSheetChips(ZoneGroupSolver.Result? solved)
        {
            if (_chipRow == null) return;
            var st = AppSettings.Instance;

            _chipRow.Children.Clear();
            _chipRow.Margin = st.Th(0, 14, 16, 0);

            if (solved == null)
            {
                _chipRow.Children.Add(CanvasChip(AppStrings.T("zones.manager.sheet.unsolved"),
                                                 "LemoineTextDim", "LemoineBorder"));
                return;
            }

            _chipRow.Children.Add(CanvasChip(FormatScale(solved.Scale), "LemoineTextDim", "LemoineBorder"));

            if (solved.Fits)
                _chipRow.Children.Add(CanvasChip(AppStrings.T("zones.manager.sheet.fits"),
                                                 "LemoineGreen", "LemoineGreen"));
            else
                _chipRow.Children.Add(CanvasChip(AppStrings.T("zones.manager.sheet.overflows"),
                                                 "LemoineRed", "LemoineRed"));
        }

        // ── Formatting ───────────────────────────────────────────────────────

        /// <summary>
        /// A view scale as an architect would read it. The denominator is exact and this is a
        /// reversible re-expression of it — nothing is rounded into the value itself.
        /// Falls back to "1 : N" for a scale with no clean imperial equivalent.
        /// </summary>
        internal static string FormatScale(int scale)
        {
            if (scale <= 0) return "";

            // View scale is the denominator of 1:N. The standard imperial ladder maps onto it
            // exactly — 1/8" = 1'-0" IS 1:96 — so this is a re-expression of an exact integer,
            // not a rounding of one. Anything off the ladder stays as the ratio it is.
            if (ImperialScales.TryGetValue(scale, out string? label))
                return AppStrings.T("zones.manager.sheet.scaleImperial", label);

            return AppStrings.T("zones.manager.sheet.scaleRatio", scale);
        }

        /// <summary>Paper feet as inches, the unit every sheet coordinate in the design is quoted in.</summary>
        internal static string FormatInches(double paperFt) => $"{paperFt * 12.0:0.#}\"";

        /// <summary>Revit's standard imperial view scales, denominator to its drawn label.</summary>
        private static readonly Dictionary<int, string> ImperialScales = new Dictionary<int, string>
        {
            { 384, "1/32\"" }, { 192, "1/16\"" }, { 128, "3/32\"" }, { 96, "1/8\"" },
            {  64, "3/16\"" }, {  48, "1/4\""  }, {  32, "3/8\""  }, { 24, "1/2\"" },
            {  16, "3/4\""  }, {  12, "1\""    },
        };
    }
}
