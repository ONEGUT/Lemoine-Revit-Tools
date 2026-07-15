using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework.Web;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Flips the machine-wide Web UI flag (see <see cref="WebUiSettings"/>). When ON, every
    /// migrated production command opens its WebView2 window instead of the WPF one — the
    /// R25 parallel-verify mechanism at scale. Shows the new state so the click is never silent.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class ToggleWebUiCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var s = WebUiSettings.Instance;
            s.Enabled = !s.Enabled;
            s.Save();
            TaskDialog.Show("Lemoine Web UI",
                s.Enabled
                    ? "Web UI is ON — migrated tools now open in the WebView2 window."
                    : "Web UI is OFF — all tools open in their WPF windows.");
            return Result.Succeeded;
        }
    }
}
