using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenOverviewCommand : IExternalCommand
    {
        private static WebToolsOverviewWindow? _webWindow;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
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
                        DiagnosticsLog.Swallowed("OpenOverview: activate web window", ex);
                        _webWindow = null;
                    }
                }

                CaptureSamples(commandData);
                WebUiThread.Invoke(() =>
                {
                    var win = new WebToolsOverviewWindow();
                    win.Closed += (s, e) => { _webWindow = null; OverviewSamples.Clear(); ToolsOverviewDemos.DropCache(); };
                    win.Show();
                    _webWindow = win;
                });
                return Result.Succeeded;
            }

            if (App.Overview != null && App.Overview.IsVisible)
            {
                App.Overview.Activate();
                return Result.Succeeded;
            }

            CaptureSamples(commandData);
            App.Overview = new ToolsOverviewWindow();
            App.Overview.Closed += (s, e) => { App.Overview = null; OverviewSamples.Clear(); ToolsOverviewDemos.DropCache(); };
            App.Overview.Show();
            return Result.Succeeded;
        }

        // Snapshot the active document (main thread) so the dummy runs present real
        // view/sheet/level/link names. A capture failure must not block the overview —
        // the demos just fall back to canned sample data.
        private static void CaptureSamples(ExternalCommandData commandData)
        {
            try
            {
                OverviewSamples.Set(ToolsOverviewSampleCapture.Capture(commandData.Application.ActiveUIDocument));
            }
            catch (Exception ex)
            {
                OverviewSamples.Clear();
                DiagnosticsLog.Error("OpenOverview: sample capture", ex);
            }
        }
    }
}
