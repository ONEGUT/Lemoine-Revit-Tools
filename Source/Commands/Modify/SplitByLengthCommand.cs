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
    public class SplitByLengthCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(
            ExternalCommandData commandData,
            ref string          message,
            ElementSet          elements)
        {
            // Commands run on the Revit main thread WITH the document; the tool window runs on its
            // own STA thread and cannot resolve it later (CLAUDE.md).
            DocumentKey.SetCurrent(commandData.Application.ActiveUIDocument?.Document);

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
                catch (System.Exception ex)
                {
                    // The remembered window's dispatcher is gone (its STA thread already shut
                    // down), so drop the singleton and open a fresh one. Routed through
                    // DiagnosticsLog rather than swallowed outright — CLAUDE.md forbids the bare
                    // catch the sibling split commands still use here.
                    DiagnosticsLog.Swallowed("SplitByLength: reactivate existing window", ex);
                    _window = null;
                }
            }

            var uiApp = commandData.Application;
            SplitByLengthViewModel BuildTool()
            {
                var uidoc        = uiApp.ActiveUIDocument;
                var doc          = uidoc.Document;
                var activeViewId = uidoc.ActiveView?.Id;

                // Only the categories this tool can cut are counted or offered.
                var supported = SplitElementsShared.LengthSplitCategories.Select(c => c.Cat).ToList();
                var supportedNames = new HashSet<string>(
                    SplitElementsShared.LengthSplitCategories.Select(c => c.Label),
                    System.StringComparer.OrdinalIgnoreCase);

                int totalElements = new FilteredElementCollector(doc)
                    .WherePasses(new ElementMulticategoryFilter(supported))
                    .WhereElementIsNotElementType()
                    .GetElementCount();

                // Pre-selection: keep only the ducts and pipes. Anything else the user had
                // highlighted is counted so step 1 can say it was left out, rather than dropping
                // it silently.
                var rawSelection = uidoc.Selection.GetElementIds()
                    .Select(id => doc.GetElement(id))
                    .Where(e => e?.Category?.Name != null)
                    .ToList();

                var usable = rawSelection
                    .Where(e => supportedNames.Contains(e.Category!.Name))
                    .ToList();

                var preSelectedIds  = usable.Select(e => e.Id).ToList();
                var preSelectedCats = usable.Select(e => e.Category!.Name).Distinct().ToList();
                int ignored         = rawSelection.Count - usable.Count;

                return new SplitByLengthViewModel(
                    App.SplitByLengthHandler!, App.SplitByLengthEvent!,
                    totalElements, activeViewId,
                    preSelectedIds, preSelectedCats, ignored);
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
