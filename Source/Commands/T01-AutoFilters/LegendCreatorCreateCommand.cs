using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Ribbon button: reads the current Legend Creator settings and creates a new
    /// legend view in the active document by duplicating an existing legend template.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class LegendCreatorCreateCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string          message,
            ElementSet          elements)
        {
            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null) return Result.Failed;

            if (App.LegendCreatorHandler == null || App.LegendCreatorEvent == null)
            {
                TaskDialog.Show("Legend Creator", "Legend Creator is not initialised. Restart Revit and try again.");
                return Result.Failed;
            }

            // A legend view must exist to use as a template for duplication.
            var template = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Views).Cast<View>()
                .FirstOrDefault(v => v.ViewType == ViewType.Legend);

            if (template == null)
            {
                TaskDialog.Show("Legend Creator — Create",
                    "No Legend views found in this project.\n\n" +
                    "Create a blank Legend view first via View → New Legend, " +
                    "then run this command again.");
                return Result.Cancelled;
            }

            App.LegendCreatorHandler.UpdateMode      = false;
            App.LegendCreatorHandler.TemplateLegendId = template.Id;
            App.LegendCreatorHandler.TargetLegendId  = null;
            App.LegendCreatorEvent.Raise();
            return Result.Succeeded;
        }
    }
}
