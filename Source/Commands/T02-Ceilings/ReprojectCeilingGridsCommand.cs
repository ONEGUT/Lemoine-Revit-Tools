using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Ceilings;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ReprojectCeilingGridsCommand : IExternalCommand
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

            // Pre-selection must be captured here on the Revit main thread
            var uiDoc = commandData.Application.ActiveUIDocument;
            var doc   = uiDoc.Document;

            var preSelectedIds = uiDoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .OfType<ModelCurve>()
                .Select(mc => mc.Id)
                .ToList();

            if (App.ReprojectHandler != null)
                App.ReprojectHandler.PreSelectedIds = preSelectedIds;

            var vm = new ReprojectCeilingGridsViewModel(
                App.ReprojectHandler!,
                App.ReprojectEvent!,
                preSelectedIds.Count);

            var ready          = new ManualResetEventSlim(false);
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
