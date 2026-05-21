using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.Testing
{
    /// <summary>
    /// Creates Revit sheets in bulk on the Revit API thread.
    /// Set all properties before calling ExternalEvent.Raise().
    /// </summary>
    public class CreateSheetsEventHandler : IExternalEventHandler
    {
        // ── Inputs set by ViewModel before Raise ──────────────────────────────
        public string          SourceMode        { get; set; } = "By Level";  // "By Level"|"By Room"|"By Scope Box"|"From CSV"
        public List<ElementId> SourceElementIds  { get; set; } = new List<ElementId>();
        public ElementId       TitleBlockTypeId  { get; set; } = ElementId.InvalidElementId;
        public int             StartingNumber    { get; set; } = 1;
        public string          NamingPattern     { get; set; } = "{LevelName}";
        public string          CsvFilePath       { get; set; } = "";

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?    PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "CreateSheets";

        public void Execute(UIApplication app)
        {
            var pushLog    = PushLog    ?? ((m, s) => { });
            var onProgress = OnProgress ?? ((p, a, b, c) => { });
            var onComplete = OnComplete ?? ((a, b, c) => { });

            int pass = 0, fail = 0, skip = 0;

            try
            {
                var doc = app.ActiveUIDocument.Document;

                if (TitleBlockTypeId == ElementId.InvalidElementId)
                {
                    pushLog("No title block selected.", "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                // Build sheet creation tasks
                var tasks = new List<(string Number, string Name, Dictionary<string, string>? ExtraParams)>();

                if (SourceMode == "From CSV")
                {
                    var rows = CsvParser.ParseAsDicts(CsvFilePath);
                    foreach (var row in rows)
                    {
                        row.TryGetValue("SheetNumber", out var num);
                        row.TryGetValue("SheetName",   out var name);
                        if (string.IsNullOrEmpty(num) && string.IsNullOrEmpty(name)) continue;
                        tasks.Add((num ?? "", name ?? "", row));
                    }
                }
                else
                {
                    int idx = StartingNumber;
                    foreach (var elemId in SourceElementIds)
                    {
                        var elem = doc.GetElement(elemId);
                        if (elem == null) { skip++; continue; }

                        var tokens = BuildTokensFor(elem, doc);
                        string name   = LemoineTokenInput.Resolve(NamingPattern, tokens);
                        string number = idx.ToString();
                        tasks.Add((number, name, null));
                        idx++;
                    }
                }

                int total = tasks.Count;

                using (var tx = new Transaction(doc, "Lemoine — Create Sheets"))
                {
                    var fho = tx.GetFailureHandlingOptions();
                    fho.SetClearAfterRollback(true);
                    tx.SetFailureHandlingOptions(fho);
                    tx.Start();

                    for (int i = 0; i < tasks.Count; i++)
                    {
                        var (number, name, extras) = tasks[i];

                        // Check if sheet already exists
                        bool exists = new FilteredElementCollector(doc)
                            .OfClass(typeof(ViewSheet))
                            .Cast<ViewSheet>()
                            .Any(vs => vs.SheetNumber == number);

                        if (exists)
                        {
                            pushLog($"Sheet {number} already exists — skipped.", "fail");
                            skip++;
                        }
                        else
                        {
                            try
                            {
                                var sheet = ViewSheet.Create(doc, TitleBlockTypeId);
                                sheet.get_Parameter(BuiltInParameter.SHEET_NUMBER)?.Set(number);
                                sheet.get_Parameter(BuiltInParameter.SHEET_NAME)?.Set(name);

                                // Write extra CSV columns to matching Revit parameters
                                if (extras != null)
                                {
                                    foreach (var kvp in extras)
                                    {
                                        if (kvp.Key == "SheetNumber" || kvp.Key == "SheetName") continue;
                                        var p = sheet.LookupParameter(kvp.Key);
                                        if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String)
                                            p.Set(kvp.Value);
                                    }
                                }

                                pass++;
                                pushLog($"✓ Sheet {number} — {name}", "pass");
                            }
                            catch (Exception ex)
                            {
                                fail++;
                                pushLog($"Failed to create sheet {number}: {ex.Message}", "fail");
                            }
                        }

                        int pct = total > 0 ? (int)((i + 1) * 90.0 / total) : 90;
                        onProgress(pct, pass, fail, skip);
                    }

                    tx.Commit();
                }

                onProgress(100, pass, fail, skip);
                onComplete(pass, fail, skip);
            }
            catch (Exception ex)
            {
                pushLog($"Create Sheets error: {ex.Message}", "fail");
                onComplete(pass, 1, skip);
            }
        }

        // ── Token builders ────────────────────────────────────────────────────

        private static Dictionary<string, string> BuildTokensFor(Element elem, Document doc)
        {
            var d = new Dictionary<string, string>();

            if (elem is Level level)
            {
                d["LevelName"]   = level.Name;
                d["LevelNumber"] = level.Id.IntegerValue.ToString();
            }
            else if (elem is SpatialElement room)
            {
                d["RoomName"]   = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()   ?? room.Name;
                d["RoomNumber"] = room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? "";
                d["LevelName"]  = doc.GetElement(room.LevelId) is Level rl ? rl.Name : "";
            }
            else
            {
                // Scope box or other
                d["ScopeBoxName"] = elem.Name;
                d["LevelName"]    = elem.Name;
            }

            d["SheetNumber"] = "";
            return d;
        }
    }
}
