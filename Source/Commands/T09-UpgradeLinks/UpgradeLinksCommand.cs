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

                string? hostFolder     = null;
                bool    hostIsCloud    = false;
                bool    hostCanCloud   = false;
                string? cloudModelGuid = null;
                Guid    cloudHubGuid   = Guid.Empty;
                Guid    cloudProjectGuid = Guid.Empty;
                string  cloudFolderId  = "";

                try
                {
                    if (doc != null)
                    {
                        hostIsCloud = doc.IsModelInCloud;
                        if (hostIsCloud)
                        {
                            // Document.PathName for a cloud-hosted model reports Revit's own local
                            // Collaboration Cache path — an internal sync cache, not a real folder
                            // anything should be saved into. Never derive hostFolder from it here.
                            try
                            {
                                var cloudMp = doc.GetCloudModelPath();
                                cloudModelGuid   = cloudMp.GetModelGUID().ToString();
                                cloudProjectGuid = cloudMp.GetProjectGUID();
                                cloudFolderId    = doc.GetCloudFolderId(false);
                                hostCanCloud     = TryParseHubGuid(doc.GetHubId(), out cloudHubGuid)
                                                 && cloudProjectGuid != Guid.Empty
                                                 && !string.IsNullOrEmpty(cloudFolderId);
                            }
                            catch (Exception ex)
                            {
                                // Cloud model not fully synced, or the user isn't signed in — fail
                                // closed. The Cloud destination card stays hidden (hostCanCloud=false)
                                // rather than offering a target we can't actually resolve.
                                LemoineLog.Swallowed("UpgradeLinksCommand: harvest cloud host ids", ex);
                                hostCanCloud = false;
                            }
                        }
                        else if (!string.IsNullOrEmpty(doc.PathName))
                        {
                            hostFolder = Path.GetDirectoryName(doc.PathName);
                        }
                    }
                }
                catch (Exception ex) { LemoineLog.Swallowed("UpgradeLinksCommand: read host folder", ex); }

                return new UpgradeLinksViewModel(
                    App.UpgradeLinksScanHandler, App.UpgradeLinksScanEvent,
                    App.UpgradeLinksRunHandler,  App.UpgradeLinksRunEvent,
                    hostFolder, hostCanCloud, hostIsCloud, cloudModelGuid,
                    cloudHubGuid, cloudProjectGuid, cloudFolderId);
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

        // Document.GetHubId() returns a string — there is no Guid-typed hub/account accessor
        // anywhere in the Revit 2024 API (confirmed against the real RevitAPI.dll), but
        // SaveAsCloudModel's only third-party-usable overload wants a raw Guid accountId.
        // Autodesk hub ids are conventionally "<prefix>.<guid>" (e.g. "b.xxxxxxxx-xxxx-...") for
        // ACC/BIM 360 hubs — try the guid after the last '.', then the whole string, before
        // giving up. Unverified against a live ACC-connected session; if this convention doesn't
        // hold, TryParseHubGuid returns false and the Cloud destination stays hidden (fail closed).
        private static bool TryParseHubGuid(string? hubId, out Guid guid)
        {
            guid = Guid.Empty;
            if (string.IsNullOrEmpty(hubId)) return false;
            int dot = hubId.LastIndexOf('.');
            if (dot >= 0 && dot + 1 < hubId.Length && Guid.TryParse(hubId.Substring(dot + 1), out guid))
                return true;
            return Guid.TryParse(hubId, out guid);
        }
    }
}
