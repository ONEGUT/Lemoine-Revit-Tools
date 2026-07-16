using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Naming;

namespace LemoineTools.Tools.BulkExport
{
    /// <summary>
    /// Executes the bulk export on the Revit API thread.
    /// Set all properties before calling ExternalEvent.Raise().
    /// </summary>
    public class BulkExportEventHandler : IExternalEventHandler
    {
        // ── Inputs (set by ViewModel before Raise) ────────────────────────────
        public List<ElementId>        SelectedIds                  { get; set; } = new List<ElementId>();
        public string                 ExportMode                   { get; set; } = "Sheets";
        public string                 FilenamePattern              { get; set; } = "{SheetNumber}-{SheetName}";
        public string                 OutputFolder                 { get; set; } = "";
        public bool                   SplitByFormat                { get; set; } = true;
        public bool                   ExportPdf                    { get; set; } = true;
        public bool                   ExportDwg                    { get; set; } = false;
        public bool                   CombinePdf                   { get; set; } = true;
        public string                 DwgSetupName                 { get; set; } = "";
        public string                 PdfPlacement                 { get; set; } = "Offset from Corner";
        public string                 HiddenLines                  { get; set; } = "Vector Processing";
        public string                 ColorDepth                   { get; set; } = "Color";
        public string                 RasterQuality                { get; set; } = "High";
        public string                 ZoomSetting                  { get; set; } = "Fit to Page";   // "Fit to Page" | "Scale %"
        public int                    ZoomPercent                  { get; set; } = 100;
        public bool                   ViewLinksInBlue              { get; set; } = false;
        public bool                   ReplaceHalftoneWithThinLines { get; set; } = false;
        public List<PrintSetExportSpec> PrintSets                  { get; set; } = new List<PrintSetExportSpec>();

        // ── Format flags ──────────────────────────────────────────────────────
        public bool                   ExportNwc                    { get; set; } = false;
        public bool                   ExportIfc                    { get; set; } = false;

        // ── NWC options (all NavisworksExportOptions properties) ──────────────
        public string                 NwcCoordinates               { get; set; } = "Shared";
        public string                 NwcParameters                { get; set; } = "All";
        public bool                   NwcConvertElementProps       { get; set; } = true;
        public bool                   NwcDivideByLevel             { get; set; } = false;
        public bool                   NwcExportLinks               { get; set; } = true;
        public bool                   NwcExportParts               { get; set; } = false;
        public bool                   NwcExportElementIds          { get; set; } = true;
        public bool                   NwcExportUrls                { get; set; } = false;
        public bool                   NwcFindMissingMaterials      { get; set; } = false;
        public bool                   NwcExportRoomGeometry        { get; set; } = false;
        public bool                   NwcExportRoomAsAttribute     { get; set; } = false;
        public bool                   NwcConvertLights             { get; set; } = false;
        public bool                   NwcConvertLinkedCad          { get; set; } = false;
        public double                 NwcFacetingFactor            { get; set; } = 1.0;

        // ── IFC options ───────────────────────────────────────────────────────
        public string                 IfcVersion                   { get; set; } = "IFC2x3";

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }
        public Action<IReadOnlyList<ResultChip>>? OnResultChips { get; set; }

        // Per-format file tallies surfaced as result chips ("30 PDF · 30 DWG · …").
        private int _pdf, _dwg, _nwc, _ifc;

        // Resolved output basenames already claimed this run, per format — so two items that
        // resolve to the same filename don't silently overwrite each other on disk.
        private readonly Dictionary<string, HashSet<string>> _usedNames =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        public string GetName() => "BulkExport";

        public void Execute(UIApplication app)
        {
            var pushLog    = PushLog    ?? ((m, s) => { });
            var onProgress = OnProgress ?? ((p, a, b, c) => { });
            var onComplete = OnComplete ?? ((a, b, c) => { });

            int pass = 0, fail = 0, skip = 0;
            _pdf = _dwg = _nwc = _ifc = 0;
            _usedNames.Clear();

            try
            {
                // Guard: no active document
                if (app.ActiveUIDocument == null)
                {
                    pushLog(AppStrings.T("export.bulkExport.log.noDoc"), "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                var doc = app.ActiveUIDocument.Document;

                // Guard: output folder
                if (string.IsNullOrEmpty(OutputFolder))
                {
                    pushLog(AppStrings.T("export.bulkExport.log.noFolder"), "fail");
                    onComplete(0, 1, 0);
                    return;
                }
                try { Directory.CreateDirectory(OutputFolder); }
                catch (Exception ex)
                {
                    pushLog(AppStrings.T("export.bulkExport.log.folderFail", ex.Message), "fail");
                    DiagnosticsLog.Swallowed("BulkExport: create output folder", ex);
                    onComplete(0, 1, 0);
                    return;
                }

                // Collect elements — log any that could not be resolved
                var elements = SelectedIds
                    .Select(id => doc.GetElement(id))
                    .Where(e => e != null)
                    .ToList();

                int dropped = SelectedIds.Count - elements.Count;
                if (dropped > 0)
                    pushLog(AppStrings.T("export.bulkExport.log.droppedWarn", dropped), "warn");

                if (elements.Count == 0)
                {
                    pushLog(AppStrings.T("export.bulkExport.log.noElements"), "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                string projNumber = doc.ProjectInformation?.get_Parameter(BuiltInParameter.PROJECT_NUMBER)?.AsString() ?? "";
                string projName   = doc.ProjectInformation?.get_Parameter(BuiltInParameter.PROJECT_NAME)?.AsString()   ?? "";

                // Pre-flight: warn on sheets missing a titleblock. One collector over all
                // titleblocks (grouped by owner sheet) — never one collector per sheet.
                if (ExportMode == "Sheets")
                {
                    var sheetsWithTitleblock = new HashSet<long>(
                        new FilteredElementCollector(doc)
                            .OfCategory(BuiltInCategory.OST_TitleBlocks)
                            .WhereElementIsNotElementType()
                            .Select(tb => tb.OwnerViewId.Value));
                    foreach (var sheet in elements.OfType<ViewSheet>())
                        if (!sheetsWithTitleblock.Contains(sheet.Id.Value))
                            pushLog(AppStrings.T("export.bulkExport.log.noTitleblock", sheet.SheetNumber), "warn");
                }

                // ── NWC availability pre-flight ───────────────────────────────
                bool nwcReady = false;
                if (ExportNwc)
                {
                    if (ExportMode == "Sheets")
                    {
                        // Format-level mismatch — surfaced as a warning and in the review step;
                        // no per-file skip is counted (the "skipped" tally is per output file).
                        pushLog(AppStrings.T("export.bulkExport.log.nwcNeedsViews"), "warn");
                    }
                    else
                    {
                        nwcReady = OptionalFunctionalityUtils.IsNavisworksExporterAvailable();
                        if (!nwcReady)
                            pushLog(AppStrings.T("export.bulkExport.log.nwcNotAvail"), "fail");
                    }
                }

                // ── IFC pre-flight ────────────────────────────────────────────
                if (ExportIfc && ExportMode == "Sheets")
                    pushLog(AppStrings.T("export.bulkExport.log.ifcNeedsViews"), "warn");

                bool nwcEffective = ExportNwc && ExportMode != "Sheets" && nwcReady;
                bool ifcEffective = ExportIfc && ExportMode != "Sheets";

                if (PrintSets.Count > 0)
                {
                    // Print sets group PDF (combined) and order DWG. NWC/IFC are inherently
                    // per-view, so they always export from the Step 1 selection regardless of
                    // which print sets are checked (matches the Print Sets step's note).
                    int nwcOps  = nwcEffective ? elements.Count : 0;
                    int ifcOps  = ifcEffective ? elements.Count : 0;
                    int total   = CountPrintSetOps(doc) + nwcOps + ifcOps;
                    int done    = 0;

                    ExportPrintSetMode(doc, projNumber, projName, pushLog, onProgress,
                        ref pass, ref fail, ref skip, total, ref done);
                    ExportNwcIfc(doc, elements, projNumber, projName, pushLog, onProgress,
                        ref pass, ref fail, ref skip, nwcEffective, ifcEffective, total, ref done);
                }
                else
                {
                    ExportIndividualMode(doc, elements, projNumber, projName, pushLog, onProgress,
                        ref pass, ref fail, ref skip, nwcEffective, ifcEffective);
                }

                onProgress(100, pass, fail, skip);
                ReportResultChips(fail, skip);
                onComplete(pass, fail, skip);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("BulkExport: fatal error in Execute", ex);
                pushLog(AppStrings.T("export.bulkExport.log.fatalError", ex.Message), "fail");
                onComplete(pass, fail + 1, skip);
            }
            finally
            {
                // Session-long static handler (App.BulkExportHandler) — drop the run's payload.
                SelectedIds = new List<ElementId>();
                PrintSets   = new List<PrintSetExportSpec>();
                _usedNames.Clear();
            }
        }

        // Builds the result-strip chips from the per-format file tallies. Only formats that
        // actually produced files appear, so the breakdown stays uncluttered.
        private void ReportResultChips(int fail, int skip)
        {
            if (OnResultChips == null) return;
            var chips = new List<ResultChip>();
            if (_pdf > 0) chips.Add(new ResultChip("PDF", _pdf, "LemoineGreen"));
            if (_dwg > 0) chips.Add(new ResultChip("DWG", _dwg, "LemoineGreen"));
            if (_nwc > 0) chips.Add(new ResultChip("NWC", _nwc, "LemoineGreen"));
            if (_ifc > 0) chips.Add(new ResultChip("IFC", _ifc, "LemoineGreen"));
            chips.Add(new ResultChip("failed",  fail, "LemoineRed"));
            chips.Add(new ResultChip("skipped", skip, "LemoineTextDim"));
            OnResultChips(chips);
        }

        // Percentage of the run's PDF/DWG/NWC/IFC ops, clamped so a phase that advances
        // `done` past `total` (e.g. a print set whose members vanished) never overshoots 90%.
        private static int Pct(int done, int total)
            => total > 0 ? Math.Min(90, (int)(done * 90.0 / total)) : 90;

        // Count of PDF/DWG export operations across all checked print sets — used to size the
        // shared progress bar. PDF is one op per set; DWG is one op per still-existing member.
        private int CountPrintSetOps(Document doc)
        {
            bool StillExists(ElementId id) => doc.GetElement(id) != null;
            int totalPdf = PrintSets.Count(ps => ps.PdfOverride ?? ExportPdf);
            int totalDwg = PrintSets.Where(ps => ps.DwgOverride ?? ExportDwg)
                                    .Sum(ps => ps.MemberIds.Count(StillExists));
            return totalPdf + totalDwg;
        }

        // ── Print set mode (PDF/DWG only) ─────────────────────────────────────

        private void ExportPrintSetMode(
            Document doc,
            string projNumber, string projName,
            Action<string, string> pushLog,
            Action<int, int, int, int> onProgress,
            ref int pass, ref int fail, ref int skip,
            int total, ref int done)
        {
            // A print set's own membership is authoritative — it is NOT gated by the S1
            // sheet/view picker (that picker only drives NWC/IFC and the individual export path).
            // Just confirm each captured member id still resolves in the document.
            bool StillExists(ElementId id) => doc.GetElement(id) != null;

            DWGExportOptions? dwgOpts     = null;
            bool              dwgResolved = false;

            string globalPattern = FilenamePattern;

            foreach (var ps in PrintSets)
            {
                if (RunState.CancelRequested)
                {
                    pushLog(AppStrings.T("common.log.stoppedByUser", done, total), "warn");
                    break;
                }

                var memberIds = ps.MemberIds.Where(StillExists).ToList();
                bool doPdf = ps.PdfOverride ?? ExportPdf;
                bool doDwg = ps.DwgOverride ?? ExportDwg;

                if (memberIds.Count == 0)
                {
                    pushLog(AppStrings.T("export.bulkExport.log.packNoItems", ps.Name), "warn");
                    skip++;
                    if (doPdf) done++;                 // the set's single PDF op counted in total
                    if (doDwg) done += memberIds.Count; // 0 surviving members → 0 DWG ops counted
                    onProgress(Pct(done, total), pass, fail, skip);
                    continue;
                }

                // A per-set pattern override only affects filename resolution — swap it in
                // for this set's PDF/DWG naming, then restore the global pattern after.
                FilenamePattern = string.IsNullOrWhiteSpace(ps.PatternOverride) ? globalPattern : ps.PatternOverride!;

                // PDF: one combined call per print set. Named from the override pattern
                // (resolved against the first member) when given, else the set's own name.
                if (doPdf)
                {
                    try
                    {
                        string outDir = SplitByFormat ? EnsureSubfolder(OutputFolder, "PDF") : OutputFolder;
                        string setName = string.IsNullOrWhiteSpace(ps.PatternOverride)
                            ? MakeUniqueName("PDF", SanitizeFilename(ps.Name), pushLog)
                            : ResolveExportName(doc.GetElement(memberIds[0]), projNumber, projName, "PDF", pushLog);
                        var opts = BuildPdfOptions(setName, combine: true);
                        bool ok  = doc.Export(outDir, memberIds, opts);
                        if (ok)
                        {
                            pass++; _pdf++;
                            pushLog(AppStrings.T("export.bulkExport.log.pdfPackOk", setName, memberIds.Count), "pass");
                        }
                        else
                        {
                            fail++;
                            pushLog(AppStrings.T("export.bulkExport.log.pdfPackFalse", ps.Name), "fail");
                        }
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        pushLog(AppStrings.T("export.bulkExport.log.pdfPackFail", ps.Name, ex.Message), "fail");
                    }
                    done++;
                    onProgress(Pct(done, total), pass, fail, skip);
                }

                // DWG: individual export per member in the set.
                if (doDwg)
                {
                    if (!dwgResolved)
                    {
                        dwgOpts     = BuildDwgOptions(doc);
                        dwgResolved = true;
                    }

                    if (dwgOpts == null)
                    {
                        pushLog(AppStrings.T("export.bulkExport.log.dwgPackNoSetup", DwgSetupName, ps.Name), "fail");
                        skip += memberIds.Count;
                        done += memberIds.Count;
                        onProgress(Pct(done, total), pass, fail, skip);
                    }
                    else
                    {
                        foreach (var id in memberIds)
                        {
                            if (RunState.CancelRequested)
                            {
                                pushLog(AppStrings.T("common.log.stoppedByUser", done, total), "warn");
                                FilenamePattern = globalPattern;
                                return;
                            }
                            var el = doc.GetElement(id);
                            string safeName = ResolveExportName(el, projNumber, projName, "DWG", pushLog);

                            try
                            {
                                string outDir = SplitByFormat ? EnsureSubfolder(OutputFolder, "DWG") : OutputFolder;
                                bool ok = doc.Export(outDir, safeName, new List<ElementId> { id }, dwgOpts);
                                if (ok)
                                {
                                    pass++; _dwg++;
                                    pushLog(AppStrings.T("export.bulkExport.log.dwgOk", safeName), "pass");
                                }
                                else
                                {
                                    fail++;
                                    pushLog(AppStrings.T("export.bulkExport.log.dwgFalse", safeName), "fail");
                                }
                            }
                            catch (Exception ex)
                            {
                                fail++;
                                pushLog(AppStrings.T("export.bulkExport.log.dwgFail", safeName, ex.Message), "fail");
                            }
                            done++;
                            onProgress(Pct(done, total), pass, fail, skip);
                        }
                    }
                }
            }

            FilenamePattern = globalPattern;
        }

        // ── Individual mode (PDF/DWG from the S1 selection, then NWC/IFC) ──────

        private void ExportIndividualMode(
            Document doc, List<Element> elements,
            string projNumber, string projName,
            Action<string, string> pushLog,
            Action<int, int, int, int> onProgress,
            ref int pass, ref int fail, ref int skip,
            bool nwcEffective, bool ifcEffective)
        {
            // When CombinePdf is on, all sheets go in one Export call → counts as 1 PDF op
            int pdfOps = ExportPdf ? (CombinePdf ? 1 : elements.Count) : 0;
            int dwgOps = ExportDwg ? elements.Count : 0;
            int nwcOps = nwcEffective ? elements.Count : 0;
            int ifcOps = ifcEffective ? elements.Count : 0;
            int total  = pdfOps + dwgOps + nwcOps + ifcOps;
            int done   = 0;

            // ── PDF ───────────────────────────────────────────────────────────
            if (ExportPdf)
            {
                if (CombinePdf)
                {
                    // One combined call with all IDs; filename from first element's tokens
                    try
                    {
                        string outDir    = SplitByFormat ? EnsureSubfolder(OutputFolder, "PDF") : OutputFolder;
                        string safeName  = ResolveExportName(elements[0], projNumber, projName, "PDF", pushLog);
                        var allIds       = elements.Select(e => e.Id).ToList();
                        var opts         = BuildPdfOptions(safeName, combine: true);
                        bool ok          = doc.Export(outDir, allIds, opts);
                        if (ok)
                        {
                            pass++; _pdf++;
                            pushLog(AppStrings.T("export.bulkExport.log.pdfCombinedOk", safeName, allIds.Count), "pass");
                        }
                        else
                        {
                            fail++;
                            pushLog(AppStrings.T("export.bulkExport.log.pdfCombinedFalse"), "fail");
                        }
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        pushLog(AppStrings.T("export.bulkExport.log.pdfCombinedFail", ex.Message), "fail");
                    }
                    done++;
                    onProgress(Pct(done, total), pass, fail, skip);
                }
                else
                {
                    // One call per element
                    foreach (var element in elements)
                    {
                        if (RunState.CancelRequested)
                        {
                            pushLog(AppStrings.T("common.log.stoppedByUser", done, total), "warn");
                            return;
                        }
                        string safeName = ResolveExportName(element, projNumber, projName, "PDF", pushLog);

                        try
                        {
                            string outDir = SplitByFormat ? EnsureSubfolder(OutputFolder, "PDF") : OutputFolder;
                            var opts      = BuildPdfOptions(safeName, combine: false);
                            bool ok       = doc.Export(outDir, new List<ElementId> { element.Id }, opts);
                            if (ok)
                            {
                                pass++; _pdf++;
                                pushLog(AppStrings.T("export.bulkExport.log.pdfOk", safeName), "pass");
                            }
                            else
                            {
                                fail++;
                                pushLog(AppStrings.T("export.bulkExport.log.pdfFalse", safeName), "fail");
                            }
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            pushLog(AppStrings.T("export.bulkExport.log.pdfFail", safeName, ex.Message), "fail");
                        }
                        done++;
                        onProgress(Pct(done, total), pass, fail, skip);
                    }
                }
            }

            // ── DWG ───────────────────────────────────────────────────────────
            if (ExportDwg)
            {
                var dwgOpts = BuildDwgOptions(doc);

                if (dwgOpts == null)
                {
                    pushLog(AppStrings.T("export.bulkExport.log.dwgNoSetupAll", DwgSetupName), "fail");
                    skip += elements.Count;
                    done += elements.Count;
                    onProgress(Pct(done, total), pass, fail, skip);
                }
                else
                {
                    foreach (var element in elements)
                    {
                        if (RunState.CancelRequested)
                        {
                            pushLog(AppStrings.T("common.log.stoppedByUser", done, total), "warn");
                            return;
                        }
                        string safeName = ResolveExportName(element, projNumber, projName, "DWG", pushLog);

                        try
                        {
                            string outDir = SplitByFormat ? EnsureSubfolder(OutputFolder, "DWG") : OutputFolder;
                            bool ok = doc.Export(outDir, safeName, new List<ElementId> { element.Id }, dwgOpts);
                            if (ok)
                            {
                                pass++; _dwg++;
                                pushLog(AppStrings.T("export.bulkExport.log.dwgOk", safeName), "pass");
                            }
                            else
                            {
                                fail++;
                                pushLog(AppStrings.T("export.bulkExport.log.dwgFalse", safeName), "fail");
                            }
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            pushLog(AppStrings.T("export.bulkExport.log.dwgFail", safeName, ex.Message), "fail");
                        }
                        done++;
                        onProgress(Pct(done, total), pass, fail, skip);
                    }
                }
            }

            ExportNwcIfc(doc, elements, projNumber, projName, pushLog, onProgress,
                ref pass, ref fail, ref skip, nwcEffective, ifcEffective, total, ref done);
        }

        // ── NWC / IFC (3D views only, always from the S1 selection) ────────────
        // Shared by both the individual and print-set paths so NWC/IFC behave identically
        // regardless of whether print sets are checked.

        private void ExportNwcIfc(
            Document doc, List<Element> elements,
            string projNumber, string projName,
            Action<string, string> pushLog,
            Action<int, int, int, int> onProgress,
            ref int pass, ref int fail, ref int skip,
            bool nwcEffective, bool ifcEffective,
            int total, ref int done)
        {
            // ── NWC ───────────────────────────────────────────────────────────
            if (nwcEffective)
            {
                foreach (var element in elements)
                {
                    if (RunState.CancelRequested)
                    {
                        pushLog(AppStrings.T("common.log.stoppedByUser", done, total), "warn");
                        return;
                    }
                    string safeName = ResolveExportName(element, projNumber, projName, "NWC", pushLog);

                    try
                    {
                        if (!(element is View3D view3d))
                        {
                            skip++;
                            pushLog(AppStrings.T("export.bulkExport.log.nwcNot3d", safeName), "warn");
                        }
                        else
                        {
                            string outDir = SplitByFormat ? EnsureSubfolder(OutputFolder, "NWC") : OutputFolder;

                            var opts = ExportOptionsFactory.BuildNwcOptions(BuildNwcOptionSet(), view3d.Id, pushLog);

                            // NWC export uses the 3-parameter overload — ViewId is set on the options object
                            doc.Export(outDir, safeName, opts);
                            pass++; _nwc++;
                            pushLog(AppStrings.T("export.bulkExport.log.nwcOk", safeName), "pass");
                        }
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        pushLog(AppStrings.T("export.bulkExport.log.nwcFail", safeName, ex.Message), "fail");
                    }
                    done++;
                    onProgress(Pct(done, total), pass, fail, skip);
                }
            }

            // ── IFC ───────────────────────────────────────────────────────────
            if (ifcEffective)
            {
                foreach (var element in elements)
                {
                    if (RunState.CancelRequested)
                    {
                        pushLog(AppStrings.T("common.log.stoppedByUser", done, total), "warn");
                        return;
                    }
                    string safeName = ResolveExportName(element, projNumber, projName, "IFC", pushLog);

                    try
                    {
                        if (!(element is View3D))
                        {
                            skip++;
                            pushLog(AppStrings.T("export.bulkExport.log.ifcNot3d", safeName), "warn");
                        }
                        else
                        {
                            string outDir = SplitByFormat ? EnsureSubfolder(OutputFolder, "IFC") : OutputFolder;

                            var opts = ExportOptionsFactory.BuildIfcOptions(IfcVersion, element.Id);

                            // IFC export writes IFC-specific data to the document and requires a transaction
                            using (var t = new Transaction(doc, "Batch IFC Export"))
                            {
                                t.Start();
                                doc.Export(outDir, safeName, opts);
                                t.Commit();
                            }
                            pass++; _ifc++;
                            pushLog(AppStrings.T("export.bulkExport.log.ifcOk", safeName), "pass");
                        }
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        pushLog(AppStrings.T("export.bulkExport.log.ifcFail", safeName, ex.Message), "fail");
                    }
                    done++;
                    onProgress(Pct(done, total), pass, fail, skip);
                }
            }
        }

        // ── Builders ──────────────────────────────────────────────────────────

        // Option construction is delegated to ExportOptionsFactory so Bulk Export and
        // Print View build identical PDF/DWG output.
        private PDFExportOptions BuildPdfOptions(string fileName, bool combine)
            => ExportOptionsFactory.BuildPdfOptions(
                   fileName, combine, PdfPlacement, ColorDepth, RasterQuality,
                   ZoomSetting, ZoomPercent, ViewLinksInBlue, ReplaceHalftoneWithThinLines);

        // Returns null when a named setup is specified but does not exist in the document.
        private DWGExportOptions? BuildDwgOptions(Document doc)
            => ExportOptionsFactory.BuildDwgOptions(doc, DwgSetupName);

        private NwcOptionSet BuildNwcOptionSet() => new NwcOptionSet
        {
            Coordinates           = NwcCoordinates,
            Parameters            = NwcParameters,
            ConvertElementProps   = NwcConvertElementProps,
            DivideByLevel         = NwcDivideByLevel,
            ExportLinks           = NwcExportLinks,
            ExportParts           = NwcExportParts,
            ExportElementIds      = NwcExportElementIds,
            ExportUrls            = NwcExportUrls,
            FindMissingMaterials  = NwcFindMissingMaterials,
            ExportRoomGeometry    = NwcExportRoomGeometry,
            ExportRoomAsAttribute = NwcExportRoomAsAttribute,
            ConvertLights         = NwcConvertLights,
            ConvertLinkedCad      = NwcConvertLinkedCad,
            FacetingFactor        = NwcFacetingFactor,
        };

        // ── Helpers ───────────────────────────────────────────────────────────

        // Resolves the export filename through the shared TokenResolver and never silently emits
        // a junk name. If the pattern resolves to something with no usable character (e.g. a stale
        // sheet-token pattern applied in Views mode collapsing to "-"), the failure is reported to
        // the run log AND diagnostics.log and a deterministic fallback (element name, else id) is
        // used. The final name is uniquified per format so two items can't overwrite one file.
        private string ResolveExportName(Element? element, string projNumber, string projName,
                                         string fmt, Action<string, string> pushLog)
        {
            var ctx = new TokenContext { Target = element };
            ctx.Computed["ProjectNumber"] = projNumber;
            ctx.Computed["ProjectName"]   = projName;
            if (element is View view && !(element is ViewSheet))
                ctx.Computed["ViewType"] = view.ViewType.ToString();

            string resolved = TokenResolver.Resolve(FilenamePattern, ctx, msg => pushLog(msg, "warn"));
            if (resolved.Any(char.IsLetterOrDigit))
                return MakeUniqueName(fmt, SanitizeFilename(resolved), pushLog);

            // Degenerate — report loudly and fall back.
            string label    = element?.Name ?? "";
            string fallback = SanitizeFilename(label);
            if (!fallback.Any(char.IsLetterOrDigit))
                fallback = "export-" + (element?.Id.Value.ToString() ?? "0");

            pushLog(AppStrings.T("export.bulkExport.log.nameFallback", fmt, FilenamePattern, label, (element is ViewSheet ? AppStrings.T("export.bulkExport.words.sheet") : AppStrings.T("export.bulkExport.words.view")), fallback), "warn");
            DiagnosticsLog.Warn("BulkExport.ResolveExportName",
                $"Degenerate filename. fmt={fmt} pattern='{FilenamePattern}' resolved='{resolved}' " +
                $"element={element?.Id} name='{label}' fallback='{fallback}'");
            return MakeUniqueName(fmt, fallback, pushLog);
        }

        // Claims a basename within a format's namespace, appending " (2)", " (3)", … on a
        // collision so two items that resolve to the same name write two files, not one.
        private string MakeUniqueName(string fmt, string baseName, Action<string, string> pushLog)
        {
            if (!_usedNames.TryGetValue(fmt, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _usedNames[fmt] = set;
            }
            if (set.Add(baseName)) return baseName;
            for (int n = 2; n < 100000; n++)
            {
                string cand = $"{baseName} ({n})";
                if (set.Add(cand))
                {
                    pushLog(AppStrings.T("export.bulkExport.log.nameCollision", fmt, baseName, cand), "warn");
                    return cand;
                }
            }
            return baseName;
        }

        private static string SanitizeFilename(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim();
            // Guard against an entirely-illegal pattern resolving to an empty string
            return name.Length > 0 ? name : "export";
        }

        private static string EnsureSubfolder(string parent, string sub)
        {
            string dir = Path.Combine(parent, sub);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
