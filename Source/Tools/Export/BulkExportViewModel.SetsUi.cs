using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;

using WpfTextBox    = System.Windows.Controls.TextBox;
using WpfVisibility = System.Windows.Visibility;
using WpfBrushes    = System.Windows.Media.Brushes;
using WpfRectangle  = System.Windows.Shapes.Rectangle;
using RevitElementId = Autodesk.Revit.DB.ElementId;

namespace LemoineTools.Tools.BulkExport
{
    /// <summary>
    /// The sets UI: the Step 1 target bar, and Step 2's granularity control, set cards, ordering,
    /// overrides and actions. Rebuilt from the model on every activation via
    /// <c>IStepAware.OnStepActivated</c> — the model lives on the ViewModel, so a rebuild never
    /// resets it, and expansion state is held there too rather than in the visual tree.
    /// </summary>
    public partial class BulkExportViewModel
    {
        // The sets step is S3 since naming moved ahead of it.
        //
        // Posted rather than called inline: several of these fire from a click handler on a
        // control that lives inside the very step being rebuilt, and RefreshStepContent swaps
        // that content out from under the handler. BeginInvoke lets the handler finish first.
        // Guarded for dispatcher shutdown — a window closing mid-refresh would otherwise throw
        // on its own STA thread, which is a hard Revit crash rather than a logged exception.
        private void PostRefresh(string stepId)
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            dispatcher.BeginInvoke(new Action(() => _refreshStep?.Invoke(stepId)));
        }

        private void RefreshSets()   => PostRefresh("S3");
        private void RefreshNaming() => PostRefresh("S2");

        /// <summary>
        /// Repaints the rail and the tree badges WITHOUT rebuilding Step 1.
        ///
        /// Rebuilding the step here would be a non-terminating loop: BuildS1 calls SetTree, whose
        /// contract is to fire SelectionChanged once at the end, and that handler is what asks for
        /// the rail to refresh. Posting the rebuild made it unbounded rather than re-entrant — the
        /// dispatcher stayed saturated and the window never became interactive.
        /// </summary>
        internal void RefreshSetRail()
        {
            if (_railHost != null) _railHost.Child = BuildSetRailContent();
            _picker?.RefreshBadges();
        }

        /// <summary>
        /// Full Step 1 rebuild — only for changes to the SELECTION itself, which the tree can pick
        /// up no other way. Safe from the loop above because the SelectionChanged it triggers
        /// routes to <see cref="RefreshSetRail"/>, which never rebuilds.
        /// </summary>
        private void RebuildStep1() => PostRefresh("S1");

        // ══════════════════════════════════════════════════════════════════════
        //  Step 1 — target-set bar
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// The set rail: one tab per set down the left of the tree, plus every Revit print set in
        /// the project listed automatically underneath. Clicking a set makes it the target for
        /// anything checked next; clicking a print set imports it AND checks its sheets.
        ///
        /// A rail rather than the previous dropdown because the active set is a persistent mode —
        /// a dropdown hid every set but one, which is the modal-state problem this whole design
        /// exists to avoid.
        /// </summary>
        internal FrameworkElement BuildSetRail()
        {
            var frame = new Border { BorderThickness = new Thickness(1) };
            frame.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_MD");
            frame.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            frame.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            frame.Child = BuildSetRailContent();
            _railHost   = frame;
            return frame;
        }

        /// <summary>The rail's interior, rebuilt on its own whenever sets or counts change.</summary>
        private FrameworkElement BuildSetRailContent()
        {
            var root = new DockPanel { LastChildFill = true };

            // ── Footer actions: creating and saving sets both live here now ───
            var actions = new StackPanel { Margin = new Thickness(5, 4, 5, 5) };

            var newBtn = ControlStyles.BuildSmallButton(AppStrings.T("export.bulkExport.sets.newSet"));
            newBtn.Margin = new Thickness(0, 0, 0, 3);
            newBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            newBtn.Click += (s, e) => PromptNewSet();
            actions.Children.Add(newBtn);

            var saveBtn = ControlStyles.BuildSmallButton(AppStrings.T("export.bulkExport.sets.saveSets"));
            saveBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            saveBtn.Click += (s, e) => SaveSets();
            actions.Children.Add(saveBtn);

            _dirtyLabel = new TextBlock
            {
                FontStyle    = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(1, 3, 0, 0),
            };
            _dirtyLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            _dirtyLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _dirtyLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            actions.Children.Add(_dirtyLabel);
            UpdateDirtyLabel();

            var railStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(1, 3, 0, 0) };
            railStatus.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            railStatus.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            railStatus.Visibility = WpfVisibility.Collapsed;
            _railStatus = railStatus;
            actions.Children.Add(railStatus);

            DockPanel.SetDock(actions, Dock.Bottom);
            root.Children.Add(actions);

            // ── Tabs ──────────────────────────────────────────────────────────
            var stack = new StackPanel { Margin = new Thickness(4, 4, 4, 0) };

            stack.Children.Add(BuildSetTab(
                AppStrings.T("export.bulkExport.sets.allItems"),
                AppStrings.T("export.bulkExport.sets.countBare", UnassignedIds().Count),
                accentHex: null, active: _targetSetId == null, dim: false,
                onClick: () => { _targetSetId = null; RefreshSetRail(); Fire(); }));

            foreach (var set in _sets)
            {
                var captured = set;   // ⚠ capture per iteration, never the loop variable
                stack.Children.Add(BuildSetTab(
                    set.Name,
                    AppStrings.T("export.bulkExport.sets.countBare", set.Members.Count),
                    set.AccentHex, active: _targetSetId == set.Id, dim: false,
                    onClick: () => { _targetSetId = captured.Id; RefreshSetRail(); Fire(); }));
            }

            // Revit print sets are listed automatically — no import button, no walking a list.
            var known = new HashSet<string>(_sets.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
            var importable = _availablePrintSets.Where(ps => !known.Contains(ps.Name)).ToList();
            if (importable.Count > 0)
            {
                var hdr = new TextBlock
                {
                    Text   = AppStrings.T("export.bulkExport.sets.printSetsHeader"),
                    Margin = new Thickness(4, 8, 0, 3),
                };
                hdr.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                hdr.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                hdr.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                stack.Children.Add(hdr);

                foreach (var ps in importable)
                {
                    var captured = ps;
                    stack.Children.Add(BuildSetTab(
                        ps.Name,
                        AppStrings.T("export.bulkExport.sets.countBare", ps.MemberIds.Count),
                        accentHex: null, active: false, dim: true,
                        onClick: () => ImportPrintSet(captured)));
                }
            }

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = stack,
            };
            ControlStyles.WireBubblingScroll(scroll);   // in-page scroller: bubble at its limits
            root.Children.Add(scroll);

            return root;
        }

        private FrameworkElement BuildSetTab(string name, string count, string? accentHex,
                                             bool active, bool dim, Action onClick)
        {
            var tab = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 0),
                Padding         = new Thickness(0, 4, 6, 4),
                Margin          = new Thickness(0, 0, 0, 2),
                Cursor          = Cursors.Hand,
            };
            tab.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Card");

            var row = new DockPanel { LastChildFill = true };

            var rule = new WpfRectangle { Width = 3, Margin = new Thickness(0, 0, 6, 0) };
            if (accentHex != null)
                rule.Fill = BrushHelper.BrushFromHex(accentHex, System.Windows.Media.Colors.Gray);
            else
                rule.SetResourceReference(WpfRectangle.FillProperty, active ? "LemoineAccent" : "LemoineBorder");
            DockPanel.SetDock(rule, Dock.Left);
            row.Children.Add(rule);

            var cnt = new TextBlock { Text = count, VerticalAlignment = VerticalAlignment.Center };
            cnt.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            cnt.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            cnt.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            DockPanel.SetDock(cnt, Dock.Right);
            row.Children.Add(cnt);

            var lbl = new TextBlock
            {
                Text              = name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
                FontWeight        = active ? FontWeights.SemiBold : FontWeights.Normal,
                FontStyle         = dim ? FontStyles.Italic : FontStyles.Normal,
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty,
                active ? "LemoineAccent" : dim ? "LemoineTextDim" : "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            row.Children.Add(lbl);

            if (active)
            {
                tab.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");
            }
            else
            {
                // ⚠ direct assignment — "Transparent" is not a resource key, and a null
                // background would make only the glyphs hit-testable.
                tab.Background = WpfBrushes.Transparent;
                MotionEffects.WireHover(tab, normalBgKey: null, hoverBgKey: "LemoineSelectBg");
            }

            tab.ToolTip = dim ? AppStrings.T("export.bulkExport.sets.importTip") : null;
            tab.Child   = row;
            tab.MouseLeftButtonDown += (s, e) => { e.Handled = true; onClick(); };
            return tab;
        }

        private void PromptNewSet()
        {
            var set = NewSet(AppStrings.T("export.bulkExport.sets.defaultName", _sets.Count + 1));
            _targetSetId = set.Id;
            RefreshSetRail();
            RefreshSets();
            Fire();
        }

        /// <summary>
        /// Turns a Revit print set into an export set and checks every sheet it contains, so
        /// picking a set is one click rather than a click plus re-finding its sheets in the tree.
        /// Membership arrives in Project Browser order — ViewSheetSet.Views has no order of its own.
        /// </summary>
        internal void ImportPrintSet(PrintSetInfo ps)
        {
            var ids = OrderByBrowser(ps.MemberIds.Select(id => id.Value).Where(_idToName.ContainsKey));
            if (ids.Count == 0)
            {
                SetStatus(AppStrings.T("export.bulkExport.sets.importEmpty", ps.Name), isError: true);
                return;
            }

            var set = NewSet(ps.Name);
            foreach (long id in ids)
            {
                RemoveFromAllSets(id);
                set.Members.Add(MakeMember(id));
            }
            _selectedIds  = OrderByBrowser(_selectedIds.Concat(ids).Distinct());
            _targetSetId  = set.Id;

            SetStatus(AppStrings.T("export.bulkExport.sets.imported", ps.Name, set.Members.Count), isError: false);
            RebuildStep1();   // re-seeds the tree with the newly checked sheets
            RefreshSets();
            Fire();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Step 2 — Sets & Order
        // ══════════════════════════════════════════════════════════════════════

        internal FrameworkElement BuildSetsAndOrder()
        {
            PruneSetsToSelection();

            var outer = new StackPanel();

            // ── Named set cards (drag-reorderable) ────────────────────────────
            var setPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            var reorder  = new ListReorder(setPanel, (from, to) =>
            {
                ListReorder.Move(_sets, from, to);
                _setsDirty = true;
                RefreshSets();
                Fire();
            });
            for (int i = 0; i < _sets.Count; i++)
            {
                var card = BuildSetCard(_sets[i]);
                reorder.Arm(card, i);
                setPanel.Children.Add(card);
            }
            outer.Children.Add(setPanel);

            if (_sets.Count == 0)
                outer.Children.Add(Dim(AppStrings.T("export.bulkExport.sets.noSetsHint")));

            // ── Unassigned ────────────────────────────────────────────────────
            var loose = UnassignedIds();
            if (loose.Count > 0) outer.Children.Add(BuildUnassignedCard(loose));

            // ── Actions ───────────────────────────────────────────────────────
            outer.Children.Add(BuildActionsRow());

            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            status.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            status.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            status.Visibility = WpfVisibility.Collapsed;
            _setsStatus = status;
            outer.Children.Add(status);

            return outer;
        }

        private FrameworkElement BuildGranularityRow()
        {
            var row = new UniformGrid { Rows = 1, Columns = 3, Margin = new Thickness(0, 2, 0, 4) };

            var perSheet = BuildModeButton(AppStrings.T("export.bulkExport.sets.perSheet"),
                                           _granularity == PdfGranularity.PerSheet);
            var perSet   = BuildModeButton(AppStrings.T("export.bulkExport.sets.perSet"),
                                           _granularity == PdfGranularity.PerSet);
            var single   = BuildModeButton(AppStrings.T("export.bulkExport.sets.singleFile"),
                                           _granularity == PdfGranularity.SingleFile);

            void Pick(PdfGranularity g)
            {
                _granularity = g;
                ApplyModeButtonStyle(perSheet, g == PdfGranularity.PerSheet);
                ApplyModeButtonStyle(perSet,   g == PdfGranularity.PerSet);
                ApplyModeButtonStyle(single,   g == PdfGranularity.SingleFile);
                _setsDirty = true;
                // The naming boxes and every set card's resolved filename both depend on this.
                RefreshNaming();
                RefreshSets();
                Fire();
            }
            perSheet.Click += (s, e) => Pick(PdfGranularity.PerSheet);
            perSet.Click   += (s, e) => Pick(PdfGranularity.PerSet);
            single.Click   += (s, e) => Pick(PdfGranularity.SingleFile);

            row.Children.Add(perSheet);
            row.Children.Add(perSet);
            row.Children.Add(single);
            return row;
        }

        // ── Set card ──────────────────────────────────────────────────────────

        private FrameworkElement BuildSetCard(ExportSet set)
        {
            bool expanded = _expandedSets.Contains(set.Id);

            var card = new Border
            {
                BorderThickness = new Thickness(1),
                Margin          = new Thickness(0, 0, 0, 5),
            };
            card.SetResourceReference(Border.CornerRadiusProperty,  "LemoineRadius_Card");
            card.SetResourceReference(Border.BackgroundProperty,    "LemoineRaised");
            card.SetResourceReference(Border.BorderBrushProperty,   "LemoineBorder");

            var stack = new StackPanel();

            // ── Header ────────────────────────────────────────────────────────
            var header = new DockPanel { LastChildFill = true, Margin = new Thickness(10, 7, 10, 7) };

            var swatch = new WpfRectangle
            {
                Width  = 3,
                Height = 14,
                Fill   = BrushHelper.BrushFromHex(set.AccentHex, System.Windows.Media.Colors.Gray),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(swatch, Dock.Left);
            header.Children.Add(swatch);

            var caret = new TextBlock
            {
                Text              = char.ConvertFromUtf32(expanded ? 0x25BE : 0x25B8),
                Width             = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor            = Cursors.Hand,
            };
            caret.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            caret.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            caret.Background = WpfBrushes.Transparent;   // ⚠ direct assignment — hit-testable box
            DockPanel.SetDock(caret, Dock.Left);
            header.Children.Add(caret);

            // Format chips + enable box sit right; the name is a shrink-to-text hit box on the
            // left so the rest of the row stays grab-able for dragging.
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(FormatChip("PDF", set.PdfOverride ?? _pdfOn));
            right.Children.Add(FormatChip("DWG", set.DwgOverride ?? _dwgOn));
            var enableCb = new CheckBox
            {
                IsChecked         = set.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(8, 0, 0, 0),
                ToolTip           = AppStrings.T("export.bulkExport.sets.enableTip"),
            };
            enableCb.Checked   += (s, e) => { set.Enabled = true;  _setsDirty = true; Fire(); };
            enableCb.Unchecked += (s, e) => { set.Enabled = false; _setsDirty = true; Fire(); };
            right.Children.Add(enableCb);
            DockPanel.SetDock(right, Dock.Right);
            header.Children.Add(right);

            var count = new TextBlock
            {
                Text              = AppStrings.T("export.bulkExport.sets.itemCount", set.Members.Count),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(8, 0, 8, 0),
            };
            count.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            count.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            count.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            DockPanel.SetDock(count, Dock.Right);
            header.Children.Add(count);

            var name = new TextBlock
            {
                Text                = set.Name,
                FontWeight          = FontWeights.SemiBold,
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,   // shrink to text — keeps the row drag-able
                TextTrimming        = TextTrimming.CharacterEllipsis,
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            name.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            name.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            header.Children.Add(name);

            // What this set will actually be called on disk, using the pattern from the naming
            // step. This is why the naming step now comes first — the answer did not exist yet
            // when sets were edited before it.
            var outName = new TextBlock
            {
                Text         = SetOutputPreview(set),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(20, 0, 10, 7),
            };
            outName.SetResourceReference(TextBlock.ForegroundProperty, "LemoineGreen");
            outName.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            outName.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            stack.Children.Add(outName);

            void Toggle()
            {
                if (!_expandedSets.Remove(set.Id)) _expandedSets.Add(set.Id);
                RefreshSets();
            }
            caret.MouseLeftButtonDown += (s, e) => { e.Handled = true; Toggle(); };
            name.MouseLeftButtonDown  += (s, e) => { e.Handled = true; Toggle(); };

            stack.Children.Add(header);

            // ── Body ──────────────────────────────────────────────────────────
            if (expanded) stack.Children.Add(BuildSetBody(set));

            card.Child = stack;
            return card;
        }

        private FrameworkElement FormatChip(string label, bool on)
        {
            var chip = new Border
            {
                BorderThickness   = new Thickness(1),
                Padding           = new Thickness(5, 0, 5, 1),
                Margin            = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            chip.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_SM");
            chip.SetResourceReference(Border.BorderBrushProperty, on ? "LemoineGreen" : "LemoineBorder");
            var tb = new TextBlock { Text = label };
            tb.SetResourceReference(TextBlock.ForegroundProperty, on ? "LemoineGreen" : "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            chip.Child = tb;
            return chip;
        }

        private FrameworkElement BuildSetBody(ExportSet set)
        {
            var body = new StackPanel { Margin = new Thickness(10, 0, 10, 8) };

            // ── Sort bar ──────────────────────────────────────────────────────
            var sortRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            var sortLbl = new TextBlock
            {
                Text              = AppStrings.T("export.bulkExport.sets.sort"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 6, 0),
            };
            sortLbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            sortLbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            sortLbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            sortRow.Children.Add(sortLbl);

            void AddSort(string label, string mode)
            {
                var b = ControlStyles.BuildSmallButton(label);
                b.Margin = new Thickness(0, 0, 4, 0);
                b.Click += (s, e) => { SortMembers(set, mode); _setsDirty = true; RefreshSets(); Fire(); };
                sortRow.Children.Add(b);
            }
            AddSort(AppStrings.T("export.bulkExport.sets.sortBrowser"), "Browser");
            AddSort(AppStrings.T("export.bulkExport.sets.sortSheetNo"), "SheetNo");
            AddSort(AppStrings.T("export.bulkExport.sets.sortName"),    "Name");
            AddSort(AppStrings.T("export.bulkExport.sets.sortReverse"), "Reverse");
            body.Children.Add(sortRow);

            // ── Members (drag-reorderable) ────────────────────────────────────
            var memberPanel = new StackPanel();
            var reorder = new ListReorder(memberPanel, (from, to) =>
            {
                ListReorder.Move(set.Members, from, to);
                _setsDirty = true;
                RefreshSets();
                Fire();
            });

            // Cap what is rendered: a 300-row card is a wall, and drag is fine-tuning here —
            // the sort bar is the mechanism for bulk ordering.
            const int MaxRows = 40;
            int shown = Math.Min(set.Members.Count, MaxRows);
            for (int i = 0; i < shown; i++)
            {
                var row = BuildMemberRow(set, set.Members[i], i);
                reorder.Arm(row, i);
                memberPanel.Children.Add(row);
            }
            body.Children.Add(memberPanel);

            if (set.Members.Count > shown)
                body.Children.Add(Dim(AppStrings.T("export.bulkExport.sets.moreMembers", set.Members.Count - shown)));

            AddDivider(body);
            body.Children.Add(BuildSetOptions(set));
            return body;
        }

        private FrameworkElement BuildMemberRow(ExportSet set, ExportSetMember member, int index)
        {
            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 1) };

            var num = new TextBlock
            {
                Text              = (index + 1).ToString("D2"),
                Width             = 26,
                VerticalAlignment = VerticalAlignment.Center,
            };
            num.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            num.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            num.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            DockPanel.SetDock(num, Dock.Left);
            row.Children.Add(num);

            var label = new TextBlock
            {
                Text              = member.Label,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            label.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            label.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            row.Children.Add(label);

            var host = new Border
            {
                Child      = row,
                Padding    = new Thickness(3, 2, 3, 2),
                Cursor     = Cursors.SizeAll,
                Background = WpfBrushes.Transparent,   // ⚠ direct assignment — whole row drag-able
                ToolTip    = AppStrings.T("export.bulkExport.sets.dragTip"),
            };
            host.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_SM");
            return host;
        }

        private FrameworkElement BuildSetOptions(ExportSet set)
        {
            var panel = new StackPanel();

            AddSectionLabel(panel, AppStrings.T("export.bulkExport.sets.optionsHeader"));

            // Name
            var nameBox = ThemedBox(set.Name);
            nameBox.TextChanged += (s, e) => { set.Name = nameBox.Text; _setsDirty = true; Fire(); };
            nameBox.LostFocus   += (s, e) => { RefreshSets(); RefreshSetRail(); };
            panel.Children.Add(LabeledRow(AppStrings.T("export.bulkExport.sets.optName"), nameBox));

            // Pattern override
            var patBox = ThemedBox(set.PatternOverride ?? "", mono: true);
            patBox.TextChanged += (s, e) =>
            {
                set.PatternOverride = string.IsNullOrWhiteSpace(patBox.Text) ? null : patBox.Text;
                _setsDirty = true;
                Fire();
            };
            panel.Children.Add(LabeledRow(AppStrings.T("export.bulkExport.sets.optPattern"), patBox));

            // Subfolder override
            var subBox = ThemedBox(set.SubfolderOverride ?? "");
            subBox.TextChanged += (s, e) =>
            {
                set.SubfolderOverride = string.IsNullOrWhiteSpace(subBox.Text) ? null : subBox.Text;
                _setsDirty = true;
                Fire();
            };
            panel.Children.Add(LabeledRow(AppStrings.T("export.bulkExport.sets.optSubfolder"), subBox));

            // Format overrides — all four, so a set can opt in or out of any format.
            panel.Children.Add(FormatOverrideRow("PDF", () => set.PdfOverride, v => set.PdfOverride = v));
            panel.Children.Add(FormatOverrideRow("DWG", () => set.DwgOverride, v => set.DwgOverride = v));
            panel.Children.Add(FormatOverrideRow("NWC", () => set.NwcOverride, v => set.NwcOverride = v));
            panel.Children.Add(FormatOverrideRow("IFC", () => set.IfcOverride, v => set.IfcOverride = v));

            // Secondary actions live here rather than in a "⋯" popup: a Popup with
            // StaysOpen=false crashes Revit, and a hand-rolled dismissable one is a lot of risk
            // for a three-item menu.
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

            var savePs = ControlStyles.BuildSmallButton(AppStrings.T("export.bulkExport.sets.saveAsPrintSet"));
            savePs.Margin = new Thickness(0, 0, 4, 0);
            savePs.Click += (s, e) => SaveSetAsPrintSet(set);
            actions.Children.Add(savePs);

            var dup = ControlStyles.BuildSmallButton(AppStrings.T("export.bulkExport.sets.duplicate"));
            dup.Margin = new Thickness(0, 0, 4, 0);
            dup.Click += (s, e) =>
            {
                var copy = set.Clone();
                copy.Id        = Guid.NewGuid().ToString("N");
                copy.Name      = set.Name + AppStrings.T("export.bulkExport.sets.copySuffix");
                copy.AccentHex = ExportSet.AccentFor(_sets.Count);
                // Membership is single-set, so a duplicate starts empty rather than stealing
                // every member out of the set it was copied from.
                copy.Members   = new List<ExportSetMember>();
                _sets.Add(copy);
                _setsDirty = true;
                RefreshSets();
                RefreshSetRail();
                Fire();
            };
            actions.Children.Add(dup);

            var del = ControlStyles.BuildSmallButton(AppStrings.T("export.bulkExport.sets.delete"));
            del.SetResourceReference(Button.ForegroundProperty, "LemoineRed");
            del.Click += (s, e) =>
            {
                // Members return to Unassigned rather than being deselected — deleting a set is
                // about the grouping, not about dropping sheets from the export.
                _sets.Remove(set);
                _expandedSets.Remove(set.Id);
                if (_targetSetId == set.Id) _targetSetId = null;
                _setsDirty = true;
                RefreshSets();
                RefreshSetRail();
                Fire();
            };
            actions.Children.Add(del);

            panel.Children.Add(actions);
            return panel;
        }

        private FrameworkElement FormatOverrideRow(string label, Func<bool?> get, Action<bool?> set)
        {
            string inherit = AppStrings.T("export.bulkExport.labels.printSetInherit");
            string on      = AppStrings.T("export.bulkExport.labels.printSetOn");
            string off     = AppStrings.T("export.bulkExport.labels.printSetOff");

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            var lbl = new TextBlock { Text = label, Width = 44, VerticalAlignment = VerticalAlignment.Center };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            row.Children.Add(lbl);

            bool? cur = get();
            var sel = new SingleSelect
            {
                Width        = 140,
                Items        = new List<string> { inherit, on, off },
                SelectedItem = cur == true ? on : cur == false ? off : inherit,
            };
            sel.SelectionChanged += v =>
            {
                set(v == on ? true : v == off ? false : (bool?)null);
                _setsDirty = true;
                RefreshSets();
                Fire();
            };
            row.Children.Add(sel);
            return row;
        }

        // ── Unassigned ────────────────────────────────────────────────────────

        private FrameworkElement BuildUnassignedCard(List<long> loose)
        {
            // Warn only once named sets exist. While Unassigned IS the whole run there is nothing
            // to warn about — it is the plain no-grouping case.
            bool warn = _sets.Count > 0;

            var card = new Border
            {
                BorderThickness = new Thickness(1),
                Margin          = new Thickness(0, 0, 0, 5),
                Padding         = new Thickness(10, 7, 10, 7),
            };
            card.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Card");
            card.SetResourceReference(Border.BackgroundProperty,  warn ? "LemoineWarnBg"     : "LemoineRaised");
            card.SetResourceReference(Border.BorderBrushProperty, warn ? "LemoineWarnBorder" : "LemoineBorder");

            var stack = new StackPanel();
            var head  = new DockPanel { LastChildFill = true };

            var cb = new CheckBox
            {
                IsChecked         = _unassignedEnabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 8, 0),
            };
            cb.Checked   += (s, e) => { _unassignedEnabled = true;  RefreshSets(); Fire(); };
            cb.Unchecked += (s, e) => { _unassignedEnabled = false; RefreshSets(); Fire(); };
            DockPanel.SetDock(cb, Dock.Left);
            head.Children.Add(cb);

            var count = new TextBlock
            {
                Text              = AppStrings.T("export.bulkExport.sets.itemCount", loose.Count),
                VerticalAlignment = VerticalAlignment.Center,
            };
            count.SetResourceReference(TextBlock.ForegroundProperty, warn ? "LemoineWarnText" : "LemoineTextDim");
            count.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            count.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            DockPanel.SetDock(count, Dock.Right);
            head.Children.Add(count);

            var title = new TextBlock
            {
                Text                = AppStrings.T("export.bulkExport.sets.unassigned"),
                FontWeight          = FontWeights.SemiBold,
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, warn ? "LemoineWarnText" : "LemoineText");
            title.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            title.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            head.Children.Add(title);

            stack.Children.Add(head);

            if (warn)
            {
                var msg = new TextBlock
                {
                    Text         = AppStrings.T("export.bulkExport.sets.unassignedWarn", loose.Count),
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(0, 4, 0, 0),
                };
                msg.SetResourceReference(TextBlock.ForegroundProperty, "LemoineWarnText");
                msg.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                msg.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                stack.Children.Add(msg);

                var moveRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                foreach (var target in _sets)
                {
                    var b = ControlStyles.BuildSmallButton(
                        AppStrings.T("export.bulkExport.sets.moveAllTo", target.Name));
                    b.Margin = new Thickness(0, 0, 4, 4);
                    var captured = target;   // ⚠ capture per iteration, never the loop variable
                    b.Click += (s, e) =>
                    {
                        foreach (long id in UnassignedIds())
                        {
                            captured.Members.Add(MakeMember(id));
                        }
                        SortMembers(captured, "Browser");
                        _setsDirty = true;
                        RefreshSets();
                        RefreshSetRail();
                        Fire();
                    };
                    moveRow.Children.Add(b);
                }
                stack.Children.Add(moveRow);
            }

            card.Child = stack;
            return card;
        }

        // ── Actions row ───────────────────────────────────────────────────────

        private FrameworkElement BuildActionsRow()
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };

            var byPrefix = ControlStyles.BuildSmallButton(AppStrings.T("export.bulkExport.sets.autoPrefix"));
            byPrefix.Margin = new Thickness(0, 0, 4, 4);
            byPrefix.Click += (s, e) => RunAutoGroup("Prefix");
            wrap.Children.Add(byPrefix);

            var byFolder = ControlStyles.BuildSmallButton(AppStrings.T("export.bulkExport.sets.autoFolder"));
            byFolder.Margin = new Thickness(0, 0, 4, 4);
            byFolder.Click += (s, e) => RunAutoGroup("Folder");
            wrap.Children.Add(byFolder);

            var hint = new TextBlock
            {
                Text              = AppStrings.T("export.bulkExport.sets.manageOnStep1"),
                FontStyle         = FontStyles.Italic,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping      = TextWrapping.Wrap,
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            hint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            hint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            wrap.Children.Add(hint);

            return wrap;
        }

        private void RunAutoGroup(string mode)
        {
            var preview = ComputeAutoGroups(mode);
            if (preview.Count == 0)
            {
                SetStatus(AppStrings.T("export.bulkExport.sets.autoNothing"), isError: true);
                return;
            }
            ApplyAutoGroups(mode, minSize: 3);
            SetStatus(AppStrings.T("export.bulkExport.sets.autoDone", _sets.Count, _selectedIds.Count), isError: false);
            _targetSetId = null;
            RefreshSets();
            RefreshSetRail();
            Fire();
        }

        private void SaveSetAsPrintSet(ExportSet set)
        {
            var handler = App.BulkExportPrintSetHandler;
            var evt     = App.BulkExportPrintSetEvent;
            if (handler == null || evt == null) return;

            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            void OnUi(Action a)
            {
                if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
                dispatcher.BeginInvoke(a);
            }

            handler.Name      = set.Name;
            handler.MemberIds = set.Members.Select(m => new RevitElementId(m.IdValue)).ToList();
            handler.OnCreated = sets => OnUi(() =>
            {
                _availablePrintSets = sets;
                SetStatus(AppStrings.T("export.bulkExport.log.printSetSaved", set.Name), isError: false);
            });
            handler.OnError = msg => OnUi(() => SetStatus(msg ?? "", isError: true));
            evt.Raise();
        }

        // ── Small themed helpers ──────────────────────────────────────────────

        private static WpfTextBox ThemedBox(string text, bool mono = false)
        {
            var box = new WpfTextBox { Text = text, Padding = new Thickness(6, 2, 6, 2) };
            box.SetResourceReference(WpfTextBox.BackgroundProperty,  "LemoineSelectBg");
            box.SetResourceReference(WpfTextBox.ForegroundProperty,  "LemoineText");
            box.SetResourceReference(WpfTextBox.BorderBrushProperty, "LemoineBorderMid");
            box.SetResourceReference(WpfTextBox.FontFamilyProperty,  mono ? "LemoineMonoFont" : "LemoineUiFont");
            box.SetResourceReference(WpfTextBox.FontSizeProperty,    "LemoineFS_SM");
            box.SetResourceReference(WpfTextBox.MinHeightProperty,   "LemoineH_Input");
            return box;
        }

        private static FrameworkElement LabeledRow(string label, FrameworkElement field)
        {
            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };
            var lbl = new TextBlock
            {
                Text              = label,
                Width             = 92,
                VerticalAlignment = VerticalAlignment.Center,
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            DockPanel.SetDock(lbl, Dock.Left);
            row.Children.Add(lbl);
            row.Children.Add(field);
            return row;
        }
    }
}
