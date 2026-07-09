using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;

namespace LemoineTools.Commands
{
    // Developer-panel pilot command — opens the WebView2 copy of Tools Overview
    // alongside the real one (OpenOverviewCommand) for a side-by-side comparison.
    // See plan-design-twin-parity-and-webview2-overview.md Part B.
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenOverviewWebCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (App.OverviewWeb != null && App.OverviewWeb.IsVisible)
            {
                App.OverviewWeb.Activate();
                return Result.Succeeded;
            }

            try
            {
                OverviewSamples.Set(ToolsOverviewSampleCapture.Capture(commandData.Application.ActiveUIDocument));
            }
            catch (System.Exception ex)
            {
                OverviewSamples.Clear();
                DiagnosticsLog.Error("OpenOverviewWeb: sample capture", ex);
            }

            App.OverviewWeb = new ToolsOverviewWebWindow();
            App.OverviewWeb.Closed += (s, e) => { App.OverviewWeb = null; OverviewSamples.Clear(); ToolsOverviewDemos.DropCache(); };
            App.OverviewWeb.Show();
            return Result.Succeeded;
        }
    }
}
