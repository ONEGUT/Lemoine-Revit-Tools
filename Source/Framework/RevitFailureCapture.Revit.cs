using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI.Events;

namespace LemoineTools.Framework
{
    // =========================================================================
    // RevitFailureCapture — the Revit-typed half (see RevitFailureCapture.cs for
    // the shared state and the contract). EXCLUDED from LemoineNavisworks.csproj:
    // everything here touches the Revit API, which that host does not have.
    // =========================================================================
    public static partial class RevitFailureCapture
    {
        // ── Transaction failures ─────────────────────────────────────────────
        public static void OnFailuresProcessing(object? sender, FailuresProcessingEventArgs e)
        {
            if (!RunLogSink.IsActive) return;

            FailuresAccessor fa;
            try { fa = e.GetFailuresAccessor(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("RevitFailureCapture: get accessor", ex); return; }

            IList<FailureMessageAccessor> messages;
            try { messages = fa.GetFailureMessages(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("RevitFailureCapture: get messages", ex); return; }

            foreach (var msg in messages.ToList())
            {
                FailureSeverity sev;
                try { sev = msg.GetSeverity(); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("RevitFailureCapture: severity", ex); sev = FailureSeverity.Warning; }

                string desc;
                try { desc = msg.GetDescriptionText(); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("RevitFailureCapture: description", ex); desc = "(unreadable failure)"; }

                bool isWarning = sev == FailureSeverity.Warning;
                string line = $"Revit {(isWarning ? "warning" : "error")}: {desc}";

                if (NoteOnce(line))
                {
                    RunLogSink.Push(line, isWarning ? "warn" : "fail");
                    if (isWarning) DiagnosticsLog.Warn("RevitFailure", desc);
                    else           DiagnosticsLog.Error("RevitFailure", new Exception(desc));
                }

                if (isWarning)
                {
                    // Resolve the warning so Revit does not pop its dialog for it.
                    try { fa.DeleteWarning(msg); }
                    catch (Exception ex) { DiagnosticsLog.Swallowed("RevitFailureCapture: delete warning", ex); }
                }
                // Errors are left in place: Revit shows its (now on-top) error dialog and the
                // user decides. We do not silently swallow real errors.
            }

            // Continue with Revit's default processing for whatever remains (errors). Warnings
            // we deleted are gone, so no warning dialog appears.
            try { e.SetProcessingResult(FailureProcessingResult.Continue); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("RevitFailureCapture: set result", ex); }
        }

        // ── Modal dialogs (TaskDialogs etc.) ─────────────────────────────────
        public static void OnDialogBoxShowing(object? sender, DialogBoxShowingEventArgs e)
        {
            if (!RunLogSink.IsActive) return;

            string detail;
            if (e is TaskDialogShowingEventArgs td)
            {
                string id = SafeId(() => td.DialogId);
                string message = "";
                try { message = td.Message ?? ""; } catch (Exception ex) { DiagnosticsLog.Swallowed("RevitFailureCapture: dialog message", ex); }
                detail = string.IsNullOrWhiteSpace(message) ? id : $"{id} — {message}";
            }
            else
            {
                detail = SafeId(() => e.DialogId);
            }

            if (string.IsNullOrWhiteSpace(detail)) detail = "(unnamed dialog)";
            RunLogSink.Push($"Revit dialog: {detail}", "warn");
            // Not dismissed — it shows on top of the tool window (window owner = Revit main).
        }
    }
}
