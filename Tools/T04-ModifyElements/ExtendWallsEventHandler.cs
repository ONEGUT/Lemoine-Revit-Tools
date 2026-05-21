using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace LemoineTools.Tools.ModifyElements
{
    public class ExtendWallsEventHandler : IExternalEventHandler
    {
        public List<ElementId>            SelectedLevelIds { get; set; } = new List<ElementId>();
        public double                     AssumedCeilingFt { get; set; } = 9.0;
        public bool                       ActiveViewOnly   { get; set; } = true;
        public Action<string, string>?     OnLog            { get; set; }
        public Action<int, int, int, int>? OnProgress      { get; set; }
        public Action<int, int, int>?      OnComplete      { get; set; }

        private static readonly BuiltInParameter PBase    = BuiltInParameter.WALL_BASE_CONSTRAINT;
        private static readonly BuiltInParameter PTop     = BuiltInParameter.WALL_HEIGHT_TYPE;
        private static readonly BuiltInParameter PBaseOff = BuiltInParameter.WALL_BASE_OFFSET;
        private static readonly BuiltInParameter PTopOff  = BuiltInParameter.WALL_TOP_OFFSET;
        private static readonly BuiltInParameter PUncH    = BuiltInParameter.WALL_USER_HEIGHT_PARAM;

        public string GetName() => "ExtendWalls";

        public void Execute(UIApplication app)
        {
            var pushLog    = OnLog      ?? ((m, s) => { });
            var onProgress = OnProgress ?? ((p, a, b, c) => { });
            var onComplete = OnComplete ?? ((a, b, c) => { });

            try
            {
                Document doc  = app.ActiveUIDocument.Document;
                View     view = app.ActiveUIDocument.ActiveView;

                var allLevels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .ToList();

                var selectedLevelSet = new HashSet<ElementId>(SelectedLevelIds);

                var ceilingThresholds = BuildCeilingThresholds(
                    doc,
                    allLevels.Where(l => selectedLevelSet.Contains(l.Id)).ToList(),
                    AssumedCeilingFt);

                IEnumerable<Element> wallSource = ActiveViewOnly && view != null && !view.IsTemplate
                    ? new FilteredElementCollector(doc, view.Id)
                        .OfClass(typeof(Wall))
                        .WhereElementIsNotElementType()
                        .ToElements()
                    : new FilteredElementCollector(doc)
                        .OfClass(typeof(Wall))
                        .WhereElementIsNotElementType()
                        .ToElements();

                var candidates       = new List<(Wall Wall, Level NextLevel)>();
                int alreadyCorrect   = 0;
                int skipped          = 0;

                foreach (Element el in wallSource)
                {
                    if (!(el is Wall wall)) continue;

                    if (wall.WallType?.Kind == WallKind.Curtain) { skipped++; continue; }

                    ElementId? baseLvlId = wall.get_Parameter(PBase)?.AsElementId();
                    if (baseLvlId == null || baseLvlId == ElementId.InvalidElementId)
                    { skipped++; continue; }

                    if (!selectedLevelSet.Contains(baseLvlId)) continue;

                    Level? baseLevel = doc.GetElement(baseLvlId) as Level;
                    if (baseLevel == null) continue;

                    Level nextLevel = NextLevelAbove(allLevels, baseLevel);
                    if (nextLevel == null) { skipped++; continue; }

                    double topElev    = ComputeTopElevation(doc, wall, baseLevel);
                    double ceilingH   = ceilingThresholds.TryGetValue(baseLvlId, out double ch)
                                        ? ch : AssumedCeilingFt;
                    double ceilingElev = baseLevel.Elevation + ceilingH;

                    ElementId? topLvlId  = wall.get_Parameter(PTop)?.AsElementId();
                    double    existingOff = wall.get_Parameter(PTopOff)?.AsDouble() ?? 0.0;

                    if (topLvlId != null && topLvlId == nextLevel.Id && Math.Abs(existingOff) < 0.001)
                    { alreadyCorrect++; continue; }

                    if (topElev <= ceilingElev - 0.01) { skipped++; continue; }

                    candidates.Add((wall, nextLevel));
                }

                if (!candidates.Any())
                {
                    pushLog($"No qualifying walls found. Already correct: {alreadyCorrect}. Skipped: {skipped}.", "info");
                    onComplete(0, 0, skipped + alreadyCorrect);
                    return;
                }

                pushLog($"Found {candidates.Count} wall(s) to extend. Already correct: {alreadyCorrect}.", "info");

                int changed = 0;
                int failed  = 0;

                using (var tx = new Transaction(doc, "Extend Walls to Next Level"))
                {
                    var fho = tx.GetFailureHandlingOptions();
                    fho.SetClearAfterRollback(true);
                    tx.SetFailureHandlingOptions(fho);
                    tx.Start();

                    foreach (var (wall, nextLevel) in candidates)
                    {
                        try
                        {
                            Parameter pTop = wall.get_Parameter(PTop);
                            Parameter pOff = wall.get_Parameter(PTopOff);

                            if (pTop == null || pTop.IsReadOnly)
                                throw new InvalidOperationException("Top Constraint is read-only.");

                            pTop.Set(nextLevel.Id);
                            pOff?.Set(0.0);

                            changed++;
                            pushLog($"✓ Wall {wall.Id} [{wall.WallType?.Name}] → {nextLevel.Name}", "pass");
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            pushLog($"✗ Wall {wall.Id}: {ex.Message}", "fail");
                        }
                    }

                    tx.Commit();
                }

                onProgress(100, changed, failed, skipped + alreadyCorrect);
                onComplete(changed, failed, skipped + alreadyCorrect);
            }
            catch (Exception ex)
            {
                pushLog($"Error: {ex.Message}", "fail");
                onComplete(0, 1, 0);
            }
        }

        // ── Ceiling threshold lookup ──────────────────────────────────────────

        private static Dictionary<ElementId, double> BuildCeilingThresholds(
            Document doc, List<Level> levels, double fallbackFt)
        {
            var result = new Dictionary<ElementId, double>();

            var allCeilings = new FilteredElementCollector(doc)
                .OfClass(typeof(Ceiling))
                .WhereElementIsNotElementType()
                .ToList();

            foreach (Level level in levels)
            {
                double lowest = double.MaxValue;

                foreach (Element c in allCeilings)
                {
                    ElementId? cLevelId = c.get_Parameter(BuiltInParameter.LEVEL_PARAM)?.AsElementId();
                    if (cLevelId == null || cLevelId != level.Id) continue;

                    double h = c.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)
                                ?.AsDouble() ?? double.MaxValue;

                    if (h > 0.01 && h < lowest) lowest = h;
                }

                result[level.Id] = lowest < double.MaxValue ? lowest : fallbackFt;
            }

            return result;
        }

        // ── Wall top elevation ────────────────────────────────────────────────

        private static double ComputeTopElevation(Document doc, Wall wall, Level baseLevel)
        {
            double baseOff = wall.get_Parameter(PBaseOff)?.AsDouble() ?? 0.0;

            ElementId? topLvlId = wall.get_Parameter(PTop)?.AsElementId();
            if (topLvlId != null && topLvlId != ElementId.InvalidElementId)
            {
                Level? topLvl = doc.GetElement(topLvlId) as Level;
                if (topLvl != null)
                {
                    double topOff = wall.get_Parameter(PTopOff)?.AsDouble() ?? 0.0;
                    return topLvl.Elevation + topOff;
                }
            }

            double uncH = wall.get_Parameter(PUncH)?.AsDouble() ?? 0.0;
            return baseLevel.Elevation + baseOff + uncH;
        }

        private static Level NextLevelAbove(List<Level> ordered, Level baseLevel)
            => ordered.FirstOrDefault(l => l.Elevation > baseLevel.Elevation + 0.001);
    }
}
