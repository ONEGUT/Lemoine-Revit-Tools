using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Helpers;
using LemoineTools.Tools.ModifyElements;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class SplitByLengthCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        /// <summary>
        /// View types that draw model geometry, and so can carry elements to split. Schedules,
        /// legends, drafting views, sheets and the report types are excluded — nothing in them is
        /// a model element with a location line.
        /// </summary>
        private static readonly HashSet<ViewType> ModelViewTypes = new HashSet<ViewType>
        {
            ViewType.FloorPlan, ViewType.EngineeringPlan, ViewType.AreaPlan, ViewType.CeilingPlan,
            ViewType.Elevation,  ViewType.Section,        ViewType.Detail,   ViewType.ThreeD,
        };

        public Result Execute(
            ExternalCommandData commandData,
            ref string          message,
            ElementSet          elements)
        {
            // Commands run on the Revit main thread WITH the document; the tool window runs on its
            // own STA thread and cannot resolve it later (CLAUDE.md).
            DocumentKey.SetCurrent(commandData.Application.ActiveUIDocument?.Document);

            if (_window != null)
            {
                try
                {
                    _window.Dispatcher.Invoke(() =>
                    {
                        if (_window.IsVisible) _window.Activate();
                        else _window = null;
                    });
                    if (_window != null) return Result.Succeeded;
                }
                catch (System.Exception ex)
                {
                    // The remembered window's dispatcher is gone (its STA thread already shut
                    // down), so drop the singleton and open a fresh one. Routed through
                    // DiagnosticsLog rather than swallowed outright — CLAUDE.md forbids the bare
                    // catch the sibling split commands still use here.
                    DiagnosticsLog.Swallowed("SplitByLength: reactivate existing window", ex);
                    _window = null;
                }
            }

            var uiApp = commandData.Application;
            SplitByLengthViewModel BuildTool()
            {
                var uidoc      = uiApp.ActiveUIDocument;
                var doc        = uidoc.Document;
                var activeView = uidoc.ActiveView;

                // The category picker is scanned from the document rather than curated: any
                // line-based element — MEP curve, beam, wall, or a line-based family instance —
                // can be cut to length, and which of those a project loads is not knowable in
                // advance. Only categories that actually have splittable content are offered
                // (CLAUDE.md UX Philosophy: never show-then-skip).
                var lineBased = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .Where(e => e.Category?.Name != null
                             && e.Category.CategoryType == CategoryType.Model
                             && SplitElementsShared.HasStraightCurve(e))
                    .ToList();

                var categoryGroups = CategoryDisciplineHelper.GroupByDiscipline(lineBased);
                int totalElements  = lineBased.Count;

                // Pre-selection: keep what actually has a straight curve.
                var selectedIds = uidoc.Selection.GetElementIds();
                var usable = selectedIds
                    .Select(id => doc.GetElement(id))
                    .Where(e => e?.Category?.Name != null && SplitElementsShared.HasStraightCurve(e))
                    .ToList();

                var preSelectedIds  = usable.Select(e => e!.Id).ToList();
                var preSelectedCats = usable.Select(e => e!.Category!.Name).Distinct().ToList();

                // Counted against the RAW selection, not against what resolved: an id that no
                // longer resolves is just as "left out" as an arc pipe, and step 1 must say so
                // either way rather than quietly shrinking the user's selection.
                int ignored = selectedIds.Count - usable.Count;

                // Views the scope picker may offer, plus the Project Browser tree it mirrors.
                // Both must be read here: this factory runs on the Revit main thread (directly on
                // launch, and via App.ReloadEvent on reload), while the tool window lives on its
                // own STA thread and could not query the document itself.
                // Only views that actually show model geometry. CanBePrinted alone would let
                // schedules and legends through, and neither holds anything this tool can cut.
                var eligibleViewIds = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate && ModelViewTypes.Contains(v.ViewType))
                    .Select(v => v.Id.Value)
                    .ToList();

                return new SplitByLengthViewModel(
                    App.SplitByLengthHandler!, App.SplitByLengthEvent!,
                    categoryGroups, totalElements,
                    activeView?.Id, activeView?.Name ?? "",
                    BrowserTreeCapture.Capture(doc), eligibleViewIds,
                    preSelectedIds, preSelectedCats, ignored);
            }

            var vm = BuildTool();
            var ready = new ManualResetEventSlim(false);
            StepFlowWindow? win = null;

            var thread = new System.Threading.Thread(() =>
            {
                win = new StepFlowWindow(vm, BuildTool);
                win.Closed += (s, e) =>
                {
                    _window = null;
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                };
                win.Show();
                ready.Set();
                Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            ready.Wait();
            _window = win;
            return Result.Succeeded;
        }
    }
}
