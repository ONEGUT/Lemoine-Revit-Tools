using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Debuggers
{
    /// <summary>
    /// Phase 2b pilot handler: counts elements of a chosen category in the active document.
    /// Read-only (no transaction) - the point is to prove the WebStepFlowWindow run lifecycle
    /// (ExternalEvent raise, RunState/RunLogSink/RevitFailureCapture, marshalled log/progress/
    /// complete callbacks) end-to-end through HTML, not to mutate the model. Per-run payload is
    /// cleared in finally so nothing outlives the run on this static handler.
    /// </summary>
    public class WebPilotEventHandler : IExternalEventHandler
    {
        public string?                    CategoryToken { get; set; }
        public Action<string, string>?     OnLog        { get; set; }
        public Action<int, int, int, int>? OnProgress   { get; set; }
        public Action<int, int, int>?      OnComplete   { get; set; }

        public string GetName() => "WebPilotCount";

        public void Execute(UIApplication app)
        {
            var pushLog    = OnLog      ?? ((m, s) => { });
            var onProgress = OnProgress ?? ((p, a, b, c) => { });
            var onComplete = OnComplete ?? ((a, b, c) => { });
            int counted = 0;

            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null) { pushLog("No active document.", "fail"); onComplete(0, 1, 0); return; }

                if (!TryMapCategory(CategoryToken, out var bic))
                {
                    pushLog($"Unknown category '{CategoryToken}'.", "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                var elements = new FilteredElementCollector(doc)
                    .OfCategory(bic).WhereElementIsNotElementType().ToElements();
                int total = elements.Count;
                // Survey/collector rule: always report the count, even zero, so a silent empty
                // result is never mistaken for a broken run.
                pushLog($"Found {total} element(s) of category '{CategoryToken}'.", "info");

                // Report in ~5 steps for the steady progress cadence + a cancellation checkpoint.
                const int batches = 5;
                for (int i = 1; i <= batches; i++)
                {
                    if (RunState.CancelRequested)
                    {
                        pushLog($"Stopped by user - {counted} of {total} counted; work so far preserved.", "warn");
                        break;
                    }
                    counted = (int)Math.Ceiling(total * (i / (double)batches));
                    int pct = i * 100 / batches;
                    onProgress(pct, counted, 0, 0);
                    pushLog($"{pct}% - {counted} of {total} counted", "pass");
                }

                onComplete(counted, 0, 0);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("WebPilot: count elements", ex);
                pushLog($"Error: {ex.Message}", "fail");
                onComplete(counted, 1, 0);
            }
            finally
            {
                // Static handler lives for the whole session - never leave per-run state on it.
                OnLog = null; OnProgress = null; OnComplete = null; CategoryToken = null;
            }
        }

        private static bool TryMapCategory(string? token, out BuiltInCategory bic)
        {
            switch (token)
            {
                case "walls":    bic = BuiltInCategory.OST_Walls;    return true;
                case "doors":    bic = BuiltInCategory.OST_Doors;    return true;
                case "windows":  bic = BuiltInCategory.OST_Windows;  return true;
                case "floors":   bic = BuiltInCategory.OST_Floors;   return true;
                case "ceilings": bic = BuiltInCategory.OST_Ceilings; return true;
                default:         bic = BuiltInCategory.INVALID;      return false;
            }
        }
    }
}
