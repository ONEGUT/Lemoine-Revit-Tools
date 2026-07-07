using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Helpers;
using LemoineTools.Framework;
using LemoineTools.Tools.LinkViews;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens the Bulk Duplicate Views window on a dedicated STA thread. Duplicatable views are
    /// collected on Revit's main thread, then handed to the ViewModel.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ViewsBulkDuplicateCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(ExternalCommandData commandData,
                              ref string message, ElementSet elements)
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
            ViewsBulkDuplicateViewModel BuildTool()
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // ── Collect duplicatable graphical views (main thread) ────────
                // Include any view that supports at least one duplicate option; the run handler
                // re-checks the specific chosen mode per view and skips unsupported ones.
                var views = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate
                             && v.ViewType != ViewType.Schedule
                             && v.ViewType != ViewType.Legend
                             && v.ViewType != ViewType.DrawingSheet
                             && v.ViewType != ViewType.ProjectBrowser
                             && v.ViewType != ViewType.SystemBrowser
                             && v.ViewType != ViewType.Internal
                             && v.ViewType != ViewType.Undefined
                             && (v.CanViewBeDuplicated(ViewDuplicateOption.Duplicate)
                              || v.CanViewBeDuplicated(ViewDuplicateOption.WithDetailing)
                              || v.CanViewBeDuplicated(ViewDuplicateOption.AsDependent)))
                    .Select(v => new ViewsBulkDuplicateViewModel.ViewEntry
                    {
                        Id        = v.Id,
                        Name      = v.Name,
                        TypeLabel = ViewsByTemplateRunHandler.ViewTypeLabel(v.ViewType),
                    })
                    .OrderBy(v => v.TypeLabel).ThenBy(v => v.Name)
                    .ToList();

                var vm = new ViewsBulkDuplicateViewModel(
                    App.ViewsBulkDuplicateRunHandler!, App.ViewsBulkDuplicateRunEvent!, views,
                    BrowserTreeCapture.Capture(doc));

                return vm;
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
    }
}
