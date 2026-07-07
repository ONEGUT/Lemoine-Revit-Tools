using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Helpers;
using LemoineTools.Framework;
using LemoineTools.Tools.Dimensioning;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens the Clash Finder &amp; Elevation Tag wizard. Gathers section + elevation views and the
    /// project's spot elevation types on Revit's main thread, loads the saved clash definitions, then
    /// launches the StepFlow window on a dedicated STA thread.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ClashElevationFinderCommand : IExternalCommand
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
            ClashElevationFinderViewModel BuildTool()
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Section + elevation views only.
                var allViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate
                             && (v.ViewType == ViewType.Section
                              || v.ViewType == ViewType.Elevation))
                    .OrderBy(v => v.Name)
                    .ToList();

                // Spot elevation types available to tag with. Filter via the collector's category
                // filter (reading SpotDimensionType.Category directly can return null for annotation
                // types, which silently dropped every type); fall back to every spot dimension type so
                // the picker is never empty when the project does have types.
                var spotTypes = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_SpotElevations)
                    .OfClass(typeof(SpotDimensionType)).Cast<SpotDimensionType>()
                    .OrderBy(t => t.Name)
                    .Select(t => (Name: t.Name, Id: t.Id))
                    .ToList();

                if (spotTypes.Count == 0)
                {
                    spotTypes = new FilteredElementCollector(doc)
                        .OfClass(typeof(SpotDimensionType)).Cast<SpotDimensionType>()
                        .OrderBy(t => t.Name)
                        .Select(t => (Name: t.Name, Id: t.Id))
                        .ToList();
                }

                var definitions = ClashDefinitionsSettings.Instance.Definitions
                    ?? new List<ClashDefinition>();

                var vm = new ClashElevationFinderViewModel(
                    App.ClashElevationFinderHandler, App.ClashElevationFinderEvent,
                    allViews, definitions, spotTypes,
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
