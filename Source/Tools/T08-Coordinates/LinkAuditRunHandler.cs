using System;
using System.Linq;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Coordinates
{
    /// <summary>
    /// Re-runs <see cref="LinkAuditCapture"/> on Revit's API thread and pushes every row into the
    /// Output log, so the report is copyable text. Fully read-only — no transaction, nothing is
    /// changed in the model.
    /// </summary>
    public sealed class LinkAuditRunHandler : IExternalEventHandler
    {
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.Coordinates.LinkAuditRunHandler";

        private void Log(string t, string s) => PushLog?.Invoke(t, s);

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null) { Log(LemoineStrings.T("setup.linkAudit.log.noDoc"), "fail"); OnComplete?.Invoke(0, 1, 0); return; }

                var data = LinkAuditCapture.Capture(doc);

                if (data.Rows.Count == 0)
                {
                    Log(LemoineStrings.T("setup.linkAudit.log.noneFound"), "warn");
                    OnProgress?.Invoke(100, 0, 0, 0);
                    OnComplete?.Invoke(0, 0, 0);
                    return;
                }

                int total = data.Rows.Count;
                int done = 0;
                int flagged = 0;
                foreach (var row in data.Rows)
                {
                    Log(LemoineStrings.T("setup.linkAudit.log.row",
                            row.Name, row.LoadStatus, row.Positioning, row.DisplayMode,
                            row.Pinned ? LemoineStrings.T("setup.linkAudit.log.pinnedYes") : LemoineStrings.T("setup.linkAudit.log.pinnedNo"),
                            row.WorksetName, row.AttachmentType, row.LastSaved),
                        row.IsWarning ? "warn" : "info");

                    if (row.IsWarning) flagged++;
                    done++;
                    OnProgress?.Invoke((int)(100.0 * done / total), total - flagged, flagged, 0);
                }

                int loaded = data.Rows.Count(r => r.LoadStatus.Equals("Loaded", StringComparison.OrdinalIgnoreCase));
                Log(LemoineStrings.T("setup.linkAudit.log.summary", total, loaded, flagged), flagged > 0 ? "warn" : "pass");

                OnProgress?.Invoke(100, total - flagged, flagged, 0);
                OnComplete?.Invoke(total - flagged, flagged, 0);
            }
            catch (Exception ex)
            {
                LemoineLog.Error("LinkAuditRunHandler.Execute", ex);
                Log(LemoineStrings.T("setup.linkAudit.log.aborted", ex.Message), "fail");
                OnComplete?.Invoke(0, 1, 0);
            }
        }
    }
}
