using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    ///
    /// One export path. Everything the run exports is an <see cref="ExportSetSpec"/> — a user's
    /// flat selection arrives as a single unnamed set — which replaced the previous split between
    /// an "individual" path and a "print set" path that explicitly ignored each other's input.
    ///
    /// The run is planned before anything is written: <see cref="BuildPlan"/> resolves every
    /// output's folder and filename up front, so the already-exists scan, the collision check and
    /// the export itself all work from the same list rather than three re-derivations that drift.
    /// </summary>
    public class BulkExportEventHandler : IExternalEventHandler
    {
        // ── Inputs (set by ViewModel before Raise) ────────────────────────────
        public List<ExportSetSpec>    Sets                         { get; set; } = new List<ExportSetSpec>();
        public string                 ExportMode                   { get; set; } = "Sheets";

        /// <summary>Names per-item outputs: per-sheet PDFs, DWG, NWC, IFC.</summary>
        public string                 FilenamePattern              { get; set; } = "{SheetNumber}-{SheetName}";

        /// <summary>Names the combined PDFs — one per set, or the single whole-run file.</summary>
        public string                 SetFilenamePattern           { get; set; } = "{SetName}";

        public PdfGranularity         Granularity                  { get; set; } = PdfGranularity.PerSet;
        public string                 OutputFolder                 { get; set; } = "";
        public bool                   SplitByFormat                { get; set; } = true;
        public bool                   SetSubfolders                { get; set; } = false;

        /// <summary>"Overwrite" | "Skip" | "Add suffix" — what to do when the target file exists.</summary>
        public string                 ExistingFileAction           { get; set; } = "Overwrite";

        public bool                   ExportPdf                    { get; set; } = true;
        public bool                   ExportDwg                    { get; set; } = false;
        public string                 DwgSetupName                 { get; set; } = "";
        public string                 PdfPlacement                 { get; set; } = "Offset from Corner";
        public string                 HiddenLines                  { get; set; } = "Vector Processing";
        public string                 ColorDepth                   { get; set; } = "Color";
        public string                 RasterQuality                { get; set; } = "High";
        public string                 ZoomSetting                  { get; set; } = "Fit to Page";   // "Fit to Page" | "Scale %"
        public int                    ZoomPercent                  { get; set; } = 100;
        public bool                   ViewLinksInBlue              { get; set; } = false;
        public bool                   ReplaceHalftoneWithThinLines { get; set; } = false;

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

        /// <summary>A set resolved against the live document — members that no longer exist are dropped.</summary>
        private sealed class ResolvedSet
        {
            public ExportSetSpec  Spec    = null!;
            public string         Name    = "";
            public List<Element>  Members = new List<Element>();
            public int            Index;                       // 1-based among enabled, non-empty sets
        }

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
                if (app.ActiveUIDocument == null)
                {
                    pushLog(AppStrings.T("export.bulkExport.log.noDoc"), "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                var doc = app.ActiveUIDocument.Document;

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

                var sets = ResolveSets(doc, pushLog);
                if (sets.Count == 0)
                {
                    pushLog(AppStrings.T("export.bulkExport.log.noElements"), "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                string projNumber = doc.ProjectInformation?.get_Parameter(BuiltInParameter.PROJECT_NUMBER)?.AsString() ?? "";
                string projName   = doc.ProjectInformation?.get_Parameter(BuiltInParameter.PROJECT_NAME)?.AsString()   ?? "";

                PreflightTitleblocks(doc, sets, pushLog);
                bool nwcEffective = PreflightNwc(pushLog);
                bool ifcEffective = PreflightIfc(pushLog);

                var plan = BuildPlan(doc, sets, projNumber, projName, nwcEffective, ifcEffective, pushLog);
                if (plan.Count == 0)
                {
                    pushLog(AppStrings.T("export.bulkExport.log.planEmpty"), "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                ScanExisting(plan, pushLog, ref skip);

                ExecutePlan(doc, plan, pushLog, onProgress, ref pass, ref fail, ref skip);

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
                Sets = new List<ExportSetSpec>();
                _usedNames.Clear();
            }
        }

        // ── Resolve ───────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves each spec's members against the live document. A member that no longer exists
        /// is dropped with a per-set count (a silent drop is indistinguishable from a broken
        /// collector), and a set left with nothing is skipped rather than exported empty.
        /// </summary>
        private List<ResolvedSet> ResolveSets(Document doc, Action<string, string> pushLog)
        {
            var result = new List<ResolvedSet>();
            foreach (var spec in Sets)
            {
                var members = new List<Element>();
                foreach (var id in spec.MemberIds)
                {
                    var el = doc.GetElement(id);
                    if (el != null) members.Add(el);
                }

                int missing = spec.MemberIds.Count - members.Count;
                if (missing > 0)
                    pushLog(AppStrings.T("export.bulkExport.log.setMembersMissing",
                                         DisplaySetName(spec.Name), missing, spec.MemberIds.Count), "warn");

                if (members.Count == 0)
                {
                    pushLog(AppStrings.T("export.bulkExport.log.setEmpty", DisplaySetName(spec.Name)), "warn");
                    continue;
                }

                result.Add(new ResolvedSet
                {
                    Spec    = spec,
                    Name    = spec.Name,
                    Members = members,
                    Index   = result.Count + 1,
                });
            }
            return result;
        }

        private static string DisplaySetName(string name)
            => string.IsNullOrWhiteSpace(name) ? AppStrings.T("export.bulkExport.words.unnamedSet") : name;

        // ── Pre-flight ────────────────────────────────────────────────────────

        // One collector over all titleblocks (grouped by owner sheet) — never one per sheet.
        private void PreflightTitleblocks(Document doc, List<ResolvedSet> sets, Action<string, string> pushLog)
        {
            if (ExportMode != "Sheets") return;

            var sheets = sets.SelectMany(s => s.Members).OfType<ViewSheet>()
                             .GroupBy(s => s.Id.Value).Select(g => g.First()).ToList();
            if (sheets.Count == 0) return;

            var withTitleblock = new HashSet<long>(
                new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsNotElementType()
                    .Select(tb => tb.OwnerViewId.Value));

            int missing = 0;
            foreach (var sheet in sheets)
                if (!withTitleblock.Contains(sheet.Id.Value))
                {
                    pushLog(AppStrings.T("export.bulkExport.log.noTitleblock", sheet.SheetNumber), "warn");
                    missing++;
                }
            if (missing == 0)
                pushLog(AppStrings.T("export.bulkExport.log.titleblocksOk", sheets.Count), "info");
        }

        private bool PreflightNwc(Action<string, string> pushLog)
        {
            if (!ExportNwc) return false;
            if (ExportMode == "Sheets")
            {
                // Format-level mismatch — surfaced as a warning and in the review step;
                // no per-file skip is counted (the "skipped" tally is per output file).
                pushLog(AppStrings.T("export.bulkExport.log.nwcNeedsViews"), "warn");
                return false;
            }
            if (!OptionalFunctionalityUtils.IsNavisworksExporterAvailable())
            {
                pushLog(AppStrings.T("export.bulkExport.log.nwcNotAvail"), "fail");
                return false;
            }
            return true;
        }

        private bool PreflightIfc(Action<string, string> pushLog)
        {
            if (!ExportIfc) return false;
            if (ExportMode == "Sheets")
            {
                pushLog(AppStrings.T("export.bulkExport.log.ifcNeedsViews"), "warn");
                return false;
            }
            return true;
        }

        // ── Plan ──────────────────────────────────────────────────────────────

        /// <summary>One (set, member) pair in run order, carrying both sequence numbers.</summary>
        private struct RunItem
        {
            public ResolvedSet Set;
            public Element     Element;
            public int         SetSeq;   // 1-based within its set
            public int         RunSeq;   // 1-based across the run
        }

        /// <summary>
        /// Resolves every output's folder and filename before anything is written. Nothing here
        /// touches the disk beyond the existence check that follows — directories are created at
        /// execution time, so planning is side-effect free.
        /// </summary>
        private List<PlannedOutput> BuildPlan(
            Document doc, List<ResolvedSet> sets,
            string projNumber, string projName,
            bool nwcEffective, bool ifcEffective,
            Action<string, string> pushLog)
        {
            var plan = new List<PlannedOutput>();

            // Flatten to run order once — every per-item format walks the same list, so DWG, NWC
            // and IFC all agree with the PDF ordering.
            var runItems = new List<RunItem>();
            foreach (var set in sets)
            {
                for (int i = 0; i < set.Members.Count; i++)
                    runItems.Add(new RunItem
                    {
                        Set     = set,
                        Element = set.Members[i],
                        SetSeq  = i + 1,
                        RunSeq  = runItems.Count + 1,
                    });
            }
            int runWidth = Math.Max(2, runItems.Count.ToString().Length);

            // ── PDF ───────────────────────────────────────────────────────────
            if (ExportPdf)
            {
                switch (Granularity)
                {
                    case PdfGranularity.PerSheet:
                        foreach (var item in runItems)
                        {
                            if (!FormatOn(item.Set, "PDF")) continue;
                            plan.Add(new PlannedOutput
                            {
                                Format    = "PDF",
                                SetName   = item.Set.Name,
                                Directory = OutputDir("PDF", item.Set),
                                BaseName  = ResolveItemFileName(doc, item, runWidth, projNumber, projName, "PDF", pushLog),
                                MemberIds = new List<ElementId> { item.Element.Id },
                            });
                        }
                        break;

                    case PdfGranularity.PerSet:
                        foreach (var set in sets)
                        {
                            if (!FormatOn(set, "PDF")) continue;
                            plan.Add(new PlannedOutput
                            {
                                Format    = "PDF",
                                SetName   = set.Name,
                                Directory = OutputDir("PDF", set),
                                BaseName  = ResolveSetFileName(doc, set, sets.Count, projNumber, projName, pushLog),
                                MemberIds = set.Members.Select(m => m.Id).ToList(),
                            });
                        }
                        break;

                    case PdfGranularity.SingleFile:
                    {
                        // One file over every PDF-enabled set, concatenated in set order. A member
                        // in two sets is exported once at its first position — Revit is handed one
                        // id list, so a duplicate would either be rejected or silently repeated.
                        var seen = new HashSet<long>();
                        var ids  = new List<ElementId>();
                        int dupes = 0;
                        foreach (var item in runItems)
                        {
                            if (!FormatOn(item.Set, "PDF")) continue;
                            if (!seen.Add(item.Element.Id.Value)) { dupes++; continue; }
                            ids.Add(item.Element.Id);
                        }
                        if (dupes > 0)
                            pushLog(AppStrings.T("export.bulkExport.log.singleFileDupes", dupes), "warn");

                        if (ids.Count > 0)
                            plan.Add(new PlannedOutput
                            {
                                Format    = "PDF",
                                SetName   = "",
                                Directory = OutputDir("PDF", null),
                                BaseName  = ResolveRunFileName(doc, sets, ids.Count, projNumber, projName, pushLog),
                                MemberIds = ids,
                            });
                        break;
                    }
                }
            }

            // ── Per-item formats ──────────────────────────────────────────────
            // Each element exports once even when it belongs to several sets, attributed to the
            // FIRST set that enables the format — so a set that switched the format off can never
            // suppress an export another set asked for.
            AddPerItemOutputs(doc, plan, runItems, runWidth, "DWG", ExportDwg,               projNumber, projName, pushLog);
            AddPerItemOutputs(doc, plan, runItems, runWidth, "NWC", nwcEffective,            projNumber, projName, pushLog);
            AddPerItemOutputs(doc, plan, runItems, runWidth, "IFC", ifcEffective,            projNumber, projName, pushLog);

            return plan;
        }

        private void AddPerItemOutputs(
            Document doc, List<PlannedOutput> plan, List<RunItem> runItems, int runWidth,
            string fmt, bool enabled, string projNumber, string projName, Action<string, string> pushLog)
        {
            if (!enabled) return;
            var seen = new HashSet<long>();
            foreach (var item in runItems)
            {
                if (!FormatOn(item.Set, fmt)) continue;
                if (!seen.Add(item.Element.Id.Value)) continue;
                plan.Add(new PlannedOutput
                {
                    Format    = fmt,
                    SetName   = item.Set.Name,
                    Directory = OutputDir(fmt, item.Set),
                    BaseName  = ResolveItemFileName(doc, item, runWidth, projNumber, projName, fmt, pushLog),
                    MemberIds = new List<ElementId> { item.Element.Id },
                });
            }
        }

        /// <summary>Whether a format runs for this set — the set's override, else the tool default.</summary>
        private bool FormatOn(ResolvedSet set, string fmt)
        {
            switch (fmt)
            {
                case "PDF": return set.Spec.PdfOverride ?? ExportPdf;
                case "DWG": return set.Spec.DwgOverride ?? ExportDwg;
                case "NWC": return set.Spec.NwcOverride ?? ExportNwc;
                case "IFC": return set.Spec.IfcOverride ?? ExportIfc;
                default:    return false;
            }
        }

        /// <summary>
        /// Target folder for one output: the base folder, then the format subfolder when split is
        /// on, then the set subfolder when per-set folders are on. Not created here — planning is
        /// side-effect free.
        /// </summary>
        private string OutputDir(string fmt, ResolvedSet? set)
        {
            string dir = OutputFolder;
            if (SplitByFormat) dir = Path.Combine(dir, fmt);
            if (SetSubfolders && set != null)
            {
                string sub = !string.IsNullOrWhiteSpace(set.Spec.SubfolderOverride)
                    ? set.Spec.SubfolderOverride!
                    : set.Name;
                if (!string.IsNullOrWhiteSpace(sub)) dir = Path.Combine(dir, SanitizeFilename(sub));
            }
            return dir;
        }

        // ── Naming ────────────────────────────────────────────────────────────

        private TokenContext BaseContext(Document doc, string projNumber, string projName)
        {
            var ctx = new TokenContext { Doc = doc };
            ctx.Computed["ProjectNumber"] = projNumber;
            ctx.Computed["ProjectName"]   = projName;
            return ctx;
        }

        /// <summary>Per-item filename: the item pattern, plus the set and sequence tokens.</summary>
        private string ResolveItemFileName(Document doc, RunItem item, int runWidth,
                                string projNumber, string projName, string fmt,
                                Action<string, string> pushLog)
        {
            var ctx = BaseContext(doc, projNumber, projName);
            ctx.Target = item.Element;
            if (item.Element is View view && !(item.Element is ViewSheet))
                ctx.Computed["ViewType"] = view.ViewType.ToString();

            int setWidth = Math.Max(2, item.Set.Members.Count.ToString().Length);
            ctx.Computed["SetName"]  = item.Set.Name;
            ctx.Computed["SetIndex"] = item.Set.Index.ToString("D2");
            ctx.Computed["Seq"]      = item.RunSeq.ToString(new string('0', runWidth));
            ctx.Computed["SetSeq"]   = item.SetSeq.ToString(new string('0', setWidth));

            string pattern = string.IsNullOrWhiteSpace(item.Set.Spec.PatternOverride)
                ? FilenamePattern
                : item.Set.Spec.PatternOverride!;

            return ResolveAndClaim(pattern, ctx, fmt, item.Element, pushLog);
        }

        /// <summary>Combined-PDF filename for one set — the set pattern, never a member's name.</summary>
        private string ResolveSetFileName(Document doc, ResolvedSet set, int setCount,
                               string projNumber, string projName, Action<string, string> pushLog)
        {
            var ctx = BaseContext(doc, projNumber, projName);
            ctx.Computed["SetName"]    = set.Name;
            ctx.Computed["SetIndex"]   = set.Index.ToString("D2");
            ctx.Computed["SetCount"]   = setCount.ToString();
            ctx.Computed["SheetCount"] = set.Members.Count.ToString();

            string pattern = string.IsNullOrWhiteSpace(set.Spec.PatternOverride)
                ? SetFilenamePattern
                : set.Spec.PatternOverride!;

            return ResolveAndClaim(pattern, ctx, "PDF", null, pushLog, fallback: set.Name);
        }

        /// <summary>Filename for the whole-run single PDF.</summary>
        private string ResolveRunFileName(Document doc, List<ResolvedSet> sets, int itemCount,
                               string projNumber, string projName, Action<string, string> pushLog)
        {
            var ctx = BaseContext(doc, projNumber, projName);
            ctx.Computed["SetName"]    = string.Join("+", sets.Select(s => s.Name).Where(n => !string.IsNullOrWhiteSpace(n)));
            ctx.Computed["SetIndex"]   = "01";
            ctx.Computed["SetCount"]   = sets.Count.ToString();
            ctx.Computed["SheetCount"] = itemCount.ToString();

            string fallback = !string.IsNullOrWhiteSpace(projName) ? projName : "Export";
            return ResolveAndClaim(SetFilenamePattern, ctx, "PDF", null, pushLog, fallback);
        }

        /// <summary>
        /// Resolves a pattern, guards a degenerate result loudly (a name with no usable character
        /// is a failure, not a fallback), sanitises it, and claims it in the format's namespace so
        /// two items can never write to one file.
        /// </summary>
        private string ResolveAndClaim(string pattern, TokenContext ctx, string fmt,
                                       Element? element, Action<string, string> pushLog,
                                       string fallback = "")
        {
            string resolved = TokenResolver.Resolve(pattern, ctx, msg => pushLog(msg, "warn"));
            if (resolved.Any(char.IsLetterOrDigit))
                return MakeUniqueName(fmt, SanitizeFilename(resolved), pushLog);

            string label = element?.Name ?? fallback;
            string safe  = SanitizeFilename(label);
            if (!safe.Any(char.IsLetterOrDigit))
                safe = "export-" + (element?.Id.Value.ToString() ?? "0");

            pushLog(AppStrings.T("export.bulkExport.log.nameFallback", fmt, pattern, label,
                        (element is ViewSheet ? AppStrings.T("export.bulkExport.words.sheet")
                                              : AppStrings.T("export.bulkExport.words.view")), safe), "warn");
            DiagnosticsLog.Warn("BulkExport.ResolveAndClaim",
                $"Degenerate filename. fmt={fmt} pattern='{pattern}' resolved='{resolved}' " +
                $"element={element?.Id} label='{label}' fallback='{safe}'");
            return MakeUniqueName(fmt, safe, pushLog);
        }

        // ── Existing-file scan ────────────────────────────────────────────────

        private static string ExtensionFor(string fmt)
        {
            switch (fmt)
            {
                case "PDF": return ".pdf";
                case "DWG": return ".dwg";
                case "NWC": return ".nwc";
                case "IFC": return ".ifc";
                default:    return "";
            }
        }

        private static string FullPath(PlannedOutput o) => Path.Combine(o.Directory, o.BaseName + ExtensionFor(o.Format));

        /// <summary>
        /// Flags planned outputs whose file already exists, and applies <see cref="ExistingFileAction"/>.
        /// Without this the tool silently overwrote a previous issue — name collisions were only
        /// de-duplicated within a single run, never against what was already on disk.
        /// </summary>
        private void ScanExisting(List<PlannedOutput> plan, Action<string, string> pushLog, ref int skip)
        {
            int existing = 0;
            foreach (var o in plan)
            {
                try { o.AlreadyExists = File.Exists(FullPath(o)); }
                catch (Exception ex)
                {
                    // An unreadable path is not fatal — report it and let the export attempt fail
                    // loudly if the path is genuinely bad.
                    DiagnosticsLog.Swallowed($"BulkExport: existence check for '{o.BaseName}'", ex);
                    pushLog(AppStrings.T("export.bulkExport.log.existsCheckFailed", o.BaseName, ex.Message), "warn");
                    continue;
                }
                if (o.AlreadyExists) existing++;
            }

            if (existing == 0)
            {
                pushLog(AppStrings.T("export.bulkExport.log.noExisting"), "info");
                return;
            }

            if (ExistingFileAction == "Skip")
            {
                pushLog(AppStrings.T("export.bulkExport.log.existingSkip", existing, plan.Count), "warn");
                skip += existing;
            }
            else if (ExistingFileAction == "Add suffix")
            {
                int renamed = 0;
                foreach (var o in plan.Where(p => p.AlreadyExists).ToList())
                {
                    string bumped = NextFreeName(o);
                    if (bumped != o.BaseName)
                    {
                        // The format's namespace always exists by now (planning claimed every
                        // name through MakeUniqueName), but indexing it blind would turn a
                        // future ordering change into a KeyNotFoundException mid-run.
                        if (!_usedNames.TryGetValue(o.Format, out var claimed))
                        {
                            claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            _usedNames[o.Format] = claimed;
                        }
                        claimed.Add(bumped);
                        o.BaseName = bumped;
                        o.AlreadyExists = false;
                        renamed++;
                    }
                }
                pushLog(AppStrings.T("export.bulkExport.log.existingSuffix", renamed), "warn");
            }
            else
            {
                pushLog(AppStrings.T("export.bulkExport.log.existingOverwrite", existing, plan.Count), "warn");
            }
        }

        private string NextFreeName(PlannedOutput o)
        {
            var claimed = _usedNames.TryGetValue(o.Format, out var set) ? set : null;
            for (int n = 2; n < 100000; n++)
            {
                string cand = $"{o.BaseName} ({n})";
                if (claimed != null && claimed.Contains(cand)) continue;
                if (!File.Exists(Path.Combine(o.Directory, cand + ExtensionFor(o.Format)))) return cand;
            }
            return o.BaseName;
        }

        // ── Execute ───────────────────────────────────────────────────────────

        private void ExecutePlan(
            Document doc, List<PlannedOutput> plan,
            Action<string, string> pushLog, Action<int, int, int, int> onProgress,
            ref int pass, ref int fail, ref int skip)
        {
            // Weight progress by member count, not by output count: a 214-sheet combined PDF is
            // one output but nearly the whole run, and counting it as 1-of-1 made the bar lie.
            int total = plan.Sum(o => Math.Max(1, o.ItemCount));
            var prog  = new RunProgressReporter(pushLog, total, AppStrings.T("export.bulkExport.words.items"));

            DWGExportOptions? dwgOpts     = null;
            bool              dwgResolved = false;

            foreach (var o in plan)
            {
                if (RunState.CancelRequested)
                {
                    pushLog(AppStrings.T("common.log.stoppedByUser", prog.Done, total), "warn");
                    break;
                }

                if (o.AlreadyExists && ExistingFileAction == "Skip")
                {
                    pushLog(AppStrings.T("export.bulkExport.log.skippedExisting", o.BaseName + ExtensionFor(o.Format)), "warn");
                    prog.Tick(Math.Max(1, o.ItemCount));
                    onProgress(prog.Percent, pass, fail, skip);
                    continue;
                }

                try { Directory.CreateDirectory(o.Directory); }
                catch (Exception ex)
                {
                    fail++;
                    pushLog(AppStrings.T("export.bulkExport.log.folderFail", ex.Message), "fail");
                    DiagnosticsLog.Swallowed($"BulkExport: create '{o.Directory}'", ex);
                    prog.Tick(Math.Max(1, o.ItemCount));
                    onProgress(prog.Percent, pass, fail, skip);
                    continue;
                }

                switch (o.Format)
                {
                    case "PDF": ExportOnePdf(doc, o, pushLog, ref pass, ref fail);                       break;
                    case "DWG": ExportOneDwg(doc, o, pushLog, ref pass, ref fail, ref skip,
                                             ref dwgOpts, ref dwgResolved);                              break;
                    case "NWC": ExportOneNwc(doc, o, pushLog, ref pass, ref fail, ref skip);             break;
                    case "IFC": ExportOneIfc(doc, o, pushLog, ref pass, ref fail, ref skip);             break;
                }

                prog.Tick(Math.Max(1, o.ItemCount));
                onProgress(prog.Percent, pass, fail, skip);
            }
        }

        private void ExportOnePdf(Document doc, PlannedOutput o, Action<string, string> pushLog,
                                  ref int pass, ref int fail)
        {
            bool combine = o.ItemCount > 1 || Granularity != PdfGranularity.PerSheet;

            // A combined export is a single Revit call: it cannot be cancelled once started and
            // reports nothing until it returns. Say so rather than appearing hung.
            if (combine && o.ItemCount >= 20)
                pushLog(AppStrings.T("export.bulkExport.log.combinedStarting", o.ItemCount, o.BaseName), "info");

            var sw = Stopwatch.StartNew();
            try
            {
                var opts = BuildPdfOptions(o.BaseName, combine);
                bool ok  = doc.Export(o.Directory, o.MemberIds, opts);
                sw.Stop();
                if (ok)
                {
                    pass++; _pdf++;
                    if (o.ItemCount > 1)
                        pushLog(AppStrings.T("export.bulkExport.log.pdfCombinedOk2",
                                             o.BaseName, o.ItemCount, FormatElapsed(sw)), "pass");
                    else
                        pushLog(AppStrings.T("export.bulkExport.log.pdfOk", o.BaseName), "pass");
                }
                else
                {
                    fail++;
                    pushLog(AppStrings.T("export.bulkExport.log.pdfFalse", o.BaseName), "fail");
                }
            }
            catch (Exception ex)
            {
                fail++;
                pushLog(AppStrings.T("export.bulkExport.log.pdfFail", o.BaseName, ex.Message), "fail");
            }
        }

        private void ExportOneDwg(Document doc, PlannedOutput o, Action<string, string> pushLog,
                                  ref int pass, ref int fail, ref int skip,
                                  ref DWGExportOptions? dwgOpts, ref bool dwgResolved)
        {
            if (!dwgResolved)
            {
                dwgOpts     = BuildDwgOptions(doc);
                dwgResolved = true;
                if (dwgOpts == null)
                    pushLog(AppStrings.T("export.bulkExport.log.dwgNoSetupAll", DwgSetupName), "fail");
            }
            if (dwgOpts == null) { skip++; return; }

            try
            {
                bool ok = doc.Export(o.Directory, o.BaseName, o.MemberIds, dwgOpts);
                if (ok) { pass++; _dwg++; pushLog(AppStrings.T("export.bulkExport.log.dwgOk", o.BaseName), "pass"); }
                else    { fail++;         pushLog(AppStrings.T("export.bulkExport.log.dwgFalse", o.BaseName), "fail"); }
            }
            catch (Exception ex)
            {
                fail++;
                pushLog(AppStrings.T("export.bulkExport.log.dwgFail", o.BaseName, ex.Message), "fail");
            }
        }

        private void ExportOneNwc(Document doc, PlannedOutput o, Action<string, string> pushLog,
                                  ref int pass, ref int fail, ref int skip)
        {
            try
            {
                if (!(doc.GetElement(o.MemberIds[0]) is View3D view3d))
                {
                    skip++;
                    pushLog(AppStrings.T("export.bulkExport.log.nwcNot3d", o.BaseName), "warn");
                    return;
                }
                var opts = ExportOptionsFactory.BuildNwcOptions(BuildNwcOptionSet(), view3d.Id, pushLog);
                // NWC export uses the 3-parameter overload — ViewId is set on the options object.
                doc.Export(o.Directory, o.BaseName, opts);
                pass++; _nwc++;
                pushLog(AppStrings.T("export.bulkExport.log.nwcOk", o.BaseName), "pass");
            }
            catch (Exception ex)
            {
                fail++;
                pushLog(AppStrings.T("export.bulkExport.log.nwcFail", o.BaseName, ex.Message), "fail");
            }
        }

        private void ExportOneIfc(Document doc, PlannedOutput o, Action<string, string> pushLog,
                                  ref int pass, ref int fail, ref int skip)
        {
            try
            {
                var el = doc.GetElement(o.MemberIds[0]);
                if (!(el is View3D))
                {
                    skip++;
                    pushLog(AppStrings.T("export.bulkExport.log.ifcNot3d", o.BaseName), "warn");
                    return;
                }
                var opts = ExportOptionsFactory.BuildIfcOptions(IfcVersion, el.Id);

                // IFC export writes IFC-specific data to the document and requires a transaction.
                using (var t = new Transaction(doc, "Batch IFC Export"))
                {
                    t.Start();
                    doc.Export(o.Directory, o.BaseName, opts);
                    t.Commit();
                }
                pass++; _ifc++;
                pushLog(AppStrings.T("export.bulkExport.log.ifcOk", o.BaseName), "pass");
            }
            catch (Exception ex)
            {
                fail++;
                pushLog(AppStrings.T("export.bulkExport.log.ifcFail", o.BaseName, ex.Message), "fail");
            }
        }

        private static string FormatElapsed(Stopwatch sw)
            => sw.Elapsed.TotalSeconds < 60
                   ? $"{sw.Elapsed.TotalSeconds:0.#}s"
                   : $"{(int)sw.Elapsed.TotalMinutes}m {sw.Elapsed.Seconds}s";

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
    }
}
