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
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class BatchExportCommand : IExternalCommand
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

            var doc = commandData.Application.ActiveUIDocument.Document;

            // Collect all sheets
            var allSheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsTemplate)
                .OrderBy(s => s.SheetNumber)
                .ToList();

            // Collect non-template, non-excluded views
            var allViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate
                         && v.ViewType != ViewType.Schedule
                         && v.ViewType != ViewType.Legend
                         && v.ViewType != ViewType.ThreeD
                         && !(v is View3D))
                .OrderBy(v => v.Name)
                .ToList();

            // Collect DWG export setup names
            var dwgSetupNames = new FilteredElementCollector(doc)
                .OfClass(typeof(ExportDWGSettings))
                .Cast<ExportDWGSettings>()
                .Select(s => s.Name)
                .OrderBy(n => n)
                .ToList();

            var vm    = new BatchExportViewModel(
                App.BatchExportHandler!, App.BatchExportEvent!,
                dwgSetupNames, allSheets, allViews);

            var ready = new ManualResetEventSlim(false);
            StepFlowWindow? win = null;

            var thread = new System.Threading.Thread(() =>
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
