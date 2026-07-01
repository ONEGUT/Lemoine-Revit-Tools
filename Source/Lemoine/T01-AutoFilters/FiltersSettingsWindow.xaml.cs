using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Shapes;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Tools.AutoFilters;

namespace LemoineTools.Lemoine
{
    public partial class FiltersSettingsWindow : Window
    {
        // ── Live project pattern lists ───────────────────────────────────────
        internal IReadOnlyList<string> FillPatternNames { get; private set; } = Array.Empty<string>();
        internal IReadOnlyList<string> LinePatternNames { get; private set; } = Array.Empty<string>();

        internal void SetPatternLists(IEnumerable<string> fillPatterns, IEnumerable<string> linePatterns)
        {
            FillPatternNames = new ReadOnlyCollection<string>(fillPatterns.ToList());
            LinePatternNames = new ReadOnlyCollection<string>(linePatterns.ToList());
        }

        // ── Filter state ─────────────────────────────────────────────────────
        private List<FilterTradeConfig>? _filterTrades;
        private string? _fActiveTradeId;
        private string? _fActiveRuleId;
        private StackPanel?  _fRuleListPanel;
        private Border?      _fEditorBorder;
        private Border?      _fTradesSidebar;
        private StackPanel?  _fTradeListPanel;
        private LemoineListReorder? _fTradeReorder;   // drag-to-reorder trades
        private UIElement?   _fAddTradeAnchor;
        private TextBlock?   _fStatusText;     // transient status (template load/save/import/export)
        private Border?      _fStatusChip;
        private ScrollViewer? _fRuleScroll;     // for auto-scroll-to-new on add
        private ScrollViewer? _fTradeScroll;
        private string       _filtersSnapshot = ""; // serialized buffer at load (dirty check)
        private Border?     _fActiveRowBorder;
        private TextBlock?  _fActiveNameTb;
        private Border?     _fActiveColorDot;  // active rule-row swatch — repainted live on FG colour change
        private readonly HashSet<string>            _fSelectedRuleIds    = new HashSet<string>();
        private          string?                    _fShiftAnchorRuleId;
        private readonly Dictionary<string, Border> _fMultiSelectBorders = new Dictionary<string, Border>();

        // Trades EXCLUDED from "Apply selected trades to view" (per-trade sidebar checkbox).
        // Tracked as exclusions so trades added mid-session are applied by default.
        private readonly HashSet<string>            _fApplyExcludedTradeIds = new HashSet<string>();

        // ── Undo/redo history — snapshot stack over the in-window trade buffer ──
        // Captured by a low-frequency poll (coalesces rapid edits), so no per-control wiring.
        private readonly List<(string Label, string Snapshot)> _history = new List<(string, string)>();
        private int _historyIndex = -1;
        private System.Windows.Threading.DispatcherTimer? _historyTimer;
        private Button? _undoBtn;
        private Button? _redoBtn;

        // ── Active drag state (rule reorder) ─────────────────────────────────
        private string?  _dragRuleId;
        private Border?  _dragSourceBorder;
        private int      _dragSourceOrigIdx;
        private Point    _dragGhostClickOffset;
        private readonly LemoineDragGhost _ruleGhost = new LemoineDragGhost();   // rule-row drag ghost
        private Border?  _dragReadyBorder;
        private bool     _isRefreshingEditor;

        // ── Double-click rename timing ───────────────────────────────────────
        private DateTime _lastClickTime   = DateTime.MinValue;


        // ─────────────────────────────────────────────────────────────────────
        public FiltersSettingsWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;

            // Named handlers (not lambdas) so they can be detached in OnClosed — a leaked
            // subscription to this STA window after its dispatcher has shut down crashes/hangs
            // Revit on the next theme change.
            LemoineSettings.Instance.ThemeChanged  += OnThemeChanged;
            LemoineSettings.Instance.UiSizeChanged += OnUiSizeChanged;

            // Reload discovered rules when focus returns from the Discover window.
            Activated += OnWindowActivated;
        }

        private void OnThemeChanged(LemoineTheme t)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LemoineSettings.Instance.ApplyTo(Resources);
                Background = t.PageBg;
                _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            }));
        }

        private void OnUiSizeChanged(LemoineUiSize _)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LemoineSettings.Instance.ApplyScaleTo(Resources);
                LemoineControlStyles.InjectInto(Resources, scrollBarWidth: 8);
                UpdateRowHeights();
            }));
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LemoineSettings.Instance.ApplyTo(Resources);
            LemoineControlStyles.InjectInto(Resources, scrollBarWidth: 8);
            Background = LemoineSettings.Instance.ActiveTheme.PageBg;
            _root.SetResourceReference(Grid.BackgroundProperty, "LemoineBg");
            _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _outerBorder.CornerRadius = new CornerRadius(8);

            UpdateRowHeights();
            BuildToolbar();
            _contentBorder.Child = BuildFiltersContent();
            BuildFloatingStatus();
            SetupHistory();
        }

        // ── Undo/redo history ─────────────────────────────────────────────────
        // Seeds the baseline snapshot and starts a coalescing poll. Capturing on a timer (rather
        // than wiring every add/delete/rename/recolor/move/toggle) keeps it robust and lets rapid
        // edits collapse into one entry. Undo/redo/jump restore a snapshot; because the poll
        // compares against the CURRENT index's snapshot, a restore never re-captures itself.
        private void SetupHistory()
        {
            _history.Clear();
            _history.Add((LemoineStrings.T("autofilters.filtersWindow.window.history.opened"), SerializeTrades(_filterTrades)));
            _historyIndex = 0;
            UpdateUndoRedoEnabled();

            _historyTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(450),
            };
            // Guard the unattended tick so a transient serialize error can never bubble to the
            // window's dispatcher (this window has no last-resort unhandled-exception net).
            _historyTimer.Tick += (s, e) =>
            {
                try { CaptureHistoryIfChanged(); }
                catch (Exception ex) { LemoineLog.Swallowed("AutoFilters: history capture", ex); }
            };
            _historyTimer.Start();
        }

        private void CaptureHistoryIfChanged()
        {
            if (_filterTrades == null || _historyIndex < 0) return;
            string cur = SerializeTrades(_filterTrades);
            if (cur == _history[_historyIndex].Snapshot) return;

            string label = DeriveHistoryLabel(_history[_historyIndex].Snapshot, cur);

            // Drop any redo tail before recording the new branch.
            if (_historyIndex < _history.Count - 1)
                _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);

            // Cap history so a long session can't grow unbounded.
            const int MaxHistory = 100;
            if (_history.Count >= MaxHistory)
            {
                _history.RemoveAt(0);
                _historyIndex--;
            }

            _history.Add((label, cur));
            _historyIndex = _history.Count - 1;
            UpdateUndoRedoEnabled();
        }

        private void HistoryUndo()
        {
            CaptureHistoryIfChanged();                 // fold any pending edit in first
            if (_historyIndex > 0) { _historyIndex--; RestoreHistory(); }
        }

        private void HistoryRedo()
        {
            if (_historyIndex >= 0 && _historyIndex < _history.Count - 1) { _historyIndex++; RestoreHistory(); }
        }

        private void HistoryJumpTo(int index)
        {
            CaptureHistoryIfChanged();
            if (index >= 0 && index < _history.Count && index != _historyIndex)
            {
                _historyIndex = index;
                RestoreHistory();
            }
        }

        private void RestoreHistory()
        {
            _filterTrades = DeserializeTrades(_history[_historyIndex].Snapshot) ?? new List<FilterTradeConfig>();

            if (_fActiveTradeId == null || !_filterTrades.Any(t => t.Id == _fActiveTradeId))
                _fActiveTradeId = _filterTrades.FirstOrDefault()?.Id;
            var at = _filterTrades.FirstOrDefault(t => t.Id == _fActiveTradeId);
            if (_fActiveRuleId == null || at?.Rules.All(r => r.Id != _fActiveRuleId) == true)
                _fActiveRuleId = at?.Rules.FirstOrDefault()?.Id;

            ClearMultiSelection();
            FRefreshTradesSidebar();
            FRefreshRuleList();
            FRefreshRuleEditor();
            UpdateUndoRedoEnabled();
        }

        private void UpdateUndoRedoEnabled()
        {
            if (_undoBtn != null) _undoBtn.IsEnabled = _historyIndex > 0;
            if (_redoBtn != null) _redoBtn.IsEnabled = _historyIndex >= 0 && _historyIndex < _history.Count - 1;
        }

        // Derives a short, human label by diffing the previous and current snapshots. Trade/rule
        // count changes give add/delete; otherwise a rename, a move (rule changed trade), else a
        // generic edit of the active rule. Rule ids can repeat after a trade duplicate, so dictionary
        // writes use the indexer (last-wins), never ToDictionary (which would throw on a duplicate).
        private string DeriveHistoryLabel(string prevSnap, string curSnap)
        {
            var prev = DeserializeTrades(prevSnap) ?? new List<FilterTradeConfig>();
            var cur  = DeserializeTrades(curSnap)  ?? new List<FilterTradeConfig>();

            int pt = prev.Count, ct = cur.Count;
            if (ct > pt) return ct - pt == 1 ? LemoineStrings.T("autofilters.filtersWindow.window.history.addTrade")    : LemoineStrings.T("autofilters.filtersWindow.window.history.addTradesPlural", ct - pt);
            if (ct < pt) return pt - ct == 1 ? LemoineStrings.T("autofilters.filtersWindow.window.history.deleteTrade") : LemoineStrings.T("autofilters.filtersWindow.window.history.deleteTradesPlural", pt - ct);

            int pr = prev.Sum(t => t.Rules?.Count ?? 0);
            int cr = cur.Sum(t => t.Rules?.Count ?? 0);
            if (cr > pr) return cr - pr == 1 ? LemoineStrings.T("autofilters.filtersWindow.window.history.addRule")    : LemoineStrings.T("autofilters.filtersWindow.window.history.addRulesPlural", cr - pr);
            if (cr < pr) return pr - cr == 1 ? LemoineStrings.T("autofilters.filtersWindow.window.history.deleteRule") : LemoineStrings.T("autofilters.filtersWindow.window.history.deleteRulesPlural", pr - cr);

            var prevName   = new Dictionary<string, string>();
            var prevTrade  = new Dictionary<string, string>();
            foreach (var t in prev)
                foreach (var r in t.Rules ?? new List<FilterRuleConfig>())
                { prevName[r.Id] = r.Name ?? ""; prevTrade[r.Id] = t.Id; }

            foreach (var t in cur)
                foreach (var r in t.Rules ?? new List<FilterRuleConfig>())
                {
                    if (prevTrade.TryGetValue(r.Id, out var pid) && pid != t.Id)
                        return LemoineStrings.T("autofilters.filtersWindow.window.history.moveRules");
                    if (prevName.TryGetValue(r.Id, out var pn) && pn != (r.Name ?? ""))
                        return LemoineStrings.T("autofilters.filtersWindow.window.history.rename", pn, r.Name);
                }

            var active = cur.FirstOrDefault(t => t.Id == _fActiveTradeId)?
                .Rules?.FirstOrDefault(r => r.Id == _fActiveRuleId);
            return active != null ? LemoineStrings.T("autofilters.filtersWindow.window.history.edit", active.Name) : LemoineStrings.T("autofilters.filtersWindow.window.history.editGeneric");
        }

        // History dropdown — entries newest-first. The current position is highlighted ("now");
        // entries after it are redoable ("redo", italic). Clicking any entry jumps to that state.
        private void ShowHistoryPopup(UIElement anchor)
        {
            CaptureHistoryIfChanged(); // make sure a pending edit shows up in the list

            var popup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget    = anchor,
                Placement          = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                StaysOpen          = false,
                AllowsTransparency = true,
            };

            var outer = new Border
            {
                Width           = 268, BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6), Margin = new Thickness(0, 2, 0, 0),
            };
            outer.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");

            var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 6) };
            var hdr = MiniLabel(LemoineStrings.T("autofilters.filtersWindow.window.history.popupHeader"));
            hdr.Margin = new Thickness(12, 0, 12, 6);
            panel.Children.Add(hdr);
            var sep = new Border { Height = 1 };
            sep.SetResourceReference(Border.BackgroundProperty, "LemoineBorder");
            panel.Children.Add(sep);

            var list = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            for (int i = _history.Count - 1; i >= 0; i--)
            {
                int idx     = i;
                bool isCur  = idx == _historyIndex;
                bool isRedo = idx > _historyIndex;

                var row = new Border { Padding = new Thickness(12, 7, 10, 7), Cursor = Cursors.Hand };
                if (isCur) row.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");
                else       row.Background = Brushes.Transparent;

                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var lbl = new TextBlock
                {
                    Text              = _history[idx].Label,
                    TextTrimming      = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontStyle         = isRedo ? FontStyles.Italic : FontStyles.Normal,
                    FontWeight        = isCur ? FontWeights.SemiBold : FontWeights.Normal,
                };
                lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                lbl.SetResourceReference(TextBlock.ForegroundProperty,
                    isCur ? "LemoineAccent" : (isRedo ? "LemoineTextDim" : "LemoineText"));
                Grid.SetColumn(lbl, 0);
                rowGrid.Children.Add(lbl);

                string tagText = isCur ? LemoineStrings.T("autofilters.filtersWindow.window.history.tagNow") : (isRedo ? LemoineStrings.T("autofilters.filtersWindow.window.history.tagRedo") : "");
                if (tagText.Length > 0)
                {
                    var tag = new Border
                    {
                        CornerRadius      = new CornerRadius(8),
                        Padding           = new Thickness(6, 1, 6, 1),
                        Margin            = new Thickness(6, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    tag.SetResourceReference(Border.BackgroundProperty, isCur ? "LemoineAccent" : "LemoineBorder");
                    var tagTb = new TextBlock { Text = tagText };
                    tagTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XS");
                    tagTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineKnobOn");
                    tagTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                    tag.Child = tagTb;
                    Grid.SetColumn(tag, 1);
                    rowGrid.Children.Add(tag);
                }

                row.Child = rowGrid;
                if (!isCur)
                {
                    row.MouseEnter += (s, e) => row.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
                    row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
                }
                row.MouseLeftButtonUp += (s, e) => { e.Handled = true; popup.IsOpen = false; HistoryJumpTo(idx); };
                list.Children.Add(row);
            }

            var listScroll = new ScrollViewer
            {
                MaxHeight                     = 340,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content                       = list,
            };
            LemoineControlStyles.SetSelfContainedScroll(listScroll, true);
            panel.Children.Add(listScroll);

            outer.Child  = panel;
            popup.Child  = outer;
            popup.IsOpen = true;
        }

        // Persist buffered edits and auto-create the project's filters when the window closes.
        // There is no Create button anymore — closing the window is the single commit point.
        // Filters are (re)generated only when something changed since load, or when the saved
        // manifest no longer matches the rules (missing or orphaned filters). Generation runs
        // on Revit's main thread via the external event; any failures are reported there by a
        // TaskDialog, because this STA window is already tearing down.
        protected override void OnClosed(EventArgs e)
        {
            LemoineSettings.Instance.ThemeChanged  -= OnThemeChanged;
            LemoineSettings.Instance.UiSizeChanged -= OnUiSizeChanged;
            Activated -= OnWindowActivated;
            _historyTimer?.Stop();
            _historyTimer = null;

            if (_filterTrades != null)
            {
                bool dirty = SerializeTrades(_filterTrades) != _filtersSnapshot;
                if (dirty)
                {
                    AutoFiltersSettings.Instance.Trades = _filterTrades;
                    AutoFiltersSettings.Instance.Save();
                }

                var expected     = AutoFiltersSettings.ComputeExpectedFilterNames(_filterTrades);
                bool manifestStale = !expected.SetEquals(
                    AutoFiltersSettings.Instance.CreatedFilterNames ?? new List<string>());

                if ((dirty || manifestStale) && expected.Count > 0)
                {
                    // Only refresh the definitions of rules whose category/parameter/match
                    // definition actually changed in the menu — every other existing filter is
                    // left untouched so edits made outside the menu (Revit's own filter editor)
                    // survive. Missing filters are still (re)created and orphans removed.
                    var snapshotTrades = DeserializeTrades(_filtersSnapshot);
                    var changed = AutoFiltersSettings.ComputeChangedFilterNames(
                        snapshotTrades, _filterTrades);
                    // Colour/override edits (which the definition-change set ignores) are propagated
                    // across every view & template already carrying the filter on close.
                    var changedOverride = AutoFiltersSettings.ComputeChangedOverrideFilterNames(
                        snapshotTrades, _filterTrades);
                    RaiseAutoCreate(changed, changedOverride);
                }
            }

            base.OnClosed(e);
        }

        // Queues the filter-creation pass for Revit's main thread. Raise() returns immediately;
        // Revit runs the handler at its next idle moment, after this window has closed — which
        // is why failures are surfaced by the handler's own TaskDialog (ShowFailureDialog),
        // never marshalled back to this dispatcher.
        private void RaiseAutoCreate(HashSet<string>? changedFilterNames, HashSet<string>? changedOverrideNames)
        {
            var handler = App.AutoFiltersHandler;
            var evt     = App.AutoFiltersEvent;
            if (handler == null || evt == null)
            {
                LemoineLog.Warn("FiltersSettingsWindow", "Auto-create skipped: event handler unavailable.");
                return;
            }

            handler.CreateOnly                = true;
            handler.ShowFailureDialog         = true;
            handler.ChangedFilterNames        = changedFilterNames;
            // Re-apply colour/override edits across every view & template already carrying the filter.
            handler.ApplyOverrideFilterNames  = changedOverrideNames;
            handler.SelectedDisciplines       = new List<string>();
            handler.SelectedLinkTitles        = new List<string>();
            handler.PushLog                   = null;
            handler.OnProgress                = null;
            handler.OnComplete                = null;

            evt.Raise();
        }

        // Opens the Discover Rules window from the sidebar's Discover button. Persists the working
        // buffer first so Discover reads the latest rules, then raises an ExternalEvent (the window
        // setup needs Revit's main thread for category capture and link enumeration). Newly
        // discovered trades/rules are reloaded when this window regains focus (OnWindowActivated).
        private void LaunchDiscover()
        {
            if (_filterTrades != null)
            {
                // Persist a DEEP COPY (not the live buffer) so Discover mutates a separate list.
                // That keeps _filterTrades == _filtersSnapshot here, so OnWindowActivated can detect
                // Discover's additions as a real external change and reload + refresh the UI.
                AutoFiltersSettings.Instance.Trades = AutoFiltersSettings.DeepCopy(_filterTrades);
                AutoFiltersSettings.Instance.Save();
                _filtersSnapshot = SerializeTrades(_filterTrades);
            }

            var evt = App.OpenDiscoverEvent;
            if (evt == null)
            {
                LemoineLog.Warn("FiltersSettingsWindow", "Discover unavailable: event handler not registered.");
                FlashStatus(LemoineStrings.T("autofilters.filtersWindow.window.status.discoverUnavailable"));
                return;
            }

            evt.Raise();
            FlashStatus(LemoineStrings.T("autofilters.filtersWindow.window.status.openingDiscover"));
        }

        // When focus returns (e.g. after Discover wrote new rules into the shared AutoFiltersSettings
        // singleton), reload the working buffer — but ONLY when this window has no unsaved edits, so
        // in-progress work is never clobbered. The buffer is persisted before Discover opens, so on
        // return with no further edits the buffer still equals its snapshot and a real external
        // change is detected by a serialized comparison against the settings singleton.
        private void OnWindowActivated(object sender, EventArgs e)
        {
            if (_filterTrades == null) return;

            string current = SerializeTrades(_filterTrades);
            if (current != _filtersSnapshot) return;                       // unsaved edits — leave alone
            string latest = SerializeTrades(AutoFiltersSettings.Instance.Trades);
            if (latest == current) return;                                 // nothing changed externally

            _filterTrades    = AutoFiltersSettings.DeepCopy(AutoFiltersSettings.Instance.Trades);
            _filtersSnapshot = SerializeTrades(_filterTrades);

            if (_fActiveTradeId == null || !_filterTrades.Any(t => t.Id == _fActiveTradeId))
                _fActiveTradeId = _filterTrades.FirstOrDefault()?.Id;
            var at = _filterTrades.FirstOrDefault(t => t.Id == _fActiveTradeId);
            if (_fActiveRuleId == null || at?.Rules.All(r => r.Id != _fActiveRuleId) == true)
                _fActiveRuleId = at?.Rules.FirstOrDefault()?.Id;

            FRefreshTradesSidebar();
            FRefreshRuleList();
            FRefreshRuleEditor();
        }

        // Rule-list footer: applies ONLY the active trade's filters to Revit's current view.
        private void ApplyActiveTradeToView()
        {
            if (_filterTrades == null) return;
            var trade = _filterTrades.FirstOrDefault(t => t.Id == _fActiveTradeId);
            if (trade == null) { FlashStatus(LemoineStrings.T("autofilters.filtersWindow.window.status.noTradeSelected")); return; }
            ApplyTradesToView(new List<FilterTradeConfig> { trade });
        }

        // Trades-sidebar footer: applies every checked trade's filters to Revit's current view.
        // Selection is tracked as an EXCLUSION set, so trades added during the session are
        // applied by default and a checkbox toggle removes/restores them.
        private void ApplySelectedTradesToView()
        {
            if (_filterTrades == null) return;
            var selected = _filterTrades.Where(t => !_fApplyExcludedTradeIds.Contains(t.Id)).ToList();
            if (selected.Count == 0) { FlashStatus(LemoineStrings.T("autofilters.filtersWindow.window.status.noTradesSelected")); return; }
            ApplyTradesToView(selected);
        }

        // Trades-sidebar footer (Remove half): detaches the checked trades' filters from Revit's
        // current view. Non-destructive — the filters stay in the project. Uses the same per-trade
        // checkbox selection as "Apply to view". Externally-managed trades carry filters even when
        // their rule has no keyword definition, so their names are always included.
        private void RemoveSelectedTradesFromView()
        {
            if (_filterTrades == null) return;
            var selected = _filterTrades.Where(t => !_fApplyExcludedTradeIds.Contains(t.Id)).ToList();
            if (selected.Count == 0) { FlashStatus(LemoineStrings.T("autofilters.filtersWindow.window.status.noTradesSelected")); return; }

            var handler = App.DeleteFiltersHandler;
            var evt     = App.DeleteFiltersEvent;
            if (handler == null || evt == null)
            {
                LemoineLog.Warn("FiltersSettingsWindow", "Remove from view unavailable: handler not registered.");
                FlashStatus(LemoineStrings.T("autofilters.filtersWindow.window.status.removeUnavailable"));
                return;
            }

            var names = new List<string>();
            foreach (var t in selected)
                foreach (var r in t.Rules)
                {
                    if (!r.Enabled) continue;
                    if (!t.ExternallyManaged && !AutoFiltersSettings.RuleProducesFilter(r)) continue;
                    names.Add(AutoFiltersSettings.MakeFilterName(t.Id, r.Name));
                }

            if (names.Count == 0) { FlashStatus(LemoineStrings.T("autofilters.filtersWindow.window.status.noFiltersToRemove")); return; }

            handler.SelectedFilterNames = names;
            handler.PushLog    = null;
            handler.OnProgress = null;
            handler.OnComplete = null;
            evt.Raise();
            FlashStatus(selected.Count == 1
                ? LemoineStrings.T("autofilters.filtersWindow.window.status.removingOne", selected[0].Label)
                : LemoineStrings.T("autofilters.filtersWindow.window.status.removingMany", selected.Count));
        }

        // Toolbar "Delete from Project" — opens the existing Delete-from-Project picker window
        // (main-thread setup via ExternalEvent). Operates on the project's actual filters,
        // independent of this window's working buffer.
        private void OpenDeleteFromProject()
        {
            var evt = App.OpenDeleteFromProjectEvent;
            if (evt == null)
            {
                LemoineLog.Warn("FiltersSettingsWindow", "Delete from Project unavailable: handler not registered.");
                FlashStatus(LemoineStrings.T("autofilters.filtersWindow.window.status.deleteFromProjectUnavailable"));
                return;
            }
            evt.Raise();
            FlashStatus(LemoineStrings.T("autofilters.filtersWindow.window.status.openingDeleteFromProject"));
        }

        // Creates and applies the given trades' filters to Revit's current view.
        // Persists the working buffer first so the handler reads the latest (possibly just-merged)
        // rules, then refreshes the dirty snapshot so OnClosed doesn't run a second redundant pass.
        // OverwriteFilterDefinition is forced true so an existing filter created before a rule was
        // merged is rebuilt with ALL its keywords. Externally-managed trades are NOT skipped — the
        // handler attaches their existing filters and re-applies overrides (IncludeSelectedExternallyManaged).
        private void ApplyTradesToView(List<FilterTradeConfig> trades)
        {
            if (_filterTrades == null || trades == null || trades.Count == 0) return;

            var handler = App.AutoFiltersHandler;
            var evt     = App.AutoFiltersEvent;
            if (handler == null || evt == null)
            {
                LemoineLog.Warn("FiltersSettingsWindow", "Apply to view skipped: event handler unavailable.");
                FlashStatus(LemoineStrings.T("autofilters.filtersWindow.window.status.applyUnavailable"));
                return;
            }

            // Persist current edits so the handler (which reads AutoFiltersSettings.Instance)
            // sees them, and keep the snapshot in sync so closing won't re-run needlessly.
            AutoFiltersSettings.Instance.Trades = _filterTrades;
            AutoFiltersSettings.Instance.Save();
            _filtersSnapshot = SerializeTrades(_filterTrades);

            handler.CreateOnly                       = false;
            handler.OverwriteFilterDefinition        = true;
            handler.KeepExistingOverrides            = false;
            handler.IncludeSelectedExternallyManaged = true;
            handler.ShowFailureDialog                = true;
            handler.ChangedFilterNames               = null;
            handler.SelectedDisciplines              = trades.Select(t => t.Label).ToList();
            handler.SelectedLinkTitles               = new List<string>();
            handler.PushLog                          = null;
            handler.OnProgress                       = null;
            handler.OnComplete                       = null;

            evt.Raise();
            FlashStatus(trades.Count == 1
                ? LemoineStrings.T("autofilters.filtersWindow.window.status.applyingOne", trades[0].Label)
                : LemoineStrings.T("autofilters.filtersWindow.window.status.applyingMany", trades.Count));
        }

        // ── Floating bottom-right status chip ───────────────────────────────────
        // The Create pill was removed (filters auto-create on close); the chip remains
        // to surface transient template messages (load / save / import / export).
        private void BuildFloatingStatus()
        {
            _fStatusChip = LemoineControlStyles.BuildStatusChip(out _fStatusText);
            _fStatusChip.HorizontalAlignment = HorizontalAlignment.Right;
            _fStatusChip.VerticalAlignment   = VerticalAlignment.Bottom;
            _fStatusChip.Visibility          = Visibility.Collapsed;

            _floatingSlot.Content = _fStatusChip;

            // Reserve space at the bottom of the editor so its last card clears the chip.
            _floatingSlot.SizeChanged += (s, e) => ApplyFloatingInset(e.NewSize.Height);
            ApplyFloatingInset(_floatingSlot.ActualHeight);
        }

        // Insets the editor's bottom so content isn't hidden behind the floating chip.
        private void ApplyFloatingInset(double chipHeight)
        {
            if (_fEditorBorder == null) return;
            double inset = Math.Max(0, chipHeight) + 16;
            _fEditorBorder.Padding = new Thickness(0, 0, 0, inset);
        }

        private void FlashStatus(string msg)
        {
            if (_fStatusText == null) return;
            _fStatusText.Text = msg;
            if (_fStatusChip != null) _fStatusChip.Visibility = Visibility.Visible;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (s, e) =>
            {
                _fStatusText.Text = "";
                if (_fStatusChip != null) _fStatusChip.Visibility = Visibility.Collapsed;
                timer.Stop();
            };
            timer.Start();
        }

        // Serializes the trade buffer for a cheap structural dirty comparison.
        // On failure we return a unique token so the window saves (never loses edits).
        private static string SerializeTrades(List<FilterTradeConfig>? trades)
        {
            if (trades == null) return "";
            try
            {
                var xs = new System.Xml.Serialization.XmlSerializer(typeof(List<FilterTradeConfig>));
                using (var sw = new System.IO.StringWriter())
                {
                    xs.Serialize(sw, trades);
                    return sw.ToString();
                }
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("FiltersSettingsWindow.SerializeTrades", ex);
                return Guid.NewGuid().ToString(); // treat as dirty → save
            }
        }

        // Reverses SerializeTrades for the load-time snapshot so the close pass can diff it
        // against the current buffer and refresh only the rules that actually changed. On any
        // failure we return an empty list, which makes ComputeChangedFilterNames treat every
        // current rule as changed — the safe fallback that preserves the old "refresh all".
        private static List<FilterTradeConfig> DeserializeTrades(string? xml)
        {
            if (string.IsNullOrEmpty(xml)) return new List<FilterTradeConfig>();
            try
            {
                var xs = new System.Xml.Serialization.XmlSerializer(typeof(List<FilterTradeConfig>));
                using (var sr = new System.IO.StringReader(xml))
                    return (List<FilterTradeConfig>)xs.Deserialize(sr) ?? new List<FilterTradeConfig>();
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("FiltersSettingsWindow.DeserializeTrades", ex);
                return new List<FilterTradeConfig>();
            }
        }

        private void UpdateRowHeights()
        {
            if (_root == null) return;
            _root.RowDefinitions[0].Height = new GridLength(LemoineSettings.Instance.ToolbarHeight);
        }

        // ── Toolbar ───────────────────────────────────────────────────────────
        // Identical structure to the Legend window: [⚙ icon + title] … [× close].
        // Filters are created automatically on close — there is no Create action here.
        private void BuildToolbar()
        {
            _toolbarBorder.BorderThickness = new Thickness(0);

            // The "Apply to view" / "Remove from view" actions live as docked footer bars on the
            // rule list and trades sidebar. The toolbar carries the destructive "Delete from
            // Project" action (opens the existing window) plus the window close button.
            var deleteProjBtn = LemoineControlStyles.BuildSmallButton(
                LemoineStrings.T("autofilters.filtersWindow.window.toolbar.deleteFromProject"), LemoineControlStyles.LemoineButtonVariant.Danger);
            deleteProjBtn.VerticalAlignment = VerticalAlignment.Center;
            deleteProjBtn.Margin            = new Thickness(0, 0, 8, 0);
            deleteProjBtn.ToolTip           = LemoineStrings.T("autofilters.filtersWindow.window.toolbar.deleteFromProjectTooltip");
            deleteProjBtn.Click += (s, e) => OpenDeleteFromProject();

            // Undo / Redo / History for in-window menu edits.
            Button IconBtn(int codepoint, string tip, Action onClick)
            {
                var b = LemoineControlStyles.BuildSmallButton(char.ConvertFromUtf32(codepoint));
                b.FontFamily        = new System.Windows.Media.FontFamily("Segoe MDL2 Assets");
                b.VerticalAlignment = VerticalAlignment.Center;
                b.Margin            = new Thickness(0, 0, 4, 0);
                b.ToolTip           = tip;
                b.Click += (s, e) => onClick();
                return b;
            }
            _undoBtn = IconBtn(0xE7A7, LemoineStrings.T("autofilters.filtersWindow.window.toolbar.undoTooltip"), HistoryUndo);  // Segoe MDL2: Undo
            _redoBtn = IconBtn(0xE7A6, LemoineStrings.T("autofilters.filtersWindow.window.toolbar.redoTooltip"), HistoryRedo);  // Segoe MDL2: Redo

            var historyBtn = LemoineControlStyles.BuildSmallButton(LemoineStrings.T("autofilters.filtersWindow.window.toolbar.historyButton"));
            historyBtn.VerticalAlignment = VerticalAlignment.Center;
            historyBtn.Margin            = new Thickness(0, 0, 12, 0);
            historyBtn.ToolTip           = LemoineStrings.T("autofilters.filtersWindow.window.toolbar.historyButtonTooltip");
            historyBtn.Click += (s, e) => ShowHistoryPopup(historyBtn);

            var closeBtn = BuildFlatButton("×");
            closeBtn.SetResourceReference(Button.ForegroundProperty, "LemoineTextDim");
            closeBtn.Click += (s, e) => Close();

            var rightPanel = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            rightPanel.Children.Add(_undoBtn);
            rightPanel.Children.Add(_redoBtn);
            rightPanel.Children.Add(historyBtn);
            rightPanel.Children.Add(deleteProjBtn);
            rightPanel.Children.Add(closeBtn);

            _toolbarBorder.Child = new Controls.LemoineTitleBar
            {
                Title        = LemoineStrings.T("autofilters.filtersWindow.window.toolbar.title"),
                IconGlyph    = "⚙",
                RightContent = rightPanel,
            };
        }

        // ═════════════════════════════════════════════════════════════════════
        // Shared helpers (used by the partial Filters class)
        // ═════════════════════════════════════════════════════════════════════

        private static SolidColorBrush BrushFromHex(string? hex) =>
            BrushHelper.BrushFromHex(hex, LemoineTheme.FallbackGrey);

        private static Color HexToMediaColor(string hex) =>
            BrushHelper.ColorFromHex(hex, LemoineTheme.FallbackGrey);

        private static TextBlock MiniLabel(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 0) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            return tb;
        }

        private static Button BuildFlatButton(string label) =>
            LemoineControlStyles.BuildButton(label, LemoineControlStyles.LemoineButtonVariant.Ghost);

        private static Button FlatSmBtn(string label) =>
            LemoineControlStyles.BuildSmallButton(label, LemoineControlStyles.LemoineButtonVariant.Ghost);

        private static ComboBox BuildAutoCompleteBox(
            string[] items, string initial, Action<string> onChange, double width = 200)
        {
            var combo = new ComboBox
            {
                IsEditable          = true,
                IsTextSearchEnabled = false,
                StaysOpenOnEdit     = true,
                MaxDropDownHeight   = 200,
                Width               = width,
                Text                = initial,
                ItemsSource         = items,
            };
            combo.SetResourceReference(ComboBox.BackgroundProperty,  "LemoineSelectBg");
            combo.SetResourceReference(ComboBox.ForegroundProperty,  "LemoineText");
            combo.SetResourceReference(ComboBox.FontSizeProperty,    "LemoineFS_SM");
            combo.SetResourceReference(ComboBox.FontFamilyProperty,  "LemoineMonoFont");
            combo.SetResourceReference(ComboBox.BorderBrushProperty, "LemoineBorderMid");

            bool suppressing = false;

            combo.PreviewTextInput += (s, e) =>
            {
                if (suppressing) return;
                combo.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (suppressing) return;
                    string typed = combo.Text;
                    var innerTb = combo.Template?.FindName("PART_EditableTextBox", combo) as TextBox;
                    int caret = innerTb != null ? innerTb.SelectionStart + innerTb.SelectionLength : typed.Length;
                    var filtered = items.Where(i => i.IndexOf(typed, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();

                    suppressing = true;
                    combo.ItemsSource = filtered.Length > 0 ? (System.Collections.IEnumerable)filtered : items;
                    combo.Text = typed;
                    if (innerTb != null) innerTb.SelectionStart = Math.Min(caret, typed.Length);
                    combo.IsDropDownOpen = filtered.Length > 0;
                    suppressing = false;
                }));
            };

            combo.SelectionChanged += (s, e) =>
            {
                if (!suppressing && combo.SelectedItem is string sel)
                    onChange(sel);
            };

            return combo;
        }

        private UIElement BuildTrashConfirmButton(string confirmLabel, Action onConfirm)
        {
            var btn = new Border
            {
                Cursor              = Cursors.Hand,
                Padding             = new Thickness(5, 5, 5, 5),
                BorderThickness     = new Thickness(1),
                CornerRadius        = new CornerRadius(3),
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background          = Brushes.Transparent,
            };
            btn.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var icon = new TextBlock
            {
                Text                = "",
                FontFamily          = new FontFamily("Segoe MDL2 Assets"),
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment       = TextAlignment.Center,
                IsHitTestVisible    = false,
            };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            icon.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            btn.Child = icon;

            btn.MouseEnter += (s, e) =>
            {
                btn.SetResourceReference(Border.BorderBrushProperty, "LemoineRed");
                icon.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
            };
            btn.MouseLeave += (s, e) =>
            {
                btn.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                icon.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            };
            btn.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                var popup = new Popup
                {
                    PlacementTarget    = btn,
                    Placement          = PlacementMode.Bottom,
                    StaysOpen          = false,
                    AllowsTransparency = false,
                };
                var outer = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(4),
                    Padding         = new Thickness(10, 8, 10, 8),
                    Margin          = new Thickness(0, 2, 0, 0),
                };
                outer.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
                outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
                var confirmBtn = FlatSmBtn(confirmLabel);
                confirmBtn.SetResourceReference(Button.ForegroundProperty,  "LemoineRed");
                confirmBtn.SetResourceReference(Button.BorderBrushProperty, "LemoineRed");
                confirmBtn.Click += (ss, ee) => { popup.IsOpen = false; onConfirm(); };
                var cancelBtn = FlatSmBtn(LemoineStrings.T("autofilters.filtersWindow.window.common.cancel"));
                cancelBtn.Margin = new Thickness(6, 0, 0, 0);
                cancelBtn.Click += (ss, ee) => popup.IsOpen = false;
                var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
                btnRow.Children.Add(confirmBtn);
                btnRow.Children.Add(cancelBtn);
                outer.Child  = btnRow;
                popup.Child  = outer;
                popup.IsOpen = true;
            };
            return btn;
        }
    }
}
