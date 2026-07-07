using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Coordinates;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class LinkAuditCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (App.LinkAudit != null && App.LinkAudit.IsVisible)
            {
                App.LinkAudit.Activate();
                return Result.Succeeded;
            }

            var doc  = commandData.Application.ActiveUIDocument.Document;
            var data = LinkAuditCapture.Capture(doc);

            App.LinkAudit = new LinkAuditWindow(data);
            App.LinkAudit.Closed += (s, e) => { App.LinkAudit = null; };
            App.LinkAudit.Show();
            return Result.Succeeded;
        }
    }
}
