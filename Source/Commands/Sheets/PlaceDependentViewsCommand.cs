using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Helpers;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Sheets.PlaceDependentViews;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class PlaceDependentViewsCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        /// <summary>View types that can host callout/section/elevation markers — the
        /// candidates offered as composite-mode source views.</summary>
        private static readonly HashSet<ViewType> CompositeSourceTypes = new HashSet<ViewType>
        {
            ViewType.FloorPlan,
            ViewType.CeilingPlan,
            ViewType.AreaPlan,
            ViewType.EngineeringPlan,
            ViewType.Section,
            ViewType.Elevation,
            ViewType.Detail,
        };

        public Result Execute(
            ExternalCommandData commandData,
            ref string          message,
            ElementSet          elements)
        {
            // Reuse the existing open window if any.
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
            PlaceDependentViewsViewModel BuildTool()
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // ── Title blocks ──────────────────────────────────────────────────
                var titleblocks = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsElementType()
                    .Cast<FamilySymbol>()
                    .OrderBy(tb => tb.FamilyName)
                    .ThenBy(tb => tb.Name)
                    .ToList();

                // ── Candidate views, one collector pass ───────────────────────────
                // parents: primary views that own dependents (dependents mode).
                // composites: view types that can host callout/section/elevation markers
                // (composite mode) — including dependent views, since a dependent shows its
                // own crop of the markers and is a valid composite source. Sub views are
                // discovered at run time, so no per-view marker scan happens here.
                var parents    = new List<ParentViewEntry>();
                var composites = new List<ParentViewEntry>();
                foreach (var v in new FilteredElementCollector(doc)
                             .OfClass(typeof(View)).Cast<View>()
                             .Where(v => !v.IsTemplate))
                {
                    string level = "";
                    try { level = v.GenLevel?.Name ?? ""; }
                    catch (System.Exception ex) { LemoineLog.Swallowed($"PlaceDependentViews: read GenLevel on view {v.Id.Value}", ex); }

                    // Only primaries can be a dependents-mode parent.
                    if (v.GetPrimaryViewId() == ElementId.InvalidElementId)
                    {
                        var deps = v.GetDependentViewIds();
                        if (deps != null && deps.Count > 0)
                            parents.Add(new ParentViewEntry(v.Id, v.Name, v.ViewType.ToString(), level, deps.Count));
                    }

                    if (CompositeSourceTypes.Contains(v.ViewType))
                        composites.Add(new ParentViewEntry(v.Id, v.Name, v.ViewType.ToString(), level, -1));
                }

                var vm = new PlaceDependentViewsViewModel(
                    App.PlaceDependentViewsHandler!, App.PlaceDependentViewsEvent!,
                    parents, composites, titleblocks,
                    BrowserTreeCapture.Capture(doc));

                return vm;
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
