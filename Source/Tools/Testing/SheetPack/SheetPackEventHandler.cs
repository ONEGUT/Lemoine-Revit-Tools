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
    /// Writes sheet-pack metadata to Revit sheet parameters and optionally
    /// exports each pack's sheets to PDF.
    ///
    /// Set all properties before calling ExternalEvent.Raise().
    /// </summary>
    public class SheetPackEventHandler : IExternalEventHandler
    {
        // ── Inputs set by ViewModel before Raise ──────────────────────────────

        /// <summary>Ordered list of packs to stamp.</summary>
        public List<SheetPackLayout> Packs { get; set; } = new List<SheetPackLayout>();

        /// <summary>Name of the Revit parameter to receive the pack name (e.g. "Issue Set").</summary>
        public string PackNameParameter { get; set; } = "Issue Set";

        /// <summary>Name of the Revit parameter to receive the issue purpose.</summary>
        public string IssuePurposeParameter { get; set; } = "Issue Purpose";

        /// <summary>When true, export each pack's sheets to a PDF in OutputFolder.</summary>
        public bool ExportPdf { get; set; } = false;

        /// <summary>Folder to write PDF exports into (used when ExportPdf is true).</summary>
        public string OutputFolder { get; set; } = "";

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "SheetPack";

        public void Execute(UIApplication app)
        {
            var pushLog    = PushLog    ?? ((m, s) => { });
            var onProgress = OnProgress ?? ((p, a, b, c) => { });
            var onComplete = OnComplete ?? ((a, b, c) => { });

            int pass = 0, fail = 0, skip = 0;

            try
            {
                var doc = app.ActiveUIDocument.Document;

                if (Packs == null || Packs.Count == 0)
                {
                    pushLog("No packs defined.", "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                // Build a number → ViewSheet map once
                var sheetMap = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .ToDictionary(vs => vs.SheetNumber, vs => vs);

                int totalSheets = Packs.Sum(p => p.SheetNumbers.Count);
                int processed   = 0;

                using (var tx = new Transaction(doc, "Lemoine — Stamp Sheet Packs"))
                {
                    var fho = tx.GetFailureHandlingOptions();
                    fho.SetClearAfterRollback(true);
                    tx.SetFailureHandlingOptions(fho);
                    tx.Start();

                    foreach (var pack in Packs)
                    {
                        pushLog($"Pack: {pack.PackName} ({pack.SheetNumbers.Count} sheet(s))", "pass");

                        foreach (var num in pack.SheetNumbers)
                        {
                            if (!sheetMap.TryGetValue(num, out var sheet))
                            {
                                pushLog($"  Sheet {num} not found — skipped.", "fail");
                                skip++;
                                processed++;
                                continue;
                            }

                            bool anyWritten = false;

                            // Write pack name parameter
                            anyWritten |= TrySetStringParam(sheet, PackNameParameter, pack.PackName, pushLog);

                            // Write issue purpose parameter
                            if (!string.IsNullOrEmpty(pack.IssuePurpose))
                                anyWritten |= TrySetStringParam(sheet, IssuePurposeParameter, pack.IssuePurpose, pushLog);

                            if (anyWritten)
                            {
                                pass++;
                                pushLog($"  ✓ {num} — {sheet.Name}", "pass");
                            }
                            else
                            {
                                skip++;
                                pushLog($"  {num} — no writable parameters matched.", "fail");
                            }

                            processed++;
                            int pct = totalSheets > 0 ? (int)(processed * 80.0 / totalSheets) : 80;
                            onProgress(pct, pass, fail, skip);
                        }
                    }

                    tx.Commit();
                }

                // ── Optional PDF export ───────────────────────────────────────
                if (ExportPdf && !string.IsNullOrEmpty(OutputFolder))
                {
                    try { Directory.CreateDirectory(OutputFolder); } catch { }

                    int packIdx = 0;
                    foreach (var pack in Packs)
                    {
                        packIdx++;
                        var ids = pack.SheetNumbers
                            .Where(n => sheetMap.ContainsKey(n))
                            .Select(n => sheetMap[n].Id)
                            .ToList();

                        if (ids.Count == 0) continue;

                        try
                        {
                            var opts = new PDFExportOptions
                            {
                                FileName       = SanitizeFilename(pack.PackName),
                                Combine        = true,
                                PaperPlacement = PaperPlacementType.LowerLeft,
                            };

                            doc.Export(OutputFolder, ids, opts);
                            pushLog($"✓ Exported PDF: {pack.PackName}.pdf", "pass");
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            pushLog($"PDF export failed for pack '{pack.PackName}': {ex.Message}", "fail");
                        }

                        int pct2 = 80 + (int)(packIdx * 20.0 / Packs.Count);
                        onProgress(pct2, pass, fail, skip);
                    }
                }

                onProgress(100, pass, fail, skip);
                onComplete(pass, fail, skip);
            }
            catch (Exception ex)
            {
                pushLog($"Sheet Pack error: {ex.Message}", "fail");
                onComplete(pass, 1, skip);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static bool TrySetStringParam(Element elem, string paramName, string value,
                                               Action<string, string> pushLog)
        {
            if (string.IsNullOrEmpty(paramName)) return false;
            var p = elem.LookupParameter(paramName);
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.String) return false;
            try
            {
                p.Set(value);
                return true;
            }
            catch (Exception ex)
            {
                pushLog($"    Could not set '{paramName}': {ex.Message}", "fail");
                return false;
            }
        }

        private static string SanitizeFilename(string raw)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                raw = raw.Replace(c, '_');
            return raw;
        }
    }
}
