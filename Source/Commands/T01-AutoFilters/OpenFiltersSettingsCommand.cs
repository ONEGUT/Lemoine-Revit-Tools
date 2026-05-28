using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens the standalone Filters / Color Settings window.
    /// Queries fill and line patterns on the Revit main thread, then opens
    /// the window on a dedicated STA thread.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenFiltersSettingsCommand : IExternalCommand
    {
        private static FiltersSettingsWindow? _window;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
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

            // Query pattern lists on the Revit main thread
            var fillNames = new List<string>();
            var lineNames = new List<string> { "Solid" };

            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc != null)
            {
                fillNames.AddRange(
                    new FilteredElementCollector(doc)
                        .OfClass(typeof(FillPatternElement))
                        .Cast<FillPatternElement>()
                        .Select(fp => fp.Name)
                        .OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase));

                lineNames.AddRange(
                    new FilteredElementCollector(doc)
                        .OfClass(typeof(LinePatternElement))
                        .Cast<LinePatternElement>()
                        .Select(lp => lp.Name)
                        .OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase));
            }

            // Open window on dedicated STA thread
            var ready = new ManualResetEventSlim(false);
            FiltersSettingsWindow? win = null;

            var thread = new Thread(() =>
            {
                win = new FiltersSettingsWindow();
                win.SetPatternLists(fillNames, lineNames);
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
