using System;
using System.Collections.Generic;
using System.Windows;

namespace LemoineTools.Framework
{
    // =========================================================================
    // IStepFlowTool — the only interface a new Revit function needs to implement
    //
    // To add a new tool to the plugin:
    //   1. Create a class that implements IStepFlowTool (see ProjectedCeilingGridsViewModel.cs
    //      as the reference template — copy it and change the domain logic)
    //   2. Add a PushButtonData in App.cs pointing to your new command
    //   3. Create a lightweight command class that opens StepFlowWindow(new YourViewModel())
    //   4. Done — the window, accordion, progress bar, log, and themes are all inherited
    //
    // You do NOT touch StepFlowWindow, the controls, or ThemePalette.
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
    public interface IStepFlowTool
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
        /// Use the Lemoine input controls (FileBrowser, SingleSelect,
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
    /// tool raises <see cref="IStepFlowTool.ValidationChanged"/>, so toggling the
    /// condition updates the accordion live.
    ///
    /// Contract: a conditional (hideable) step must never be the LAST step — the final
    /// step carries the Run button, log area, and review summary and is always shown.
    /// Tools that do not implement this interface are unaffected (every step is visible).
    /// </summary>
    public interface IConditionalSteps
    {
        /// <summary>Return false to hide the step with this id from the accordion and navigation.</summary>
        bool IsStepVisible(string stepId);
    }

    /// <summary>
    /// Optional cleanup hook called by StepFlowWindow when its window closes. Tools that wire
    /// callbacks onto a long-lived (static) ExternalEvent handler should null them here so the
    /// closed window's ViewModel doesn't stay rooted by the handler until the next run.
    /// </summary>
    public interface IToolCleanup
    {
        void OnWindowClosed();
    }

    /// <summary>
    /// Optional interface for tools that need to drive accordion navigation programmatically.
    /// StepFlowWindow subscribes when the tool implements this interface.
    /// </summary>
    public interface IStepNavigable
    {
        /// <summary>Raised with the target step index to trigger navigation from within step content.</summary>
        event EventHandler<int> NavigateRequested;
    }

    /// <summary>
    /// Optional interface for tools that need to customise the per-step Confirm button label
    /// or hook into the Confirm click before StepFlowWindow advances to the next step.
    /// </summary>
    public interface IStepConfirmable
    {
        /// <summary>Return a custom label for the Confirm button on the given step, or null for the default "Confirm →".</summary>
        string? ConfirmLabelFor(string stepId);
        /// <summary>Called by StepFlowWindow when the user clicks Confirm on the given step, before navigation occurs.</summary>
        void OnStepConfirm(string stepId);
    }

    /// <summary>
    /// Optional interface for a run that must pause on a manual step it cannot detect the
    /// completion of automatically (e.g. a native Revit dialog posted via
    /// <c>UIApplication.PostCommand</c>, which Revit does not report cancellation of). While
    /// paused, StepFlowWindow shows two extra footer buttons — "Continue" and "Skip" — next to
    /// the normal Reset/Cancel button, calling <see cref="ContinueRun"/> / <see cref="SkipCurrentItem"/>
    /// on click. The existing Cancel button also calls <see cref="SkipCurrentItem"/> first when a
    /// pause is active, since no running loop is left to observe <see cref="RunState.CancelRequested"/>
    /// while paused. Tools that don't implement this interface are unaffected (buttons never shown).
    /// </summary>
    public interface IRunPausable
    {
        /// <summary>Raised when the tool starts/stops waiting on a manual step. Args: is-awaiting,
        /// the Continue button label (or null for the default), the Skip button label (or null
        /// for the default). Raised from whatever thread the run is on — StepFlowWindow marshals.</summary>
        event Action<bool, string?, string?>? AwaitingUserChanged;

        /// <summary>User clicked "Continue" — the manual step is done; resume the run.</summary>
        void ContinueRun();

        /// <summary>User clicked "Skip" (or Cancel, mid-pause) — abandon the current item and resume.</summary>
        void SkipCurrentItem();
    }
}
