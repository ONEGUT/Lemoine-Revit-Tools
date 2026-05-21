using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Testing.CoordSet;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CoordSetCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(ExternalCommandData commandData,
                              ref string message, ElementSet elements)
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

            // Collect title blocks available in the project
            var titleBlocks = new Dictionary<string, ElementId>();
            foreach (var sym in new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .Cast<FamilySymbol>())
            {
                string label = $"{sym.Family.Name} : {sym.Name}";
                if (!titleBlocks.ContainsKey(label))
                    titleBlocks[label] = sym.Id;
            }

            var vm = new CoordSetViewModel(
                App.LinkViewsLevelPhase1Handler!, App.LinkViewsLevelPhase1Event!,
                App.CoordSetRunHandler!,          App.CoordSetRunEvent!,
                titleBlocks,
                includeHost: true);

            var ready = new ManualResetEventSlim(false);
            StepFlowWindow? win = null;

            var thread = new Thread(() =>
            {
                win = new StepFlowWindow(vm);

                // Show resume banner if a prior partial run exists
                var s = CoordSetSettings.Instance;
                if (s.LastCompletedPhase != CoordSetRunPhase.None &&
                    s.LastCompletedPhase != CoordSetRunPhase.G)
                {
                    win.PushLog(
                        $"Partial run detected (last completed: Phase {s.LastCompletedPhase}). " +
                        "Run will resume from the next phase. Click Reset to start over.", "info");
                }

                win.Closed += (sender, e) =>
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
