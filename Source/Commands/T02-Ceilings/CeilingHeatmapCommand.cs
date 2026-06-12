using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Helpers;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Ceilings;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CeilingHeatmapCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(
            ExternalCommandData commandData,
            ref string          message,
            ElementSet          elements)
        {
            // Bring existing window to front if already open
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

            Document doc = commandData.Application.ActiveUIDocument.Document;

            // ── Collect ceiling plan views, grouped by level (main thread — safe) ──
            var viewsByLevel = new Dictionary<string, List<(string Name, ElementId Id)>>();

            var rcpViews = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .Where(v =>
                {
                    if (v.IsTemplate) return false;
                    var vft = doc.GetElement(v.GetTypeId()) as ViewFamilyType;
                    return vft?.ViewFamily == ViewFamily.CeilingPlan;
                })
                .OrderBy(v => v.Name)
                .ToList();

            foreach (ViewPlan vp in rcpViews)
            {
                string levelName = vp.GenLevel?.Name ?? "No Level";
                if (!viewsByLevel.ContainsKey(levelName))
                    viewsByLevel[levelName] = new List<(string, ElementId)>();
                viewsByLevel[levelName].Add((vp.Name, vp.Id));
            }

            // Sort level keys naturally so tabs appear floor-by-floor
            var sortedByLevel = viewsByLevel
                .OrderBy(kvp => kvp.Key)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            // ── Spin up dedicated STA thread for the WPF window ──────────────────
            var vm    = new CeilingHeatmapViewModel(App.CeilingHeatmapHandler!, App.CeilingHeatmapEvent!, sortedByLevel,
                                                    BrowserTreeCapture.Capture(doc));
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
