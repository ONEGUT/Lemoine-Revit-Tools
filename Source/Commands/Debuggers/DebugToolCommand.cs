using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Debuggers;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens the UI Debug tool — exercises every input control and UI element
    /// in the Lemoine system. Available from the Developer panel on the ribbon.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class DebugToolCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (_window != null && _window.IsVisible) { _window.Activate(); return Result.Succeeded; }

            var vm = new DebugToolViewModel();
            _window = new StepFlowWindow(vm);
            _window.Closed += (s, e) => _window = null;
            _window.Show();
            return Result.Succeeded;
        }
    }
}
