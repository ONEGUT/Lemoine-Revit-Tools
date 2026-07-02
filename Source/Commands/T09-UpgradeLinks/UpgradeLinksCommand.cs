using System;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.UpgradeLinks;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class UpgradeLinksCommand : IExternalCommand
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
            UpgradeLinksViewModel BuildTool()
            {
                var doc = uiApp.ActiveUIDocument?.Document;

                string? hostFolder = null;
                try
                {
                    if (doc != null && !string.IsNullOrEmpty(doc.PathName))
                        hostFolder = Path.GetDirectoryName(doc.PathName);
                }
                catch (Exception ex) { LemoineLog.Swallowed("UpgradeLinksCommand: read host folder", ex); }

                // Cloud destination stays off until an APS/Forge integration can supply the host's
                // account id + containing folder id — the Revit API exposes neither, so we cannot
                // resolve a valid SaveAsCloudModel target from the document alone (see plan phase 2).
                bool hostCanCloud = false;

                return new UpgradeLinksViewModel(
                    App.UpgradeLinksScanHandler, App.UpgradeLinksScanEvent,
                    App.UpgradeLinksRunHandler,  App.UpgradeLinksRunEvent,
                    hostFolder, hostCanCloud);
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
