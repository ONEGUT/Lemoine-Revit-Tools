using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LemoineTools.Tools.FiltersLegends.LegendCreator;

namespace LemoineTools.Framework.Controls
{
    /// <summary>
    /// One horizontal row of group cards. Carries:
    ///   • A horizontal stack of group cards
    ///   • Drop-zones between groups → emit "insert group at index"
    /// </summary>
    public partial class LegendRow : UserControl
    {
        public LegendRowConfig Row { get; private set; } = new LegendRowConfig();

        public event EventHandler? Changed;
        public event EventHandler<BlockDropArgs>?  BlockDropRequested;
        public event EventHandler<string>?         GroupDeleteRequested;
        public event Action<string, bool, bool>?   BlockClickedOnCanvas; // blockId, ctrl, shift

        // Group cards in left-to-right order. The builder's placement overlay reads
        // their bounds to position the single insertion marker.
        private readonly List<LegendGroupCard> _cardList = new List<LegendGroupCard>();
        internal IReadOnlyList<LegendGroupCard> GroupCards => _cardList;

        // ── Selection context ────────────────────────────────────────────────
        private HashSet<string> _selectionContext = new HashSet<string>();
        private string? _activeContext;

        // Cursor-following drag ghost for whole-group drags.
        private readonly DragGhost _ghost = new DragGhost();

        public LegendRow()
        {
            InitializeComponent();
            Loaded += (s, e) => BuildAll();
        }

        public void Bind(LegendRowConfig row)
        {
            Row = row ?? new LegendRowConfig();
            if (IsLoaded) BuildAll();
        }

        public void SetSelectionContext(HashSet<string> selectedIds, string? activeId)
        {
            _selectionContext = selectedIds ?? new HashSet<string>();
            _activeContext = activeId;
        }

        /// <summary>
        /// Propagates selection state to existing group cards and block rows in-place,
        /// without rebuilding the visual tree. Use instead of Bind() for selection-only updates.
        /// </summary>
        public void RefreshBlockSelection(HashSet<string> selectedIds, string? activeId)
        {
            _selectionContext = selectedIds ?? new HashSet<string>();
            _activeContext    = activeId;
            if (_rowBorder?.Child is Grid rowGrid)
                foreach (var card in rowGrid.Children.OfType<LegendGroupCard>())
                    card.RefreshBlockSelection(_selectionContext, activeId);
        }

        private void BuildAll()
        {
            _rowBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineBg");
            _rowBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            int n = Row.Groups?.Count ?? 0;

            // Proportional Grid — each group gets an equal Star share so cards fill the
            // row. Gutters are fixed card margins (not interactive slivers): group
            // placement is owned entirely by the builder's single-marker overlay.
            var rowGrid = new Grid();
            _cardList.Clear();
            for (int i = 0; i < n; i++)
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int i = 0; i < n; i++)
            {
                var card = new LegendGroupCard
                {
                    Margin = new Thickness(i == 0 ? 0 : 4, 0, i == n - 1 ? 0 : 4, 0),
                };
                card.SetSelectionContext(_selectionContext, _activeContext);
                card.Bind(Row.Groups![i]);
                int idx = i;

                card.BlockClickedOnCanvas += (bId, ctrl, shift) => BlockClickedOnCanvas?.Invoke(bId, ctrl, shift);
                card.Changed            += (s, e) => Changed?.Invoke(this, EventArgs.Empty);
                card.DeleteRequested    += (s, e) => GroupDeleteRequested?.Invoke(this, Row.Groups[idx].Id);
                card.BlockDropRequested += (s, args) => BlockDropRequested?.Invoke(this, args);
                card.GroupDragInitiated += (s, args) =>
                {
                    var payload = new LegendDragPayload
                    {
                        What    = LegendDragPayload.Kind.Group,
                        GroupId = Row.Groups[idx].Id,
                    };
                    QueryContinueDragEventHandler? ghostUpdater = null;
                    try
                    {
                        // Cursor-following snapshot ghost for the whole-group drag.
                        _ghost.Begin(card, args.GetPosition(card));
                        ghostUpdater = (fs, fe) => _ghost.Update();
                        card.QueryContinueDrag += ghostUpdater;

                        LegendDragSession.Begin(payload);
                        DragDrop.DoDragDrop(card,
                            new DataObject(LegendPalette.DragFormat, payload),
                            DragDropEffects.Move);
                    }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Swallowed("LegendRow: group drag", ex);
                    }
                    finally
                    {
                        LegendDragSession.End();
                        if (ghostUpdater != null) card.QueryContinueDrag -= ghostUpdater;
                        _ghost.End();
                    }
                };

                Grid.SetColumn(card, i);
                rowGrid.Children.Add(card);
                _cardList.Add(card);
            }

            _rowBorder.Child = rowGrid;
        }
    }
}
