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

        /// <summary>True once the user has clicked Cancel for the current run.</summary>
        public static bool CancelRequested => _cancelRequested;

        /// <summary>
        /// Log a line AND test for cancellation in one call. Returns true when the caller
        /// should stop now (preserving the work done so far). This is the canonical break
        /// point: wherever a handler reports per-item progress to the log inside a processing
        /// loop, route it through here, e.g.
        ///
        ///   if (LemoineRun.Checkpoint(pushLog, $"✓ {name}", "pass"))
        ///   {
        ///       pushLog($"Stopped by user — {done} of {total} processed; work so far preserved.", "warn");
        ///       break;   // falls through to the existing Commit() so done work is kept
        ///   }
        /// </summary>
        public static bool Checkpoint(Action<string, string> pushLog, string msg, string status = "info")
        {
            pushLog?.Invoke(msg, status);
            return _cancelRequested;
        }
    }
}
