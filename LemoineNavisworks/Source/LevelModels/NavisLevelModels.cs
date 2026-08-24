using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Navisworks.Api;
using LemoineTools.Framework;

namespace LemoineNavisworks.LevelModels
{
    // =========================================================================
    // NavisLevelModels — the Navisworks API layer for the Level Models tool.
    //
    //   • List the appended models so each level can be assigned some of them.
    //   • Hide every model NOT assigned to the level being exported, so the NWD
    //     cannot carry another level's models (ExcludeHiddenItems drops them).
    //   • Optionally trim WITHIN the assigned models by elevation band.
    //   • Optionally save a clipped viewpoint per level.
    //   • Export the NWD and restore the model's original visibility.
    //
    // WHY HIDE AND NOT CLIP. Navisworks has no cut/boolean API — scene geometry is
    // baked and read-only — and Autodesk documents that ExcludeHiddenItems does NOT
    // drop items that are merely section-clipped: they stay in the tree, whole, in
    // the written file. So hiding is the only thing that keeps geometry OUT of an
    // NWD; the clipped viewpoint below is presentation only, and is described that
    // way everywhere the user can see it.
    //
    // The trim is element-granular, not geometry-granular: a riser modelled as
    // per-storey segments distributes correctly, a one-piece full-height riser is
    // kept or dropped whole per StraddleRule. Nothing here can halve a solid.
    //
    // REQUIRES the Navisworks 2026 .NET API — NwdExportOptions / TryExportToNwd and
    // ExcludeHiddenItems do not exist before 2026.
    //
    // Every member this file calls has been confirmed against a real
    // Autodesk.Navisworks.Api.dll (2026) by decoding its metadata tables with dnfile —
    // the same technique CLAUDE.md's Research Discipline prescribes for RevitAPI.dll,
    // via devtools/navis_dump.py. This project still cannot be BUILT or RUN on Linux
    // (no Navisworks host, no dnfile-confirmable runtime behavior), so two things
    // remain genuinely unverified despite every name/signature being real, and stay
    // tagged "⚠ verify" at their call sites rather than the whole file:
    //   • whether DocumentModels.SetHidden on a model ROOT item cascades to its
    //     descendants (the whole-model hide optimization depends on it);
    //   • whether ClipPlaneSet.Mode = Box actually renders as "clip outside the box,
    //     unclipped inside" (see ApplyClip).
    // Both degrade to a logged warning rather than an unhandled throw if wrong.
    // =========================================================================
    internal static class NavisLevelModels
    {
        // Per-item geometry probes (HasGeometry / BoundingBox) can fail on an individual item
        // without the scan being wrong. Logging each failure would flood the log on a federation
        // with hundreds of thousands of items, but discarding them outright would make a
        // systematically broken probe look exactly like "this model has no geometry". So they are
        // counted here and reported ONCE by the caller; the first is also written to diagnostics
        // so there is a stack trace to work from.
        private static int  _probeFailures;
        private static bool _probeFirstLogged;

        /// <summary>Number of items whose geometry could not be probed during the last scan.</summary>
        public static int ProbeFailures => _probeFailures;

        private static void ResetProbeFailures() { _probeFailures = 0; _probeFirstLogged = false; }

        private static void NoteProbeFailure(string context, Exception ex)
        {
            _probeFailures++;
            if (_probeFirstLogged) return;
            _probeFirstLogged = true;
            DiagnosticsLog.Swallowed(context + " (first of possibly many this scan)", ex);
        }

        // A node whose Children cannot be read during the level walk gets the same treatment, but
        // on its OWN counter: _probeFailures is reported to the user as "could not be measured,
        // treated as outside every band", which a tree-walk failure is not.
        private static int  _childFailures;
        private static bool _childFirstLogged;

        /// <summary>Nodes whose children could not be read during the last level discovery.</summary>
        public static int ChildFailures => _childFailures;

        private static void ResetChildFailures() { _childFailures = 0; _childFirstLogged = false; }

        private static void NoteChildFailure(Exception ex)
        {
            _childFailures++;
            if (_childFirstLogged) return;
            _childFirstLogged = true;
            DiagnosticsLog.Swallowed("LevelModels: item children (first of possibly many this scan)", ex);
        }

        // ── Appended models ──────────────────────────────────────────────────

        /// <summary>Lists every appended model. Keys are made unique, because two models sharing
        /// a display name would otherwise collapse into one picker row meaning both.</summary>
        public static List<ModelRef> ListModels(Document doc)
        {
            var list = new List<ModelRef>();
            if (doc == null || doc.IsClear) return list;

            var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < doc.Models.Count; i++)
            {
                var mr = new ModelRef { Index = i };
                try
                {
                    Model m = doc.Models[i];
                    string file = "";
                    try { file = m.FileName ?? ""; }                                  // Model.FileName — confirmed
                    catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: model filename", ex); }

                    mr.SourceFile  = string.IsNullOrWhiteSpace(file) ? "" : Path.GetFileName(file);
                    mr.DisplayName = SafeModelName(m, mr.SourceFile, i);
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed($"LevelModels: read model {i}", ex);
                    mr.DisplayName = $"Model {i + 1}";
                }

                string key = mr.DisplayName;
                if (used.TryGetValue(key, out int n))
                {
                    used[key] = n + 1;
                    key = $"{mr.DisplayName} ({n + 1})";
                }
                else used[key] = 1;

                mr.Key = key;
                list.Add(mr);
            }
            return list;
        }

        private static string SafeModelName(Model m, string sourceFile, int index)
        {
            try
            {
                string rn = m.RootItem?.DisplayName ?? "";                             // ModelItem.DisplayName — confirmed
                if (!string.IsNullOrWhiteSpace(rn)) return rn.Trim();
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: root item name", ex); }

            if (!string.IsNullOrWhiteSpace(sourceFile))
                return Path.GetFileNameWithoutExtension(sourceFile);
            return $"Model {index + 1}";
        }

        // ── Units ────────────────────────────────────────────────────────────

        /// <summary>Multiplier from the document's own units into FEET. Bands and every Z value
        /// the tool compares are held in feet, because that is what the UI states — a model
        /// authored in millimetres must not silently make a "12.00" band mean 12 mm.</summary>
        public static double ToFeet(Document doc)
        {
            try
            {
                return UnitConversion.ScaleFactor(doc.Units, Units.Feet);   // confirmed member
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("LevelModels: unit scale factor", ex);
                return 1.0;   // treat as already-feet rather than scaling by a guess
            }
        }

        /// <summary>Highest point of the whole federation, in FEET, or null when nothing could be
        /// measured. This is what gives the TOPMOST level a band top: a grid level carries an
        /// elevation and no ceiling, so without it the top level would have a zero-height band and
        /// trim nothing.</summary>
        public static double? ModelTopZ(Document doc)
        {
            if (doc == null || doc.IsClear) return null;

            double toFeet = ToFeet(doc);
            double top    = double.MinValue;
            bool   any    = false;

            for (int i = 0; i < doc.Models.Count; i++)
            {
                try
                {
                    if (!TryZExtent(doc.Models[i].RootItem, out double _, out double hi)) continue;
                    any = true;
                    if (hi > top) top = hi;
                }
                catch (Exception ex) { DiagnosticsLog.Swallowed($"LevelModels: model {i} extent", ex); }
            }
            return any ? top * toFeet : (double?)null;
        }

        // ── Level discovery — from the source model's TREE (fallback only) ────
        //
        // Navisworks keeps a federation's levels in GRIDS & LEVELS, not in the selection tree —
        // see NavisGridLevels, which is the primary source. This tree walk survives only for a
        // federation carrying no grid systems at all, and the step says when it was used.

        /// <summary>
        /// One depth of the source model's tree, as considered by the level search. Kept so a
        /// wrong or empty result can show its working instead of leaving the user guessing.
        /// </summary>
        public sealed class LayerProbe
        {
            /// <summary>1 = the direct children of the model's root item.</summary>
            public int Depth;
            /// <summary>Distinct node names at this depth, in tree order.</summary>
            public List<string> Names = new List<string>();
            /// <summary>Nodes seen at this depth before the width cap stopped the walk.</summary>
            public int NodeCount;
            /// <summary>0..1 — how level-like this depth's names read.</summary>
            public double NameScore;
            /// <summary>0..1 — how cleanly this depth's nodes STACK in Z. Levels stack; categories
            /// (Walls, Doors, Floors…) each span the whole building and overlap completely.</summary>
            public double StackScore;
            /// <summary>Combined score; the highest one wins the level layer.</summary>
            public double Score;
            /// <summary>Set when the walk stopped here because the depth was wider than a level
            /// layer could plausibly be — i.e. it is elements, not groups.</summary>
            public bool TooWide;
        }

        /// <summary>What the last <see cref="DiscoverLevels"/> call saw, so a zero result can say
        /// why instead of leaving the user guessing.</summary>
        public sealed class DiscoveryReport
        {
            public string SourceModel = "";
            public int    GroupsSeen;
            /// <summary>Names of the nodes at the depth the levels were taken from — or, when the
            /// search found no level layer at all, of the shallowest real layer it did see. The
            /// diagnostic that makes an empty result actionable.</summary>
            public List<string> ChildNames = new List<string>();

            /// <summary>Tree depth the level list was read from. 1 = the direct children of the
            /// model's root item. A Revit NWC normally puts its levels at depth <b>2</b>, under a
            /// "&lt;file&gt;.rvt : n : location &lt;…&gt;" node — which is precisely why reading
            /// depth 1 only ever returned that one wrapper node.</summary>
            public int Depth;

            /// <summary>Node names from the model root down to the parent of the chosen layer, so
            /// the user can see WHERE in the tree the levels were read from.</summary>
            public string LayerPath = "";

            /// <summary>True when a depth actually scored as a level layer. False means nothing in
            /// this model's tree reads as levels, and no levels are returned.</summary>
            public bool Confident;

            /// <summary>Every depth considered, shallowest first.</summary>
            public List<LayerProbe> Layers = new List<LayerProbe>();

            /// <summary>Nodes whose children could not be read. Non-zero means the walk saw less of
            /// the tree than the tree actually holds, which is a different answer from "this model
            /// has no levels" and must not be reported as one.</summary>
            public int UnreadableNodes;

            /// <summary>One line per probed depth, for the diagnostics log.</summary>
            public string Describe()
            {
                if (Layers.Count == 0) return "(no tree)";
                return string.Join(" | ", Layers.Select(l =>
                    $"d{l.Depth}: {l.Names.Count} name(s) [{string.Join(", ", l.Names.Take(6))}]" +
                    $" name={l.NameScore:0.00} stack={l.StackScore:0.00} score={l.Score:0.00}" +
                    (l.TooWide ? " (too wide)" : "")));
            }
        }

        public static DiscoveryReport LastDiscovery { get; private set; } = new DiscoveryReport();

        /// <summary>Deepest layer the level search looks at. Levels sit at depth 1 or 2 in every
        /// real export; 5 is slack for an unusually wrapped federation, not an invitation to walk
        /// the tree.</summary>
        private const int MaxLevelSearchDepth = 5;

        /// <summary>A layer wider than this is elements, not levels — stop walking there.</summary>
        private const int MaxNodesPerDepth = 600;

        /// <summary>Bounding boxes are only probed for a layer this narrow. A box on a group node
        /// aggregates its whole subtree, so probing a wide layer costs real time for a result that
        /// was going to be rejected anyway.</summary>
        private const int MaxGroupsForZProbe = 150;

        /// <summary>Below this a layer is not a level layer, and discovery returns nothing rather
        /// than offering the user a list of Revit categories to delete one by one.</summary>
        private const double LevelLayerAcceptScore = 0.35;

        /// <summary>
        /// Reads the levels of ONE model out of its tree.
        ///
        /// <para>An NWC exported from Revit "divided by level" carries LEVEL 0, LEVEL 01, …
        /// ROOF BEARING as a layer of group nodes — but NOT, in general, as the direct children of
        /// the model's root. A Revit export nests them under a document/instance node first
        /// (<c>DT - Arch.rvt : 7 : location &lt;Not Shared&gt;</c>), and a federation can carry
        /// several such nodes side by side. Reading only <c>RootItem.Children</c> therefore
        /// returned that single wrapper as if it were the one and only "level".</para>
        ///
        /// <para>So the search walks DOWN a few depths and scores each one on how much it reads
        /// like a level layer — numbered names, and nodes that STACK in Z rather than all spanning
        /// the whole building the way Revit categories do — then takes the best. Nodes sharing a
        /// name at that depth are ONE level with their extents unioned, so a model split across
        /// several instance nodes does not produce duplicate rows.</para>
        ///
        /// <para>If nothing scores as levels, nothing is returned and <see cref="LastDiscovery"/>
        /// carries the tree it did see, so the step can say "this model was not exported divided
        /// by level" instead of presenting categories as levels.</para>
        ///
        /// Elevations come from each group's own bounding box and are returned in FEET.
        /// </summary>
        public static List<DiscoveredLevel> DiscoverLevels(Document doc, int modelIndex)
        {
            ResetProbeFailures();
            ResetChildFailures();
            var report = new DiscoveryReport();
            LastDiscovery = report;

            var found = new List<DiscoveredLevel>();
            if (doc == null || doc.IsClear) return found;
            if (modelIndex < 0 || modelIndex >= doc.Models.Count) return found;

            double toFeet = ToFeet(doc);

            try
            {
                Model model = doc.Models[modelIndex];
                report.SourceModel = SafeModelName(model, "", modelIndex);

                List<TreeLayer> layers = WalkLayers(model.RootItem, report);
                TreeLayer? chosen = ChooseLevelLayer(layers, report);

                if (chosen != null)
                {
                    report.Depth      = chosen.Depth;
                    report.LayerPath  = chosen.Path;
                    report.GroupsSeen = chosen.Groups.Count;
                    report.ChildNames = chosen.Groups.Select(g => g.Name).ToList();

                    if (report.Confident)
                        foreach (var g in chosen.Groups)
                            found.Add(new DiscoveredLevel
                            {
                                Name      = g.Name,
                                Elevation = g.HasExtent ? g.MinZ * toFeet : 0,
                                Top       = g.HasExtent ? g.MaxZ * toFeet : 0,
                            });
                }

                report.UnreadableNodes = _childFailures;

                // A zero result must say so — a silent empty list is indistinguishable from a
                // broken collector, and that silence is exactly what hid this bug.
                DiagnosticsLog.Info("LevelModels: level discovery",
                    $"{report.SourceModel}: {found.Count} level(s) from depth {report.Depth} " +
                    $"(confident={report.Confident}, unreadable={report.UnreadableNodes}); " +
                    $"tree: {report.Describe()}");
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("LevelModels: read level groups", ex);
            }

            return found.OrderBy(l => l.Elevation).ToList();
        }

        // ── The tree walk behind DiscoverLevels ──────────────────────────────

        /// <summary>One name at one depth, plus the union of every node carrying it. A federation
        /// can hold the same level under several instance nodes; those are ONE level.</summary>
        private sealed class TreeGroup
        {
            public string Name = "";
            public double MinZ = double.MaxValue;
            public double MaxZ = double.MinValue;
            public bool   HasExtent;
            public readonly List<ModelItem> Nodes = new List<ModelItem>();
        }

        private sealed class TreeLayer
        {
            public int    Depth;
            /// <summary>Names from the model root down to this layer's parent.</summary>
            public string Path = "";
            public readonly List<TreeGroup> Groups = new List<TreeGroup>();
            public int    NodeCount;
            public double NameScore;
            public double StackScore;
            public double Score;
            public bool   TooWide;
        }

        private static List<TreeLayer> WalkLayers(ModelItem root, DiscoveryReport report)
        {
            var layers  = new List<TreeLayer>();
            var current = new List<ModelItem> { root };
            string path = SafeDisplayName(root).Trim();

            for (int depth = 1; depth <= MaxLevelSearchDepth && current.Count > 0; depth++)
            {
                var nodes   = new List<ModelItem>();
                bool tooWide = false;

                foreach (var parent in current)
                {
                    foreach (var child in SafeChildren(parent, MaxNodesPerDepth + 1 - nodes.Count))
                    {
                        nodes.Add(child);
                        if (nodes.Count > MaxNodesPerDepth) { tooWide = true; break; }
                    }
                    if (tooWide) break;
                }

                if (nodes.Count == 0) break;

                TreeLayer layer = BuildLayer(depth, path, nodes, tooWide);
                layers.Add(layer);
                report.Layers.Add(new LayerProbe
                {
                    Depth      = layer.Depth,
                    Names      = layer.Groups.Select(g => g.Name).ToList(),
                    NodeCount  = layer.NodeCount,
                    NameScore  = layer.NameScore,
                    StackScore = layer.StackScore,
                    Score      = layer.Score,
                    TooWide    = layer.TooWide,
                });

                if (tooWide) break;

                // A node that carries geometry is an element — nothing below it can be a level.
                current = nodes.Where(n => !SafeHasGeometry(n)).ToList();

                // Only extend the printed path while the tree is a single spine; once it forks,
                // naming one branch would misrepresent where the levels came from.
                // "\u203a" / "\u2026" as escapes, not literal glyphs: this file carries no BOM, so
                // ASCII-only source is the safe form for anything that reaches the UI.
                path = layer.Groups.Count == 1
                    ? (path.Length == 0 ? layer.Groups[0].Name : path + " \u203a " + layer.Groups[0].Name)
                    : path + " \u203a \u2026";
            }

            return layers;
        }

        private static TreeLayer BuildLayer(int depth, string parentPath, List<ModelItem> nodes, bool tooWide)
        {
            var layer = new TreeLayer { Depth = depth, Path = parentPath, NodeCount = nodes.Count, TooWide = tooWide };

            var byName = new Dictionary<string, TreeGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in nodes)
            {
                string name = SafeDisplayName(n).Trim();
                if (name.Length == 0) continue;
                if (!byName.TryGetValue(name, out var g))
                {
                    g = new TreeGroup { Name = name };
                    byName[name] = g;
                    layer.Groups.Add(g);
                }
                g.Nodes.Add(n);
            }

            if (layer.Groups.Count == 0) return layer;

            if (!tooWide && layer.Groups.Count <= MaxGroupsForZProbe)
                foreach (var g in layer.Groups)
                    foreach (var n in g.Nodes)
                        if (TryZExtent(n, out double lo, out double hi))
                        {
                            g.HasExtent = true;
                            if (lo < g.MinZ) g.MinZ = lo;
                            if (hi > g.MaxZ) g.MaxZ = hi;
                        }

            ScoreLayer(layer);
            return layer;
        }

        /// <summary>How much a layer reads like the model's levels, in 0..1.
        ///
        /// <para>Two independent signals, half each, because either one alone is wrong somewhere:
        /// names catch "LEVEL 02" but not "GROUND"/"PODIUM", and Z-stacking catches an unnamed
        /// level scheme but would also accept two nodes that merely happen not to overlap. A file
        /// or instance node (".rvt", ".nwc") counts AGAINST the layer — that is the wrapper the
        /// search exists to walk past.</para></summary>
        private static void ScoreLayer(TreeLayer layer)
        {
            int n = layer.Groups.Count;
            if (n == 0) { layer.Score = 0; return; }

            layer.NameScore = layer.Groups.Count(g => LooksLikeLevelName(g.Name)) / (double)n;

            // Levels stack: laid end to end they fill the building's height about once. Revit
            // categories each span the WHOLE height, so n of them sum to about n times the span.
            var withZ = layer.Groups.Where(g => g.HasExtent && g.MaxZ > g.MinZ).ToList();
            if (withZ.Count >= 2)
            {
                double span = withZ.Max(g => g.MaxZ) - withZ.Min(g => g.MinZ);
                double sum  = withZ.Sum(g => g.MaxZ - g.MinZ);
                if (span > 0 && sum > 0) layer.StackScore = Math.Min(1.0, span / sum);
            }

            double fileNodes = layer.Groups.Count(g => LooksLikeFileNode(g.Name)) / (double)n;
            layer.Score = Math.Max(0.0, 0.5 * layer.NameScore + 0.5 * layer.StackScore - 0.5 * fileNodes);
        }

        /// <summary>The best layer, and whether it is good enough to believe.
        ///
        /// <para>Ties go to the SHALLOWEST layer: levels sit near the top of the tree, and a deeper
        /// layer scoring the same is that level's own category breakdown. A layer holding a single
        /// node can still win, but only on its NAME — that is what separates a one-level model
        /// ("LEVEL 00") from the wrapper node the search exists to walk past
        /// ("DT - Arch.rvt : 7 : location &lt;Not Shared&gt;", which scores zero).</para></summary>
        private static TreeLayer? ChooseLevelLayer(List<TreeLayer> layers, DiscoveryReport report)
        {
            TreeLayer? best = null;
            foreach (var l in layers)
                if (l.Groups.Count > 0 && (best == null || l.Score > best.Score + 1e-9))
                    best = l;

            if (best == null)
            {
                report.Confident = false;
                return layers.FirstOrDefault();     // nothing readable — report what was there
            }

            report.Confident = best.Score >= LevelLayerAcceptScore;
            return best;
        }

        /// <summary>Whether a node name reads as a level.
        ///
        /// <para>Deliberately NUMBER-led. A level layer is identified by the numbered rows in it;
        /// the un-numbered ones (ROOF, PODIUM, ROOF BEARING) ride along with the layer that wins
        /// and are never used to identify it — because "Roofs", "Floors" and "Ceilings" are Revit
        /// CATEGORY names, and matching on those words would score a category layer as levels.</para></summary>
        private static bool LooksLikeLevelName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return LevelNameRx.IsMatch(name.Trim());
        }

        // "LEVEL 02" / "LVL2" / "FLOOR 3" / "STOREY 04" anywhere in the name; "L02" / "B1" at the
        // start; or a name that simply STARTS with a number ("00 - GROUND", "01 PODIUM").
        private static readonly Regex LevelNameRx = new Regex(
            @"(?:^|[\s\-_.])(?:LEVEL|LVL|FLOOR|FLR|STOREY|STORY|BASEMENT|BSMT)[\s\-_.]*\d{1,3}(?![0-9])"
            + @"|^(?:L|B)[\s\-_.]*\d{1,3}(?![0-9])"
            + @"|^\d{1,3}(?![0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>A file or link-instance node — the wrapper the level search walks past.</summary>
        private static bool LooksLikeFileNode(string name)
        {
            string s = (name ?? "").ToUpperInvariant();
            return s.Contains(".RVT") || s.Contains(".NWC") || s.Contains(".NWD") || s.Contains(".NWF")
                || s.Contains(".DWG") || s.Contains(".DGN") || s.Contains(".IFC") || s.Contains(".SKP");
        }

        /// <summary>Children of one node, capped, never throwing. <c>cap</c> keeps a node with tens
        /// of thousands of element children from being materialised just to be rejected.</summary>
        private static List<ModelItem> SafeChildren(ModelItem item, int cap)
        {
            var kids = new List<ModelItem>();
            if (item == null || cap <= 0) return kids;
            try
            {
                foreach (ModelItem k in item.Children)                                  // ModelItem.Children — confirmed
                {
                    kids.Add(k);
                    if (kids.Count >= cap) break;
                }
            }
            catch (Exception ex) { NoteChildFailure(ex); }
            return kids;
        }

        private static string SafeDisplayName(ModelItem item)
        {
            try { return item.DisplayName ?? ""; }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: item display name", ex); return ""; }
        }

        // ── Auto-assign models to levels by file name ────────────────────────

        /// <summary>
        /// Matches each model to the levels its FILE NAME mentions. "DT-Arch-L02.nwc" lands on
        /// "LEVEL 02".
        ///
        /// <para>Matching is done on a squashed form of both strings (letters and digits only,
        /// upper-cased) so "LEVEL 02" / "Level02" / "L02" / "L2" all reconcile. The longest level
        /// token that matches wins, so "LEVEL 01" is never mistaken for "LEVEL 0" when both
        /// exist — the single most likely way a name match goes quietly wrong.</para>
        ///
        /// <para>Returns HOW MANY models it placed. Zero is the normal answer for a federation split
        /// by DISCIPLINE (DT - Arch, DT - Struct, DT - Mech) rather than by level — no file name
        /// mentions a level, and there is nothing here to guess from. The caller decides what to do
        /// about that; this method never invents an assignment.</para>
        /// </summary>
        public static int AutoAssign(IReadOnlyList<ModelRef> models, IReadOnlyList<LevelDef> levels)
        {
            if (models == null || levels == null || models.Count == 0 || levels.Count == 0)
                return 0;

            // Longest first: "LEVEL01" must be tested before "LEVEL0".
            var tokens = levels
                .Select(lv => new { Level = lv, Keys = LevelTokens(lv.Name) })
                .Where(x => x.Keys.Count > 0)
                .ToList();

            int matched = 0;
            foreach (var m in models)
            {
                string hay = Squash(Path.GetFileNameWithoutExtension(m.SourceFile ?? "") + " " + m.DisplayName);
                if (hay.Length == 0) continue;

                var best = tokens
                    .Select(x => new { x.Level, Hit = x.Keys.Where(k => hay.Contains(k)).OrderByDescending(k => k.Length).FirstOrDefault() })
                    .Where(x => x.Hit != null)
                    .OrderByDescending(x => x.Hit!.Length)
                    .FirstOrDefault();

                if (best == null) continue;
                matched++;
                if (!best.Level.Models.Contains(m.Key)) best.Level.Models.Add(m.Key);
            }

            return matched;
        }

        /// <summary>The forms a level name might take inside a file name, longest first.</summary>
        private static List<string> LevelTokens(string levelName)
        {
            var keys = new List<string>();
            string squashed = Squash(levelName);
            if (squashed.Length == 0) return keys;

            keys.Add(squashed);                                   // LEVEL02

            // A trailing number gives the short forms people actually use in file names.
            var m = System.Text.RegularExpressions.Regex.Match(squashed, @"^(?:LEVEL|LVL|FLOOR|STOREY|STORY|L|B)(\d{1,3})$",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string digits = m.Groups[1].Value;               // "02"
                string bare   = digits.TrimStart('0');
                if (bare.Length == 0) bare = "0";
                keys.Add("LEVEL" + digits);
                keys.Add("L" + digits);                          // L02
                if (bare != digits) { keys.Add("LEVEL" + bare); keys.Add("L" + bare); }   // L2
            }
            return keys.Distinct().OrderByDescending(k => k.Length).ToList();
        }

        /// <summary>Letters and digits only, upper-cased — so spaces, dashes and underscores in
        /// either the level name or the file name stop mattering.</summary>
        private static string Squash(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
            return sb.ToString();
        }

        // ── Geometry extents (only gathered when some level trims) ────────────

        /// <summary>Vertical extents of every geometry item, in FEET so they compare directly
        /// against the levels' bands.</summary>
        public static List<ItemZ> GatherItemZ(Document doc)
        {
            ResetProbeFailures();
            var list = new List<ItemZ>();
            if (doc == null || doc.IsClear) return list;

            double toFeet = ToFeet(doc);
            for (int i = 0; i < doc.Models.Count; i++)
                foreach (ModelItem item in Descendants(doc.Models[i]))
                {
                    if (!SafeHasGeometry(item)) continue;
                    if (!TryZExtent(item, out double lo, out double hi)) continue;
                    list.Add(new ItemZ
                    {
                        Item = item, ModelIndex = i,
                        MinZ = lo * toFeet, MaxZ = hi * toFeet,
                    });
                }
            return list;
        }

        private static IEnumerable<ModelItem> Descendants(Model model)
        {
            IEnumerable<ModelItem> seq;
            try { seq = model.RootItem.DescendantsAndSelf; }                            // ModelItem.DescendantsAndSelf : IEnumerable<ModelItem> — confirmed
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("LevelModels: descend model", ex);
                yield break;
            }
            foreach (var it in seq) yield return it;
        }

        private static bool SafeHasGeometry(ModelItem item)
        {
            try { return item.HasGeometry; }                                            // ModelItem.HasGeometry — confirmed
            catch (Exception ex) { NoteProbeFailure("LevelModels: HasGeometry", ex); return false; }
        }

        private static bool TryZExtent(ModelItem item, out double minZ, out double maxZ)
        {
            minZ = 0; maxZ = 0;
            try
            {
                BoundingBox3D bb = item.BoundingBox();                                  // ModelItem.BoundingBox() — confirmed
                if (bb == null) return false;
                minZ = bb.Min.Z; maxZ = bb.Max.Z;
                return true;
            }
            catch (Exception ex) { NoteProbeFailure("LevelModels: bounding box", ex); return false; }
        }

        // ── Visibility ───────────────────────────────────────────────────────

        /// <summary>Root items of every appended model — the cheap handle for whole-model hiding.</summary>
        public static List<ModelItem> RootItems(Document doc)
        {
            var roots = new List<ModelItem>();
            if (doc == null || doc.IsClear) return roots;
            for (int i = 0; i < doc.Models.Count; i++)
            {
                try { roots.Add(doc.Models[i].RootItem); }
                catch (Exception ex) { DiagnosticsLog.Swallowed($"LevelModels: root item {i}", ex); }
            }
            return roots;
        }

        public static void SetHidden(Document doc, IReadOnlyCollection<ModelItem> items, bool hidden)
        {
            if (doc == null || items == null || items.Count == 0) return;
            try
            {
                // DocumentModels.SetHidden(IEnumerable<ModelItem>, bool) — confirmed via
                // libs-navis dnfile decode, no ModelItemCollection wrapper needed. ⚠ verify:
                // whether hiding a model's ROOT item (the whole-model hide optimization this file
                // relies on) cascades to its descendants is scene-graph BEHAVIOR, not something a
                // metadata decode can confirm — needs a real Navisworks run.
                doc.Models.SetHidden(items, hidden);
            }
            catch (Exception ex) { DiagnosticsLog.Error("LevelModels: SetHidden", ex); throw; }
        }

        /// <summary>Which of <paramref name="items"/> are hidden right now — the snapshot restored
        /// after the run so the live model is left exactly as the user had it.</summary>
        public static List<ModelItem> CurrentlyHidden(IEnumerable<ModelItem> items)
        {
            var hidden = new List<ModelItem>();
            foreach (var it in items.OrEmpty())
            {
                try { if (it.IsHidden) hidden.Add(it); }                                 // ModelItem.IsHidden — confirmed
                catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: IsHidden", ex); }
            }
            return hidden;
        }

        /// <summary>Items to hide for one level: everything in a model the level does not own,
        /// plus — when the level trims — items in an owned model that fall outside its band.</summary>
        // ownedModelIndices is a HashSet, not IReadOnlyCollection, on purpose: through the
        // interface `Contains` would bind to the LINQ extension and run a LINEAR scan for every
        // one of potentially hundreds of thousands of items. The concrete type binds to the O(1)
        // member instead.
        public static List<ModelItem> HideSetFor(
            LevelDef level,
            HashSet<int> ownedModelIndices,
            IReadOnlyList<ModelItem> allRoots,
            IReadOnlyList<ItemZ> items,
            StraddleRule rule)
        {
            var hide = new List<ModelItem>();

            // 1. Whole models this level does not own — one entry per model, not per element.
            for (int i = 0; i < allRoots.Count; i++)
                if (!ownedModelIndices.Contains(i)) hide.Add(allRoots[i]);

            // 2. Within the owned models, elements outside the band. Trim is always on; a level
            //    with no real band (top not above bottom) simply has nothing to trim against.
            if (level.HasBand && items != null)
            {
                foreach (var z in items)
                {
                    if (!ownedModelIndices.Contains(z.ModelIndex)) continue;   // already hidden wholesale
                    bool keep = rule == StraddleRule.ByCentroid
                        ? (z.CentreZ >= level.Bottom && z.CentreZ < level.Top)
                        : !(z.MaxZ < level.Bottom || z.MinZ > level.Top);      // keep anything overlapping
                    if (!keep) hide.Add(z.Item);
                }
            }
            return hide;
        }

        // ── Clipped viewpoint ────────────────────────────────────────────────

        /// <summary>Saves a viewpoint named after the level, optionally carrying clipping planes at
        /// its band. PRESENTATION ONLY — clipped geometry still ships inside the NWD; only the
        /// hide/trim above keeps anything out of the file.
        ///
        /// A saved viewpoint records the hide state only when Options ▸ Interface ▸ Viewpoint
        /// Defaults ▸ "Save Hide/Required Attributes" is enabled (off by default). The export does
        /// not depend on that — it reads live visibility — so a failure here is logged and the run
        /// continues.</summary>
        /// <summary>What <see cref="SaveViewpoint"/> actually did. Saved and Clipped are SEPARATE
        /// answers: the run log used to infer "clipped" from "a viewpoint was saved", which reports
        /// a clip that never happened — the exact shape of failure this tool exists to avoid.</summary>
        public struct ViewpointResult
        {
            public bool Saved;
            public bool Clipped;
        }

        public static ViewpointResult SaveViewpoint(Document doc, string name, LevelDef level, bool clip,
                                                    Action<string, string> log)
        {
            var result = new ViewpointResult();
            try
            {
                // Two writes, on purpose. ApplyClip clips the LIVE viewpoint and copies it back
                // through doc.CurrentViewpoint; ClipViewpoint then writes the same clip straight
                // onto the copy that is about to be saved. Whether ClipPlaneSet is a live handle on
                // its Viewpoint or a detached copy could NOT be confirmed offline (no Navisworks
                // DLL to decode here), and those two possibilities need opposite code — so do both
                // and then VERIFY, rather than pick one and hope.
                if (clip && level.HasBand) ApplyClip(doc, level.Bottom, level.Top);

                Viewpoint vp = doc.CurrentViewpoint.ToViewpoint();                       // confirmed
                AimAtBand(vp, doc, level, log);

                if (clip) result.Clipped = ClipViewpoint(vp, doc, level, log);

                var sv = new SavedViewpoint(vp) { DisplayName = name };                  // SavedViewpoint(Viewpoint) + inherited SavedItem.DisplayName — confirmed
                doc.SavedViewpoints.AddCopy(sv);                                         // AddCopy(SavedItem) — confirmed (SavedViewpoint : SavedItem)
                result.Saved = true;
                return result;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("LevelModels: save viewpoint", ex);
                log?.Invoke(AppStrings.T("navis.levelModels.log.viewpointFailed", name, ex.Message), "warn");
                return result;
            }
        }

        /// <summary>Clips ONE viewpoint to a level's band and says whether the clip actually took.
        ///
        /// <para>Order matters: <c>Mode</c> goes to Box BEFORE <c>Box</c> is written, because a box
        /// assigned while the set is still in Planes mode is the likeliest way this quietly does
        /// nothing. The values are then READ BACK — if <c>ClipPlanes</c> hands out a detached copy,
        /// every write above is a silent no-op, and an uncut viewpoint with nothing in the log is
        /// precisely the failure that was reported. ⚠ verify on a Windows/Navisworks run.</para></summary>
        private static bool ClipViewpoint(Viewpoint vp, Document doc, LevelDef level, Action<string, string> log)
        {
            if (!level.HasBand)
            {
                // Requested but impossible — say so rather than saving an uncut viewpoint in silence.
                log?.Invoke(AppStrings.T("navis.levelModels.log.clipNoBand"), "warn");
                return false;
            }

            try
            {
                if (!TryBandInDocUnits(doc, level.Bottom, level.Top, out double lo, out double hi))
                {
                    log?.Invoke(AppStrings.T("navis.levelModels.log.clipBadBand"), "warn");
                    return false;
                }

                ClipPlaneSet planes = vp.ClipPlanes;
                planes.Mode    = ClipPlaneSetMode.Box;
                planes.Box     = BandBox(lo, hi);
                planes.Enabled = true;

                ClipPlaneSet check = vp.ClipPlanes;
                if (!check.Enabled || check.Mode != ClipPlaneSetMode.Box)
                {
                    DiagnosticsLog.Warn("LevelModels: clip did not take",
                        $"after write: enabled={check.Enabled}, mode={check.Mode}");
                    log?.Invoke(AppStrings.T("navis.levelModels.log.clipNotTaken"), "warn");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("LevelModels: clip saved viewpoint", ex);
                log?.Invoke(AppStrings.T("navis.levelModels.log.clipFailed", ex.Message), "warn");
                return false;
            }
        }

        /// <summary>Lifts the camera to the MIDDLE of the level's band so the saved viewpoint opens
        /// looking at that level rather than wherever the user happened to be standing. Only the
        /// height is changed — the direction the user is facing is left alone, since that is a
        /// preference and the band is the thing this tool actually knows about.</summary>
        private static void AimAtBand(Viewpoint vp, Document doc, LevelDef level, Action<string, string> log)
        {
            try
            {
                if (!level.HasBand) return;
                double toFeet = ToFeet(doc);
                double scale  = Math.Abs(toFeet) > 1e-9 ? 1.0 / toFeet : 1.0;
                double midZ   = ((level.Bottom + level.Top) * 0.5) * scale;   // band is in feet

                Point3D p = vp.Position;                                                  // confirmed get/set
                vp.Position = new Point3D(p.X, p.Y, midZ);
            }
            catch (Exception ex)
            {
                // Previously swallowed. If the Viewpoint copy turns out to be read-only, this is
                // the FIRST thing that fails and the clip is the second — a silent aim failure hides
                // the cause of both.
                DiagnosticsLog.Error("LevelModels: aim viewpoint at band", ex);
                log?.Invoke(AppStrings.T("navis.levelModels.log.aimFailed", ex.Message), "warn");
            }
        }

        // ── Viewpoint isolation for the export ───────────────────────────────

        /// <summary>
        /// Takes every saved viewpoint out of the document and hands them back for restoring, so
        /// the NWD written next contains ONLY the viewpoint this run creates. Returns null when
        /// there was nothing to remove (or the read failed), which the caller treats as "nothing
        /// to restore".
        ///
        /// <para>This mutates the LIVE document. It is only ever safe because the caller restores
        /// in a finally — and even a hard failure costs the session's viewpoints, not the file's,
        /// since nothing here saves the document.</para>
        /// </summary>
        public static IList<SavedItem>? TakeViewpoints(Document doc)
        {
            try
            {
                var snapshot = doc.SavedViewpoints.CreateCopy();                         // confirmed
                if (snapshot == null || snapshot.Count == 0) return null;
                doc.SavedViewpoints.Clear();                                             // confirmed
                return snapshot;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("LevelModels: take saved viewpoints", ex);
                return null;
            }
        }

        /// <summary>Puts the document's own viewpoints back, dropping whatever the run added.</summary>
        public static void RestoreViewpoints(Document doc, IList<SavedItem>? snapshot)
        {
            try
            {
                doc.SavedViewpoints.Clear();
                if (snapshot != null && snapshot.Count > 0)
                    doc.SavedViewpoints.CopyFrom(snapshot);                              // CopyFrom(IEnumerable<SavedItem>) — confirmed
            }
            catch (Exception ex) { DiagnosticsLog.Error("LevelModels: restore saved viewpoints", ex); }
        }

        /// <summary>Removes just the viewpoints added since the document was emptied, so the next
        /// level does not inherit the previous level's viewpoint.</summary>
        public static void ClearViewpoints(Document doc)
        {
            try { doc.SavedViewpoints.Clear(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: clear saved viewpoints", ex); }
        }

        // Clipping planes — real API confirmed by decoding libs-navis\Autodesk.Navisworks.Api.dll
        // with the same dnfile metadata-table approach CLAUDE.md prescribes for RevitAPI.dll (see
        // Research Discipline). An earlier draft guessed Viewpoint.GetClippingPlanes() /
        // SetClippingPlanes() and a ClippingPlaneAlignment/ClippingPlaneState enum pair; none of
        // that exists. The real surface is a single mutable object:
        //
        //   Viewpoint.ClipPlanes : ClipPlaneSet
        //     .Mode          : ClipPlaneSetMode   (Planes | Box)         — get/set, confirmed
        //     .Box           : BoundingBox3D      (the active volume when Mode == Box) — get/set, confirmed
        //     .BoxTransform  : Transform3D        (frame the Box is expressed in)      — GET-ONLY, confirmed
        //     .Enabled       : bool                                     — get/set, confirmed
        //
        // A first pass also tried to assign BoxTransform to force identity (no rotation/offset).
        // That does not compile — CS0200, "cannot be assigned to, it is read only" — confirmed by
        // decoding ClipPlaneSet's methods: only get_BoxTransform/GetBoxTransform exist, no setter
        // anywhere. So BoxTransform is left alone entirely; whatever frame a fresh ClipPlaneSet
        // reports is out of this code's control, and Box's coordinates are supplied assuming that
        // frame is identity (world space). Box mode is used with a box tight on Z (the level's
        // band) and enormous on X/Y, so the visible effect is a horizontal band cut without
        // needing the model's real X/Y extent. This compiles against confirmed members throughout;
        // what is NOT independently confirmed (no live Navisworks to render it) is that Box mode
        // reads exactly as "clip only outside the box" AND that BoxTransform is in fact identity —
        // treat the visual result as ⚠ verify on a real Windows run even though every call in it
        // is real. If the clip renders skewed/rotated, that read-only transform is the first thing
        // to inspect (log its Translation/Linear via CurrentPlane or a diagnostic dump).
        //
        // Bottom/Top being in the wrong order would silently produce an inverted or empty box —
        // guard it rather than pass whatever the caller has.
        private const double ClipPlaneHorizontalExtent = 1_000_000; // matches the level-band stepper's own ±range

        /// <summary>A level's band converted from FEET into the document's own units. False when the
        /// band has no height, which would otherwise produce an inverted or empty clip box.</summary>
        private static bool TryBandInDocUnits(Document doc, double bottomFt, double topFt,
                                              out double lo, out double hi)
        {
            lo = 0; hi = 0;
            if (topFt <= bottomFt) return false;

            double toFeet = ToFeet(doc);
            double scale  = Math.Abs(toFeet) > 1e-9 ? 1.0 / toFeet : 1.0;
            lo = bottomFt * scale;
            hi = topFt    * scale;
            return hi > lo;
        }

        /// <summary>A clip box tight on Z and enormous on X/Y — a horizontal band cut that needs no
        /// knowledge of the model's real plan extent.</summary>
        private static BoundingBox3D BandBox(double lo, double hi) => new BoundingBox3D(
            new Point3D(-ClipPlaneHorizontalExtent, -ClipPlaneHorizontalExtent, lo),
            new Point3D( ClipPlaneHorizontalExtent,  ClipPlaneHorizontalExtent, hi));

        /// <summary>Clips the LIVE viewpoint, so the copy taken from it a moment later already
        /// carries the clip. Deliberately reports nothing to the run log: <see cref="ClipViewpoint"/>
        /// is the authoritative attempt and owns the user-facing message, and both logging would
        /// print every clip problem twice for one level.</summary>
        private static void ApplyClip(Document doc, double bottom, double top)
        {
            try
            {
                if (!TryBandInDocUnits(doc, bottom, top, out double lo, out double hi)) return;

                Viewpoint vp = doc.CurrentViewpoint.ToViewpoint();
                ClipPlaneSet clip = vp.ClipPlanes;
                // Mode FIRST: a Box written while the set is still in Planes mode is the likeliest
                // way this silently does nothing (or throws). Order was the other way round when the
                // clip was reported as not taking.
                clip.Mode    = ClipPlaneSetMode.Box;
                clip.Box     = BandBox(lo, hi);
                clip.Enabled = true;
                doc.CurrentViewpoint.CopyFrom(vp);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("LevelModels: apply clip planes to the live viewpoint", ex);
            }
        }

        /// <summary>Disables the clip so the run leaves the live view unclipped afterwards.</summary>
        public static void ClearClip(Document doc)
        {
            try
            {
                Viewpoint vp = doc.CurrentViewpoint.ToViewpoint();
                vp.ClipPlanes.Enabled = false;
                doc.CurrentViewpoint.CopyFrom(vp);
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: clear clip planes", ex); }
        }

        // ── Export ───────────────────────────────────────────────────────────

        /// <summary>Writes the currently-visible model to an NWD, physically dropping hidden
        /// geometry. Returns the failure reason, or "" on success.</summary>
        public static string ExportNwd(Document doc, string path, bool embedXrefs, bool keepProps)
        {
            try
            {
                var opts = new NwdExportOptions                                          // confirmed — 2026-only API per Autodesk's own docs, not present before
                {
                    ExcludeHiddenItems          = true,
                    EmbedXrefs                  = embedXrefs,
                    PreventObjectPropertyExport = !keepProps,
                    // Stamp the version explicitly rather than leaving it at the default, so the
                    // NWD always opens as the same Navisworks version it was created from.
                    FileVersion                 = (int)DocumentFileVersion.Navisworks2026,
                };
                bool ok = doc.TryExportToNwd(path, opts);                                // confirmed — 2026-only API per Autodesk's own docs, not present before
                return ok ? "" : AppStrings.T("navis.levelModels.log.exportRefused");
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("LevelModels: export nwd", ex);
                return ex.Message;
            }
        }

    }
}
