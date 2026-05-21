using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.AutoFilters;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Thin launcher for the Auto Filters step-flow window.
    /// Captures loaded link titles and configured disciplines on the main thread,
    /// then opens the UI on a dedicated STA thread.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AutoFiltersLaunchCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(
            ExternalCommandData commandData,
            ref string          message,
            ElementSet          elements)
        {
            // FIX: bring existing window to front via Dispatcher (STA-safe)
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

            // Capture loaded link titles on the main thread
            var linkTitles = new System.Collections.Generic.List<string>();
            foreach (RevitLinkInstance li in
                new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var ld = li.GetLinkDocument();
                if (ld == null) continue;
                string title = ld.Title ?? li.Name;
                if (!linkTitles.Contains(title))
                    linkTitles.Add(title);
            }

            // Discipline list uses Label (full trade name) for display and selection.
            // The event handler matches SelectedDisciplines against t.Label, not t.Id.
            var disciplines = AutoFiltersSettings.Instance.Trades
                .Select(t => t.Label)
                .ToList();

            var vm = new AutoFiltersViewModel(
                App.AutoFiltersHandler!,
                App.AutoFiltersEvent!,
                linkTitles,
                disciplines);

            // FIX: open window on dedicated STA thread so real-time progress works
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
