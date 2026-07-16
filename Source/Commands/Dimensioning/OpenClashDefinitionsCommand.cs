using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Tools.Dimensioning;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens the Clash Definitions library window. Gathers line-style names and the
    /// source-document list on Revit's main thread, then opens the window on a dedicated
    /// STA thread (re-activating an already-open instance).
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenClashDefinitionsCommand : IExternalCommand
    {
        private static ClashDefinitionsWindow? _window;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (_window != null)
            {
                try
                {
                    _window.Dispatcher.Invoke(() =>
                    {
                        if (_window.IsVisible) _window.Activate();
                        else _window = null;
                    });
                    if (_window != null) return Result.Succeeded;
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed("OpenClashDefinitionsCommand: reactivate existing window", ex);
                    _window = null;
                }
            }

            var doc = commandData.Application.ActiveUIDocument?.Document;

            // Capture the exact filterable-category list so the clash group editor's category
            // picker mirrors Revit's own "Edit Filters → Categories" list (main-thread read).
            if (doc != null)
                LemoineTools.Tools.AutoFilters.AutoFiltersSettings.CaptureFilterableCategories(doc);

            // Line style names (cross-line style picker).
            var lineStyleNames = new List<string>();
            if (doc != null)
            {
                var linesCat = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                if (linesCat != null)
                {
                    foreach (Category sub in linesCat.SubCategories)
                        lineStyleNames.Add(sub.Name);
                    lineStyleNames.Sort(StringComparer.OrdinalIgnoreCase);
                }
            }

            // Host phase names in sequence order (for the Specific-phase dropdown).
            var phaseNames = new List<string>();
            if (doc != null)
            {
                try
                {
                    foreach (Phase ph in doc.Phases) phaseNames.Add(ph.Name);
                }
                catch (Exception ex) { DiagnosticsLog.Swallowed("OpenClashDefinitionsCommand: read host phases", ex); }
            }

            // Source documents available to each clash group (host + each loaded link),
            // each carrying its user worksets for the per-document workset checklist.
            var docs = new List<ClashDocInfo>();
            if (doc != null)
            {
                docs.Add(new ClashDocInfo
                {
                    Name       = "(Host) " + doc.Title,
                    LinkInstId = 0L,
                    Worksets   = ReadUserWorksets(doc),
                });
                foreach (var li in new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    var ld = li.GetLinkDocument();
                    if (ld == null) continue;
                    docs.Add(new ClashDocInfo
                    {
                        Name       = "[" + Path.GetFileNameWithoutExtension(ld.Title) + "]",
                        LinkInstId = li.Id.Value,
                        Worksets   = ReadUserWorksets(ld),
                    });
                }
            }

            var ready = new ManualResetEventSlim(false);
            ClashDefinitionsWindow? win = null;

            var thread = new Thread(() =>
            {
                win = new ClashDefinitionsWindow();
                win.SetContext(lineStyleNames, docs, phaseNames, App.ClashPickHandler, App.ClashPickEvent);
                win.Closed += (s, e) =>
                {
                    _window = null;
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                };
                win.Show();
                ready.Set();
                Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            ready.Wait();
            _window = win;
            return Result.Succeeded;
        }

        /// <summary>User worksets of a document, sorted by name. Empty when the document is not
        /// workshared (or the read throws on a detached/closed link).</summary>
        private static List<ClashWorksetInfo> ReadUserWorksets(Document d)
        {
            var list = new List<ClashWorksetInfo>();
            try
            {
                if (d == null || !d.IsWorkshared) return list;
                foreach (var ws in new FilteredWorksetCollector(d).OfKind(WorksetKind.UserWorkset))
                    list.Add(new ClashWorksetInfo { Id = ws.Id.IntegerValue, Name = ws.Name });
                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("OpenClashDefinitionsCommand: read user worksets", ex);
            }
            return list;
        }
    }
}
