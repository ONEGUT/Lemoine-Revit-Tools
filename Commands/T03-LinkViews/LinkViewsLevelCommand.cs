using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.LinkViews;

namespace LemoineTools.Commands
{
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

            var uiDoc = commandData.Application.ActiveUIDocument;
            var doc   = uiDoc.Document;

            // ── Capture available documents on the main thread ─────────
            var availableDocs = new List<LinkViewsLevelViewModel.DocEntry>
            {
                new LinkViewsLevelViewModel.DocEntry
                {
                    Label     = "(Host document)",
                    IsHost    = true,
                    LinkInstId = ElementId.InvalidElementId,
                }
            };

            var seenDocIds = new System.Collections.Generic.HashSet<string>
                { doc.PathName ?? doc.Title };

            foreach (var li in new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .Where(l => l.GetLinkDocument() != null))
            {
                var ld = li.GetLinkDocument();
                string key = ld.PathName ?? ld.Title;
                if (!seenDocIds.Add(key)) continue;

                availableDocs.Add(new LinkViewsLevelViewModel.DocEntry
                {
                    Label      = ld.Title ?? li.Name,
                    IsHost     = false,
                    LinkInstId = li.Id,
                });
            }

            // ── Create ViewModel and open window on STA thread ────────
            var vm    = new LinkViewsLevelViewModel(
                App.LinkViewsLevelPhase1Handler!, App.LinkViewsLevelPhase1Event!,
                App.LinkViewsLevelRunHandler!,    App.LinkViewsLevelRunEvent!,
                availableDocs);

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
