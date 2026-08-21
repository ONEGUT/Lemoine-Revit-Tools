using System;

namespace LemoineTools.Framework
{
    // =========================================================================
    // ToolReloadBridge — host-neutral seam for StepFlowWindow's "Reload" action.
    //
    // Reload rebuilds the tool's ViewModel from a fresh read of the model. WHERE
    // that read is allowed to happen differs per host, and StepFlowWindow is
    // shared by both:
    //
    //   • Revit — a tool window lives on its own STA thread and may not touch the
    //     Revit API from there, so the factory must be marshalled onto Revit's
    //     main thread through an ExternalEvent (App.ReloadHandler/ReloadEvent).
    //   • Navisworks — plugin code already runs on the main STA thread and the
    //     API is callable directly, so the factory is simply invoked in place.
    //
    // Each host installs its own <see cref="Marshal"/> at startup. StepFlowWindow
    // never references App, which is what lets it compile into the Navisworks
    // add-in (which has no Revit API at all).
    //
    // Left unset, Reload degrades to a plain Reset — StepFlowWindow checks
    // <see cref="IsAvailable"/> and logs the fallback rather than doing nothing.
    // =========================================================================
    public static class ToolReloadBridge
    {
        /// <summary>
        /// Rebuilds a tool wherever the host allows model reads, then hands the result back.
        /// Args: the factory to run, and the callback to receive the new tool (null on failure —
        /// the host is responsible for logging why before calling back).
        /// </summary>
        public static Action<Func<IStepFlowTool>, Action<IStepFlowTool?>>? Marshal { get; set; }

        /// <summary>False when no host has installed a marshaller; Reload then falls back to Reset.</summary>
        public static bool IsAvailable => Marshal != null;

        /// <summary>Runs the rebuild through the installed marshaller. A throw from the host's
        /// marshaller is reported through <paramref name="onBuilt"/>(null) rather than escaping
        /// into a click handler, where it would reach the dispatcher unhandled.</summary>
        public static void Rebuild(Func<IStepFlowTool> factory, Action<IStepFlowTool?> onBuilt)
        {
            if (factory == null || onBuilt == null) return;

            var marshal = Marshal;
            if (marshal == null)
            {
                DiagnosticsLog.Warn("ToolReloadBridge", "no marshaller installed; reload cannot run.");
                onBuilt(null);
                return;
            }

            try { marshal(factory, onBuilt); }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ToolReloadBridge.Rebuild", ex);
                onBuilt(null);
            }
        }
    }
}
