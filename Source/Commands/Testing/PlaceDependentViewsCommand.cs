using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Testing.PlaceDependentViews;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class PlaceDependentViewsCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

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

            var doc = commandData.Application.ActiveUIDocument.Document;

            // ── Title blocks ──────────────────────────────────────────────────
            var titleblocks = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType()
                .Cast<FamilySymbol>()
                .OrderBy(tb => tb.FamilyName)
                .ThenBy(tb => tb.Name)
                .ToList();

            // ── Primary views that own dependent views ────────────────────────
            var parents = new List<ParentViewEntry>();
            foreach (var v in new FilteredElementCollector(doc)
                         .OfClass(typeof(View)).Cast<View>()
                         .Where(v => !v.IsTemplate))
            {
                // A view is a primary (not itself a dependent) and owns dependents.
                if (v.GetPrimaryViewId() != ElementId.InvalidElementId) continue;
                var deps = v.GetDependentViewIds();
                if (deps == null || deps.Count == 0) continue;

                string level = "";
                try { level = v.GenLevel?.Name ?? ""; }
                catch (System.Exception ex) { LemoineLog.Swallowed($"PlaceDependentViews: read GenLevel on view {v.Id.Value}", ex); }

                parents.Add(new ParentViewEntry(v.Id, v.Name, v.ViewType.ToString(), level, deps.Count));
            }

            var vm = new PlaceDependentViewsViewModel(
                App.PlaceDependentViewsHandler!, App.PlaceDependentViewsEvent!,
                parents, titleblocks);

            var ready = new ManualResetEventSlim(false);
            StepFlowWindow? win = null;

            var thread = new System.Threading.Thread(() =>
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
