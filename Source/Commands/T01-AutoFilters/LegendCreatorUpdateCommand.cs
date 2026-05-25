using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Ribbon button: reads the current Legend Creator settings and redraws an
    /// existing legend view chosen by the user.  When only one legend exists it is
    /// picked automatically; otherwise a picker dialog is shown.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class LegendCreatorUpdateCommand : IExternalCommand
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

            var legends = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Views).Cast<View>()
                .Where(v => v.ViewType == ViewType.Legend)
                .OrderBy(v => v.Name, System.StringComparer.OrdinalIgnoreCase)
                .Select(v => (v.Id, v.Name))
                .ToList();

            if (legends.Count == 0)
            {
                TaskDialog.Show("Legend Creator — Update",
                    "No Legend views found in this project.\n\n" +
                    "Use the 'Create Legend' button first to create a legend from the current settings.");
                return Result.Cancelled;
            }

            ElementId targetId;
            if (legends.Count == 1)
            {
                targetId = legends[0].Id;
            }
            else
            {
                var win = new LegendPickerWindow(legends);
                bool? picked = win.ShowDialog();
                if (picked != true || win.SelectedLegendId == null) return Result.Cancelled;
                targetId = win.SelectedLegendId;
            }

            App.LegendCreatorHandler.UpdateMode     = true;
            App.LegendCreatorHandler.TargetLegendId = targetId;
            App.LegendCreatorEvent.Raise();
            return Result.Succeeded;
        }
    }
}
