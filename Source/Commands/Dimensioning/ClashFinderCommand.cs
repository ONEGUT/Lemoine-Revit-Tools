using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Helpers;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Dimensioning;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens the Clash Finder &amp; Dimension wizard. Gathers plan views on Revit's main
    /// thread, loads the saved clash definitions, then launches the StepFlow window on a
    /// dedicated STA thread.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ClashFinderCommand : IExternalCommand
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

            var uiApp = commandData.Application;
            ClashFinderViewModel BuildTool()
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Plan views only (floor + reflected ceiling).
                var allViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate
                             && (v.ViewType == ViewType.FloorPlan
                              || v.ViewType == ViewType.CeilingPlan))
                    .OrderBy(v => v.Name)
                    .ToList();

                var definitions = ClashDefinitionsSettings.Instance.Definitions
                    ?? new List<ClashDefinition>();

                var vm = new ClashFinderViewModel(
                    App.ClashFinderHandler, App.ClashFinderEvent, allViews, definitions,
                    App.SlabPickHandler, App.SlabPickEvent,
                    BrowserTreeCapture.Capture(doc));

                return vm;
            }
            var vm = BuildTool();
            var ready = new ManualResetEventSlim(false);
            StepFlowWindow? win = null;

            var thread = new Thread(() =>
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
