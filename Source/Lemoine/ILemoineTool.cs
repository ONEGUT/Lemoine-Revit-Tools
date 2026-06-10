using System;
using System.Collections.Generic;
using System.Windows;

namespace LemoineTools.Lemoine
{
    // =========================================================================
    // ILemoineTool — the only interface a new Revit function needs to implement
    //
    // To add a new tool to the plugin:
    //   1. Create a class that implements ILemoineTool (see ProjectedCeilingGridsViewModel.cs
    //      as the reference template — copy it and change the domain logic)
    //   2. Add a PushButtonData in App.cs pointing to your new command
    //   3. Create a lightweight command class that opens StepFlowWindow(new YourViewModel())
    //   4. Done — the window, accordion, progress bar, log, and themes are all inherited
    //
    // You do NOT touch StepFlowWindow, the controls, or LemoineTheme.
    // =========================================================================

    /// <summary>
    /// Metadata for a single step in the accordion.
    /// </summary>
    public sealed class StepDefinition
    {
        public string Id       { get; }
        public string Title    { get; }
        public bool   Required { get; }

        public StepDefinition(string id, string title, bool required = true)
        {
            Id       = id;
            Title    = title;
            Required = required;
        }
    }

    /// <summary>
    /// The contract every Lemoine tool must implement.
    /// StepFlowWindow drives all UI from this interface alone.
    /// </summary>
    public interface ILemoineTool
    {
        // ── Identity ─────────────────────────────────────────────────────────
        /// <summary>Title shown in the toolbar (e.g. "Projected Ceiling Grids").</summary>
        string Title { get; }

        /// <summary>Label on the final action button (e.g. "Run in Revit →").</summary>
        string RunLabel { get; }

        // ── Step definitions ──────────────────────────────────────────────────
        /// <summary>
        /// Ordered list of steps.  The window renders them as an accordion;
        /// the last step's Confirm button triggers Run().
        /// </summary>
        StepDefinition[] Steps { get; }

        // ── Per-step content ──────────────────────────────────────────────────
        /// <summary>
        /// Return the WPF input control(s) for a given step.
        /// Called once per step when the window opens.  The returned element is
        /// placed inside the accordion's expandable content area.
        ///
        /// Use the Lemoine input controls (LemoineFileBrowser, LemoineSingleSelect,
        /// etc.) for consistent theming.  Bind values to your viewmodel's fields
        /// via event handlers or DependencyProperty bindings.
        /// </summary>
        FrameworkElement? GetStepContent(string stepId);

        // ── Validation ────────────────────────────────────────────────────────
        /// <summary>
        /// Return true if the step's current value is acceptable.
        /// When a required step returns false, the Confirm button is disabled.
        /// Called on every property change.
        /// </summary>
        bool IsValid(string stepId);

        // ── Collapsed summary ─────────────────────────────────────────────────
        /// <summary>
        /// Return a one-line string shown beneath the step title when the step is
        /// collapsed (e.g. "ceiling-plan.dwg", "12 levels selected").
        /// </summary>
        string SummaryFor(string stepId);

        // ── Execution ─────────────────────────────────────────────────────────
        /// <summary>
        /// Called when the user clicks the run button on the final step.
        ///
        /// This method runs on the WPF UI thread.  Do NOT call Revit API here.
        /// Instead: set your ExternalEventHandler's pending parameters, then call
        /// ExternalEvent.Raise().  The handler will call the callbacks when done.
        ///
        /// Callbacks:
        ///   pushLog(text, status)             — add a log entry ("pass","fail","info")
        ///   onProgress(pct, pass, fail, skip) — update the progress bar
        ///   onComplete(pass, fail, skip)       — mark the run as finished
        /// </summary>
        void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete);

        // ── Validation change notification ────────────────────────────────────
        /// <summary>
        /// Raised by the tool whenever a step value changes and the window should
        /// re-check validation.  StepFlowWindow subscribes to this automatically.
        /// </summary>
        event EventHandler? ValidationChanged;
    }

    /// <summary>
    /// Optional interface for tools whose step list is conditional — i.e. some steps
    /// should be hidden entirely depending on earlier choices (e.g. a "PDF Settings"
    /// step that only applies when PDF output is enabled).
    ///
    /// StepFlowWindow hides a step's accordion row and progress pip when
    /// <see cref="IsStepVisible"/> returns false, and skips it during forward/back
    /// navigation. Visibility is re-evaluated on every step activation and whenever the
    /// tool raises <see cref="ILemoineTool.ValidationChanged"/>, so toggling the
    /// condition updates the accordion live.
    ///
    /// Contract: a conditional (hideable) step must never be the LAST step — the final
    /// step carries the Run button, log area, and review summary and is always shown.
    /// Tools that do not implement this interface are unaffected (every step is visible).
    /// </summary>
    public interface ILemoineConditionalSteps
    {
        /// <summary>Return false to hide the step with this id from the accordion and navigation.</summary>
        bool IsStepVisible(string stepId);
    }

    /// <summary>
    /// Optional cleanup hook called by StepFlowWindow when its window closes. Tools that wire
    /// callbacks onto a long-lived (static) ExternalEvent handler should null them here so the
    /// closed window's ViewModel doesn't stay rooted by the handler until the next run.
    /// </summary>
    public interface ILemoineToolCleanup
    {
        void OnWindowClosed();
    }

    /// <summary>
    /// Optional interface for tools that need to drive accordion navigation programmatically.
    /// StepFlowWindow subscribes when the tool implements this interface.
    /// </summary>
    public interface ILemoineNavigable
    {
        /// <summary>Raised with the target step index to trigger navigation from within step content.</summary>
        event EventHandler<int> NavigateRequested;
    }

    /// <summary>
    /// Optional interface for tools that need to customise the per-step Confirm button label
    /// or hook into the Confirm click before StepFlowWindow advances to the next step.
    /// </summary>
    public interface ILemoineStepConfirmable
    {
        /// <summary>Return a custom label for the Confirm button on the given step, or null for the default "Confirm →".</summary>
        string? ConfirmLabelFor(string stepId);
        /// <summary>Called by StepFlowWindow when the user clicks Confirm on the given step, before navigation occurs.</summary>
        void OnStepConfirm(string stepId);
    }
}
