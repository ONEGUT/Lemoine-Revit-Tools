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
    /// Opens the standalone Legend Creator Settings window on a dedicated STA thread.
    /// Queries TextNoteTypes and existing Legend views on the Revit main thread before
    /// opening the window so they are available in the text-style pickers.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenLegendSettingsCommand : IExternalCommand
    {
        private static LegendSettingsWindow? _window;

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

            var doc = commandData.Application.ActiveUIDocument?.Document;

            // Query TextNoteTypes on Revit main thread
            var textTypes = doc == null
                ? new List<(ElementId, string)>()
                : new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType))
                    .Cast<TextNoteType>()
                    .OrderBy(t => t.Name, System.StringComparer.OrdinalIgnoreCase)
                    .Select(t => (t.Id, t.Name))
                    .ToList();

            // Query existing Legend views on Revit main thread
            var legendViews = doc == null
                ? new List<(ElementId, string)>()
                : new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Views)
                    .Cast<View>()
                    .Where(v => v.ViewType == ViewType.Legend)
                    .OrderBy(v => v.Name, System.StringComparer.OrdinalIgnoreCase)
                    .Select(v => (v.Id, v.Name))
                    .ToList();

            var ready = new ManualResetEventSlim(false);
            LegendSettingsWindow? win = null;

            var thread = new System.Threading.Thread(() =>
            {
                win = new LegendSettingsWindow(textTypes, legendViews);
                win.Closed += (s, e) =>
                {
                    _window = null;
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                };
                win.Show();
                ready.Set();
                Dispatcher.Run();
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            ready.Wait();
            _window = win;
            return Result.Succeeded;
        }
    }
}
