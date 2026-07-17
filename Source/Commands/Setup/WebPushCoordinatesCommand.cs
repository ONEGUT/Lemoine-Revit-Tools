using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;
using LemoineTools.Tools.Setup;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Phase 3 wave-1 migration: opens Push Coordinates to Links in the HTML WebStepFlowWindow
    /// (PushCoordinatesWebTool) instead of the WPF StepFlowWindow. Reuses the exact same
    /// data collection and ExternalEvent handler as PushCoordinatesToLinksCommand. Kept as a
    /// separate (Developer-panel) command until verified on Windows; then the production
    /// command flips to this window and the WPF ViewModel is retired (rule R25).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class WebPushCoordinatesCommand : IExternalCommand
    {
        private static WebStepFlowWindow? _window;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (_window != null)
            {
                try
                {
                    WebUiThread.Invoke(() =>
                    {
                        var w = _window;
                        if (w != null && w.IsVisible) w.Activate();
                        else _window = null;
                    });
                    if (_window != null) return Result.Succeeded;
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed("WebPushCoordinatesCommand: activate existing window", ex);
                    _window = null;
                }
            }

            var doc  = commandData.Application.ActiveUIDocument?.Document;
            var data = CollectData(doc);   // Revit read on the main thread
            var vm   = new PushCoordinatesWebTool(App.PushCoordinatesRunHandler, App.PushCoordinatesRunEvent, data);

            WebUiThread.Invoke(() =>
            {
                var win = new WebStepFlowWindow(vm);
                win.Closed += (s, e) => { _window = null; };
                win.Show();
                _window = win;
            });
            return Result.Succeeded;
        }

        // Same collection as PushCoordinatesToLinksCommand (kept independent so the WPF command
        // is untouched during the parallel-verify window).
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
            catch (Exception ex) { DiagnosticsLog.Swallowed("WebPushCoordinatesCommand: read links", ex); }

            return data;
        }
    }
}
