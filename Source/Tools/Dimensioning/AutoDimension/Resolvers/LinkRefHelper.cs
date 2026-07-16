using System;
using Autodesk.Revit.DB;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Dimensioning.AutoDimension.Resolvers
{
    /// <summary>
    /// Converts a reference obtained inside a linked document into a host-resolvable
    /// reference via <see cref="Reference.CreateLinkReference"/>. Coordination slabs/grids
    /// commonly live in a Revit link, so this is first-class. Returns null (and reports) when
    /// a link reference can't be produced rather than failing silently.
    /// </summary>
    public static class LinkRefHelper
    {
        /// <summary>
        /// Promotes <paramref name="linkedRef"/> (valid in the link's document) to a reference
        /// usable for dimensioning in the host, stamped through <paramref name="linkInstance"/>.
        /// Returns null on failure.
        /// </summary>
        public static Reference? ToHostReference(
            RevitLinkInstance linkInstance, Reference linkedRef, Action<string>? reportMissing = null)
        {
            if (linkInstance == null || linkedRef == null)
            {
                reportMissing?.Invoke("null link instance or reference");
                return null;
            }

            try
            {
                return linkedRef.CreateLinkReference(linkInstance);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("LinkRefHelper: CreateLinkReference", ex);
                reportMissing?.Invoke(
                    $"link '{SafeName(linkInstance)}': {ex.Message}");
                return null;
            }
        }

        private static string SafeName(RevitLinkInstance li)
        {
            try { return li.Name ?? li.Id.ToString(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LinkRefHelper: read link name", ex); return "?"; }
        }
    }
}
