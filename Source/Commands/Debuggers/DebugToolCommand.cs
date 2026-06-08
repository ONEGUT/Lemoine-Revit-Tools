using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Debuggers;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens a debug harness from the reserved Developer-panel slot. Currently wired to the
    /// ScrollProbeViewModel (scroll-mechanics investigation). Swap the ViewModel in Execute to
    /// MotionTestViewModel / CrashProbeViewModel / SourceIngestProbeViewModel to re-run an earlier
    /// harness (per CLAUDE.md "Crashes & Large Ambiguous Issues — Build a Debugger First").
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class DebugToolCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (_window != null && _window.IsVisible) { _window.Activate(); return Result.Succeeded; }

            // Active investigation: scroll mechanics (dropdowns / discover menu). The probe is
            // Revit-free — no snapshot needed. Swap to `new MotionTestViewModel()` /
            // `new CrashProbeViewModel()` / `new SourceIngestProbeViewModel(SourceIngestProbe.Collect(commandData.Application))`
            // to re-run an earlier harness.
            var vm = new ScrollProbeViewModel();
            _window = new StepFlowWindow(vm);
            _window.Closed += (s, e) => _window = null;
            _window.Show();
            return Result.Succeeded;
        }
    }
}
