using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.BulkExport;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class PrintViewCommand : IExternalCommand
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

            // Initial guard with a proper Revit message. On reload, BuildTool re-validates and
            // throws if the active view is no longer printable; the reload handler logs that and
            // the window keeps its current tool.
            var activeView0 = uiApp.ActiveUIDocument?.ActiveView;
            if (activeView0 == null || activeView0.IsTemplate)
            {
                message = "No printable active view. Open a floor plan, section, 3D view, or sheet first.";
                return Result.Failed;
            }

            PrintViewViewModel BuildTool()
            {
                var uidoc      = uiApp.ActiveUIDocument;
                var activeView = uidoc?.ActiveView;
                if (activeView == null || activeView.IsTemplate)
                    throw new System.InvalidOperationException("No printable active view to reload.");

                // Build a human-readable display name for the view
                string displayName = activeView is ViewSheet sheet
                    ? $"{sheet.SheetNumber} — {sheet.Name}"
                    : activeView.Name;

                // NWC/IFC export 3D views only — used to decide which formats the tool offers.
                bool isThreeD = activeView is View3D;

                // DWG export setup names (same source as Bulk Export).
                var doc = uidoc!.Document;
                var dwgSetupNames = new FilteredElementCollector(doc)
                    .OfClass(typeof(ExportDWGSettings))
                    .Cast<ExportDWGSettings>()
                    .Select(setting => setting.Name)
                    .OrderBy(n => n)
                    .ToList();

                return new PrintViewViewModel(
                    App.PrintViewHandler!, App.PrintViewEvent!,
                    activeView.Id, displayName, isThreeD, dwgSetupNames);
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
