using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI.Events;

namespace LemoineTools.Lemoine
{
    // =========================================================================
    // LemoineFailureCapture — surfaces Revit's transaction failures and modal
    // dialogs into the active tool run's Output log.
    //
    // Subscribed ONCE in App.OnStartup to the process-wide events:
    //   ControlledApplication.FailuresProcessing  (warnings / errors in a tx)
    //   UIControlledApplication.DialogBoxShowing   (TaskDialogs etc.)
    //
    // Both fire on Revit's main thread. They only act while a Lemoine run is
    // active (LemoineRunLog.IsActive) — otherwise they return immediately and
    // never interfere with non-Lemoine transactions or dialogs.
    //
    // Behaviour (per user decision):
    //   • Warnings  → logged once, then DeleteWarning'd so no dialog pops.
    //   • Errors    → logged, then left for Revit to show its dialog. The dialog
    //                 now renders ON TOP of the tool window because StepFlowWindow
    //                 owns itself to Revit's main window, so the user can resolve it.
    //   • Dialogs   → logged (not auto-dismissed) so the user sees what appeared.
    // =========================================================================
    public static class LemoineFailureCapture
    {
        // Per-run de-dup so a repeated warning text isn't logged on every regen.
        private static readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private static readonly object _gate = new object();

        /// <summary>Called by StepFlowWindow at the start of a run to reset de-dup state.</summary>
        public static void BeginRun()
        {
            lock (_gate) _seen.Clear();
        }

        // ── Transaction failures ─────────────────────────────────────────────
        public static void OnFailuresProcessing(object? sender, FailuresProcessingEventArgs e)
        {
            if (!LemoineRunLog.IsActive) return;

            FailuresAccessor fa;
            try { fa = e.GetFailuresAccessor(); }
            catch (Exception ex) { LemoineLog.Swallowed("LemoineFailureCapture: get accessor", ex); return; }

            IList<FailureMessageAccessor> messages;
            try { messages = fa.GetFailureMessages(); }
            catch (Exception ex) { LemoineLog.Swallowed("LemoineFailureCapture: get messages", ex); return; }

            foreach (var msg in messages.ToList())
            {
                FailureSeverity sev;
                try { sev = msg.GetSeverity(); }
                catch (Exception ex) { LemoineLog.Swallowed("LemoineFailureCapture: severity", ex); sev = FailureSeverity.Warning; }

                string desc;
                try { desc = msg.GetDescriptionText(); }
                catch (Exception ex) { LemoineLog.Swallowed("LemoineFailureCapture: description", ex); desc = "(unreadable failure)"; }

                bool isWarning = sev == FailureSeverity.Warning;
                string line = $"Revit {(isWarning ? "warning" : "error")}: {desc}";

                bool firstTime;
                lock (_gate) firstTime = _seen.Add(line);
                if (firstTime)
                {
                    LemoineRunLog.Push(line, isWarning ? "warn" : "fail");
                    if (isWarning) LemoineLog.Warn("RevitFailure", desc);
                    else           LemoineLog.Error("RevitFailure", new Exception(desc));
                }

                if (isWarning)
                {
                    // Resolve the warning so Revit does not pop its dialog for it.
                    try { fa.DeleteWarning(msg); }
                    catch (Exception ex) { LemoineLog.Swallowed("LemoineFailureCapture: delete warning", ex); }
                }
                // Errors are left in place: Revit shows its (now on-top) error dialog and the
                // user decides. We do not silently swallow real errors.
            }

            // Continue with Revit's default processing for whatever remains (errors). Warnings
            // we deleted are gone, so no warning dialog appears.
            try { e.SetProcessingResult(FailureProcessingResult.Continue); }
            catch (Exception ex) { LemoineLog.Swallowed("LemoineFailureCapture: set result", ex); }
        }

        // ── Modal dialogs (TaskDialogs etc.) ─────────────────────────────────
        public static void OnDialogBoxShowing(object? sender, DialogBoxShowingEventArgs e)
        {
            if (!LemoineRunLog.IsActive) return;

            string detail;
            if (e is TaskDialogShowingEventArgs td)
            {
                string id = SafeId(() => td.DialogId);
                string message = "";
                try { message = td.Message ?? ""; } catch (Exception ex) { LemoineLog.Swallowed("LemoineFailureCapture: dialog message", ex); }
                detail = string.IsNullOrWhiteSpace(message) ? id : $"{id} — {message}";
            }
            else
            {
                detail = SafeId(() => e.DialogId);
            }

            if (string.IsNullOrWhiteSpace(detail)) detail = "(unnamed dialog)";
            LemoineRunLog.Push($"Revit dialog: {detail}", "warn");
            // Not dismissed — it shows on top of the tool window (window owner = Revit main).
        }

        private static string SafeId(Func<string> get)
        {
            try { return get() ?? ""; }
            catch (Exception ex) { LemoineLog.Swallowed("LemoineFailureCapture: dialog id", ex); return ""; }
        }
    }
}
