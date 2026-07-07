using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Dimensioning.AutoDimension
{
    /// <summary>
    /// Tracks which UI views a picker opens while activating views for PickObject, and closes
    /// them again afterwards. Activating a view opens it in the Revit UI, and Revit keeps every
    /// open view's graphics in native memory for the rest of the session — so a multi-view pick
    /// pass would otherwise leave dozens of views open and pin their RAM. Views the user already
    /// had open before the pick pass (including the original active view) are never closed.
    /// </summary>
    internal static class PickerViewGuard
    {
        /// <summary>Records the open UI views and the active view before any picker activation.</summary>
        internal static (HashSet<long> OpenIds, ElementId ActiveId) Snapshot(UIDocument uidoc)
        {
            var openIds = new HashSet<long>();
            var activeId = ElementId.InvalidElementId;
            try
            {
                foreach (var uv in uidoc.GetOpenUIViews())
                    openIds.Add(uv.ViewId.Value);
                activeId = uidoc.ActiveView?.Id ?? ElementId.InvalidElementId;
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("PickerViewGuard: snapshot open views", ex); }
            return (openIds, activeId);
        }

        /// <summary>
        /// Restores the original active view, then closes every UI view that was not open in the
        /// snapshot. A close failure (e.g. the last open view) leaves that view open and is logged
        /// rather than aborting the cleanup.
        /// </summary>
        internal static void CloseOpenedViews(
            UIDocument uidoc, (HashSet<long> OpenIds, ElementId ActiveId) before, Action<string, string> log)
        {
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
                    log?.Invoke($"Closed {closed} view(s) the pick pass opened.", "info");
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("PickerViewGuard: close opened views", ex); }
        }
    }
}
