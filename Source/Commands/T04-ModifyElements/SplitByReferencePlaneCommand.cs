using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.ModifyElements;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class SplitByReferencePlaneCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(
            ExternalCommandData commandData,
            ref string          message,
            ElementSet          elements)
        {
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
                catch { _window = null; }
            }

            var uiApp = commandData.Application;
            SplitByReferencePlaneViewModel BuildTool()
            {
                var uidoc        = uiApp.ActiveUIDocument;
                var doc          = uidoc.Document;
                var activeViewId = uidoc.ActiveView?.Id;

                // Only categories the Reference Plane cutter can actually act on →
                // discipline-grouped picker. SplitTargets is the single source of truth so the
                // picker never offers — and a run never silently skips — a category the engine
                // has no strategy for.
                var refPlaneTargets = SplitTargets.For(SplitCutterKind.ReferencePlane);
                var (categoryGroups, totalElements, _) = SplitTargets.CollectForPicker(doc, null, refPlaneTargets);

                // Pre-selection: any selected element whose category is a supported target.
                var rawSelection = uidoc.Selection.GetElementIds()
                    .Select(id => doc.GetElement(id))
                    .Where(e => SplitTargets.IsSupported(e, refPlaneTargets))
                    .ToList();
                var preSelectedIds  = rawSelection.Select(e => e.Id).ToList();
                var preSelectedCats = rawSelection
                    .Select(e => e.Category!.Name)
                    .Distinct().ToList();

                var allRefPlanes = new FilteredElementCollector(doc)
                    .OfClass(typeof(ReferencePlane))
                    .Cast<ReferencePlane>()
                    .ToList();

                var vm = new SplitByReferencePlaneViewModel(
                    App.SplitByReferencePlaneHandler!, App.SplitByReferencePlaneEvent!,
                    allRefPlanes, categoryGroups, totalElements, activeViewId,
                    preSelectedIds, preSelectedCats);

                return vm;
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
