using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Framework;

namespace LemoineTools.Tools.LinkViews
{
    /// <summary>
    /// View-specific static helpers for <see cref="LinkViewsLevelRunHandler"/>.
    /// Read-only queries and creation helpers only.
    ///
    /// The room search + building clustering that used to live here moved to
    /// <see cref="LemoineTools.Tools.ScopeBoxes.RoomClusterSearch"/> so the scope-box
    /// tools and view tools share one implementation.
    /// </summary>
    internal static class LinkViewsLevelHelpers
    {
        /// <summary>
        /// Fallback Z half-extent used for the top/bottom levels so section boxes
        /// and view ranges always fully contain the model.
        /// </summary>
        public const double UnlimitedZ = 500.0; // feet

        // ── View existence checks ─────────────────────────────────────────────────

        public static bool View3dExists(Document doc, string name) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(View3D)).Cast<View3D>()
                .Any(v => !v.IsTemplate && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

        public static bool PlanExists(Document doc, string name, ViewFamily family) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Any(v => !v.IsTemplate
                          && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)
                          && v.ViewType == (family == ViewFamily.FloorPlan ? ViewType.FloorPlan : ViewType.CeilingPlan));

        // ── View creation helpers ─────────────────────────────────────────────────

        public static ViewFamilyType FindVFT(Document doc, ViewFamily family) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == family);

        public static View3D Create3d(Document doc, string name, ElementId vftId)
        {
            View3D v = View3D.CreateIsometric(doc, vftId);
            try { v.Name = name; } catch (Exception __lex) { DiagnosticsLog.Swallowed($"LinkViews level: set name on view {v.Id.Value}", __lex); }
            return v;
        }

    }
}
