using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using LemoineTools.Framework;
using LemoineTools.Tools.Dimensioning.AutoDimension.Resolvers;

namespace LemoineTools.Tools.Dimensioning.AutoDimension
{
    /// <summary>
    /// Floor-pick support for the slab-edge target mode: the selection filter and the
    /// picked-reference → <see cref="SlabScope"/> resolution used by the up-front
    /// <see cref="SlabPickEventHandler"/> pick. (The per-view in-canvas cascading pick this
    /// class used to host was dead code — superseded by the up-front pick — and was removed.)
    /// </summary>
    public static class SlabScopePicker
    {
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
            catch (Exception ex) { DiagnosticsLog.Swallowed("SlabScopePicker: resolve picked floor", ex); }
            return null;
        }

        private static string SafeName(Element? e)
        {
            try { return string.IsNullOrWhiteSpace(e?.Name) ? (e?.Id.ToString() ?? "?") : e!.Name; }
            catch (Exception ex) { DiagnosticsLog.Swallowed("SlabScopePicker: read element name", ex); return "?"; }
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
                catch (Exception ex) { DiagnosticsLog.Swallowed("SlabScopePicker: filter linked reference", ex); }
                return false;
            }
        }
    }
}
