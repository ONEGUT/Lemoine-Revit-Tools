using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Testing.AutoDimension;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens the Auto Dimension wizard. Gathers plan views on Revit's main thread, then launches
    /// the StepFlow window on a dedicated STA thread (mirrors ClashFinderCommand).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AutoDimensionCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
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

            var doc = commandData.Application.ActiveUIDocument.Document;

            var allViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate
                         && (v.ViewType == ViewType.FloorPlan
                          || v.ViewType == ViewType.CeilingPlan))
                .OrderBy(v => v.Name)
                .ToList();

            var vm = new AutoDimensionViewModel(
                App.AutoDimensionHandler, App.AutoDimensionEvent, allViews);

            var ready = new ManualResetEventSlim(false);
            StepFlowWindow? win = null;

            var thread = new Thread(() =>
            {
                win = new StepFlowWindow(vm);
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
