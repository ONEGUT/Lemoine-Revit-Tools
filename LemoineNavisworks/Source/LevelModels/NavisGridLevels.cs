using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.DocumentParts;
using LemoineTools.Framework;

namespace LemoineNavisworks.LevelModels
{
    // =========================================================================
    // NavisGridLevels — reads the federation's levels from GRIDS & LEVELS, which
    // is where Navisworks actually keeps them (View tab ▸ Grids panel: a grid
    // system such as "DT - Arch - Building A", and under it Level 0, LEVEL 01 …
    // ROOF OVERHANG).
    //
    // This replaces reading levels out of the selection tree. A Revit NWC does
    // not put its levels in the tree at all — walking it found the model's own
    // wrapper node ("DT - Arch.rvt : 7 : location <Not Shared>"), then 32 Revit
    // CATEGORIES below that, and no levels at any depth. The tree walk survives
    // only as a fallback for a federation carrying no grid systems whatsoever.
    //
    // Every member below is confirmed against the real Autodesk.Navisworks.Api.dll
    // by decoding its metadata tables with devtools/navis_dump.py, per CLAUDE.md's
    // Research Discipline:
    //
    //   Document.Grids            : DocumentParts.DocumentGrids   ← note the namespace
    //   DocumentGrids.Systems     : GridSystemCollection
    //   DocumentGrids.ActiveSystem: GridSystem
    //   GridSystem.DisplayName    : string
    //   GridSystem.Levels         : GridLevelCollection
    //   GridLevel.DisplayName     : string
    //   GridLevel.Elevation       : double        (document units)
    //
    // Both collections expose IEnumerator<T> GetEnumerator(), so foreach is real
    // rather than an IList cast.
    // =========================================================================
    internal static class NavisGridLevels
    {
        /// <summary>What the last read saw, so a zero result says why rather than looking like a
        /// broken collector — the same discipline the tree walk's DiscoveryReport follows.</summary>
        public sealed class GridReport
        {
            /// <summary>The document exposed a grids object at all.</summary>
            public bool HasGrids;
            /// <summary>Names of every grid system found, in the order the API returned them.</summary>
            public List<string> SystemNames = new List<string>();
            /// <summary>The system the levels were actually read from.</summary>
            public string UsedSystem = "";
            public int    LevelsRead;
            /// <summary>Why nothing came back, when nothing came back.</summary>
            public string Failure = "";

            public string Describe() =>
                $"hasGrids={HasGrids}, systems=[{string.Join(", ", SystemNames.Take(8))}], " +
                $"used='{UsedSystem}', levels={LevelsRead}" +
                (Failure.Length > 0 ? $", failure={Failure}" : "");
        }

        public static GridReport LastRead { get; private set; } = new GridReport();

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>Names of every grid system in the document, in API order. Empty means the
        /// federation carries no grids — the caller falls back to the tree walk and says so.</summary>
        public static List<string> ListSystemNames(Document doc)
        {
            var report = new GridReport();
            LastRead = report;

            var names = new List<string>();
            foreach (var sys in Systems(doc, report))
            {
                string n = SafeName(sys);
                if (n.Length > 0) names.Add(n);
            }
            report.SystemNames = names;
            if (names.Count == 0 && report.Failure.Length == 0) report.Failure = "no grid systems";

            Log(report);
            return names;
        }

        /// <summary>
        /// Levels of one grid system, in FEET, ordered bottom-up.
        ///
        /// <para>A grid level carries a NAME and an ELEVATION and nothing else — there is no top.
        /// So <see cref="DiscoveredLevel.Top"/> is left at the elevation here, and the caller's
        /// floor-to-floor pass turns each level's top into the next one's bottom. Only the topmost
        /// level has no next, and the caller resolves that from the model's own extent.</para>
        ///
        /// <paramref name="systemName"/> empty means "the first system".
        /// </summary>
        public static List<DiscoveredLevel> ReadLevels(Document doc, string systemName)
        {
            var report = new GridReport();
            LastRead = report;

            var found = new List<DiscoveredLevel>();

            var systems = Systems(doc, report).ToList();
            report.SystemNames = systems.Select(SafeName).Where(n => n.Length > 0).ToList();
            if (systems.Count == 0)
            {
                if (report.Failure.Length == 0) report.Failure = "no grid systems";
                Log(report);
                return found;
            }

            GridSystem? chosen = null;
            if (!string.IsNullOrWhiteSpace(systemName))
                chosen = systems.FirstOrDefault(
                    s => string.Equals(SafeName(s), systemName, StringComparison.OrdinalIgnoreCase));

            // A named system that is no longer in the document must NOT silently read a different
            // one — that would hand the user another building's levels. An empty pick legitimately
            // means "the first".
            if (chosen == null && string.IsNullOrWhiteSpace(systemName)) chosen = systems[0];
            if (chosen == null)
            {
                report.Failure = $"grid system '{systemName}' is not in this document";
                Log(report);
                return found;
            }

            report.UsedSystem = SafeName(chosen);
            double toFeet = NavisLevelModels.ToFeet(doc);

            try
            {
                foreach (GridLevel lvl in chosen.Levels)                     // GridSystem.Levels — confirmed
                {
                    string name;
                    double elevation;
                    try
                    {
                        name      = (lvl.DisplayName ?? "").Trim();          // GridLevel.DisplayName — confirmed
                        elevation = lvl.Elevation;                           // GridLevel.Elevation : double — confirmed
                    }
                    catch (Exception ex)
                    {
                        // One unreadable level must not cost the rest of the list, but it is a
                        // missing floor in the export — never silent.
                        DiagnosticsLog.Swallowed("LevelModels: read grid level", ex);
                        continue;
                    }
                    if (name.Length == 0) continue;

                    found.Add(new DiscoveredLevel
                    {
                        Name      = name,
                        Elevation = elevation * toFeet,
                        // A grid level has no ceiling; the caller derives it floor-to-floor.
                        Top       = elevation * toFeet,
                    });
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("LevelModels: read grid levels", ex);
                report.Failure = ex.Message;
            }

            report.LevelsRead = found.Count;
            if (found.Count == 0 && report.Failure.Length == 0)
                report.Failure = "the grid system reported no levels";

            Log(report);
            return found.OrderBy(l => l.Elevation).ToList();
        }

        // ── Internals ────────────────────────────────────────────────────────

        /// <summary>Every grid system in the document. Falls back to the ACTIVE system when the
        /// collection is empty — the user's own Grids panel had one selected, so reporting none
        /// would contradict what they are looking at.</summary>
        private static List<GridSystem> Systems(Document doc, GridReport report)
        {
            var list = new List<GridSystem>();
            if (doc == null || doc.IsClear) { report.Failure = "no document"; return list; }

            DocumentGrids grids;
            try { grids = doc.Grids; }                                        // Document.Grids — confirmed
            catch (Exception ex)
            {
                DiagnosticsLog.Error("LevelModels: read document grids", ex);
                report.Failure = "this document exposes no grids";
                return list;
            }
            report.HasGrids = true;

            try
            {
                foreach (GridSystem sys in grids.Systems)                     // DocumentGrids.Systems — confirmed
                    if (sys != null) list.Add(sys);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("LevelModels: enumerate grid systems", ex);
                report.Failure = ex.Message;
            }

            if (list.Count == 0)
            {
                try
                {
                    var active = grids.ActiveSystem;                          // DocumentGrids.ActiveSystem — confirmed
                    if (active != null) list.Add(active);
                }
                catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: read active grid system", ex); }
            }

            return list;
        }

        private static string SafeName(GridSystem? sys)
        {
            if (sys == null) return "";
            try { return (sys.DisplayName ?? "").Trim(); }                    // GridSystem.DisplayName — confirmed
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("LevelModels: grid system name", ex);
                return "";
            }
        }

        /// <summary>Always logged, zero result included: a silent empty read is indistinguishable
        /// from a broken collector, and that silence is what hid the tree-walk bug for so long.</summary>
        private static void Log(GridReport report) =>
            DiagnosticsLog.Info("LevelModels: grids & levels", report.Describe());
    }
}
