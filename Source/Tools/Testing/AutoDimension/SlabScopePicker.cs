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
                    var scope = ResolveScope(doc, r, out string name);
                    if (scope != null)
                    {
                        map[viewId] = new List<SlabScope> { scope };
                        log($"View '{view.Name}': slab scoped to {name}.", "info");
                    }
                    else
                    {
                        log($"View '{view.Name}': picked element is not a floor — scanning all floors.", "info");
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
