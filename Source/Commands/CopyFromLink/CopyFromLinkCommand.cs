using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Tools.AutoFilters;
using LemoineTools.Tools.CopyFromLink;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CopyFromLinkCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
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
            CopyFromLinkViewModel BuildTool()
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Capture the full filterable-category list on the Revit main thread (same as Copy Linear).
                AutoFiltersSettings.CaptureFilterableCategories(doc);

                var links = CollectLinks(doc);

                var vm = new CopyFromLinkViewModel(
                    App.CopyFromLinkScanHandler, App.CopyFromLinkScanEvent,
                    App.CopyFromLinkRunHandler,  App.CopyFromLinkRunEvent,
                    links);

                return vm;
            }
            var vm = BuildTool();
            var ready = new ManualResetEventSlim(false);
            StepFlowWindow? win = null;

            var thread = new Thread(() =>
            {
                win = new StepFlowWindow(vm, BuildTool);
                win.Closed += (s, e) => { _window = null; Dispatcher.CurrentDispatcher.InvokeShutdown(); };
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

        // Every loaded link, each with its user worksets. The host is excluded — this tool copies
        // out of links only.
        private static List<CopyFromLinkLinkInfo> CollectLinks(Document doc)
        {
            var links = new List<CopyFromLinkLinkInfo>();
            if (doc == null) return links;

            foreach (var li in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var ld = li.GetLinkDocument();
                if (ld == null) continue;
                links.Add(new CopyFromLinkLinkInfo
                {
                    Name = "[" + Path.GetFileNameWithoutExtension(ld.Title) + "]",
                    LinkInstId = li.Id.Value, LinkInstUid = li.UniqueId,
                    Worksets = ReadUserWorksets(ld),
                });
            }
            links.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return links;
        }

        private static List<CopyFromLinkWorksetInfo> ReadUserWorksets(Document d)
        {
            var list = new List<CopyFromLinkWorksetInfo>();
            try
            {
                if (d == null || !d.IsWorkshared) return list;
                foreach (var ws in new FilteredWorksetCollector(d).OfKind(WorksetKind.UserWorkset))
                    list.Add(new CopyFromLinkWorksetInfo { Id = ws.Id.IntegerValue, Name = ws.Name });
                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("CopyFromLinkCommand: read user worksets", ex); }
            return list;
        }
    }
}
