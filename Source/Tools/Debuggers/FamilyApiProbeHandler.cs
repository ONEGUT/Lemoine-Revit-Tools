using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Debuggers
{
    /// <summary>
    /// TEMPORARY Phase-0 verification harness for the Family Modification Tools
    /// (plan-family-modification-tools.md). Answers, on a real Windows/Revit run, the three
    /// unknowns that cannot be resolved from Linux:
    ///
    ///   1. The exact <see cref="FamilyElementVisibility"/> member surface for THIS Revit year
    ///      (member names have shifted across versions — Tool A's rule body depends on them).
    ///   2. The real cost and shape of an <c>EditFamily</c> walk over a live family library,
    ///      including whether nested <c>EditFamily</c> recursion works at all, and how often
    ///      IS_VISIBLE_PARAM is parameter-associated (the "looks fixable but isn't" case).
    ///   3. Whether <see cref="ReferenceIntersector"/> with <c>FindReferencesInRevitLinks</c>
    ///      actually returns ceiling faces from the linked ARCH model (Tool C).
    ///
    /// Strictly READ-ONLY on the user's model. The only mutation is a temporary 3D view for
    /// probe 3, created and deleted inside this run; if deletion fails the run log names it so
    /// it can be removed by hand.
    ///
    /// Delete this file, FamilyApiProbeViewModel, FamilyApiProbeCommand, and the temporary
    /// ribbon button in App.cs once the results are captured. The repo ships with no
    /// Developer panel.
    ///
    /// Strings here are deliberately hardcoded rather than routed through AppStrings: this is
    /// developer diagnostics with a scheduled deletion date, not shipping user-facing text.
    /// </summary>
    public sealed class FamilyApiProbeHandler : IExternalEventHandler
    {
        // ── Inputs (set by the ViewModel before Raise) ────────────────────────────
        public bool ProbeVisibilityApi { get; set; } = true;
        public bool ProbeFamilyWalk    { get; set; } = true;
        public bool ProbeCuttable      { get; set; } = true;
        public bool ProbeLinkIntersect { get; set; } = true;

        /// <summary>BuiltInCategory ints of the families probe 2 walks.</summary>
        public List<int> FamilyCategories { get; set; } = new List<int>();
        /// <summary>BuiltInCategory ints of the instances probe 3 shoots down from.</summary>
        public List<int> SourceCategories { get; set; } = new List<int>();
        /// <summary>BuiltInCategory ints probe 3 treats as a ceiling/soffit hit.</summary>
        public List<int> TargetCategories { get; set; } = new List<int>();

        public int SampleSize { get; set; } = 25;

        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.Debuggers.FamilyApiProbeHandler";

        private StringBuilder _report = new StringBuilder();

        public void Execute(UIApplication app)
        {
            var pushLog    = PushLog;
            var onComplete = OnComplete;
            int pass = 0, fail = 0, skip = 0;

            try
            {
                _report = new StringBuilder();

                var doc = app.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    pushLog?.Invoke("No active document — open a project and re-run.", "fail");
                    onComplete?.Invoke(0, 1, 0);
                    return;
                }

                Head(app, doc);

                if (ProbeVisibilityApi)
                {
                    try   { ProbeVisibility(pushLog); pass++; }
                    catch (Exception ex) { fail++; Report(pushLog, "Probe 1 FAILED: " + ex.Message, "fail");
                                           DiagnosticsLog.Error("FamilyApiProbe: visibility api", ex); }
                }
                else skip++;

                if (ProbeCuttable)
                {
                    try   { ProbeCuttableCategories(doc, pushLog); pass++; }
                    catch (Exception ex) { fail++; Report(pushLog, "Probe 2 FAILED: " + ex.Message, "fail");
                                           DiagnosticsLog.Error("FamilyApiProbe: cuttable", ex); }
                }
                else skip++;

                if (ProbeFamilyWalk)
                {
                    try   { ProbeFamilies(doc, pushLog); pass++; }
                    catch (Exception ex) { fail++; Report(pushLog, "Probe 3 FAILED: " + ex.Message, "fail");
                                           DiagnosticsLog.Error("FamilyApiProbe: family walk", ex); }
                }
                else skip++;

                if (ProbeLinkIntersect)
                {
                    try   { ProbeIntersector(doc, pushLog); pass++; }
                    catch (Exception ex) { fail++; Report(pushLog, "Probe 4 FAILED: " + ex.Message, "fail");
                                           DiagnosticsLog.Error("FamilyApiProbe: intersector", ex); }
                }
                else skip++;

                WriteReport(pushLog);
                onComplete?.Invoke(pass, fail, skip);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("FamilyApiProbe: run", ex);
                pushLog?.Invoke("Probe run failed: " + ex.Message, "fail");
                onComplete?.Invoke(pass, fail + 1, skip);
            }
            finally
            {
                // Session-long static handler — drop the run payload so nothing is rooted.
                FamilyCategories = new List<int>();
                SourceCategories = new List<int>();
                TargetCategories = new List<int>();
                PushLog    = null;
                OnProgress = null;
                OnComplete = null;
                _report    = new StringBuilder();
            }
        }

        // ═════════════════════════════════════════════════════════════════════════
        // Probe 1 — FamilyElementVisibility member surface (reflection is the point here:
        // the whole question is WHICH members exist in this build).
        // ═════════════════════════════════════════════════════════════════════════
        private void ProbeVisibility(Action<string, string>? pushLog)
        {
            Section(pushLog, "PROBE 1 — FamilyElementVisibility surface");

            var t = typeof(FamilyElementVisibility);
            Emit($"Type: {t.AssemblyQualifiedName}");

            var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .OrderBy(p => p.Name).ToList();
            Report(pushLog, $"FamilyElementVisibility — {props.Count} public instance properties.", "info");
            foreach (var p in props)
                Emit($"  {p.Name,-32} {Short(p.PropertyType),-10} get={p.CanRead} set={p.CanWrite}");

            var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                           .Where(m => !m.IsSpecialName).OrderBy(m => m.Name).ToList();
            Emit($"Methods ({methods.Count}):");
            foreach (var m in methods)
                Emit($"  {m.Name}({string.Join(", ", m.GetParameters().Select(x => Short(x.ParameterType) + " " + x.Name))})");

            Emit("Constructors:");
            foreach (var c in t.GetConstructors())
                Emit($"  ({string.Join(", ", c.GetParameters().Select(x => Short(x.ParameterType) + " " + x.Name))})");

            Emit("FamilyElementVisibilityType values:");
            foreach (var name in Enum.GetNames(typeof(FamilyElementVisibilityType)))
                Emit($"  {name} = {(int)Enum.Parse(typeof(FamilyElementVisibilityType), name)}");

            // GenericForm's visibility-related surface — the other half of Tool A's rule body.
            var gf = typeof(GenericForm);
            Emit("GenericForm visibility-related members:");
            foreach (var m in gf.GetMembers(BindingFlags.Public | BindingFlags.Instance)
                                .Where(m => m.Name.IndexOf("Visib", StringComparison.OrdinalIgnoreCase) >= 0
                                         || m.Name.IndexOf("Solid", StringComparison.OrdinalIgnoreCase) >= 0
                                         || m.Name.IndexOf("Subcategory", StringComparison.OrdinalIgnoreCase) >= 0)
                                .Select(m => m.Name).Distinct().OrderBy(n => n))
                Emit($"  {m}");

            Report(pushLog, "Probe 1 done — full member list is in the report file.", "pass");
        }

        // ═════════════════════════════════════════════════════════════════════════
        // Probe 2 — Category.IsCuttable (read-only, hardcoded in Revit; bounds Tool A)
        // ═════════════════════════════════════════════════════════════════════════
        private void ProbeCuttableCategories(Document doc, Action<string, string>? pushLog)
        {
            Section(pushLog, "PROBE 2 — Category.IsCuttable");

            var rows = new List<string>();
            foreach (Category c in doc.Settings.Categories)
            {
                if (c == null) continue;
                try
                {
                    if (c.CategoryType != CategoryType.Model) continue;
                    rows.Add($"  {c.Name,-42} cuttable={c.IsCuttable,-5} id={c.Id.Value}");
                }
                catch (Exception ex) { DiagnosticsLog.Swallowed("FamilyApiProbe: read category " + (c.Name ?? "?"), ex); }
            }

            if (rows.Count == 0)
            {
                Report(pushLog, "No model categories found — unexpected; report this.", "fail");
                return;
            }

            rows.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows) Emit(r);
            Report(pushLog, $"Probe 2 done — {rows.Count} model categories listed in the report file.", "pass");
        }

        // ═════════════════════════════════════════════════════════════════════════
        // Probe 3 — EditFamily cost + library shape
        // ═════════════════════════════════════════════════════════════════════════
        private void ProbeFamilies(Document doc, Action<string, string>? pushLog)
        {
            Section(pushLog, "PROBE 3 — EditFamily walk");

            var wanted = new HashSet<long>(FamilyCategories.Select(i => (long)i));
            var all = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().ToList();

            var scoped = all.Where(f =>
            {
                try { return wanted.Count == 0 || (f.FamilyCategory != null && wanted.Contains(f.FamilyCategory.Id.Value)); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("FamilyApiProbe: family category", ex); return false; }
            }).ToList();

            int inPlace = 0, notEditable = 0;
            var editable = new List<Family>();
            foreach (var f in scoped)
            {
                try
                {
                    if (f.IsInPlace)  { inPlace++;     continue; }
                    if (!f.IsEditable) { notEditable++; continue; }
                    editable.Add(f);
                }
                catch (Exception ex) { DiagnosticsLog.Swallowed("FamilyApiProbe: gate family", ex); }
            }

            Report(pushLog, $"Found {all.Count} families in the document; {scoped.Count} in the selected categories.", "info");
            Report(pushLog, $"  {editable.Count} editable · {inPlace} in-place (skipped) · {notEditable} not editable (skipped).", "info");
            Emit($"Families total={all.Count} scoped={scoped.Count} editable={editable.Count} inPlace={inPlace} notEditable={notEditable}");

            if (editable.Count == 0)
            {
                Report(pushLog, "No editable families in the selected categories — nothing to time.", "warn");
                return;
            }

            var sample = editable.Take(Math.Max(1, SampleSize)).ToList();
            Report(pushLog, $"Timing EditFamily on {sample.Count} of {editable.Count} families…", "info");

            var times = new List<double>();
            int forms = 0, voids = 0, nested = 0, shared = 0, assoc = 0;
            bool triedDeep = false;
            int done = 0;

            var prog = new RunProgressReporter(
                pushLog ?? ((a, b) => { }), sample.Count, "families");

            foreach (var fam in sample)
            {
                if (RunState.CancelRequested)
                {
                    Report(pushLog, $"Stopped by user — {done} of {sample.Count} processed; results so far preserved.", "warn");
                    break;
                }

                Document? famDoc = null;
                var sw = Stopwatch.StartNew();
                try
                {
                    famDoc = doc.EditFamily(fam);
                    sw.Stop();
                    times.Add(sw.Elapsed.TotalSeconds);

                    var gfs = new FilteredElementCollector(famDoc).OfClass(typeof(GenericForm))
                                  .Cast<GenericForm>().ToList();
                    var fis = new FilteredElementCollector(famDoc).OfClass(typeof(FamilyInstance))
                                  .Cast<FamilyInstance>().ToList();

                    int famVoids = 0, famAssoc = 0;
                    foreach (var gf in gfs)
                    {
                        try { if (!gf.IsSolid) famVoids++; }
                        catch (Exception ex) { DiagnosticsLog.Swallowed("FamilyApiProbe: read IsSolid", ex); }

                        // The "looks fixable but isn't" case: writing IS_VISIBLE_PARAM is silently
                        // reverted on regeneration when it is driven by a family parameter.
                        try
                        {
                            var vp = gf.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
                            if (vp != null && famDoc.FamilyManager.GetAssociatedFamilyParameter(vp) != null) famAssoc++;
                        }
                        catch (Exception ex) { DiagnosticsLog.Swallowed("FamilyApiProbe: read visibility association", ex); }
                    }

                    int famShared = 0;
                    foreach (var fi in fis)
                    {
                        try
                        {
                            var nf = fi.Symbol?.Family;
                            if (nf == null) continue;
                            var sp = nf.get_Parameter(BuiltInParameter.FAMILY_SHARED);
                            if (sp != null && sp.AsInteger() == 1) famShared++;
                        }
                        catch (Exception ex) { DiagnosticsLog.Swallowed("FamilyApiProbe: read nested shared flag", ex); }
                    }

                    forms  += gfs.Count;  voids  += famVoids;
                    nested += fis.Count;  shared += famShared;  assoc += famAssoc;

                    Emit($"  {fam.Name,-46} {sw.Elapsed.TotalSeconds,6:0.00}s  forms={gfs.Count,-4} voids={famVoids,-3} " +
                         $"nested={fis.Count,-3} shared={famShared,-3} visAssoc={famAssoc}");

                    // Does nested EditFamily recursion work at all? Try once, on the first family
                    // that actually has a nested family — this decides whether the engine can recurse.
                    if (!triedDeep && fis.Count > 0)
                    {
                        triedDeep = true;
                        ProbeNestedEdit(famDoc, fis, pushLog);
                    }
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    Emit($"  {fam.Name,-46} EDITFAMILY THREW: {ex.Message}");
                    DiagnosticsLog.Swallowed("FamilyApiProbe: EditFamily " + fam.Name, ex);
                }
                finally
                {
                    try { famDoc?.Close(false); }
                    catch (Exception ex) { DiagnosticsLog.Swallowed("FamilyApiProbe: close family doc", ex); }
                }

                done++;
                prog.Tick();
                OnProgress?.Invoke(prog.Percent, done, 0, 0);
            }

            if (times.Count == 0)
            {
                Report(pushLog, "No family opened successfully — every EditFamily threw. See the report file.", "fail");
                return;
            }

            var sorted = times.OrderBy(x => x).ToList();
            double median = sorted[sorted.Count / 2];
            Report(pushLog, $"EditFamily timing over {times.Count}: min {sorted.First():0.00}s · " +
                            $"median {median:0.00}s · max {sorted.Last():0.00}s · total {times.Sum():0.0}s.", "pass");
            Report(pushLog, $"Contents: {forms} forms ({voids} voids) · {nested} nested instances ({shared} shared) · " +
                            $"{assoc} forms with a parameter-associated visibility.", "info");
            Emit($"TIMING min={sorted.First():0.000} median={median:0.000} max={sorted.Last():0.000} total={times.Sum():0.00} n={times.Count}");

            double projected = median * editable.Count;
            Report(pushLog, $"Projected full-library audit: {projected:0} s (~{projected / 60.0:0.0} min) for {editable.Count} families.",
                            projected > 600 ? "warn" : "info");
        }

        /// <summary>
        /// One-shot check that a family document can itself open a nested family — the
        /// precondition for the engine's recursion. Read-only; closes without saving.
        /// </summary>
        private void ProbeNestedEdit(Document famDoc, List<FamilyInstance> nestedInstances, Action<string, string>? pushLog)
        {
            Family? nestedFamily = null;
            foreach (var fi in nestedInstances)
            {
                try
                {
                    var nf = fi.Symbol?.Family;
                    if (nf == null) continue;
                    if (nf.IsInPlace || !nf.IsEditable) continue;
                    nestedFamily = nf;
                    break;
                }
                catch (Exception ex) { DiagnosticsLog.Swallowed("FamilyApiProbe: gate nested family", ex); }
            }

            if (nestedFamily == null)
            {
                Report(pushLog, "Nested recursion: no editable nested family in the first sample — not tested.", "warn");
                Emit("NESTED EDIT: no editable nested family available to test");
                return;
            }

            Document? deep = null;
            var sw = Stopwatch.StartNew();
            try
            {
                deep = famDoc.EditFamily(nestedFamily);
                sw.Stop();
                int deepForms = new FilteredElementCollector(deep).OfClass(typeof(GenericForm)).GetElementCount();
                Report(pushLog, $"Nested recursion WORKS — opened '{nestedFamily.Name}' from inside its host " +
                                $"in {sw.Elapsed.TotalSeconds:0.00}s ({deepForms} forms).", "pass");
                Emit($"NESTED EDIT ok family={nestedFamily.Name} {sw.Elapsed.TotalSeconds:0.000}s forms={deepForms}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                Report(pushLog, "Nested recursion FAILED — famDoc.EditFamily threw: " + ex.Message, "fail");
                Emit($"NESTED EDIT THREW family={nestedFamily.Name}: {ex}");
                DiagnosticsLog.Swallowed("FamilyApiProbe: nested EditFamily", ex);
            }
            finally
            {
                try { deep?.Close(false); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("FamilyApiProbe: close nested family doc", ex); }
            }
        }

        // ═════════════════════════════════════════════════════════════════════════
        // Probe 4 — ReferenceIntersector across links
        // ═════════════════════════════════════════════════════════════════════════
        private void ProbeIntersector(Document doc, Action<string, string>? pushLog)
        {
            Section(pushLog, "PROBE 4 — ReferenceIntersector into links");

            if (TargetCategories.Count == 0)
            {
                Report(pushLog, "No target categories selected — probe 4 skipped.", "warn");
                return;
            }

            var sources = CollectSources(doc, pushLog);
            if (sources.Count == 0) return;

            View3D? temp = null;
            try
            {
                temp = CreateTempView3D(doc, pushLog);
                if (temp == null) return;

                var targets = TargetCategories.Select(i => (BuiltInCategory)i).ToList();
                var filter  = new ElementMulticategoryFilter(targets);
                var ri = new ReferenceIntersector(filter, FindReferenceTarget.Face, temp)
                {
                    FindReferencesInRevitLinks = true,
                };

                int hits = 0, misses = 0, fromLink = 0;
                foreach (var fi in sources)
                {
                    if (RunState.CancelRequested)
                    {
                        Report(pushLog, $"Stopped by user — {hits + misses} of {sources.Count} probed.", "warn");
                        break;
                    }

                    XYZ? origin = null;
                    try { origin = (fi.Location as LocationPoint)?.Point; }
                    catch (Exception ex) { DiagnosticsLog.Swallowed("FamilyApiProbe: read location", ex); }
                    if (origin == null) { misses++; Emit($"  {fi.Id.Value} no LocationPoint"); continue; }

                    ReferenceWithContext? hit = null;
                    try { hit = ri.FindNearest(origin, XYZ.BasisZ.Negate()); }
                    catch (Exception ex) { DiagnosticsLog.Swallowed("FamilyApiProbe: FindNearest", ex); }

                    if (hit == null)
                    {
                        misses++;
                        Emit($"  {fi.Id.Value,-10} {Trim(fi.Name, 30),-30} NO HIT below");
                        continue;
                    }

                    hits++;
                    var r = hit.GetReference();
                    string where = "host", cat = "?";
                    try
                    {
                        if (r.LinkedElementId != ElementId.InvalidElementId)
                        {
                            fromLink++;
                            var li  = doc.GetElement(r.ElementId) as RevitLinkInstance;
                            var ldoc = li?.GetLinkDocument();
                            where = "LINK: " + (ldoc?.Title ?? li?.Name ?? "unknown");
                            cat   = ldoc?.GetElement(r.LinkedElementId)?.Category?.Name ?? "?";
                        }
                        else
                        {
                            cat = doc.GetElement(r.ElementId)?.Category?.Name ?? "?";
                        }
                    }
                    catch (Exception ex) { DiagnosticsLog.Swallowed("FamilyApiProbe: resolve hit", ex); }

                    Emit($"  {fi.Id.Value,-10} {Trim(fi.Name, 30),-30} drop={hit.Proximity,7:0.00}ft  {cat,-18} {where}");
                }

                Report(pushLog, $"Probed {hits + misses} instances: {hits} hit · {misses} no hit below.",
                                hits == 0 ? "fail" : "pass");
                Report(pushLog, $"  {fromLink} of {hits} hits came from a LINKED document " +
                                $"(FindReferencesInRevitLinks {(fromLink > 0 ? "confirmed working" : "returned nothing — investigate")}).",
                                fromLink > 0 ? "pass" : "warn");
            }
            finally
            {
                DeleteTempView(doc, temp, pushLog);
            }
        }

        private List<FamilyInstance> CollectSources(Document doc, Action<string, string>? pushLog)
        {
            var result = new List<FamilyInstance>();
            if (SourceCategories.Count == 0)
            {
                Report(pushLog, "No source categories selected — probe 4 skipped.", "warn");
                return result;
            }

            try
            {
                var srcFilter = new ElementMulticategoryFilter(SourceCategories.Select(i => (BuiltInCategory)i).ToList());
                result = new FilteredElementCollector(doc)
                    .WherePasses(srcFilter)
                    .WhereElementIsNotElementType()
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Take(Math.Max(1, SampleSize))
                    .ToList();
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("FamilyApiProbe: collect source instances", ex);
                Report(pushLog, "Could not collect source instances: " + ex.Message, "fail");
                return result;
            }

            if (result.Count == 0)
                Report(pushLog, "No instances found in the selected source categories — probe 4 has nothing to shoot from.", "warn");
            else
                Report(pushLog, $"Found {result.Count} source instance(s) to probe.", "info");

            return result;
        }

        /// <summary>
        /// A dedicated non-template 3D view. ReferenceIntersector needs one, and an existing
        /// view may hide the link, be a template, or have the workset closed — so build our own.
        /// </summary>
        private View3D? CreateTempView3D(Document doc, Action<string, string>? pushLog)
        {
            try
            {
                var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional);

                if (vft == null)
                {
                    Report(pushLog, "No 3D ViewFamilyType in this project — probe 4 cannot run.", "fail");
                    return null;
                }

                using (var t = new Transaction(doc, "Lemoine probe — temp 3D view"))
                {
                    t.Start();
                    var v = View3D.CreateIsometric(doc, vft.Id);
                    if (v == null)
                    {
                        t.RollBack();
                        Report(pushLog, "View3D.CreateIsometric returned null — probe 4 cannot run.", "fail");
                        return null;
                    }
                    v.Name = "LEMOINE_PROBE_" + DateTime.Now.ToString("HHmmss");
                    v.DetailLevel = ViewDetailLevel.Fine;
                    doc.Regenerate();
                    t.Commit();
                    Emit($"TEMP VIEW created: {v.Name} (id {v.Id.Value})");
                    return v;
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("FamilyApiProbe: create temp 3D view", ex);
                Report(pushLog, "Could not create the temporary 3D view: " + ex.Message, "fail");
                return null;
            }
        }

        private void DeleteTempView(Document doc, View3D? view, Action<string, string>? pushLog)
        {
            if (view == null) return;
            string name = "?";
            try { name = view.Name; }
            catch (Exception ex) { DiagnosticsLog.Swallowed("FamilyApiProbe: read temp view name", ex); }

            try
            {
                using (var t = new Transaction(doc, "Lemoine probe — remove temp 3D view"))
                {
                    t.Start();
                    doc.Delete(view.Id);
                    t.Commit();
                }
                Emit($"TEMP VIEW deleted: {name}");
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("FamilyApiProbe: delete temp 3D view", ex);
                Report(pushLog, $"Could not delete the temporary view '{name}' — delete it by hand.", "warn");
            }
        }

        // ── report plumbing ───────────────────────────────────────────────────────
        private void Head(UIApplication app, Document doc)
        {
            Emit("Lemoine Family API Probe — Phase 0");
            Emit("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            try   { Emit($"Revit: {app.Application.VersionName} / {app.Application.VersionNumber} / build {app.Application.VersionBuild}"); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("FamilyApiProbe: read Revit version", ex); }
            try   { Emit("Document: " + doc.Title); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("FamilyApiProbe: read doc title", ex); }
            Emit(new string('=', 78));
        }

        private void Section(Action<string, string>? pushLog, string title)
        {
            Emit("");
            Emit(new string('─', 78));
            Emit(title);
            Emit(new string('─', 78));
            pushLog?.Invoke(title, "info");
        }

        private void Emit(string line) => _report.AppendLine(line);

        private void Report(Action<string, string>? pushLog, string line, string status)
        {
            pushLog?.Invoke(line, status);
            _report.AppendLine((status == "info" ? "" : "[" + status + "] ") + line);
        }

        private void WriteReport(Action<string, string>? pushLog)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools", "Reports");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "family-api-probe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                System.IO.File.WriteAllText(path, _report.ToString());
                pushLog?.Invoke("Report written: " + path, "pass");
                pushLog?.Invoke("Attach that file to the plan thread — it carries the full member lists.", "info");
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("FamilyApiProbe: write report", ex);
                pushLog?.Invoke("Could not write the report file: " + ex.Message + " — the log above still has the summary.", "fail");
            }
        }

        private static string Short(Type t) => t.Name;

        private static string Trim(string? s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s!.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }
    }
}
