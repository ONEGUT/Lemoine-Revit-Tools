using System;
using System.Collections.Generic;

namespace LemoineTools.Lemoine
{
    // =========================================================================
    // LemoineFailureCapture (host-agnostic half)
    //
    // Per-run de-dup state + BeginRun(). This file contains NO Revit references
    // so it can be linked into the Navisworks build alongside StepFlowWindow.
    //
    // The Revit-specific event handlers (FailuresProcessing / DialogBoxShowing),
    // which surface Revit's transaction failures and modal dialogs into the
    // active run's Output log, live in LemoineFailureCapture.Revit.cs and are
    // compiled only into the Revit (LemoineTools) assembly.
    // =========================================================================
    public static partial class LemoineFailureCapture
    {
        // Per-run de-dup so a repeated warning text isn't logged on every regen.
        // Shared with the Revit partial (same class).
        private static readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private static readonly object _gate = new object();

        /// <summary>Called by StepFlowWindow at the start of a run to reset de-dup state.</summary>
        public static void BeginRun()
        {
            lock (_gate) _seen.Clear();
        }
    }
}
