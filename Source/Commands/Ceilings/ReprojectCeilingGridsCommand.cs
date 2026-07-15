using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Helpers;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;
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

            var uiApp = commandData.Application;
            ReprojectCeilingGridsViewModel BuildTool()
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Collect all ceiling plan views with their associated level names — must happen on main thread
                var levelMap = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .ToDictionary(l => l.Id, l => l.Name);

                var viewEntries = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewPlan))
                    .Cast<ViewPlan>()
                    .Where(v => v.ViewType == ViewType.CeilingPlan && !v.IsTemplate)
                    .OrderBy(v => v.Name)
                    .Select(v =>
                    {
                        string levelName = "";
                        if (v.GenLevel != null && levelMap.TryGetValue(v.GenLevel.Id, out string? ln))
                            levelName = ln;
                        return new ReprojectCeilingGridsViewModel.CeilingPlanViewEntry(v.Id, v.Name, levelName);
                    })
                    .ToList();

                var vm = new ReprojectCeilingGridsViewModel(
                    App.ReprojectHandler!,
                    App.ReprojectEvent!,
                    viewEntries,
                    BrowserTreeCapture.Capture(doc));

                return vm;
            }
            if (WebToolLauncher.Enabled)
            {
                WebToolLauncher.Open("reprojectCeilingGrids", () =>
                {
                    var doc = uiApp.ActiveUIDocument.Document;
                    var levelMap = new FilteredElementCollector(doc)
                        .OfClass(typeof(Level))
                        .Cast<Level>()
                        .ToDictionary(l => l.Id, l => l.Name);
                    var viewEntries = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewPlan))
                        .Cast<ViewPlan>()
                        .Where(v => v.ViewType == ViewType.CeilingPlan && !v.IsTemplate)
                        .OrderBy(v => v.Name)
                        .Select(v =>
                        {
                            string levelName = "";
                            if (v.GenLevel != null && levelMap.TryGetValue(v.GenLevel.Id, out string? ln))
                                levelName = ln;
                            return new ReprojectCeilingGridsViewModel.CeilingPlanViewEntry(v.Id, v.Name, levelName);
                        })
                        .ToList();
                    return new ReprojectCeilingGridsWebTool(
                        App.ReprojectHandler!,
                        App.ReprojectEvent!,
                        viewEntries,
                        BrowserTreeCapture.Capture(doc));
                });
                return Result.Succeeded;
            }
            var vm = BuildTool();
            var ready          = new ManualResetEventSlim(false);
            StepFlowWindow? win = null;

            var thread = new Thread(() =>
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
