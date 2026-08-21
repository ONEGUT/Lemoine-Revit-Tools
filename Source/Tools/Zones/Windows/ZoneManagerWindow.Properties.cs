using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LemoineTools.Framework;
using LemoineTools.Framework.Zones;

using WpfGrid = System.Windows.Controls.Grid;

namespace LemoineTools.Tools.Zones.Windows
{
    // =========================================================================
    // The properties pane — the 300px right column.
    //
    // The canvas selects; this edits. Every panel is the same three pieces: a
    // header carrying the selection's name and a type pill, cards, and 96|*
    // field rows inside them.
    //
    // Warning and confirmation cards are driven by LIBRARY AND CAPTURE STATE,
    // never by a dismissable flag: a card describing a stale placement has to
    // disappear when the placement stops being stale, not when someone clicks
    // it away.
    // =========================================================================
    public partial class ZoneManagerWindow
    {
        /// <summary>The selection's name beside a type pill.</summary>
        private UIElement PropsHeader(string name, string pill)
        {
            var st = AppSettings.Instance;

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = st.Th(0, 0, 0, 10),
            };

            var t = new TextBlock
            {
                Text = name,
                VerticalAlignment = VerticalAlignment.Bottom,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = FontWeights.SemiBold,
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_LG");
            row.Children.Add(t);

            if (!string.IsNullOrEmpty(pill))
            {
                var p = new TextBlock { Text = pill, VerticalAlignment = VerticalAlignment.Center };
                p.FontFamily = st.ActiveTheme.MonoFont;
                p.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                p.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_Meta");

                var box = new Border
                {
                    Padding = st.Th(6, 2, 6, 2),
                    Margin  = st.Th(8, 0, 0, 0),
                    BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Child = p,
                };
                box.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_SM");
                box.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorder");
                row.Children.Add(box);
            }

            return row;
        }

        // ── Nothing selected ─────────────────────────────────────────────────

        /// <summary>
        /// The prompt, then what the launch capture actually read.
        ///
        /// The counts card exists so a user looking at an empty window can tell "Discover has
        /// nothing to work with" from "Discover has not run yet" — two states that otherwise
        /// look identical and lead to opposite next moves.
        /// </summary>
        private void BuildNothingSelected()
        {
            var st = AppSettings.Instance;

            var prompt = new TextBlock
            {
                Text = AppStrings.T("zones.manager.pickSomething"),
                TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyles.Italic,
                LineHeight = st.S(19),
            };
            prompt.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            prompt.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            _detailStack.Children.Add(prompt);

            var card = MakeCard(AppStrings.T("zones.manager.props.readFromModel"));
            var counts = _snapshot.Counts;

            card.Children.Add(CountRow(AppStrings.T("zones.manager.props.scopeBoxes"),   counts.ScopeBoxes,      divider: true));
            card.Children.Add(CountRow(AppStrings.T("zones.manager.props.titleBlocks"),  counts.TitleBlockTypes, divider: true));
            card.Children.Add(CountRow(AppStrings.T("zones.manager.props.hostLevels"),   counts.HostLevels,      divider: true));
            card.Children.Add(CountRow(AppStrings.T("zones.manager.props.linkedModels"), counts.LinkedModels,    divider: false));
            card.Children.Add(MakeNote(AppStrings.T("zones.manager.props.capturedNote")));

            var wrapped = WrapCard(card);
            if (wrapped is FrameworkElement fe) fe.Margin = st.Th(0, 22, 0, 0);
            _detailStack.Children.Add(wrapped);
        }

        private UIElement CountRow(string label, int count, bool divider)
        {
            var st = AppSettings.Instance;

            var g = new WpfGrid { Margin = st.Th(0, 0, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            l.FontFamily = st.ActiveTheme.MonoFont;
            l.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            l.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            WpfGrid.SetColumn(l, 0);
            g.Children.Add(l);

            var c = new TextBlock { Text = count.ToString(), VerticalAlignment = VerticalAlignment.Center };
            c.FontFamily = st.ActiveTheme.MonoFont;
            // Green: these are values that were successfully read, not neutral facts.
            c.SetResourceReference(TextBlock.ForegroundProperty, "LemoineGreen");
            c.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            WpfGrid.SetColumn(c, 1);
            g.Children.Add(c);

            var b = new Border
            {
                Padding = st.Th(0, 5, 0, 5),
                BorderThickness = divider ? new Thickness(0, 0, 0, 1) : new Thickness(0),
                Child = g,
            };
            if (divider) b.SetResourceReference(Border.BorderBrushProperty, "LemoineRaised");
            return b;
        }

        // ── Cards ────────────────────────────────────────────────────────────

        /// <summary>A titled warning card. Present only while the condition it names is true.</summary>
        private UIElement WarnCard(string title, string body)
        {
            var st = AppSettings.Instance;
            var sp = new StackPanel();

            var h = new TextBlock
            {
                Text = title.ToUpperInvariant(),
                Margin = st.Th(0, 0, 0, 3),
                FontWeight = FontWeights.SemiBold,
            };
            h.FontFamily = st.ActiveTheme.MonoFont;
            h.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
            h.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            sp.Children.Add(h);

            var t = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, LineHeight = st.S(16) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            sp.Children.Add(t);

            return SemanticCard(sp, "LemoineRed", "LemoineRedDim");
        }

        /// <summary>A confirmation card — the same metrics as the warning, in the success family.</summary>
        private UIElement OkCard(string body)
        {
            var st = AppSettings.Instance;

            var t = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, LineHeight = st.S(16) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineGreen");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");

            return SemanticCard(t, "LemoineGreen", "LemoineGreenDim");
        }

        private UIElement SemanticCard(UIElement content, string borderKey, string fillKey)
        {
            var st = AppSettings.Instance;
            var b = new Border
            {
                Padding = st.Th(11, 9, 11, 9),
                Margin  = st.Th(0, 0, 0, 8),
                BorderThickness = new Thickness(1),
                Child = content,
            };
            b.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Card");
            b.SetResourceReference(Border.BorderBrushProperty,  borderKey);
            b.SetResourceReference(Border.BackgroundProperty,   fillKey);
            return b;
        }

        /// <summary>A list-card row: a name and either a muted state word or a state pill.</summary>
        private UIElement ListRow(string name, string state, string? pillFgKey, string? pillBorderKey, bool divider)
        {
            var st = AppSettings.Instance;

            var g = new WpfGrid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var n = new TextBlock
            {
                Text = name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            n.FontFamily = st.ActiveTheme.MonoFont;
            n.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            n.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            WpfGrid.SetColumn(n, 0);
            g.Children.Add(n);

            if (!string.IsNullOrEmpty(state))
            {
                UIElement trailing;
                if (pillFgKey == null || pillBorderKey == null)
                {
                    // No pill: an inherited value is the absence of a decision, and a pill would
                    // make it look like one.
                    var t = new TextBlock { Text = state, VerticalAlignment = VerticalAlignment.Center };
                    t.FontFamily = st.ActiveTheme.MonoFont;
                    t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                    t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_Meta");
                    trailing = t;
                }
                else
                {
                    var t = new TextBlock { Text = state };
                    t.FontFamily = st.ActiveTheme.MonoFont;
                    t.SetResourceReference(TextBlock.ForegroundProperty, pillFgKey);
                    t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_Meta");

                    var pill = new Border
                    {
                        Padding = st.Th(6, 1, 6, 1),
                        BorderThickness = new Thickness(1),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = t,
                    };
                    pill.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_SM");
                    pill.SetResourceReference(Border.BorderBrushProperty,  pillBorderKey);
                    trailing = pill;
                }

                WpfGrid.SetColumn((UIElement)trailing, 1);
                g.Children.Add(trailing);
            }

            var b = new Border
            {
                Padding = st.Th(0, 5, 0, 5),
                BorderThickness = divider ? new Thickness(0, 0, 0, 1) : new Thickness(0),
                Child = g,
            };
            if (divider) b.SetResourceReference(Border.BorderBrushProperty, "LemoineRaised");
            return b;
        }

        // ── Area extras ──────────────────────────────────────────────────────

        /// <summary>
        /// The cards that sit above and below an area's own fields: the scope-box warning, the
        /// views it inherits from its level, and the sheets it is placed on.
        /// </summary>
        private void AppendAreaCards(ZoneArea a, ZoneLevel? level)
        {
            // ── Views the area inherits, and any it overrides.
            if (level != null)
            {
                var views = MakeCard(AppStrings.T("zones.manager.props.viewsFrom", level.Name));
                var defs = (level.ViewDefs ?? new List<ZoneViewDef>()).OrderBy(v => v.SortIndex).ToList();

                if (defs.Count == 0)
                {
                    views.Children.Add(MakeNote(AppStrings.T("zones.manager.level.noViews")));
                }
                else
                {
                    for (int i = 0; i < defs.Count; i++)
                    {
                        var def = defs[i];
                        var ov  = a.ViewOverrides?.FirstOrDefault(o => o.BaseId == def.Id);
                        int n   = ov?.OverriddenFields?.Count ?? 0;

                        if (n > 0)
                            views.Children.Add(ListRow(def.Name,
                                AppStrings.T("zones.manager.props.overridden", n),
                                "LemoineAccent", "LemoineAccent", divider: i < defs.Count - 1));
                        else
                            views.Children.Add(ListRow(def.Name,
                                AppStrings.T("zones.manager.props.inherited"),
                                null, null, divider: i < defs.Count - 1));
                    }
                }
                _detailStack.Children.Add(WrapCard(views));
            }

            // ── Where this area lands. A sheet set that carries the area in a group but has no
            // stored placement reads "not placed" — the two are different states and the card
            // must not collapse them.
            var onSheets = MakeCard(AppStrings.T("zones.manager.props.onSheets"));
            var rows = new List<UIElement>();

            foreach (var set in Lib.SheetSets.OrderBy(y => y.SortIndex))
            {
                foreach (var g in (set.Groups ?? new List<ZoneSheetGroup>()).OrderBy(g => g.SortIndex))
                {
                    if (g.AreaIds == null || !g.AreaIds.Contains(a.Id)) continue;

                    bool placed = Lib.Placements != null && Lib.Placements.Any(
                        p => p != null && p.AreaId == a.Id);

                    rows.Add(ListRow(
                        AppStrings.T("zones.manager.props.sheetGroup", set.Name, GroupLabel(set, g)),
                        placed ? AppStrings.T("zones.manager.props.placed")
                               : AppStrings.T("zones.manager.props.notPlaced"),
                        placed ? "LemoineGreen"  : "LemoineTextDim",
                        placed ? "LemoineGreen"  : "LemoineBorder",
                        divider: false));
                }
            }

            if (rows.Count == 0) onSheets.Children.Add(MakeNote(AppStrings.T("zones.manager.props.onNoSheets")));
            else foreach (var r in rows) onSheets.Children.Add(r);

            _detailStack.Children.Add(WrapCard(onSheets));
        }

        /// <summary>
        /// The scope-box warning, when the box under this area changed size since it adopted it.
        /// Offers the re-solve inline, because that is the action that clears this card.
        /// </summary>
        private UIElement? AreaWarningCard(ZoneArea a)
        {
            if (_missingBoxAreas.Contains(a.Id))
                return WarnCard(AppStrings.T("zones.manager.props.boxMissingTitle"),
                                AppStrings.T("zones.manager.area.boxMissing", a.ScopeBoxName));

            if (!_boxDrift.TryGetValue(a.Id, out var drift)) return null;

            string body = drift.HadPlacements
                ? AppStrings.T("zones.manager.props.boxResizedPlaced", $"{drift.DriftFt:0.##}")
                : AppStrings.T("zones.manager.props.boxResized", $"{drift.DriftFt:0.##}");

            return WarnCard(AppStrings.T("zones.manager.props.boxResizedTitle"), body);
        }
    }
}
