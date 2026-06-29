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
        private static List<string> L(params string[] s) => s.ToList();

        private static readonly List<string> PlanViews   = L("Level 1 - Mechanical", "Level 2 - Mechanical", "Level 3 - Mechanical", "Roof - Mechanical");
        private static readonly List<string> CeilingPlans= L("Level 1 - RCP", "Level 2 - RCP", "Level 3 - RCP");
        private static readonly List<string> Views3D     = L("3D - Coordination", "3D - MEP Overall", "3D - Level 2");
        private static readonly List<string> Sections    = L("Section A-A", "Elevation - North", "Section - Riser 1");
        private static readonly List<string> Sheets      = L("M-101 - Level 1 Mechanical", "M-102 - Level 2 Mechanical", "M-103 - Level 3 Mechanical");
        private static readonly List<string> Levels      = L("Level 1", "Level 2", "Level 3", "Level 4", "Roof");
        private static readonly List<string> Documents   = L("ARCH - Architecture.rvt", "STR - Structure.rvt", "MEP - Services.rvt");
        private static readonly List<string> Trades      = L("Supply Air", "Return Air", "Exhaust Air", "Chilled Water", "Heating Water", "Domestic Cold Water");
        private static readonly List<string> Filters     = L("Supply Air", "Return Air", "Chilled Water Supply", "Chilled Water Return", "Hot Water Supply");
        private static readonly List<string> Definitions = L("Duct vs Structure", "Pipe vs Duct", "Cable Tray vs Beam");
        private static readonly List<string> Templates   = L("MEP - Coordination", "MEP - Working", "Architectural - Presentation");
        private static readonly List<string> Grids       = L("Grid A", "Grid B", "Grid C", "Grid 1", "Grid 2", "Grid 3");
        private static readonly List<string> RefPlanes   = L("Centerline", "Corridor Edge", "Shaft Boundary");
        private static readonly List<string> TitleBlocks = L("A1 Metric", "A3 Landscape", "Custom 30x42");

        private static Dictionary<string, List<string>> Categories() => new Dictionary<string, List<string>>
        {
            ["Mechanical"] = L("Ducts", "Duct Fittings", "Air Terminals"),
            ["Piping"]     = L("Pipes", "Pipe Fittings", "Pipe Accessories"),
            ["Electrical"] = L("Cable Trays", "Conduits"),
            ["Structure"]  = L("Walls", "Structural Columns", "Structural Framing"),
        };

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
        private static OverviewDemoStep Num(string id, string title, string hint, double val, double min, double max, double step, string unit)
            => new OverviewDemoStep { Id = id, Title = title, Kind = "number", Hint = hint, NumValue = val, NumMin = min, NumMax = max, NumStep = step, NumUnit = unit };
        private static OverviewDemoStep Txt(string id, string title, bool req, string hint, string def, string ph)
            => new OverviewDemoStep { Id = id, Title = title, Required = req, Kind = "text", Hint = hint, TextDefault = def, TextPlaceholder = ph };
        private static OverviewDemoStep Info(string id, string title, string info)
            => new OverviewDemoStep { Id = id, Title = title, Kind = "info", Info = info };

        private static (string, string)[] RL(params (string, string)[] lines) => lines;

        // ── Lookup ────────────────────────────────────────────────────────────
        public static OverviewDemoSpec? For(string toolName)
            => _specs.TryGetValue(toolName, out var s) ? s : null;

        private static readonly Dictionary<string, OverviewDemoSpec> _specs =
            new Dictionary<string, OverviewDemoSpec>
        {
            ["Auto Filters"] = new OverviewDemoSpec
            {
                Title = "Auto Filters", RunLabel = "Create Filters →",
                Steps = new[]
                {
                    MultiFlat("S1", "Select Links", true, "Choose the linked models to scan for trades / systems.", Documents),
                    Toggles("S2", "Configure Links", "How each link's elements are read into trades.",
                        Tg("sys", "By system name", "Group by Revit system name", true),
                        Tg("fab", "By fabrication service", "Group by MEP fabrication service", false),
                        Tg("host", "Include host model", "Also scan the host document", false)),
                    Info("S3", "Scanning", "Discover scans the selected links and groups elements into candidate trades. (Simulated here.)"),
                    MultiFlat("S4", "Review Rules", true, "Tick the discovered trade rules to keep.", Trades),
                    Info("S5", "Confirm & Commit", "Creates one view filter per kept trade and applies its color override across the selected views."),
                },
                RunLog = RL(("Scanning 3 links…", "info"), ("Found 6 candidate trades", "pass"),
                            ("Created 6 view filters", "pass"), ("Applied color overrides to active view", "pass")),
            },

            ["Legend Creation"] = new OverviewDemoSpec
            {
                Title = "Legend Creation", RunLabel = "Build Legend →",
                Steps = new[]
                {
                    MultiFlat("S1", "Select Filters", true, "Pick the filters to include as legend rows.", Filters),
                    Single("S2", "Layout", true, "Legend layout.", "Single column", "Two columns", "By system group"),
                    Toggles("S3", "Options", "Legend row content.",
                        Tg("swatch", "Color swatch", "Show the filter's color", true),
                        Tg("count", "Element count", "Show how many elements match", false),
                        Tg("desc", "Description", "Show the trade description", true)),
                    Info("S4", "Review & Run", "Builds or updates a Legend view from the selected filters."),
                },
                RunLog = RL(("Building legend from 5 filters…", "info"), ("Placed 5 swatch rows", "pass"),
                            ("Legend view 'MEP Legend' updated", "pass")),
            },

            ["Ceiling Heatmap"] = new OverviewDemoSpec
            {
                Title = "Ceiling Heatmap", RunLabel = "Color Ceilings →",
                Steps = new[]
                {
                    MultiFlat("S1", "Select Ceiling Plan Views", true, "Reflected ceiling plans to color by height.", CeilingPlans),
                    Single("S_RAMP", "Color Ramp", true, "How height maps to color.", "Blue to Red (low to high)", "Viridis", "Grayscale"),
                    Toggles("S2", "Run Options", "Overrides applied with the heatmap.",
                        Tg("hidelinked", "Hide linked ceilings", "Suppress ceilings inside links", true),
                        Tg("legend", "Add legend", "Place a height legend", true),
                        Tg("halftone", "Halftone background", "Halftone everything else", false)),
                    Info("S3", "Review & Run", "Applies per-height color overrides through view filters."),
                },
                RunLog = RL(("Reading ceiling heights on 3 views…", "info"), ("8 height bands detected", "pass"),
                            ("Color overrides applied", "pass"), ("Legend placed", "pass")),
            },

            ["Make Ceiling Grids"] = new OverviewDemoSpec
            {
                Title = "Make Ceiling Grids", RunLabel = "Export DWGs →",
                Steps = new[]
                {
                    MultiFlat("docs", "Select Documents", true, "Models to build ceiling RCPs for.", Documents),
                    MultiFlat("filter", "Filter Ceiling Types", false, "Limit to these ceiling types (optional).",
                        L("Compound Ceiling 600x600", "GWB on Mtl. Stud", "ACT 1200x600")),
                    FileStep("export", "Export Location", true, "Folder for the DWG output.", "Folders|*.*", "Choose export folder…"),
                    Info("run", "Review & Run", "Creates an RCP per level showing only ceilings, then exports each as a DWG."),
                },
                RunLog = RL(("Creating RCP views per level…", "info"), ("3 RCP views created", "pass"),
                            ("Exported 3 DWG files", "pass")),
            },

            ["Project Grids"] = new OverviewDemoSpec
            {
                Title = "Project Grids", RunLabel = "Project →",
                Steps = new[]
                {
                    FileStep("S1", "DWG Source", true, "The ceiling-grid DWG to project onto soffits.", "AutoCAD DWG|*.dwg", "Browse for .dwg…"),
                    Info("S2", "Review & Run", "Projects the DWG lines onto ceiling soffit faces as model curves."),
                },
                RunLog = RL(("Importing ceiling-grid DWG…", "info"), ("Projected 124 curves onto soffits", "pass")),
            },

            ["Reproject Grids"] = new OverviewDemoSpec
            {
                Title = "Reproject Grids", RunLabel = "Reproject →",
                Steps = new[]
                {
                    MultiFlat("S1", "Select Ceiling Plans", true, "Plans whose grid curves should follow updated ceilings.", CeilingPlans),
                    Info("S2", "Review & Run", "Reprojects existing grid model curves onto current ceiling elevations."),
                },
                RunLog = RL(("Reprojecting grid curves on 3 plans…", "info"), ("Updated 124 curves to new elevations", "pass")),
            },

            ["Bulk Views by Level"] = new OverviewDemoSpec
            {
                Title = "Bulk Views by Level", RunLabel = "Create Views →",
                Steps = new[]
                {
                    MultiFlat("S1", "Source Documents", true, "Models to create per-level views from.", Documents),
                    Multi("S2", "Levels & View Types", true, "Levels and the view types to create for each.",
                        Groups(("Levels", Levels), ("View Types", L("3D", "Floor Plan", "Ceiling Plan")))),
                    Txt("S3", "View Naming", false, "Token pattern for new view names.", "{Level} - {ViewType}", "{Level} - {ViewType}"),
                    Info("S4", "Review & Run", "Creates cropped views per level and building cluster."),
                },
                RunLog = RL(("Creating views for 5 levels…", "info"), ("30 cropped views created", "pass")),
            },

            ["Bulk Duplicate"] = new OverviewDemoSpec
            {
                Title = "Bulk Duplicate", RunLabel = "Duplicate →",
                Steps = new[]
                {
                    MultiFlat("S1", "Source Views", true, "Views to duplicate.", PlanViews),
                    Single("S2", "Duplicate Mode", true, "How to duplicate.", "Duplicate", "With Detailing", "As Dependent"),
                    Txt("S3", "View Naming", true, "Token pattern for the copies.", "{ViewName} - COORD", "{ViewName} - COORD"),
                    Info("S4", "Review & Run", "Duplicates each selected view with token-based naming."),
                },
                RunLog = RL(("Duplicating 4 views (With Detailing)…", "info"), ("4 views created", "pass")),
            },

            ["Bulk Views by Template"] = new OverviewDemoSpec
            {
                Title = "Bulk Views by Template", RunLabel = "Duplicate →",
                Steps = new[]
                {
                    MultiFlat("S1", "Source Views", true, "Views to duplicate across templates.", PlanViews),
                    MultiFlat("S2", "View Templates", true, "Templates to apply, one copy per template.", Templates),
                    Txt("S3", "View Naming", true, "Token pattern.", "{ViewName} - {Template}", "{ViewName} - {Template}"),
                    Info("S4", "Review & Run", "Duplicates each view once per selected template."),
                },
                RunLog = RL(("4 views across 3 templates…", "info"), ("12 views created", "pass")),
            },

            ["Bulk Dependent Views"] = new OverviewDemoSpec
            {
                Title = "Bulk Dependent Views", RunLabel = "Replicate →",
                Steps = new[]
                {
                    Single("S1", "Source View", true, "The view whose dependents/crops are copied.", PlanViews.ToArray()),
                    Info("S2", "Dependent Preview", "The source's dependent views and crop regions are previewed here."),
                    MultiFlat("S3", "Target Views", true, "Views to copy the dependents onto.", PlanViews),
                    Txt("S4", "View Naming", false, "Token pattern for new dependents.", "{ParentView} - {Dependent}", "{ParentView} - {Dependent}"),
                    Info("S5", "Review & Run", "Copies dependents and crop regions onto each target view."),
                },
                RunLog = RL(("Source has 4 dependents…", "info"), ("Cloned onto 3 target views", "pass"), ("12 dependent views created", "pass")),
            },

            ["Explode View by Trade"] = new OverviewDemoSpec
            {
                Title = "Explode View by Trade", RunLabel = "Explode →",
                Steps = new[]
                {
                    Single("S1", "Select Source 3D View", true, "The 3D view to explode.", Views3D.ToArray()),
                    MultiFlat("S2", "Select Trades", true, "One isolated 3D view is made per trade.", Trades),
                    Toggles("S3", "Options", "How the exploded views are made.",
                        Tg("box", "Keep section box", "Reuse the source's section box", true),
                        Tg("stack", "Stack by elevation", "Offset each trade view by element elevation", true),
                        Tg("tags", "Copy tags", "Carry annotation tags across", false)),
                    Info("S4", "Review & Run", "Duplicates the 3D view once per trade, isolated by its filters."),
                },
                RunLog = RL(("Source camera + section box captured…", "info"), ("6 trade views created", "pass"), ("Stacked by elevation", "pass")),
            },

            ["Place Dependent Views"] = new OverviewDemoSpec
            {
                Title = "Place Dependent Views", RunLabel = "Place on Sheets →",
                Steps = new[]
                {
                    MultiFlat("S1", "Views to Place", true, "Parent views: one sheet each, with the dependents packed on it.", PlanViews),
                    Single("S2", "Title Block", true, "Title block for the new sheets.", TitleBlocks.ToArray()),
                    Txt("S3", "Sheet Naming", true, "Token pattern for sheet names.", "{ParentView}", "{ParentView}"),
                    Toggles("S4", "Layout", "Packing options.",
                        Tg("pack", "Pack without overlap", "Avoid viewport overlap", true),
                        Tg("trim", "Trim annotations", "Annotation-crop each dependent", true),
                        Tg("align", "Align titles", "Align the view titles", false)),
                    Info("S5", "Review & Run", "Creates one sheet per view and packs its dependents."),
                },
                RunLog = RL(("Measuring 4 parent views…", "info"), ("4 sheets created", "pass"), ("18 dependents placed without overlap", "pass")),
            },

            ["Align Sheet Views"] = new OverviewDemoSpec
            {
                Title = "Align Sheet Views", RunLabel = "Align →",
                Steps = new[]
                {
                    MultiFlat("S1", "Source Sheets", true, "Reference sheets to align to.", Sheets),
                    MultiFlat("S2", "Target Sheets", true, "Sheets whose viewports get aligned.", Sheets),
                    Toggles("S3", "Options", "What to inherit from the reference.",
                        Tg("scope", "Inherit scope box", "Match the reference scope box", true),
                        Tg("crop", "Match crop size", "Match crop region size", true),
                        Tg("vis", "Match crop visibility", "Match crop visibility", false),
                        Tg("grid", "Inherit grid extents", "Match grid bubble extents", false)),
                    Info("S4", "Review & Run", "Aligns target viewports so matching views overlay the reference."),
                },
                RunLog = RL(("Pairing views by scope box…", "info"), ("3 sheets aligned", "pass"), ("1 ambiguous pair reported", "skip")),
            },

            ["Bulk Rename"] = new OverviewDemoSpec
            {
                Title = "Bulk Rename", RunLabel = "Rename →",
                Steps = new[]
                {
                    Single("S1", "Target", true, "What to rename.", "Sheets", "Views"),
                    MultiFlat("S2", "Select Items", true, "Items to rename.", Sheets),
                    Single("S3", "Field & Operation", true, "How to rename.", "Find & Replace", "Prefix", "Suffix", "Sequential Number", "Token Pattern"),
                    Info("S4", "Review & Run", "Applies the rename, skipping and logging any duplicate-number/name clashes."),
                },
                RunLog = RL(("Renaming 3 sheets (Prefix)…", "info"), ("3 sheets renamed", "pass")),
            },

            ["Bulk Export"] = new OverviewDemoSpec
            {
                Title = "Bulk Export", RunLabel = "Export →",
                Steps = new[]
                {
                    MultiFlat("S1", "Select Sheets / Views", true, "What to export.", Sheets),
                    Toggles("S3", "Filename & Formats", "Output formats (filename pattern: {SheetNumber}-{SheetName}).",
                        Tg("pdf", "PDF", "Export PDF", true),
                        Tg("dwg", "DWG", "Export DWG", false),
                        Tg("nwc", "NWC", "Export NWC (3D only)", false),
                        Tg("ifc", "IFC", "Export IFC (3D only)", false)),
                    Toggles("S4", "PDF Settings", "PDF options.",
                        Tg("combine", "Single combined PDF", "One file for all sheets", true),
                        Tg("vector", "Vector processing", "Vector rather than raster", true),
                        Tg("hideref", "Hide ref work planes", "Suppress reference planes", true)),
                    FileStep("S8", "Output", true, "Export destination folder.", "Folders|*.*", "Choose export folder…"),
                    Info("S9", "Review & Run", "Exports each selected sheet/view with token-based filenames."),
                },
                RunLog = RL(("Exporting 3 sheets to PDF…", "info"), ("M-101 → M-101-Level 1 Mechanical.pdf", "pass"),
                            ("3 of 3 exported", "pass")),
            },

            ["Print View"] = new OverviewDemoSpec
            {
                Title = "Print View", RunLabel = "Print →",
                Steps = new[]
                {
                    Single("S1", "Formats", true, "Format for the active view/sheet.", "PDF", "DWG", "NWC (3D only)", "IFC (3D only)"),
                    Toggles("S2", "PDF Settings", "PDF options.",
                        Tg("vector", "Vector processing", "Vector rather than raster", true),
                        Tg("hideref", "Hide ref work planes", "Suppress reference planes", true)),
                    FileStep("S6", "Output", true, "Export destination folder.", "Folders|*.*", "Choose export folder…"),
                    Info("S7", "Review & Run", "Exports just the active view or sheet using the Bulk Export settings."),
                },
                RunLog = RL(("Exporting active sheet to PDF…", "info"), ("M-102-Level 2 Mechanical.pdf written", "pass")),
            },

            ["Split by Levels"] = new OverviewDemoSpec
            {
                Title = "Split by Levels", RunLabel = "Split →",
                Steps = new[]
                {
                    Multi("S1", "Select Categories", true, "Element categories to split.", Categories()),
                    MultiFlat("S2", "Select Levels", true, "Split at these level elevations.", Levels),
                    Info("S3", "Review & Run", "Splits the selected elements at each chosen level."),
                },
                RunLog = RL(("Splitting walls & MEP at 5 levels…", "info"), ("42 elements split", "pass")),
            },

            ["Split by Grid Lines"] = new OverviewDemoSpec
            {
                Title = "Split by Grid Lines", RunLabel = "Split →",
                Steps = new[]
                {
                    Multi("S1", "Select Categories", true, "Element categories to split.", Categories()),
                    MultiFlat("S2", "Select Grids", true, "Split at these grid planes.", Grids),
                    Info("S3", "Review & Run", "Splits the selected elements at each chosen grid plane."),
                },
                RunLog = RL(("Splitting at 6 grid planes…", "info"), ("31 elements split", "pass")),
            },

            ["Split by Ref Plane"] = new OverviewDemoSpec
            {
                Title = "Split by Ref Plane", RunLabel = "Split →",
                Steps = new[]
                {
                    Multi("S1", "Select Categories", true, "Element categories to split.", Categories()),
                    MultiFlat("S2", "Select Reference Planes", true, "Split at these reference planes.", RefPlanes),
                    Info("S3", "Review & Run", "Splits the selected elements at each chosen reference plane."),
                },
                RunLog = RL(("Splitting at 3 reference planes…", "info"), ("12 elements split", "pass")),
            },

            ["Split by Cell"] = new OverviewDemoSpec
            {
                Title = "Split by Cell", RunLabel = "Split →",
                Steps = new[]
                {
                    Multi("S1", "Select Categories", true, "Floors, ceilings, or filled regions to split.", Categories()),
                    Num("S2", "Cell Size", "Size of each grid cell.", 2000, 100, 10000, 100, "mm"),
                    Info("S3", "Review & Run", "Splits each selected surface into a regular grid of cells."),
                },
                RunLog = RL(("Splitting floors into 2000 mm cells…", "info"), ("1 floor → 24 cells", "pass")),
            },

            ["Extend Walls"] = new OverviewDemoSpec
            {
                Title = "Extend Walls", RunLabel = "Extend →",
                Steps = new[]
                {
                    MultiFlat("S1", "Select Base Levels", true, "Walls based on these levels are considered.", Levels),
                    Toggles("S2", "Options", "Extend behavior.",
                        Tg("aboveceil", "Only walls above ceiling", "Limit to walls that pass the ceiling", true),
                        Tg("levelup", "Snap to level above", "Set top constraint to the next level", true)),
                    Info("S3", "Review & Run", "Re-hosts qualifying walls' top constraint to the level above."),
                },
                RunLog = RL(("Checking walls on 5 levels…", "info"), ("18 walls re-constrained to Level Above", "pass")),
            },

            ["Clash Definitions"] = new OverviewDemoSpec
            {
                Title = "Clash Definitions", RunLabel = "Save Definition →",
                Steps = new[]
                {
                    Txt("name", "Definition Name", true, "Name this clash definition.", "Duct vs Structure", "Name this definition"),
                    Multi("groupA", "Group A", true, "First element group.", Categories()),
                    Multi("groupB", "Group B", true, "Second element group.", Categories()),
                    Toggles("marker", "Marker Settings", "How clashes are marked.",
                        Tg("color", "Colored marker", "Place a colored marker", true),
                        Tg("tag", "Tag clash", "Add a clash tag", true),
                        Tg("round", "Round marker", "Use a round marker", false)),
                    Info("save", "Save Definition", "Adds this definition to the reusable library used by the finders."),
                },
                RunLog = RL(("Validating groups…", "info"), ("Definition 'Duct vs Structure' saved to library", "pass")),
            },

            ["Clash Finder & Dimension"] = new OverviewDemoSpec
            {
                Title = "Clash Finder & Dimension", RunLabel = "Find & Dimension →",
                Steps = new[]
                {
                    MultiFlat("S1", "Select Definitions", true, "Saved clash definitions to run.", Definitions),
                    MultiFlat("S2", "Select Views", true, "Plan views to detect and mark in.", PlanViews),
                    Toggles("S3", "Marker Settings", "Marker behavior.",
                        Tg("color", "Colored markers", "Color by definition", true),
                        Tg("tag", "Tag each clash", "Add a tag per clash", true),
                        Tg("dense", "Survey dense areas", "Add callouts where clashes cluster", true)),
                    Toggles("S4", "Dimensioning", "Dimension targets.",
                        Tg("grid", "Dimension to grids", "Dimension to nearest grid", true),
                        Tg("slab", "Dimension to slab edges", "Dimension to nearest slab edge", true),
                        Tg("scale", "Change view scale", "Rescale views to fit", false)),
                    Info("S5", "Review & Run", "Detects clashes, marks them, and dimensions each out."),
                },
                RunLog = RL(("Running 3 definitions over 4 views…", "info"), ("Level 1 - Mechanical: 4 clashes marked", "pass"),
                            ("Level 2 - Mechanical: 2 clashes marked", "pass"), ("6 markers dimensioned to grids", "pass")),
            },

            ["Clash Finder & Elevation"] = new OverviewDemoSpec
            {
                Title = "Clash Finder & Elevation", RunLabel = "Find & Elevate →",
                Steps = new[]
                {
                    MultiFlat("S1", "Select Definitions", true, "Saved clash definitions to run.", Definitions),
                    MultiFlat("S2", "Select Views", true, "Sections / elevations to mark in.", Sections),
                    Single("S3", "Marker & Tag Settings", false, "Where the spot elevation sits on the round marker.", "Top of round", "Center", "Bottom of round"),
                    Info("S4", "Review & Run", "Detects clashes and tags each with a spot elevation."),
                },
                RunLog = RL(("Running 3 definitions over 3 views…", "info"), ("Section A-A: 5 clashes marked", "pass"),
                            ("Spot elevations tagged at top of round", "pass")),
            },

            ["Refine Dimensions"] = new OverviewDemoSpec
            {
                Title = "Refine Dimensions", RunLabel = "Re-dimension →",
                Steps = new[]
                {
                    MultiFlat("S1", "Select Views", true, "Views that already carry clash markers.", PlanViews),
                    Single("S2", "Destination", false, "Where to dimension each marker.", "Nearest grid", "Nearest slab edge", "Grid then slab edge"),
                    Info("S3", "Review & Run", "Re-dimensions the existing markers. No detection, no scale change."),
                },
                RunLog = RL(("Re-dimensioning markers on 4 views…", "info"), ("17 markers re-dimensioned to nearest grid", "pass")),
            },

            ["Copy Linear"] = new OverviewDemoSpec
            {
                Title = "Copy Linear", RunLabel = "Copy →",
                Steps = new[]
                {
                    Multi("source", "Source", true, "Linked model and the linear run categories.",
                        Groups(("Linked Model", Documents), ("Run Categories", L("Pipes", "Ducts", "Cable Trays")))),
                    MultiFlat("filters", "Parameter Filters", false, "Limit by parameter values (optional).",
                        L("System: Chilled Water", "Size: 150 mm", "Reference Level: Level 2")),
                    Single("operation", "Operation", true, "What to do with the copied runs.", "Split into standard lengths", "Replace with family at intervals"),
                    Toggles("changes", "Change Detection", "What a re-run does.",
                        Tg("changed", "Only changed elements", "Skip unchanged sources on re-run", true),
                        Tg("stamp", "Re-stamp outputs", "Refresh the provenance stamp", true)),
                    Info("run", "Review & Run", "Copies linked runs into the host and applies the chosen operation."),
                },
                RunLog = RL(("Reading linked runs…", "info"), ("Copied 28 segments into host", "pass"),
                            ("Split into 6 m standard lengths", "pass")),
            },

            ["Copy Grids"] = new OverviewDemoSpec
            {
                Title = "Copy Grids", RunLabel = "Copy Grids →",
                Steps = new[]
                {
                    Multi("source", "Source Link & Grids", true, "Linked model and the grids to copy.",
                        Groups(("Linked Model", Documents), ("Grids", Grids))),
                    Info("run", "Review & Run", "Copies the selected grids into the host; existing names are skipped."),
                },
                RunLog = RL(("Copying 6 grids…", "info"), ("4 grids copied", "pass"), ("2 grids skipped (name already exists)", "skip")),
            },

            ["Copy Elements"] = new OverviewDemoSpec
            {
                Title = "Copy Elements", RunLabel = "Copy →",
                Steps = new[]
                {
                    Multi("source", "Source Link & Categories", true, "Linked model and the categories to pull from.",
                        Groups(("Linked Model", Documents), ("Categories", L("Mechanical Equipment", "Air Terminals", "Plumbing Fixtures")))),
                    MultiFlat("types", "Families to Copy", true, "Tick the family types within those categories.",
                        L("AHU-01", "VAV-Series", "Diffuser 600x600", "FCU-Series")),
                    Toggles("changes", "Change Detection", "What a re-run does.",
                        Tg("changed", "Only changed elements", "Skip unchanged sources on re-run", true),
                        Tg("stamp", "Re-stamp outputs", "Refresh the provenance stamp", true)),
                    Info("run", "Review & Run", "Copies the ticked family types from the link into the host."),
                },
                RunLog = RL(("Reading linked elements…", "info"), ("Copied 36 elements into host", "pass")),
            },
        };
    }
}
