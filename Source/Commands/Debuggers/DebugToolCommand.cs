using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Placeholder command for the reserved Developer-panel slot.
    /// The original UI Debug tool was removed; this keeps the ribbon button
    /// in place for a future tool. Clicking is an intentional no-op.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class DebugToolCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Intentional no-op — reserved placeholder slot, no tool wired yet.
            return Result.Succeeded;
        }
    }
}
