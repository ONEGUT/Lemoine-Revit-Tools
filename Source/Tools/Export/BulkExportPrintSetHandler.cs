using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;

namespace LemoineTools.Tools.BulkExport
{
    /// <summary>
    /// Creates a new Revit print set (ViewSheetSet) from the current Bulk Export selection,
    /// then returns every print set in the document so the tool's list refreshes to include it.
    /// [Verify on Windows]: PrintManager.ViewSheetSetting.SaveAs is the documented mechanism for
    /// creating a named ViewSheetSet programmatically; this run is the first exercise of it here.
    /// </summary>
    public sealed class BulkExportPrintSetHandler : IExternalEventHandler
    {
        public string          Name      { get; set; } = "";
        public List<ElementId> MemberIds { get; set; } = new List<ElementId>();

        public Action<List<PrintSetInfo>>? OnCreated { get; set; }
        public Action<string>?             OnError   { get; set; }

        public string GetName() => "LemoineTools.Tools.BulkExport.BulkExportPrintSetHandler";

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

                if (string.IsNullOrWhiteSpace(Name))
                {
                    OnError?.Invoke(AppStrings.T("export.bulkExport.log.printSetNameRequired"));
                    return;
                }

                var viewSet = new ViewSet();
                foreach (var id in MemberIds)
                    if (doc.GetElement(id) is View v) viewSet.Insert(v);

                if (viewSet.IsEmpty)
                {
                    OnError?.Invoke(AppStrings.T("export.bulkExport.log.printSetEmptySelection"));
                    return;
                }

                using (var tx = new Transaction(doc, "Create Print Set"))
                {
                    tx.Start();
                    var pm  = doc.PrintManager;
                    pm.PrintRange = PrintRange.Select;
                    var vss = pm.ViewSheetSetting;
                    vss.CurrentViewSheetSet.Views = viewSet;
                    vss.SaveAs(Name.Trim());
                    tx.Commit();
                }

                OnCreated?.Invoke(Collect(doc));
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("BulkExportPrintSetHandler: create print set", ex);
                OnError?.Invoke(AppStrings.T("export.bulkExport.log.printSetCreateFailed", ex.Message));
            }
            finally
            {
                Name      = "";
                MemberIds = new List<ElementId>();
            }
        }

        /// <summary>Every ViewSheetSet in the document, alphabetized, with its member view/sheet
        /// ids. Runs on the main thread — used both at command launch and after a create.</summary>
        internal static List<PrintSetInfo> Collect(Document doc)
        {
            var result = new List<PrintSetInfo>();
            foreach (var vss in new FilteredElementCollector(doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>())
            {
                var info = new PrintSetInfo { Id = vss.Id, Name = vss.Name };
                try
                {
                    foreach (View v in vss.Views) info.MemberIds.Add(v.Id);
                }
                catch (Exception ex) { DiagnosticsLog.Swallowed($"BulkExportPrintSetHandler: read members of '{vss.Name}'", ex); }
                result.Add(info);
            }
            return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
