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
using LemoineTools.Tools.Setup;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CompareGridsCommand : IExternalCommand
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
            CompareGridsViewModel BuildTool()
            {
                var doc  = uiApp.ActiveUIDocument.Document;
                var data = CollectData(doc);
                return new CompareGridsViewModel(App.CompareGridsRunHandler, App.CompareGridsRunEvent, data);
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

        private static CompareGridsData CollectData(Document doc)
        {
            var data = new CompareGridsData();
            if (doc == null) return data;

            // Only links that actually contain grids are worth comparing.
            foreach (var li in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var ld = li.GetLinkDocument();
                if (ld == null) continue;

                bool hasGrids = false;
                try { hasGrids = new FilteredElementCollector(ld).OfClass(typeof(Grid)).Any(); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("CompareGridsCommand: probe link grids", ex); }
                if (!hasGrids) continue;

                data.Files.Add(new CompareFileInfo
                {
                    Name       = "[" + Path.GetFileNameWithoutExtension(ld.Title) + "]",
                    LinkInstId = li.Id.Value,
                });
            }
            return data;
        }
    }
}
