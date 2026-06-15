using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Helpers;
using LemoineTools.Lemoine;
using LemoineTools.Tools.LinkViews;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens the Bulk Views by Template window on a dedicated STA thread. Available views and
    /// view templates are collected on Revit's main thread, then handed to the ViewModel.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ViewsByTemplateCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(ExternalCommandData commandData,
                              ref string message, ElementSet elements)
        {
            // Bring an existing window to front via its own dispatcher.
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

            // ── Collect duplicatable graphical views (main thread) ────────
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
                         && v.CanViewBeDuplicated(ViewDuplicateOption.WithDetailing))
                .Select(v => new ViewsByTemplateViewModel.ViewEntry
                {
                    Id        = v.Id,
                    Name      = v.Name,
                    TypeLabel = ViewsByTemplateRunHandler.ViewTypeLabel(v.ViewType),
                })
                .OrderBy(v => v.TypeLabel).ThenBy(v => v.Name)
                .ToList();

            // ── Collect view templates (main thread) ──────────────────────
            var templates = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.IsTemplate)
                .Select(v => new ViewsByTemplateViewModel.TemplateEntry
                {
                    Id        = v.Id,
                    Name      = v.Name,
                    TypeLabel = ViewsByTemplateRunHandler.ViewTypeLabel(v.ViewType),
                })
                .OrderBy(t => t.TypeLabel).ThenBy(t => t.Name)
                .ToList();

            var vm = new ViewsByTemplateViewModel(
                App.ViewsByTemplateRunHandler!, App.ViewsByTemplateRunEvent!,
                views, templates,
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
