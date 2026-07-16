using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Naming;
using LemoineTools.Framework.Web;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenSettingsCommand : IExternalCommand
    {
        // The web settings window lives on the shared WebUiThread (never disposed for the
        // session), so track it there rather than on App.GlobalSettings (which is the WPF one).
        private static WebSettingsWindow? _webWindow;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Feature-flagged web settings surface (General tab). Other tabs still render a
            // placeholder — the classic WPF window remains the fallback when the flag is off.
            if (WebUiSettings.Instance.Enabled)
            {
                if (_webWindow != null)
                {
                    try
                    {
                        WebUiThread.Invoke(() =>
                        {
                            var w = _webWindow;
                            if (w != null && w.IsVisible) w.Activate();
                            else _webWindow = null;
                        });
                        if (_webWindow != null) return Result.Succeeded;
                    }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Swallowed("OpenSettingsCommand: activate web settings window", ex);
                        _webWindow = null;
                    }
                }

                // Capture the parameter catalog on the Revit main thread (same as the WPF branch);
                // the web window is Revit-free and only reads this snapshot for the Naming tab.
                var webDoc = commandData.Application.ActiveUIDocument?.Document;
                var webSnapshot = ParameterCatalog.Capture(webDoc);
                WebUiThread.Invoke(() =>
                {
                    var win = new WebSettingsWindow(webSnapshot);
                    win.Closed += (s, e) => { _webWindow = null; };
                    win.Show();
                    _webWindow = win;
                });
                return Result.Succeeded;
            }

            if (App.GlobalSettings != null && App.GlobalSettings.IsVisible)
            {
                App.GlobalSettings.Activate();
                return Result.Succeeded;
            }

            // Main-thread-only capture, same pattern as
            // AutoFiltersSettings.CaptureFilterableCategories(doc) — the window itself never
            // touches Revit. No document open still opens the page (manual entry only).
            var doc = commandData.Application.ActiveUIDocument?.Document;
            var namingSnapshot = ParameterCatalog.Capture(doc);

            App.GlobalSettings = new GlobalSettingsWindow(namingSnapshot);
            App.GlobalSettings.Closed += (s, e) => App.GlobalSettings = null;
            App.GlobalSettings.Show();
            return Result.Succeeded;
        }
    }
}
