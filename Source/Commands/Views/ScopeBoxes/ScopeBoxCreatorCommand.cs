using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.ScopeBoxes;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens the Scope Box Creator window on a dedicated STA thread. Available documents
    /// and the current scope-box list are captured on Revit's main thread, then handed
    /// to the ViewModel.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ScopeBoxCreatorCommand : IExternalCommand
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
            ScopeBoxCreatorViewModel BuildTool()
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // ── Capture available documents on the main thread ─────────
                var availableDocs = new List<ScopeBoxCreatorViewModel.DocEntry>
                {
                    new ScopeBoxCreatorViewModel.DocEntry
                    {
                        Label      = "(Host document)",
                        IsHost     = true,
                        LinkInstId = ElementId.InvalidElementId,
                    }
                };

                var seenDocIds = new HashSet<string> { doc.PathName ?? doc.Title };

                foreach (var li in new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .Where(l => l.GetLinkDocument() != null))
                {
                    var ld = li.GetLinkDocument();
                    string key = ld.PathName ?? ld.Title;
                    if (!seenDocIds.Add(key)) continue;

                    availableDocs.Add(new ScopeBoxCreatorViewModel.DocEntry
                    {
                        Label      = ld.Title ?? li.Name,
                        IsHost     = false,
                        LinkInstId = li.Id,
                    });
                }

                // ── Capture existing scope boxes for the seed picker ───────
                var scopeBoxes = ScopeBoxCreatorScanHandler.CollectScopeBoxes(doc);

                return new ScopeBoxCreatorViewModel(
                    App.ScopeBoxCreatorScanHandler!, App.ScopeBoxCreatorScanEvent!,
                    App.ScopeBoxCreatorRunHandler!,  App.ScopeBoxCreatorRunEvent!,
                    availableDocs, scopeBoxes);
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
