using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenSettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (App.GlobalSettings != null && App.GlobalSettings.IsVisible)
            {
                App.GlobalSettings.Activate();
                return Result.Succeeded;
            }

            App.GlobalSettings = new GlobalSettingsWindow();
            App.GlobalSettings.Closed += (s, e) => App.GlobalSettings = null;
            App.GlobalSettings.Show();
            return Result.Succeeded;
        }
    }
}
