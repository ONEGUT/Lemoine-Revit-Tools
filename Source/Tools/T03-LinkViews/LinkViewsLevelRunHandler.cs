using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using static LemoineTools.Tools.LinkViews.LinkViewsLevelHelpers;

namespace LemoineTools.Tools.LinkViews
{
    /// <summary>
    /// Creates 3D / floor-plan / ceiling-plan views per selected level — either uncropped
    /// ("ByLevel" mode) or bounded by selected scope boxes ("ByScopeBox" mode: one view set
    /// per box × level; plans get the box assigned to their Scope Box parameter so the crop
    /// stays live, 3D views get the box bounds copied into their section box since 3D views
    /// cannot carry the parameter).
    ///
    /// The room search / building clustering this tool used to run moved to the Scope Box
    /// Creator (Tools/T10-ScopeBoxes) — view extents now come from levels or scope boxes only.
    /// </summary>
    public sealed class LinkViewsLevelRunHandler : IExternalEventHandler
    {
        // ── Inputs set before Raise() ─────────────────────────────────

        /// <summary>Extents mode token: "ByLevel" (uncropped) or "ByScopeBox".</summary>
        public string          Mode              { get; set; } = "ByLevel";
        /// <summary>Host levels views are created for.</summary>
        public List<ElementId> SelectedLevelIds  { get; set; } = new List<ElementId>();
        /// <summary>Scope boxes used in "ByScopeBox" mode (ignored otherwise).</summary>
        public List<ElementId> SelectedBoxIds    { get; set; } = new List<ElementId>();

        public bool Create3D  { get; set; } = true;
        public bool CreateFP  { get; set; } = true;
        public bool CreateRCP { get; set; } = true;

        // Naming slots. Token values are logic identifiers:
        // "Level" | "Scope Box" | "View Type" | "Custom" | "None".
        public string NamingFront        { get; set; } = "Level";
        public string NamingFrontCustom  { get; set; } = "";
        public string NamingCenter       { get; set; } = "Scope Box";
        public string NamingCenterCustom { get; set; } = "";
        public string NamingEnd          { get; set; } = "None";
        public string NamingEndCustom    { get; set; } = "";
        /// <summary>When true, the view-type token (3D/FP/RCP) is appended as the final name segment.</summary>
        public bool   AppendViewType     { get; set; } = true;

        /// <summary>Sub Discipline parameter values per view type. Empty = skip.</summary>
        public string SubDisc3D  { get; set; } = "";
        public string SubDiscFP  { get; set; } = "";
        public string SubDiscRCP { get; set; } = "";

        /// <summary>View templates per view type. InvalidElementId = none.
        /// Assigned BEFORE geometry (template assignment can reset view geometry).</summary>
        public ElementId Template3D  { get; set; } = ElementId.InvalidElementId;
        public ElementId TemplateFP  { get; set; } = ElementId.InvalidElementId;
        public ElementId TemplateRCP { get; set; } = ElementId.InvalidElementId;

        // ── Callbacks ─────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.LinkViews.LinkViewsLevelRunHandler";

        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument.Document;
            long issues0 = LemoineLog.IssueCount;
            int pass = 0, fail = 0, skip = 0;
            try
            {
                try { RunViews(doc, ref pass, ref fail, ref skip); }
                catch (Exception ex) { LemoineLog.Error("LinkViews level: run aborted", ex); Log(LemoineStrings.T("linkviews.level.log.error", ex.Message), "fail"); fail++; }
                Progress(100, pass, fail, skip);
                long issues = LemoineLog.IssuesSince(issues0);
                if (issues > 0) Log(LemoineStrings.T("linkviews.level.log.nonFatalIssues", issues), "warn");
                Complete(pass, fail, skip);
            }
            finally
            {
                // Session-long static handler — drop the run's payload.
                SelectedLevelIds = new List<ElementId>();
                SelectedBoxIds   = new List<ElementId>();
            }
        }

        // ── One scope-box target ──────────────────────────────────────
        private sealed class BoxTarget
        {
            public ElementId Id = ElementId.InvalidElementId;
            public string    Name = "";
            public BoundingBoxXYZ? Bounds;
        }

        // ── Main logic ─────────────────────────────────────────────────
        private void RunViews(Document doc, ref int pass, ref int fail, ref int skip)
        {
            bool byBox = Mode == "ByScopeBox";

            // All host levels in elevation order (Z ranges come from neighbours)
            var allLevels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).ToList();

            var selectedIdSet = new HashSet<long>(SelectedLevelIds.Select(id => id.Value));
            var keptLevels    = allLevels.Where(l => selectedIdSet.Contains(l.Id.Value)).ToList();
            if (keptLevels.Count == 0)
            {
                Log(LemoineStrings.T("linkviews.level.log.noLevels"), "fail");
                fail++;
                return;
            }

            // Resolve scope-box targets ("ByScopeBox" mode)
            var boxTargets = new List<BoxTarget>();
            if (byBox)
            {
                foreach (var id in SelectedBoxIds)
                {
                    var el = doc.GetElement(id);
                    if (el == null)
                    {
                        Log(LemoineStrings.T("linkviews.level.log.boxMissing", id.Value), "warn");
                        continue;
                    }
                    boxTargets.Add(new BoxTarget { Id = el.Id, Name = el.Name, Bounds = el.get_BoundingBox(null) });
                }
                if (boxTargets.Count == 0)
                {
                    Log(LemoineStrings.T("linkviews.level.log.noBoxes"), "fail");
                    fail++;
                    return;
                }
            }

            // Locate ViewFamilyTypes — warn explicitly when missing so skipped types are visible
            var vft3d  = Create3D  ? FindVFT(doc, ViewFamily.ThreeDimensional) : null;
            var vftFP  = CreateFP  ? FindVFT(doc, ViewFamily.FloorPlan)        : null;
            var vftRCP = CreateRCP ? FindVFT(doc, ViewFamily.CeilingPlan)      : null;

            if (Create3D  && vft3d  == null) Log(LemoineStrings.T("linkviews.level.log.no3dType"),  "info");
            if (CreateFP  && vftFP  == null) Log(LemoineStrings.T("linkviews.level.log.noFpType"),  "info");
            if (CreateRCP && vftRCP == null) Log(LemoineStrings.T("linkviews.level.log.noRcpType"), "info");

            // Progress is tracked per view SET (one level × one target).
            int totalSets = Math.Max(keptLevels.Count * (byBox ? boxTargets.Count : 1), 1);
            int done      = 0;

            using (var tx = new Transaction(doc, "Bulk Views by Level"))
            {
                ConfigureFailures(tx);
                tx.Start();

                for (int idx = 0; idx < keptLevels.Count; idx++)
                {
                    if (LemoineRun.CancelRequested)
                    {
                        Log(LemoineStrings.T("common.log.stoppedByUser", idx, keptLevels.Count), "warn");
                        break;   // falls through to the existing tx.Commit() below
                    }

                    Level lvl = keptLevels[idx];

                    // Z range for 3D section boxes — position in the global ordered list
                    int ord     = allLevels.IndexOf(lvl);
                    double zBot = lvl.Elevation;
                    double zTop = (ord == allLevels.Count - 1)
                        ? lvl.Elevation + UnlimitedZ
                        : allLevels[ord + 1].Elevation;

                    if (byBox)
                    {
                        foreach (var box in boxTargets)
                        {
                            CreateViewSet(doc, lvl, box, zBot, zTop,
                                          vft3d, vftFP, vftRCP, ref pass, ref fail, ref skip);
                            done++;
                            Progress((int)(done * 90.0 / totalSets), pass, fail, skip);
                        }
                    }
                    else
                    {
                        CreateViewSet(doc, lvl, null, zBot, zTop,
                                      vft3d, vftFP, vftRCP, ref pass, ref fail, ref skip);
                        done++;
                        Progress((int)(done * 90.0 / totalSets), pass, fail, skip);
                    }
                }

                tx.Commit();
            }

            Log(LemoineStrings.T("linkviews.level.log.complete", pass, skip, fail), "pass");
        }

        /// <summary>Creates the enabled view types for one (level, scope-box?) pair.</summary>
        private void CreateViewSet(
            Document doc, Level lvl, BoxTarget? box,
            double zBot, double zTop,
            ViewFamilyType? vft3d, ViewFamilyType? vftFP, ViewFamilyType? vftRCP,
            ref int pass, ref int fail, ref int skip)
        {
            // ── 3D ────────────────────────────────────────────────────
            if (vft3d != null)
            {
                string n = BuildViewName(lvl.Name, box?.Name, "3D");
                if (View3dExists(doc, n)) { Log(LemoineStrings.T("linkviews.level.log.skipExists", n), "info"); skip++; }
                else
                {
                    try
                    {
                        View3D v = Create3d(doc, n, vft3d.Id);
                        ApplyTemplate(v, Template3D);
                        if (box?.Bounds != null)
                        {
                            // 3D views cannot carry the Scope Box parameter — copy the box's
                            // XY bounds and slice Z to this level's range instead.
                            v.SetSectionBox(new BoundingBoxXYZ
                            {
                                Min = new XYZ(box.Bounds.Min.X, box.Bounds.Min.Y, zBot),
                                Max = new XYZ(box.Bounds.Max.X, box.Bounds.Max.Y, zTop),
                            });
                        }
                        SetSubDisc(v, SubDisc3D);
                        Log(LemoineStrings.T("linkviews.level.log.created3d", n), "pass"); pass++;
                    }
                    catch (Exception e) { Log(LemoineStrings.T("linkviews.level.log.fail3d", n, e.Message), "fail"); fail++; }
                }
            }

            // ── Floor Plan ────────────────────────────────────────────
            if (vftFP != null)
                CreatePlan(doc, lvl, box, ViewFamily.FloorPlan, vftFP, TemplateFP, SubDiscFP,
                           "FP", ref pass, ref fail, ref skip);

            // ── Ceiling Plan ──────────────────────────────────────────
            if (vftRCP != null)
                CreatePlan(doc, lvl, box, ViewFamily.CeilingPlan, vftRCP, TemplateRCP, SubDiscRCP,
                           "RCP", ref pass, ref fail, ref skip);
        }

        private void CreatePlan(
            Document doc, Level lvl, BoxTarget? box,
            ViewFamily family, ViewFamilyType vft, ElementId templateId, string subDisc,
            string typeLabel, ref int pass, ref int fail, ref int skip)
        {
            string n = BuildViewName(lvl.Name, box?.Name, typeLabel);
            if (PlanExists(doc, n, family)) { Log(LemoineStrings.T("linkviews.level.log.skipExists", n), "info"); skip++; return; }

            try
            {
                ViewPlan plan = ViewPlan.Create(doc, vft.Id, lvl.Id);
                plan.Name = n;
                ApplyTemplate(plan, templateId);   // template BEFORE geometry / scope box

                if (box != null)
                {
                    // Live extents: assign the scope box to the view's Scope Box parameter.
                    // A failure here (template locks it, view type refuses it) leaves a valid
                    // uncropped view — report it rather than failing the whole view.
                    try
                    {
                        var p = plan.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                        if (p == null || p.IsReadOnly)
                            Log(LemoineStrings.T("linkviews.level.log.boxAssignRefused", n,
                                LemoineStrings.T("linkviews.level.log.boxParamUnavailable")), "warn");
                        else
                            p.Set(box.Id);
                    }
                    catch (Exception aex)
                    {
                        Log(LemoineStrings.T("linkviews.level.log.boxAssignRefused", n, aex.Message), "warn");
                    }
                }

                SetSubDisc(plan, subDisc);
                Log(LemoineStrings.T(typeLabel == "FP"
                    ? "linkviews.level.log.createdFp"
                    : "linkviews.level.log.createdRcp", n), "pass");
                pass++;
            }
            catch (Exception e)
            {
                Log(LemoineStrings.T(typeLabel == "FP"
                    ? "linkviews.level.log.failFp"
                    : "linkviews.level.log.failRcp", n, e.Message), "fail");
                fail++;
            }
        }

        /// <summary>
        /// Assembles the final view name from the three naming slots.
        /// typeLabel (3D/FP/RCP) is appended only when <see cref="AppendViewType"/> is true.
        /// </summary>
        private string BuildViewName(string levelName, string? boxName, string typeLabel)
        {
            string ResolveSlot(string slot, string custom)
            {
                switch (slot)
                {
                    case "Level":     return $"L{levelName}";
                    case "Scope Box": return boxName ?? "";
                    case "View Type": return typeLabel;
                    case "Custom":    return string.IsNullOrWhiteSpace(custom) ? "" : custom.Trim();
                    default:          return "";
                }
            }

            bool anySet = NamingFront != "None" || NamingCenter != "None" || NamingEnd != "None";

            var parts = anySet
                ? new[] { ResolveSlot(NamingFront,  NamingFrontCustom),
                          ResolveSlot(NamingCenter, NamingCenterCustom),
                          ResolveSlot(NamingEnd,    NamingEndCustom) }
                  .Where(s => !string.IsNullOrEmpty(s)).ToList()
                : new List<string>();

            if (parts.Count == 0)
            {
                // Nothing resolved — fall back to level (+ box) so names stay meaningful.
                parts.Add($"L{levelName}");
                if (!string.IsNullOrEmpty(boxName)) parts.Add(boxName!);
            }

            if (AppendViewType) parts.Add(typeLabel);
            if (parts.Count == 0) parts.Add(typeLabel);

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
