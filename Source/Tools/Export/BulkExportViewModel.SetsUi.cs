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
        private void RefreshS2() => _refreshStep?.Invoke("S2");

        // ══════════════════════════════════════════════════════════════════════
        //  Step 1 — target-set bar
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// "Adding to [set] ▾ — N items · + New set". Checking a sheet files it into this set.
        /// Full width with the set's own accent as a leading rule so it reads as active state,
        /// not as a filter tucked in a corner.
        /// </summary>
        internal FrameworkElement BuildTargetBar()
        {
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(0, 5, 8, 5),
                Margin          = new Thickness(0, 0, 0, 8),
            };
            card.SetResourceReference(Border.CornerRadiusProperty,  "LemoineRadius_Card");
            card.SetResourceReference(Border.BackgroundProperty,    "LemoineRaised");
            card.SetResourceReference(Border.BorderBrushProperty,   "LemoineBorder");

            var row = new DockPanel { LastChildFill = true };

            _targetRule = new WpfRectangle { Width = 4, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(_targetRule, Dock.Left);
            row.Children.Add(_targetRule);

            var lbl = new TextBlock
            {
                Text              = AppStrings.T("export.bulkExport.sets.addingTo"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 8, 0),
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            DockPanel.SetDock(lbl, Dock.Left);
            row.Children.Add(lbl);

            var newBtn = ControlStyles.BuildSmallButton(AppStrings.T("export.bulkExport.sets.newSet"));
            newBtn.Click += (s, e) => PromptNewSet();
            DockPanel.SetDock(newBtn, Dock.Right);
            row.Children.Add(newBtn);

            _targetCount = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(8, 0, 8, 0),
            };
            _targetCount.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            _targetCount.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _targetCount.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            DockPanel.SetDock(_targetCount, Dock.Right);
            row.Children.Add(_targetCount);

            // The set chooser itself — a themed combo listing "(no set)" plus every named set.
            var combo = new ComboBox
            {
                IsEditable        = false,
                MaxDropDownHeight = 220,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth          = 150,
            };
            combo.SetResourceReference(ComboBox.BackgroundProperty, "LemoineSelectBg");
            combo.SetResourceReference(ComboBox.ForegroundProperty, "LemoineText");
            combo.SetResourceReference(ComboBox.FontFamilyProperty, "LemoineUiFont");
            combo.SetResourceReference(ComboBox.FontSizeProperty,   "LemoineFS_MD");
            ControlStyles.WireComboWheelBubbling(combo);

            string noSet = AppStrings.T("export.bulkExport.sets.noSet");
            var items = new List<string> { noSet };
            items.AddRange(_sets.Select(s => s.Name));
            combo.ItemsSource   = items;
            combo.SelectedIndex = _targetSetId == null
                ? 0
                : Math.Max(0, _sets.FindIndex(s => s.Id == _targetSetId) + 1);

            combo.SelectionChanged += (s, e) =>
            {
                int idx = combo.SelectedIndex;
                // Deliberately does NOT retarget already-checked rows: a retroactive bulk move on
                // a dropdown change is exactly the invisible mistake this model is prone to.
                _targetSetId = idx <= 0 || idx - 1 >= _sets.Count ? null : _sets[idx - 1].Id;
                UpdateTargetBar();
                Fire();
            };
            row.Children.Add(combo);

            card.Child = row;
            UpdateTargetBar();
            return card;
        }

        private void UpdateTargetBar()
        {
            var target = TargetSet();
            if (_targetRule != null)
            {
                if (target != null)
                    _targetRule.Fill = BrushHelper.BrushFromHex(target.AccentHex, System.Windows.Media.Colors.Gray);
                else
                    _targetRule.SetResourceReference(WpfRectangle.FillProperty, "LemoineBorder");
            }
            if (_targetCount != null)
                _targetCount.Text = target == null
                    ? AppStrings.T("export.bulkExport.sets.unassignedCount", UnassignedIds().Count)
                    : AppStrings.T("export.bulkExport.sets.itemCount", target.Members.Count);
        }

        private void PromptNewSet()
        {
            var set = NewSet(AppStrings.T("export.bulkExport.sets.defaultName", _sets.Count + 1));
            _targetSetId = set.Id;
            _refreshStep?.Invoke("S1");
            RefreshS2();
            Fire();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Step 2 — Sets & Order
        // ══════════════════════════════════════════════════════════════════════

        internal FrameworkElement BuildS2Sets()
        {
            PruneSetsToSelection();

            var outer = new StackPanel();

            // ── Output as ─────────────────────────────────────────────────────
            AddSectionLabel(outer, AppStrings.T("export.bulkExport.sets.outputAs"));
            outer.Children.Add(BuildGranularityRow());

            // ── Named set cards (drag-reorderable) ────────────────────────────
            var setPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            var reorder  = new ListReorder(setPanel, (from, to) =>
            {
                ListReorder.Move(_sets, from, to);
                _setsDirty = true;
                RefreshS2();
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
            _setStatus = status;
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
                // Step 3 hides whichever naming box cannot apply at this granularity.
                _refreshStep?.Invoke("S3");
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

            void Toggle()
            {
                if (!_expandedSets.Remove(set.Id)) _expandedSets.Add(set.Id);
                RefreshS2();
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
                b.Click += (s, e) => { SortMembers(set, mode); _setsDirty = true; RefreshS2(); Fire(); };
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
                RefreshS2();
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
            nameBox.LostFocus   += (s, e) => { RefreshS2(); _refreshStep?.Invoke("S1"); };
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
                RefreshS2();
                _refreshStep?.Invoke("S1");
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
                RefreshS2();
                _refreshStep?.Invoke("S1");
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
                RefreshS2();
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
            cb.Checked   += (s, e) => { _unassignedEnabled = true;  RefreshS2(); Fire(); };
            cb.Unchecked += (s, e) => { _unassignedEnabled = false; RefreshS2(); Fire(); };
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
                        RefreshS2();
                        _refreshStep?.Invoke("S1");
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

            var newBtn = ControlStyles.BuildSmallButton(AppStrings.T("export.bulkExport.sets.newSet"));
            newBtn.Margin = new Thickness(0, 0, 4, 4);
            newBtn.Click += (s, e) => PromptNewSet();
            wrap.Children.Add(newBtn);

            var byPrefix = ControlStyles.BuildSmallButton(AppStrings.T("export.bulkExport.sets.autoPrefix"));
            byPrefix.Margin = new Thickness(0, 0, 4, 4);
            byPrefix.Click += (s, e) => RunAutoGroup("Prefix");
            wrap.Children.Add(byPrefix);

            var byFolder = ControlStyles.BuildSmallButton(AppStrings.T("export.bulkExport.sets.autoFolder"));
            byFolder.Margin = new Thickness(0, 0, 4, 4);
            byFolder.Click += (s, e) => RunAutoGroup("Folder");
            wrap.Children.Add(byFolder);

            var importBtn = ControlStyles.BuildSmallButton(AppStrings.T("export.bulkExport.sets.importPrintSet"));
            importBtn.Margin = new Thickness(0, 0, 4, 4);
            importBtn.Click += (s, e) => ImportNextPrintSet();
            wrap.Children.Add(importBtn);

            var saveBtn = ControlStyles.BuildSmallButton(AppStrings.T("export.bulkExport.sets.saveSets"));
            saveBtn.Margin = new Thickness(0, 0, 8, 4);
            saveBtn.Click += (s, e) => SaveSets();
            wrap.Children.Add(saveBtn);

            _dirtyLabel = new TextBlock
            {
                FontStyle         = FontStyles.Italic,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _dirtyLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            _dirtyLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _dirtyLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            wrap.Children.Add(_dirtyLabel);
            UpdateDirtyLabel();

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
            RefreshS2();
            _refreshStep?.Invoke("S1");
            Fire();
        }

        // Imports the first print set not already present as a set. Kept as a plain button rather
        // than a dropdown: a Popup with StaysOpen=false crashes Revit, and repeated presses walk
        // the list without needing one.
        private void ImportNextPrintSet()
        {
            var existing = new HashSet<string>(_sets.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            var next = _availablePrintSets.FirstOrDefault(ps => !existing.Contains(ps.Name));
            if (next == null)
            {
                SetStatus(_availablePrintSets.Count == 0
                    ? AppStrings.T("export.bulkExport.labels.noPrintSets")
                    : AppStrings.T("export.bulkExport.sets.allPrintSetsImported"), isError: true);
                return;
            }

            var set = NewSet(next.Name);
            // A print set's own membership is unordered (ViewSheetSet.Views is a ViewSet), so
            // impose browser order on the way in.
            var ids = OrderByBrowser(next.MemberIds.Select(id => id.Value).Where(_idToName.ContainsKey));
            foreach (long id in ids)
            {
                RemoveFromAllSets(id);
                set.Members.Add(MakeMember(id));
            }
            _selectedIds = OrderByBrowser(_selectedIds.Concat(ids).Distinct());
            SetStatus(AppStrings.T("export.bulkExport.sets.imported", next.Name, set.Members.Count), isError: false);
            RefreshS2();
            _refreshStep?.Invoke("S1");
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
            handler.MemberIds = set.Members.Select(m => new ElementId(m.IdValue)).ToList();
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
