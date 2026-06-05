using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.LinkViews
{
    /// <summary>Assignment of one link to a discipline code.</summary>
    public sealed class DisciplineAssignment
    {
        public ElementId LinkInstId  { get; set; } = null!;
        public string    LinkName    { get; set; } = null!;
        public string    Discipline  { get; set; } = null!; // "ARCH","MEP","STRUCT","OTHER","SKIP"
    }

    public sealed class LinkViewsDisciplineRunHandler : IExternalEventHandler
    {
        // ── Inputs ────────────────────────────────────────────────────
        public List<DisciplineAssignment> Assignments { get; set; } = new List<DisciplineAssignment>();
        /// <summary>Sub Discipline parameter value applied to all created views. Empty = skip.</summary>
        public string SubDisc { get; set; } = "";
        /// <summary>View template applied to 3D views before section box is set. InvalidElementId = none.</summary>
        public ElementId Template3D { get; set; } = ElementId.InvalidElementId;

        // ── Callbacks ─────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.LinkViews.LinkViewsDisciplineRunHandler";

        public void Execute(UIApplication app)
        {
            var doc  = app.ActiveUIDocument.Document;
            long __issues0 = LemoineLog.IssueCount;
            int pass = 0, fail = 0, skip = 0;
            try { RunViews(doc, ref pass, ref fail, ref skip); }
            catch (Exception ex) { LemoineLog.Error("LinkViews discipline: run aborted", ex); Log($"Error: {ex.Message}", "fail"); fail++; }
            Progress(100, pass, fail, skip);
            long __issues = LemoineLog.IssuesSince(__issues0);
            if (__issues > 0) Log($"{__issues} non-fatal issue(s) recorded — see diagnostics log.", "warn");
            Complete(pass, fail, skip);
        }

        private void RunViews(Document doc, ref int pass, ref int fail, ref int skip)
        {
            var s = LinkViewsDisciplineSettings.Instance;

            // Filter out SKIP assignments
            var active = Assignments.Where(a => a.Discipline != "SKIP").ToList();
            if (active.Count == 0) { Log("All links set to SKIP.", "info"); return; }

            // Locate default 3D VFT
            var vft3d = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);
            if (vft3d == null) { Log("No 3D ViewFamilyType found.", "fail"); fail++; return; }

            // Group by discipline
            var byDisc = active
                .GroupBy(a => a.Discipline, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            int total = s.CombinedDisciplines.Count(d => byDisc.ContainsKey(d)) + active.Count;
            int done  = 0;

            using (var tx = new Transaction(doc, "Link Views — Discipline"))
            {
                ConfigureFailures(tx);
                tx.Start();

                // ── Combined views for designated disciplines ─────────
                foreach (string disc in s.CombinedDisciplines)
                {
                    if (!byDisc.TryGetValue(disc, out var discLinks)) continue;

                    string viewName = $"Combined {disc}";
                    if (View3dExists(doc, viewName))
                    {
                        Log($"Skip '{viewName}' (exists)", "info"); skip++;
                        done++; Progress((int)(done * 90.0 / Math.Max(total, 1)), pass, fail, skip);
                        continue;
                    }

                    BoundingBoxXYZ combined = null;
                    foreach (var a in discLinks)
                    {
                        var li = doc.GetElement(a.LinkInstId) as RevitLinkInstance;
                        if (li == null) continue;
                        var bb = GetLinkBoundingBox(li);
                        if (bb == null) continue;
                        combined = combined == null ? bb : UnionBBox(combined, bb);
                    }

                    if (combined == null)
                    {
                        Log($"No geometry for combined '{disc}'.", "info");
                        done++; Progress((int)(done * 90.0 / Math.Max(total, 1)), pass, fail, skip);
                        continue;
                    }

                    try
                    {
                        var keepIds = discLinks
                            .Select(a => doc.GetElement(a.LinkInstId))
                            .Where(e => e != null)
                            .Select(e => e.Id)
                            .ToList();

                        View3D v = CreateIsometric(doc, viewName, vft3d.Id);
                        ApplyTemplate(v, Template3D);
                        v.SetSectionBox(ExpandBBox(combined, SectionBoxBuffer));
                        HideNonGridLevelAnnotations(v, doc);
                        HideOtherLinks(v, doc, keepIds);
                        SetSubDisc(v, SubDisc);
                        Log($"Created combined view: {viewName}  ({discLinks.Count} link(s))", "pass");
                        pass++;
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed combined '{disc}': {ex.Message}", "fail");
                        fail++;
                    }

                    done++; Progress((int)(done * 90.0 / Math.Max(total, 1)), pass, fail, skip);
                }

                // ── Per-link views ────────────────────────────────────
                foreach (var a in active)
                {
                    string viewName = $"Link - {a.LinkName}";
                    if (View3dExists(doc, viewName))
                    {
                        Log($"Skip '{viewName}' (exists)", "info"); skip++;
                        done++; Progress((int)(done * 90.0 / Math.Max(total, 1)), pass, fail, skip);
                        continue;
                    }

                    var li = doc.GetElement(a.LinkInstId) as RevitLinkInstance;
                    var bb = li != null ? GetLinkBoundingBox(li) : null;
                    if (bb == null)
                    {
                        Log($"No geometry for '{a.LinkName}'.", "info");
                        done++; Progress((int)(done * 90.0 / Math.Max(total, 1)), pass, fail, skip);
                        continue;
                    }

                    try
                    {
                        View3D v = CreateIsometric(doc, viewName, vft3d.Id);
                        ApplyTemplate(v, Template3D);
                        v.SetSectionBox(ExpandBBox(bb, SectionBoxBuffer));
                        HideNonGridLevelAnnotations(v, doc);
                        HideOtherLinks(v, doc, new List<ElementId> { a.LinkInstId });
                        SetSubDisc(v, SubDisc);
                        Log($"Created: {viewName}", "pass");
                        pass++;
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed '{a.LinkName}': {ex.Message}", "fail");
                        fail++;
                    }

                    done++; Progress((int)(done * 90.0 / Math.Max(total, 1)), pass, fail, skip);
                }

                tx.Commit();
            }

            Log($"Complete — {pass} created, {skip} skipped, {fail} failed.", "pass");
        }

        // ── Helpers ───────────────────────────────────────────────────

        private static void ApplyTemplate(View view, ElementId templateId)
        {
            if (templateId == null || templateId.Value == ElementId.InvalidElementId.Value) return;
            try { view.ViewTemplateId = templateId; } catch (Exception __lex) { LemoineLog.Swallowed($"LinkViews discipline: apply view template to view {view.Id.Value}", __lex); }
        }

        private static void SetSubDisc(View view, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            try { view.LookupParameter("Sub Discipline")?.Set(value.Trim()); } catch (Exception __lex) { LemoineLog.Swallowed($"LinkViews discipline: set Sub Discipline on view {view.Id.Value}", __lex); }
        }

        private static double SectionBoxBuffer =>
            LinkViewsDisciplineSettings.Instance.SectionBoxBuffer; // serialized; defaults to 3 ft

        private static bool View3dExists(Document doc, string name) =>
            new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .Any(v => !v.IsTemplate && v.Name == name);

        private static View3D CreateIsometric(Document doc, string name, ElementId vftId)
        {
            View3D v = View3D.CreateIsometric(doc, vftId);
            try { v.Name = name; } catch (Exception __lex) { LemoineLog.Swallowed($"LinkViews discipline: set name on view {v.Id.Value}", __lex); }
            return v;
        }

        private static BoundingBoxXYZ GetLinkBoundingBox(RevitLinkInstance li)
        {
            BoundingBoxXYZ bb = li.get_BoundingBox(null);
            if (bb != null) return bb;

            Document ld = li.GetLinkDocument();
            if (ld == null) return null;

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            bool found = false;

            foreach (Element el in new FilteredElementCollector(ld)
                .WhereElementIsNotElementType().ToElements())
            {
                try
                {
                    var eb = el.get_BoundingBox(null);
                    if (eb == null) continue;
                    found = true;
                    minX = Math.Min(minX, eb.Min.X); minY = Math.Min(minY, eb.Min.Y); minZ = Math.Min(minZ, eb.Min.Z);
                    maxX = Math.Max(maxX, eb.Max.X); maxY = Math.Max(maxY, eb.Max.Y); maxZ = Math.Max(maxZ, eb.Max.Z);
                }
                catch (Exception __lex) { LemoineLog.Swallowed("LinkViews discipline: read element bounding box", __lex); }
            }
            if (!found) return null;

            Transform tf = li.GetTotalTransform();
            var corners  = new[]
            {
                new XYZ(minX,minY,minZ), new XYZ(maxX,minY,minZ),
                new XYZ(minX,maxY,minZ), new XYZ(maxX,maxY,minZ),
                new XYZ(minX,minY,maxZ), new XYZ(maxX,minY,maxZ),
                new XYZ(minX,maxY,maxZ), new XYZ(maxX,maxY,maxZ),
            };
            var tr = corners.Select(tf.OfPoint).ToList();
            return new BoundingBoxXYZ
            {
                Min = new XYZ(tr.Min(p => p.X), tr.Min(p => p.Y), tr.Min(p => p.Z)),
                Max = new XYZ(tr.Max(p => p.X), tr.Max(p => p.Y), tr.Max(p => p.Z)),
            };
        }

        private static BoundingBoxXYZ UnionBBox(BoundingBoxXYZ a, BoundingBoxXYZ b) =>
            new BoundingBoxXYZ
            {
                Min = new XYZ(Math.Min(a.Min.X, b.Min.X), Math.Min(a.Min.Y, b.Min.Y), Math.Min(a.Min.Z, b.Min.Z)),
                Max = new XYZ(Math.Max(a.Max.X, b.Max.X), Math.Max(a.Max.Y, b.Max.Y), Math.Max(a.Max.Z, b.Max.Z)),
            };

        private static BoundingBoxXYZ ExpandBBox(BoundingBoxXYZ bb, double buf) =>
            new BoundingBoxXYZ
            {
                Min = new XYZ(bb.Min.X - buf, bb.Min.Y - buf, bb.Min.Z - buf),
                Max = new XYZ(bb.Max.X + buf, bb.Max.Y + buf, bb.Max.Z + buf),
            };

        private static void HideNonGridLevelAnnotations(View3D view, Document doc)
        {
            var keep = new HashSet<long>
            {
                (long)BuiltInCategory.OST_Grids,
                (long)BuiltInCategory.OST_Levels
            };
            foreach (Category cat in doc.Settings.Categories)
            {
                try
                {
                    if (!cat.get_AllowsVisibilityControl(view)) continue;
                    if (cat.CategoryType != CategoryType.Annotation) continue;
                    if (keep.Contains(cat.Id.Value)) continue;
                    view.SetCategoryHidden(cat.Id, true);
                }
                catch (Exception __lex) { LemoineLog.Swallowed($"LinkViews discipline: hide annotation category {cat.Id.Value} in view {view.Id.Value}", __lex); }
            }
        }

        private static void HideOtherLinks(View3D view, Document doc, List<ElementId> keepIds)
        {
            var keepSet = new HashSet<long>(keepIds.Select(id => id.Value));
            var toHide = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                .Where(li => !keepSet.Contains(li.Id.Value))
                .Select(li => li.Id).ToList();
            if (toHide.Count == 0) return;
            try { view.HideElements(new List<ElementId>(toHide)); } catch (Exception __lex) { LemoineLog.Swallowed($"LinkViews discipline: hide elements in view {view.Id.Value}", __lex); }
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
