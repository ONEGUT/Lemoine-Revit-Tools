using System;
using System.Collections.Generic;
using System.Linq;
using LemoineTools.Framework.Controls;

namespace LemoineTools.Framework
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
            for (int i = 1; i <= count; i++) list.Add(AppStrings.T($"{prefix}.{i}"));
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
                [AppStrings.T("overviewDemos.categoryGroups.mechanical.label")] = Canned("overviewDemos.categoryGroups.mechanical.items", 3),
                [AppStrings.T("overviewDemos.categoryGroups.piping.label")]     = Canned("overviewDemos.categoryGroups.piping.items", 3),
                [AppStrings.T("overviewDemos.categoryGroups.electrical.label")] = Canned("overviewDemos.categoryGroups.electrical.items", 2),
                [AppStrings.T("overviewDemos.categoryGroups.structure.label")]  = Canned("overviewDemos.categoryGroups.structure.items", 3),
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
                Title = AppStrings.T("overviewDemos.tools.autoFilters.title"), RunLabel = AppStrings.T("overviewDemos.tools.autoFilters.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", AppStrings.T("overviewDemos.tools.autoFilters.steps.s1.title"), true, AppStrings.T("overviewDemos.tools.autoFilters.steps.s1.hint"), Documents),
                    Toggles("S2", AppStrings.T("overviewDemos.tools.autoFilters.steps.s2.title"), AppStrings.T("overviewDemos.tools.autoFilters.steps.s2.hint"),
                        Tg("sys", AppStrings.T("overviewDemos.tools.autoFilters.steps.s2.toggles.sys.label"), AppStrings.T("overviewDemos.tools.autoFilters.steps.s2.toggles.sys.desc"), true),
                        Tg("fab", AppStrings.T("overviewDemos.tools.autoFilters.steps.s2.toggles.fab.label"), AppStrings.T("overviewDemos.tools.autoFilters.steps.s2.toggles.fab.desc"), false),
                        Tg("host", AppStrings.T("overviewDemos.tools.autoFilters.steps.s2.toggles.host.label"), AppStrings.T("overviewDemos.tools.autoFilters.steps.s2.toggles.host.desc"), false)),
                    Info("S3", AppStrings.T("overviewDemos.tools.autoFilters.steps.s3.title"), AppStrings.T("overviewDemos.tools.autoFilters.steps.s3.info")),
                    MultiFlat("S4", AppStrings.T("overviewDemos.tools.autoFilters.steps.s4.title"), true, AppStrings.T("overviewDemos.tools.autoFilters.steps.s4.hint"), Trades),
                    Info("S5", AppStrings.T("overviewDemos.tools.autoFilters.steps.s5.title"), AppStrings.T("overviewDemos.tools.autoFilters.steps.s5.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.autoFilters.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.autoFilters.runLog.2"), "pass"), (AppStrings.T("overviewDemos.tools.autoFilters.runLog.3"), "pass"), (AppStrings.T("overviewDemos.tools.autoFilters.runLog.4"), "pass")),
            },

            ["Legend Creation"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.legendCreation.title"), RunLabel = AppStrings.T("overviewDemos.tools.legendCreation.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", AppStrings.T("overviewDemos.tools.legendCreation.steps.s1.title"), true, AppStrings.T("overviewDemos.tools.legendCreation.steps.s1.hint"), Filters),
                    Single("S2", AppStrings.T("overviewDemos.tools.legendCreation.steps.s2.title"), true, AppStrings.T("overviewDemos.tools.legendCreation.steps.s2.hint"), AppStrings.T("overviewDemos.tools.legendCreation.steps.s2.items.1"), AppStrings.T("overviewDemos.tools.legendCreation.steps.s2.items.2"), AppStrings.T("overviewDemos.tools.legendCreation.steps.s2.items.3")),
                    Toggles("S3", AppStrings.T("overviewDemos.tools.legendCreation.steps.s3.title"), AppStrings.T("overviewDemos.tools.legendCreation.steps.s3.hint"),
                        Tg("swatch", AppStrings.T("overviewDemos.tools.legendCreation.steps.s3.toggles.swatch.label"), AppStrings.T("overviewDemos.tools.legendCreation.steps.s3.toggles.swatch.desc"), true),
                        Tg("count", AppStrings.T("overviewDemos.tools.legendCreation.steps.s3.toggles.count.label"), AppStrings.T("overviewDemos.tools.legendCreation.steps.s3.toggles.count.desc"), false),
                        Tg("desc", AppStrings.T("overviewDemos.tools.legendCreation.steps.s3.toggles.desc.label"), AppStrings.T("overviewDemos.tools.legendCreation.steps.s3.toggles.desc.desc"), true)),
                    Info("S4", AppStrings.T("overviewDemos.tools.legendCreation.steps.s4.title"), AppStrings.T("overviewDemos.tools.legendCreation.steps.s4.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.legendCreation.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.legendCreation.runLog.2"), "pass"), (AppStrings.T("overviewDemos.tools.legendCreation.runLog.3"), "pass")),
            },

            ["Ceiling Heatmap"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.ceilingHeatmap.title"), RunLabel = AppStrings.T("overviewDemos.tools.ceilingHeatmap.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", AppStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s1.title"), true, AppStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s1.hint"), CeilingPlans),
                    Single("S_RAMP", AppStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s_ramp.title"), true, AppStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s_ramp.hint"), AppStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s_ramp.items.1"), AppStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s_ramp.items.2"), AppStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s_ramp.items.3")),
                    Toggles("S2", AppStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s2.title"), AppStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s2.hint"),
                        Tg("hidelinked", AppStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s2.toggles.hidelinked.label"), AppStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s2.toggles.hidelinked.desc"), true),
                        Tg("legend", AppStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s2.toggles.legend.label"), AppStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s2.toggles.legend.desc"), true),
                        Tg("halftone", AppStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s2.toggles.halftone.label"), AppStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s2.toggles.halftone.desc"), false)),
                    Info("S3", AppStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s3.title"), AppStrings.T("overviewDemos.tools.ceilingHeatmap.steps.s3.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.ceilingHeatmap.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.ceilingHeatmap.runLog.2"), "pass"), (AppStrings.T("overviewDemos.tools.ceilingHeatmap.runLog.3"), "pass"), (AppStrings.T("overviewDemos.tools.ceilingHeatmap.runLog.4"), "pass")),
            },

            ["Make Ceiling Grids"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.makeCeilingGrids.title"), RunLabel = AppStrings.T("overviewDemos.tools.makeCeilingGrids.runLabel"),
                Steps = new[]
                {
                    MultiFlat("docs", AppStrings.T("overviewDemos.tools.makeCeilingGrids.steps.docs.title"), true, AppStrings.T("overviewDemos.tools.makeCeilingGrids.steps.docs.hint"), Documents),
                    MultiFlat("filter", AppStrings.T("overviewDemos.tools.makeCeilingGrids.steps.filter.title"), false, AppStrings.T("overviewDemos.tools.makeCeilingGrids.steps.filter.hint"), L(AppStrings.T("overviewDemos.tools.makeCeilingGrids.steps.filter.items.1"), AppStrings.T("overviewDemos.tools.makeCeilingGrids.steps.filter.items.2"), AppStrings.T("overviewDemos.tools.makeCeilingGrids.steps.filter.items.3"))),
                    FileStep("export", AppStrings.T("overviewDemos.tools.makeCeilingGrids.steps.export.title"), true, AppStrings.T("overviewDemos.tools.makeCeilingGrids.steps.export.hint"), AppStrings.T("overviewDemos.tools.makeCeilingGrids.steps.export.filter"), AppStrings.T("overviewDemos.tools.makeCeilingGrids.steps.export.placeholder")),
                    Info("run", AppStrings.T("overviewDemos.tools.makeCeilingGrids.steps.run.title"), AppStrings.T("overviewDemos.tools.makeCeilingGrids.steps.run.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.makeCeilingGrids.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.makeCeilingGrids.runLog.2"), "pass"), (AppStrings.T("overviewDemos.tools.makeCeilingGrids.runLog.3"), "pass")),
            },

            ["Project Grids"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.projectGrids.title"), RunLabel = AppStrings.T("overviewDemos.tools.projectGrids.runLabel"),
                Steps = new[]
                {
                    FileStep("S1", AppStrings.T("overviewDemos.tools.projectGrids.steps.s1.title"), true, AppStrings.T("overviewDemos.tools.projectGrids.steps.s1.hint"), AppStrings.T("overviewDemos.tools.projectGrids.steps.s1.filter"), AppStrings.T("overviewDemos.tools.projectGrids.steps.s1.placeholder")),
                    Info("S2", AppStrings.T("overviewDemos.tools.projectGrids.steps.s2.title"), AppStrings.T("overviewDemos.tools.projectGrids.steps.s2.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.projectGrids.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.projectGrids.runLog.2"), "pass")),
            },

            ["Reproject Grids"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.reprojectGrids.title"), RunLabel = AppStrings.T("overviewDemos.tools.reprojectGrids.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", AppStrings.T("overviewDemos.tools.reprojectGrids.steps.s1.title"), true, AppStrings.T("overviewDemos.tools.reprojectGrids.steps.s1.hint"), CeilingPlans),
                    Info("S2", AppStrings.T("overviewDemos.tools.reprojectGrids.steps.s2.title"), AppStrings.T("overviewDemos.tools.reprojectGrids.steps.s2.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.reprojectGrids.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.reprojectGrids.runLog.2"), "pass")),
            },

            // Bulk Views merged 4 separate tools (by level, duplicate, by template, dependents)
            // into one 5-mode tool (WS-11); this demo (formerly "Bulk Views by Level") is kept
            // as the representative walkthrough. The other 3 demos' dictionary entries were
            // removed since no card resolves to their old tool names anymore — their JSON
            // strings stay in overviewDemos.json (harmless unused text, not a broken lookup).
            ["Bulk Views"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.bulkViewsByLevel.title"), RunLabel = AppStrings.T("overviewDemos.tools.bulkViewsByLevel.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", AppStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s1.title"), true, AppStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s1.hint"), Levels),
                    Multi("S2", AppStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s2.title"), true, AppStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s2.hint"), Groups((AppStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s2.groups.viewTypes.label"), L(AppStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s2.groups.viewTypes.items.1"), AppStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s2.groups.viewTypes.items.2"), AppStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s2.groups.viewTypes.items.3"))))),
                    Txt("S3", AppStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s3.title"), false, AppStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s3.hint"), AppStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s3.default"), AppStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s3.placeholder")),
                    Info("S4", AppStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s4.title"), AppStrings.T("overviewDemos.tools.bulkViewsByLevel.steps.s4.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.bulkViewsByLevel.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.bulkViewsByLevel.runLog.2"), "pass")),
            },

            ["Explode View by Trade"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.explodeViewByTrade.title"), RunLabel = AppStrings.T("overviewDemos.tools.explodeViewByTrade.runLabel"),
                Steps = new[]
                {
                    Single("S1", AppStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s1.title"), true, AppStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s1.hint"), Views3D.ToArray()),
                    MultiFlat("S2", AppStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s2.title"), true, AppStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s2.hint"), Trades),
                    Toggles("S3", AppStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s3.title"), AppStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s3.hint"),
                        Tg("box", AppStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s3.toggles.box.label"), AppStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s3.toggles.box.desc"), true),
                        Tg("stack", AppStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s3.toggles.stack.label"), AppStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s3.toggles.stack.desc"), true),
                        Tg("tags", AppStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s3.toggles.tags.label"), AppStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s3.toggles.tags.desc"), false)),
                    Info("S4", AppStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s4.title"), AppStrings.T("overviewDemos.tools.explodeViewByTrade.steps.s4.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.explodeViewByTrade.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.explodeViewByTrade.runLog.2"), "pass"), (AppStrings.T("overviewDemos.tools.explodeViewByTrade.runLog.3"), "pass")),
            },

            ["Place Dependent Views"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.placeDependentViews.title"), RunLabel = AppStrings.T("overviewDemos.tools.placeDependentViews.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", AppStrings.T("overviewDemos.tools.placeDependentViews.steps.s1.title"), true, AppStrings.T("overviewDemos.tools.placeDependentViews.steps.s1.hint"), PlanViews),
                    Single("S2", AppStrings.T("overviewDemos.tools.placeDependentViews.steps.s2.title"), true, AppStrings.T("overviewDemos.tools.placeDependentViews.steps.s2.hint"), TitleBlocks.ToArray()),
                    Txt("S3", AppStrings.T("overviewDemos.tools.placeDependentViews.steps.s3.title"), true, AppStrings.T("overviewDemos.tools.placeDependentViews.steps.s3.hint"), AppStrings.T("overviewDemos.tools.placeDependentViews.steps.s3.default"), AppStrings.T("overviewDemos.tools.placeDependentViews.steps.s3.placeholder")),
                    Toggles("S4", AppStrings.T("overviewDemos.tools.placeDependentViews.steps.s4.title"), AppStrings.T("overviewDemos.tools.placeDependentViews.steps.s4.hint"),
                        Tg("pack", AppStrings.T("overviewDemos.tools.placeDependentViews.steps.s4.toggles.pack.label"), AppStrings.T("overviewDemos.tools.placeDependentViews.steps.s4.toggles.pack.desc"), true),
                        Tg("trim", AppStrings.T("overviewDemos.tools.placeDependentViews.steps.s4.toggles.trim.label"), AppStrings.T("overviewDemos.tools.placeDependentViews.steps.s4.toggles.trim.desc"), true),
                        Tg("align", AppStrings.T("overviewDemos.tools.placeDependentViews.steps.s4.toggles.align.label"), AppStrings.T("overviewDemos.tools.placeDependentViews.steps.s4.toggles.align.desc"), false)),
                    Info("S5", AppStrings.T("overviewDemos.tools.placeDependentViews.steps.s5.title"), AppStrings.T("overviewDemos.tools.placeDependentViews.steps.s5.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.placeDependentViews.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.placeDependentViews.runLog.2"), "pass"), (AppStrings.T("overviewDemos.tools.placeDependentViews.runLog.3"), "pass")),
            },

            ["Align Sheet Views"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.alignSheetViews.title"), RunLabel = AppStrings.T("overviewDemos.tools.alignSheetViews.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", AppStrings.T("overviewDemos.tools.alignSheetViews.steps.s1.title"), true, AppStrings.T("overviewDemos.tools.alignSheetViews.steps.s1.hint"), Sheets),
                    MultiFlat("S2", AppStrings.T("overviewDemos.tools.alignSheetViews.steps.s2.title"), true, AppStrings.T("overviewDemos.tools.alignSheetViews.steps.s2.hint"), Sheets),
                    Toggles("S3", AppStrings.T("overviewDemos.tools.alignSheetViews.steps.s3.title"), AppStrings.T("overviewDemos.tools.alignSheetViews.steps.s3.hint"),
                        Tg("scope", AppStrings.T("overviewDemos.tools.alignSheetViews.steps.s3.toggles.scope.label"), AppStrings.T("overviewDemos.tools.alignSheetViews.steps.s3.toggles.scope.desc"), true),
                        Tg("crop", AppStrings.T("overviewDemos.tools.alignSheetViews.steps.s3.toggles.crop.label"), AppStrings.T("overviewDemos.tools.alignSheetViews.steps.s3.toggles.crop.desc"), true),
                        Tg("vis", AppStrings.T("overviewDemos.tools.alignSheetViews.steps.s3.toggles.vis.label"), AppStrings.T("overviewDemos.tools.alignSheetViews.steps.s3.toggles.vis.desc"), false),
                        Tg("grid", AppStrings.T("overviewDemos.tools.alignSheetViews.steps.s3.toggles.grid.label"), AppStrings.T("overviewDemos.tools.alignSheetViews.steps.s3.toggles.grid.desc"), false)),
                    Info("S4", AppStrings.T("overviewDemos.tools.alignSheetViews.steps.s4.title"), AppStrings.T("overviewDemos.tools.alignSheetViews.steps.s4.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.alignSheetViews.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.alignSheetViews.runLog.2"), "pass"), (AppStrings.T("overviewDemos.tools.alignSheetViews.runLog.3"), "skip")),
            },

            ["Bulk Rename"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.bulkRename.title"), RunLabel = AppStrings.T("overviewDemos.tools.bulkRename.runLabel"),
                Steps = new[]
                {
                    Single("S1", AppStrings.T("overviewDemos.tools.bulkRename.steps.s1.title"), true, AppStrings.T("overviewDemos.tools.bulkRename.steps.s1.hint"), AppStrings.T("overviewDemos.tools.bulkRename.steps.s1.items.1"), AppStrings.T("overviewDemos.tools.bulkRename.steps.s1.items.2")),
                    MultiFlat("S2", AppStrings.T("overviewDemos.tools.bulkRename.steps.s2.title"), true, AppStrings.T("overviewDemos.tools.bulkRename.steps.s2.hint"), Sheets),
                    Single("S3", AppStrings.T("overviewDemos.tools.bulkRename.steps.s3.title"), true, AppStrings.T("overviewDemos.tools.bulkRename.steps.s3.hint"), AppStrings.T("overviewDemos.tools.bulkRename.steps.s3.items.1"), AppStrings.T("overviewDemos.tools.bulkRename.steps.s3.items.2"), AppStrings.T("overviewDemos.tools.bulkRename.steps.s3.items.3"), AppStrings.T("overviewDemos.tools.bulkRename.steps.s3.items.4"), AppStrings.T("overviewDemos.tools.bulkRename.steps.s3.items.5")),
                    Info("S4", AppStrings.T("overviewDemos.tools.bulkRename.steps.s4.title"), AppStrings.T("overviewDemos.tools.bulkRename.steps.s4.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.bulkRename.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.bulkRename.runLog.2"), "pass")),
            },

            ["Bulk Export"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.bulkExport.title"), RunLabel = AppStrings.T("overviewDemos.tools.bulkExport.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", AppStrings.T("overviewDemos.tools.bulkExport.steps.s1.title"), true, AppStrings.T("overviewDemos.tools.bulkExport.steps.s1.hint"), Sheets),
                    Toggles("S3", AppStrings.T("overviewDemos.tools.bulkExport.steps.s3.title"), AppStrings.T("overviewDemos.tools.bulkExport.steps.s3.hint"),
                        Tg("pdf", AppStrings.T("overviewDemos.tools.bulkExport.steps.s3.toggles.pdf.label"), AppStrings.T("overviewDemos.tools.bulkExport.steps.s3.toggles.pdf.desc"), true),
                        Tg("dwg", AppStrings.T("overviewDemos.tools.bulkExport.steps.s3.toggles.dwg.label"), AppStrings.T("overviewDemos.tools.bulkExport.steps.s3.toggles.dwg.desc"), false),
                        Tg("nwc", AppStrings.T("overviewDemos.tools.bulkExport.steps.s3.toggles.nwc.label"), AppStrings.T("overviewDemos.tools.bulkExport.steps.s3.toggles.nwc.desc"), false),
                        Tg("ifc", AppStrings.T("overviewDemos.tools.bulkExport.steps.s3.toggles.ifc.label"), AppStrings.T("overviewDemos.tools.bulkExport.steps.s3.toggles.ifc.desc"), false)),
                    Toggles("S4", AppStrings.T("overviewDemos.tools.bulkExport.steps.s4.title"), AppStrings.T("overviewDemos.tools.bulkExport.steps.s4.hint"),
                        Tg("combine", AppStrings.T("overviewDemos.tools.bulkExport.steps.s4.toggles.combine.label"), AppStrings.T("overviewDemos.tools.bulkExport.steps.s4.toggles.combine.desc"), true),
                        Tg("vector", AppStrings.T("overviewDemos.tools.bulkExport.steps.s4.toggles.vector.label"), AppStrings.T("overviewDemos.tools.bulkExport.steps.s4.toggles.vector.desc"), true),
                        Tg("hideref", AppStrings.T("overviewDemos.tools.bulkExport.steps.s4.toggles.hideref.label"), AppStrings.T("overviewDemos.tools.bulkExport.steps.s4.toggles.hideref.desc"), true)),
                    FileStep("S8", AppStrings.T("overviewDemos.tools.bulkExport.steps.s8.title"), true, AppStrings.T("overviewDemos.tools.bulkExport.steps.s8.hint"), AppStrings.T("overviewDemos.tools.bulkExport.steps.s8.filter"), AppStrings.T("overviewDemos.tools.bulkExport.steps.s8.placeholder")),
                    Info("S9", AppStrings.T("overviewDemos.tools.bulkExport.steps.s9.title"), AppStrings.T("overviewDemos.tools.bulkExport.steps.s9.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.bulkExport.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.bulkExport.runLog.2"), "pass"), (AppStrings.T("overviewDemos.tools.bulkExport.runLog.3"), "pass")),
            },

            ["Print View"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.printView.title"), RunLabel = AppStrings.T("overviewDemos.tools.printView.runLabel"),
                Steps = new[]
                {
                    Single("S1", AppStrings.T("overviewDemos.tools.printView.steps.s1.title"), true, AppStrings.T("overviewDemos.tools.printView.steps.s1.hint"), AppStrings.T("overviewDemos.tools.printView.steps.s1.items.1"), AppStrings.T("overviewDemos.tools.printView.steps.s1.items.2"), AppStrings.T("overviewDemos.tools.printView.steps.s1.items.3"), AppStrings.T("overviewDemos.tools.printView.steps.s1.items.4")),
                    Toggles("S2", AppStrings.T("overviewDemos.tools.printView.steps.s2.title"), AppStrings.T("overviewDemos.tools.printView.steps.s2.hint"),
                        Tg("vector", AppStrings.T("overviewDemos.tools.printView.steps.s2.toggles.vector.label"), AppStrings.T("overviewDemos.tools.printView.steps.s2.toggles.vector.desc"), true),
                        Tg("hideref", AppStrings.T("overviewDemos.tools.printView.steps.s2.toggles.hideref.label"), AppStrings.T("overviewDemos.tools.printView.steps.s2.toggles.hideref.desc"), true)),
                    FileStep("S6", AppStrings.T("overviewDemos.tools.printView.steps.s6.title"), true, AppStrings.T("overviewDemos.tools.printView.steps.s6.hint"), AppStrings.T("overviewDemos.tools.printView.steps.s6.filter"), AppStrings.T("overviewDemos.tools.printView.steps.s6.placeholder")),
                    Info("S7", AppStrings.T("overviewDemos.tools.printView.steps.s7.title"), AppStrings.T("overviewDemos.tools.printView.steps.s7.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.printView.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.printView.runLog.2"), "pass")),
            },

            ["Split by Levels"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.splitByLevels.title"), RunLabel = AppStrings.T("overviewDemos.tools.splitByLevels.runLabel"),
                Steps = new[]
                {
                    Multi("S1", AppStrings.T("overviewDemos.tools.splitByLevels.steps.s1.title"), true, AppStrings.T("overviewDemos.tools.splitByLevels.steps.s1.hint"), Categories()),
                    MultiFlat("S2", AppStrings.T("overviewDemos.tools.splitByLevels.steps.s2.title"), true, AppStrings.T("overviewDemos.tools.splitByLevels.steps.s2.hint"), Levels),
                    Info("S3", AppStrings.T("overviewDemos.tools.splitByLevels.steps.s3.title"), AppStrings.T("overviewDemos.tools.splitByLevels.steps.s3.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.splitByLevels.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.splitByLevels.runLog.2"), "pass")),
            },

            ["Split by Grid Lines"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.splitByGridLines.title"), RunLabel = AppStrings.T("overviewDemos.tools.splitByGridLines.runLabel"),
                Steps = new[]
                {
                    Multi("S1", AppStrings.T("overviewDemos.tools.splitByGridLines.steps.s1.title"), true, AppStrings.T("overviewDemos.tools.splitByGridLines.steps.s1.hint"), Categories()),
                    MultiFlat("S2", AppStrings.T("overviewDemos.tools.splitByGridLines.steps.s2.title"), true, AppStrings.T("overviewDemos.tools.splitByGridLines.steps.s2.hint"), Grids),
                    Info("S3", AppStrings.T("overviewDemos.tools.splitByGridLines.steps.s3.title"), AppStrings.T("overviewDemos.tools.splitByGridLines.steps.s3.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.splitByGridLines.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.splitByGridLines.runLog.2"), "pass")),
            },

            ["Split by Ref Plane"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.splitByRefPlane.title"), RunLabel = AppStrings.T("overviewDemos.tools.splitByRefPlane.runLabel"),
                Steps = new[]
                {
                    Multi("S1", AppStrings.T("overviewDemos.tools.splitByRefPlane.steps.s1.title"), true, AppStrings.T("overviewDemos.tools.splitByRefPlane.steps.s1.hint"), Categories()),
                    MultiFlat("S2", AppStrings.T("overviewDemos.tools.splitByRefPlane.steps.s2.title"), true, AppStrings.T("overviewDemos.tools.splitByRefPlane.steps.s2.hint"), RefPlanes),
                    Info("S3", AppStrings.T("overviewDemos.tools.splitByRefPlane.steps.s3.title"), AppStrings.T("overviewDemos.tools.splitByRefPlane.steps.s3.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.splitByRefPlane.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.splitByRefPlane.runLog.2"), "pass")),
            },

            ["Split by Cell"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.splitByCell.title"), RunLabel = AppStrings.T("overviewDemos.tools.splitByCell.runLabel"),
                Steps = new[]
                {
                    Multi("S1", AppStrings.T("overviewDemos.tools.splitByCell.steps.s1.title"), true, AppStrings.T("overviewDemos.tools.splitByCell.steps.s1.hint"), Categories()),
                    Num("S2", AppStrings.T("overviewDemos.tools.splitByCell.steps.s2.title"), AppStrings.T("overviewDemos.tools.splitByCell.steps.s2.hint"), 2000, 100, 10000, 100, AppStrings.T("overviewDemos.tools.splitByCell.steps.s2.unit")),
                    Info("S3", AppStrings.T("overviewDemos.tools.splitByCell.steps.s3.title"), AppStrings.T("overviewDemos.tools.splitByCell.steps.s3.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.splitByCell.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.splitByCell.runLog.2"), "pass")),
            },

            ["Extend Walls"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.extendWalls.title"), RunLabel = AppStrings.T("overviewDemos.tools.extendWalls.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", AppStrings.T("overviewDemos.tools.extendWalls.steps.s1.title"), true, AppStrings.T("overviewDemos.tools.extendWalls.steps.s1.hint"), Levels),
                    Toggles("S2", AppStrings.T("overviewDemos.tools.extendWalls.steps.s2.title"), AppStrings.T("overviewDemos.tools.extendWalls.steps.s2.hint"),
                        Tg("aboveceil", AppStrings.T("overviewDemos.tools.extendWalls.steps.s2.toggles.aboveceil.label"), AppStrings.T("overviewDemos.tools.extendWalls.steps.s2.toggles.aboveceil.desc"), true),
                        Tg("levelup", AppStrings.T("overviewDemos.tools.extendWalls.steps.s2.toggles.levelup.label"), AppStrings.T("overviewDemos.tools.extendWalls.steps.s2.toggles.levelup.desc"), true)),
                    Info("S3", AppStrings.T("overviewDemos.tools.extendWalls.steps.s3.title"), AppStrings.T("overviewDemos.tools.extendWalls.steps.s3.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.extendWalls.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.extendWalls.runLog.2"), "pass")),
            },

            ["Clash Definitions"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.clashDefinitions.title"), RunLabel = AppStrings.T("overviewDemos.tools.clashDefinitions.runLabel"),
                Steps = new[]
                {
                    Txt("name", AppStrings.T("overviewDemos.tools.clashDefinitions.steps.name.title"), true, AppStrings.T("overviewDemos.tools.clashDefinitions.steps.name.hint"), AppStrings.T("overviewDemos.tools.clashDefinitions.steps.name.default"), AppStrings.T("overviewDemos.tools.clashDefinitions.steps.name.placeholder")),
                    Multi("groupA", AppStrings.T("overviewDemos.tools.clashDefinitions.steps.groupa.title"), true, AppStrings.T("overviewDemos.tools.clashDefinitions.steps.groupa.hint"), Categories()),
                    Multi("groupB", AppStrings.T("overviewDemos.tools.clashDefinitions.steps.groupb.title"), true, AppStrings.T("overviewDemos.tools.clashDefinitions.steps.groupb.hint"), Categories()),
                    Toggles("marker", AppStrings.T("overviewDemos.tools.clashDefinitions.steps.marker.title"), AppStrings.T("overviewDemos.tools.clashDefinitions.steps.marker.hint"),
                        Tg("color", AppStrings.T("overviewDemos.tools.clashDefinitions.steps.marker.toggles.color.label"), AppStrings.T("overviewDemos.tools.clashDefinitions.steps.marker.toggles.color.desc"), true),
                        Tg("tag", AppStrings.T("overviewDemos.tools.clashDefinitions.steps.marker.toggles.tag.label"), AppStrings.T("overviewDemos.tools.clashDefinitions.steps.marker.toggles.tag.desc"), true),
                        Tg("round", AppStrings.T("overviewDemos.tools.clashDefinitions.steps.marker.toggles.round.label"), AppStrings.T("overviewDemos.tools.clashDefinitions.steps.marker.toggles.round.desc"), false)),
                    Info("save", AppStrings.T("overviewDemos.tools.clashDefinitions.steps.save.title"), AppStrings.T("overviewDemos.tools.clashDefinitions.steps.save.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.clashDefinitions.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.clashDefinitions.runLog.2"), "pass")),
            },

            ["Clash Finder & Dimension"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.clashFinderDimension.title"), RunLabel = AppStrings.T("overviewDemos.tools.clashFinderDimension.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", AppStrings.T("overviewDemos.tools.clashFinderDimension.steps.s1.title"), true, AppStrings.T("overviewDemos.tools.clashFinderDimension.steps.s1.hint"), Definitions),
                    MultiFlat("S2", AppStrings.T("overviewDemos.tools.clashFinderDimension.steps.s2.title"), true, AppStrings.T("overviewDemos.tools.clashFinderDimension.steps.s2.hint"), PlanViews),
                    Toggles("S3", AppStrings.T("overviewDemos.tools.clashFinderDimension.steps.s3.title"), AppStrings.T("overviewDemos.tools.clashFinderDimension.steps.s3.hint"),
                        Tg("color", AppStrings.T("overviewDemos.tools.clashFinderDimension.steps.s3.toggles.color.label"), AppStrings.T("overviewDemos.tools.clashFinderDimension.steps.s3.toggles.color.desc"), true),
                        Tg("tag", AppStrings.T("overviewDemos.tools.clashFinderDimension.steps.s3.toggles.tag.label"), AppStrings.T("overviewDemos.tools.clashFinderDimension.steps.s3.toggles.tag.desc"), true),
                        Tg("dense", AppStrings.T("overviewDemos.tools.clashFinderDimension.steps.s3.toggles.dense.label"), AppStrings.T("overviewDemos.tools.clashFinderDimension.steps.s3.toggles.dense.desc"), true)),
                    Toggles("S4", AppStrings.T("overviewDemos.tools.clashFinderDimension.steps.s4.title"), AppStrings.T("overviewDemos.tools.clashFinderDimension.steps.s4.hint"),
                        Tg("grid", AppStrings.T("overviewDemos.tools.clashFinderDimension.steps.s4.toggles.grid.label"), AppStrings.T("overviewDemos.tools.clashFinderDimension.steps.s4.toggles.grid.desc"), true),
                        Tg("slab", AppStrings.T("overviewDemos.tools.clashFinderDimension.steps.s4.toggles.slab.label"), AppStrings.T("overviewDemos.tools.clashFinderDimension.steps.s4.toggles.slab.desc"), true),
                        Tg("scale", AppStrings.T("overviewDemos.tools.clashFinderDimension.steps.s4.toggles.scale.label"), AppStrings.T("overviewDemos.tools.clashFinderDimension.steps.s4.toggles.scale.desc"), false)),
                    Info("S5", AppStrings.T("overviewDemos.tools.clashFinderDimension.steps.s5.title"), AppStrings.T("overviewDemos.tools.clashFinderDimension.steps.s5.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.clashFinderDimension.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.clashFinderDimension.runLog.2"), "pass"), (AppStrings.T("overviewDemos.tools.clashFinderDimension.runLog.3"), "pass"), (AppStrings.T("overviewDemos.tools.clashFinderDimension.runLog.4"), "pass")),
            },

            ["Clash Finder & Elevation"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.clashFinderElevation.title"), RunLabel = AppStrings.T("overviewDemos.tools.clashFinderElevation.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", AppStrings.T("overviewDemos.tools.clashFinderElevation.steps.s1.title"), true, AppStrings.T("overviewDemos.tools.clashFinderElevation.steps.s1.hint"), Definitions),
                    MultiFlat("S2", AppStrings.T("overviewDemos.tools.clashFinderElevation.steps.s2.title"), true, AppStrings.T("overviewDemos.tools.clashFinderElevation.steps.s2.hint"), Sections),
                    Single("S3", AppStrings.T("overviewDemos.tools.clashFinderElevation.steps.s3.title"), false, AppStrings.T("overviewDemos.tools.clashFinderElevation.steps.s3.hint"), AppStrings.T("overviewDemos.tools.clashFinderElevation.steps.s3.items.1"), AppStrings.T("overviewDemos.tools.clashFinderElevation.steps.s3.items.2"), AppStrings.T("overviewDemos.tools.clashFinderElevation.steps.s3.items.3")),
                    Info("S4", AppStrings.T("overviewDemos.tools.clashFinderElevation.steps.s4.title"), AppStrings.T("overviewDemos.tools.clashFinderElevation.steps.s4.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.clashFinderElevation.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.clashFinderElevation.runLog.2"), "pass"), (AppStrings.T("overviewDemos.tools.clashFinderElevation.runLog.3"), "pass")),
            },

            ["Refine Dimensions"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.refineDimensions.title"), RunLabel = AppStrings.T("overviewDemos.tools.refineDimensions.runLabel"),
                Steps = new[]
                {
                    MultiFlat("S1", AppStrings.T("overviewDemos.tools.refineDimensions.steps.s1.title"), true, AppStrings.T("overviewDemos.tools.refineDimensions.steps.s1.hint"), PlanViews),
                    Single("S2", AppStrings.T("overviewDemos.tools.refineDimensions.steps.s2.title"), false, AppStrings.T("overviewDemos.tools.refineDimensions.steps.s2.hint"), AppStrings.T("overviewDemos.tools.refineDimensions.steps.s2.items.1"), AppStrings.T("overviewDemos.tools.refineDimensions.steps.s2.items.2"), AppStrings.T("overviewDemos.tools.refineDimensions.steps.s2.items.3")),
                    Info("S3", AppStrings.T("overviewDemos.tools.refineDimensions.steps.s3.title"), AppStrings.T("overviewDemos.tools.refineDimensions.steps.s3.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.refineDimensions.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.refineDimensions.runLog.2"), "pass")),
            },

            ["Copy Linear"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.copyLinear.title"), RunLabel = AppStrings.T("overviewDemos.tools.copyLinear.runLabel"),
                Steps = new[]
                {
                    Multi("source", AppStrings.T("overviewDemos.tools.copyLinear.steps.source.title"), true, AppStrings.T("overviewDemos.tools.copyLinear.steps.source.hint"), Groups((AppStrings.T("overviewDemos.tools.copyLinear.steps.source.groups.linkedModel.label"), Documents), (AppStrings.T("overviewDemos.tools.copyLinear.steps.source.groups.runCategories.label"), L(AppStrings.T("overviewDemos.tools.copyLinear.steps.source.groups.runCategories.items.1"), AppStrings.T("overviewDemos.tools.copyLinear.steps.source.groups.runCategories.items.2"), AppStrings.T("overviewDemos.tools.copyLinear.steps.source.groups.runCategories.items.3"))))),
                    MultiFlat("filters", AppStrings.T("overviewDemos.tools.copyLinear.steps.filters.title"), false, AppStrings.T("overviewDemos.tools.copyLinear.steps.filters.hint"), L(AppStrings.T("overviewDemos.tools.copyLinear.steps.filters.items.1"), AppStrings.T("overviewDemos.tools.copyLinear.steps.filters.items.2"), AppStrings.T("overviewDemos.tools.copyLinear.steps.filters.items.3"))),
                    Single("operation", AppStrings.T("overviewDemos.tools.copyLinear.steps.operation.title"), true, AppStrings.T("overviewDemos.tools.copyLinear.steps.operation.hint"), AppStrings.T("overviewDemos.tools.copyLinear.steps.operation.items.1"), AppStrings.T("overviewDemos.tools.copyLinear.steps.operation.items.2")),
                    Toggles("changes", AppStrings.T("overviewDemos.tools.copyLinear.steps.changes.title"), AppStrings.T("overviewDemos.tools.copyLinear.steps.changes.hint"),
                        Tg("changed", AppStrings.T("overviewDemos.tools.copyLinear.steps.changes.toggles.changed.label"), AppStrings.T("overviewDemos.tools.copyLinear.steps.changes.toggles.changed.desc"), true),
                        Tg("stamp", AppStrings.T("overviewDemos.tools.copyLinear.steps.changes.toggles.stamp.label"), AppStrings.T("overviewDemos.tools.copyLinear.steps.changes.toggles.stamp.desc"), true)),
                    Info("run", AppStrings.T("overviewDemos.tools.copyLinear.steps.run.title"), AppStrings.T("overviewDemos.tools.copyLinear.steps.run.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.copyLinear.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.copyLinear.runLog.2"), "pass"), (AppStrings.T("overviewDemos.tools.copyLinear.runLog.3"), "pass")),
            },

            ["Copy Datums"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.copyGrids.title"), RunLabel = AppStrings.T("overviewDemos.tools.copyGrids.runLabel"),
                Steps = new[]
                {
                    Multi("source", AppStrings.T("overviewDemos.tools.copyGrids.steps.source.title"), true, AppStrings.T("overviewDemos.tools.copyGrids.steps.source.hint"), Groups((AppStrings.T("overviewDemos.tools.copyGrids.steps.source.groups.linkedModel.label"), Documents), (AppStrings.T("overviewDemos.tools.copyGrids.steps.source.groups.grids.label"), Grids))),
                    Info("run", AppStrings.T("overviewDemos.tools.copyGrids.steps.run.title"), AppStrings.T("overviewDemos.tools.copyGrids.steps.run.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.copyGrids.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.copyGrids.runLog.2"), "pass"), (AppStrings.T("overviewDemos.tools.copyGrids.runLog.3"), "skip")),
            },

            ["Copy Elements"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.copyElements.title"), RunLabel = AppStrings.T("overviewDemos.tools.copyElements.runLabel"),
                Steps = new[]
                {
                    Multi("source", AppStrings.T("overviewDemos.tools.copyElements.steps.source.title"), true, AppStrings.T("overviewDemos.tools.copyElements.steps.source.hint"), Groups((AppStrings.T("overviewDemos.tools.copyElements.steps.source.groups.linkedModel.label"), Documents), (AppStrings.T("overviewDemos.tools.copyElements.steps.source.groups.categories.label"), L(AppStrings.T("overviewDemos.tools.copyElements.steps.source.groups.categories.items.1"), AppStrings.T("overviewDemos.tools.copyElements.steps.source.groups.categories.items.2"), AppStrings.T("overviewDemos.tools.copyElements.steps.source.groups.categories.items.3"))))),
                    MultiFlat("types", AppStrings.T("overviewDemos.tools.copyElements.steps.types.title"), true, AppStrings.T("overviewDemos.tools.copyElements.steps.types.hint"), L(AppStrings.T("overviewDemos.tools.copyElements.steps.types.items.1"), AppStrings.T("overviewDemos.tools.copyElements.steps.types.items.2"), AppStrings.T("overviewDemos.tools.copyElements.steps.types.items.3"), AppStrings.T("overviewDemos.tools.copyElements.steps.types.items.4"))),
                    Toggles("changes", AppStrings.T("overviewDemos.tools.copyElements.steps.changes.title"), AppStrings.T("overviewDemos.tools.copyElements.steps.changes.hint"),
                        Tg("changed", AppStrings.T("overviewDemos.tools.copyElements.steps.changes.toggles.changed.label"), AppStrings.T("overviewDemos.tools.copyElements.steps.changes.toggles.changed.desc"), true),
                        Tg("stamp", AppStrings.T("overviewDemos.tools.copyElements.steps.changes.toggles.stamp.label"), AppStrings.T("overviewDemos.tools.copyElements.steps.changes.toggles.stamp.desc"), true)),
                    Info("run", AppStrings.T("overviewDemos.tools.copyElements.steps.run.title"), AppStrings.T("overviewDemos.tools.copyElements.steps.run.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.copyElements.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.copyElements.runLog.2"), "pass")),
            },

            ["Align Coordinates"] = new OverviewDemoSpec
            {
                Title = AppStrings.T("overviewDemos.tools.alignCoordinates.title"), RunLabel = AppStrings.T("overviewDemos.tools.alignCoordinates.runLabel"),
                Steps = new[]
                {
                    Composite("host", AppStrings.T("overviewDemos.tools.alignCoordinates.steps.host.title"), true, AppStrings.T("overviewDemos.tools.alignCoordinates.steps.host.hint"),
                        Pre(Single("grid1", AppStrings.T("overviewDemos.tools.alignCoordinates.steps.host.parts.grid1"), true, "", Grids.ToArray()), 0),
                        Pre(Single("grid2", AppStrings.T("overviewDemos.tools.alignCoordinates.steps.host.parts.grid2"), true, "", Grids.ToArray()), 1),
                        Pre(Single("level", AppStrings.T("overviewDemos.tools.alignCoordinates.steps.host.parts.level"), true, "", Levels.ToArray()), 0),
                        Toggles("points", AppStrings.T("overviewDemos.tools.alignCoordinates.steps.host.parts.points"), "",
                            Tg("survey", AppStrings.T("overviewDemos.tools.alignCoordinates.steps.host.parts.survey"), "", true),
                            Tg("pbp", AppStrings.T("overviewDemos.tools.alignCoordinates.steps.host.parts.pbp"), "", true))),
                    MultiFlat("links", AppStrings.T("overviewDemos.tools.alignCoordinates.steps.links.title"), false, AppStrings.T("overviewDemos.tools.alignCoordinates.steps.links.hint"), Links),
                    Info("run", AppStrings.T("overviewDemos.tools.alignCoordinates.steps.run.title"), AppStrings.T("overviewDemos.tools.alignCoordinates.steps.run.info")),
                },
                RunLog = RL((AppStrings.T("overviewDemos.tools.alignCoordinates.runLog.1"), "info"), (AppStrings.T("overviewDemos.tools.alignCoordinates.runLog.2"), "pass"), (AppStrings.T("overviewDemos.tools.alignCoordinates.runLog.3"), "pass")),
            },

            // Compare Grids' demo entry was removed — the tool is ribbon-retired
            // (ShowRetiredSetupTools = false) and no longer has a catalog card.
        };

            string sampledFrom = OverviewSamples.Current?.DocumentTitle ?? "";
            foreach (var s in specs.Values) s.SampledFrom = sampledFrom;
            return specs;
        }
    }
}
