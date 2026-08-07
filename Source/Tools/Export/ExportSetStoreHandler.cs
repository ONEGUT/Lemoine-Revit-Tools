using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;

namespace LemoineTools.Tools.BulkExport
{
    /// <summary>
    /// Writes Bulk Export's set layout into the active document. Extensible Storage needs Revit's
    /// main thread and an open transaction, so the save runs through this ExternalEvent rather
    /// than directly from the tool window's STA thread.
    ///
    /// Deliberately NOT fired on every edit — a transaction per drag would be slow and, on a
    /// workshared model, a sync-to-central storm. The ViewModel raises this only on an explicit
    /// "Save sets" and at the start of a run.
    /// </summary>
    public sealed class ExportSetStoreHandler : IExternalEventHandler
    {
        /// <summary>The layout to persist. Set before <c>Raise()</c>.</summary>
        public ExportSetLayout? Layout { get; set; }

        /// <summary>Invoked on the Revit thread after a successful commit — marshal before touching WPF.</summary>
        public Action? OnSaved { get; set; }

        /// <summary>Invoked with a user-facing reason when the layout could not be stored.</summary>
        public Action<string>? OnError { get; set; }

        public string GetName() => "LemoineTools.Tools.BulkExport.ExportSetStoreHandler";

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    OnError?.Invoke(AppStrings.T("export.bulkExport.log.noDoc"));
                    return;
                }

                var layout = Layout;
                if (layout == null)
                {
                    // Nothing to write is a programming error, not a user one — report it rather
                    // than returning quietly and leaving the Save button looking like it worked.
                    DiagnosticsLog.Warn("ExportSetStoreHandler", "raised with no layout payload");
                    OnError?.Invoke(AppStrings.T("export.bulkExport.sets.saveFailed", "no layout"));
                    return;
                }

                if (!ExportSetStore.CanPersist(doc, out string reason))
                {
                    OnError?.Invoke(reason);
                    return;
                }

                using (var tx = new Transaction(doc, "Save Bulk Export Sets"))
                {
                    tx.Start();
                    ExportSetStore.Write(doc, layout);
                    tx.Commit();
                }

                OnSaved?.Invoke();
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ExportSetStoreHandler: save set layout", ex);
                OnError?.Invoke(AppStrings.T("export.bulkExport.sets.saveFailed", ex.Message));
            }
            finally
            {
                // Session-long static handler (App.BulkExportSetStoreHandler) — drop the payload
                // so a closed window's layout is not retained for the rest of the Revit session.
                Layout = null;
            }
        }
    }
}
