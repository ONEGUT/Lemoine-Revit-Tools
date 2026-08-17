using System;
using System.Collections.Generic;
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
    public class ReplaceLinkCommand : IExternalCommand
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
                catch (Exception ex) { DiagnosticsLog.Swallowed("ReplaceLinkCommand: activate existing window", ex); _window = null; }
            }

            var uiApp = commandData.Application;

            // Captured ONCE, here, on Revit's main/API thread. BuildTool is also the window's
            // Reset factory, which runs on the window's own STA thread — a FilteredElementCollector
            // sweep there would be an API call off the API thread. Reset therefore reuses this
            // snapshot; a link added or removed in Revit after the window opened needs the tool
            // reopened, which is the safe trade.
            var capturedLinks = new List<HostLinkInfo>();
            try
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                if (doc != null) capturedLinks = ReplaceLinkCapture.Capture(doc);
            }
            catch (Exception ex) { DiagnosticsLog.Error("ReplaceLinkCommand: capture host links", ex); }

            ReplaceLinkViewModel BuildTool()
            {
                return new ReplaceLinkViewModel(
                    App.ReplaceLinkScanHandler,  App.ReplaceLinkScanEvent,
                    App.ReplaceLinkRunHandler,   App.ReplaceLinkRunEvent,
                    capturedLinks);
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
    }
}
