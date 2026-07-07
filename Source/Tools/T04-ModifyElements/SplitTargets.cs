using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.ModifyElements
{
    /// <summary>
    /// Split strategy an element category is handled by. Determines which geometry engine in
    /// <see cref="SplitElementsShared"/> / <see cref="SplitByCellHelpers"/> processes it.
    /// </summary>
    internal enum SplitStrategyKind
    {
        /// <summary>Walls / columns — copies with adjusted base/top level constraints.</summary>
        LevelConstrained,
        /// <summary>MEP curves / structural framing — split along a straight LocationCurve.</summary>
        LinearCurve,
        /// <summary>Floors, ceilings, footprint roofs, foundation slabs, filled regions —
        /// boolean/polygon split of the element's own footprint solid or sketch.</summary>
        SheetSolid,
    }

    internal enum SplitCutterKind { Level, Grid, ReferencePlane, Cell }

    /// <summary>
    /// Describes one splittable category: its strategy, a display note, and which cutters
    /// (Level / Grid / Reference Plane / Cell) can act on it.
    /// </summary>
    internal sealed class SplitTargetDef
    {
        internal BuiltInCategory   Category         { get; }
        internal string            Label            { get; }
        internal SplitStrategyKind Strategy         { get; }
        internal string            Note             { get; }
        internal bool              SupportsLevel    { get; }
        internal bool              SupportsGrid     { get; }
        internal bool              SupportsRefPlane { get; }
        internal bool              SupportsCell     { get; }

        internal SplitTargetDef(
            BuiltInCategory category, string label, SplitStrategyKind strategy, string note,
            bool supportsLevel, bool supportsGrid, bool supportsRefPlane, bool supportsCell)
        {
            Category         = category;
            Label            = label;
            Strategy         = strategy;
            Note             = note;
            SupportsLevel    = supportsLevel;
            SupportsGrid     = supportsGrid;
            SupportsRefPlane = supportsRefPlane;
            SupportsCell     = supportsCell;
        }
    }

    /// <summary>
    /// Single source of truth for which element categories the split tools support, and by
    /// which cutter. Drives both the category pickers (so a tool never offers a category it
    /// cannot act on — see CLAUDE.md's "hide invalid options" rule) and the run-time strategy
    /// dispatch in <see cref="SplitElementsShared"/> / <see cref="SplitByCellHelpers"/>.
    /// </summary>
    internal static class SplitTargets
    {
        internal static readonly IReadOnlyList<SplitTargetDef> All = new[]
        {
            new SplitTargetDef(BuiltInCategory.OST_Walls, "Walls",
                SplitStrategyKind.LevelConstrained,
                "One segment per level span (Level) or curve-based split (Grid / Reference Plane)",
                supportsLevel: true, supportsGrid: true, supportsRefPlane: true, supportsCell: false),

            new SplitTargetDef(BuiltInCategory.OST_StructuralColumns, "Structural Columns",
                SplitStrategyKind.LevelConstrained,
                "One segment per level span — base/top constraint",
                supportsLevel: true, supportsGrid: false, supportsRefPlane: false, supportsCell: false),

            new SplitTargetDef(BuiltInCategory.OST_Columns, "Architectural Columns",
                SplitStrategyKind.LevelConstrained,
                "One segment per level span — base/top constraint",
                supportsLevel: true, supportsGrid: false, supportsRefPlane: false, supportsCell: false),

            new SplitTargetDef(BuiltInCategory.OST_StructuralFraming, "Structural Framing",
                SplitStrategyKind.LinearCurve,
                "Splits vertical/diagonal members — curve-based",
                supportsLevel: true, supportsGrid: true, supportsRefPlane: true, supportsCell: false),

            new SplitTargetDef(BuiltInCategory.OST_DuctCurves, "Ducts",
                SplitStrategyKind.LinearCurve,
                "Splits in place — connectivity preserved",
                supportsLevel: true, supportsGrid: true, supportsRefPlane: true, supportsCell: false),

            new SplitTargetDef(BuiltInCategory.OST_PipeCurves, "Pipes",
                SplitStrategyKind.LinearCurve,
                "Splits in place — connectivity preserved",
                supportsLevel: true, supportsGrid: true, supportsRefPlane: true, supportsCell: false),

            new SplitTargetDef(BuiltInCategory.OST_Conduit, "Conduit",
                SplitStrategyKind.LinearCurve,
                "Splits — curve-based",
                supportsLevel: true, supportsGrid: true, supportsRefPlane: true, supportsCell: false),

            new SplitTargetDef(BuiltInCategory.OST_CableTray, "Cable Trays",
                SplitStrategyKind.LinearCurve,
                "Splits — curve-based",
                supportsLevel: true, supportsGrid: true, supportsRefPlane: true, supportsCell: false),

            new SplitTargetDef(BuiltInCategory.OST_Floors, "Floors",
                SplitStrategyKind.SheetSolid,
                "Splits the floor's footprint — solid-based",
                supportsLevel: true, supportsGrid: true, supportsRefPlane: true, supportsCell: true),

            new SplitTargetDef(BuiltInCategory.OST_Ceilings, "Ceilings",
                SplitStrategyKind.SheetSolid,
                "Splits the ceiling's footprint — solid-based",
                supportsLevel: true, supportsGrid: true, supportsRefPlane: true, supportsCell: true),

            new SplitTargetDef(BuiltInCategory.OST_Roofs, "Roofs",
                SplitStrategyKind.SheetSolid,
                "Footprint roofs only — solid-based",
                supportsLevel: true, supportsGrid: true, supportsRefPlane: true, supportsCell: true),

            new SplitTargetDef(BuiltInCategory.OST_StructuralFoundation, "Structural Foundation Slabs",
                SplitStrategyKind.SheetSolid,
                "Slab-type foundations only — solid-based",
                supportsLevel: true, supportsGrid: true, supportsRefPlane: true, supportsCell: true),

            // A flat FilledRegion sits at one Z with no vertical extent, so a Level cutter (a
            // horizontal plane) can never produce a meaningful split — hide it rather than
            // offering an option that would always skip-log.
            new SplitTargetDef(BuiltInCategory.OST_FilledRegion, "Filled Regions",
                SplitStrategyKind.SheetSolid,
                "Splits the region's boundary — sketch-based",
                supportsLevel: false, supportsGrid: true, supportsRefPlane: true, supportsCell: true),
        };

        internal static readonly IReadOnlyDictionary<BuiltInCategory, SplitTargetDef> ByCategory =
            All.ToDictionary(t => t.Category);

        /// <summary>Targets a given cutter is allowed to offer in its category picker.</summary>
        internal static IReadOnlyList<SplitTargetDef> For(SplitCutterKind cutter)
        {
            switch (cutter)
            {
                case SplitCutterKind.Level:         return All.Where(t => t.SupportsLevel).ToList();
                case SplitCutterKind.Grid:           return All.Where(t => t.SupportsGrid).ToList();
                case SplitCutterKind.ReferencePlane: return All.Where(t => t.SupportsRefPlane).ToList();
                case SplitCutterKind.Cell:           return All.Where(t => t.SupportsCell).ToList();
                default:                             return new List<SplitTargetDef>();
            }
        }

        private static bool TryGetBic(Category? cat, out BuiltInCategory bic)
        {
            bic = BuiltInCategory.INVALID;
            if (cat == null) return false;
            try { bic = (BuiltInCategory)(int)cat.Id.Value; return true; }
            catch (Exception ex) { LemoineLog.Swallowed("SplitTargets: resolve BuiltInCategory from Category.Id", ex); return false; }
        }

        /// <summary>
        /// True if <paramref name="el"/> is one of the categories in <paramref name="targets"/>.
        /// Structural Foundation is further restricted to Floor-instance slabs — isolated
        /// footings and wall foundations are FamilyInstances that the recreation engine cannot
        /// rebuild, so they must never be offered (they would otherwise throw
        /// NotSupportedException at run time instead of being cleanly excluded).
        /// </summary>
        internal static bool IsSupported(Element? el, IReadOnlyList<SplitTargetDef> targets)
        {
            if (el == null || !TryGetBic(el.Category, out var bic)) return false;
            if (!targets.Any(t => t.Category == bic)) return false;
            if (bic == BuiltInCategory.OST_StructuralFoundation && !(el is Floor)) return false;
            return true;
        }

        /// <summary>
        /// Resolves an element's split strategy from its live category, or null if the
        /// category has no registered strategy or fails the Structural Foundation
        /// Floor-instance check (see <see cref="IsSupported"/>).
        /// </summary>
        internal static SplitStrategyKind? StrategyFor(Element? el)
        {
            if (el == null || !TryGetBic(el.Category, out var bic)) return null;
            if (!ByCategory.TryGetValue(bic, out var def)) return null;
            if (bic == BuiltInCategory.OST_StructuralFoundation && !(el is Floor)) return null;
            return def.Strategy;
        }

        /// <summary>
        /// Collects elements (optionally scoped to a view) whose category is one of
        /// <paramref name="targets"/>, grouped by discipline for the category picker (reusing
        /// <see cref="CategoryDisciplineHelper.GroupByDiscipline"/>'s existing display
        /// grouping), with a total count. The input list is restricted up front to categories
        /// the calling tool can actually act on, so the picker never offers — and a run never
        /// silently skips — a category the engine has no strategy for.
        /// </summary>
        internal static (Dictionary<string, List<string>> Groups, int Total, List<Element> Elements)
            CollectForPicker(Document doc, View? view, IReadOnlyList<SplitTargetDef> targets)
        {
            var coll = (view != null && !view.IsTemplate)
                ? new FilteredElementCollector(doc, view.Id)
                : new FilteredElementCollector(doc);

            var elements = coll.WhereElementIsNotElementType()
                .Where(e => IsSupported(e, targets))
                .ToList();

            var groups = CategoryDisciplineHelper.GroupByDiscipline(elements);
            return (groups, elements.Count, elements);
        }
    }
}
