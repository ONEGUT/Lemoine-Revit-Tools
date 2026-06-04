using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Testing;

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

            var doc = commandData.Application.ActiveUIDocument.Document;

            // Section + elevation views only.
            var allViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate
                         && (v.ViewType == ViewType.Section
                          || v.ViewType == ViewType.Elevation))
                .OrderBy(v => v.Name)
                .ToList();

            // Spot elevation types available to tag with.
            var spotElevCatId = new ElementId(BuiltInCategory.OST_SpotElevations);
            var spotTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(SpotDimensionType)).Cast<SpotDimensionType>()
                .Where(t => t.Category != null && t.Category.Id == spotElevCatId)
                .OrderBy(t => t.Name)
                .Select(t => (Name: t.Name, Id: t.Id))
                .ToList();

            var definitions = ClashDefinitionsSettings.Instance.Definitions
                ?? new List<ClashDefinition>();

            var vm = new ClashElevationFinderViewModel(
                App.ClashElevationFinderHandler, App.ClashElevationFinderEvent,
                allViews, definitions, spotTypes);

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
