using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

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

            App.Overview = new ToolsOverviewWindow();
            App.Overview.Closed += (s, e) => App.Overview = null;
            App.Overview.Show();
            return Result.Succeeded;
        }
    }
}
