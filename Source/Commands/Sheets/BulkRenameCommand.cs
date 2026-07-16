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
using LemoineTools.Tools.LinkViews;
using LemoineTools.Tools.LinkViews.BulkRename;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens the Bulk Rename window on a dedicated STA thread. Sheets and views are
    /// collected on Revit's main thread, then handed to the ViewModel.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class BulkRenameCommand : IExternalCommand
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
                catch (System.Exception ex) { DiagnosticsLog.Swallowed("BulkRenameCommand: reuse open window", ex); _window = null; }
            }

            var uiApp = commandData.Application;
            BulkRenameViewModel BuildTool()
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // ── Sheets (main thread) ──────────────────────────────────────
                var sheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .Where(s => !s.IsTemplate)
                    .OrderBy(s => s.SheetNumber)
                    .Select(s => new BulkRenameViewModel.SheetEntry
                    {
                        Id     = s.Id,
                        Number = s.SheetNumber ?? "",
                        Name   = s.Name ?? "",
                    })
                    .ToList();

                // ── Views (main thread) — graphical/schedule/legend, never sheets ─
                var views = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate
                             && !(v is ViewSheet)
                             && v.ViewType != ViewType.ProjectBrowser
                             && v.ViewType != ViewType.SystemBrowser
                             && v.ViewType != ViewType.Internal
                             && v.ViewType != ViewType.Undefined)
                    .Select(v => new BulkRenameViewModel.ViewEntry
                    {
                        Id        = v.Id,
                        Name      = v.Name,
                        TypeLabel = ViewsByTemplateRunHandler.ViewTypeLabel(v.ViewType),
                    })
                    .OrderBy(v => v.TypeLabel).ThenBy(v => v.Name)
                    .ToList();

                var vm = new BulkRenameViewModel(
                    App.BulkRenameRunHandler!, App.BulkRenameRunEvent!, sheets, views,
                    BrowserTreeCapture.Capture(doc));

                return vm;
            }
            if (WebToolLauncher.Enabled)
            {
                WebToolLauncher.Open("bulkRename", () =>
                {
                    var doc = uiApp.ActiveUIDocument.Document;

                    var sheets = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .Where(s => !s.IsTemplate)
                        .OrderBy(s => s.SheetNumber)
                        .Select(s => new BulkRenameViewModel.SheetEntry
                        {
                            Id     = s.Id,
                            Number = s.SheetNumber ?? "",
                            Name   = s.Name ?? "",
                        })
                        .ToList();

                    var views = new FilteredElementCollector(doc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => !v.IsTemplate
                                 && !(v is ViewSheet)
                                 && v.ViewType != ViewType.ProjectBrowser
                                 && v.ViewType != ViewType.SystemBrowser
                                 && v.ViewType != ViewType.Internal
                                 && v.ViewType != ViewType.Undefined)
                        .Select(v => new BulkRenameViewModel.ViewEntry
                        {
                            Id        = v.Id,
                            Name      = v.Name,
                            TypeLabel = ViewsByTemplateRunHandler.ViewTypeLabel(v.ViewType),
                        })
                        .OrderBy(v => v.TypeLabel).ThenBy(v => v.Name)
                        .ToList();

                    return new BulkRenameWebTool(
                        App.BulkRenameRunHandler!, App.BulkRenameRunEvent!, sheets, views,
                        BrowserTreeCapture.Capture(doc));
                });
                return Result.Succeeded;
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
