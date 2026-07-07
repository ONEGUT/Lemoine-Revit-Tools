using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Tools.LinkViews;
using LemoineTools.Tools.ScopeBoxes;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens the Bulk Views by Level window on a dedicated STA thread. Levels, scope
    /// boxes, and view templates are captured on Revit's main thread and handed to the
    /// ViewModel — the tool has no scan phase (room searching lives in the Scope Box
    /// Creator now).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class LinkViewsLevelCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(ExternalCommandData commandData,
                              ref string message, ElementSet elements)
        {
            // Bring existing window to front
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
            LinkViewsLevelViewModel BuildTool()
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // ── Capture host levels on the main thread ─────────────
                var levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .Select(l => new LinkViewsLevelViewModel.LevelEntry
                    {
                        Id          = l.Id,
                        Name        = l.Name,
                        ElevationFt = System.Math.Round(UnitUtils.ConvertFromInternalUnits(
                                          l.Elevation, UnitTypeId.Feet), 2),
                    })
                    .ToList();

                // ── Capture scope boxes on the main thread ─────────────
                var scopeBoxes = ScopeBoxCreatorScanHandler.CollectScopeBoxes(doc);

                // ── Collect view templates on main thread ──────────────
                var templates3D  = CollectViewTemplates(doc, ViewType.ThreeD);
                var templatesFP  = CollectViewTemplates(doc, ViewType.FloorPlan);
                var templatesRCP = CollectViewTemplates(doc, ViewType.CeilingPlan);

                return new LinkViewsLevelViewModel(
                    App.LinkViewsLevelRunHandler!, App.LinkViewsLevelRunEvent!,
                    levels, scopeBoxes, templates3D, templatesFP, templatesRCP);
            }
            var vm = BuildTool();
            var ready = new ManualResetEventSlim(false);
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

        private static List<LinkViewsLevelViewModel.ViewTemplateEntry> CollectViewTemplates(
            Document doc, ViewType vt) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.IsTemplate && v.ViewType == vt)
                .OrderBy(v => v.Name)
                .Select(v => new LinkViewsLevelViewModel.ViewTemplateEntry
                    { Id = v.Id, Name = v.Name })
                .ToList();
    }
}
