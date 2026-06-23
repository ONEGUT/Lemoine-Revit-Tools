using System;
using System.Windows;
using System.Windows.Controls;
using LemoineTools.Lemoine;
using NavisApp = Autodesk.Navisworks.Api.Application;

namespace LemoineNavisworks
{
    // =========================================================================
    // HelloNavisViewModel — Phase 1 placeholder tool.
    //
    // Proves two things end-to-end:
    //   1. The shared StepFlowWindow + theme/controls render inside Navisworks.
    //   2. The tool's Run() can read the active Navisworks document directly
    //      (no ExternalEvent), and results flow into the shared Output log.
    //
    // It is a single, non-required step so Run is reachable immediately.
    // =========================================================================
    public class HelloNavisViewModel : ILemoineTool
    {
        public string Title    => "Lemoine Tools (Navisworks)";
        public string RunLabel => "Read Active Model →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Connection check", required: false),
        };

        // No interactive inputs yet, so validation never changes — but the event
        // is part of the contract and StepFlowWindow subscribes to it.
        public event EventHandler? ValidationChanged;

        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "S1")
            {
                var tb = new TextBlock
                {
                    Text = "Phase 1 smoke test.\n\n"
                         + "This confirms the shared Lemoine UI renders inside Navisworks. "
                         + "Click “Read Active Model” to read the open document and "
                         + "list its loaded models in the Output log below.",
                    TextWrapping = TextWrapping.Wrap,
                };
                // Theme-driven styling only — never hard-coded colours/sizes.
                tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                tb.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_MD");
                return tb;
            }
            return null;
        }

        public bool IsValid(string stepId) => true;

        public string SummaryFor(string stepId) => "Ready";

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            int pass = 0, fail = 0;
            try
            {
                pushLog("Navisworks plugin loaded — shared Lemoine UI is live.", "pass");
                pass++;

                var doc = NavisApp.ActiveDocument;
                if (doc == null || doc.IsClear)
                {
                    pushLog("No model open. Open an NWF/NWD/NWC and run again.", "info");
                }
                else
                {
                    string title = string.IsNullOrWhiteSpace(doc.Title) ? "(untitled)" : doc.Title;
                    pushLog($"Active document: {title}", "pass");
                    pass++;

                    int count = 0;
                    try { count = doc.Models.Count; }
                    catch (Exception ex) { LemoineLog.Swallowed("HelloNavis.Run: models count", ex); }
                    pushLog($"Loaded models: {count}", count > 0 ? "pass" : "info");
                    if (count > 0) pass++;
                }

                onProgress(100, pass, fail, 0);
            }
            catch (Exception ex)
            {
                pushLog("Error reading document: " + ex.Message, "fail");
                LemoineLog.Error("HelloNavis.Run", ex);
                fail++;
            }
            onComplete(pass, fail, 0);
        }
    }
}
