using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

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

                    // Already tall enough — the top is at or above the next level (e.g. topped to the
                    // next level with a positive offset). "Extending" it would reset that offset to 0
                    // and SHORTEN the wall, so leave deliberately taller walls untouched.
                    if (topElev >= nextLevel.Elevation - 0.001)
                    { alreadyCorrect++; continue; }

                    if (topElev <= ceilingElev - 0.01) { skipped++; continue; }

                    candidates.Add((wall, nextLevel));
                }

                if (!candidates.Any())
                {
                    pushLog(LemoineStrings.T("modify.extendWalls.log.noQualifying", alreadyCorrect, skipped), "info");
                    onComplete(0, 0, skipped + alreadyCorrect);
                    return;
                }

                pushLog(LemoineStrings.T("modify.extendWalls.log.found", candidates.Count, alreadyCorrect), "info");

                int changed = 0;
                int failed  = 0;

                var progress = new RunProgressReporter(pushLog, candidates.Count, "walls");

                using (var tx = new Transaction(doc, "Extend Walls to Next Level"))
                {
                    var fho = tx.GetFailureHandlingOptions();
                    fho.SetClearAfterRollback(true);
                    tx.SetFailureHandlingOptions(fho);
                    tx.Start();

                    foreach (var (wall, nextLevel) in candidates)
                    {
                        // Abandon mid-run: stop processing more walls but let tx.Commit() (below)
                        // still run so every wall already extended this run is preserved.
                        if (LemoineRun.CancelRequested)
                        {
                            pushLog(LemoineStrings.T("common.log.stoppedByUser", progress.Done, candidates.Count), "warn");
                            break;
                        }

                        try
                        {
                            Parameter pTop = wall.get_Parameter(PTop);
                            Parameter pOff = wall.get_Parameter(PTopOff);

                            if (pTop == null || pTop.IsReadOnly)
                                throw new InvalidOperationException("Top Constraint is read-only.");

                            if (!pTop.Set(nextLevel.Id))
                                throw new InvalidOperationException(
                                    $"Top Constraint would not accept level '{nextLevel.Name}'.");
                            pOff?.Set(0.0);

                            changed++;
                            pushLog(LemoineStrings.T("modify.extendWalls.log.wallOk", wall.Id, wall.WallType?.Name, nextLevel.Name), "pass");
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            pushLog(LemoineStrings.T("modify.extendWalls.log.wallFail", wall.Id, ex.Message), "fail");
                        }

                        progress.Tick();
                        onProgress(progress.Percent, changed, failed, skipped + alreadyCorrect);
                    }

                    tx.Commit();
                }

                pushLog(LemoineStrings.T("modify.extendWalls.log.done", changed, failed, skipped + alreadyCorrect),
                        failed > 0 ? "fail" : "pass");
                onProgress(100, changed, failed, skipped + alreadyCorrect);
                onComplete(changed, failed, skipped + alreadyCorrect);
            }
            catch (Exception ex)
            {
                pushLog(LemoineStrings.T("modify.extendWalls.log.error", ex.Message), "fail");
                onComplete(0, 1, 0);
            }
            finally
            {
                // Session-long static handler — drop the run's payload.
                SelectedLevelIds = new List<ElementId>();
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
