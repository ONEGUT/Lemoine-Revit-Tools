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

            var uiApp = commandData.Application;
            LinkViewsDisciplineViewModel BuildTool()
            {
                var doc = uiApp.ActiveUIDocument.Document;

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

                // ── Collect 3D view templates on the main thread ────────────
                var templates3D = CollectViewTemplates(doc);

                var vm = new LinkViewsDisciplineViewModel(
                    App.LinkViewsDisciplineRunHandler!,
                    App.LinkViewsDisciplineRunEvent!,
                    links,
                    templates3D);

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
        private static List<LinkViewsDisciplineViewModel.ViewTemplateEntry> CollectViewTemplates(Document doc) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .Where(v => v.IsTemplate && v.ViewType == ViewType.ThreeD)
                .OrderBy(v => v.Name)
                .Select(v => new LinkViewsDisciplineViewModel.ViewTemplateEntry { Id = v.Id, Name = v.Name })
                .ToList();
    }
}
