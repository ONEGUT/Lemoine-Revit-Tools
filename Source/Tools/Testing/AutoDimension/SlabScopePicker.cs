using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Testing.AutoDimension.Resolvers;

namespace LemoineTools.Tools.Testing.AutoDimension
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
            foreach (var viewId in viewIds)
            {
                if (!(doc.GetElement(viewId) is View view)) continue;

                try { uidoc.ActiveView = view; }
                catch (Exception ex) { LemoineLog.Swallowed("SlabScopePicker: set active view", ex); }

                try
                {
                    log($"ACTION — view '{view.Name}': click the slab/floor to dimension to in the Revit view (Esc = scan all floors).", "info");
                    var r = uidoc.Selection.PickObject(ObjectType.Element, filter,
                        $"Pick the slab/floor to dimension to for view '{view.Name}' (Esc to scan all floors).");
                    var scope = BuildScope(doc, r, log, view.Name);
                    if (scope != null)
                    {
                        map[viewId] = new List<SlabScope> { scope };
                        log($"View '{view.Name}': slab scoped to one floor.", "info");
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    log($"View '{view.Name}': no floor picked — scanning all floors.", "info");
                }
                catch (Exception ex)
                {
                    LemoineLog.Error("SlabScopePicker: pick", ex);
                    log($"View '{view.Name}': floor pick failed — {ex.Message}", "fail");
                }
            }
            return map;
        }

        private static SlabScope? BuildScope(Document doc, Reference r, Action<string, string> log, string viewName)
        {
            if (r == null) return null;
            try
            {
                if (r.LinkedElementId != ElementId.InvalidElementId)
                {
                    var li = doc.GetElement(r.ElementId) as RevitLinkInstance;
                    var le = li?.GetLinkDocument()?.GetElement(r.LinkedElementId);
                    if (le is Floor)
                        return new SlabScope { LinkInstanceId = r.ElementId, FloorId = r.LinkedElementId };
                }
                else if (doc.GetElement(r.ElementId) is Floor)
                {
                    return new SlabScope { LinkInstanceId = ElementId.InvalidElementId, FloorId = r.ElementId };
                }
            }
            catch (Exception ex) { LemoineLog.Swallowed("SlabScopePicker: resolve picked floor", ex); }

            log($"View '{viewName}': picked element is not a floor — scanning all floors.", "info");
            return null;
        }

        /// <summary>Allows host floors directly, and linked floors via their reference.</summary>
        private sealed class FloorFilter : ISelectionFilter
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
