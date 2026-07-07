using System;
using System.IO;
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

                string? hostFolder  = null;
                bool    hostIsCloud = false;

                try
                {
                    if (doc != null)
                    {
                        hostIsCloud = doc.IsModelInCloud;
                        // Document.PathName for a cloud-hosted model reports Revit's own local
                        // Collaboration Cache path — an internal sync cache, not a real folder
                        // anything should be saved into. Never derive hostFolder from it there;
                        // the Cloud destination doesn't need a local folder at all — every file's
                        // cloud save goes through Revit's own native "Save As Cloud Model" dialog
                        // (see UpgradeLinksRunHandler), which handles ACC folder picking itself.
                        if (!hostIsCloud && !string.IsNullOrEmpty(doc.PathName))
                            hostFolder = Path.GetDirectoryName(doc.PathName);
                    }
                }
                catch (Exception ex) { DiagnosticsLog.Swallowed("UpgradeLinksCommand: read host folder", ex); }

                return new UpgradeLinksViewModel(
                    App.UpgradeLinksScanHandler, App.UpgradeLinksScanEvent,
                    App.UpgradeLinksRunHandler,  App.UpgradeLinksRunEvent,
                    hostFolder, hostIsCloud);
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
