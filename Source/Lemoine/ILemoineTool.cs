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
}
