using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Testing;

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
                catch { _window = null; }
            }

            var doc = commandData.Application.ActiveUIDocument?.Document;

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

            // Source documents available to each clash group (host + each loaded link).
            var docs = new List<ClashDocInfo>();
            if (doc != null)
            {
                docs.Add(new ClashDocInfo { Name = "(Host) " + doc.Title, LinkInstId = 0L });
                foreach (var li in new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    var ld = li.GetLinkDocument();
                    if (ld == null) continue;
                    docs.Add(new ClashDocInfo
                    {
                        Name       = "[" + Path.GetFileNameWithoutExtension(ld.Title) + "]",
                        LinkInstId = li.Id.Value,
                    });
                }
            }

            var ready = new ManualResetEventSlim(false);
            ClashDefinitionsWindow? win = null;

            var thread = new Thread(() =>
            {
                win = new ClashDefinitionsWindow();
                win.SetContext(lineStyleNames, docs, App.ClashPickHandler, App.ClashPickEvent);
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
    }
}
