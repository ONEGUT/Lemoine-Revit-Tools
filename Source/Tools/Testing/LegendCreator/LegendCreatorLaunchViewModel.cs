using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;
using WpfGrid = System.Windows.Controls.Grid;

namespace LemoineTools.Tools.Testing.LegendCreator
{
    /// <summary>
    /// ILemoineTool for the Legend Creation ribbon button step-flow.
    /// S1: Create/Update mode + legend view picker + settings summary.
    /// S2: Per-role text type selection (title / subtitle / group header / label).
    /// </summary>
    public sealed class LegendCreatorLaunchViewModel : ILemoineTool
    {
        // ── ILemoineTool identity ──────────────────────────────────────────────
        public string Title    => "Legend Creation";
        public string RunLabel => _updateMode ? "Update Legend →" : "Create Legend →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Legend View",  required: false),
            new StepDefinition("S2", "Text Styles",  required: false),
        };

        public event EventHandler? ValidationChanged;

        // ── Data ──────────────────────────────────────────────────────────────
        private readonly List<(ElementId Id, string Name)> _legendViews;
        private readonly List<(ElementId Id, string Name)> _textTypes;
        private readonly LegendCreatorEventHandler         _handler;
        private readonly Autodesk.Revit.UI.ExternalEvent   _event;

        private bool     _updateMode        = false;
        private int      _createTemplateIdx = 0;
        private int      _updateTargetIdx   = 0;

        // Per-role text type selections (null → handler uses its own default)
        private ElementId? _titleTypeId;
        private ElementId? _subtitleTypeId;
        private ElementId? _groupHeaderTypeId;
        private ElementId? _labelTypeId;

        public LegendCreatorLaunchViewModel(
            List<(ElementId Id, string Name)> legendViews,
            List<(ElementId Id, string Name)> textTypes,
            LegendCreatorEventHandler         handler,
            Autodesk.Revit.UI.ExternalEvent   externalEvent)
        {
            _legendViews = legendViews;
            _textTypes   = textTypes;
            _handler     = handler;
            _event       = externalEvent;

            // Default all roles to the first text type in the list.
            if (_textTypes.Count > 0)
            {
                _titleTypeId       = _textTypes[0].Id;
                _subtitleTypeId    = _textTypes[0].Id;
                _groupHeaderTypeId = _textTypes[0].Id;
                _labelTypeId       = _textTypes[0].Id;
            }
        }

        // ── CardDef struct ─────────────────────────────────────────────────────
        private struct CardDef
        {
            public string Label, Val;
            public int    Row, Col;
            public CardDef(string label, string val, int row, int col)
            { Label = label; Val = val; Row = row; Col = col; }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // GetStepContent
        // ═══════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "S1") return BuildS1();
            if (stepId == "S2") return BuildS2();
            return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // S1 — Legend view picker + settings summary
        // ─────────────────────────────────────────────────────────────────────
        private FrameworkElement BuildS1()
        {
            var outer = new StackPanel();

            // ── Mode toggle ──────────────────────────────────────────────────
            var modeHeader = new TextBlock
            {
                Text   = "MODE",
                Margin = new Thickness(0, 0, 0, 6),
            };
            modeHeader.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            modeHeader.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            modeHeader.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            outer.Children.Add(modeHeader);

            var pillRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 0, 0, 14),
            };

            var createPill = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3, 0, 0, 3),
                Padding         = new Thickness(10, 5, 10, 5),
                Cursor          = Cursors.Hand,
                Child           = MakePillText("Create New"),
            };
            var updatePill = new Border
            {
                BorderThickness = new Thickness(1, 1, 1, 1),
                CornerRadius    = new CornerRadius(0, 3, 3, 0),
                Padding         = new Thickness(10, 5, 10, 5),
                Cursor          = Cursors.Hand,
                Child           = MakePillText("Update Existing"),
            };

            var pickerLabel = new TextBlock { Margin = new Thickness(0, 0, 0, 4) };
            pickerLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            pickerLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            pickerLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var picker = new ComboBox
            {
                IsEditable = false,
                Margin     = new Thickness(0, 0, 0, 16),
            };
            picker.SetResourceReference(FrameworkElement.HeightProperty, "LemoineH_Input");
            picker.SetResourceReference(ComboBox.FontFamilyProperty,     "LemoineMonoFont");
            picker.SetResourceReference(ComboBox.FontSizeProperty,       "LemoineFS_SM");
            foreach (var (_, name) in _legendViews)
                picker.Items.Add(name);

            void ApplyMode()
            {
                pickerLabel.Text     = _updateMode ? "LEGEND TO UPDATE" : "BASE LEGEND VIEW";
                picker.SelectedIndex = _updateMode ? _updateTargetIdx : _createTemplateIdx;

                if (!_updateMode)
                {
                    createPill.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                    createPill.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                    updatePill.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                    updatePill.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                }
                else
                {
                    createPill.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                    createPill.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                    updatePill.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                    updatePill.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                }
            }

            createPill.MouseLeftButtonUp += (s, e) =>
            {
                if (_updateMode) { _updateMode = false; ApplyMode(); ValidationChanged?.Invoke(this, EventArgs.Empty); }
            };
            updatePill.MouseLeftButtonUp += (s, e) =>
            {
                if (!_updateMode) { _updateMode = true; ApplyMode(); ValidationChanged?.Invoke(this, EventArgs.Empty); }
            };

            picker.SelectionChanged += (s, e) =>
            {
                if (picker.SelectedIndex < 0) return;
                if (_updateMode) _updateTargetIdx   = picker.SelectedIndex;
                else             _createTemplateIdx = picker.SelectedIndex;
            };

            pillRow.Children.Add(createPill);
            pillRow.Children.Add(updatePill);
            outer.Children.Add(pillRow);
            outer.Children.Add(pickerLabel);
            outer.Children.Add(picker);

            ApplyMode();

            // ── Settings summary cards ────────────────────────────────────────
            var settings = LegendCreatorSettings.Instance;
            var layout   = settings.Layout ?? new LegendLayoutConfig();
            var rows     = settings.Rows   ?? new List<LegendRowConfig>();
            int groups   = rows.Sum(r => r.Groups?.Count ?? 0);
            int blocks   = rows.SelectMany(r => r.Groups ?? new List<LegendGroupConfig>())
                               .Sum(g => g.Blocks?.Count ?? 0);

            var grid = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var cards = new[]
            {
                new CardDef("Layout", string.IsNullOrWhiteSpace(layout.Title) ? "—" : layout.Title.Trim(), 0, 0),
                new CardDef("Groups", groups.ToString(),     0, 1),
                new CardDef("Blocks", blocks.ToString(),     1, 0),
                new CardDef("Rows",   rows.Count.ToString(), 1, 1),
            };

            foreach (var c in cards)
            {
                var card = new Border
                {
                    Margin          = new Thickness(c.Col == 0 ? 0 : 4, c.Row == 0 ? 0 : 4, 0, 0),
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(3),
                };
                card.SetResourceReference(Border.PaddingProperty,    "LemoineTh_CardPad");
                card.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

                var lbl = new TextBlock { Text = c.Label.ToUpper(), Margin = new Thickness(0, 0, 0, 2) };
                lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

                var valTb = new TextBlock
                {
                    Text         = c.Val,
                    FontWeight   = FontWeights.Medium,
                    TextWrapping = TextWrapping.Wrap,
                };
                valTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                valTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                valTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

                var sp = new StackPanel();
                sp.Children.Add(lbl);
                sp.Children.Add(valTb);
                card.Child = sp;

                WpfGrid.SetRow(card, c.Row);
                WpfGrid.SetColumn(card, c.Col);
                grid.Children.Add(card);
            }
            outer.Children.Add(grid);

            return outer;
        }

        // ─────────────────────────────────────────────────────────────────────
        // S2 — Text type picker (4 roles)
        // ─────────────────────────────────────────────────────────────────────
        private FrameworkElement BuildS2()
        {
            var outer = new StackPanel();

            if (_textTypes.Count == 0)
            {
                var noTypes = new TextBlock { Text = "No TextNoteTypes found in project.", TextWrapping = TextWrapping.Wrap };
                noTypes.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                noTypes.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                noTypes.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                outer.Children.Add(noTypes);
                return outer;
            }

            // Section label
            var sectionLbl = new TextBlock
            {
                Text   = "TEXT STYLES",
                Margin = new Thickness(0, 0, 0, 10),
            };
            sectionLbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            sectionLbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            sectionLbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            outer.Children.Add(sectionLbl);

            // 2-column grid: [Auto label | 8px gap | * picker]
            var grid = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddTypeRow(grid, row: 0, label: "TITLE",        getIdx: () => IndexOf(_titleTypeId),       setId: id => _titleTypeId       = id);
            AddTypeRow(grid, row: 1, label: "SUBTITLE",     getIdx: () => IndexOf(_subtitleTypeId),    setId: id => _subtitleTypeId    = id);
            AddTypeRow(grid, row: 2, label: "GROUP HEADER", getIdx: () => IndexOf(_groupHeaderTypeId), setId: id => _groupHeaderTypeId = id);
            AddTypeRow(grid, row: 3, label: "LABEL",        getIdx: () => IndexOf(_labelTypeId),       setId: id => _labelTypeId       = id);

            outer.Children.Add(grid);
            return outer;
        }

        private void AddTypeRow(
            WpfGrid grid,
            int     row,
            string  label,
            Func<int>            getIdx,
            Action<ElementId?>   setId)
        {
            var lbl = new TextBlock
            {
                Text              = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, row == 0 ? 0 : 8, 0, 0),
            };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var combo = new ComboBox
            {
                IsEditable = false,
                Margin     = new Thickness(0, row == 0 ? 0 : 8, 0, 0),
            };
            combo.SetResourceReference(FrameworkElement.HeightProperty, "LemoineH_Input");
            combo.SetResourceReference(ComboBox.FontFamilyProperty,     "LemoineMonoFont");
            combo.SetResourceReference(ComboBox.FontSizeProperty,       "LemoineFS_SM");

            foreach (var (_, name) in _textTypes)
                combo.Items.Add(name);

            combo.SelectedIndex = Math.Max(0, getIdx());

            combo.SelectionChanged += (s, e) =>
            {
                int idx = combo.SelectedIndex;
                setId(idx >= 0 && idx < _textTypes.Count ? _textTypes[idx].Id : (ElementId?)null);
            };

            WpfGrid.SetRow(lbl,   row); WpfGrid.SetColumn(lbl,   0);
            WpfGrid.SetRow(combo, row); WpfGrid.SetColumn(combo, 2);
            grid.Children.Add(lbl);
            grid.Children.Add(combo);
        }

        private int IndexOf(ElementId? id)
        {
            if (id == null) return 0;
            for (int i = 0; i < _textTypes.Count; i++)
                if (_textTypes[i].Id == id) return i;
            return 0;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // IsValid / SummaryFor / Run
        // ═══════════════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (_legendViews.Count == 0) return false;
            var s = LegendCreatorSettings.Instance;
            return s.Rows.Any(r => r.Groups?.Any(g => g.Blocks?.Count > 0) == true);
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1")
            {
                int idx = _updateMode ? _updateTargetIdx : _createTemplateIdx;
                if (idx < 0 || idx >= _legendViews.Count) return "—";
                return (_updateMode ? "Update: " : "Template: ") + _legendViews[idx].Name;
            }
            if (stepId == "S2")
            {
                string LookupName(ElementId? id)
                {
                    if (id == null) return "—";
                    var t = _textTypes.FirstOrDefault(x => x.Id == id);
                    return string.IsNullOrEmpty(t.Name) ? "—" : t.Name;
                }
                return $"Title: {LookupName(_titleTypeId)}  |  Label: {LookupName(_labelTypeId)}";
            }
            return "—";
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _handler.UpdateMode = _updateMode;

            if (_updateMode)
            {
                _handler.TargetLegendId   =
                    _updateTargetIdx >= 0 && _updateTargetIdx < _legendViews.Count
                        ? _legendViews[_updateTargetIdx].Id : null;
                _handler.TemplateLegendId = null;
            }
            else
            {
                _handler.TemplateLegendId =
                    _createTemplateIdx >= 0 && _createTemplateIdx < _legendViews.Count
                        ? _legendViews[_createTemplateIdx].Id : null;
                _handler.TargetLegendId   = null;
            }

            _handler.TitleTypeId       = _titleTypeId;
            _handler.SubtitleTypeId    = _subtitleTypeId;
            _handler.GroupHeaderTypeId = _groupHeaderTypeId;
            _handler.LabelTypeId       = _labelTypeId;

            _handler.PushLog    = pushLog;
            _handler.OnProgress = onProgress;
            _handler.OnComplete = onComplete;

            pushLog("Raising Revit ExternalEvent…", "info");
            _event.Raise();
        }

        private static TextBlock MakePillText(string text)
        {
            var tb = new TextBlock
            {
                Text              = text,
                VerticalAlignment = VerticalAlignment.Center,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return tb;
        }
    }
}
