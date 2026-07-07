using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenOverviewCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (App.Overview != null && App.Overview.IsVisible)
            {
                App.Overview.Activate();
                return Result.Succeeded;
            }

            // Snapshot the active document (main thread) so the dummy runs present
            // real view/sheet/level/link names. Cleared with the window so the
            // captured strings don't outlive it. A capture failure must not block
            // the overview — the demos just fall back to canned sample data.
            try
            {
                OverviewSamples.Set(ToolsOverviewSampleCapture.Capture(commandData.Application.ActiveUIDocument));
            }
            catch (System.Exception ex)
            {
                OverviewSamples.Clear();
                DiagnosticsLog.Error("OpenOverview: sample capture", ex);
            }

            App.Overview = new ToolsOverviewWindow();
            App.Overview.Closed += (s, e) => { App.Overview = null; OverviewSamples.Clear(); ToolsOverviewDemos.DropCache(); };
            App.Overview.Show();
            return Result.Succeeded;
        }
    }
}
