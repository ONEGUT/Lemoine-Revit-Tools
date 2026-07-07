using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Dimensioning.AutoDimension.Resolvers;

namespace LemoineTools.Tools.Dimensioning.AutoDimension
{
    /// <summary>
    /// Slab-edge target mode: prompts the user to pick ONE floor element per view (host or linked).
    /// Slab-edge resolution is then scoped to that floor's edges only — instead of scanning every
    /// floor — mirroring the older auto-clash workflow. Must run on Revit's main thread, before the
    /// read-only plan build. Esc on a view skips it (that view falls back to scanning all floors).
    /// </summary>
    public static class SlabScopePicker
    {
        public static Dictionary<ElementId, List<SlabScope>> PickForViews(
            UIDocument uidoc, IList<ElementId> viewIds, Action<string, string> log)
        {
            log = log ?? ((a, b) => { });
            var map = new Dictionary<ElementId, List<SlabScope>>();
            if (uidoc == null || viewIds == null) return map;

            var doc = uidoc.Document;
            var filter = new FloorFilter(doc);
            var before = PickerViewGuard.Snapshot(uidoc);
            try
            {
                foreach (var viewId in viewIds)
                {
                    if (!(doc.GetElement(viewId) is View view)) continue;

                    try { uidoc.ActiveView = view; }
                    catch (Exception ex) { LemoineLog.Swallowed("SlabScopePicker: set active view", ex); }

                    try
                    {
                        // A single PickObject can't reach both host and linked floors, so the in-canvas
                        // flow cascades on Esc: host floor → linked floor → scan all floors.
                        log($"ACTION — view '{view.Name}': click a HOST floor to dimension to (Esc = pick from a link instead).", "info");
                        var scope = PickFloor(uidoc, filter, ObjectType.Element,
                            $"Pick a HOST floor for view '{view.Name}' (Esc to pick from a link).", out string name);

                        if (scope == null)
                        {
                            log($"ACTION — view '{view.Name}': click a LINKED floor to dimension to (Esc = scan all floors).", "info");
                            scope = PickFloor(uidoc, filter, ObjectType.LinkedElement,
                                $"Pick a LINKED floor for view '{view.Name}' (Esc to scan all floors).", out name);
                        }

                        if (scope != null)
                        {
                            map[viewId] = new List<SlabScope> { scope };
                            log($"View '{view.Name}': slab scoped to {name}.", "info");
                        }
                        else
                        {
                            log($"View '{view.Name}': no floor picked — scanning all floors.", "info");
                        }
                    }
                    catch (Exception ex)
                    {
                        LemoineLog.Error("SlabScopePicker: pick", ex);
                        log($"View '{view.Name}': floor pick failed — {ex.Message}", "fail");
                    }
                }
            }
            finally
            {
                // Activating each view opened it in the UI — close what the pick pass opened
                // so the run doesn't leave dozens of views (and their graphics RAM) behind.
                PickerViewGuard.CloseOpenedViews(uidoc, before, log);
            }
            return map;
        }

        /// <summary>
        /// Runs one PickObject in the given mode (<see cref="ObjectType.Element"/> for host floors,
        /// <see cref="ObjectType.LinkedElement"/> to drill into a link) and resolves it to a scope.
        /// Returns null when the user presses Esc or picks a non-floor; genuine pick errors bubble up.
        /// </summary>
        private static SlabScope? PickFloor(
            UIDocument uidoc, FloorFilter filter, ObjectType objectType, string prompt, out string name)
        {
            name = "";
            try
            {
                var r = uidoc.Selection.PickObject(objectType, filter, prompt);
                return ResolveScope(uidoc.Document, r, out name);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null;   // Esc — caller cascades to the next stage.
            }
        }

        /// <summary>Resolves a picked reference to a floor scope (host or linked), with a display
        /// name. Returns null when the reference is not a floor.</summary>
        internal static SlabScope? ResolveScope(Document doc, Reference r, out string name)
        {
            name = "";
            if (r == null) return null;
            try
            {
                if (r.LinkedElementId != ElementId.InvalidElementId)
                {
                    var li = doc.GetElement(r.ElementId) as RevitLinkInstance;
                    var le = li?.GetLinkDocument()?.GetElement(r.LinkedElementId);
                    if (le is Floor f)
                    {
                        name = $"{SafeName(f)} (linked: {SafeName(li)})";
                        return new SlabScope { LinkInstanceId = r.ElementId, FloorId = r.LinkedElementId };
                    }
                }
                else if (doc.GetElement(r.ElementId) is Floor f)
                {
                    name = SafeName(f);
                    return new SlabScope { LinkInstanceId = ElementId.InvalidElementId, FloorId = r.ElementId };
                }
            }
            catch (Exception ex) { LemoineLog.Swallowed("SlabScopePicker: resolve picked floor", ex); }
            return null;
        }

        private static string SafeName(Element? e)
        {
            try { return string.IsNullOrWhiteSpace(e?.Name) ? (e?.Id.ToString() ?? "?") : e!.Name; }
            catch { return "?"; }
        }

        /// <summary>Allows host floors directly, and linked floors via their reference.</summary>
        internal sealed class FloorFilter : ISelectionFilter
        {
            private readonly Document _doc;
            public FloorFilter(Document doc) { _doc = doc; }

            public bool AllowElement(Element e) => e is Floor || e is RevitLinkInstance;

            public bool AllowReference(Reference reference, XYZ position)
            {
                try
                {
                    if (reference.LinkedElementId != ElementId.InvalidElementId)
                    {
                        var li = _doc.GetElement(reference.ElementId) as RevitLinkInstance;
                        var le = li?.GetLinkDocument()?.GetElement(reference.LinkedElementId);
                        return le is Floor;
                    }
                }
                catch (Exception ex) { LemoineLog.Swallowed("SlabScopePicker: filter linked reference", ex); }
                return false;
            }
        }
    }
}
