using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace LemoineTools.Framework.Project
{
    // =========================================================================
    // ProjectLibrarySaveHandler — commits trade / legend / clash libraries into
    // the document.
    //
    // Extensible-storage writes need an open transaction on the Revit main
    // thread, and tool windows run on their own STA threads with no document.
    // So a window stages its section and raises this event; Revit runs it at the
    // next idle moment, after the window has closed.
    //
    // Two things this deliberately does NOT do:
    //   • It does not write when nothing changed. Opening a tool and closing it
    //     must not dirty the model or add an undo entry.
    //   • It does not resolve the document itself beyond checking that the one
    //     staged is still the active one. Staging records WHICH document the
    //     payload belongs to, because the user can switch models between closing
    //     a window and this event firing — writing A's library into B is exactly
    //     the leak this whole rework exists to stop.
    // =========================================================================
    public class ProjectLibrarySaveHandler : IExternalEventHandler
    {
        private static readonly object _gate = new object();
        private static readonly Dictionary<string, string> _staged =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static string? _stagedDocKey;

        /// <summary>
        /// Queues one library section for the document it was edited in. Safe to call from a
        /// tool window's STA thread. Staging a section for a different document than the one
        /// already staged flushes nothing — it replaces, because the previous window's write
        /// either already ran or was for a model the user has left.
        /// </summary>
        public static void Stage(string section, string xml, string? docKey)
        {
            lock (_gate)
            {
                if (!string.Equals(_stagedDocKey, docKey, StringComparison.OrdinalIgnoreCase))
                {
                    _staged.Clear();
                    _stagedDocKey = docKey;
                }
                _staged[section] = xml ?? "";
            }
        }

        /// <summary>True when there is anything queued.</summary>
        public static bool HasStaged
        {
            get { lock (_gate) return _staged.Count > 0; }
        }

        public string GetName() => "LemoineTools.Framework.Project.ProjectLibrarySaveHandler";

        public void Execute(UIApplication app)
        {
            Dictionary<string, string> sections;
            string? forDocKey;
            lock (_gate)
            {
                if (_staged.Count == 0) return;
                sections      = new Dictionary<string, string>(_staged, StringComparer.Ordinal);
                forDocKey     = _stagedDocKey;
                _staged.Clear();
                _stagedDocKey = null;
            }

            try
            {
                var doc = app?.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    DiagnosticsLog.Warn("ProjectLibrarySave",
                        "No active document when the save fired — project libraries were not written.");
                    return;
                }

                // The user may have switched models since the window closed.
                string? activeKey = DocumentKey.For(doc);
                if (!string.Equals(activeKey, forDocKey, StringComparison.OrdinalIgnoreCase))
                {
                    DiagnosticsLog.Warn("ProjectLibrarySave",
                        $"Active document changed before the save ran (edited '{forDocKey}', now '{activeKey}') — " +
                        "libraries were NOT written, rather than written into the wrong model.");
                    return;
                }

                if (!ProjectLibraryStore.CanWrite(doc, out string? why))
                {
                    DiagnosticsLog.Warn("ProjectLibrarySave",
                        $"Project libraries could not be saved: {why}.");
                    return;
                }

                using (var tx = new Transaction(doc, "Lemoine — Save Project Libraries"))
                {
                    tx.Start();
                    bool ok = ProjectLibraryStore.Write(doc, sections);
                    if (ok) tx.Commit();
                    else
                    {
                        tx.RollBack();
                        DiagnosticsLog.Warn("ProjectLibrarySave",
                            "Write reported failure — the transaction was rolled back and nothing was saved.");
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ProjectLibrarySave: commit", ex);
            }
        }
    }
}
