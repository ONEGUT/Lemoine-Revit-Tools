using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Tools.AutoFilters;
using LemoineTools.Tools.LinkViews;

using WpfCheckBox   = System.Windows.Controls.CheckBox;
using WpfComboBox   = System.Windows.Controls.ComboBox;
using WpfGrid       = System.Windows.Controls.Grid;
using WpfPanel      = System.Windows.Controls.Panel;
using WpfRectangle  = System.Windows.Shapes.Rectangle;
using WpfTextBox    = System.Windows.Controls.TextBox;

namespace LemoineTools.Tools.Testing.CoordSet
{
    public sealed class CoordSetViewModel : ILemoineTool
    {
        public string Title    => "Coordination Drawing Set";
        public string RunLabel => "Create Set in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("filters",    "Select Filters",          required: true),
            new StepDefinition("legend",     "Legend Config",           required: false),
            new StepDefinition("types",      "Drawing Types",           required: true),
            new StepDefinition("levels",     "Select Levels",           required: true),
            new StepDefinition("sheets",     "Sheet Layout",            required: true),
            new StepDefinition("depviews",   "Dependent View Layout",   required: false),
            new StepDefinition("gridbubbles","Grid Bubbles",            required: false),
            new StepDefinition("coversheet", "Cover Sheet",             required: false),
        };

        public event EventHandler? ValidationChanged;
        private void FireValidation() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── State ──────────────────────────────────────────────────────────────
        private readonly CoordSetSettings  _s;
        private List<string>               _selectedTradeIds   = new List<string>();
        private List<ElementId>            _selectedLevelIds   = new List<ElementId>();
        private Dictionary<string, ElementId> _levelKeyToId    = new Dictionary<string, ElementId>();
        private List<LevelScanResult>      _scannedLevels      = new List<LevelScanResult>();
        private bool                       _scanning           = false;
        private bool                       _scanDone           = false;
        private string                     _titleBlockKey      = "";
        private Dictionary<string, ElementId> _titleBlocks     = new Dictionary<string, ElementId>();
        private StackPanel?                _levelsContainer;
        private Dispatcher?                _levelsDispatcher;

        // ── ExternalEvent wiring ───────────────────────────────────────────────
        private readonly LinkViewsLevelPhase1Handler _phase1Handler;
        private readonly ExternalEvent               _phase1Event;
        private readonly CoordSetRunHandler          _runHandler;
        private readonly ExternalEvent               _runEvent;

        // ── Available link documents (for level scan) ──────────────────────────
        private readonly bool _includeHost;

        public CoordSetViewModel(
            LinkViewsLevelPhase1Handler phase1Handler, ExternalEvent phase1Event,
            CoordSetRunHandler          runHandler,    ExternalEvent runEvent,
            Dictionary<string, ElementId> titleBlocks,
            bool includeHost)
        {
            _phase1Handler = phase1Handler;
            _phase1Event   = phase1Event;
            _runHandler    = runHandler;
            _runEvent      = runEvent;
            _titleBlocks   = titleBlocks ?? new Dictionary<string, ElementId>();
            _includeHost   = includeHost;
            _s             = CoordSetSettings.Instance;

            // Restore from settings
            _selectedTradeIds = new List<string>(_s.SelectedTradeIds ?? new List<string>());
            if (_selectedTradeIds.Count == 0)
                _selectedTradeIds = AutoFiltersSettings.Instance.Trades
                    .Select(t => t.Id).ToList();
        }

        // ═══════════════════════════════════════════════════════════════════════
        // GetStepContent
        // ═══════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "filters":    return BuildFiltersStep();
                case "legend":     return BuildLegendStep();
                case "types":      return BuildTypesStep();
                case "levels":     return BuildLevelsStep();
                case "sheets":     return BuildSheetsStep();
                case "depviews":   return BuildDepViewsStep();
                case "gridbubbles":return BuildGridBubblesStep();
                case "coversheet": return BuildCoverSheetStep();
                default:           return null;
            }
        }

        // ── Step 1: Select Filters ─────────────────────────────────────────────
        private FrameworkElement BuildFiltersStep()
        {
            var trades   = AutoFiltersSettings.Instance.Trades;
            var container = new StackPanel();

            var hdr = Label("TRADES TO INCLUDE");
            container.Children.Add(hdr);

            var sub = SubLabel("These trades will have filters created and applied to discipline views.");
            container.Children.Add(sub);

            foreach (var trade in trades)
            {
                var capturedId = trade.Id;
                bool on = _selectedTradeIds.Contains(trade.Id);

                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin      = new Thickness(0, 0, 0, 4),
                    Cursor      = Cursors.Hand,
                };

                var cb = new WpfCheckBox
                {
                    IsChecked         = on,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(0, 0, 6, 0),
                };

                var swatch = new Border
                {
                    Width  = 12, Height = 12,
                    CornerRadius = new CornerRadius(2),
                    Background = BrushFromHex(trade.Color),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var lbl = new TextBlock
                {
                    Text = $"{trade.Id}  —  {trade.Label}",
                    VerticalAlignment = VerticalAlignment.Center,
                };
                lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

                row.Children.Add(cb);
                row.Children.Add(swatch);
                row.Children.Add(lbl);

                LemoineMotion.WireHover(row, normalBgKey: null, hoverBgKey: "LemoineAccentDim");

                void Toggle(bool newVal)
                {
                    if (newVal) { if (!_selectedTradeIds.Contains(capturedId)) _selectedTradeIds.Add(capturedId); }
                    else        _selectedTradeIds.Remove(capturedId);
                    FireValidation();
                }

                cb.Checked   += (s, e) => Toggle(true);
                cb.Unchecked += (s, e) => Toggle(false);
                row.MouseLeftButtonDown += (s, e) =>
                {
                    if (e.OriginalSource is WpfCheckBox) return;
                    cb.IsChecked = !(cb.IsChecked == true);
                    e.Handled = true;
                };

                container.Children.Add(row);
            }

            return container;
        }

        // ── Step 2: Legend Config ──────────────────────────────────────────────
        private FrameworkElement BuildLegendStep()
        {
            var outer = new StackPanel();

            var hdr = Label("LEGEND GROUPS");
            outer.Children.Add(hdr);

            var sub = SubLabel("Drag to reorder groups. Each entry maps a filter rule to a swatch shape.");
            outer.Children.Add(sub);

            // Left/right split panel
            var grid = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });      // divider
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ── Left pane: group list ──────────────────────────────────────────
            var leftPane  = new StackPanel();
            var divider   = new WpfRectangle { Width = 1, Margin = new Thickness(0) };
            divider.SetResourceReference(WpfRectangle.FillProperty, "LemoineBorder");
            var rightPane = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };

            WpfGrid.SetColumn(leftPane,  0);
            WpfGrid.SetColumn(divider,   1);
            WpfGrid.SetColumn(rightPane, 2);
            grid.Children.Add(leftPane);
            grid.Children.Add(divider);
            grid.Children.Add(rightPane);

            // Populate left pane from settings
            var groups   = _s.LegendGroups ?? new List<LegendGroupConfig>();
            var groupRows = new StackPanel();

            void RefreshGroupRows()
            {
                groupRows.Children.Clear();
                foreach (var grp in groups)
                {
                    var capturedGrp = grp;
                    var row = new Border
                    {
                        BorderThickness = new Thickness(1),
                        CornerRadius    = new CornerRadius(3),
                        Margin          = new Thickness(0, 0, 0, 3),
                        Padding         = new Thickness(6, 4, 6, 4),
                        Cursor          = Cursors.Hand,
                    };
                    row.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                    row.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");

                    var dp = new DockPanel();

                    var del = new TextBlock
                    {
                        Text = "✕", VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(4, 0, 0, 0), Cursor = Cursors.Hand,
                    };
                    del.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                    del.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    LemoineMotion.WireTextHover(del, "LemoineTextDim", "LemoineText");
                    del.MouseLeftButtonDown += (s2, e2) =>
                    {
                        groups.Remove(capturedGrp);
                        _s.LegendGroups = groups;
                        _s.Save();
                        RefreshGroupRows();
                        e2.Handled = true;
                    };
                    DockPanel.SetDock(del, Dock.Right);
                    dp.Children.Add(del);

                    var nameTb = new TextBlock
                    {
                        Text = capturedGrp.Label,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    nameTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                    nameTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                    nameTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    dp.Children.Add(nameTb);
                    row.Child = dp;
                    groupRows.Children.Add(row);
                }
            }

            RefreshGroupRows();

            var addGrpBtn = new Button
            {
                Content = "+ Add Group",
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 4, 0, 0),
                Template = LemoineControlStyles.BuildFlatButtonTemplate(),
            };
            addGrpBtn.SetResourceReference(Button.MinHeightProperty,  "LemoineH_BtnMin");
            addGrpBtn.SetResourceReference(Button.PaddingProperty,    "LemoineTh_BtnPad");
            addGrpBtn.SetResourceReference(Button.FontSizeProperty,   "LemoineFS_SM");
            addGrpBtn.SetResourceReference(Button.FontFamilyProperty, "LemoineUiFont");
            addGrpBtn.Background = System.Windows.Media.Brushes.Transparent;
            addGrpBtn.SetResourceReference(Button.BorderBrushProperty, "LemoineBorder");
            addGrpBtn.SetResourceReference(Button.ForegroundProperty,  "LemoineText");
            addGrpBtn.Click += (s2, e2) =>
            {
                var ng = new LegendGroupConfig
                {
                    Id    = Guid.NewGuid().ToString("N").Substring(0, 8),
                    Label = "New Group",
                };
                groups.Add(ng);
                _s.LegendGroups = groups;
                _s.Save();
                RefreshGroupRows();
            };

            leftPane.Children.Add(Label("GROUPS"));
            leftPane.Children.Add(groupRows);
            leftPane.Children.Add(addGrpBtn);

            // ── Right pane: placeholder ────────────────────────────────────────
            var rightHint = new TextBlock
            {
                Text = "Select a group to edit its filter entries.",
                TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic,
            };
            rightHint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            rightHint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            rightHint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            rightPane.Children.Add(rightHint);

            outer.Children.Add(grid);
            return outer;
        }

        // ── Step 3: Drawing Types ──────────────────────────────────────────────
        private FrameworkElement BuildTypesStep()
        {
            var tog = new LemoineToggleSwitches();
            tog.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id="coord", Label="Coordination",  Desc="Base floor plan with all trades",       DefaultOn=_s.CreateCoordination },
                new ToggleItem { Id="mech",  Label="Mechanical",    Desc="Ductwork + piping only",                DefaultOn=_s.CreateMechanical   },
                new ToggleItem { Id="plmb",  Label="Plumbing",      Desc="Plumbing systems only",                 DefaultOn=_s.CreatePlumbing     },
                new ToggleItem { Id="fire",  Label="Fire Protection",Desc="Sprinkler piping only",                DefaultOn=_s.CreateFire         },
                new ToggleItem { Id="elec",  Label="Electrical",    Desc="Cable tray, conduit, equipment",        DefaultOn=_s.CreateElectrical   },
                new ToggleItem { Id="other", Label="Other Trades",  Desc="All remaining trades not listed above", DefaultOn=_s.CreateOther        },
                new ToggleItem { Id="rcp",   Label="Ceiling Plans", Desc="Reflected ceiling plans per level",     DefaultOn=_s.CreateRCP          },
            });
            tog.StateChanged += state =>
            {
                _s.CreateCoordination = state.TryGetValue("coord", out var v0) && v0;
                _s.CreateMechanical   = state.TryGetValue("mech",  out var v1) && v1;
                _s.CreatePlumbing     = state.TryGetValue("plmb",  out var v2) && v2;
                _s.CreateFire         = state.TryGetValue("fire",  out var v3) && v3;
                _s.CreateElectrical   = state.TryGetValue("elec",  out var v4) && v4;
                _s.CreateOther        = state.TryGetValue("other", out var v5) && v5;
                _s.CreateRCP          = state.TryGetValue("rcp",   out var v6) && v6;
                _s.Save();
                FireValidation();
            };
            return tog;
        }

        // ── Step 4: Select Levels ──────────────────────────────────────────────
        private FrameworkElement BuildLevelsStep()
        {
            _levelsDispatcher = Dispatcher.CurrentDispatcher;
            _levelsContainer  = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            if (_scanDone && _scannedLevels.Count > 0)
                PopulateLevels();
            else if (_scanDone)
                ShowLevelsMsg("No levels with placed rooms found.");
            else if (!_scanning)
            {
                ShowLevelsMsg("Scanning project levels…");
                TriggerPhase1();
            }
            else
                ShowLevelsMsg("Scanning project levels…");

            return _levelsContainer;
        }

        private void ShowLevelsMsg(string text)
        {
            _levelsContainer?.Children.Clear();
            var tb = new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 4, 0, 0),
            };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _levelsContainer?.Children.Add(tb);
        }

        private void TriggerPhase1()
        {
            _scanning = true;
            _phase1Handler.IncludeHost = _includeHost;
            _phase1Handler.LinkInstIds = new List<ElementId>();

            _phase1Handler.OnLevelsLoaded = results =>
            {
                _levelsDispatcher?.BeginInvoke((Action)(() =>
                {
                    _scanning      = false;
                    _scanDone      = true;
                    _scannedLevels = results ?? new List<LevelScanResult>();

                    if (_scannedLevels.Count == 0)
                    {
                        ShowLevelsMsg("No levels with placed rooms found.");
                        FireValidation();
                        return;
                    }

                    _selectedLevelIds = _scannedLevels
                        .Select(l => l.LevelId)
                        .GroupBy(id => id.Value)
                        .Select(g => g.First())
                        .ToList();

                    PopulateLevels();
                    FireValidation();
                }));
            };

            _phase1Handler.OnError = msg =>
            {
                _levelsDispatcher?.BeginInvoke((Action)(() =>
                {
                    _scanning = false;
                    _scanDone = true;
                    ShowLevelsMsg($"Scan error: {msg}");
                    FireValidation();
                }));
            };

            _phase1Event.Raise();
        }

        private void PopulateLevels()
        {
            _levelsContainer?.Children.Clear();
            _levelKeyToId.Clear();

            var groups = _scannedLevels
                .GroupBy(l => l.DocumentName ?? "(Unknown)")
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(l => l.ElevationFt)
                          .Select(l =>
                          {
                              string key = $"[{l.DocumentName}] {l.Name}   ({l.ElevationFt:F2} ft · {l.RoomCount} room{(l.RoomCount != 1 ? "s" : "")})";
                              _levelKeyToId[key] = l.LevelId;
                              return key;
                          })
                          .ToList());

            var selectedIdSet = new HashSet<long>(_selectedLevelIds.Select(id => id.Value));
            var preSelected   = _levelKeyToId
                .Where(kv => selectedIdSet.Contains(kv.Value.Value))
                .Select(kv => kv.Key).ToList();
            if (preSelected.Count == 0) preSelected = _levelKeyToId.Keys.ToList();

            var tabs = new LemoineMultiSelectTabs();
            tabs.SelectionChanged += selected =>
            {
                _selectedLevelIds = selected
                    .Where(k => _levelKeyToId.ContainsKey(k))
                    .Select(k => _levelKeyToId[k])
                    .GroupBy(id => id.Value)
                    .Select(g => g.First())
                    .ToList();
                FireValidation();
            };
            tabs.SetGroups(groups, preSelected);
            _levelsContainer.Children.Add(tabs);
        }

        // ── Step 5: Sheet Layout ───────────────────────────────────────────────
        private FrameworkElement BuildSheetsStep()
        {
            var outer = new StackPanel();

            outer.Children.Add(Label("TITLE BLOCK"));
            outer.Children.Add(SubLabel("Select the title block family to use for all sheets."));

            var tbSel = new LemoineSingleSelect
            {
                Items        = _titleBlocks.Keys.ToList(),
                SelectedItem = _titleBlockKey,
            };
            tbSel.SelectionChanged += v =>
            {
                _titleBlockKey = v ?? "";
                FireValidation();
            };
            outer.Children.Add(tbSel);

            outer.Children.Add(new WpfRectangle { Height = 10 });
            outer.Children.Add(Label("SHEET NUMBER PREFIX"));

            var prefixTb = new WpfTextBox { Text = _s.SheetNumberPrefix, Width = 120 };
            prefixTb.SetResourceReference(WpfTextBox.HeightProperty,     "LemoineH_Input");
            prefixTb.SetResourceReference(WpfTextBox.PaddingProperty,    "LemoineTh_InputPad");
            prefixTb.SetResourceReference(WpfTextBox.BackgroundProperty, "LemoineSelectBg");
            prefixTb.SetResourceReference(WpfTextBox.ForegroundProperty, "LemoineText");
            prefixTb.SetResourceReference(WpfTextBox.FontSizeProperty,   "LemoineFS_SM");
            prefixTb.SetResourceReference(WpfTextBox.FontFamilyProperty, "LemoineMonoFont");
            prefixTb.TextChanged += (s2, e2) => { _s.SheetNumberPrefix = prefixTb.Text; _s.Save(); };
            outer.Children.Add(prefixTb);

            outer.Children.Add(new WpfRectangle { Height = 10 });
            outer.Children.Add(Label("PER-DISCIPLINE PREFIXES"));

            // Build small inline table
            var table = new StackPanel();
            foreach (var entry in _s.DisciplinePrefixes)
            {
                var capturedEntry = entry;
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin      = new Thickness(0, 0, 0, 4),
                };
                var disc = new TextBlock
                {
                    Text = entry.Discipline, Width = 120,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                disc.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                disc.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                disc.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

                var pf = new WpfTextBox { Text = entry.Prefix, Width = 80 };
                pf.SetResourceReference(WpfTextBox.HeightProperty,     "LemoineH_Input");
                pf.SetResourceReference(WpfTextBox.PaddingProperty,    "LemoineTh_InputPad");
                pf.SetResourceReference(WpfTextBox.BackgroundProperty, "LemoineSelectBg");
                pf.SetResourceReference(WpfTextBox.ForegroundProperty, "LemoineText");
                pf.SetResourceReference(WpfTextBox.FontSizeProperty,   "LemoineFS_SM");
                pf.SetResourceReference(WpfTextBox.FontFamilyProperty, "LemoineMonoFont");
                pf.TextChanged += (s2, e2) => { capturedEntry.Prefix = pf.Text; _s.Save(); };

                row.Children.Add(disc);
                row.Children.Add(pf);
                table.Children.Add(row);
            }
            outer.Children.Add(table);

            return outer;
        }

        // ── Step 6: Dependent View Layout ─────────────────────────────────────
        private FrameworkElement BuildDepViewsStep()
        {
            var outer = new StackPanel();
            outer.Children.Add(Label("DEPENDENT VIEW LAYOUT"));
            outer.Children.Add(SubLabel(
                "Dependent views are created from the first selected level and replicated to all other levels. " +
                "Crop boxes and viewport positions established on the first level are mirrored exactly."));

            var tog = new LemoineToggleSwitches();
            tog.SetItems(new List<ToggleItem>
            {
                new ToggleItem
                {
                    Id = "hideNonMatching",
                    Label = "Hide Non-Matching Trades",
                    Desc  = "When creating discipline views, hide all trade filters that don't belong to that discipline.",
                    DefaultOn = _s.HideNonMatchingTrades,
                },
            });
            tog.StateChanged += state =>
            {
                _s.HideNonMatchingTrades = state.TryGetValue("hideNonMatching", out var v) && v;
                _s.Save();
            };
            outer.Children.Add(tog);
            return outer;
        }

        // ── Step 7: Grid Bubbles ───────────────────────────────────────────────
        private FrameworkElement BuildGridBubblesStep()
        {
            var outer = new StackPanel();
            outer.Children.Add(Label("GRID BUBBLE VISIBILITY"));
            outer.Children.Add(SubLabel(
                "Control which ends of grids show bubbles on dependent views. " +
                "Vertical grids use Top/Bottom; horizontal grids use Left/Right."));

            var bs = _s.GridBubbles ?? (_s.GridBubbles = new GridBubbleSettings());

            var tog = new LemoineToggleSwitches();
            tog.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id="top",    Label="Top",    Desc="Show bubbles at top ends of vertical grids",    DefaultOn=bs.ShowTop    },
                new ToggleItem { Id="bottom", Label="Bottom", Desc="Show bubbles at bottom ends of vertical grids", DefaultOn=bs.ShowBottom },
                new ToggleItem { Id="left",   Label="Left",   Desc="Show bubbles at left ends of horizontal grids", DefaultOn=bs.ShowLeft   },
                new ToggleItem { Id="right",  Label="Right",  Desc="Show bubbles at right ends of horizontal grids",DefaultOn=bs.ShowRight  },
            });
            tog.StateChanged += state =>
            {
                bs.ShowTop    = state.TryGetValue("top",    out var vt) && vt;
                bs.ShowBottom = state.TryGetValue("bottom", out var vb) && vb;
                bs.ShowLeft   = state.TryGetValue("left",   out var vl) && vl;
                bs.ShowRight  = state.TryGetValue("right",  out var vr) && vr;
                _s.Save();
            };
            outer.Children.Add(tog);

            // Quick buttons
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 8, 0, 0),
            };

            var allOn = LemoineControlStyles.BuildButton(
                "Set All On", LemoineControlStyles.LemoineButtonVariant.Primary);
            allOn.Margin = new Thickness(0, 0, 8, 0);
            allOn.Click += (s2, e2) =>
            {
                bs.ShowTop = bs.ShowBottom = bs.ShowLeft = bs.ShowRight = true;
                _s.Save();
                tog.SetItems(new List<ToggleItem>
                {
                    new ToggleItem { Id="top",    Label="Top",    Desc="Show bubbles at top ends of vertical grids",    DefaultOn=true },
                    new ToggleItem { Id="bottom", Label="Bottom", Desc="Show bubbles at bottom ends of vertical grids", DefaultOn=true },
                    new ToggleItem { Id="left",   Label="Left",   Desc="Show bubbles at left ends of horizontal grids", DefaultOn=true },
                    new ToggleItem { Id="right",  Label="Right",  Desc="Show bubbles at right ends of horizontal grids",DefaultOn=true },
                });
            };

            var allOff = LemoineControlStyles.BuildButton(
                "Set All Off", LemoineControlStyles.LemoineButtonVariant.Ghost);
            allOff.Click += (s2, e2) =>
            {
                bs.ShowTop = bs.ShowBottom = bs.ShowLeft = bs.ShowRight = false;
                _s.Save();
                tog.SetItems(new List<ToggleItem>
                {
                    new ToggleItem { Id="top",    Label="Top",    Desc="Show bubbles at top ends of vertical grids",    DefaultOn=false },
                    new ToggleItem { Id="bottom", Label="Bottom", Desc="Show bubbles at bottom ends of vertical grids", DefaultOn=false },
                    new ToggleItem { Id="left",   Label="Left",   Desc="Show bubbles at left ends of horizontal grids", DefaultOn=false },
                    new ToggleItem { Id="right",  Label="Right",  Desc="Show bubbles at right ends of horizontal grids",DefaultOn=false },
                });
            };

            btnRow.Children.Add(allOn);
            btnRow.Children.Add(allOff);
            outer.Children.Add(btnRow);

            return outer;
        }

        // ── Step 8: Cover Sheet ────────────────────────────────────────────────
        private FrameworkElement BuildCoverSheetStep()
        {
            var outer = new StackPanel();

            var tog = new LemoineToggleSwitches();
            tog.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id="createCover", Label="Create Cover Sheet",
                    Desc="Generate a title sheet with project info and sheet index.",
                    DefaultOn=_s.CreateCoverSheet },
            });
            tog.StateChanged += state =>
            {
                _s.CreateCoverSheet = state.TryGetValue("createCover", out var v) && v;
                _s.Save();
            };
            outer.Children.Add(tog);

            outer.Children.Add(new WpfRectangle { Height = 8 });
            outer.Children.Add(Label("PROJECT INFO"));

            WpfTextBox MakeField(string placeholder, string current, Action<string> setter)
            {
                var tb = new WpfTextBox { Text = current };
                tb.SetResourceReference(WpfTextBox.HeightProperty,     "LemoineH_Input");
                tb.SetResourceReference(WpfTextBox.PaddingProperty,    "LemoineTh_InputPad");
                tb.SetResourceReference(WpfTextBox.BackgroundProperty, "LemoineSelectBg");
                tb.SetResourceReference(WpfTextBox.ForegroundProperty, "LemoineText");
                tb.SetResourceReference(WpfTextBox.FontSizeProperty,   "LemoineFS_SM");
                tb.SetResourceReference(WpfTextBox.FontFamilyProperty, "LemoineUiFont");
                tb.TextChanged += (s2, e2) => { setter(tb.Text); _s.Save(); };
                return tb;
            }

            void AddRow(string labelText, string current, Action<string> setter)
            {
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin      = new Thickness(0, 0, 0, 6),
                };
                var lbl = new TextBlock
                {
                    Text = labelText, Width = 120,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                row.Children.Add(lbl);
                row.Children.Add(MakeField(labelText, current, setter));
                outer.Children.Add(row);
            }

            AddRow("Project Name",   _s.ProjectName,   v => _s.ProjectName   = v);
            AddRow("Project Number", _s.ProjectNumber, v => _s.ProjectNumber = v);
            AddRow("Project Date",   _s.ProjectDate,   v => _s.ProjectDate   = v);
            AddRow("Sheet Number",   _s.CoverSheetNumber, v => _s.CoverSheetNumber = v);

            return outer;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Validation + Summary
        // ═══════════════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "filters")     return _selectedTradeIds.Count > 0;
            if (stepId == "types")       return _s.CreateCoordination || _s.CreateMechanical
                                             || _s.CreatePlumbing || _s.CreateFire
                                             || _s.CreateElectrical || _s.CreateOther || _s.CreateRCP;
            if (stepId == "levels")      return _scanDone && _selectedLevelIds.Count > 0;
            if (stepId == "sheets")      return !string.IsNullOrEmpty(_titleBlockKey);
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "filters")     return $"{_selectedTradeIds.Count} trade(s)";
            if (stepId == "legend")      return $"{_s.LegendGroups?.Count ?? 0} group(s)";
            if (stepId == "types")
            {
                var on = new List<string>();
                if (_s.CreateCoordination) on.Add("Coord");
                if (_s.CreateMechanical)   on.Add("Mech");
                if (_s.CreatePlumbing)     on.Add("Plmb");
                if (_s.CreateFire)         on.Add("Fire");
                if (_s.CreateElectrical)   on.Add("Elec");
                if (_s.CreateOther)        on.Add("Other");
                if (_s.CreateRCP)          on.Add("RCP");
                return on.Count > 0 ? string.Join(" · ", on) : "—";
            }
            if (stepId == "levels")      return _scanDone ? $"{_selectedLevelIds.Count} level(s)" : "—";
            if (stepId == "sheets")      return string.IsNullOrEmpty(_titleBlockKey) ? "—" : _titleBlockKey;
            if (stepId == "depviews")    return _s.HideNonMatchingTrades ? "Hide non-matching" : "Show all";
            if (stepId == "gridbubbles")
            {
                var bs = _s.GridBubbles;
                if (bs == null) return "—";
                var on = new List<string>();
                if (bs.ShowTop)    on.Add("T");
                if (bs.ShowBottom) on.Add("B");
                if (bs.ShowLeft)   on.Add("L");
                if (bs.ShowRight)  on.Add("R");
                return on.Count > 0 ? string.Join("/", on) : "All off";
            }
            if (stepId == "coversheet")  return _s.CreateCoverSheet ? "Yes" : "No";
            return "—";
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Run
        // ═══════════════════════════════════════════════════════════════════════
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _s.SelectedTradeIds = new List<string>(_selectedTradeIds);
            _s.Save();

            ElementId titleBlockId = ElementId.InvalidElementId;
            if (!string.IsNullOrEmpty(_titleBlockKey) &&
                _titleBlocks.TryGetValue(_titleBlockKey, out var tbId))
                titleBlockId = tbId;

            // Determine resume phase
            CoordSetRunPhase startFrom = CoordSetRunPhase.None;
            if (_s.LastCompletedPhase != CoordSetRunPhase.None &&
                _s.LastCompletedPhase != CoordSetRunPhase.G)
            {
                startFrom = _s.LastCompletedPhase;
                pushLog($"Resuming from after phase {_s.LastCompletedPhase}…", "info");
            }

            _runHandler.Settings         = _s;
            _runHandler.SelectedLevelIds = new List<ElementId>(
                _selectedLevelIds.GroupBy(id => id.Value).Select(g => g.First()));
            _runHandler.SelectedTradeIds = new List<string>(_selectedTradeIds);
            _runHandler.TitleBlockTypeId = titleBlockId;
            _runHandler.StartFromPhase   = startFrom;
            _runHandler.PushLog          = pushLog;
            _runHandler.OnProgress       = onProgress;
            _runHandler.OnComplete       = onComplete;

            pushLog("Raising Revit ExternalEvent…", "info");
            _runEvent.Raise();
        }

        // ═══════════════════════════════════════════════════════════════════════
        // UI Helpers
        // ═══════════════════════════════════════════════════════════════════════
        private static TextBlock Label(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 6) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static TextBlock SubLabel(string text)
        {
            var tb = new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10), FontStyle = FontStyles.Italic,
            };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static System.Windows.Media.SolidColorBrush? BrushFromHex(string hex)
        {
            try
            {
                hex = hex.TrimStart('#');
                if (hex?.Length == 6)
                {
                    byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                    byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                    byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                    return new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(r, g, b));
                }
            }
            catch (Exception __lex) { LemoineLog.Swallowed("CoordSet: parse colour hex", __lex); }
            return System.Windows.Media.Brushes.Gray;
        }

    }

}
