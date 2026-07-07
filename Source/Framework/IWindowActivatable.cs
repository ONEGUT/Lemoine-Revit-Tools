using System;

namespace LemoineTools.Framework
{
    /// <summary>
    /// Optional interface for tool ViewModels that trigger a Revit interaction which steals
    /// focus from the wizard — e.g. a <c>PickObject</c> slab pick raises a Revit ExternalEvent,
    /// and Revit's main window comes to the foreground for the pick. After the pick resolves the
    /// wizard is left behind Revit; this hook lets the tool bring its host window back to front.
    ///
    /// StepFlowWindow checks for this interface and supplies the activate callback automatically,
    /// alongside the existing <see cref="IStepAware"/> content-refresh wiring.
    /// </summary>
    public interface IWindowActivatable
    {
        /// <summary>
        /// Gives the ViewModel a callback that activates and focuses the host window.
        /// The callback dispatches to the WPF thread automatically — the ViewModel may
        /// call it from any thread.
        /// </summary>
        void SetActivateCallback(Action activateWindow);
    }
}
