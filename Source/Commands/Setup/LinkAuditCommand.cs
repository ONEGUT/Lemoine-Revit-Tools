using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Tools.Setup;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class LinkAuditCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Keep the project-scoped settings key fresh: commands run on the Revit main
            // thread with the document in hand, tool windows do not.
            DocumentKey.SetCurrent(commandData.Application.ActiveUIDocument?.Document);
            // Libraries live in the .rvt so they travel with the project and every team
            // member sees the same ones. Pull this document's in before any window opens.
            LemoineTools.Framework.Project.ProjectLibraries.LoadForDocument(
                commandData.Application.ActiveUIDocument?.Document);

            var doc = commandData.Application.ActiveUIDocument.Document;

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
