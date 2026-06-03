using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Ceilings
{
    public sealed class MakeCeilingGridsRunHandler : IExternalEventHandler
    {
        // ── Inputs ────────────────────────────────────────────────────────────
        public bool                   IncludeHost   { get; set; } = true;
        public List<ElementId>        LinkInstIds   { get; set; } = new List<ElementId>();

        /// <summary>
        /// Family/Type name pairs the user unchecked. Ceilings whose Family Name AND
        /// Type Name match a pair are hidden in every RCP view — host and linked alike —
        /// via a name-based <see cref="ParameterFilterElement"/>.
        /// </summary>
        public List<(string Family, string Type)> ExcludedTypeNames { get; set; } = new List<(string, string)>();

        public string                 OutputFolder             { get; set; } = "";
        public bool                   UseCeilingGridsSubfolder { get; set; } = false;

        private const string HideFilterName = "Lemoine — Hidden Ceiling Types";

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.Ceilings.MakeCeilingGridsRunHandler";

        public void Execute(UIApplication app)
        {
            var doc  = app.ActiveUIDocument.Document;
            long __issues0 = LemoineLog.IssueCount;
            int pass = 0, fail = 0, skip = 0;

            try
            {
                var levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .ToList();

                Log($"Found {levels.Count} level(s).", "info");

                // Name-keyed exclusion set: "Family|Type". Used for the host-side
                // per-instance fallback hide (the name filter covers links).
                var excludedNameKeys = new HashSet<string>(
                    ExcludedTypeNames.Select(p => $"{p.Family}|{p.Type}"), StringComparer.Ordinal);

                // Build (or recreate) the single name-based hide filter once. Adding it to
                // each view and turning its visibility off hides matching ceilings in the
                // host AND in links shown "By Host View" (the default for new RCP views).
                ElementId hideFilterId = ElementId.InvalidElementId;
                if (ExcludedTypeNames.Count > 0)
                    hideFilterId = EnsureHideFilter(doc);

                // Find a CeilingPlan ViewFamilyType once
                var vft = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(t => t.ViewFamily == ViewFamily.CeilingPlan);

                if (vft == null)
                {
                    Log("No Ceiling Plan view family type found in this project.", "fail");
                    fail++;
                    Progress(100, pass, fail, skip);
                    Complete(pass, fail, skip);
                    return;
                }

                int total = levels.Count;
                int done  = 0;

                foreach (var level in levels)
                {
                    try
                    {
                        string viewName = Sanitize(level.Name) + "_CeilingGrid";

                        // ── T1: find or create the RCP view ──────────────────────
                        ViewPlan? view = null;
                        using (var tx = new Transaction(doc, $"Create RCP: {viewName}"))
                        {
                            ConfigureFailures(tx);
                            tx.Start();

                            view = FindRcp(doc, viewName);
                            if (view == null)
                            {
                                view = ViewPlan.Create(doc, vft.Id, level.Id);
                                try   { view.Name = viewName; }
                                catch (Exception __lex) { LemoineLog.Swallowed($"MakeCeilingGrids: name conflict for RCP '{viewName}' — keeping Revit's generated name", __lex); }
                            }

                            tx.Commit();
                        }

                        if (view == null) { Log($"Could not create RCP for {level.Name}.", "fail"); fail++; goto Next; }

                        // ── T2: set ceiling-only visibility ────────────────────────
                        using (var tx = new Transaction(doc, $"Ceiling Visibility: {viewName}"))
                        {
                            ConfigureFailures(tx);
                            tx.Start();
                            SetCeilingOnlyVisibility(doc, view);
                            if (excludedNameKeys.Count > 0)
                            {
                                if (hideFilterId != ElementId.InvalidElementId)
                                    ApplyHideFilter(view, hideFilterId);
                                // Host-only safety net by name — covers host ceilings even
                                // if the view filter could not be created.
                                HideExcludedHostCeilings(doc, view, excludedNameKeys);
                            }
                            tx.Commit();
                        }

                        // ── DWG export (no transaction needed) ────────────────────
                        if (!string.IsNullOrWhiteSpace(OutputFolder))
                        {
                            string outDir = UseCeilingGridsSubfolder
                                ? EnsureDir(Path.Combine(OutputFolder, "Ceiling Grids"))
                                : OutputFolder;
                            EnsureDir(outDir);
                            ExportDwg(doc, view, viewName, outDir);
                            Log($"Exported: {viewName}.dwg", "pass");
                        }
                        else
                        {
                            Log($"Created: {viewName}", "pass");
                        }

                        pass++;
                    }
                    catch (Exception ex)
                    {
                        Log($"Level {level.Name} failed: {ex.Message}", "fail");
                        fail++;
                    }

                    Next:
                    done++;
                    Progress((int)(done * 90.0 / total), pass, fail, skip);
                }
            }
            catch (Exception ex)
            {
                LemoineLog.Error("MakeCeilingGrids: run aborted", ex);
                Log($"Error: {ex.Message}", "fail");
                fail++;
            }

            Progress(100, pass, fail, skip);
            long __issues = LemoineLog.IssuesSince(__issues0);
            if (__issues > 0) Log($"{__issues} non-fatal issue(s) recorded — see diagnostics log.", "warn");
            Complete(pass, fail, skip);
        }

        // ── View helpers ───────────────────────────────────────────────────────
        private static ViewPlan? FindRcp(Document doc, string name)
            => new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .FirstOrDefault(v =>
                    v.Name == name && !v.IsTemplate &&
                    (doc.GetElement(v.GetTypeId()) as ViewFamilyType)?.ViewFamily == ViewFamily.CeilingPlan);

        private static void SetCeilingOnlyVisibility(Document doc, View view)
        {
            view.AreAnnotationCategoriesHidden      = true;
            view.AreAnalyticalModelCategoriesHidden = true;
            view.AreImportCategoriesHidden          = true;

            // Model categories: hide everything except Ceilings
            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat.CategoryType != CategoryType.Model) continue;
                if (!cat.get_AllowsVisibilityControl(view)) continue;
                bool keep = cat.Id.Value == (int)BuiltInCategory.OST_Ceilings;
                try { view.SetCategoryHidden(cat.Id, !keep); } catch (Exception __lex) { LemoineLog.Swallowed($"MakeCeilingGrids: set category {cat.Id.Value} visibility in view {view.Id.Value}", __lex); }
            }
        }

        // ── Name-based hide filter ──────────────────────────────────────────────
        //
        // Creates a single ParameterFilterElement on OST_Ceilings that matches every
        // excluded (Family Name, Type Name) pair, OR'd together. Mirrors the proven
        // parameter mapping in AutoFiltersEventHandler: ELEM_FAMILY_PARAM for Family
        // Name and ALL_MODEL_TYPE_NAME for Type Name. Any existing filter of the same
        // name is deleted first so the definition reflects the current selection.
        //
        // Returns InvalidElementId (with a logged failure) if Revit refuses to create
        // the filter even after falling back to Type-Name-only matching.
        private ElementId EnsureHideFilter(Document doc)
        {
            ElementId result = ElementId.InvalidElementId;

            using (var tx = new Transaction(doc, "Ceiling Hide Filter"))
            {
                ConfigureFailures(tx);
                tx.Start();

                var existing = new FilteredElementCollector(doc)
                    .OfClass(typeof(ParameterFilterElement))
                    .Cast<ParameterFilterElement>()
                    .FirstOrDefault(f => f.Name == HideFilterName);
                if (existing != null)
                {
                    try { doc.Delete(existing.Id); }
                    catch (Exception __lex) { LemoineLog.Swallowed("MakeCeilingGrids: delete stale hide filter", __lex); }
                }

                var catIds = new List<ElementId> { new ElementId((long)(int)BuiltInCategory.OST_Ceilings) };

                // Attempt 1: match Family Name AND Type Name. Attempt 2 (fallback): Type Name only.
                try
                {
                    var pfe = ParameterFilterElement.Create(doc, HideFilterName, catIds, BuildExcludedFilter(false));
                    result = pfe.Id;
                }
                catch (Exception exBoth)
                {
                    LemoineLog.Swallowed("MakeCeilingGrids: family+type hide filter failed — retrying type-name only", exBoth);
                    try
                    {
                        var pfe = ParameterFilterElement.Create(doc, HideFilterName, catIds, BuildExcludedFilter(true));
                        result = pfe.Id;
                    }
                    catch (Exception exType)
                    {
                        LemoineLog.Error("MakeCeilingGrids: could not create ceiling hide filter", exType);
                        Log($"Could not create ceiling hide filter: {exType.Message}. Host ceilings are still hidden; linked ceilings may remain visible.", "fail");
                    }
                }

                tx.Commit();
            }

            return result;
        }

        // Builds the OR-of-(Family AND Type) element filter for the excluded pairs.
        // When typeNameOnly is true, each clause matches Type Name alone.
        private ElementFilter BuildExcludedFilter(bool typeNameOnly)
        {
            var familyParam = new ElementId((long)(int)BuiltInParameter.ELEM_FAMILY_PARAM);
            var typeParam   = new ElementId((long)(int)BuiltInParameter.ALL_MODEL_TYPE_NAME);

            var clauses = new List<ElementFilter>();
            foreach (var (family, type) in ExcludedTypeNames)
            {
                var typeRule = new ElementParameterFilter(
                    ParameterFilterRuleFactory.CreateEqualsRule(typeParam, type));

                if (typeNameOnly)
                {
                    clauses.Add(typeRule);
                }
                else
                {
                    var famRule = new ElementParameterFilter(
                        ParameterFilterRuleFactory.CreateEqualsRule(familyParam, family));
                    clauses.Add(new LogicalAndFilter(famRule, typeRule));
                }
            }

            return clauses.Count == 1 ? clauses[0] : new LogicalOrFilter(clauses);
        }

        private static void ApplyHideFilter(View view, ElementId filterId)
        {
            if (!view.AreGraphicsOverridesAllowed())
            {
                LemoineLog.Warn("MakeCeilingGrids",
                    $"View '{view.Name}' does not allow graphic overrides — linked ceilings of excluded types may remain visible (host ceilings are still hidden).");
                return;
            }
            if (!view.GetFilters().Any(id => id.Value == filterId.Value))
                view.AddFilter(filterId);
            view.SetFilterVisibility(filterId, false);
        }

        // Host-only fallback: hide host ceiling instances whose Family|Type name is
        // excluded. The name filter above is the primary mechanism (and the only one
        // that reaches linked ceilings); this guarantees host hiding even if the filter
        // could not be created.
        private static void HideExcludedHostCeilings(Document doc, View view, HashSet<string> excludedNameKeys)
        {
            var toHide = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(Ceiling))
                .Cast<Ceiling>()
                .Where(c =>
                {
                    var ct = doc.GetElement(c.GetTypeId()) as CeilingType;
                    return ct != null &&
                           excludedNameKeys.Contains($"{ct.FamilyName}|{ct.Name}");
                })
                .Select(c => c.Id)
                .ToList();

            if (toHide.Count > 0)
                view.HideElements(toHide);
        }

        // ── DWG export ─────────────────────────────────────────────────────────
        private static void ExportDwg(Document doc, View view, string baseName, string outDir)
        {
            var opts = new DWGExportOptions
            {
                FileVersion = ACADVersion.R2018,
                MergedViews = true,
            };
            doc.Export(outDir, baseName, new List<ElementId> { view.Id }, opts);
        }

        private static string Sanitize(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Join("_", name.Split(invalid)).Trim();
        }

        private static string EnsureDir(string path)
        {
            Directory.CreateDirectory(path);
            return path;
        }

        private static void ConfigureFailures(Transaction tx)
        {
            var opts = tx.GetFailureHandlingOptions();
            opts.SetClearAfterRollback(true);
            opts.SetDelayedMiniWarnings(true);
            tx.SetFailureHandlingOptions(opts);
        }

        private void Log(string text, string status) => PushLog?.Invoke(text, status);
        private void Progress(int pct, int p, int f, int s) => OnProgress?.Invoke(pct, p, f, s);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
