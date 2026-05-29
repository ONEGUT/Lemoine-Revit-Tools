using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Debuggers;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens the P0/P1 UI motion test harness from the reserved Developer-panel slot.
    /// Temporary scaffolding for verifying the interaction-bug and motion changes on
    /// Windows; the placeholder slot returns once testing is done.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class DebugToolCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (_window != null && _window.IsVisible) { _window.Activate(); return Result.Succeeded; }

            var vm = new MotionTestViewModel();
            _window = new StepFlowWindow(vm);
            // Register a second log tab so the tab bar (and the P0 hover-reset fix) is testable.
            _window.RegisterLogTab("notes", "Notes", vm.BuildNotesTab());
            _window.Closed += (s, e) => _window = null;
            _window.Show();
            return Result.Succeeded;
        }
    }
}
