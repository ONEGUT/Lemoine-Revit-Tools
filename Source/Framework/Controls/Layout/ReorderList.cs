using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LemoineTools.Framework.Controls
{
    /// <summary>
    /// Code-behind-only two-column list whose rows are reordered with Move up / Move down,
    /// selecting the way Revit does: click selects one, Ctrl+click toggles one, Shift+click
    /// selects the range from the anchor.
    ///
    /// Distinct from <see cref="ListReorder"/>, which is drag-and-drop and moves a single row.
    /// This exists for ordering that has to be precise and multi-row — Create Sheets' sheet order,
    /// where the RIGHT column (the sheet number) belongs to the row POSITION and the left column
    /// travels, so what the user is really arranging is which item lands in which numbered slot.
    ///
    /// The control does not own the data. It renders what it is given and reports the move it was
    /// asked for; the caller mutates its own list and calls <see cref="SetRows"/> again. One
    /// source of truth, and the caller's list is always what the run will use.
    /// </summary>
    public sealed class ReorderList : UserControl
    {
        private readonly StackPanel   _rows      = new StackPanel();
        private readonly ScrollViewer _scroll;
        private readonly Button       _upBtn;
        private readonly Button       _downBtn;
        private readonly TextBlock    _leftHeader;
        private readonly TextBlock    _rightHeader;

        private readonly List<Border> _rowBorders = new List<Border>();
        private readonly HashSet<int> _selected   = new HashSet<int>();
        private int _anchor = -1;
        private int _count;

        /// <summary>
        /// The user asked to move the selected rows. Args: the selected indices (ascending) and
        /// the direction (-1 up, +1 down). The caller applies it to its own list, then calls
        /// <see cref="SetRows"/> with the new rows and the moved selection.
        /// </summary>
        public event Action<IReadOnlyList<int>, int>? MoveRequested;

        /// <summary>Raised whenever the selected rows change, so a caller can mirror the selection.</summary>
        public event Action<IReadOnlyList<int>>? SelectionChanged;

        /// <summary>Header shown above the left (item) column.</summary>
        public string LeftHeader  { get => _leftHeader.Text;  set => _leftHeader.Text  = value ?? ""; }

        /// <summary>Header shown above the right (slot) column.</summary>
        public string RightHeader { get => _rightHeader.Text; set => _rightHeader.Text = value ?? ""; }

        public ReorderList()
        {
            // ── Header row ────────────────────────────────────────────────────
            var header = TwoColumnGrid();
            header.Margin = new Thickness(8, 0, 8, 4);
            _leftHeader  = ColumnHeader("");
            _rightHeader = ColumnHeader("");
            Grid.SetColumn(_leftHeader, 0);
            Grid.SetColumn(_rightHeader, 1);
            header.Children.Add(_leftHeader);
            header.Children.Add(_rightHeader);

            // ── Scrolling row list ────────────────────────────────────────────
            // Horizontal scrolling is DISABLED on purpose: it is what gives the rows a finite
            // measured width, which is what makes the star columns inside them legal at all.
            // Inside a vertical StackPanel only the HEIGHT is infinite.
            _scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content                       = _rows,
            };

            var listBorder = new Border
            {
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(0, 4, 0, 4),
                Child           = _scroll,
            };
            listBorder.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorder");
            listBorder.SetResourceReference(Border.BackgroundProperty,   "LemoineRaised");
            listBorder.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_MD");

            // ── Move buttons ──────────────────────────────────────────────────
            _upBtn   = MoveButton(char.ConvertFromUtf32(0x25B2));   // ▲
            _downBtn = MoveButton(char.ConvertFromUtf32(0x25BC));   // ▼
            _upBtn.Click   += (s, e) => RequestMove(-1);
            _downBtn.Click += (s, e) => RequestMove(+1);

            var btnRow = new StackPanel
            {
                Orientation         = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin              = new Thickness(0, 8, 0, 0),
            };
            btnRow.Children.Add(_upBtn);
            btnRow.Children.Add(_downBtn);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(header, 0);      root.Children.Add(header);
            Grid.SetRow(listBorder, 1);  root.Children.Add(listBorder);
            Grid.SetRow(btnRow, 2);      root.Children.Add(btnRow);

            Content = root;
            UpdateButtons();
        }

        /// <summary>Replaces every row. <paramref name="selected"/> restores the selection after a
        /// move (or after a rebuild driven by a change on another step); out-of-range entries are
        /// dropped rather than throwing.</summary>
        public void SetRows(IReadOnlyList<(string Left, string Right)> rows, IReadOnlyList<int>? selected = null)
        {
            var items = rows ?? Array.Empty<(string Left, string Right)>();

            _rows.Children.Clear();
            _rowBorders.Clear();
            _selected.Clear();
            _anchor = -1;
            _count  = items.Count;

            for (int i = 0; i < _count; i++)
            {
                int index = i;                       // captured per row
                var grid  = TwoColumnGrid();

                var left  = RowText(items[i].Left);
                var right = RowText(items[i].Right);
                right.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                Grid.SetColumn(left, 0);
                Grid.SetColumn(right, 1);
                grid.Children.Add(left);
                grid.Children.Add(right);

                var row = new Border
                {
                    Padding = new Thickness(8, 5, 8, 5),
                    Child   = grid,
                    // Direct assignment, never SetResourceReference("Transparent") — a null
                    // Background makes the row hit-testable only on its glyphs, so clicking the
                    // empty part of a row would do nothing.
                    Background = Brushes.Transparent,
                };
                row.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_SM");
                row.MouseLeftButtonDown += (s, e) => { OnRowClick(index); e.Handled = true; };

                _rowBorders.Add(row);
                _rows.Children.Add(row);
            }

            if (selected != null)
            {
                foreach (var i in selected.Where(i => i >= 0 && i < _count)) _selected.Add(i);
                if (_selected.Count > 0) _anchor = _selected.Min();
            }

            PaintRows();
            UpdateButtons();
        }

        /// <summary>Currently selected row indices, ascending.</summary>
        public IReadOnlyList<int> SelectedIndices => _selected.OrderBy(i => i).ToList();

        // ── Selection ─────────────────────────────────────────────────────────
        private void OnRowClick(int index)
        {
            bool ctrl  = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift)   == ModifierKeys.Shift;

            if (shift && _anchor >= 0)
            {
                // Range from the anchor, replacing the selection — Revit's behaviour, and the
                // anchor deliberately stays put so a second Shift+click re-spans from the same row.
                _selected.Clear();
                int lo = Math.Min(_anchor, index), hi = Math.Max(_anchor, index);
                for (int i = lo; i <= hi; i++) _selected.Add(i);
            }
            else if (ctrl)
            {
                if (!_selected.Remove(index)) _selected.Add(index);
                _anchor = index;
            }
            else
            {
                _selected.Clear();
                _selected.Add(index);
                _anchor = index;
            }

            PaintRows();
            UpdateButtons();
            SelectionChanged?.Invoke(SelectedIndices);
        }

        private void RequestMove(int delta)
        {
            var sel = SelectedIndices;
            if (sel.Count == 0 || !CanMove(sel, delta)) return;
            MoveRequested?.Invoke(sel, delta);
        }

        /// <summary>A move is legal only when every selected row has somewhere to go — the block
        /// moves as a block, so one row already at the end blocks the whole move rather than the
        /// selection silently collapsing on top of itself.</summary>
        private bool CanMove(IReadOnlyList<int> sel, int delta)
        {
            if (_count == 0) return false;
            return delta < 0 ? sel[0] > 0 : sel[sel.Count - 1] < _count - 1;
        }

        private void PaintRows()
        {
            for (int i = 0; i < _rowBorders.Count; i++)
            {
                if (_selected.Contains(i))
                {
                    _rowBorders[i].SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");
                }
                else
                {
                    _rowBorders[i].Background = Brushes.Transparent;
                }
            }
        }

        private void UpdateButtons()
        {
            var sel = SelectedIndices;
            _upBtn.IsEnabled   = sel.Count > 0 && CanMove(sel, -1);
            _downBtn.IsEnabled = sel.Count > 0 && CanMove(sel, +1);
        }

        // ── Chrome helpers ────────────────────────────────────────────────────
        private static Grid TwoColumnGrid()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            return g;
        }

        private static TextBlock ColumnHeader(string text)
        {
            var t = new TextBlock { Text = text, FontWeight = FontWeights.Medium };
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            t.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return t;
        }

        private static TextBlock RowText(string text)
        {
            // Ellipsis rather than wrap: every row stays one line high, so the numbered slot on the
            // right always reads across from the item it belongs to.
            var t = new TextBlock
            {
                Text              = text ?? "",
                TextTrimming      = TextTrimming.CharacterEllipsis,
                TextWrapping      = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 8, 0),
            };
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            t.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return t;
        }

        private static Button MoveButton(string glyph)
        {
            var b = new Button
            {
                Content         = glyph,
                Padding         = new Thickness(12, 3, 12, 3),
                Margin          = new Thickness(0, 0, 6, 0),
                BorderThickness = new Thickness(1),
                Cursor          = Cursors.Hand,
                Template        = ControlStyles.BuildFlatButtonTemplate(),
            };
            b.SetResourceReference(Button.MinHeightProperty,   "LemoineH_BtnSm");
            b.SetResourceReference(Button.FontSizeProperty,    "LemoineFS_SM");
            b.SetResourceReference(Button.FontFamilyProperty,  "LemoineUiFont");
            b.SetResourceReference(Button.BackgroundProperty,  "LemoineRaised");
            b.SetResourceReference(Button.BorderBrushProperty, "LemoineBorder");
            b.SetResourceReference(Button.ForegroundProperty,  "LemoineText");
            return b;
        }
    }
}
