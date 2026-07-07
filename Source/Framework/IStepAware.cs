using System;

namespace LemoineTools.Framework
{
    /// <summary>
    /// Optional interface for tool ViewModels that need to react when
    /// the user navigates to a step — e.g. to refresh Revit-sourced data
    /// (levels, grids, reference planes) that may have changed since the
    /// tool window was opened.
    ///
    /// StepFlowWindow checks for this interface and wires it up automatically.
    /// Implement it alongside IStepFlowTool when S2 (or any step) shows live
    /// Revit data that should refresh on every activation.
    /// </summary>
    public interface IStepAware
    {
        /// <summary>
        /// Called by StepFlowWindow each time a step becomes the active step.
        /// Runs on the WPF UI thread.
        /// </summary>
        void OnStepActivated(string stepId);

        /// <summary>
        /// Gives the ViewModel a callback it can invoke to replace the content
        /// widget of a step (cs.Children[0]) with fresh WPF content.
        /// The callback dispatches to the WPF thread automatically — the
        /// ViewModel may call it from any thread.
        /// The callback signature: Action(stepId) — pass the step ID whose
        /// content should be rebuilt by calling GetStepContent(stepId) again.
        /// </summary>
        void SetContentRefreshCallback(Action<string> rebuildStepContent);
    }
}
