using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Helpers;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;
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
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed("ClashElevationFinderCommand: reactivate existing window", ex);
                    _window = null;
                }
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
                // types, which silently dropped every type). No uncategorized fallback: collecting
                // every SpotDimensionType also listed spot COORDINATE / SLOPE types, and picking one
                // of those either throws at ChangeTypeId or mis-tags — the tool's "no spot elevation
                // type found" warning is the honest path when the project truly has none.
                var spotTypes = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_SpotElevations)
                    .OfClass(typeof(SpotDimensionType)).Cast<SpotDimensionType>()
                    .OrderBy(t => t.Name)
                    .Select(t => (Name: t.Name, Id: t.Id))
                    .ToList();

                var definitions = ClashDefinitionsSettings.Instance.Definitions
                    ?? new List<ClashDefinition>();

                var vm = new ClashElevationFinderViewModel(
                    App.ClashElevationFinderHandler, App.ClashElevationFinderEvent,
                    allViews, definitions, spotTypes,
                    BrowserTreeCapture.Capture(doc));

                return vm;
            }
            if (WebToolLauncher.Enabled)
            {
                WebToolLauncher.Open("clashElevationFinder", () =>
                {
                    var doc = uiApp.ActiveUIDocument.Document;

                    var allViews = new FilteredElementCollector(doc)
                        .OfClass(typeof(View)).Cast<View>()
                        .Where(v => !v.IsTemplate
                                 && (v.ViewType == ViewType.Section
                                  || v.ViewType == ViewType.Elevation))
                        .OrderBy(v => v.Name)
                        .ToList();

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

                    return new ClashElevationFinderWebTool(
                        App.ClashElevationFinderHandler, App.ClashElevationFinderEvent,
                        allViews, definitions, spotTypes,
                        BrowserTreeCapture.Capture(doc));
                });
                return Result.Succeeded;
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
