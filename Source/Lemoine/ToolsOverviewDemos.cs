using System;
using System.Collections.Generic;
using System.Linq;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Lemoine
{
    // ─────────────────────────────────────────────────────────────────────────
    // Per-tool demo specs for the Tools Overview "Dummy run". Each spec mirrors the
    // real tool's step flow (titles + required flags taken from the actual
    // ViewModels) and seeds representative sample data so the StepFlowWindow can be
    // navigated and "run" without a Revit document. Keyed by the catalog tool name.
    // ─────────────────────────────────────────────────────────────────────────
    public static class ToolsOverviewDemos
    {
        // ── Shared sample pools ───────────────────────────────────────────────
        // Each pool prefers the live document snapshot captured when the overview
        // opened (OverviewSamples) and falls back to the canned JSON strings when
        // no document was open or that pool came back empty.
        private static List<string> L(params string[] s) => s.ToList();

        private static List<string> Canned(string prefix, int count)
        {
            var list = new List<string>(count);
            for (int i = 1; i <= count; i++) list.Add(LemoineStrings.T($"{prefix}.{i}"));
            return list;
        }

        private static List<string> Pool(Func<OverviewSampleSnapshot, List<string>> pick, string cannedPrefix, int cannedCount)
        {
            var snap = OverviewSamples.Current;
            var live = snap == null ? null : pick(snap);
            return live != null && live.Count > 0 ? live : Canned(cannedPrefix, cannedCount);
        }

        private static List<string> PlanViews    => Pool(s => s.PlanViews,    "overviewDemos.pools.planViews", 4);
        private static List<string> CeilingPlans => Pool(s => s.CeilingPlans, "overviewDemos.pools.ceilingPlans", 3);
        private static List<string> Views3D      => Pool(s => s.Views3D,      "overviewDemos.pools.views3D", 3);
        private static List<string> Sections     => Pool(s => s.Sections,     "overviewDemos.pools.sections", 3);
        private static List<string> Sheets       => Pool(s => s.Sheets,       "overviewDemos.pools.sheets", 3);
        private static List<string> Levels       => Pool(s => s.Levels,       "overviewDemos.pools.levels", 5);
        private static List<string> Documents    => Pool(s => s.Documents,    "overviewDemos.pools.documents", 3);
        private static List<string> Links        => Pool(s => s.Links,        "overviewDemos.pools.documents", 3);
        private static List<string> Trades       => Pool(s => s.Trades,       "overviewDemos.pools.trades", 6);
        private static List<string> Filters      => Pool(s => s.Filters,      "overviewDemos.pools.filters", 5);
        private static List<string> Definitions  => Pool(s => s.Definitions,  "overviewDemos.pools.definitions", 3);
        private static List<string> Templates    => Pool(s => s.Templates,    "overviewDemos.pools.templates", 3);
        private static List<string> Grids        => Pool(s => s.Grids,        "overviewDemos.pools.grids", 6);
        private static List<string> RefPlanes    => Pool(s => s.RefPlanes,    "overviewDemos.pools.refPlanes", 3);
        private static List<string> TitleBlocks  => Pool(s => s.TitleBlocks,  "overviewDemos.pools.titleBlocks", 3);

        private static Dictionary<string, List<string>> Categories()
        {
            var snap = OverviewSamples.Current;
            if (snap != null && snap.CategoryGroups.Count > 0)
                return snap.CategoryGroups.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value));

            return new Dictionary<string, List<string>>
            {
                [LemoineStrings.T("overviewDemos.categoryGroups.mechanical.label")] = Canned("overviewDemos.categoryGroups.mechanical.items", 3),
                [LemoineStrings.T("overviewDemos.categoryGroups.piping.label")]     = Canned("overviewDemos.categoryGroups.piping.items", 3),
                [LemoineStrings.T("overviewDemos.categoryGroups.electrical.label")] = Canned("overviewDemos.categoryGroups.electrical.items", 2),
                [LemoineStrings.T("overviewDemos.categoryGroups.structure.label")]  = Canned("overviewDemos.categoryGroups.structure.items", 3),
            };
        }

        private static Dictionary<string, List<string>> Group(string key, List<string> items) =>
            new Dictionary<string, List<string>> { [key] = items };

        private static Dictionary<string, List<string>> Groups(params (string Key, List<string> Items)[] g)
        {
            var d = new Dictionary<string, List<string>>();
            foreach (var (k, v) in g) d[k] = v;
            return d;
        }

        // ── Step builders ─────────────────────────────────────────────────────
        private static OverviewDemoStep Multi(string id, string title, bool req, string hint, Dictionary<string, List<string>> groups)
            => new OverviewDemoStep { Id = id, Title = title, Required = req, Kind = "multi", Hint = hint, Groups = groups };
        private static OverviewDemoStep MultiFlat(string id, string title, bool req, string hint, List<string> items)
            => Multi(id, title, req, hint, Group("Items", items));
        private static ToggleItem Tg(string id, string label, string desc, bool on)
            => new ToggleItem { Id = id, Label = label, Desc = desc, DefaultOn = on };
        private static OverviewDemoStep Toggles(string id, string title, string hint, params ToggleItem[] items)
            => new OverviewDemoStep { Id = id, Title = title, Kind = "toggles", Hint = hint, Toggles = items.ToList() };
        private static OverviewDemoStep Single(string id, string title, bool req, string hint, params string[] items)
            => new OverviewDemoStep { Id = id, Title = title, Required = req, Kind = "single", Hint = hint, Items = items.ToList() };
        private static OverviewDemoStep FileStep(string id, string title, bool req, string hint, string filter, string ph)
            => new OverviewDemoStep { Id = id, Title = title, Required = req, Kind = "file", Hint = hint, FileFilter = filter, FilePlaceholder = ph };
        private static OverviewDemoStep Num(string id, string title, string hint, double val, double min, double max, double step, string unit, int decimals = 0)
            => new OverviewDemoStep { Id = id, Title = title, Kind = "number", Hint = hint, NumValue = val, NumMin = min, NumMax = max, NumStep = step, NumUnit = unit, NumDecimals = decimals };
        private static OverviewDemoStep Pre(OverviewDemoStep s, int index) { s.PreselectIndex = index; return s; }
        private static OverviewDemoStep Composite(string id, string title, bool req, string hint, params OverviewDemoStep[] parts)
            => new OverviewDemoStep { Id = id, Title = title, Required = req, Kind = "composite", Hint = hint, Parts = parts.ToList() };
        private static OverviewDemoStep Txt(string id, string title, bool req, string hint, string def, string ph)
            => new OverviewDemoStep { Id = id, Title = title, Required = req, Kind = "text", Hint = hint, TextDefault = def, TextPlaceholder = ph };
        private static OverviewDemoStep Info(string id, string title, string info)
            => new OverviewDemoStep { Id = id, Title = title, Kind = "info", Info = info };

        private static (string, string)[] RL(params (string, string)[] lines) => lines;

        // ── Lookup ────────────────────────────────────────────────────────────
        // Specs are rebuilt whenever the document snapshot changes (each overview
        // open recaptures), so the sample pools never go stale across documents.
        private static Dictionary<string, OverviewDemoSpec>? _specs;
        private static OverviewSampleSnapshot?               _builtFrom;

        public static OverviewDemoSpec? For(string toolName)
        {
            var snap = OverviewSamples.Current;
            if (_specs == null || !ReferenceEquals(_builtFrom, snap))
            {
                _specs     = BuildSpecs();
                _builtFrom = snap;
            }
            return _specs.TryGetValue(toolName, out var s) ? s : null;
        }

        /// <summary>Drop the cached specs when the overview window closes so the
        /// captured document strings don't stay rooted on this static.</summary>
        public static void DropCache() { _specs = null; _builtFrom = null; }

        private static Dictionary<string, OverviewDemoSpec> BuildSpecs()
        {
            var specs = new Dictionary<string, OverviewDemoSpec>
        {
            ["Auto Filters"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.autoFilters.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.autoFilters.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", LemoineStrings.T("overviewDemos.tools.autoFilters.steps.s1.title"), true, LemoineStrings.T("overviewDemos.tools.autoFilters.steps.s1.hint"), Documents),
                    Toggles("S2", LemoineStrings.T("overviewDemos.tools.autoFilters.steps.s2.title"), LemoineStrings.T("overviewDemos.tools.autoFilters.steps.s2.hint"),
                        Tg("sys", LemoineStrings.T("overviewDemos.tools.autoFilters.steps.s2.toggles.sys.label"), LemoineStrings.T("overviewDemos.tools.autoFilters.steps.s2.toggles.sys.desc"), true),
                        Tg("fab", LemoineStrings.T("overviewDemos.tools.autoFilters.steps.s2.toggles.fab.label"), LemoineStrings.T("overviewDemos.tools.autoFilters.steps.s2.toggles.fab.desc"), false),
                        Tg("host", LemoineStrings.T("overviewDemos.tools.autoFilters.steps.s2.toggles.host.label"), LemoineStrings.T("overviewDemos.tools.autoFilters.steps.s2.toggles.host.desc"), false)),
                    Info("S3", LemoineStrings.T("overviewDemos.tools.autoFilters.steps.s3.title"), LemoineStrings.T("overviewDemos.tools.autoFilters.steps.s3.info")),
                    MultiFlat("S4", LemoineStrings.T("overviewDemos.tools.autoFilters.steps.s4.title"), true, LemoineStrings.T("overviewDemos.tools.autoFilters.steps.s4.hint"), Trades),
                    Info("S5", LemoineStrings.T("overviewDemos.tools.autoFilters.steps.s5.title"), LemoineStrings.T("overviewDemos.tools.autoFilters.steps.s5.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.autoFilters.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.autoFilters.runLog.2"), "pass"), (LemoineStrings.T("overviewDemos.tools.autoFilters.runLog.3"), "pass"), (LemoineStrings.T("overviewDemos.tools.autoFilters.runLog.4"), "pass")),
            },

            ["Legend Creation"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.legendCreation.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.legendCreation.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", LemoineStrings.T("overviewDemos.tools.legendCreation.steps.s1.title"), true, LemoineStrings.T("overviewDemos.tools.legendCreation.steps.s1.hint"), Filters),
                    Single("S2", LemoineStrings.T("overviewDemos.tools.legendCreation.steps.s2.title"), true, LemoineStrings.T("overviewDemos.tools.legendCreation.steps.s2.hint"), LemoineStrings.T("overviewDemos.tools.legendCreation.steps.s2.items.1"), LemoineStrings.T("overviewDemos.tools.legendCreation.steps.s2.items.2"), LemoineStrings.T("overviewDemos.tools.legendCreation.steps.s2.items.3")),
                    Toggles("S3", LemoineStrings.T("overviewDemos.tools.legendCreation.steps.s3.title"), LemoineStrings.T("overviewDemos.tools.legendCreation.steps.s3.hint"),
                        Tg("swatch", LemoineStrings.T("overviewDemos.tools.legendCreation.steps.s3.toggles.swatch.label"), LemoineStrings.T("overviewDemos.tools.legendCreation.steps.s3.toggles.swatch.desc"), true),
                        Tg("count", LemoineStrings.T("overviewDemos.tools.legendCreation.steps.s3.toggles.count.label"), LemoineStrings.T("overviewDemos.tools.legendCreation.steps.s3.toggles.count.desc"), false),
                        Tg("desc", LemoineStrings.T("overviewDemos.tools.legendCreation.steps.s3.toggles.desc.label"), LemoineStrings.T("overviewDemos.tools.legendCreation.steps.s3.toggles.desc.desc"), true)),
                    Info("S4", LemoineStrings.T("overviewDemos.tools.legendCreation.steps.s4.title"), LemoineStrings.T("overviewDemos.tools.legendCreation.steps.s4.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.legendCreation.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.legendCreation.runLog.2"), "pass"), (LemoineStrings.T("overviewDemos.tools.legendCreation.runLog.3"), "pass")),
            },

            ["Ceiling Heatmap"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.ceilingHeatmap.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.ceilingHeatmap.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", LemoineStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s1.title"), true, LemoineStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s1.hint"), CeilingPlans),
                    Single("S_RAMP", LemoineStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s_ramp.title"), true, LemoineStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s_ramp.hint"), LemoineStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s_ramp.items.1"), LemoineStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s_ramp.items.2"), LemoineStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s_ramp.items.3")),
                    Toggles("S2", LemoineStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s2.title"), LemoineStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s2.hint"),
                        Tg("hidelinked", LemoineStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s2.toggles.hidelinked.label"), LemoineStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s2.toggles.hidelinked.desc"), true),
                        Tg("legend", LemoineStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s2.toggles.legend.label"), LemoineStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s2.toggles.legend.desc"), true),
                        Tg("halftone", LemoineStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s2.toggles.halftone.label"), LemoineStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s2.toggles.halftone.desc"), false)),
                    Info("S3", LemoineStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s3.title"), LemoineStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s3.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.ceilingHeatmap.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.ceilingHeatmap.runLog.2"), "pass"), (LemoineStrings.T("overviewDemos.tools.ceilingHeatmap.runLog.3"), "pass"), (LemoineStrings.T("overviewDemos.tools.ceilingHeatmap.runLog.4"), "pass")),
            },

            ["Make Ceiling Grids"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.makeCeilingGrids.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.makeCeilingGrids.runLabel"),
                Steps = new[]
                {
                    MultiFlat("docs", LemoineStrings.T("overviewDemos.tools.makeCeilingGrids.steps.docs.title"), true, LemoineStrings.T("overviewDemos.tools.makeCeilingGrids.steps.docs.hint"), Documents),
                    MultiFlat("filter", LemoineStrings.T("overviewDemos.tools.makeCeilingGrids.steps.filter.title"), false, LemoineStrings.T("overviewDemos.tools.makeCeilingGrids.steps.filter.hint"), L(LemoineStrings.T("overviewDemos.tools.makeCeilingGrids.steps.filter.items.1"), LemoineStrings.T("overviewDemos.tools.makeCeilingGrids.steps.filter.items.2"), LemoineStrings.T("overviewDemos.tools.makeCeilingGrids.steps.filter.items.3"))),
                    FileStep("export", LemoineStrings.T("overviewDemos.tools.makeCeilingGrids.steps.export.title"), true, LemoineStrings.T("overviewDemos.tools.makeCeilingGrids.steps.export.hint"), LemoineStrings.T("overviewDemos.tools.makeCeilingGrids.steps.export.filter"), LemoineStrings.T("overviewDemos.tools.makeCeilingGrids.steps.export.placeholder")),
                    Info("run", LemoineStrings.T("overviewDemos.tools.makeCeilingGrids.steps.run.title"), LemoineStrings.T("overviewDemos.tools.makeCeilingGrids.steps.run.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.makeCeilingGrids.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.makeCeilingGrids.runLog.2"), "pass"), (LemoineStrings.T("overviewDemos.tools.makeCeilingGrids.runLog.3"), "pass")),
            },

            ["Project Grids"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.projectGrids.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.projectGrids.runLabel"),
                Steps = new[]
                {
                    FileStep("S1", LemoineStrings.T("overviewDemos.tools.projectGrids.steps.s1.title"), true, LemoineStrings.T("overviewDemos.tools.projectGrids.steps.s1.hint"), LemoineStrings.T("overviewDemos.tools.projectGrids.steps.s1.filter"), LemoineStrings.T("overviewDemos.tools.projectGrids.steps.s1.placeholder")),
                    Info("S2", LemoineStrings.T("overviewDemos.tools.projectGrids.steps.s2.title"), LemoineStrings.T("overviewDemos.tools.projectGrids.steps.s2.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.projectGrids.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.projectGrids.runLog.2"), "pass")),
            },

            ["Reproject Grids"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.reprojectGrids.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.reprojectGrids.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", LemoineStrings.T("overviewDemos.tools.reprojectGrids.steps.s1.title"), true, LemoineStrings.T("overviewDemos.tools.reprojectGrids.steps.s1.hint"), CeilingPlans),
                    Info("S2", LemoineStrings.T("overviewDemos.tools.reprojectGrids.steps.s2.title"), LemoineStrings.T("overviewDemos.tools.reprojectGrids.steps.s2.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.reprojectGrids.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.reprojectGrids.runLog.2"), "pass")),
            },

            ["Bulk Views by Level"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.bulkViewsByLevel.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.bulkViewsByLevel.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", LemoineStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s1.title"), true, LemoineStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s1.hint"), Documents),
                    Multi("S2", LemoineStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s2.title"), true, LemoineStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s2.hint"), Groups((LemoineStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s2.groups.levels.label"), Levels), (LemoineStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s2.groups.viewTypes.label"), L(LemoineStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s2.groups.viewTypes.items.1"), LemoineStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s2.groups.viewTypes.items.2"), LemoineStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s2.groups.viewTypes.items.3"))))),
                    Txt("S3", LemoineStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s3.title"), false, LemoineStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s3.hint"), LemoineStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s3.default"), LemoineStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s3.placeholder")),
                    Info("S4", LemoineStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s4.title"), LemoineStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s4.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.bulkViewsByLevel.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.bulkViewsByLevel.runLog.2"), "pass")),
            },

            ["Bulk Duplicate"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.bulkDuplicate.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.bulkDuplicate.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", LemoineStrings.T("overviewDemos.tools.bulkDuplicate.steps.s1.title"), true, LemoineStrings.T("overviewDemos.tools.bulkDuplicate.steps.s1.hint"), PlanViews),
                    Single("S2", LemoineStrings.T("overviewDemos.tools.bulkDuplicate.steps.s2.title"), true, LemoineStrings.T("overviewDemos.tools.bulkDuplicate.steps.s2.hint"), LemoineStrings.T("overviewDemos.tools.bulkDuplicate.steps.s2.items.1"), LemoineStrings.T("overviewDemos.tools.bulkDuplicate.steps.s2.items.2"), LemoineStrings.T("overviewDemos.tools.bulkDuplicate.steps.s2.items.3")),
                    Txt("S3", LemoineStrings.T("overviewDemos.tools.bulkDuplicate.steps.s3.title"), true, LemoineStrings.T("overviewDemos.tools.bulkDuplicate.steps.s3.hint"), LemoineStrings.T("overviewDemos.tools.bulkDuplicate.steps.s3.default"), LemoineStrings.T("overviewDemos.tools.bulkDuplicate.steps.s3.placeholder")),
                    Info("S4", LemoineStrings.T("overviewDemos.tools.bulkDuplicate.steps.s4.title"), LemoineStrings.T("overviewDemos.tools.bulkDuplicate.steps.s4.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.bulkDuplicate.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.bulkDuplicate.runLog.2"), "pass")),
            },

            ["Bulk Views by Template"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.bulkViewsByTemplate.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.bulkViewsByTemplate.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", LemoineStrings.T("overviewDemos.tools.bulkViewsByTemplate.steps.s1.title"), true, LemoineStrings.T("overviewDemos.tools.bulkViewsByTemplate.steps.s1.hint"), PlanViews),
                    MultiFlat("S2", LemoineStrings.T("overviewDemos.tools.bulkViewsByTemplate.steps.s2.title"), true, LemoineStrings.T("overviewDemos.tools.bulkViewsByTemplate.steps.s2.hint"), Templates),
                    Txt("S3", LemoineStrings.T("overviewDemos.tools.bulkViewsByTemplate.steps.s3.title"), true, LemoineStrings.T("overviewDemos.tools.bulkViewsByTemplate.steps.s3.hint"), LemoineStrings.T("overviewDemos.tools.bulkViewsByTemplate.steps.s3.default"), LemoineStrings.T("overviewDemos.tools.bulkViewsByTemplate.steps.s3.placeholder")),
                    Info("S4", LemoineStrings.T("overviewDemos.tools.bulkViewsByTemplate.steps.s4.title"), LemoineStrings.T("overviewDemos.tools.bulkViewsByTemplate.steps.s4.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.bulkViewsByTemplate.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.bulkViewsByTemplate.runLog.2"), "pass")),
            },

            ["Bulk Dependent Views"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.bulkDependentViews.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.bulkDependentViews.runLabel"),
                Steps = new[]
                {
                    Single("S1", LemoineStrings.T("overviewDemos.tools.bulkDependentViews.steps.s1.title"), true, LemoineStrings.T("overviewDemos.tools.bulkDependentViews.steps.s1.hint"), PlanViews.ToArray()),
                    Info("S2", LemoineStrings.T("overviewDemos.tools.bulkDependentViews.steps.s2.title"), LemoineStrings.T("overviewDemos.tools.bulkDependentViews.steps.s2.info")),
                    MultiFlat("S3", LemoineStrings.T("overviewDemos.tools.bulkDependentViews.steps.s3.title"), true, LemoineStrings.T("overviewDemos.tools.bulkDependentViews.steps.s3.hint"), PlanViews),
                    Txt("S4", LemoineStrings.T("overviewDemos.tools.bulkDependentViews.steps.s4.title"), false, LemoineStrings.T("overviewDemos.tools.bulkDependentViews.steps.s4.hint"), LemoineStrings.T("overviewDemos.tools.bulkDependentViews.steps.s4.default"), LemoineStrings.T("overviewDemos.tools.bulkDependentViews.steps.s4.placeholder")),
                    Info("S5", LemoineStrings.T("overviewDemos.tools.bulkDependentViews.steps.s5.title"), LemoineStrings.T("overviewDemos.tools.bulkDependentViews.steps.s5.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.bulkDependentViews.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.bulkDependentViews.runLog.2"), "pass"), (LemoineStrings.T("overviewDemos.tools.bulkDependentViews.runLog.3"), "pass")),
            },

            ["Explode View by Trade"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.explodeViewByTrade.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.explodeViewByTrade.runLabel"),
                Steps = new[]
                {
                    Single("S1", LemoineStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s1.title"), true, LemoineStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s1.hint"), Views3D.ToArray()),
                    MultiFlat("S2", LemoineStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s2.title"), true, LemoineStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s2.hint"), Trades),
                    Toggles("S3", LemoineStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s3.title"), LemoineStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s3.hint"),
                        Tg("box", LemoineStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s3.toggles.box.label"), LemoineStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s3.toggles.box.desc"), true),
                        Tg("stack", LemoineStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s3.toggles.stack.label"), LemoineStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s3.toggles.stack.desc"), true),
                        Tg("tags", LemoineStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s3.toggles.tags.label"), LemoineStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s3.toggles.tags.desc"), false)),
                    Info("S4", LemoineStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s4.title"), LemoineStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s4.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.explodeViewByTrade.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.explodeViewByTrade.runLog.2"), "pass"), (LemoineStrings.T("overviewDemos.tools.explodeViewByTrade.runLog.3"), "pass")),
            },

            ["Place Dependent Views"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.placeDependentViews.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.placeDependentViews.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", LemoineStrings.T("overviewDemos.tools.placeDependentViews.steps.s1.title"), true, LemoineStrings.T("overviewDemos.tools.placeDependentViews.steps.s1.hint"), PlanViews),
                    Single("S2", LemoineStrings.T("overviewDemos.tools.placeDependentViews.steps.s2.title"), true, LemoineStrings.T("overviewDemos.tools.placeDependentViews.steps.s2.hint"), TitleBlocks.ToArray()),
                    Txt("S3", LemoineStrings.T("overviewDemos.tools.placeDependentViews.steps.s3.title"), true, LemoineStrings.T("overviewDemos.tools.placeDependentViews.steps.s3.hint"), LemoineStrings.T("overviewDemos.tools.placeDependentViews.steps.s3.default"), LemoineStrings.T("overviewDemos.tools.placeDependentViews.steps.s3.placeholder")),
                    Toggles("S4", LemoineStrings.T("overviewDemos.tools.placeDependentViews.steps.s4.title"), LemoineStrings.T("overviewDemos.tools.placeDependentViews.steps.s4.hint"),
                        Tg("pack", LemoineStrings.T("overviewDemos.tools.placeDependentViews.steps.s4.toggles.pack.label"), LemoineStrings.T("overviewDemos.tools.placeDependentViews.steps.s4.toggles.pack.desc"), true),
                        Tg("trim", LemoineStrings.T("overviewDemos.tools.placeDependentViews.steps.s4.toggles.trim.label"), LemoineStrings.T("overviewDemos.tools.placeDependentViews.steps.s4.toggles.trim.desc"), true),
                        Tg("align", LemoineStrings.T("overviewDemos.tools.placeDependentViews.steps.s4.toggles.align.label"), LemoineStrings.T("overviewDemos.tools.placeDependentViews.steps.s4.toggles.align.desc"), false)),
                    Info("S5", LemoineStrings.T("overviewDemos.tools.placeDependentViews.steps.s5.title"), LemoineStrings.T("overviewDemos.tools.placeDependentViews.steps.s5.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.placeDependentViews.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.placeDependentViews.runLog.2"), "pass"), (LemoineStrings.T("overviewDemos.tools.placeDependentViews.runLog.3"), "pass")),
            },

            ["Align Sheet Views"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.alignSheetViews.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.alignSheetViews.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", LemoineStrings.T("overviewDemos.tools.alignSheetViews.steps.s1.title"), true, LemoineStrings.T("overviewDemos.tools.alignSheetViews.steps.s1.hint"), Sheets),
                    MultiFlat("S2", LemoineStrings.T("overviewDemos.tools.alignSheetViews.steps.s2.title"), true, LemoineStrings.T("overviewDemos.tools.alignSheetViews.steps.s2.hint"), Sheets),
                    Toggles("S3", LemoineStrings.T("overviewDemos.tools.alignSheetViews.steps.s3.title"), LemoineStrings.T("overviewDemos.tools.alignSheetViews.steps.s3.hint"),
                        Tg("scope", LemoineStrings.T("overviewDemos.tools.alignSheetViews.steps.s3.toggles.scope.label"), LemoineStrings.T("overviewDemos.tools.alignSheetViews.steps.s3.toggles.scope.desc"), true),
                        Tg("crop", LemoineStrings.T("overviewDemos.tools.alignSheetViews.steps.s3.toggles.crop.label"), LemoineStrings.T("overviewDemos.tools.alignSheetViews.steps.s3.toggles.crop.desc"), true),
                        Tg("vis", LemoineStrings.T("overviewDemos.tools.alignSheetViews.steps.s3.toggles.vis.label"), LemoineStrings.T("overviewDemos.tools.alignSheetViews.steps.s3.toggles.vis.desc"), false),
                        Tg("grid", LemoineStrings.T("overviewDemos.tools.alignSheetViews.steps.s3.toggles.grid.label"), LemoineStrings.T("overviewDemos.tools.alignSheetViews.steps.s3.toggles.grid.desc"), false)),
                    Info("S4", LemoineStrings.T("overviewDemos.tools.alignSheetViews.steps.s4.title"), LemoineStrings.T("overviewDemos.tools.alignSheetViews.steps.s4.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.alignSheetViews.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.alignSheetViews.runLog.2"), "pass"), (LemoineStrings.T("overviewDemos.tools.alignSheetViews.runLog.3"), "skip")),
            },

            ["Bulk Rename"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.bulkRename.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.bulkRename.runLabel"),
                Steps = new[]
                {
                    Single("S1", LemoineStrings.T("overviewDemos.tools.bulkRename.steps.s1.title"), true, LemoineStrings.T("overviewDemos.tools.bulkRename.steps.s1.hint"), LemoineStrings.T("overviewDemos.tools.bulkRename.steps.s1.items.1"), LemoineStrings.T("overviewDemos.tools.bulkRename.steps.s1.items.2")),
                    MultiFlat("S2", LemoineStrings.T("overviewDemos.tools.bulkRename.steps.s2.title"), true, LemoineStrings.T("overviewDemos.tools.bulkRename.steps.s2.hint"), Sheets),
                    Single("S3", LemoineStrings.T("overviewDemos.tools.bulkRename.steps.s3.title"), true, LemoineStrings.T("overviewDemos.tools.bulkRename.steps.s3.hint"), LemoineStrings.T("overviewDemos.tools.bulkRename.steps.s3.items.1"), LemoineStrings.T("overviewDemos.tools.bulkRename.steps.s3.items.2"), LemoineStrings.T("overviewDemos.tools.bulkRename.steps.s3.items.3"), LemoineStrings.T("overviewDemos.tools.bulkRename.steps.s3.items.4"), LemoineStrings.T("overviewDemos.tools.bulkRename.steps.s3.items.5")),
                    Info("S4", LemoineStrings.T("overviewDemos.tools.bulkRename.steps.s4.title"), LemoineStrings.T("overviewDemos.tools.bulkRename.steps.s4.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.bulkRename.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.bulkRename.runLog.2"), "pass")),
            },

            ["Bulk Export"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.bulkExport.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.bulkExport.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s1.title"), true, LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s1.hint"), Sheets),
                    Toggles("S3", LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s3.title"), LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s3.hint"),
                        Tg("pdf", LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s3.toggles.pdf.label"), LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s3.toggles.pdf.desc"), true),
                        Tg("dwg", LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s3.toggles.dwg.label"), LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s3.toggles.dwg.desc"), false),
                        Tg("nwc", LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s3.toggles.nwc.label"), LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s3.toggles.nwc.desc"), false),
                        Tg("ifc", LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s3.toggles.ifc.label"), LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s3.toggles.ifc.desc"), false)),
                    Toggles("S4", LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s4.title"), LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s4.hint"),
                        Tg("combine", LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s4.toggles.combine.label"), LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s4.toggles.combine.desc"), true),
                        Tg("vector", LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s4.toggles.vector.label"), LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s4.toggles.vector.desc"), true),
                        Tg("hideref", LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s4.toggles.hideref.label"), LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s4.toggles.hideref.desc"), true)),
                    FileStep("S8", LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s8.title"), true, LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s8.hint"), LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s8.filter"), LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s8.placeholder")),
                    Info("S9", LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s9.title"), LemoineStrings.T("overviewDemos.tools.bulkExport.steps.s9.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.bulkExport.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.bulkExport.runLog.2"), "pass"), (LemoineStrings.T("overviewDemos.tools.bulkExport.runLog.3"), "pass")),
            },

            ["Print View"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.printView.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.printView.runLabel"),
                Steps = new[]
                {
                    Single("S1", LemoineStrings.T("overviewDemos.tools.printView.steps.s1.title"), true, LemoineStrings.T("overviewDemos.tools.printView.steps.s1.hint"), LemoineStrings.T("overviewDemos.tools.printView.steps.s1.items.1"), LemoineStrings.T("overviewDemos.tools.printView.steps.s1.items.2"), LemoineStrings.T("overviewDemos.tools.printView.steps.s1.items.3"), LemoineStrings.T("overviewDemos.tools.printView.steps.s1.items.4")),
                    Toggles("S2", LemoineStrings.T("overviewDemos.tools.printView.steps.s2.title"), LemoineStrings.T("overviewDemos.tools.printView.steps.s2.hint"),
                        Tg("vector", LemoineStrings.T("overviewDemos.tools.printView.steps.s2.toggles.vector.label"), LemoineStrings.T("overviewDemos.tools.printView.steps.s2.toggles.vector.desc"), true),
                        Tg("hideref", LemoineStrings.T("overviewDemos.tools.printView.steps.s2.toggles.hideref.label"), LemoineStrings.T("overviewDemos.tools.printView.steps.s2.toggles.hideref.desc"), true)),
                    FileStep("S6", LemoineStrings.T("overviewDemos.tools.printView.steps.s6.title"), true, LemoineStrings.T("overviewDemos.tools.printView.steps.s6.hint"), LemoineStrings.T("overviewDemos.tools.printView.steps.s6.filter"), LemoineStrings.T("overviewDemos.tools.printView.steps.s6.placeholder")),
                    Info("S7", LemoineStrings.T("overviewDemos.tools.printView.steps.s7.title"), LemoineStrings.T("overviewDemos.tools.printView.steps.s7.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.printView.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.printView.runLog.2"), "pass")),
            },

            ["Split by Levels"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.splitByLevels.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.splitByLevels.runLabel"),
                Steps = new[]
                {
                    Multi("S1", LemoineStrings.T("overviewDemos.tools.splitByLevels.steps.s1.title"), true, LemoineStrings.T("overviewDemos.tools.splitByLevels.steps.s1.hint"), Categories()),
                    MultiFlat("S2", LemoineStrings.T("overviewDemos.tools.splitByLevels.steps.s2.title"), true, LemoineStrings.T("overviewDemos.tools.splitByLevels.steps.s2.hint"), Levels),
                    Info("S3", LemoineStrings.T("overviewDemos.tools.splitByLevels.steps.s3.title"), LemoineStrings.T("overviewDemos.tools.splitByLevels.steps.s3.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.splitByLevels.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.splitByLevels.runLog.2"), "pass")),
            },

            ["Split by Grid Lines"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.splitByGridLines.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.splitByGridLines.runLabel"),
                Steps = new[]
                {
                    Multi("S1", LemoineStrings.T("overviewDemos.tools.splitByGridLines.steps.s1.title"), true, LemoineStrings.T("overviewDemos.tools.splitByGridLines.steps.s1.hint"), Categories()),
                    MultiFlat("S2", LemoineStrings.T("overviewDemos.tools.splitByGridLines.steps.s2.title"), true, LemoineStrings.T("overviewDemos.tools.splitByGridLines.steps.s2.hint"), Grids),
                    Info("S3", LemoineStrings.T("overviewDemos.tools.splitByGridLines.steps.s3.title"), LemoineStrings.T("overviewDemos.tools.splitByGridLines.steps.s3.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.splitByGridLines.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.splitByGridLines.runLog.2"), "pass")),
            },

            ["Split by Ref Plane"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.splitByRefPlane.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.splitByRefPlane.runLabel"),
                Steps = new[]
                {
                    Multi("S1", LemoineStrings.T("overviewDemos.tools.splitByRefPlane.steps.s1.title"), true, LemoineStrings.T("overviewDemos.tools.splitByRefPlane.steps.s1.hint"), Categories()),
                    MultiFlat("S2", LemoineStrings.T("overviewDemos.tools.splitByRefPlane.steps.s2.title"), true, LemoineStrings.T("overviewDemos.tools.splitByRefPlane.steps.s2.hint"), RefPlanes),
                    Info("S3", LemoineStrings.T("overviewDemos.tools.splitByRefPlane.steps.s3.title"), LemoineStrings.T("overviewDemos.tools.splitByRefPlane.steps.s3.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.splitByRefPlane.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.splitByRefPlane.runLog.2"), "pass")),
            },

            ["Split by Cell"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.splitByCell.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.splitByCell.runLabel"),
                Steps = new[]
                {
                    Multi("S1", LemoineStrings.T("overviewDemos.tools.splitByCell.steps.s1.title"), true, LemoineStrings.T("overviewDemos.tools.splitByCell.steps.s1.hint"), Categories()),
                    Num("S2", LemoineStrings.T("overviewDemos.tools.splitByCell.steps.s2.title"), LemoineStrings.T("overviewDemos.tools.splitByCell.steps.s2.hint"), 2000, 100, 10000, 100, LemoineStrings.T("overviewDemos.tools.splitByCell.steps.s2.unit")),
                    Info("S3", LemoineStrings.T("overviewDemos.tools.splitByCell.steps.s3.title"), LemoineStrings.T("overviewDemos.tools.splitByCell.steps.s3.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.splitByCell.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.splitByCell.runLog.2"), "pass")),
            },

            ["Extend Walls"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.extendWalls.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.extendWalls.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", LemoineStrings.T("overviewDemos.tools.extendWalls.steps.s1.title"), true, LemoineStrings.T("overviewDemos.tools.extendWalls.steps.s1.hint"), Levels),
                    Toggles("S2", LemoineStrings.T("overviewDemos.tools.extendWalls.steps.s2.title"), LemoineStrings.T("overviewDemos.tools.extendWalls.steps.s2.hint"),
                        Tg("aboveceil", LemoineStrings.T("overviewDemos.tools.extendWalls.steps.s2.toggles.aboveceil.label"), LemoineStrings.T("overviewDemos.tools.extendWalls.steps.s2.toggles.aboveceil.desc"), true),
                        Tg("levelup", LemoineStrings.T("overviewDemos.tools.extendWalls.steps.s2.toggles.levelup.label"), LemoineStrings.T("overviewDemos.tools.extendWalls.steps.s2.toggles.levelup.desc"), true)),
                    Info("S3", LemoineStrings.T("overviewDemos.tools.extendWalls.steps.s3.title"), LemoineStrings.T("overviewDemos.tools.extendWalls.steps.s3.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.extendWalls.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.extendWalls.runLog.2"), "pass")),
            },

            ["Clash Definitions"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.clashDefinitions.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.clashDefinitions.runLabel"),
                Steps = new[]
                {
                    Txt("name", LemoineStrings.T("overviewDemos.tools.clashDefinitions.steps.name.title"), true, LemoineStrings.T("overviewDemos.tools.clashDefinitions.steps.name.hint"), LemoineStrings.T("overviewDemos.tools.clashDefinitions.steps.name.default"), LemoineStrings.T("overviewDemos.tools.clashDefinitions.steps.name.placeholder")),
                    Multi("groupA", LemoineStrings.T("overviewDemos.tools.clashDefinitions.steps.groupa.title"), true, LemoineStrings.T("overviewDemos.tools.clashDefinitions.steps.groupa.hint"), Categories()),
                    Multi("groupB", LemoineStrings.T("overviewDemos.tools.clashDefinitions.steps.groupb.title"), true, LemoineStrings.T("overviewDemos.tools.clashDefinitions.steps.groupb.hint"), Categories()),
                    Toggles("marker", LemoineStrings.T("overviewDemos.tools.clashDefinitions.steps.marker.title"), LemoineStrings.T("overviewDemos.tools.clashDefinitions.steps.marker.hint"),
                        Tg("color", LemoineStrings.T("overviewDemos.tools.clashDefinitions.steps.marker.toggles.color.label"), LemoineStrings.T("overviewDemos.tools.clashDefinitions.steps.marker.toggles.color.desc"), true),
                        Tg("tag", LemoineStrings.T("overviewDemos.tools.clashDefinitions.steps.marker.toggles.tag.label"), LemoineStrings.T("overviewDemos.tools.clashDefinitions.steps.marker.toggles.tag.desc"), true),
                        Tg("round", LemoineStrings.T("overviewDemos.tools.clashDefinitions.steps.marker.toggles.round.label"), LemoineStrings.T("overviewDemos.tools.clashDefinitions.steps.marker.toggles.round.desc"), false)),
                    Info("save", LemoineStrings.T("overviewDemos.tools.clashDefinitions.steps.save.title"), LemoineStrings.T("overviewDemos.tools.clashDefinitions.steps.save.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.clashDefinitions.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.clashDefinitions.runLog.2"), "pass")),
            },

            ["Clash Finder & Dimension"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.clashFinderDimension.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.clashFinderDimension.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", LemoineStrings.T("overviewDemos.tools.clashFinderDimension.steps.s1.title"), true, LemoineStrings.T("overviewDemos.tools.clashFinderDimension.steps.s1.hint"), Definitions),
                    MultiFlat("S2", LemoineStrings.T("overviewDemos.tools.clashFinderDimension.steps.s2.title"), true, LemoineStrings.T("overviewDemos.tools.clashFinderDimension.steps.s2.hint"), PlanViews),
                    Toggles("S3", LemoineStrings.T("overviewDemos.tools.clashFinderDimension.steps.s3.title"), LemoineStrings.T("overviewDemos.tools.clashFinderDimension.steps.s3.hint"),
                        Tg("color", LemoineStrings.T("overviewDemos.tools.clashFinderDimension.steps.s3.toggles.color.label"), LemoineStrings.T("overviewDemos.tools.clashFinderDimension.steps.s3.toggles.color.desc"), true),
                        Tg("tag", LemoineStrings.T("overviewDemos.tools.clashFinderDimension.steps.s3.toggles.tag.label"), LemoineStrings.T("overviewDemos.tools.clashFinderDimension.steps.s3.toggles.tag.desc"), true),
                        Tg("dense", LemoineStrings.T("overviewDemos.tools.clashFinderDimension.steps.s3.toggles.dense.label"), LemoineStrings.T("overviewDemos.tools.clashFinderDimension.steps.s3.toggles.dense.desc"), true)),
                    Toggles("S4", LemoineStrings.T("overviewDemos.tools.clashFinderDimension.steps.s4.title"), LemoineStrings.T("overviewDemos.tools.clashFinderDimension.steps.s4.hint"),
                        Tg("grid", LemoineStrings.T("overviewDemos.tools.clashFinderDimension.steps.s4.toggles.grid.label"), LemoineStrings.T("overviewDemos.tools.clashFinderDimension.steps.s4.toggles.grid.desc"), true),
                        Tg("slab", LemoineStrings.T("overviewDemos.tools.clashFinderDimension.steps.s4.toggles.slab.label"), LemoineStrings.T("overviewDemos.tools.clashFinderDimension.steps.s4.toggles.slab.desc"), true),
                        Tg("scale", LemoineStrings.T("overviewDemos.tools.clashFinderDimension.steps.s4.toggles.scale.label"), LemoineStrings.T("overviewDemos.tools.clashFinderDimension.steps.s4.toggles.scale.desc"), false)),
                    Info("S5", LemoineStrings.T("overviewDemos.tools.clashFinderDimension.steps.s5.title"), LemoineStrings.T("overviewDemos.tools.clashFinderDimension.steps.s5.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.clashFinderDimension.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.clashFinderDimension.runLog.2"), "pass"), (LemoineStrings.T("overviewDemos.tools.clashFinderDimension.runLog.3"), "pass"), (LemoineStrings.T("overviewDemos.tools.clashFinderDimension.runLog.4"), "pass")),
            },

            ["Clash Finder & Elevation"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.clashFinderElevation.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.clashFinderElevation.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", LemoineStrings.T("overviewDemos.tools.clashFinderElevation.steps.s1.title"), true, LemoineStrings.T("overviewDemos.tools.clashFinderElevation.steps.s1.hint"), Definitions),
                    MultiFlat("S2", LemoineStrings.T("overviewDemos.tools.clashFinderElevation.steps.s2.title"), true, LemoineStrings.T("overviewDemos.tools.clashFinderElevation.steps.s2.hint"), Sections),
                    Single("S3", LemoineStrings.T("overviewDemos.tools.clashFinderElevation.steps.s3.title"), false, LemoineStrings.T("overviewDemos.tools.clashFinderElevation.steps.s3.hint"), LemoineStrings.T("overviewDemos.tools.clashFinderElevation.steps.s3.items.1"), LemoineStrings.T("overviewDemos.tools.clashFinderElevation.steps.s3.items.2"), LemoineStrings.T("overviewDemos.tools.clashFinderElevation.steps.s3.items.3")),
                    Info("S4", LemoineStrings.T("overviewDemos.tools.clashFinderElevation.steps.s4.title"), LemoineStrings.T("overviewDemos.tools.clashFinderElevation.steps.s4.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.clashFinderElevation.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.clashFinderElevation.runLog.2"), "pass"), (LemoineStrings.T("overviewDemos.tools.clashFinderElevation.runLog.3"), "pass")),
            },

            ["Refine Dimensions"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.refineDimensions.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.refineDimensions.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", LemoineStrings.T("overviewDemos.tools.refineDimensions.steps.s1.title"), true, LemoineStrings.T("overviewDemos.tools.refineDimensions.steps.s1.hint"), PlanViews),
                    Single("S2", LemoineStrings.T("overviewDemos.tools.refineDimensions.steps.s2.title"), false, LemoineStrings.T("overviewDemos.tools.refineDimensions.steps.s2.hint"), LemoineStrings.T("overviewDemos.tools.refineDimensions.steps.s2.items.1"), LemoineStrings.T("overviewDemos.tools.refineDimensions.steps.s2.items.2"), LemoineStrings.T("overviewDemos.tools.refineDimensions.steps.s2.items.3")),
                    Info("S3", LemoineStrings.T("overviewDemos.tools.refineDimensions.steps.s3.title"), LemoineStrings.T("overviewDemos.tools.refineDimensions.steps.s3.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.refineDimensions.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.refineDimensions.runLog.2"), "pass")),
            },

            ["Copy Linear"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.copyLinear.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.copyLinear.runLabel"),
                Steps = new[]
                {
                    Multi("source", LemoineStrings.T("overviewDemos.tools.copyLinear.steps.source.title"), true, LemoineStrings.T("overviewDemos.tools.copyLinear.steps.source.hint"), Groups((LemoineStrings.T("overviewDemos.tools.copyLinear.steps.source.groups.linkedModel.label"), Documents), (LemoineStrings.T("overviewDemos.tools.copyLinear.steps.source.groups.runCategories.label"), L(LemoineStrings.T("overviewDemos.tools.copyLinear.steps.source.groups.runCategories.items.1"), LemoineStrings.T("overviewDemos.tools.copyLinear.steps.source.groups.runCategories.items.2"), LemoineStrings.T("overviewDemos.tools.copyLinear.steps.source.groups.runCategories.items.3"))))),
                    MultiFlat("filters", LemoineStrings.T("overviewDemos.tools.copyLinear.steps.filters.title"), false, LemoineStrings.T("overviewDemos.tools.copyLinear.steps.filters.hint"), L(LemoineStrings.T("overviewDemos.tools.copyLinear.steps.filters.items.1"), LemoineStrings.T("overviewDemos.tools.copyLinear.steps.filters.items.2"), LemoineStrings.T("overviewDemos.tools.copyLinear.steps.filters.items.3"))),
                    Single("operation", LemoineStrings.T("overviewDemos.tools.copyLinear.steps.operation.title"), true, LemoineStrings.T("overviewDemos.tools.copyLinear.steps.operation.hint"), LemoineStrings.T("overviewDemos.tools.copyLinear.steps.operation.items.1"), LemoineStrings.T("overviewDemos.tools.copyLinear.steps.operation.items.2")),
                    Toggles("changes", LemoineStrings.T("overviewDemos.tools.copyLinear.steps.changes.title"), LemoineStrings.T("overviewDemos.tools.copyLinear.steps.changes.hint"),
                        Tg("changed", LemoineStrings.T("overviewDemos.tools.copyLinear.steps.changes.toggles.changed.label"), LemoineStrings.T("overviewDemos.tools.copyLinear.steps.changes.toggles.changed.desc"), true),
                        Tg("stamp", LemoineStrings.T("overviewDemos.tools.copyLinear.steps.changes.toggles.stamp.label"), LemoineStrings.T("overviewDemos.tools.copyLinear.steps.changes.toggles.stamp.desc"), true)),
                    Info("run", LemoineStrings.T("overviewDemos.tools.copyLinear.steps.run.title"), LemoineStrings.T("overviewDemos.tools.copyLinear.steps.run.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.copyLinear.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.copyLinear.runLog.2"), "pass"), (LemoineStrings.T("overviewDemos.tools.copyLinear.runLog.3"), "pass")),
            },

            ["Copy Grids"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.copyGrids.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.copyGrids.runLabel"),
                Steps = new[]
                {
                    Multi("source", LemoineStrings.T("overviewDemos.tools.copyGrids.steps.source.title"), true, LemoineStrings.T("overviewDemos.tools.copyGrids.steps.source.hint"), Groups((LemoineStrings.T("overviewDemos.tools.copyGrids.steps.source.groups.linkedModel.label"), Documents), (LemoineStrings.T("overviewDemos.tools.copyGrids.steps.source.groups.grids.label"), Grids))),
                    Info("run", LemoineStrings.T("overviewDemos.tools.copyGrids.steps.run.title"), LemoineStrings.T("overviewDemos.tools.copyGrids.steps.run.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.copyGrids.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.copyGrids.runLog.2"), "pass"), (LemoineStrings.T("overviewDemos.tools.copyGrids.runLog.3"), "skip")),
            },

            ["Copy Elements"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.copyElements.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.copyElements.runLabel"),
                Steps = new[]
                {
                    Multi("source", LemoineStrings.T("overviewDemos.tools.copyElements.steps.source.title"), true, LemoineStrings.T("overviewDemos.tools.copyElements.steps.source.hint"), Groups((LemoineStrings.T("overviewDemos.tools.copyElements.steps.source.groups.linkedModel.label"), Documents), (LemoineStrings.T("overviewDemos.tools.copyElements.steps.source.groups.categories.label"), L(LemoineStrings.T("overviewDemos.tools.copyElements.steps.source.groups.categories.items.1"), LemoineStrings.T("overviewDemos.tools.copyElements.steps.source.groups.categories.items.2"), LemoineStrings.T("overviewDemos.tools.copyElements.steps.source.groups.categories.items.3"))))),
                    MultiFlat("types", LemoineStrings.T("overviewDemos.tools.copyElements.steps.types.title"), true, LemoineStrings.T("overviewDemos.tools.copyElements.steps.types.hint"), L(LemoineStrings.T("overviewDemos.tools.copyElements.steps.types.items.1"), LemoineStrings.T("overviewDemos.tools.copyElements.steps.types.items.2"), LemoineStrings.T("overviewDemos.tools.copyElements.steps.types.items.3"), LemoineStrings.T("overviewDemos.tools.copyElements.steps.types.items.4"))),
                    Toggles("changes", LemoineStrings.T("overviewDemos.tools.copyElements.steps.changes.title"), LemoineStrings.T("overviewDemos.tools.copyElements.steps.changes.hint"),
                        Tg("changed", LemoineStrings.T("overviewDemos.tools.copyElements.steps.changes.toggles.changed.label"), LemoineStrings.T("overviewDemos.tools.copyElements.steps.changes.toggles.changed.desc"), true),
                        Tg("stamp", LemoineStrings.T("overviewDemos.tools.copyElements.steps.changes.toggles.stamp.label"), LemoineStrings.T("overviewDemos.tools.copyElements.steps.changes.toggles.stamp.desc"), true)),
                    Info("run", LemoineStrings.T("overviewDemos.tools.copyElements.steps.run.title"), LemoineStrings.T("overviewDemos.tools.copyElements.steps.run.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.copyElements.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.copyElements.runLog.2"), "pass")),
            },

            ["Align Coordinates"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.alignCoordinates.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.alignCoordinates.runLabel"),
                Steps = new[]
                {
                    Composite("host", LemoineStrings.T("overviewDemos.tools.alignCoordinates.steps.host.title"), true, LemoineStrings.T("overviewDemos.tools.alignCoordinates.steps.host.hint"),
                        Pre(Single("grid1", LemoineStrings.T("overviewDemos.tools.alignCoordinates.steps.host.parts.grid1"), true, "", Grids.ToArray()), 0),
                        Pre(Single("grid2", LemoineStrings.T("overviewDemos.tools.alignCoordinates.steps.host.parts.grid2"), true, "", Grids.ToArray()), 1),
                        Pre(Single("level", LemoineStrings.T("overviewDemos.tools.alignCoordinates.steps.host.parts.level"), true, "", Levels.ToArray()), 0),
                        Toggles("points", LemoineStrings.T("overviewDemos.tools.alignCoordinates.steps.host.parts.points"), "",
                            Tg("survey", LemoineStrings.T("overviewDemos.tools.alignCoordinates.steps.host.parts.survey"), "", true),
                            Tg("pbp", LemoineStrings.T("overviewDemos.tools.alignCoordinates.steps.host.parts.pbp"), "", true))),
                    MultiFlat("links", LemoineStrings.T("overviewDemos.tools.alignCoordinates.steps.links.title"), false, LemoineStrings.T("overviewDemos.tools.alignCoordinates.steps.links.hint"), Links),
                    Info("run", LemoineStrings.T("overviewDemos.tools.alignCoordinates.steps.run.title"), LemoineStrings.T("overviewDemos.tools.alignCoordinates.steps.run.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.alignCoordinates.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.alignCoordinates.runLog.2"), "pass"), (LemoineStrings.T("overviewDemos.tools.alignCoordinates.runLog.3"), "pass")),
            },

            ["Compare Grids"] = new OverviewDemoSpec
            {
                Title = LemoineStrings.T("overviewDemos.tools.compareGrids.title"), RunLabel = LemoineStrings.T("overviewDemos.tools.compareGrids.runLabel"),
                Steps = new[]
                {
                    Composite("files", LemoineStrings.T("overviewDemos.tools.compareGrids.steps.files.title"), true, LemoineStrings.T("overviewDemos.tools.compareGrids.steps.files.hint"),
                        MultiFlat("links", LemoineStrings.T("overviewDemos.tools.compareGrids.steps.files.parts.links"), true, "", Links),
                        Toggles("host", "", "",
                            Tg("inclhost", LemoineStrings.T("overviewDemos.tools.compareGrids.steps.files.parts.inclHost"), "", true)),
                        Num("postol", LemoineStrings.T("overviewDemos.tools.compareGrids.steps.files.parts.posTol"), "", 0.5, 0, 120, 0.0625, "", 3),
                        Num("angtol", LemoineStrings.T("overviewDemos.tools.compareGrids.steps.files.parts.angTol"), "", 0.5, 0, 45, 0.05, "", 2)),
                    Info("run", LemoineStrings.T("overviewDemos.tools.compareGrids.steps.run.title"), LemoineStrings.T("overviewDemos.tools.compareGrids.steps.run.info")),
                },
                RunLog = RL((LemoineStrings.T("overviewDemos.tools.compareGrids.runLog.1"), "info"), (LemoineStrings.T("overviewDemos.tools.compareGrids.runLog.2"), "pass"), (LemoineStrings.T("overviewDemos.tools.compareGrids.runLog.3"), "skip")),
            },
        };

            string sampledFrom = OverviewSamples.Current?.DocumentTitle ?? "";
            foreach (var s in specs.Values) s.SampledFrom = sampledFrom;
            return specs;
        }
    }
}
