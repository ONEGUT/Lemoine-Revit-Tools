using System;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Tools.Zones;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens "Create Views from Zones" on its own STA thread. The zone library is loaded from
    /// the document here, on Revit's main thread, before the window exists.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ZoneViewsCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Open(commandData?.Application);
            return Result.Succeeded;
        }

        /// <summary>
        /// Opens (or re-activates) the window. MUST be called on Revit's main thread — it reads
        /// the active document. Same shape as ZoneDiscoverCommand.Open, and for the same reason:
        /// the Zone Manager's toolbar launches this through an ExternalEvent.
        /// </summary>
        public static void Open(UIApplication? uiApp)
        {
            var doc = uiApp?.ActiveUIDocument?.Document;

            DocumentKey.SetCurrent(doc);
            LemoineTools.Framework.Project.ProjectLibraries.LoadForDocument(doc);

            if (_window != null)
            {
                try
                {
                    _window.Dispatcher.Invoke(() =>
                    {
                        if (_window.IsVisible) _window.Activate();
                        else _window = null;
                    });
                    if (_window != null) return;
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed("ZoneViewsCommand: reactivate existing window", ex);
                    _window = null;
                }
            }

            ZoneViewsViewModel BuildTool()
                => new ZoneViewsViewModel(App.ZoneViewsRunHandler, App.ZoneViewsRunEvent);

            var vm    = BuildTool();
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
        }
    }
}
