using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace LemoineTools.Framework
{
    /// <summary>
    /// Opens Revit UI views for the duration of a run and closes again exactly what it opened.
    ///
    /// Two unrelated tools need this, for two different reasons:
    ///
    /// * a picker that must activate each view to call <c>PickObject</c> in it;
    /// * a bulk writer that must have its target views OPEN so Revit does not recompute a
    ///   closed view for every element written into it (see <see cref="OpenViews"/>).
    ///
    /// Both carry the same hazard: activating a view opens it in the Revit UI, and Revit keeps
    /// every open view's graphics in native memory for the rest of the session — GC can never
    /// reclaim it. A run that activates a dozen views would otherwise pin their RAM until Revit
    /// restarts. Views the user already had open before the run (including the original active
    /// view) are never closed.
    ///
    /// Lives in Framework rather than beside either caller because it is Revit-generic and
    /// belongs to neither tool.
    /// </summary>
    internal static class PickerViewGuard
    {
        /// <summary>
        /// Records the open UI views and the active view before any activation.
        ///
        /// <c>Valid</c> is false when the snapshot could not be taken. That flag is load-bearing:
        /// a failed snapshot yields an EMPTY open-view set, and cleanup driven by an empty set
        /// would read every open view as "opened by this run" and close the user's own views.
        /// <see cref="CloseOpenedViews"/> declines to close anything rather than risk that.
        /// </summary>
        internal static (HashSet<long> OpenIds, ElementId ActiveId, bool Valid) Snapshot(UIDocument uidoc)
        {
            var openIds = new HashSet<long>();
            var activeId = ElementId.InvalidElementId;
            try
            {
                foreach (var uv in uidoc.GetOpenUIViews())
                    openIds.Add(uv.ViewId.Value);
                activeId = uidoc.ActiveView?.Id ?? ElementId.InvalidElementId;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("PickerViewGuard: snapshot open views", ex);
                return (openIds, activeId, false);
            }
            return (openIds, activeId, true);
        }

        /// <summary>
        /// Opens every listed view, skipping the ones the user already had open, and returns how
        /// many were newly opened.
        ///
        /// This is a SPEED measure, not a cosmetic one. Revit maintains a computed element and
        /// geometry set for each OPEN view. A view-specific element created in a CLOSED view has
        /// no such state to build on, so Revit computes the whole view — and because the create
        /// re-dirties it, the next create recomputes it from scratch again. That is the per-item
        /// regeneration that turns a bulk write from seconds into minutes; with the view open the
        /// same loop costs one incremental delta per element.
        ///
        /// MUST be called with NO transaction open: Revit refuses to change the active view
        /// inside one, so every target view has to be opened before the run's transaction starts,
        /// not per view inside it.
        ///
        /// <c>UIDocument.ActiveView</c> is the only way to open a view — <c>RequestViewChange</c>
        /// is deferred until after the API context returns, so it cannot help here, and there is
        /// no hidden/background open. Revit will visibly flip through the views.
        /// </summary>
        internal static int OpenViews(
            UIDocument uidoc, IEnumerable<ElementId> viewIds,
            (HashSet<long> OpenIds, ElementId ActiveId, bool Valid) before,
            Action<string, string>? log = null)
        {
            if (uidoc == null || viewIds == null) return 0;

            int opened = 0, failed = 0;
            var handled = new HashSet<long>();

            foreach (ElementId id in viewIds)
            {
                if (id is null || id == ElementId.InvalidElementId) continue;
                // Already open (user's own, or opened earlier in this same loop) — activating it
                // again would be a wasted view switch.
                if (!handled.Add(id.Value)) continue;
                if (before.OpenIds.Contains(id.Value)) continue;

                if (!(uidoc.Document.GetElement(id) is View v) || v.IsTemplate) continue;

                try { uidoc.ActiveView = v; opened++; }
                catch (Exception ex)
                {
                    // A view Revit refuses to activate loses only the fast path — it is still
                    // written to, just slowly. Never abort the run over it, but never swallow it
                    // silently either: the user's run is about to be far slower than expected.
                    failed++;
                    DiagnosticsLog.Swallowed($"PickerViewGuard: open view {id.Value} for the fast path", ex);
                }
            }

            if (opened > 0)
                log?.Invoke(AppStrings.T("common.log.openedViews", opened), "info");
            if (failed > 0)
                log?.Invoke(AppStrings.T("common.log.openViewFailed", failed), "warn");

            return opened;
        }

        /// <summary>
        /// Restores the original active view, then closes every UI view that was not open in the
        /// snapshot. A close failure (e.g. the last open view) leaves that view open and is logged
        /// rather than aborting the cleanup. Like <see cref="OpenViews"/>, must run with no
        /// transaction open.
        /// </summary>
        internal static void CloseOpenedViews(
            UIDocument uidoc, (HashSet<long> OpenIds, ElementId ActiveId, bool Valid) before, Action<string, string> log)
        {
            // An untrustworthy snapshot means every open view would look like one this run
            // opened. Leaving views open costs graphics RAM; closing the user's own views loses
            // their work-in-progress layout. Leave them.
            if (!before.Valid)
            {
                log?.Invoke(AppStrings.T("common.log.viewSnapshotFailed"), "warn");
                return;
            }

            try
            {
                // Reactivate the user's original view first — the active view cannot be closed.
                if (before.ActiveId != ElementId.InvalidElementId
                    && uidoc.Document.GetElement(before.ActiveId) is View original)
                {
                    try { uidoc.ActiveView = original; }
                    catch (Exception ex) { DiagnosticsLog.Swallowed("PickerViewGuard: restore active view", ex); }
                }

                long activeId = (uidoc.ActiveView?.Id ?? ElementId.InvalidElementId).Value;
                int closed = 0;
                foreach (var uv in uidoc.GetOpenUIViews())
                {
                    long id = uv.ViewId.Value;
                    if (before.OpenIds.Contains(id)) continue;
                    if (id == activeId) continue;
                    try { uv.Close(); closed++; }
                    catch (Exception ex) { DiagnosticsLog.Swallowed("PickerViewGuard: close view", ex); }
                }
                if (closed > 0)
                    log?.Invoke(AppStrings.T("common.log.closedViews", closed), "info");
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("PickerViewGuard: close opened views", ex); }
        }
    }
}
