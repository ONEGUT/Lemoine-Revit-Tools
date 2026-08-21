using System;
using System.Collections.Generic;

namespace LemoineTools.Framework
{
    // =========================================================================
    // RevitFailureCapture — surfaces Revit's transaction failures and modal
    // dialogs into the active tool run's Output log.
    //
    // Subscribed ONCE in App.OnStartup to the process-wide events:
    //   ControlledApplication.FailuresProcessing  (warnings / errors in a tx)
    //   UIControlledApplication.DialogBoxShowing   (TaskDialogs etc.)
    //
    // Both fire on Revit's main thread. They only act while a Lemoine run is
    // active (RunLogSink.IsActive) — otherwise they return immediately and
    // never interfere with non-Lemoine transactions or dialogs.
    //
    // Behaviour (per user decision):
    //   • Warnings  → logged once, then DeleteWarning'd so no dialog pops.
    //   • Errors    → logged, then left for Revit to show its dialog. The dialog
    //                 now renders ON TOP of the tool window because StepFlowWindow
    //                 owns itself to Revit's main window, so the user can resolve it.
    //   • Dialogs   → logged (not auto-dismissed) so the user sees what appeared.
    //
    // SPLIT ACROSS TWO FILES. This half is Revit-free so it can compile into the
    // Navisworks add-in, which links the same Source/Framework tree but has no
    // Revit API to reference; the Revit event handlers live in
    // RevitFailureCapture.Revit.cs, which that project excludes. StepFlowWindow
    // only ever calls BeginRun(), so the shared window compiles in either host.
    // =========================================================================
    public static partial class RevitFailureCapture
    {
        // Per-run de-dup so a repeated warning text isn't logged on every regen.
        private static readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private static readonly object _gate = new object();

        /// <summary>Called by StepFlowWindow at the start of a run to reset de-dup state.</summary>
        public static void BeginRun()
        {
            lock (_gate) _seen.Clear();
        }

        /// <summary>Logs <paramref name="line"/> to the run's Output log the FIRST time it is
        /// seen this run, and reports whether it was new. Shared by the Revit handlers.</summary>
        private static bool NoteOnce(string line)
        {
            lock (_gate) return _seen.Add(line);
        }

        private static string SafeId(Func<string> get)
        {
            try { return get() ?? ""; }
            catch (Exception ex) { DiagnosticsLog.Swallowed("RevitFailureCapture: dialog id", ex); return ""; }
        }
    }
}
