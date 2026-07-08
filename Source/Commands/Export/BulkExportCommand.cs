using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Helpers;
using LemoineTools.Framework;
using LemoineTools.Tools.BulkExport;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class BulkExportCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(
            ExternalCommandData commandData,
            ref string          message,
            ElementSet          elements)
        {
            // Reuse existing open window
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
            BulkExportViewModel BuildTool()
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Collect all sheets
                var allSheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .Where(s => !s.IsTemplate)
                    .OrderBy(s => s.SheetNumber)
                    .ToList();

                // Collect non-template views including 3D views (required for NWC/IFC export)
                var allViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate
                             && v.ViewType != ViewType.Schedule
                             && v.ViewType != ViewType.Legend)
                    .OrderBy(v => v.Name)
                    .ToList();

                // Collect DWG export setup names
                var dwgSetupNames = new FilteredElementCollector(doc)
                    .OfClass(typeof(ExportDWGSettings))
                    .Cast<ExportDWGSettings>()
                    .Select(s => s.Name)
                    .OrderBy(n => n)
                    .ToList();

                // Snapshot of the Project Browser organization so the picker mirrors it exactly.
                var browserTree = BrowserTreeCapture.Capture(doc);

                // Existing print sets (ViewSheetSet elements) for the Print Sets step.
                var printSets = LemoineTools.Tools.BulkExport.BulkExportPrintSetHandler.Collect(doc);

                var vm    = new BulkExportViewModel(
                    App.BulkExportHandler!, App.BulkExportEvent!,
                    dwgSetupNames, allSheets, allViews, browserTree, printSets);

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
