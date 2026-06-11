using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Tools.Testing.PdfRegionWand;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class PdfRegionWandCommand : IExternalCommand
    {
        private static PdfRegionWandWindow? _window;

        public Result Execute(
            ExternalCommandData commandData,
            ref string          message,
            ElementSet          elements)
        {
            // Reuse the existing open palette if any.
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
                    LemoineTools.Lemoine.LemoineLog.Swallowed("PdfRegionWand: reuse open palette", ex);
                    _window = null;
                }
            }

            var uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null)
            {
                message = "Open a document first.";
                return Result.Cancelled;
            }
            if (!(uidoc.ActiveView is ViewPlan))
            {
                TaskDialog.Show("PDF Region Wand", "Open a plan view with the placed PDF underlay, then run the tool.");
                return Result.Cancelled;
            }

            var doc = uidoc.Document;

            // Inputs gathered on Revit's main thread before the palette opens.
            var floorTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(FloorType)).Cast<FloorType>()
                .Where(t => !t.IsFoundationSlab)
                .OrderBy(t => t.FamilyName).ThenBy(t => t.Name)
                .Select(t => new NamedId($"{t.FamilyName}: {t.Name}", t.Id))
                .ToList();

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => new NamedId(l.Name, l.Id))
                .ToList();

            if (floorTypes.Count == 0 || levels.Count == 0)
            {
                message = "The document has no floor types or no levels.";
                return Result.Cancelled;
            }

            // Default the level to the active plan's generation level.
            var genLevel = (uidoc.ActiveView as ViewPlan)?.GenLevel;
            if (genLevel != null)
            {
                var hit = levels.FirstOrDefault(l => l.Id == genLevel.Id);
                if (hit != null)
                {
                    levels.Remove(hit);
                    levels.Insert(0, hit);
                }
            }

            var mainWindow = commandData.Application.MainWindowHandle;

            var ready = new ManualResetEventSlim(false);
            PdfRegionWandWindow? win = null;

            var thread = new Thread(() =>
            {
                win = new PdfRegionWandWindow(
                    App.PdfRegionWandHandler!, App.PdfRegionWandEvent!,
                    floorTypes, levels, mainWindow);
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
