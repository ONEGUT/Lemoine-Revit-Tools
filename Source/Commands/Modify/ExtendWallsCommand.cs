using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Tools.ModifyElements;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ExtendWallsCommand : IExternalCommand
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
            ExtendWallsViewModel BuildTool()
            {
                Document doc = uiApp.ActiveUIDocument.Document;

                // Collect levels that have another level above them (processable)
                var allLevels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .ToList();

                var processableLevels = allLevels
                    .Where(l => allLevels.Any(other => other.Elevation > l.Elevation + 0.001))
                    .ToList();

                var vm    = new ExtendWallsViewModel(App.ExtendWallsHandler!, App.ExtendWallsEvent!, processableLevels);
                return vm;
            }
            if (LemoineTools.Framework.Web.WebToolLauncher.Enabled)
            {
                LemoineTools.Framework.Web.WebToolLauncher.Open("extendWalls", () =>
                {
                        Document doc = uiApp.ActiveUIDocument.Document;

                        // Collect levels that have another level above them (processable)
                        var allLevels = new FilteredElementCollector(doc)
                            .OfClass(typeof(Level))
                            .Cast<Level>()
                            .OrderBy(l => l.Elevation)
                            .ToList();

                        var processableLevels = allLevels
                            .Where(l => allLevels.Any(other => other.Elevation > l.Elevation + 0.001))
                            .ToList();

                        var vm    = new LemoineTools.Tools.ModifyElements.ExtendWallsWebTool(App.ExtendWallsHandler!, App.ExtendWallsEvent!, processableLevels);
                        return vm;
                });
                return Result.Succeeded;
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
