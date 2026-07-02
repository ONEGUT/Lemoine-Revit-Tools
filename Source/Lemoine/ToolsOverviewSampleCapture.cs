using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Tools.AutoFilters;
using LemoineTools.Tools.Clash;

namespace LemoineTools.Lemoine
{
    // ─────────────────────────────────────────────────────────────────────────
    // Revit-facing capture that fills an OverviewSampleSnapshot from the active
    // document. Runs on the Revit main thread (OpenOverviewCommand) BEFORE the
    // Tools Overview window opens; the snapshot itself is plain strings, so the
    // demo StepFlowWindows on their own STA threads can read it safely.
    //
    // Every pool is wrapped in its own try/catch → LemoineLog.Swallowed so one
    // odd document can't sink the rest — a failed pool just falls back to the
    // canned JSON sample data for that pool.
    // ─────────────────────────────────────────────────────────────────────────
    public static class ToolsOverviewSampleCapture
    {
        private const int PoolCap = 12;

        /// <summary>Category probes per demo group — cheap per-category counts,
        /// never a whole-document element sweep.</summary>
        private static readonly (string GroupKey, BuiltInCategory[] Cats)[] CategoryProbes =
        {
            ("overviewDemos.categoryGroups.mechanical.label", new[]
            {
                BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting,
                BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_FabricationDuctwork,
            }),
            ("overviewDemos.categoryGroups.piping.label", new[]
            {
                BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting,
                BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_PlumbingFixtures,
                BuiltInCategory.OST_Sprinklers, BuiltInCategory.OST_FabricationPipework,
            }),
            ("overviewDemos.categoryGroups.electrical.label", new[]
            {
                BuiltInCategory.OST_CableTray, BuiltInCategory.OST_Conduit,
                BuiltInCategory.OST_ElectricalEquipment, BuiltInCategory.OST_LightingFixtures,
                BuiltInCategory.OST_FabricationContainment,
            }),
            ("overviewDemos.categoryGroups.structure.label", new[]
            {
                BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors,
                BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_StructuralFraming,
                BuiltInCategory.OST_StructuralColumns,
            }),
        };

        /// <summary>Snapshot the active document (null when no document is open).</summary>
        public static OverviewSampleSnapshot? Capture(UIDocument? uidoc)
        {
            var doc = uidoc?.Document;
            var snap = new OverviewSampleSnapshot();

            // Settings-backed pools work even with no document open.
            Guard("trades", () =>
            {
                snap.Trades = (AutoFiltersSettings.Instance.Trades ?? new List<FilterTradeConfig>())
                    .Select(t => t.Label).Where(l => !string.IsNullOrWhiteSpace(l))
                    .Take(PoolCap).ToList();
            });
            Guard("clash definitions", () =>
            {
                snap.Definitions = (ClashDefinitionsSettings.Instance.Definitions ?? new List<ClashDefinition>())
                    .Select(d => d.Name).Where(n => !string.IsNullOrWhiteSpace(n))
                    .Take(PoolCap).ToList();
            });

            if (doc == null)
            {
                LemoineLog.Info("OverviewSampleCapture", "No active document — demos use canned sample data.");
                return snap.Trades.Count > 0 || snap.Definitions.Count > 0 ? snap : null;
            }

            snap.DocumentTitle = doc.Title;

            Guard("views", () =>
            {
                foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
                {
                    if (v.IsTemplate) { snap.Templates.Add(v.Name); continue; }
                    switch (v.ViewType)
                    {
                        case ViewType.FloorPlan:    snap.PlanViews.Add(v.Name);    break;
                        case ViewType.CeilingPlan:  snap.CeilingPlans.Add(v.Name); break;
                        case ViewType.ThreeD:       snap.Views3D.Add(v.Name);      break;
                        case ViewType.Section:
                        case ViewType.Elevation:    snap.Sections.Add(v.Name);     break;
                    }
                }
                SortCap(snap.PlanViews); SortCap(snap.CeilingPlans);
                SortCap(snap.Views3D);   SortCap(snap.Sections); SortCap(snap.Templates);
            });

            Guard("sheets", () =>
            {
                snap.Sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Where(s => !s.IsTemplate)
                    .Select(s => $"{s.SheetNumber} - {s.Name}")
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(PoolCap).ToList();
            });

            Guard("levels", () =>
            {
                snap.Levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .Select(l => l.Name).Take(PoolCap).ToList();
            });

            Guard("grids", () =>
            {
                snap.Grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
                    .Select(g => g.Name)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(PoolCap).ToList();
            });

            Guard("reference planes", () =>
            {
                snap.RefPlanes = new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                    .Select(r => r.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n) && n != "Reference Plane")
                    .Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(PoolCap).ToList();
            });

            Guard("title blocks", () =>
            {
                snap.TitleBlocks = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_TitleBlocks).Cast<FamilySymbol>()
                    .Select(fs => $"{fs.FamilyName}: {fs.Name}")
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(PoolCap).ToList();
            });

            Guard("view filters", () =>
            {
                snap.Filters = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement))
                    .Cast<ParameterFilterElement>().Select(f => f.Name)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(PoolCap).ToList();
            });

            Guard("links", () =>
            {
                snap.Links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .Select(li => li.GetLinkDocument()?.Title ?? li.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(PoolCap).ToList();
                snap.Documents.Add(doc.Title);
                snap.Documents.AddRange(snap.Links);
            });

            Guard("categories", () =>
            {
                foreach (var (groupKey, cats) in CategoryProbes)
                {
                    var present = new List<string>();
                    foreach (var bic in cats)
                    {
                        int count = new FilteredElementCollector(doc).OfCategory(bic)
                            .WhereElementIsNotElementType().GetElementCount();
                        if (count <= 0) continue;
                        var name = Category.GetCategory(doc, bic)?.Name;
                        if (!string.IsNullOrWhiteSpace(name)) present.Add(name!);
                    }
                    if (present.Count > 0) snap.CategoryGroups[LemoineStrings.T(groupKey)] = present;
                }
            });

            LemoineLog.Info("OverviewSampleCapture",
                $"'{snap.DocumentTitle}': {snap.PlanViews.Count} plans, {snap.CeilingPlans.Count} RCPs, " +
                $"{snap.Views3D.Count} 3D, {snap.Sections.Count} sections, {snap.Sheets.Count} sheets, " +
                $"{snap.Levels.Count} levels, {snap.Grids.Count} grids, {snap.Links.Count} links, " +
                $"{snap.Filters.Count} filters, {snap.CategoryGroups.Count} category groups.");
            return snap;
        }

        private static void SortCap(List<string> list)
        {
            list.Sort(StringComparer.OrdinalIgnoreCase);
            if (list.Count > PoolCap) list.RemoveRange(PoolCap, list.Count - PoolCap);
        }

        // A failed pool must not sink the capture — the demo falls back to canned
        // data for that pool, and the failure is visible in diagnostics.log.
        private static void Guard(string what, Action fill)
        {
            try { fill(); }
            catch (Exception ex) { LemoineLog.Swallowed($"OverviewSampleCapture: {what}", ex); }
        }
    }
}
