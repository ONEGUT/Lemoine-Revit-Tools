using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.CopyLinear;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CopyGridsCommand : IExternalCommand
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

            var doc = commandData.Application.ActiveUIDocument.Document;
            var links = CollectGridLinks(doc);

            var vm = new CopyGridsViewModel(App.CopyGridsRunHandler, App.CopyGridsRunEvent, links);

            var ready = new ManualResetEventSlim(false);
            StepFlowWindow? win = null;

            var thread = new Thread(() =>
            {
                win = new StepFlowWindow(vm);
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

        // Every loaded link that has grids, each grid flagged when its name already exists in the host.
        private static List<CopyGridLinkInfo> CollectGridLinks(Document doc)
        {
            var result = new List<CopyGridLinkInfo>();
            if (doc == null) return result;

            var hostNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
                    hostNames.Add(g.Name);
            }
            catch (Exception ex) { LemoineLog.Swallowed("CopyGridsCommand: read host grid names", ex); }

            foreach (var li in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var ld = li.GetLinkDocument();
                if (ld == null) continue;

                var info = new CopyGridLinkInfo
                {
                    Name = "[" + Path.GetFileNameWithoutExtension(ld.Title) + "]",
                    LinkInstId = li.Id.Value,
                };
                try
                {
                    foreach (var g in new FilteredElementCollector(ld).OfClass(typeof(Grid)).Cast<Grid>()
                                 .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
                        info.Grids.Add(new CopyGridItem
                        {
                            Name = g.Name, ElemId = g.Id.Value, ExistsInHost = hostNames.Contains(g.Name),
                        });
                }
                catch (Exception ex) { LemoineLog.Swallowed($"CopyGridsCommand: read grids in {info.Name}", ex); }

                if (info.Grids.Count > 0) result.Add(info);
            }
            return result;
        }
    }
}
