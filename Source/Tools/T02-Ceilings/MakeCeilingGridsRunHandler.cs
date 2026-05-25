using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace LemoineTools.Tools.Ceilings
{
    public sealed class MakeCeilingGridsRunHandler : IExternalEventHandler
    {
        // ── Inputs ────────────────────────────────────────────────────────────
        public bool                   IncludeHost   { get; set; } = true;
        public List<ElementId>        LinkInstIds   { get; set; } = new List<ElementId>();
        public List<CeilingTypeEntry> IncludedTypes { get; set; } = new List<CeilingTypeEntry>();
        public string                 NamingPattern { get; set; } = "{Level}_CeilingGrid";
        public string                 OutputFolder  { get; set; } = "";
        public bool                   SplitByLevel  { get; set; } = false;

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.Ceilings.MakeCeilingGridsRunHandler";

        public void Execute(UIApplication app)
        {
            var doc  = app.ActiveUIDocument.Document;
            int pass = 0, fail = 0, skip = 0;

            try
            {
                var projInfo   = doc.ProjectInformation;
                string projNum = projInfo?.get_Parameter(BuiltInParameter.PROJECT_NUMBER)?.AsString() ?? "";
                string projName= projInfo?.get_Parameter(BuiltInParameter.PROJECT_NAME)?.AsString()   ?? doc.Title;

                var levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .ToList();

                Log($"Found {levels.Count} level(s).", "info");

                // Build set of included host type IDs
                var allHostTypeIds = new FilteredElementCollector(doc)
                    .OfClass(typeof(CeilingType))
                    .Cast<CeilingType>()
                    .Select(t => t.Id.Value)
                    .ToHashSet();

                var includedHostIds = new HashSet<long>(
                    IncludedTypes.Where(t => !t.IsLinked).Select(t => t.TypeIdValue));

                var excludedTypeIds = allHostTypeIds.Except(includedHostIds).ToHashSet();

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
                        string viewName = Sanitize(ResolveTokens(NamingPattern, level.Name, projNum, projName));

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
                                catch { /* name conflict — keep Revit's generated name */ }
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
                            if (excludedTypeIds.Count > 0)
                                HideExcludedCeilingTypes(doc, view, excludedTypeIds);
                            tx.Commit();
                        }

                        // ── DWG export (no transaction needed) ────────────────────
                        if (!string.IsNullOrWhiteSpace(OutputFolder))
                        {
                            string outDir = SplitByLevel
                                ? EnsureDir(Path.Combine(OutputFolder, Sanitize(level.Name)))
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
                Log($"Fatal: {ex.Message}", "fail");
                fail++;
            }

            Progress(100, pass, fail, skip);
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
            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat.CategoryType != CategoryType.Model) continue;
                if (!cat.AllowsVisibilityControl(view)) continue;
                bool keep = cat.Id.Value == (int)BuiltInCategory.OST_Ceilings;
                try { view.SetCategoryHidden(cat.Id, !keep); } catch { }
            }
        }

        private static void HideExcludedCeilingTypes(Document doc, View view, HashSet<long> excludedTypeIds)
        {
            var toHide = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(Ceiling))
                .Cast<Ceiling>()
                .Where(c => excludedTypeIds.Contains(c.GetTypeId().Value))
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

        // ── Token resolution ───────────────────────────────────────────────────
        private static string ResolveTokens(string pattern, string level, string projNum, string projName)
        {
            var now = DateTime.Now;
            return pattern
                .Replace("{Level}",         level)
                .Replace("{ProjectNumber}", projNum)
                .Replace("{ProjectName}",   projName)
                .Replace("{Year}",          now.Year.ToString())
                .Replace("{Month}",         now.Month.ToString("D2"))
                .Replace("{Day}",           now.Day.ToString("D2"));
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
