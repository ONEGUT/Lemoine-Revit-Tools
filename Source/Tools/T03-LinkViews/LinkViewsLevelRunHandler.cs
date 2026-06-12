using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using static LemoineTools.Tools.LinkViews.LinkViewsLevelHelpers;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.LinkViews
{
    /// <summary>
    /// <see cref="IExternalEventHandler"/> that executes the Level-based Link Views
    /// operation inside a Revit external-event context.  The ViewModel populates all
    /// public input properties before calling <c>Raise()</c>, then monitors progress
    /// and completion via the callback delegates.
    /// </summary>
    public sealed class LinkViewsLevelRunHandler : IExternalEventHandler
    {
        // ── Inputs set before Raise() ─────────────────────────────────

        /// <summary>
        /// When <see langword="true"/>, the host document is included as a source
        /// when collecting rooms for view creation.
        /// </summary>
        public bool            IncludeHost      { get; set; } = true;

        /// <summary>
        /// Element IDs of the <see cref="RevitLinkInstance"/> elements whose linked
        /// documents should also be searched for rooms.
        /// </summary>
        public List<ElementId> LinkInstIds      { get; set; } = new List<ElementId>();

        /// <summary>
        /// Element IDs of the host <see cref="Level"/> elements for which views
        /// should be created.  Only levels whose IDs are in this list are processed.
        /// </summary>
        public List<ElementId> SelectedLevelIds { get; set; } = new List<ElementId>();

        /// <summary>
        /// When <see langword="true"/>, a section-box 3D view is created for each
        /// room cluster at each selected level.
        /// </summary>
        public bool            Create3D         { get; set; } = true;

        /// <summary>
        /// When <see langword="true"/>, a cropped floor-plan view is created for each
        /// room cluster at each selected level.
        /// </summary>
        public bool            CreateFP         { get; set; } = true;

        /// <summary>
        /// When <see langword="true"/>, a cropped reflected ceiling plan is created for
        /// each room cluster at each selected level.
        /// </summary>
        public bool            CreateRCP        { get; set; } = true;

        // ── Naming options set before Raise() ─────────────────────────

        /// <summary>
        /// Slot selector for the first (leading) segment of the generated view name.
        /// Accepted values: <c>"Host Level"</c>, <c>"Model Name"</c>, <c>"View Type"</c>,
        /// <c>"Custom"</c>, or <c>"None"</c>.
        /// </summary>
        public string NamingFront        { get; set; } = "Host Level";

        /// <summary>
        /// Custom text used for the front slot when <see cref="NamingFront"/> is
        /// <c>"Custom"</c>.  Ignored for all other slot values.
        /// </summary>
        public string NamingFrontCustom  { get; set; } = "";

        /// <summary>
        /// Slot selector for the middle segment of the generated view name.
        /// Accepted values: <c>"Host Level"</c>, <c>"Model Name"</c>, <c>"View Type"</c>,
        /// <c>"Custom"</c>, or <c>"None"</c>.
        /// </summary>
        public string NamingCenter       { get; set; } = "Model Name";

        /// <summary>
        /// Custom text used for the center slot when <see cref="NamingCenter"/> is
        /// <c>"Custom"</c>.  Ignored for all other slot values.
        /// </summary>
        public string NamingCenterCustom { get; set; } = "";

        /// <summary>
        /// Slot selector for the trailing segment of the generated view name.
        /// Accepted values: <c>"Host Level"</c>, <c>"Model Name"</c>, <c>"View Type"</c>,
        /// <c>"Custom"</c>, or <c>"None"</c>.
        /// </summary>
        public string NamingEnd          { get; set; } = "None";

        /// <summary>
        /// Custom text used for the end slot when <see cref="NamingEnd"/> is
        /// <c>"Custom"</c>.  Ignored for all other slot values.
        /// </summary>
        public string NamingEndCustom    { get; set; } = "";
        /// <summary>levelId.Value → dominant model name, populated from Phase1 scan.</summary>
        public Dictionary<long, string>  LevelModelNames { get; set; } = new Dictionary<long, string>();

        /// <summary>Sub Discipline parameter value applied to created 3D views. Empty = skip.</summary>
        public string SubDisc3D  { get; set; } = "";
        /// <summary>Sub Discipline parameter value applied to created floor plans. Empty = skip.</summary>
        public string SubDiscFP  { get; set; } = "";
        /// <summary>Sub Discipline parameter value applied to created ceiling plans. Empty = skip.</summary>
        public string SubDiscRCP { get; set; } = "";

        /// <summary>View template applied to 3D views before geometry is set. InvalidElementId = none.</summary>
        public ElementId Template3D  { get; set; } = ElementId.InvalidElementId;
        /// <summary>View template applied to floor plans before crop is set. InvalidElementId = none.</summary>
        public ElementId TemplateFP  { get; set; } = ElementId.InvalidElementId;
        /// <summary>View template applied to ceiling plans before crop is set. InvalidElementId = none.</summary>
        public ElementId TemplateRCP { get; set; } = ElementId.InvalidElementId;

        /// <summary>When true, adds created views to named print sets. Default false (opt-in per run).</summary>
        public bool   CreatePrintSets { get; set; } = false;
        /// <summary>Label prefix for multi-cluster building names (e.g. "Bldg" → "Bldg A"). Default "Bldg".</summary>
        public string BuildingLabel   { get; set; } = "Bldg";
        /// <summary>When true, the view-type token (3D/FP/RCP) is appended as the final name segment.</summary>
        public bool   AppendViewType  { get; set; } = true;

        // ── Callbacks ─────────────────────────────────────────────────

        /// <summary>
        /// Optional callback invoked for each log message produced during execution.
        /// Parameters are <c>(message, severity)</c> where severity is one of
        /// <c>"pass"</c>, <c>"fail"</c>, or <c>"info"</c>.
        /// </summary>
        public Action<string, string>?     PushLog    { get; set; }

        /// <summary>
        /// Optional callback invoked as views are created, reporting incremental progress.
        /// Parameters are <c>(percentComplete, passCount, failCount, skipCount)</c>.
        /// </summary>
        public Action<int, int, int, int>? OnProgress { get; set; }

        /// <summary>
        /// Optional callback invoked once after the transaction commits, reporting final
        /// totals.  Parameters are <c>(passCount, failCount, skipCount)</c>.
        /// </summary>
        public Action<int, int, int>?      OnComplete { get; set; }

        /// <summary>
        /// Returns the unique handler name required by the Revit <see cref="IExternalEventHandler"/>
        /// contract, used for event identification and logging.
        /// </summary>
        public string GetName() => "LemoineTools.Tools.LinkViews.LinkViewsLevelRunHandler";

        /// <summary>
        /// Entry point called by the Revit external-event mechanism on the main thread.
        /// Delegates to <c>RunViews</c> inside a try/catch so that any unhandled exception
        /// is logged as a failure rather than propagating to Revit.
        /// </summary>
        /// <param name="app">The active <see cref="UIApplication"/> provided by Revit.</param>
        public void Execute(UIApplication app)
        {
            var doc  = app.ActiveUIDocument.Document;
            long __issues0 = LemoineLog.IssueCount;
            int pass = 0, fail = 0, skip = 0;
            try
            {
                try { RunViews(doc, ref pass, ref fail, ref skip); }
                catch (Exception ex) { LemoineLog.Error("LinkViews level: run aborted", ex); Log($"Error: {ex.Message}", "fail"); fail++; }
                Progress(100, pass, fail, skip);
                long __issues = LemoineLog.IssuesSince(__issues0);
                if (__issues > 0) Log($"{__issues} non-fatal issue(s) recorded — see diagnostics log.", "warn");
                Complete(pass, fail, skip);
            }
            finally
            {
                // This handler is a session-long static (App.LinkViewsLevelRunHandler) —
                // drop the run's payload so it doesn't outlive the run.
                LinkInstIds      = new List<ElementId>();
                SelectedLevelIds = new List<ElementId>();
                LevelModelNames  = new Dictionary<long, string>();
            }
        }

        // ── Main logic ─────────────────────────────────────────────────
        private void RunViews(Document doc, ref int pass, ref int fail, ref int skip)
        {
            var s = LinkViewsLevelSettings.Instance;

            // Rebuild source document list
            var sourceDocs = new List<Document>();
            if (IncludeHost) sourceDocs.Add(doc);
            foreach (var id in LinkInstIds)
            {
                var li = doc.GetElement(id) as RevitLinkInstance;
                var ld = li?.GetLinkDocument();
                if (ld != null && !sourceDocs.Any(d => d.Equals(ld)))
                    sourceDocs.Add(ld);
            }

            // All host levels in elevation order
            var allLevels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).ToList();

            // Collect rooms, then reconcile link rooms to host levels by elevation so
            // differently-named link levels still group under the correct host level.
            var rooms = CollectRooms(doc, sourceDocs);
            AssignHostLevelsByElevation(rooms, allLevels, LevelMatchToleranceFt);

            var roomsByLevel = new Dictionary<string, List<RoomInfo>>(StringComparer.Ordinal);
            foreach (var r in rooms)
            {
                if (!roomsByLevel.ContainsKey(r.LevelName))
                    roomsByLevel[r.LevelName] = new List<RoomInfo>();
                roomsByLevel[r.LevelName].Add(r);
            }

            var selectedIdSet = new HashSet<long>(SelectedLevelIds.Select(id => id.Value));
            var keptLevels    = allLevels.Where(l => selectedIdSet.Contains(l.Id.Value)).ToList();

            // Locate ViewFamilyTypes — warn explicitly when missing so skipped types are visible
            var vft3d  = Create3D  ? FindVFT(doc, ViewFamily.ThreeDimensional) : null;
            var vftFP  = CreateFP  ? FindVFT(doc, ViewFamily.FloorPlan)        : null;
            var vftRCP = CreateRCP ? FindVFT(doc, ViewFamily.CeilingPlan)      : null;

            if (Create3D  && vft3d  == null) Log("No 3D ViewFamilyType found — 3D views will be skipped.", "info");
            if (CreateFP  && vftFP  == null) Log("No FloorPlan ViewFamilyType found — floor plans will be skipped.", "info");
            if (CreateRCP && vftRCP == null) Log("No CeilingPlan ViewFamilyType found — ceiling plans will be skipped.", "info");

            // Estimate total for progress
            int totalEst = 0;
            foreach (var lvl in keptLevels)
            {
                if (!roomsByLevel.TryGetValue(lvl.Name, out var lr)) continue;
                totalEst += ClusterRooms(lr, s.ClusterThreshold).Count
                            * ((Create3D ? 1 : 0) + (CreateFP ? 1 : 0) + (CreateRCP ? 1 : 0));
            }

            int done = 0;
            var created3d  = new List<View>();
            var createdFP  = new List<View>();
            var createdRCP = new List<View>();
            var txLog      = new List<string>();

            using (var tx = new Transaction(doc, "Link Views — Level"))
            {
                ConfigureFailures(tx);
                tx.Start();

                for (int idx = 0; idx < keptLevels.Count; idx++)
                {
                    Level  lvl      = keptLevels[idx];
                    string lname    = lvl.Name;
                    bool   hasRooms = roomsByLevel.TryGetValue(lname, out var levelRooms);

                    // Z range — use position in the global ordered list
                    int ord     = allLevels.IndexOf(lvl);
                    double zBot = (ord == 0)                   ? lvl.Elevation - UnlimitedZ : lvl.Elevation;
                    double zTop = (ord == allLevels.Count - 1) ? lvl.Elevation + UnlimitedZ : allLevels[ord + 1].Elevation;

                    if (hasRooms)
                    {
                        var clusters = ClusterRooms(levelRooms, s.ClusterThreshold);
                        clusters.Sort((a, b) =>
                            b.Average(r => r.CentroidY).CompareTo(a.Average(r => r.CentroidY)));

                        for (int bi = 0; bi < clusters.Count; bi++)
                        {
                            string baseName = clusters.Count > 1
                                ? $"L{lname} - {BuildingLabel} {BldgLetter(bi)}"
                                : $"L{lname}";

                            (double x0, double y0, double x1, double y1) =
                                ClusterBoundsXY(clusters[bi], s.BufferXY);

                            // ── 3D ───────────────────────────────────────────────
                            if (Create3D && vft3d != null)
                            {
                                string n = BuildViewName(baseName, "3D", lvl.Id);
                                if (View3dExists(doc, n)) { Log($"Skip '{n}' (exists)", "info"); skip++; }
                                else
                                {
                                    try
                                    {
                                        View3D v = Create3d(doc, n, vft3d.Id);
                                        ApplyTemplate(v, Template3D);
                                        v.SetSectionBox(new BoundingBoxXYZ
                                        {
                                            Min = new XYZ(x0, y0, zBot),
                                            Max = new XYZ(x1, y1, zTop),
                                        });
                                        SetSubDisc(v, SubDisc3D);
                                        created3d.Add(v);
                                        Log($"Created 3D: {n}", "pass"); pass++;
                                    }
                                    catch (Exception e) { Log($"[3D] '{n}': {e.Message}", "fail"); fail++; }
                                }
                            }

                            // ── Floor Plan ────────────────────────────────────────
                            if (CreateFP && vftFP != null)
                            {
                                string n = BuildViewName(baseName, "FP", lvl.Id);
                                if (PlanExists(doc, n, ViewFamily.FloorPlan)) { Log($"Skip '{n}' (exists)", "info"); skip++; }
                                else
                                {
                                    try
                                    {
                                        ViewPlan fp = ViewPlan.Create(doc, vftFP.Id, lvl.Id);
                                        fp.Name = n;
                                        ApplyTemplate(fp, TemplateFP);
                                        SetPlanCrop(fp, x0, y0, x1, y1, zBot, zTop, lvl.Elevation, s.CutOffset);
                                        SetSubDisc(fp, SubDiscFP);
                                        createdFP.Add(fp);
                                        Log($"Created FP: {n}", "pass"); pass++;
                                    }
                                    catch (Exception e) { Log($"[FP] '{n}': {e.Message}", "fail"); fail++; }
                                }
                            }

                            // ── Ceiling Plan ──────────────────────────────────────
                            if (CreateRCP && vftRCP != null)
                            {
                                string n = BuildViewName(baseName, "RCP", lvl.Id);
                                if (PlanExists(doc, n, ViewFamily.CeilingPlan)) { Log($"Skip '{n}' (exists)", "info"); skip++; }
                                else
                                {
                                    try
                                    {
                                        ViewPlan rcp = ViewPlan.Create(doc, vftRCP.Id, lvl.Id);
                                        rcp.Name = n;
                                        ApplyTemplate(rcp, TemplateRCP);
                                        SetPlanCrop(rcp, x0, y0, x1, y1, zBot, zTop, lvl.Elevation, s.CutOffset);
                                        SetSubDisc(rcp, SubDiscRCP);
                                        createdRCP.Add(rcp);
                                        Log($"Created RCP: {n}", "pass"); pass++;
                                    }
                                    catch (Exception e) { Log($"[RCP] '{n}': {e.Message}", "fail"); fail++; }
                                }
                            }

                            done++;
                            Progress((int)(done * 90.0 / Math.Max(totalEst, 1)), pass, fail, skip);
                        }
                    }
                    else
                    {
                        // Fallback: level has no rooms — create uncropped views
                        string baseName = $"L{lname}";

                        if (Create3D && vft3d != null)
                        {
                            string n = BuildViewName(baseName, "3D", lvl.Id);
                            if (View3dExists(doc, n)) { Log($"Skip '{n}' (exists)", "info"); skip++; }
                            else
                            {
                                try
                                {
                                    View3D v = Create3d(doc, n, vft3d.Id);
                                    ApplyTemplate(v, Template3D);
                                    SetSubDisc(v, SubDisc3D);
                                    created3d.Add(v);
                                    Log($"Created 3D (no rooms): {n}", "pass"); pass++;
                                }
                                catch (Exception e) { Log($"[3D] '{n}': {e.Message}", "fail"); fail++; }
                            }
                        }

                        if (CreateFP && vftFP != null)
                        {
                            string n = BuildViewName(baseName, "FP", lvl.Id);
                            if (PlanExists(doc, n, ViewFamily.FloorPlan)) { Log($"Skip '{n}' (exists)", "info"); skip++; }
                            else
                            {
                                try
                                {
                                    ViewPlan fp = ViewPlan.Create(doc, vftFP.Id, lvl.Id);
                                    fp.Name = n;
                                    ApplyTemplate(fp, TemplateFP);
                                    SetSubDisc(fp, SubDiscFP);
                                    createdFP.Add(fp);
                                    Log($"Created FP (no rooms): {n}", "pass"); pass++;
                                }
                                catch (Exception e) { Log($"[FP] '{n}': {e.Message}", "fail"); fail++; }
                            }
                        }

                        if (CreateRCP && vftRCP != null)
                        {
                            string n = BuildViewName(baseName, "RCP", lvl.Id);
                            if (PlanExists(doc, n, ViewFamily.CeilingPlan)) { Log($"Skip '{n}' (exists)", "info"); skip++; }
                            else
                            {
                                try
                                {
                                    ViewPlan rcp = ViewPlan.Create(doc, vftRCP.Id, lvl.Id);
                                    rcp.Name = n;
                                    ApplyTemplate(rcp, TemplateRCP);
                                    SetSubDisc(rcp, SubDiscRCP);
                                    createdRCP.Add(rcp);
                                    Log($"Created RCP (no rooms): {n}", "pass"); pass++;
                                }
                                catch (Exception e) { Log($"[RCP] '{n}': {e.Message}", "fail"); fail++; }
                            }
                        }

                        done++;
                        Progress((int)(done * 90.0 / Math.Max(totalEst, 1)), pass, fail, skip);
                    }
                }

                if (CreatePrintSets)
                {
                    if (created3d.Count  > 0) GetOrCreateViewSheetSet(doc, "Coordination - 3D Views",     created3d,  txLog);
                    if (createdFP.Count  > 0) GetOrCreateViewSheetSet(doc, "Coordination - Floor Plans",   createdFP,  txLog);
                    if (createdRCP.Count > 0) GetOrCreateViewSheetSet(doc, "Coordination - Ceiling Plans", createdRCP, txLog);
                }

                tx.Commit();
            }

            foreach (var line in txLog) Log(line, "info");
            Log($"Complete — {pass} created, {skip} skipped, {fail} failed.", "pass");
        }

        /// <summary>
        /// Local equivalent of <c>LinkViewsLevelShared.SetPlanCropAndRange</c> that
        /// accepts <paramref name="cutOffset"/> as a parameter instead of reading
        /// from PluginSettings.
        /// </summary>
        // log parameter removed — view range exceptions now propagate to the per-view
        // try/catch in RunViews where fail++ and "fail" logging live, so range errors
        // are no longer silently counted as pass.
        private static void SetPlanCrop(
            ViewPlan plan,
            double x0, double y0, double x1, double y1,
            double zBot, double zTop, double levelElev,
            double cutOffset)
        {
            plan.CropBoxActive  = true;
            plan.CropBoxVisible = true;
            var cb = plan.CropBox;
            cb.Min = new XYZ(x0, y0, -1.0);
            cb.Max = new XYZ(x1, y1,  1.0);
            plan.CropBox = cb;

            PlanViewRange vr    = plan.GetViewRange();
            ElementId     lvlId = plan.GenLevel?.Id ?? ElementId.InvalidElementId;
            if (lvlId == ElementId.InvalidElementId) return;

            double topOff = zTop  - levelElev;
            double botOff = zBot  - levelElev;
            double cutOff = Math.Min(cutOffset, topOff - 0.1);

            vr.SetLevelId(PlanViewPlane.TopClipPlane,    lvlId);
            vr.SetOffset(PlanViewPlane.TopClipPlane,     topOff);
            vr.SetLevelId(PlanViewPlane.CutPlane,        lvlId);
            vr.SetOffset(PlanViewPlane.CutPlane,         cutOff);
            vr.SetLevelId(PlanViewPlane.BottomClipPlane, lvlId);
            vr.SetOffset(PlanViewPlane.BottomClipPlane,  botOff);
            vr.SetLevelId(PlanViewPlane.ViewDepthPlane,  lvlId);
            vr.SetOffset(PlanViewPlane.ViewDepthPlane,   botOff);
            plan.SetViewRange(vr);
        }

        /// <summary>
        /// Assembles the final view name from the three naming slots.
        /// baseName is the cluster descriptor (e.g. "L2 - Bldg A").
        /// typeLabel (3D/FP/RCP) is appended only when <see cref="AppendViewType"/> is true.
        /// </summary>
        private string BuildViewName(string baseName, string typeLabel, ElementId levelId)
        {
            string modelName = LevelModelNames.TryGetValue(levelId.Value, out var m) ? m : "";

            string ResolveSlot(string slot, string custom)
            {
                switch (slot)
                {
                    case "Host Level":  return baseName;
                    case "Model Name":  return modelName;
                    case "View Type":   return typeLabel;
                    case "Custom":      return string.IsNullOrWhiteSpace(custom) ? "" : custom.Trim();
                    default:            return "";
                }
            }

            bool anySet = NamingFront != "None" || NamingCenter != "None" || NamingEnd != "None";

            var parts = anySet
                ? new[] { ResolveSlot(NamingFront,  NamingFrontCustom),
                          ResolveSlot(NamingCenter, NamingCenterCustom),
                          ResolveSlot(NamingEnd,    NamingEndCustom) }
                  .Where(s => !string.IsNullOrEmpty(s)).ToList()
                : new List<string> { baseName };

            if (AppendViewType) parts.Add(typeLabel);

            // Safety: nothing resolved (all Custom slots blank, type off) → baseName + typeLabel
            if (parts.Count == 0) { parts.Add(baseName); parts.Add(typeLabel); }

            return string.Join(" - ", parts);
        }

        private static void ApplyTemplate(View view, ElementId templateId)
        {
            if (templateId == null || templateId.Value == ElementId.InvalidElementId.Value) return;
            try { view.ViewTemplateId = templateId; } catch (Exception __lex) { LemoineLog.Swallowed($"LinkViews level: apply view template to view {view.Id.Value}", __lex); }
        }

        private static void SetSubDisc(View view, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            try { view.LookupParameter("Sub Discipline")?.Set(value.Trim()); } catch (Exception __lex) { LemoineLog.Swallowed($"LinkViews level: set Sub Discipline on view {view.Id.Value}", __lex); }
        }

        private static void ConfigureFailures(Transaction tx)
        {
            var opts = tx.GetFailureHandlingOptions();
            opts.SetClearAfterRollback(true);
            opts.SetDelayedMiniWarnings(true);
            tx.SetFailureHandlingOptions(opts);
        }

        private void Log(string t, string s) => PushLog?.Invoke(t, s);
        private void Progress(int p, int pa, int f, int s) => OnProgress?.Invoke(p, pa, f, s);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
