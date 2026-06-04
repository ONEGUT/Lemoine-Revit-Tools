using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LemoineTools.Tools.BulkExport
{
    /// <summary>
    /// Code-behind-only UserControl that renders a sheet-pack layout editor.
    ///
    /// Left panel  — available sheets not yet in the current pack (scrollable list).
    /// Right panel — ordered sheets in the current pack (scrollable Canvas with cards).
    ///
    /// Cards in the right panel can be reordered via the Up/Down buttons.
    /// Double-clicking a card in either panel moves it to the other panel.
    /// </summary>
    public class SheetPackLayoutEditor : UserControl
    {
        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Fired when the pack's sheet list changes (add, remove, reorder).</summary>
        public event Action? LayoutChanged;

        /// <summary>Returns the current ordered sheet numbers in the pack.</summary>
        public IReadOnlyList<string> PackSheetNumbers => _packSheets.AsReadOnly();

        // ── Internal state ────────────────────────────────────────────────────

        // Available = all project sheets keyed by number
        private readonly Dictionary<string, string> _allSheets = new Dictionary<string, string>(); // number → name

        // Left: sheets not in pack (sorted)
        private readonly List<string> _availableSheets = new List<string>();

        // Right: sheets in pack (ordered)
        private readonly List<string> _packSheets = new List<string>();

        // UI panels
        private readonly StackPanel _availablePanel;
        private readonly StackPanel _packPanel;

        // ── Construction ──────────────────────────────────────────────────────

        public SheetPackLayoutEditor()
        {
            // ── Root layout: two equal columns ────────────────────────────────
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) }); // gutter
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ── Left side: Available sheets ───────────────────────────────────
            var leftOuter = new StackPanel();
            var leftHeader = BuildColumnHeader("AVAILABLE SHEETS", "Double-click to add →");
            leftOuter.Children.Add(leftHeader);

            var leftScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 320,
            };
            _availablePanel = new StackPanel();
            leftScroll.Content = _availablePanel;
            leftOuter.Children.Add(leftScroll);

            Grid.SetColumn(leftOuter, 0);
            root.Children.Add(leftOuter);

            // ── Right side: Pack sheets ────────────────────────────────────────
            var rightOuter = new StackPanel();
            var rightHeader = BuildColumnHeader("IN THIS PACK", "← Double-click to remove");
            rightOuter.Children.Add(rightHeader);

            var rightScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 320,
            };
            _packPanel = new StackPanel();
            rightScroll.Content = _packPanel;
            rightOuter.Children.Add(rightScroll);

            Grid.SetColumn(rightOuter, 2);
            root.Children.Add(rightOuter);

            Content = root;
        }

        // ── Public methods ────────────────────────────────────────────────────

        /// <summary>
        /// Loads the full sheet dictionary and the initial pack sheet list.
        /// </summary>
        /// <param name="allSheets">All project sheets: number → name.</param>
        /// <param name="packSheetNumbers">Sheets initially in the pack (ordered).</param>
        public void Load(Dictionary<string, string> allSheets, IEnumerable<string>? packSheetNumbers)
        {
            _allSheets.Clear();
            foreach (var kv in allSheets) _allSheets[kv.Key] = kv.Value;

            _packSheets.Clear();
            if (packSheetNumbers != null)
                _packSheets.AddRange(packSheetNumbers.Where(n => _allSheets.ContainsKey(n)));

            _availableSheets.Clear();
            _availableSheets.AddRange(
                _allSheets.Keys.Except(_packSheets).OrderBy(n => n));

            Rebuild();
        }

        // ── Building card rows ────────────────────────────────────────────────

        private void Rebuild()
        {
            RebuildAvailable();
            RebuildPack();
        }

        private void RebuildAvailable()
        {
            _availablePanel.Children.Clear();
            foreach (var num in _availableSheets)
            {
                var card = BuildSheetCard(num, _allSheets.TryGetValue(num, out var n) ? n : "", isInPack: false);
                _availablePanel.Children.Add(card);
            }

            if (_availableSheets.Count == 0)
            {
                var empty = BuildEmptyLabel("All sheets are in the pack.");
                _availablePanel.Children.Add(empty);
            }
        }

        private void RebuildPack()
        {
            _packPanel.Children.Clear();
            for (int i = 0; i < _packSheets.Count; i++)
            {
                int captured = i;
                var num  = _packSheets[i];
                var name = _allSheets.TryGetValue(num, out var n) ? n : "";

                var card = BuildSheetCard(num, name, isInPack: true,
                    onUp:   captured > 0                    ? (Action)(() => MovePackSheet(captured, -1)) : null,
                    onDown: captured < _packSheets.Count - 1? (Action)(() => MovePackSheet(captured, +1)) : null);
                _packPanel.Children.Add(card);
            }

            if (_packSheets.Count == 0)
            {
                var empty = BuildEmptyLabel("No sheets in this pack yet.");
                _packPanel.Children.Add(empty);
            }
        }

        private Border BuildSheetCard(string number, string name, bool isInPack,
                                       Action? onUp = null, Action? onDown = null)
        {
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
                Margin          = new Thickness(0, 0, 0, 4),
                Cursor          = Cursors.Hand,
            };
            card.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            card.MouseEnter += (s, e) =>
                card.SetResourceReference(Border.BackgroundProperty, "LemoineSelectBg");
            card.MouseLeave += (s, e) =>
                card.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");

            // Double-click toggles membership
            card.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount < 2) return;
                if (isInPack)
                    RemoveFromPack(number);
                else
                    AddToPack(number);
            };

            var innerGrid = new Grid();
            innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // number chip
            innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name
            if (isInPack)
            {
                innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // buttons
            }
            innerGrid.Margin = new Thickness(8, 5, 8, 5);

            // Sheet number chip
            var numChip = new Border
            {
                CornerRadius    = new CornerRadius(2),
                Padding         = new Thickness(4, 1, 4, 1),
                Margin          = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            numChip.SetResourceReference(Border.BackgroundProperty, "LemoineAccentMuted");
            var numText = new TextBlock
            {
                Text       = number,
                FontWeight = FontWeights.SemiBold,
            };
            numText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            numText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            numText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            numChip.Child = numText;
            Grid.SetColumn(numChip, 0);
            innerGrid.Children.Add(numChip);

            // Sheet name
            var nameText = new TextBlock
            {
                Text                = name,
                TextTrimming        = TextTrimming.CharacterEllipsis,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            nameText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            nameText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            nameText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            Grid.SetColumn(nameText, 1);
            innerGrid.Children.Add(nameText);

            // Up/Down buttons (pack side only)
            if (isInPack)
            {
                var btnPanel = new StackPanel
                {
                    Orientation       = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(6, 0, 0, 0),
                };

                var upBtn   = BuildArrowButton("▲", onUp);
                var downBtn = BuildArrowButton("▼", onDown);
                btnPanel.Children.Add(upBtn);
                btnPanel.Children.Add(downBtn);
                Grid.SetColumn(btnPanel, 2);
                innerGrid.Children.Add(btnPanel);
            }

            card.Child = innerGrid;
            return card;
        }

        private Button BuildArrowButton(string symbol, Action? onClick)
        {
            var btn = new Button
            {
                Content         = symbol,
                Width           = 20,
                Height          = 20,
                Margin          = new Thickness(2, 0, 0, 0),
                Padding         = new Thickness(0),
                BorderThickness = new Thickness(0),
                IsEnabled       = onClick != null,
                Cursor          = onClick != null ? Cursors.Hand : Cursors.Arrow,
                ToolTip         = symbol == "▲" ? "Move up" : "Move down",
            };
            btn.SetResourceReference(Button.BackgroundProperty, "LemoineCanvas");
            btn.SetResourceReference(Button.ForegroundProperty, onClick != null ? "LemoineTextDim" : "LemoineBorder");
            btn.SetResourceReference(Button.FontSizeProperty,   "LemoineFS_SM");
            btn.SetResourceReference(Button.FontFamilyProperty, "LemoineUiFont");

            if (onClick != null)
            {
                btn.Click       += (s, e) => { e.Handled = true; onClick(); };
                btn.MouseEnter  += (s, e) => btn.SetResourceReference(Button.ForegroundProperty, "LemoineAccent");
                btn.MouseLeave  += (s, e) => btn.SetResourceReference(Button.ForegroundProperty, "LemoineTextDim");
            }
            return btn;
        }

        private UIElement BuildColumnHeader(string title, string hint)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };

            var titleBlock = new TextBlock
            {
                Text       = title,
                FontWeight = FontWeights.SemiBold,
            };
            titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            titleBlock.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            titleBlock.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            sp.Children.Add(titleBlock);

            var hintBlock = new TextBlock
            {
                Text      = hint,
                FontStyle = FontStyles.Italic,
                Margin    = new Thickness(0, 1, 0, 0),
            };
            hintBlock.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            hintBlock.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            hintBlock.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XS");
            sp.Children.Add(hintBlock);

            return sp;
        }

        private TextBlock BuildEmptyLabel(string text)
        {
            var tb = new TextBlock
            {
                Text      = text,
                Margin    = new Thickness(0, 8, 0, 0),
                FontStyle = FontStyles.Italic,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return tb;
        }

        // ── Mutation helpers ──────────────────────────────────────────────────

        private void AddToPack(string sheetNumber)
        {
            _availableSheets.Remove(sheetNumber);
            if (!_packSheets.Contains(sheetNumber))
                _packSheets.Add(sheetNumber);
            Rebuild();
            LayoutChanged?.Invoke();
        }

        private void RemoveFromPack(string sheetNumber)
        {
            _packSheets.Remove(sheetNumber);
            if (!_availableSheets.Contains(sheetNumber))
            {
                _availableSheets.Add(sheetNumber);
                _availableSheets.Sort(StringComparer.OrdinalIgnoreCase);
            }
            Rebuild();
            LayoutChanged?.Invoke();
        }

        private void MovePackSheet(int index, int delta)
        {
            int newIndex = index + delta;
            if (newIndex < 0 || newIndex >= _packSheets.Count) return;
            var item = _packSheets[index];
            _packSheets.RemoveAt(index);
            _packSheets.Insert(newIndex, item);
            RebuildPack();
            LayoutChanged?.Invoke();
        }
    }
}
