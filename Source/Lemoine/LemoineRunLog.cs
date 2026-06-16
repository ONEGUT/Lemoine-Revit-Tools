using System;

namespace LemoineTools.Lemoine
{
    // =========================================================================
    // LemoineRunLog — the active run's Output-log sink.
    //
    // Process-wide Revit failure/dialog capture (LemoineFailureCapture) fires on
    // Revit's main thread and needs a way to write into whichever tool window is
    // currently running. StepFlowWindow registers its (thread-marshalling) pushLog
    // callback here when a run starts and clears it when the run ends or the window
    // closes. Revit serialises work on its single main thread, so exactly one run's
    // sink is active at a time.
    //
    // The registered callback is StepFlowWindow's own SafeBeginInvoke wrapper, so
    // pushing from the main thread is non-blocking and shutdown-guarded.
    // =========================================================================
    public static class LemoineRunLog
    {
        private static volatile Action<string, string>? _sink;

        /// <summary>True while a run has registered its log sink.</summary>
        public static bool IsActive => _sink != null;

        /// <summary>Called by StepFlowWindow when a run starts.</summary>
        public static void Set(Action<string, string> pushLog) => _sink = pushLog;

        /// <summary>Called by StepFlowWindow when the run finishes, resets, or the window closes.</summary>
        public static void Clear() => _sink = null;

        /// <summary>Route a message into the active run's Output log (no-op if none is active).</summary>
        public static void Push(string text, string status = "info")
        {
            var sink = _sink;
            if (sink == null) return;
            try { sink(text, status); }
            catch (Exception ex) { LemoineLog.Swallowed("LemoineRunLog.Push", ex); }
        }
    }
}
