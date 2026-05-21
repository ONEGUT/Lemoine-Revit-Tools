using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LemoineTools.Tools.Testing.LegendCreator;

namespace LemoineTools.Lemoine.Controls
{
    /// <summary>
    /// One horizontal row of group cards. Carries:
    ///   • Slim drop-zones above and below the row → emit "split into new row"
    ///   • A horizontal stack of group cards
    ///   • Drop-zones between groups → emit "insert group at index"
    /// </summary>
    public partial class LemoineLegendRow : UserControl
    {
        public LegendRowConfig Row { get; private set; } = new LegendRowConfig();

        public event EventHandler? Changed;
        public event EventHandler<BlockDropArgs>?  BlockDropRequested;
        public event EventHandler<GroupDropArgs>?  GroupDropInRowRequested;
        public event EventHandler<RowSplitDropArgs>? RowSplitDropRequested;
        public event EventHandler<string>?         GroupDeleteRequested;

        public LemoineLegendRow()
        {
            InitializeComponent();
            Loaded += (s, e) => BuildAll();
        }

        private static void ResetSplitZone(Border zone)
        {
            zone.Height          = 5;
            zone.Margin          = new Thickness(0);
            zone.Background      = Brushes.Transparent;
            zone.BorderBrush     = null;
            zone.BorderThickness = new Thickness(0);
            zone.CornerRadius    = new CornerRadius(0);
        }

        public void Bind(LegendRowConfig row)
        {
            Row = row ?? new LegendRowConfig();
            if (IsLoaded) BuildAll();
        }

        private void BuildAll()
        {
            _rowBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineBg");
            _rowBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            // Split drop zones
            WireRowSplit(_rowSplitTop,    above: true);
            WireRowSplit(_rowSplitBottom, above: false);

            int n = Row.Groups?.Count ?? 0;

            // Build a proportional Grid: [slot | group★ | slot | group★ | slot …]
            // Each group gets an equal Star share so they fill the full row width.
            var rowGrid = new Grid();

            // Column 0 — leading slot
            var leadingCol = new ColumnDefinition { Width = new GridLength(4) };
            rowGrid.ColumnDefinitions.Add(leadingCol);

            for (int i = 0; i < n; i++)
            {
                // Group column (indices 1, 3, 5 …)
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                // Trailing slot column (indices 2, 4, 6 …)
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            }

            // Leading drop slot (col 0)
            var slot0 = MakeGroupSlot(0, leadingCol);
            Grid.SetColumn(slot0, 0);
            rowGrid.Children.Add(slot0);

            for (int i = 0; i < n; i++)
            {
                var card = new LemoineLegendGroupCard();
                card.Bind(Row.Groups![i]);
                int idx = i;

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
                    try
                    {
                        LegendDragSession.Begin(payload);
                        DragDrop.DoDragDrop(card,
                            new DataObject(LemoineLegendPalette.DragFormat, payload),
                            DragDropEffects.Move);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Row] group drag fail: {ex.Message}");
                    }
                    finally { LegendDragSession.End(); }
                };

                Grid.SetColumn(card, 1 + i * 2);
                rowGrid.Children.Add(card);

                // Trailing slot — column definition at index (2 + i*2)
                var trailingCol = rowGrid.ColumnDefinitions[2 + i * 2];
                var slot        = MakeGroupSlot(i + 1, trailingCol);
                Grid.SetColumn(slot, 2 + i * 2);
                rowGrid.Children.Add(slot);
            }

            _rowBorder.Child = rowGrid;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Slot between groups — width is driven by the owning ColumnDefinition
        // so the group cards can fill the remaining row space with Star sizing.
        // ─────────────────────────────────────────────────────────────────────
        private Border MakeGroupSlot(int targetIndex, ColumnDefinition colDef)
        {
            var slot = new Border
            {
                MinHeight = 40,
                CornerRadius = new CornerRadius(2),
                Background = Brushes.Transparent,
                AllowDrop = true,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            slot.DragEnter += (s, e) =>
            {
                if (IsGroupAccepting(e))
                {
                    colDef.Width = new GridLength(14);
                    slot.SetResourceReference(Border.BackgroundProperty, "LemoineAccent");
                    e.Effects = DragDropEffects.Move;
                    e.Handled = true;
                }
                else e.Effects = DragDropEffects.None;
            };
            slot.DragOver  += (s, e) =>
            {
                e.Effects = IsGroupAccepting(e) ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
            };
            slot.DragLeave += (s, e) =>
            {
                colDef.Width = new GridLength(4);
                slot.Background = Brushes.Transparent;
            };
            slot.Drop      += (s, e) =>
            {
                colDef.Width = new GridLength(4);
                slot.Background = Brushes.Transparent;
                var payload = e.Data.GetData(LemoineLegendPalette.DragFormat) as LegendDragPayload;
                if (payload == null) return;
                if (payload.What != LegendDragPayload.Kind.Group &&
                    payload.What != LegendDragPayload.Kind.PaletteCategory) return;
                GroupDropInRowRequested?.Invoke(this, new GroupDropArgs(payload, Row.Id, targetIndex));
                e.Handled = true;
            };
            return slot;
        }

        private static bool IsGroupAccepting(DragEventArgs e)
        {
            var p = e.Data.GetData(LemoineLegendPalette.DragFormat) as LegendDragPayload;
            if (p == null) return false;
            return p.What == LegendDragPayload.Kind.Group ||
                   p.What == LegendDragPayload.Kind.PaletteCategory;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Above-/below-row split drops → new row request.
        // Only show on hover — no session-wide pre-lighting.
        // ─────────────────────────────────────────────────────────────────────
        private void WireRowSplit(Border zone, bool above)
        {
            zone.AllowDrop = true;
            ResetSplitZone(zone);

            zone.DragEnter += (s, e) =>
            {
                if (IsGroupAccepting(e))
                {
                    zone.Height = 14;
                    zone.Margin = new Thickness(0, 2, 0, 2);
                    zone.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                    zone.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                    zone.BorderThickness = new Thickness(1);
                    zone.CornerRadius    = new CornerRadius(3);
                    e.Effects = DragDropEffects.Move;
                    e.Handled = true;
                }
                else e.Effects = DragDropEffects.None;
            };
            zone.DragOver += (s, e) =>
            {
                e.Effects = IsGroupAccepting(e) ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
            };
            zone.DragLeave += (s, e) => ResetSplitZone(zone);
            zone.Drop += (s, e) =>
            {
                ResetSplitZone(zone);
                var payload = e.Data.GetData(LemoineLegendPalette.DragFormat) as LegendDragPayload;
                if (payload == null) return;
                if (payload.What != LegendDragPayload.Kind.Group &&
                    payload.What != LegendDragPayload.Kind.PaletteCategory) return;
                RowSplitDropRequested?.Invoke(this, new RowSplitDropArgs(payload, Row.Id, above));
                e.Handled = true;
            };
        }
    }

    public sealed class GroupDropArgs : EventArgs
    {
        public LegendDragPayload Payload { get; }
        public string TargetRowId        { get; }
        public int    TargetIndex        { get; }
        public GroupDropArgs(LegendDragPayload p, string rid, int i)
        { Payload = p; TargetRowId = rid; TargetIndex = i; }
    }

    public sealed class RowSplitDropArgs : EventArgs
    {
        public LegendDragPayload Payload { get; }
        public string AnchorRowId        { get; }
        public bool   Above              { get; }
        public RowSplitDropArgs(LegendDragPayload p, string rid, bool above)
        { Payload = p; AnchorRowId = rid; Above = above; }
    }
}
