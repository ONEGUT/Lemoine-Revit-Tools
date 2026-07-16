using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;
using LemoineTools.Tools.Setup;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class LinkAuditCommand : IExternalCommand
    {
        // Tracked on the shared WebUiThread, like the other web windows.
        private static WebLinkAuditWindow? _webWindow;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument.Document;

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
                        DiagnosticsLog.Swallowed("LinkAuditCommand: activate web window", ex);
                        _webWindow = null;
                    }
                }

                var webData = LinkAuditCapture.Capture(doc); // main-thread capture (re-captured each open)
                WebUiThread.Invoke(() =>
                {
                    var win = new WebLinkAuditWindow(webData);
                    win.Closed += (s, e) => { _webWindow = null; };
                    win.Show();
                    _webWindow = win;
                });
                return Result.Succeeded;
            }

            if (App.LinkAudit != null && App.LinkAudit.IsVisible)
            {
                App.LinkAudit.Activate();
                return Result.Succeeded;
            }

            var data = LinkAuditCapture.Capture(doc);
            App.LinkAudit = new LinkAuditWindow(data);
            App.LinkAudit.Closed += (s, e) => { App.LinkAudit = null; };
            App.LinkAudit.Show();
            return Result.Succeeded;
        }
    }
}
