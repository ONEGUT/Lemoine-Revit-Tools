using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Naming;

namespace LemoineTools.Tools.ScopeBoxes
{
    /// <summary>
    /// Creates scope boxes from room clusters by DUPLICATING a user-picked seed scope box
    /// (the Revit API cannot create one from scratch), renaming and positioning each copy.
    ///
    /// Sizing: the API's ability to resize a scope box is unverified (see the Scope Box
    /// Probe harness). Until probe results confirm a writable dimension, every box whose
    /// required footprint differs from the seed's is reported with the exact required
    /// W × D × H in a warn log line so the user can drag its handles once — the position
    /// and name are already correct.
    /// </summary>
    public sealed class ScopeBoxCreatorRunHandler : IExternalEventHandler
    {
        // ── Inputs set before Raise() ─────────────────────────────────

        /// <summary>Include the host document as a room source.</summary>
        public bool            IncludeHost      { get; set; } = true;
        /// <summary>RevitLinkInstance ids whose linked documents are also searched for rooms.</summary>
        public List<ElementId> LinkInstIds      { get; set; } = new List<ElementId>();
        /// <summary>Host levels the boxes are computed for.</summary>
        public List<ElementId> SelectedLevelIds { get; set; } = new List<ElementId>();
        /// <summary>Layout mode token: "FullHeight" (one box per cluster spanning all selected
        /// levels) or "PerLevel" (one box per cluster per level).</summary>
        public string          Mode             { get; set; } = "FullHeight";
        /// <summary>The existing scope box duplicated for every created box.</summary>
        public ElementId       SeedBoxId        { get; set; } = ElementId.InvalidElementId;

        /// <summary>Naming pattern, e.g. "{BuildingLetter} - {LevelRange}".</summary>
        public string NamePattern { get; set; } = "{BuildingLetter} - {LevelRange}";

        // ── Callbacks ─────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.ScopeBoxes.ScopeBoxCreatorRunHandler";

        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument.Document;
            long issues0 = DiagnosticsLog.IssueCount;
            int pass = 0, fail = 0, skip = 0;
            try
            {
                try { RunBoxes(doc, ref pass, ref fail, ref skip); }
                catch (Exception ex)
                {
                    DiagnosticsLog.Error("ScopeBoxCreator: run aborted", ex);
                    Log(AppStrings.T("scopeBoxes.creator.log.error", ex.Message), "fail");
                    fail++;
                }
                Progress(100, pass, fail, skip);
                long issues = DiagnosticsLog.IssuesSince(issues0);
                if (issues > 0) Log(AppStrings.T("scopeBoxes.creator.log.nonFatalIssues", issues), "warn");
                Complete(pass, fail, skip);
            }
            finally
            {
                // Session-long static handler — drop the run's payload.
                LinkInstIds      = new List<ElementId>();
                SelectedLevelIds = new List<ElementId>();
            }
        }

        // ── One computed box ──────────────────────────────────────────
        private sealed class BoxSpec
        {
            public string Name = "";
            public double X0, Y0, X1, Y1, ZBot, ZTop;
            public double Width  => X1 - X0;
            public double Depth  => Y1 - Y0;
            public double Height => ZTop - ZBot;
            public XYZ Center => new XYZ((X0 + X1) / 2, (Y0 + Y1) / 2, (ZBot + ZTop) / 2);

            // ── Auto datum assignment (WS-6a) ──────────────────────────
            // The level this box's floor sits on (InvalidElementId = none). Bottom claims
            // always win a conflict over a top claim on the same level (see ApplyLevelClaims).
            public ElementId BottomLevelId = ElementId.InvalidElementId;
            // The level this box's ceiling touches (only set for the box(es) spanning the
            // highest selected level) — the level ABOVE the highest selected level, i.e. the
            // one whose datum line this box's top actually reaches. InvalidElementId when no
            // such level exists (topmost level in the model, or the TopLevelHeight fallback).
            public ElementId TopLevelId = ElementId.InvalidElementId;
            // Cluster index (0 = "Building A") — conflict priority when two boxes on the SAME
            // level both claim it the same way (e.g. two clusters' bottom box on one level).
            public int ClusterIndex;
        }

        private void RunBoxes(Document doc, ref int pass, ref int fail, ref int skip)
        {
            var s = ScopeBoxSettings.Instance;

            // ── Seed ──────────────────────────────────────────────────
            var seed = SeedBoxId != ElementId.InvalidElementId ? doc.GetElement(SeedBoxId) : null;
            var seedBb = seed?.get_BoundingBox(null);
            if (seed == null || seedBb == null)
            {
                Log(AppStrings.T("scopeBoxes.creator.log.noSeed"), "fail");
                fail++;
                return;
            }
            XYZ seedCenter = (seedBb.Min + seedBb.Max) / 2;
            double seedW = seedBb.Max.X - seedBb.Min.X;
            double seedD = seedBb.Max.Y - seedBb.Min.Y;
            double seedH = seedBb.Max.Z - seedBb.Min.Z;

            // ── Rooms ─────────────────────────────────────────────────
            var sourceDocs = new List<Document>();
            if (IncludeHost) sourceDocs.Add(doc);
            foreach (var id in LinkInstIds)
            {
                var li = doc.GetElement(id) as RevitLinkInstance;
                var ld = li?.GetLinkDocument();
                if (ld != null && !sourceDocs.Any(d => d.Equals(ld)))
                    sourceDocs.Add(ld);
            }

            var allLevels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).ToList();

            var rooms = RoomClusterSearch.CollectRooms(doc, sourceDocs);
            RoomClusterSearch.AssignHostLevelsByElevation(
                rooms, allLevels, RoomClusterSearch.LevelMatchToleranceFt);
            Log(AppStrings.T("scopeBoxes.creator.log.roomsFound", rooms.Count, sourceDocs.Count), "info");

            var selectedIdSet = new HashSet<long>(SelectedLevelIds.Select(id => id.Value));
            var selLevels     = allLevels.Where(l => selectedIdSet.Contains(l.Id.Value)).ToList();
            if (selLevels.Count == 0)
            {
                Log(AppStrings.T("scopeBoxes.creator.log.noLevels"), "fail");
                fail++;
                return;
            }

            // Z-top of a level = the level above it in the full ordered list, else + fallback.
            double TopAbove(Level lvl)
            {
                int ord = allLevels.IndexOf(lvl);
                return ord >= 0 && ord < allLevels.Count - 1
                    ? allLevels[ord + 1].Elevation
                    : lvl.Elevation + s.TopLevelHeight;
            }

            // ── Compute box specs ─────────────────────────────────────
            var specs = new List<BoxSpec>();

            // Level ABOVE the given one in the full (unfiltered) level list, or null if it's
            // the topmost level in the model. Used to find the level a box's ceiling actually
            // touches, for the auto datum-assignment in WS-6a.
            Level? LevelAbove(Level lvl)
            {
                int ord = allLevels.IndexOf(lvl);
                return ord >= 0 && ord < allLevels.Count - 1 ? allLevels[ord + 1] : null;
            }

            if (Mode == "PerLevel")
            {
                var lowestSel  = selLevels.First();
                var highestSel = selLevels.Last();

                foreach (var lvl in selLevels)
                {
                    var levelRooms = rooms.Where(r => r.LevelName == lvl.Name).ToList();
                    if (levelRooms.Count == 0) continue;
                    var clusters = RoomClusterSearch.ClusterRooms(levelRooms, s.ClusterThreshold);
                    clusters.Sort((a, b) =>
                        b.Average(r => r.CentroidY).CompareTo(a.Average(r => r.CentroidY)));

                    // Per-Level end extension (WS-6b): the bottommost and topmost selected
                    // level's box(es) push their outer face PerLevelEndExtension feet further,
                    // so they truly act as the building's overall bottom/top.
                    double zBot = lvl.Elevation;
                    double zTop = TopAbove(lvl);
                    bool isLowest  = lvl.Id.Value == lowestSel.Id.Value;
                    bool isHighest = lvl.Id.Value == highestSel.Id.Value;
                    if (isLowest)  zBot -= s.PerLevelEndExtension;
                    if (isHighest) zTop += s.PerLevelEndExtension;

                    var topLevelId = isHighest
                        ? LevelAbove(lvl)?.Id ?? ElementId.InvalidElementId
                        : ElementId.InvalidElementId;

                    for (int bi = 0; bi < clusters.Count; bi++)
                    {
                        var spec = MakeSpec(clusters[bi], clusters.Count, bi,
                                             lvl.Name, lvl.Name, zBot, zTop, s.BufferXY);
                        spec.BottomLevelId = lvl.Id;
                        spec.TopLevelId    = topLevelId;
                        spec.ClusterIndex  = bi;
                        specs.Add(spec);
                    }
                }
            }
            else // FullHeight
            {
                var selNames = new HashSet<string>(selLevels.Select(l => l.Name), StringComparer.Ordinal);
                var selRooms = rooms.Where(r => selNames.Contains(r.LevelName)).ToList();
                if (selRooms.Count > 0)
                {
                    var clusters = RoomClusterSearch.ClusterRooms(selRooms, s.ClusterThreshold);
                    clusters.Sort((a, b) =>
                        b.Average(r => r.CentroidY).CompareTo(a.Average(r => r.CentroidY)));

                    var    lowest  = selLevels.First();
                    var    highest = selLevels.Last();
                    string range   = selLevels.Count == 1
                        ? lowest.Name
                        : AppStrings.T("scopeBoxes.creator.tokens.levelRange", lowest.Name, highest.Name);
                    var topLevelId = LevelAbove(highest)?.Id ?? ElementId.InvalidElementId;

                    for (int bi = 0; bi < clusters.Count; bi++)
                    {
                        var spec = MakeSpec(clusters[bi], clusters.Count, bi,
                                             lowest.Name, range,
                                             lowest.Elevation, TopAbove(highest), s.BufferXY);
                        spec.BottomLevelId = lowest.Id;
                        spec.TopLevelId    = topLevelId;
                        spec.ClusterIndex  = bi;
                        specs.Add(spec);
                    }
                }
            }

            // A silent empty result is indistinguishable from a broken collector — say so.
            if (specs.Count == 0)
            {
                Log(AppStrings.T("scopeBoxes.creator.log.noClusters"), "warn");
                return;
            }
            Log(AppStrings.T("scopeBoxes.creator.log.planned", specs.Count), "info");

            // ── Duplicate the seed per spec ───────────────────────────
            // Scope box name uniqueness is unverified in the API — pre-check against
            // existing boxes AND earlier boxes in this batch, skip-and-log collisions.
            var takenNames = new HashSet<string>(
                ScopeBoxCreatorScanHandler.CollectScopeBoxes(doc).Select(b => b.Name),
                StringComparer.OrdinalIgnoreCase);

            // ── Level datum-assignment claims (WS-6a) ──────────────────
            // One level can carry only ONE scope box in its own DATUM_VOLUME_OF_INTEREST
            // parameter, so when two boxes both want the same level (an interior level is
            // simultaneously the "bottom" of its own per-level box and the "top" of the box
            // below it), a bottom claim always wins over a top claim; among two claims of the
            // SAME kind (e.g. two building clusters' bottom box landing on one level), the
            // lower cluster index ("Building A") wins. The loser is logged, never silently lost.
            var levelClaims = new Dictionary<long, (ElementId BoxId, string BoxName, bool IsBottom, int ClusterIndex)>();

            void ClaimLevel(ElementId levelId, ElementId boxId, string boxName, bool isBottom, int clusterIndex)
            {
                if (levelId == ElementId.InvalidElementId) return;
                long key = levelId.Value;
                string levelName = (doc.GetElement(levelId) as Level)?.Name ?? levelId.Value.ToString();

                if (!levelClaims.TryGetValue(key, out var existing))
                {
                    levelClaims[key] = (boxId, boxName, isBottom, clusterIndex);
                    return;
                }

                bool takeNew = isBottom != existing.IsBottom ? isBottom : clusterIndex < existing.ClusterIndex;
                string winnerName = takeNew ? boxName : existing.BoxName;
                string loserName  = takeNew ? existing.BoxName : boxName;
                Log(AppStrings.T("scopeBoxes.creator.log.levelClaimConflict", levelName, winnerName, loserName), "warn");
                if (takeNew) levelClaims[key] = (boxId, boxName, isBottom, clusterIndex);
            }

            using (var tx = new Transaction(doc, "Create Scope Boxes"))
            {
                var opts = tx.GetFailureHandlingOptions();
                opts.SetClearAfterRollback(true);
                opts.SetDelayedMiniWarnings(true);
                tx.SetFailureHandlingOptions(opts);
                tx.Start();

                for (int i = 0; i < specs.Count; i++)
                {
                    if (RunState.CancelRequested)
                    {
                        Log(AppStrings.T("common.log.stoppedByUser", i, specs.Count), "warn");
                        break;   // fall through to commit — work so far is preserved
                    }

                    var spec = specs[i];

                    if (takenNames.Contains(spec.Name))
                    {
                        Log(AppStrings.T("scopeBoxes.creator.log.skipExists", spec.Name), "info");
                        skip++;
                    }
                    else
                    {
                        try
                        {
                            // Bottom-align in Z: the Height parameter grows from the box's
                            // minimum Z (confirmed on Revit 2026 via the probe), so landing the
                            // seed's bottom on zBot and then setting Height gives an exact
                            // [zBot, zTop] span. XY is centred on the cluster (W/D aren't settable).
                            XYZ translation = new XYZ(
                                spec.Center.X - seedCenter.X,
                                spec.Center.Y - seedCenter.Y,
                                spec.ZBot      - seedBb.Min.Z);
                            var copyIds = ElementTransformUtils.CopyElement(doc, seed.Id, translation);
                            var copyId  = copyIds.FirstOrDefault() ?? ElementId.InvalidElementId;
                            var copy    = copyId != ElementId.InvalidElementId ? doc.GetElement(copyId) : null;
                            if (copy == null)
                            {
                                Log(AppStrings.T("scopeBoxes.creator.log.copyFailed", spec.Name), "fail");
                                fail++;
                            }
                            else
                            {
                                try { copy.Name = spec.Name; }
                                catch (Exception rex)
                                {
                                    // Rename refused (duplicate under Revit's rules, etc.) —
                                    // delete the junk copy rather than leaving an auto-named box.
                                    doc.Delete(copyId);
                                    throw new InvalidOperationException(
                                        AppStrings.T("scopeBoxes.creator.log.renameRefused", rex.Message), rex);
                                }

                                takenNames.Add(spec.Name);
                                pass++;
                                Log(AppStrings.T("scopeBoxes.creator.log.created", spec.Name), "pass");

                                ClaimLevel(spec.BottomLevelId, copyId, spec.Name, isBottom: true,  clusterIndex: spec.ClusterIndex);
                                ClaimLevel(spec.TopLevelId,    copyId, spec.Name, isBottom: false, clusterIndex: spec.ClusterIndex);

                                // Height IS settable via VOLUME_OF_INTEREST_HEIGHT (grows from the
                                // bottom, which we bottom-aligned above) — set it so Z fits exactly.
                                bool heightSet = false;
                                try
                                {
                                    var hp = copy.get_Parameter(BuiltInParameter.VOLUME_OF_INTEREST_HEIGHT);
                                    if (hp != null && !hp.IsReadOnly) { hp.Set(spec.Height); heightSet = true; }
                                }
                                catch (Exception hex)
                                { DiagnosticsLog.Swallowed($"ScopeBoxCreator: set height on '{spec.Name}'", hex); }

                                // Width/Depth are NOT exposed by the API — report the required
                                // footprint so the user drags the handles once (name, position and
                                // height are already correct).
                                const double tol = 0.5; // ft
                                if (Math.Abs(spec.Width - seedW) > tol || Math.Abs(spec.Depth - seedD) > tol)
                                {
                                    Log(AppStrings.T("scopeBoxes.creator.log.resizeNeeded",
                                        spec.Name, spec.Width.ToString("0.#"),
                                        spec.Depth.ToString("0.#")), "warn");
                                }
                                if (!heightSet && Math.Abs(spec.Height - seedH) > tol)
                                {
                                    Log(AppStrings.T("scopeBoxes.creator.log.heightManual",
                                        spec.Name, spec.Height.ToString("0.#")), "warn");
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Log(AppStrings.T("scopeBoxes.creator.log.failBox", spec.Name, e.Message), "fail");
                            fail++;
                        }
                    }

                    Progress((int)((i + 1) * 90.0 / specs.Count), pass, fail, skip);
                }

                // ── Apply the resolved level claims (WS-6a) ────────────
                int levelsAssigned = 0;
                foreach (var kv in levelClaims)
                {
                    var level = doc.GetElement(new ElementId(kv.Key)) as Level;
                    if (level == null) continue;
                    try
                    {
                        var p = level.get_Parameter(BuiltInParameter.DATUM_VOLUME_OF_INTEREST);
                        if (p == null || p.IsReadOnly) continue;
                        p.Set(kv.Value.BoxId);
                        levelsAssigned++;
                    }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Swallowed($"ScopeBoxCreator: assign level '{level.Name}' to a scope box", ex);
                        Log(AppStrings.T("scopeBoxes.creator.log.levelAssignFailed", level.Name), "warn");
                    }
                }
                if (levelsAssigned > 0)
                    Log(AppStrings.T("scopeBoxes.creator.log.levelsAssigned", levelsAssigned), "info");

                tx.Commit();
            }

            Log(AppStrings.T("scopeBoxes.creator.log.complete", pass, skip, fail), "pass");
        }

        private BoxSpec MakeSpec(
            List<RoomInfo> cluster, int clusterCount, int clusterIdx,
            string levelToken, string rangeToken,
            double zBot, double zTop, double buffer)
        {
            (double x0, double y0, double x1, double y1) =
                RoomClusterSearch.ClusterBoundsXY(cluster, buffer);

            string letter = RoomClusterSearch.BldgLetter(clusterIdx);
            string model  = cluster
                .GroupBy(r => r.DocName, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .First().Key;

            var ctx = new TokenContext();
            ctx.Computed["BuildingLetter"] = letter;
            ctx.Computed["LevelName"]      = levelToken;
            ctx.Computed["LevelRange"]     = rangeToken;
            ctx.Computed["ModelName"]      = model;

            string resolved = TokenResolver.Resolve(NamePattern, ctx, msg => Log(msg, "warn"));
            string name = TokenResolver.GuardDegenerate(resolved, ctx, $"{letter} - {rangeToken}", msg => Log(msg, "warn"));

            return new BoxSpec
            {
                Name = name,
                X0 = x0, Y0 = y0, X1 = x1, Y1 = y1,
                ZBot = zBot, ZTop = zTop,
            };
        }

        private void Log(string t, string s) => PushLog?.Invoke(t, s);
        private void Progress(int p, int pa, int f, int s) => OnProgress?.Invoke(p, pa, f, s);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
