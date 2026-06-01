using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace LemoineTools.Lemoine
{
    /// <summary>
    /// Drag-to-reorder for a vertical <see cref="StackPanel"/> list — the whole row is the
    /// drag handle (no separate grip), exactly like the legend rule rows: arm past the system
    /// threshold, show a cursor-following snapshot ghost, drop to a new position. One instance
    /// per list; <see cref="Arm"/> each row as it is built, and the panel-level drop wiring is
    /// set up in the constructor.
    ///
    /// The reorder callback receives (fromIndex, insertionIndex). Use the static
    /// <see cref="Move{T}"/> to apply it to the backing list with correct removal-offset math,
    /// then persist and rebuild.
    /// </summary>
    public sealed class LemoineListReorder
    {
        private const string Fmt = "LemoineListReorder.Index";

        private readonly LemoineDragGhost _ghost = new LemoineDragGhost();
        private readonly StackPanel       _panel;
        private readonly Action<int, int> _onReorder;

        public LemoineListReorder(StackPanel panel, Action<int, int> onReorder)
        {
            _panel     = panel ?? throw new ArgumentNullException(nameof(panel));
            _onReorder = onReorder ?? throw new ArgumentNullException(nameof(onReorder));

            _panel.AllowDrop = true;
            _panel.DragOver += (s, e) =>
            {
                if (e.Data.GetDataPresent(Fmt)) { e.Effects = DragDropEffects.Move; e.Handled = true; }
                else                              e.Effects = DragDropEffects.None;
            };
            _panel.Drop += OnDrop;
        }

        /// <summary>Makes <paramref name="row"/> a drag source that reorders to a new index.</summary>
        public void Arm(FrameworkElement row, int index)
        {
            if (row == null) return;
            LemoineMotion.WireDragArm(row, e =>
            {
                QueryContinueDragEventHandler upd = (fs, fe) => _ghost.Update();
                try
                {
                    _ghost.Begin(row);
                    row.QueryContinueDrag += upd;
                    DragDrop.DoDragDrop(row, new DataObject(Fmt, index), DragDropEffects.Move);
                }
                catch (Exception ex)
                {
                    LemoineLog.Swallowed("LemoineListReorder: drag", ex);
                }
                finally
                {
                    row.QueryContinueDrag -= upd;
                    _ghost.End();
                }
            });
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(Fmt)) return;
            e.Handled = true;
            int from = (int)e.Data.GetData(Fmt);
            int to   = InsertionIndex(e.GetPosition(_panel));
            if (to >= 0 && to != from) _onReorder(from, to);
        }

        // Insertion index = first row whose vertical midpoint is below the cursor, else end.
        private int InsertionIndex(Point p)
        {
            for (int i = 0; i < _panel.Children.Count; i++)
            {
                if (_panel.Children[i] is FrameworkElement fe)
                {
                    var top = fe.TranslatePoint(new Point(0, 0), _panel).Y;
                    if (p.Y < top + fe.ActualHeight / 2.0) return i;
                }
            }
            return _panel.Children.Count;
        }

        /// <summary>
        /// Moves the item at <paramref name="from"/> to <paramref name="insertion"/> in
        /// <paramref name="list"/>, correcting for the shift caused by removing the source
        /// first. No-op for an out-of-range source.
        /// </summary>
        public static void Move<T>(IList<T> list, int from, int insertion)
        {
            if (list == null || from < 0 || from >= list.Count) return;
            var item = list[from];
            list.RemoveAt(from);
            int target = insertion > from ? insertion - 1 : insertion;
            if (target < 0) target = 0;
            if (target > list.Count) target = list.Count;
            list.Insert(target, item);
        }
    }
}
