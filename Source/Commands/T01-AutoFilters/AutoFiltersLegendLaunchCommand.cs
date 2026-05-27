using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Testing.LegendCreator;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens the Legend Creation step-flow. Queries existing Legend views from
    /// the active document on the Revit main thread so the user can pick which
    /// one to duplicate, then fires App.LegendCreatorEvent on confirmation.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AutoFiltersLegendLaunchCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(
            ExternalCommandData commandData,
            ref string          message,
            ElementSet          elements)
        {
            // Bring existing window to front (STA-safe)
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

            // Query legend views on the Revit main thread (here)
            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null) return Result.Failed;

            var legendViews = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Views)
                .Cast<View>()
                .Where(v => v.ViewType == ViewType.Legend)
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .Select(v => (v.Id, v.Name))
                .ToList();

            if (legendViews.Count == 0)
            {
                TaskDialog.Show("Legend Creation",
                    "No Legend views found in this project.\n\n" +
                    "Create a blank Legend view first via View → New Legend, " +
                    "then run this command again.");
                return Result.Cancelled;
            }

            // Query TextNoteTypes so the user can pick one per text role.
            var textNoteTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(t => (t.Id, t.Name))
                .ToList();

            var vm = new LegendCreatorLaunchViewModel(
                legendViews,
                textNoteTypes,
                App.LegendCreatorHandler!,
                App.LegendCreatorEvent!);

            // Open window on dedicated STA thread so real-time progress works
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
