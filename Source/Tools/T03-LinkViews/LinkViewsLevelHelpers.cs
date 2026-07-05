using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.LinkViews
{
    /// <summary>
    /// View-specific static helpers shared between <see cref="LinkViewsLevelPhase1Handler"/>
    /// and <see cref="LinkViewsLevelRunHandler"/>.  No Revit transactions here except the
    /// print-set helper (which documents its own transaction requirement).
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
            try { v.Name = name; } catch (Exception __lex) { LemoineLog.Swallowed($"LinkViews level: set name on view {v.Id.Value}", __lex); }
            return v;
        }

        // ── Print set helper ──────────────────────────────────────────────────────

        /// <summary>
        /// Gets or creates a <see cref="ViewSheetSet"/> named <paramref name="setName"/>
        /// and adds all <paramref name="views"/> to it.  Must be called inside an open
        /// transaction.
        /// </summary>
        public static void GetOrCreateViewSheetSet(
            Document doc, string setName, List<View> views, List<string> log)
        {
            if (views.Count == 0) return;
            try
            {
                var pm = doc.PrintManager;
                pm.PrintRange = Autodesk.Revit.DB.PrintRange.Select;

                ViewSheetSetting vss = pm.ViewSheetSetting;

                // Find existing set
                ViewSheetSet existing = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>()
                    .FirstOrDefault(s => s.Name == setName);

                var viewSet = new ViewSet();
                if (existing != null)
                {
                    // Merge existing views into the set before saving
                    foreach (View v in existing.Views) viewSet.Insert(v);
                }
                foreach (var v in views) viewSet.Insert(v);

                vss.CurrentViewSheetSet.Views = viewSet;
                vss.SaveAs(setName);

                log.Add($"Print set '{setName}' updated ({viewSet.Size} view(s)).");
            }
            catch (Exception e)
            {
                log.Add($"[PrintSet] '{setName}': {e.Message}");
            }
        }
    }
}
