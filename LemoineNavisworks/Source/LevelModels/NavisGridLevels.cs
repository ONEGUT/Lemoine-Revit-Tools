using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.Navisworks.Api;
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
    // not put its levels in the tree at all — the tree walk only ever found the
    // model's own wrapper node ("DT - Arch.rvt : 7 : location <Not Shared>") —
    // and it survives here only as a fallback for a federation carrying no grid
    // systems whatsoever.
    //
    // ── WHY THIS FILE USES REFLECTION ────────────────────────────────────────
    // CLAUDE.md's Research Discipline forbids guessing Navisworks member names:
    // an earlier clipping-plane implementation on this branch was invented whole
    // from memory and cost a full Windows build to discover. The prescribed tool
    // is devtools/navis_dump.py against a real Autodesk.Navisworks.Api.dll — but
    // that DLL is licensed and .gitignore'd (libs-navis/*.dll), so a cloud clone
    // of this repo never has one to decode, and the public API docs are not
    // reachable from that environment either.
    //
    // Public Autodesk sources confirm the SHAPE — Document.Grids : DocumentGrids,
    // GridSystem.Levels : GridLevelCollection, GridLevel.DisplayName, and a
    // GridLevel elevation member. What they do NOT confirm is how the grid
    // SYSTEMS come off DocumentGrids (Systems? GridSystems? ActiveSystem?).
    //
    // So this reader asks the RUNTIME rather than guessing at compile time:
    // every member is resolved by name against the live object, trying the
    // plausible names in order, and the ones that actually resolved are recorded
    // in LastRead.ResolvedMembers and written to diagnostics.log. That is the
    // opposite of guessing — it cannot break the build, it works against
    // whichever names are real, and its log names them for us.
    //
    // ▸ ONCE A RUN HAS LOGGED THE RESOLVED MEMBERS, replace this file with
    //   direct typed calls. It is a bridge over an environment limitation, not
    //   an architecture. Nothing else in the tool touches reflection.
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
            /// <summary>Which member names the runtime actually had — this is the record that lets
            /// the reflection below be replaced with direct calls.</summary>
            public string ResolvedMembers = "";

            public string Describe() =>
                $"hasGrids={HasGrids}, systems=[{string.Join(", ", SystemNames.Take(8))}], " +
                $"used='{UsedSystem}', levels={LevelsRead}, members=[{ResolvedMembers}]" +
                (Failure.Length > 0 ? $", failure={Failure}" : "");
        }

        public static GridReport LastRead { get; private set; } = new GridReport();

        // Candidate member names, most likely first. Each list is tried in order against the live
        // object and the winner is recorded; an empty result means NONE of them existed, which the
        // report states rather than hiding.
        private static readonly string[] GridsOnDocument   = { "Grids", "DocumentGrids", "GridSystems" };
        private static readonly string[] SystemsOnGrids    = { "Systems", "GridSystems", "AllSystems", "ActiveSystem", "CurrentSystem", "CurrentGridSystem" };
        private static readonly string[] LevelsOnSystem    = { "Levels", "GridLevels", "AllLevels" };
        private static readonly string[] NameMembers       = { "DisplayName", "Name" };
        private static readonly string[] ElevationMembers  = { "Elevation", "Height", "Z", "Level", "Position" };

        // Not thread-safe, and does not need to be: every read runs on Navisworks' main thread,
        // marshalled there by NavisMainThread like every other document access in this tool.
        private static readonly HashSet<string> _resolved = new HashSet<string>(StringComparer.Ordinal);

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>Names of every grid system in the document, in API order. Empty means the
        /// federation carries no grids — the caller falls back to the tree walk and says so.</summary>
        public static List<string> ListSystemNames(Document doc)
        {
            var report = BeginRead();
            var names  = new List<string>();
            foreach (var sys in SystemObjects(doc, report))
            {
                string n = ReadName(sys);
                if (n.Length > 0) names.Add(n);
            }
            report.SystemNames = names;
            Finish(report);
            return names;
        }

        /// <summary>
        /// Levels of one grid system, in FEET, ordered bottom-up.
        ///
        /// <para>A grid level carries a NAME and an ELEVATION and nothing else — there is no top.
        /// So <see cref="DiscoveredLevel.Top"/> is left at the elevation here, and the caller's
        /// existing floor-to-floor pass turns each level's top into the next one's bottom. Only the
        /// topmost level has no next, and the caller resolves that from the model's own extent.</para>
        ///
        /// <paramref name="systemName"/> empty means "the first system".
        /// </summary>
        public static List<DiscoveredLevel> ReadLevels(Document doc, string systemName)
        {
            var report = BeginRead();
            var found  = new List<DiscoveredLevel>();

            double toFeet = NavisLevelModels.ToFeet(doc);

            var systems = SystemObjects(doc, report).ToList();
            report.SystemNames = systems.Select(ReadName).Where(n => n.Length > 0).ToList();

            if (systems.Count == 0)
            {
                report.Failure = "no grid systems";
                Finish(report);
                return found;
            }

            object? chosen = null;
            if (!string.IsNullOrWhiteSpace(systemName))
                chosen = systems.FirstOrDefault(
                    s => string.Equals(ReadName(s), systemName, StringComparison.OrdinalIgnoreCase));
            // A named system that is no longer in the document must not silently read a different
            // one — but an empty pick legitimately means "the first".
            if (chosen == null && string.IsNullOrWhiteSpace(systemName)) chosen = systems[0];
            if (chosen == null)
            {
                report.Failure = $"grid system '{systemName}' is not in this document";
                Finish(report);
                return found;
            }

            report.UsedSystem = ReadName(chosen);

            foreach (var lvl in Enumerate(ReadMember(chosen, LevelsOnSystem, "GridSystem.Levels")))
            {
                string name = ReadName(lvl);
                if (name.Length == 0) continue;

                double? elev = ReadElevation(lvl);
                found.Add(new DiscoveredLevel
                {
                    Name      = name,
                    Elevation = (elev ?? 0) * toFeet,
                    // No top exists on a grid level; the caller derives it floor-to-floor.
                    Top       = (elev ?? 0) * toFeet,
                });
            }

            report.LevelsRead = found.Count;
            if (found.Count == 0) report.Failure = "the grid system reported no levels";
            Finish(report);

            return found.OrderBy(l => l.Elevation).ToList();
        }

        // ── Runtime member resolution ────────────────────────────────────────

        private static GridReport BeginRead()
        {
            _resolved.Clear();
            var report = new GridReport();
            LastRead = report;
            return report;
        }

        private static void Finish(GridReport report)
        {
            report.ResolvedMembers = string.Join(", ", _resolved.OrderBy(x => x, StringComparer.Ordinal));
            // Always logged, zero result included: this line is what lets the reflection above be
            // replaced with direct typed calls, and a silent empty read is exactly the failure this
            // whole tool has been chasing.
            DiagnosticsLog.Info("LevelModels: grids & levels", report.Describe());
        }

        /// <summary>The grid systems, as live objects. Handles a collection or a single active
        /// system equally, because which of the two DocumentGrids exposes is the one thing the
        /// public docs do not say.</summary>
        private static IEnumerable<object> SystemObjects(Document doc, GridReport report)
        {
            if (doc == null || doc.IsClear) { report.Failure = "no document"; return Enumerable.Empty<object>(); }

            object? grids = ReadMember(doc, GridsOnDocument, "Document.Grids");
            if (grids == null)
            {
                report.Failure = "this Navisworks build exposes no grids object on Document";
                return Enumerable.Empty<object>();
            }
            report.HasGrids = true;

            object? systems = ReadMember(grids, SystemsOnGrids, "DocumentGrids.Systems");
            if (systems == null)
            {
                report.Failure = "the grids object exposes no grid systems";
                return Enumerable.Empty<object>();
            }
            return Enumerate(systems);
        }

        /// <summary>Reads the first of <paramref name="candidates"/> that the live object actually
        /// has, recording which one won. Property first, then a no-argument method — Navisworks
        /// uses both shapes across its API.</summary>
        private static object? ReadMember(object? target, string[] candidates, string label)
        {
            if (target == null) return null;
            Type t = target.GetType();

            foreach (string name in candidates)
            {
                try
                {
                    PropertyInfo? p = t.GetProperty(name,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    if (p != null && p.CanRead)
                    {
                        object? v = p.GetValue(target, null);
                        if (v != null) { _resolved.Add($"{label}={name}"); return v; }
                        continue;   // the member exists but is empty — a real answer, keep looking
                    }

                    MethodInfo? m = t.GetMethod(name,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                        null, Type.EmptyTypes, null);
                    if (m != null && m.ReturnType != typeof(void))
                    {
                        object? v = m.Invoke(target, null);
                        if (v != null) { _resolved.Add($"{label}={name}()"); return v; }
                    }
                }
                catch (Exception ex)
                {
                    // One candidate throwing must not stop the others being tried; the member that
                    // does work is the answer, and a total miss is reported by the caller.
                    DiagnosticsLog.Swallowed($"LevelModels: read {label}.{name}", ex);
                }
            }
            return null;
        }

        private static IEnumerable<object> Enumerate(object? value)
        {
            if (value == null) yield break;

            // A single object (an "active system") is a collection of one — treating it as empty
            // would silently lose the only system the document has.
            if (value is string || !(value is IEnumerable seq)) { yield return value; yield break; }

            IEnumerator it;
            try { it = seq.GetEnumerator(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: enumerate grid collection", ex); yield break; }

            while (true)
            {
                object? current = null;
                try { if (!it.MoveNext()) break; current = it.Current; }
                catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: step grid collection", ex); break; }
                if (current != null) yield return current;
            }
        }

        private static string ReadName(object? o)
        {
            object? v = ReadMember(o, NameMembers, "DisplayName");
            return (v as string)?.Trim() ?? "";
        }

        /// <summary>A grid level's elevation in DOCUMENT units, or null when none of the candidate
        /// members exists. A member holding a point rather than a number (a plane origin) is read
        /// through its Z.</summary>
        private static double? ReadElevation(object? level)
        {
            object? v = ReadMember(level, ElevationMembers, "GridLevel.Elevation");
            if (v == null) return null;

            if (v is double d)  return d;
            if (v is float f)   return f;
            if (v is decimal m) return (double)m;
            if (v is int i)     return i;

            // Not a number — try a Z off it (a Point3D-shaped member).
            object? z = ReadMember(v, new[] { "Z" }, "GridLevel.Elevation.Z");
            if (z is double dz) return dz;
            if (z is float fz)  return fz;

            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("LevelModels: grid level elevation is not numeric", ex);
                return null;
            }
        }
    }
}
