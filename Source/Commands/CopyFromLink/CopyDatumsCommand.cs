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
using LemoineTools.Framework.Web;
using LemoineTools.Tools.CopyFromLink;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CopyDatumsCommand : IExternalCommand
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
            CopyDatumsViewModel BuildTool()
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var links = CollectDatumLinks(doc);

                var vm = new CopyDatumsViewModel(App.CopyDatumsRunHandler, App.CopyDatumsRunEvent, links);

                return vm;
            }
            if (WebToolLauncher.Enabled)
            {
                WebToolLauncher.Open("copyDatums", () =>
                {
                    var doc = uiApp.ActiveUIDocument.Document;
                    var links = CollectDatumLinks(doc);
                    return new CopyDatumsWebTool(App.CopyDatumsRunHandler, App.CopyDatumsRunEvent, links);
                });
                return Result.Succeeded;
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

        // Every loaded link that has grids and/or levels, each item flagged when its name
        // already exists in the host — Revit enforces unique names for both element types.
        private static List<CopyDatumLinkInfo> CollectDatumLinks(Document doc)
        {
            var result = new List<CopyDatumLinkInfo>();
            if (doc == null) return result;

            var hostGridNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
                    hostGridNames.Add(g.Name);
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("CopyDatumsCommand: read host grid names", ex); }

            var hostLevelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var lvl in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
                    hostLevelNames.Add(lvl.Name);
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("CopyDatumsCommand: read host level names", ex); }

            foreach (var li in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var ld = li.GetLinkDocument();
                if (ld == null) continue;

                var info = new CopyDatumLinkInfo
                {
                    Name = "[" + Path.GetFileNameWithoutExtension(ld.Title) + "]",
                    LinkInstId = li.Id.Value,
                };
                try
                {
                    foreach (var g in new FilteredElementCollector(ld).OfClass(typeof(Grid)).Cast<Grid>()
                                 .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
                        info.Grids.Add(new CopyDatumItem
                        {
                            Name = g.Name, ElemId = g.Id.Value, ExistsInHost = hostGridNames.Contains(g.Name),
                        });
                }
                catch (Exception ex) { DiagnosticsLog.Swallowed($"CopyDatumsCommand: read grids in {info.Name}", ex); }

                try
                {
                    foreach (var lvl in new FilteredElementCollector(ld).OfClass(typeof(Level)).Cast<Level>()
                                 .OrderBy(lvl => lvl.Name, StringComparer.OrdinalIgnoreCase))
                        info.Levels.Add(new CopyDatumItem
                        {
                            Name = lvl.Name, ElemId = lvl.Id.Value, ExistsInHost = hostLevelNames.Contains(lvl.Name),
                        });
                }
                catch (Exception ex) { DiagnosticsLog.Swallowed($"CopyDatumsCommand: read levels in {info.Name}", ex); }

                if (info.Grids.Count > 0 || info.Levels.Count > 0) result.Add(info);
            }
            return result;
        }
    }
}
