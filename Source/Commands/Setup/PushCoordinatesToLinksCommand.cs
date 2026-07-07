using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Setup;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class PushCoordinatesToLinksCommand : IExternalCommand
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
            PushCoordinatesToLinksViewModel BuildTool()
            {
                var doc  = uiApp.ActiveUIDocument?.Document;
                var data = CollectData(doc);
                return new PushCoordinatesToLinksViewModel(App.PushCoordinatesRunHandler, App.PushCoordinatesRunEvent, data);
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

        private static PushCoordinatesData CollectData(Document? doc)
        {
            var data = new PushCoordinatesData();
            if (doc == null) return data;

            try
            {
                foreach (var li in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    var ld = li.GetLinkDocument();
                    if (ld == null) continue;

                    data.Links.Add(new PushLinkInfo
                    {
                        Name       = "[" + Path.GetFileNameWithoutExtension(ld.Title) + "]",
                        LinkInstId = li.Id.Value,
                    });
                }
            }
            catch (Exception ex) { LemoineLog.Swallowed("PushCoordinatesToLinksCommand: read links", ex); }

            return data;
        }
    }
}
