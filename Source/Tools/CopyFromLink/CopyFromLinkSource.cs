using System;
using System.Globalization;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;
using LemoineTools.Tools.CopyFromLink;

namespace LemoineTools.Tools.CopyFromLink
{
    /// <summary>
    /// One source of truth for how the scan and the run identify an element, so a "Family: Type"
    /// offered as a pick is exactly the value the run compares against. Element collection +
    /// link/transform resolution are delegated to <see cref="CopyLinearSource"/> (shared reader)
    /// via a translated <see cref="CopyLinearSourceSpec"/> — category + workset filtering is
    /// identical to Copy Linear.
    /// </summary>
    public static class CopyFromLinkSource
    {
        /// <summary>Resolve the link document + transform (never the host — this tool copies from links).</summary>
        public static CopyLinearSource.ResolvedSource? Resolve(Document hostDoc, long linkInstId)
            => CopyLinearSource.Resolve(hostDoc, linkInstId);

        /// <summary>Translate this tool's spec into the shared reader's spec for collection.</summary>
        public static CopyLinearSourceSpec ToLinearSpec(CopyFromLinkSpec spec) => new CopyLinearSourceSpec
        {
            LinkInstId         = spec.LinkInstId,
            Categories         = new System.Collections.Generic.List<string>(spec.Categories ?? new System.Collections.Generic.List<string>()),
            ExcludedWorksetIds = new System.Collections.Generic.List<int>(spec.ExcludedWorksetIds ?? new System.Collections.Generic.List<int>()),
        };

        /// <summary>
        /// "Family: Type" key for an element, read from its type element (works for both loadable
        /// families and system families, where <see cref="ElementType.FamilyName"/> is the system
        /// family name e.g. "Basic Wall"). Falls back to the type name, then the element name.
        /// </summary>
        public static string TypeKey(Document srcDoc, Element el)
        {
            try
            {
                var typeId = el?.GetTypeId();
                var et = (typeId != null && typeId != ElementId.InvalidElementId)
                    ? srcDoc?.GetElement(typeId) as ElementType : null;
                if (et != null)
                {
                    string fam = SafeName(() => et.FamilyName);
                    string tn  = SafeName(() => et.Name);
                    if (!string.IsNullOrWhiteSpace(fam) && !string.IsNullOrWhiteSpace(tn)) return fam + ": " + tn;
                    if (!string.IsNullOrWhiteSpace(tn)) return tn;
                }
                string en = SafeName(() => el?.Name ?? "");
                return string.IsNullOrWhiteSpace(en) ? "(unnamed type)" : en;
            }
            catch (Exception ex) { LemoineLog.Swallowed("CopyFromLinkSource.TypeKey", ex); return "(unnamed type)"; }
        }

        /// <summary>Category display name for grouping (falls back to "Other").</summary>
        public static string CategoryDisplay(Element el)
        {
            try
            {
                var n = el?.Category?.Name;
                return string.IsNullOrWhiteSpace(n) ? "Other" : n!;
            }
            catch (Exception ex) { LemoineLog.Swallowed("CopyFromLinkSource.CategoryDisplay", ex); return "Other"; }
        }

        /// <summary>
        /// Identity hash for change detection: the "Family: Type" key plus the element's world-space
        /// bounding-box corners (link transform applied), rounded. A move/resize/type-swap changes it,
        /// so a re-run rebuilds only what actually changed. A box-less element hashes on its key alone.
        /// </summary>
        public static string GeoHash(Document srcDoc, Element el, Transform t, string typeKey)
        {
            string box = "(no box)";
            try
            {
                var bb = el?.get_BoundingBox(null);
                if (bb != null)
                {
                    var tr = (t ?? Transform.Identity).Multiply(bb.Transform);
                    XYZ mn = tr.OfPoint(bb.Min), mx = tr.OfPoint(bb.Max);
                    box = R(mn) + ";" + R(mx);
                }
            }
            catch (Exception ex) { LemoineLog.Swallowed("CopyFromLinkSource.GeoHash: box", ex); }
            return (typeKey ?? "") + "|" + box;
        }

        /// <summary>Composite source key: which link instance + which element, both UniqueId-stable.</summary>
        public static string SourceKey(string linkInstanceUid, string sourceElementUid)
            => (linkInstanceUid ?? "?") + "::" + (sourceElementUid ?? "");

        private static string R(XYZ p) => string.Format(CultureInfo.InvariantCulture, "{0:F3},{1:F3},{2:F3}", p.X, p.Y, p.Z);

        private static string SafeName(Func<string?> get)
        {
            try { return get() ?? ""; }
            catch (Exception ex) { LemoineLog.Swallowed("CopyFromLinkSource.SafeName", ex); return ""; }
        }
    }
}
