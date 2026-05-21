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
    public class LinkViewsDisciplineCommand : IExternalCommand
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

            var doc = commandData.Application.ActiveUIDocument.Document;

            // ── Capture link instances on the main thread ──────────────
            var links = new List<LinkViewsDisciplineViewModel.LinkEntry>();
            var seen  = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            foreach (var li in new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .Where(l => l.GetLinkDocument() != null)
                .OrderBy(l => l.Name))
            {
                var ld  = li.GetLinkDocument();
                string key = ld.PathName ?? ld.Title;
                if (!seen.Add(key)) continue;  // skip duplicate instances of same link

                links.Add(new LinkViewsDisciplineViewModel.LinkEntry
                {
                    LinkInstId = li.Id,
                    Name       = ld.Title ?? li.Name,
                });
            }

            var vm = new LinkViewsDisciplineViewModel(
                App.LinkViewsDisciplineRunHandler!,
                App.LinkViewsDisciplineRunEvent!,
                links);

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
