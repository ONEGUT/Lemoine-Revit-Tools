using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;
using WpfGrid       = System.Windows.Controls.Grid;
using WpfVisibility = System.Windows.Visibility;

namespace LemoineTools.Tools.AutoFilters
{
    // =========================================================================
    // DiscoverViewModel — ILemoineTool + ILemoineNavigable + ILemoineStepConfirmable
    //
    // Step flow:
    //   S1  Select which loaded Revit links to scan (one trade per link)
    //   S2  Configure links: per-link accordion — "Discover Rules →" Confirm starts scan
    //   S3  Scanning — live log + progress; user confirms manually to advance
    //   S4  Review discovered rules grouped by trade: include/exclude, rename, colour
    //   S5  Summary card + Confirm triggers DiscoverEventHandler (Commit)
    // =========================================================================

    public class DiscoverViewModel : ILemoineTool, ILemoineNavigable, ILemoineStepConfirmable, ILemoineRunResult, ILemoineToolCleanup
    {
        // Self-describing result label for the run strip (see ILemoineRunResult).
        public string? ResultNoun => "rules";
        public System.Collections.Generic.IReadOnlyList<LemoineTools.Lemoine.ResultChip>? ResultChips => null;

        // ── Nested types ──────────────────────────────────────────────────────

        public class LinkEntry
        {
            public ElementId Id         { get; }
            public string    Label      { get; }
            public string    TradeName  { get; set; }
            public bool      IsSelected { get; set; }
            public bool      IsExpanded { get; set; } = true;
            public List<string>        SelectedCategories { get; } = new List<string>();
            public List<ScanConfigRow> ConfigRows         { get; } = new List<ScanConfigRow>();

            public LinkEntry(ElementId id, string label)
            {
                Id        = id;
                Label     = label;
                TradeName = label.Length > 20 ? label.Substring(0, 20).TrimEnd() : label;
            }
        }

        public class ScanConfigRow
        {
            public string   CategoryLabel   { get; }
            public string   OstCategory     { get; }
            public string   Mode            { get; set; } = "PerValue";
            public string   Parameter       { get; set; }
            public string[] AvailableParams { get; }
            public bool IsKnownSafe => DiscoverEventHandler.KnownSafeParams.Contains(Parameter);

            public ScanConfigRow(string label, string ostCategory)
            {
                CategoryLabel   = label;
                OstCategory     = ostCategory;
                AvailableParams = AutoFiltersSettings.GetParametersFor(ostCategory);
                Parameter = AvailableParams.FirstOrDefault(
                    p => DiscoverEventHandler.KnownSafeParams.Contains(p))
                    ?? (AvailableParams.Length > 0 ? AvailableParams[0] : "Type Name");
            }
        }

        public class DiscoveredRuleRow
        {
            public bool   IsIncluded       { get; set; } = true;
            public string RuleName         { get; set; } = "";
            public string HexColor         { get; set; } = "#888888";
            public string TradeName        { get; set; } = "";
            public int    ElementCount     { get; set; }
            public bool   IsWholeCategory  { get; set; }
            public string ParameterValue   { get; set; } = "";
            public string Parameter        { get; set; } = "Type Name";
            public List<string> BuiltInCategories { get; set; } = new List<string>();
        }

        // ── ILemoineTool identity ─────────────────────────────────────────────

        public string Title    => LemoineStrings.T("autofilters.discover.title");
        public string RunLabel => LemoineStrings.T("autofilters.discover.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", LemoineStrings.T("autofilters.discover.steps.S1"),     required: true),
            new StepDefinition("S2", LemoineStrings.T("autofilters.discover.steps.S2"),  required: true),
            new StepDefinition("S3", LemoineStrings.T("autofilters.discover.steps.S3"),         required: true),
            new StepDefinition("S4", LemoineStrings.T("autofilters.discover.steps.S4"),     required: true),
            new StepDefinition("S5", LemoineStrings.T("autofilters.discover.steps.S5"), required: false),
        };

        public event EventHandler?     ValidationChanged;
        public event EventHandler<int>? NavigateRequested;
        private void RaiseValidation() => ValidationChanged?.Invoke(this, EventArgs.Empty);
        private void RaiseNavigate(int index) => NavigateRequested?.Invoke(this, index);

        // Null the callbacks parked on the static handlers so this VM isn't retained after close.
        public void OnWindowClosed()
        {
            if (_handler != null)
            {
                _handler.PushLog    = null;
                _handler.OnProgress = null;
                _handler.OnComplete = null;
            }
            if (_createHandler != null)
            {
                _createHandler.PushLog    = null;
                _createHandler.OnProgress = null;
                _createHandler.OnComplete = null;
            }
        }

        // ── ILemoineStepConfirmable ────────────────────────────────────────────
        public string? ConfirmLabelFor(string stepId) => stepId == "S2" ? LemoineStrings.T("autofilters.discover.labels.confirmS2") : null;
        public void OnStepConfirm(string stepId) { if (stepId == "S2") StartScan(); }

        // ── State ─────────────────────────────────────────────────────────────

        private readonly DiscoverEventHandler            _handler;
        private readonly Autodesk.Revit.UI.ExternalEvent _event;
        private readonly List<LinkEntry>                 _links;
        private readonly List<DiscoveredRuleRow>         _discoveredRules = new List<DiscoveredRuleRow>();

        // Optional: chain a CreateOnly Auto Filters pass after commit so the flow ends
        // with real ParameterFilterElements in the project (Phase 3.3). Null in the
        // standalone preview app, where no Revit document is available.
        private readonly AutoFiltersEventHandler?        _createHandler;
        private readonly Autodesk.Revit.UI.ExternalEvent? _createEvent;
        private bool _createAfterCommit = true;

        private bool        _scanComplete;
        private bool        _isScanning;
        private Dispatcher? _wpfDispatcher;

        // S2 UI
        private StackPanel? _s2CardsStack;

        // S3 scanning UI
        private TextBlock?    _s3StatusTb;
        private ProgressBar?  _s3Progress;
        private ScrollViewer? _s3LogScroll;
        private StackPanel?   _s3LogStack;

        // S4 review UI
        private StackPanel? _s4Panel;

        // S5 confirm UI
        private LemoineReviewSummary? _s5Review;

        // S2 single-expand accordion: one open card at a time
        private readonly List<(Action Open, Action Close)> _cardActions
            = new List<(Action Open, Action Close)>();
        private int _openCardIndex = 0;

        // S2 card header labels, keyed by link, so an S1 trade rename updates the
        // matching S2 header in place (the scan already reads link.TradeName live).
        private readonly Dictionary<ElementId, TextBlock> _s2HeaderTbs
            = new Dictionary<ElementId, TextBlock>();

        // S4 single-expand accordion
        private readonly List<(Action Open, Action Close)> _s4CardActions
            = new List<(Action Open, Action Close)>();
        private int _s4OpenCardIndex = 0;

        // Shared across all link cards — allocated once
        private static readonly Dictionary<string, List<string>> CategoryGroups =
            new Dictionary<string, List<string>>
            {
                ["MEP / HVAC"]    = new List<string> { "Ducts","Duct Fittings","Duct Accessories","Duct Insulation","Duct Linings","Air Terminals","Flex Ducts","Mechanical Equipment","Fabrication Ductwork" },
                ["Piping"]        = new List<string> { "Pipes","Pipe Fittings","Pipe Accessories","Pipe Insulation","Pipe Linings","Flex Pipes","Plumbing Fixtures","Sprinklers","Fabrication Pipework","Fabrication Hangers","Fabrication Containment" },
                ["Electrical"]    = new List<string> { "Cable Trays","Cable Tray Fittings","Conduits","Conduit Fittings","Electrical Equipment","Electrical Fixtures","Lighting Fixtures","Lighting Devices","Communication Devices","Fire Alarm Devices","Security Devices","Data Devices","Telephone Devices","Nurse Call Devices" },
                ["Structural"]    = new List<string> { "Structural Framing","Structural Columns","Structural Foundations","Structural Trusses","Rebar" },
                ["Architectural"] = new List<string> { "Walls","Floors","Roofs","Ceilings","Doors","Windows","Generic Models","Rooms","Spaces" },
            };

        // ── Constructor ───────────────────────────────────────────────────────

        public DiscoverViewModel(
            DiscoverEventHandler             handler,
            Autodesk.Revit.UI.ExternalEvent  externalEvent,
            List<LinkEntry>                  links,
            AutoFiltersEventHandler?         createHandler = null,
            Autodesk.Revit.UI.ExternalEvent? createEvent   = null)
        {
            _handler       = handler       ?? throw new ArgumentNullException(nameof(handler));
            _event         = externalEvent ?? throw new ArgumentNullException(nameof(externalEvent));
            _links         = links         ?? new List<LinkEntry>();
            _createHandler = createHandler;
            _createEvent   = createEvent;
        }

        // ═════════════════════════════════════════════════════════════════════
        // GetStepContent — called once per step at window-open time
        // ═════════════════════════════════════════════════════════════════════

        public FrameworkElement? GetStepContent(string stepId)
        {
            if (_wpfDispatcher == null)
                _wpfDispatcher = Dispatcher.CurrentDispatcher;

            switch (stepId)
            {
                case "S1": return BuildS1();
                case "S2": return BuildS2();
                case "S3": return BuildS3();
                case "S4": return BuildS4();
                case "S5": return BuildS5();
                default:   return null;
            }
        }

        // ── S1 — Select Links ─────────────────────────────────────────────────

        private FrameworkElement BuildS1()
        {
            var sv = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 320,
            };
            LemoineControlStyles.WireBubblingScroll(sv);

            var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            sp.Children.Add(MakeNote(
                LemoineStrings.T("autofilters.discover.labels.noteS1")));

            if (_links.Count == 0)
            {
                sp.Children.Add(new LemoineWarnBanner(
                    LemoineStrings.T("autofilters.discover.labels.noLinks")));
            }
            else
            {
                foreach (var link in _links)
                    sp.Children.Add(BuildLinkRow(link));
            }

            sv.Content = sp;
            return sv;
        }

        private FrameworkElement BuildLinkRow(LinkEntry link)
        {
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Margin          = new Thickness(0, 0, 0, 4),
                Padding         = new Thickness(8, 6, 8, 6),
            };
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            card.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");

            var g = new WpfGrid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(155) });

            var cb = new CheckBox
            {
                IsChecked         = link.IsSelected,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 8, 0),
            };
            cb.Checked   += (s, e) =>
            {
                link.IsSelected = true;
                RaiseValidation();
                _wpfDispatcher?.BeginInvoke(DispatcherPriority.Normal, new Action(RebuildS2Cards));
            };
            cb.Unchecked += (s, e) =>
            {
                link.IsSelected = false;
                RaiseValidation();
                _wpfDispatcher?.BeginInvoke(DispatcherPriority.Normal, new Action(RebuildS2Cards));
            };

            var lbl = new TextBlock
            {
                Text              = link.Label,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
            };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var tradeLbl = new TextBlock
            {
                Text              = LemoineStrings.T("autofilters.discover.labels.trade"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(8, 0, 6, 0),
            };
            tradeLbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tradeLbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tradeLbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var tradeBox = new TextBox
            {
                Text              = link.TradeName,
                Padding           = new Thickness(4, 2, 4, 2),
                VerticalAlignment = VerticalAlignment.Center,
                BorderThickness   = new Thickness(1),
            };
            tradeBox.SetResourceReference(TextBox.BackgroundProperty,  "LemoineSelectBg");
            tradeBox.SetResourceReference(TextBox.ForegroundProperty,  "LemoineText");
            tradeBox.SetResourceReference(TextBox.BorderBrushProperty, "LemoineBorder");
            tradeBox.SetResourceReference(TextBox.FontSizeProperty,    "LemoineFS_SM");
            tradeBox.SetResourceReference(TextBox.FontFamilyProperty,  "LemoineUiFont");
            tradeBox.TextChanged += (s, e) =>
            {
                link.TradeName = tradeBox.Text;
                if (_s2HeaderTbs.TryGetValue(link.Id, out var hdr)) hdr.Text = link.TradeName;
                RaiseValidation();
            };

            WpfGrid.SetColumn(cb,       0);
            WpfGrid.SetColumn(lbl,      1);
            WpfGrid.SetColumn(tradeLbl, 2);
            WpfGrid.SetColumn(tradeBox, 3);
            g.Children.Add(cb);
            g.Children.Add(lbl);
            g.Children.Add(tradeLbl);
            g.Children.Add(tradeBox);

            card.Child = g;
            return card;
        }

        // ── S2 — Configure Links ──────────────────────────────────────────────

        private FrameworkElement BuildS2()
        {
            var root = new StackPanel();
            root.Children.Add(MakeNote(
                LemoineStrings.T("autofilters.discover.labels.noteS2")));

            var sv = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 420,
                Margin    = new Thickness(0, 0, 0, 8),
            };
            LemoineControlStyles.WireBubblingScroll(sv);

            _s2CardsStack = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            RebuildS2Cards();
            sv.Content = _s2CardsStack;
            root.Children.Add(sv);

            return root;
        }

        private void RebuildS2Cards()
        {
            if (_s2CardsStack == null) return;
            _s2CardsStack.Children.Clear();
            _cardActions.Clear();
            _s2HeaderTbs.Clear();
            _openCardIndex = 0;

            var selectedLinks = _links.Where(l => l.IsSelected).ToList();
            if (selectedLinks.Count == 0)
            {
                _s2CardsStack.Children.Add(new LemoineWarnBanner(
                    LemoineStrings.T("autofilters.discover.labels.noLinksSelected")));
                return;
            }

            foreach (var link in selectedLinks)
                _s2CardsStack.Children.Add(BuildLinkCard(link));
        }

        private FrameworkElement BuildLinkCard(LinkEntry link)
        {
            var outer = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Margin          = new Thickness(0, 0, 0, 6),
            };
            outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            outer.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");

            var stack = new StackPanel();

            // ── Header ────────────────────────────────────────────────────────
            var header = new Border
            {
                Padding = new Thickness(10, 7, 10, 7),
                Cursor  = Cursors.Hand,
            };
            header.SetResourceReference(Border.BackgroundProperty,  "LemoineCard");
            header.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var headerGrid = new WpfGrid();
            headerGrid.Background = Brushes.Transparent;  // ensure empty star-column area is hit-testable
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var headerLeft = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var tradeTb = new TextBlock
            {
                Text              = link.TradeName,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 6, 0),
            };
            tradeTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            tradeTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tradeTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _s2HeaderTbs[link.Id] = tradeTb; // S1 rename updates this header in place

            var labelTb = new TextBlock
            {
                Text              = $"({link.Label})",
                VerticalAlignment = VerticalAlignment.Center,
            };
            labelTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            labelTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            labelTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            headerLeft.Children.Add(tradeTb);
            headerLeft.Children.Add(labelTb);
            WpfGrid.SetColumn(headerLeft, 0);
            headerGrid.Children.Add(headerLeft);

            var chevron = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            chevron.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            chevron.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            chevron.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            WpfGrid.SetColumn(chevron, 1);
            headerGrid.Children.Add(chevron);
            header.Child = headerGrid;

            // ── Body ─────────────────────────────────────────────────────────
            var body      = new Border { Padding = new Thickness(10, 8, 10, 8) };
            var bodyStack = new StackPanel();

            var catTabs = new LemoineMultiSelectTabs { MaxHeight = 200, Hierarchy = AutoFiltersSettings.CategorySubcategories };
            // Scroll-wheel handling is global (OnScrollViewerWheel) and MultiSelectTabs already wires
            // its own inner scrollers — no per-call-site wiring needed here.

            var configPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            configPanel.Children.Add(MakeNote(LemoineStrings.T("autofilters.discover.labels.selectCats")));

            // Subscribe BEFORE SetGroups (LemoineMultiSelectTabs contract: SetGroups fires
            // SelectionChanged once at the end). Re-building S2 cards on any S1 toggle must
            // restore each link's prior selection — pass link.SelectedCategories as the
            // initial selection so configured links don't get silently cleared.
            catTabs.SelectionChanged += cats =>
            {
                var newCats = cats?.ToList() ?? new List<string>();
                // Restoring the saved selection (the SetGroups initial fire) reports the
                // same set — only a real user change should invalidate a completed scan.
                bool changed = !new HashSet<string>(newCats, StringComparer.OrdinalIgnoreCase)
                    .SetEquals(link.SelectedCategories);
                link.SelectedCategories.Clear();
                link.SelectedCategories.AddRange(newCats);
                _wpfDispatcher?.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    RebuildLinkConfigPanel(link, configPanel);
                    if (changed) InvalidateScan();
                    else         RaiseValidation();
                }));
            };
            catTabs.SetGroups(CategoryGroups, link.SelectedCategories);

            bodyStack.Children.Add(catTabs);
            bodyStack.Children.Add(configPanel);
            body.Child = bodyStack;

            // ── Single-expand accordion wiring ────────────────────────────────
            int myIndex = _cardActions.Count;

            Action open = () =>
            {
                link.IsExpanded        = true;
                body.Visibility        = WpfVisibility.Visible;
                chevron.Text           = "▲";
                header.BorderThickness = new Thickness(0, 0, 0, 1);
                header.CornerRadius    = new CornerRadius(6, 6, 0, 0);
            };
            Action close = () =>
            {
                link.IsExpanded        = false;
                body.Visibility        = WpfVisibility.Collapsed;
                chevron.Text           = "▼";
                header.BorderThickness = new Thickness(0);
                header.CornerRadius    = new CornerRadius(6);
            };
            _cardActions.Add((open, close));

            // First card starts open; all others start closed
            if (myIndex == 0) open(); else close();

            // PreviewMouseLeftButtonDown tunnels before any child can consume it
            header.PreviewMouseLeftButtonDown += (s, e) => ExpandCard(myIndex);

            stack.Children.Add(header);
            stack.Children.Add(body);
            outer.Child = stack;
            return outer;
        }

        private void ExpandCard(int index)
        {
            int target;
            if (index == _openCardIndex)
            {
                // Clicking the currently open card: advance to next, or close all if it's the last
                target = (index + 1 < _cardActions.Count) ? index + 1 : -1;
            }
            else
            {
                target = index;
            }
            _openCardIndex = target;
            for (int i = 0; i < _cardActions.Count; i++)
            {
                if (i == target) _cardActions[i].Open();
                else             _cardActions[i].Close();
            }
        }

        private void RebuildLinkConfigPanel(LinkEntry link, StackPanel configPanel)
        {
            configPanel.Children.Clear();

            // Preserve existing rows so user's parameter selections survive category re-selection
            var existingByLabel = link.ConfigRows.ToDictionary(r => r.CategoryLabel, r => r);
            link.ConfigRows.Clear();

            foreach (var catLabel in link.SelectedCategories)
            {
                if (existingByLabel.TryGetValue(catLabel, out var existing))
                    link.ConfigRows.Add(existing);
                else if (AutoFiltersSettings.TryResolveCategoryOst(catLabel, out var ost))
                    link.ConfigRows.Add(new ScanConfigRow(catLabel, ost));
                else
                    // A curated S2 group label that the live document's category map doesn't
                    // expose (version-specific name, or a non-builtin category): no config row
                    // can be built, so the user's pick would silently vanish. Surface it.
                    LemoineLog.Warn("AutoFilters.Discover",
                        $"Category '{catLabel}' has no filterable OST mapping in this document — skipped.");
            }

            if (link.ConfigRows.Count == 0)
            {
                configPanel.Children.Add(MakeNote(LemoineStrings.T("autofilters.discover.labels.selectCats")));
            }
            else
            {
                configPanel.Children.Add(BuildConfigHeader());
                foreach (var row in link.ConfigRows)
                    configPanel.Children.Add(BuildConfigRow(row));
            }
        }

        private FrameworkElement BuildConfigHeader()
        {
            var border = new Border
            {
                Margin          = new Thickness(0, 0, 0, 2),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding         = new Thickness(4, 2, 4, 4),
            };
            border.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var g = new WpfGrid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });

            void H(string text, int col)
            {
                var tb = new TextBlock { Text = text };
                tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                WpfGrid.SetColumn(tb, col);
                g.Children.Add(tb);
            }
            H(LemoineStrings.T("autofilters.discover.labels.colCategory"),  0);
            H(LemoineStrings.T("autofilters.discover.labels.colParameter"), 1);
            border.Child = g;
            return border;
        }

        private FrameworkElement BuildConfigRow(ScanConfigRow row)
        {
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Margin          = new Thickness(0, 0, 0, 3),
                Padding         = new Thickness(6, 5, 6, 5),
            };
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            card.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");

            var g = new WpfGrid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });

            var catLbl = new TextBlock
            {
                Text              = row.CategoryLabel,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
            };
            catLbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            catLbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            catLbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            WpfGrid.SetColumn(catLbl, 0);
            g.Children.Add(catLbl);

            // "Whole Category" pinned as first item; rest are the available params
            var comboItems = new List<string> { LemoineStrings.T("autofilters.discover.labels.wholeCategory") };
            comboItems.AddRange(row.AvailableParams);

            int initialIndex = row.Mode == "WholeCategory"
                ? 0
                : Math.Max(1, Array.IndexOf(row.AvailableParams, row.Parameter) + 1);

            var paramCombo = new ComboBox
            {
                ItemsSource   = comboItems,
                SelectedIndex = initialIndex,
                Margin        = new Thickness(4, 0, 4, 0),
            };
            paramCombo.SetResourceReference(ComboBox.BackgroundProperty,  "LemoineSelectBg");
            paramCombo.SetResourceReference(ComboBox.ForegroundProperty,  "LemoineText");
            paramCombo.SetResourceReference(ComboBox.FontSizeProperty,    "LemoineFS_SM");
            paramCombo.SetResourceReference(ComboBox.FontFamilyProperty,  "LemoineUiFont");
            paramCombo.SetResourceReference(ComboBox.BorderBrushProperty, "LemoineBorder");
            LemoineControlStyles.WireComboWheelBubbling(paramCombo); // don't eat page scroll when closed
            WpfGrid.SetColumn(paramCombo, 1);
            g.Children.Add(paramCombo);

            var infoTb = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment     = TextAlignment.Center,
                Margin            = new Thickness(2, 0, 0, 0),
            };
            infoTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            infoTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            infoTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            WpfGrid.SetColumn(infoTb, 2);
            g.Children.Add(infoTb);

            void UpdateInfoTb()
            {
                if (row.Mode == "WholeCategory")
                {
                    infoTb.Visibility = WpfVisibility.Collapsed;
                }
                else
                {
                    infoTb.Visibility = WpfVisibility.Visible;
                    infoTb.Text       = row.IsKnownSafe ? "" : "ⓘ";
                    infoTb.ToolTip    = row.IsKnownSafe ? null
                        : (object)LemoineStrings.T("autofilters.discover.labels.paramWarnTip");
                }
            }
            UpdateInfoTb();

            paramCombo.SelectionChanged += (s, e) =>
            {
                var sel = paramCombo.SelectedItem as string;
                if (sel == null) return;

                if (sel == LemoineStrings.T("autofilters.discover.labels.wholeCategory"))
                {
                    row.Mode = "WholeCategory";
                }
                else
                {
                    row.Mode      = "PerValue";
                    row.Parameter = sel;
                }
                UpdateInfoTb();
                InvalidateScan();
            };

            card.Child = g;
            return card;
        }

        // Discards any completed scan when the S2 configuration changes, so stale
        // discovered rules can never be carried into S4/S5 and committed. Resets the
        // S4 panel to its placeholder and refreshes the S5 summary.
        private void InvalidateScan()
        {
            _scanComplete = false;
            if (_discoveredRules.Count > 0)
            {
                _discoveredRules.Clear();
                _s4CardActions.Clear();
                _s4OpenCardIndex = 0;
                if (_s4Panel != null)
                {
                    _s4Panel.Children.Clear();
                    _s4Panel.Children.Add(MakeNote(LemoineStrings.T("autofilters.discover.labels.noRules")));
                }
                UpdateS5Summary();
            }
            RaiseValidation();
        }

        // ── S2 scan trigger ───────────────────────────────────────────────────

        private void StartScan()
        {
            if (_isScanning) return;

            var selectedLinks = _links.Where(l => l.IsSelected && l.ConfigRows.Count > 0).ToList();
            if (selectedLinks.Count == 0) return;

            _isScanning   = true;
            _scanComplete = false;

            // Auto-advance to the Scanning step so the user watches progress live
            // instead of manually walking forward (NavigateRequested is wired in
            // StepFlowWindow). Step index 2 = "S3".
            RaiseNavigate(2);

            // Reset S3 display (called on WPF thread via OnStepConfirm)
            if (_s3LogStack != null) _s3LogStack.Children.Clear();
            if (_s3Progress != null) _s3Progress.Value = 0;
            if (_s3StatusTb != null)
            {
                _s3StatusTb.Text = LemoineStrings.T("autofilters.discover.status.scanning");
                _s3StatusTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            }

            var specs = new List<ScanSpec>();
            foreach (var link in selectedLinks)
                foreach (var row in link.ConfigRows)
                    specs.Add(new ScanSpec
                    {
                        OstCategory = row.OstCategory,
                        Mode        = row.Mode,
                        Parameter   = row.Parameter,
                        LinkId      = link.Id.Value,
                        TradeName   = link.TradeName,
                    });

            _handler.Mode       = DiscoverMode.MainScan;
            _handler.ScanSpecs  = specs;
            _handler.PushLog    = (text, status) =>
                _wpfDispatcher?.BeginInvoke(DispatcherPriority.Normal,
                    new Action(() => AppendScanLog(text, status)));
            _handler.OnProgress = (pct, p, f, sk) =>
                _wpfDispatcher?.BeginInvoke(DispatcherPriority.Normal,
                    new Action(() => UpdateScanProgress(pct)));
            _handler.OnComplete = OnScanComplete;
            _event.Raise();
        }

        private void OnScanComplete(int pass, int fail, int skip)
        {
            var results = _handler.ScanResults;

            _wpfDispatcher?.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                _isScanning   = false;
                _scanComplete = results.Count > 0;

                if (_s3StatusTb != null)
                {
                    _s3StatusTb.Text = _scanComplete
                        ? LemoineStrings.T("autofilters.discover.status.scanCompleteFound", results.Count)
                        : LemoineStrings.T("autofilters.discover.status.scanCompleteNone");
                    _s3StatusTb.SetResourceReference(TextBlock.ForegroundProperty,
                        _scanComplete ? "LemoineGreen" : "LemoineRed");
                }
                if (_s3Progress != null) _s3Progress.Value = 100;

                PopulateS4(results);
                UpdateS5Summary();
                RaiseValidation();

                // Advance to Review Rules once a successful scan has populated it
                // (step index 3 = "S4"). On an empty scan, stay on S3.
                if (_scanComplete) RaiseNavigate(3);
            }));
        }

        // ── S3 — Scanning ─────────────────────────────────────────────────────

        private FrameworkElement BuildS3()
        {
            var root = new StackPanel();

            _s3StatusTb = new TextBlock
            {
                Text   = LemoineStrings.T("autofilters.discover.status.waiting"),
                Margin = new Thickness(0, 0, 0, 8),
            };
            _s3StatusTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            _s3StatusTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            _s3StatusTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            root.Children.Add(_s3StatusTb);

            _s3Progress = new ProgressBar
            {
                Minimum         = 0,
                Maximum         = 100,
                Value           = 0,
                Height          = 6,
                Margin          = new Thickness(0, 0, 0, 10),
                BorderThickness = new Thickness(0),
            };
            _s3Progress.SetResourceReference(ProgressBar.ForegroundProperty,  "LemoineAccent");
            _s3Progress.SetResourceReference(ProgressBar.BackgroundProperty,  "LemoineBorder");
            root.Children.Add(_s3Progress);

            _s3LogScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 260,
            };
            LemoineControlStyles.WireBubblingScroll(_s3LogScroll);
            _s3LogStack          = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            _s3LogScroll.Content = _s3LogStack;
            root.Children.Add(_s3LogScroll);

            return root;
        }

        private void AppendScanLog(string text, string status)
        {
            if (_s3LogStack == null || _s3LogScroll == null) return;

            var row    = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 1) };
            var colKey = status == "pass" ? "LemoineGreen" : status == "fail" ? "LemoineRed" : "LemoineTextDim";

            var icon = new TextBlock
            {
                Text   = status == "pass" ? "✓" : status == "fail" ? "✗" : "·",
                Margin = new Thickness(0, 0, 6, 0),
            };
            icon.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            icon.SetResourceReference(TextBlock.ForegroundProperty, colKey);
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var msg = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            msg.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            msg.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            msg.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            row.Children.Add(icon);
            row.Children.Add(msg);
            _s3LogStack.Children.Add(row);
            _s3LogScroll.ScrollToEnd();
        }

        private void UpdateScanProgress(int pct)
        {
            if (_s3Progress != null) _s3Progress.Value = pct;
            if (_s3StatusTb != null) _s3StatusTb.Text  = LemoineStrings.T("autofilters.discover.status.scanningPct", pct);
        }

        // ── S4 — Review Rules ─────────────────────────────────────────────────

        private FrameworkElement BuildS4()
        {
            _s4Panel = new StackPanel();
            _s4Panel.Children.Add(MakeNote(LemoineStrings.T("autofilters.discover.labels.noRules")));
            return _s4Panel;
        }

        private void PopulateS4(List<ScanResult> results)
        {
            if (_s4Panel == null) return;
            _s4Panel.Children.Clear();
            _discoveredRules.Clear();
            _s4CardActions.Clear();
            _s4OpenCardIndex = 0;

            // A value found in several categories (e.g. "Supply Air" in both Ducts and
            // Duct Fittings) arrives as multiple ScanResults. Merge per-value rows that
            // share (trade, value, parameter) into ONE rule whose categories are the union
            // and whose count is the sum — otherwise commit-time dedupe-by-name silently
            // dropped every category after the first. Whole-category rows stay one-per-
            // category (each is its own "whole category" rule).
            var perValueByKey = new Dictionary<(string Trade, string Param, string Value), DiscoveredRuleRow>();
            foreach (var r in results)
            {
                if (r.IsWholeCategory)
                {
                    _discoveredRules.Add(new DiscoveredRuleRow
                    {
                        IsIncluded        = true,
                        RuleName          = AutoFiltersSettings.DisplayNameForOst(r.OstCategory),
                        HexColor          = r.HexColor,
                        TradeName         = r.TradeName ?? "",
                        ElementCount      = r.ElementCount,
                        IsWholeCategory   = true,
                        ParameterValue    = r.ParameterValue ?? "",
                        Parameter         = r.Parameter ?? "",
                        BuiltInCategories = new List<string> { r.OstCategory },
                    });
                    continue;
                }

                var key = (r.TradeName ?? "", r.Parameter ?? "", r.ParameterValue ?? "");
                if (perValueByKey.TryGetValue(key, out var existing))
                {
                    existing.ElementCount += r.ElementCount;
                    if (!existing.BuiltInCategories.Contains(r.OstCategory))
                        existing.BuiltInCategories.Add(r.OstCategory);
                }
                else
                {
                    var rowVm = new DiscoveredRuleRow
                    {
                        IsIncluded        = true,
                        RuleName          = r.ParameterValue ?? "",
                        HexColor          = r.HexColor,
                        TradeName         = r.TradeName ?? "",
                        ElementCount      = r.ElementCount,
                        IsWholeCategory   = false,
                        ParameterValue    = r.ParameterValue ?? "",
                        Parameter         = r.Parameter ?? "",
                        BuiltInCategories = new List<string> { r.OstCategory },
                    };
                    perValueByKey[key] = rowVm;
                    _discoveredRules.Add(rowVm);
                }
            }

            if (_discoveredRules.Count == 0)
            {
                _s4Panel.Children.Add(new LemoineWarnBanner(
                    LemoineStrings.T("autofilters.discover.labels.noRulesFound")));
                return;
            }

            _s4Panel.Children.Add(MakeNote(
                LemoineStrings.T("autofilters.discover.labels.rulesDiscovered", _discoveredRules.Count)));

            var byTrade = _discoveredRules
                .GroupBy(r => r.TradeName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sv = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 380,
            };
            LemoineControlStyles.WireBubblingScroll(sv);
            var cardStack = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };

            for (int i = 0; i < byTrade.Count; i++)
                cardStack.Children.Add(BuildS4TradeCard(byTrade[i].Key, byTrade[i].ToList(), i));

            sv.Content = cardStack;
            _s4Panel.Children.Add(sv);
        }

        private FrameworkElement BuildS4TradeCard(string tradeName, List<DiscoveredRuleRow> rules, int cardIndex)
        {
            var outer = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Margin          = new Thickness(0, 0, 0, 6),
            };
            outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            outer.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");

            var stack = new StackPanel();

            // ── Header ────────────────────────────────────────────────────────
            var header = new Border
            {
                Padding = new Thickness(10, 7, 10, 7),
                Cursor  = Cursors.Hand,
            };
            header.SetResourceReference(Border.BackgroundProperty,  "LemoineCard");
            header.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var headerGrid = new WpfGrid();
            headerGrid.Background = Brushes.Transparent;
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var headerLeft = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var tradeTb = new TextBlock
            {
                Text              = tradeName,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 6, 0),
            };
            tradeTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            tradeTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tradeTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var countBadge = new Border
            {
                Padding = new Thickness(5, 1, 5, 1),
                Margin  = new Thickness(4, 0, 0, 0),
            };
            countBadge.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Chip");
            countBadge.SetResourceReference(Border.BackgroundProperty,   "LemoineAccentDim");
            countBadge.SetResourceReference(Border.BorderBrushProperty,  "LemoineAccent");
            var countBadgeTb = new TextBlock { Text = rules.Count.ToString() };
            countBadgeTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            countBadgeTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            countBadgeTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            countBadge.Child = countBadgeTb;

            headerLeft.Children.Add(tradeTb);
            headerLeft.Children.Add(countBadge);
            WpfGrid.SetColumn(headerLeft, 0);
            headerGrid.Children.Add(headerLeft);

            var chevron = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            chevron.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            chevron.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            chevron.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            WpfGrid.SetColumn(chevron, 1);
            headerGrid.Children.Add(chevron);
            header.Child = headerGrid;

            // ── Body ─────────────────────────────────────────────────────────
            var body      = new Border { Padding = new Thickness(8, 6, 8, 8) };
            var bodyStack = new StackPanel();
            bodyStack.Children.Add(BuildS4Header());
            foreach (var rule in rules)
                bodyStack.Children.Add(BuildS4RuleRow(rule));
            body.Child = bodyStack;

            // ── Single-expand accordion wiring ────────────────────────────────
            Action open = () =>
            {
                body.Visibility        = WpfVisibility.Visible;
                chevron.Text           = "▲";
                header.BorderThickness = new Thickness(0, 0, 0, 1);
                header.CornerRadius    = new CornerRadius(6, 6, 0, 0);
            };
            Action close = () =>
            {
                body.Visibility        = WpfVisibility.Collapsed;
                chevron.Text           = "▼";
                header.BorderThickness = new Thickness(0);
                header.CornerRadius    = new CornerRadius(6);
            };
            _s4CardActions.Add((open, close));

            if (cardIndex == 0) open(); else close();

            header.PreviewMouseLeftButtonDown += (s, e) => ExpandS4Card(cardIndex);

            stack.Children.Add(header);
            stack.Children.Add(body);
            outer.Child = stack;
            return outer;
        }

        private void ExpandS4Card(int index)
        {
            int target = (index == _s4OpenCardIndex)
                ? (index + 1 < _s4CardActions.Count ? index + 1 : -1)
                : index;
            _s4OpenCardIndex = target;
            for (int i = 0; i < _s4CardActions.Count; i++)
            {
                if (i == target) _s4CardActions[i].Open();
                else             _s4CardActions[i].Close();
            }
        }

        private FrameworkElement BuildS4Header()
        {
            var border = new Border
            {
                Margin          = new Thickness(0, 0, 0, 2),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding         = new Thickness(4, 2, 4, 4),
            };
            border.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var g = new WpfGrid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });

            void H(string text, int col)
            {
                var tb = new TextBlock { Text = text };
                tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                WpfGrid.SetColumn(tb, col);
                g.Children.Add(tb);
            }
            H("",          0);
            H("",          1);
            H(LemoineStrings.T("autofilters.discover.labels.colRuleName"), 2);
            H(LemoineStrings.T("autofilters.discover.labels.colCategory"),  3);
            H(LemoineStrings.T("autofilters.discover.labels.colCount"),     4);
            border.Child = g;
            return border;
        }

        private FrameworkElement BuildS4RuleRow(DiscoveredRuleRow rule)
        {
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
                Margin          = new Thickness(0, 0, 0, 2),
                Padding         = new Thickness(4, 4, 4, 4),
            };
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            card.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");

            var g = new WpfGrid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });

            var cb = new CheckBox
            {
                IsChecked         = rule.IsIncluded,
                VerticalAlignment = VerticalAlignment.Center,
            };
            cb.Checked   += (s, e) => { rule.IsIncluded = true;  UpdateS5Summary(); RaiseValidation(); };
            cb.Unchecked += (s, e) => { rule.IsIncluded = false; UpdateS5Summary(); RaiseValidation(); };
            WpfGrid.SetColumn(cb, 0);
            g.Children.Add(cb);

            var swatchPanel = LemoineColorPickerWindow.BuildColorPickerSwatch(
                getHex: () => rule.HexColor,
                setHex: hex => { rule.HexColor = hex; },
                showHexLabel: false);
            swatchPanel.VerticalAlignment = VerticalAlignment.Center;
            WpfGrid.SetColumn(swatchPanel, 1);
            g.Children.Add(swatchPanel);

            var nameEdit = new LemoineInlineEdit
            {
                Text              = rule.RuleName,
                FontSizeKey       = "LemoineFS_SM",
                Margin            = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            nameEdit.TextCommitted += (s, name) => rule.RuleName = name;
            WpfGrid.SetColumn(nameEdit, 2);
            g.Children.Add(nameEdit);

            var catTb = new TextBlock
            {
                Text              = CategoryDisplayName(rule.BuiltInCategories.Count > 0 ? rule.BuiltInCategories[0] : ""),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
            };
            catTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            catTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            catTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            WpfGrid.SetColumn(catTb, 3);
            g.Children.Add(catTb);

            var countTb = new TextBlock
            {
                Text              = rule.ElementCount.ToString(),
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment     = TextAlignment.Right,
            };
            countTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            countTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            countTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            WpfGrid.SetColumn(countTb, 4);
            g.Children.Add(countTb);

            card.Child = g;
            return card;
        }

        // ── S5 — Confirm & Commit ─────────────────────────────────────────────

        private FrameworkElement BuildS5()
        {
            var sp = new StackPanel();
            _s5Review = new LemoineReviewSummary { Margin = new Thickness(0, 0, 0, 12) };
            sp.Children.Add(_s5Review);

            // When a create handler is available, offer to build the filter elements
            // immediately after commit (Phase 3.3) so the flow ends with usable filters
            // instead of a "now go run Auto Filters" instruction.
            if (_createHandler != null && _createEvent != null)
            {
                var toggles = new LemoineToggleSwitches();
                toggles.SetItems(new List<ToggleItem>
                {
                    new ToggleItem
                    {
                        Id        = "create",
                        Label     = LemoineStrings.T("autofilters.discover.labels.createLabel"),
                        Desc      = LemoineStrings.T("autofilters.discover.labels.createDesc"),
                        DefaultOn = _createAfterCommit,
                    },
                }, new Dictionary<string, bool> { ["create"] = _createAfterCommit });
                toggles.StateChanged += state =>
                {
                    if (state.TryGetValue("create", out bool on)) _createAfterCommit = on;
                };
                sp.Children.Add(toggles);
            }
            else
            {
                sp.Children.Add(new LemoineWarnBanner(
                    LemoineStrings.T("autofilters.discover.labels.noCreateWarn"),
                    bottomMargin: 0));
            }
            return sp;
        }

        private void UpdateS5Summary()
        {
            if (_s5Review == null) return;
            var included   = _discoveredRules.Where(r => r.IsIncluded).ToList();
            int tradeCount = included.Select(r => r.TradeName)
                                     .Distinct(StringComparer.OrdinalIgnoreCase).Count();

            var items = new List<(string id, string label)>
            {
                ("total",   LemoineStrings.T("autofilters.discover.review.itemTotal")),
                ("include", LemoineStrings.T("autofilters.discover.review.itemInclude")),
                ("skip",    LemoineStrings.T("autofilters.discover.review.itemSkip")),
                ("trades",  LemoineStrings.T("autofilters.discover.review.itemTrades")),
            };
            var values = new Dictionary<string, string>
            {
                ["total"]   = _discoveredRules.Count.ToString(),
                ["include"] = included.Count.ToString(),
                ["skip"]    = (_discoveredRules.Count - included.Count).ToString(),
                ["trades"]  = tradeCount.ToString(),
            };
            var chips = included.Select(r => r.RuleName).Distinct().Take(24).ToList();
            _s5Review.SetItems(items, values, chips);
        }

        // ═════════════════════════════════════════════════════════════════════
        // IsValid, SummaryFor, Run
        // ═════════════════════════════════════════════════════════════════════

        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1":
                    return _links.Any(l => l.IsSelected)
                        && _links.Where(l => l.IsSelected)
                                 .All(l => !string.IsNullOrWhiteSpace(l.TradeName));
                case "S2":
                    return _links.Any(l => l.IsSelected && l.ConfigRows.Count > 0);
                case "S3":
                    return _scanComplete;
                case "S4":
                    return _discoveredRules.Any(r => r.IsIncluded);
                case "S5":
                    return true;
                default:
                    return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1":
                {
                    var sel = _links.Where(l => l.IsSelected).ToList();
                    if (sel.Count == 0) return LemoineStrings.T("autofilters.discover.summaries.s1None");
                    return sel.Count == 1
                        ? sel[0].Label
                        : LemoineStrings.T("autofilters.discover.summaries.s1Multi", sel.Count, string.Join(", ", sel.Select(l => l.TradeName)));
                }
                case "S2":
                {
                    var configured = _links.Where(l => l.IsSelected && l.ConfigRows.Count > 0).ToList();
                    if (configured.Count == 0) return LemoineStrings.T("autofilters.discover.summaries.s2None");
                    return LemoineStrings.T("autofilters.discover.summaries.s2Count", configured.Count);
                }
                case "S3":
                    return _scanComplete
                        ? LemoineStrings.T("autofilters.discover.summaries.s3Found", _discoveredRules.Count)
                        : LemoineStrings.T("autofilters.discover.summaries.s3NotScanned");
                case "S4":
                {
                    int inc = _discoveredRules.Count(r => r.IsIncluded);
                    return LemoineStrings.T("autofilters.discover.summaries.s4Selected", inc, _discoveredRules.Count);
                }
                case "S5":
                    return "";
                default:
                    return "";
            }
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            UpdateS5Summary();

            var specs = _discoveredRules
                .Where(r => r.IsIncluded)
                .Select(r => new CommitRuleSpec
                {
                    TradeName         = r.TradeName,
                    RuleName          = r.RuleName,
                    HexColor          = r.HexColor,
                    IsWholeCategory   = r.IsWholeCategory,
                    ParameterValue    = r.ParameterValue,
                    Parameter         = r.Parameter,
                    BuiltInCategories = r.BuiltInCategories,
                }).ToList();

            bool chainCreate = _createAfterCommit && _createHandler != null && _createEvent != null;

            // Explicit local (not a ternary) so the lambda has a target type under C# 7.3.
            Action<int, int, int> commitDone = onComplete;
            if (chainCreate)
                commitDone = (p, f, s) =>
                {
                    // Commit done — chain a CreateOnly Auto Filters pass so the new rules
                    // exist as ParameterFilterElements. Only when at least one rule
                    // committed; otherwise finish on the commit result.
                    if (p <= 0) { onComplete(p, f, s); return; }
                    pushLog(LemoineStrings.T("autofilters.discover.log.creatingFilters"), "info");
                    _createHandler!.CreateOnly               = true;
                    _createHandler.OverwriteFilterDefinition = false;
                    _createHandler.ChangedFilterNames        = null; // create/refresh all owned filters
                    _createHandler.SelectedDisciplines       = new List<string>(); // all trades
                    _createHandler.SelectedLinkTitles        = new List<string>();
                    _createHandler.PushLog                   = pushLog;
                    _createHandler.OnProgress                = onProgress;
                    _createHandler.OnComplete                = (cp, cf, cs, _) => onComplete(cp, cf, cs);
                    _createEvent!.Raise();
                };

            _handler.Mode        = DiscoverMode.Commit;
            _handler.CommitSpecs = specs;
            _handler.PushLog     = pushLog;
            _handler.OnProgress  = onProgress;
            _handler.OnComplete  = commitDone;
            _event.Raise();
        }

        // ── UI helpers ────────────────────────────────────────────────────────

        private static TextBlock MakeNote(string text)
        {
            var tb = new TextBlock
            {
                Text         = text,
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, 0, 0, 10),
            };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static string CategoryDisplayName(string ostCategory)
            => AutoFiltersSettings.DisplayNameForOst(ostCategory);
    }
}
