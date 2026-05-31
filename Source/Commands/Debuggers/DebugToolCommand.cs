using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Debuggers;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens the motion/feature test harness (MotionTestViewModel) from the reserved
    /// Developer-panel slot. Swap the ViewModel in Execute to CrashProbeViewModel to re-run
    /// the lazy-build crash-isolation probe (per CLAUDE.md "Crashes & Large Ambiguous
    /// Issues — Build a Debugger First").
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class DebugToolCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (_window != null && _window.IsVisible) { _window.Activate(); return Result.Succeeded; }

            // Crash isolated (cross-thread CubicEase) — back to the motion/feature harness.
            // Swap to `new CrashProbeViewModel()` to re-run the lazy-build crash probe.
            var vm = new MotionTestViewModel();
            _window = new StepFlowWindow(vm);
            _window.RegisterLogTab("notes", "Notes", vm.BuildNotesTab());
            _window.Closed += (s, e) => _window = null;
            _window.Show();
            return Result.Succeeded;
        }
    }
}
