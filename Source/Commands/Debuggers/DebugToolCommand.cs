using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Debuggers;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens the crash-isolation probe harness (CrashProbeViewModel) from the reserved
    /// Developer-panel slot. Each step lazily builds one suspect WPF construct so the
    /// button-press that crashes Revit names the culprit (per CLAUDE.md "Crashes & Large
    /// Ambiguous Issues — Build a Debugger First"). Swap the ViewModel here to run the
    /// motion harness (MotionTestViewModel) instead.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class DebugToolCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (_window != null && _window.IsVisible) { _window.Activate(); return Result.Succeeded; }

            var vm = new CrashProbeViewModel();
            _window = new StepFlowWindow(vm);
            _window.Closed += (s, e) => _window = null;
            _window.Show();
            return Result.Succeeded;
        }
    }
}
