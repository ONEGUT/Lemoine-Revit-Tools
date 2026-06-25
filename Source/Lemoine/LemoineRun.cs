using System;

namespace LemoineTools.Lemoine
{
    // =========================================================================
    // LemoineRun — cooperative cancellation for running tools.
    //
    // A tool's work runs inside IExternalEventHandler.Execute on Revit's main
    // thread, looping over items and reporting to the Output log as it goes.
    // The footer Cancel button (StepFlowWindow) lives on the window's own STA
    // UI thread. This static carries the stop request across the two threads.
    //
    // Revit serialises every ExternalEvent.Execute on its single main thread, so
    // exactly one handler runs at a time — a process-wide volatile flag is the
    // right granularity. The flag is reset at the start of every run.
    //
    // Work is preserved by a COOPERATIVE BREAK: a handler that sees a cancel
    // request finishes the current item, breaks its loop, and falls through to
    // its existing Transaction.Commit(). Never throw out of the loop — that
    // disposes the transaction uncommitted and loses the work already done.
    // =========================================================================
    public static class LemoineRun
    {
        private static volatile bool _cancelRequested;

        /// <summary>Called by StepFlowWindow just before a run starts. Clears any prior request.</summary>
        public static void Begin() => _cancelRequested = false;

        /// <summary>Called by the Cancel button (UI thread) to request the running function stop.</summary>
        public static void RequestCancel() => _cancelRequested = true;

        /// <summary>Called by StepFlowWindow when the run finishes or the window is reset.</summary>
        public static void End() => _cancelRequested = false;

        /// <summary>
        /// True once the user has clicked Cancel for the current run. A looping handler
        /// tests this at its per-progress log point, logs a "Stopped by user — N of M
        /// processed; work so far preserved" line, then <c>break</c>s and falls through
        /// to its existing <c>Transaction.Commit()</c> so committed work is kept. Use
        /// <see cref="RunProgressReporter"/> for the steady 5% log cadence; this flag is
        /// only the stop signal.
        /// </summary>
        public static bool CancelRequested => _cancelRequested;
    }
}
