using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

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

        // Naming slots (token values are logic identifiers, same contract as Bulk Views):
        // "Building Letter" | "Level" | "Level Range" | "Model Name" | "Custom" | "None".
        public string NamingFront        { get; set; } = "Building Letter";
        public string NamingFrontCustom  { get; set; } = "";
        public string NamingCenter       { get; set; } = "Level Range";
        public string NamingCenterCustom { get; set; } = "";
        public string NamingEnd          { get; set; } = "None";
        public string NamingEndCustom    { get; set; } = "";

        // ── Callbacks ─────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.ScopeBoxes.ScopeBoxCreatorRunHandler";

        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument.Document;
            long issues0 = LemoineLog.IssueCount;
            int pass = 0, fail = 0, skip = 0;
            try
            {
                try { RunBoxes(doc, ref pass, ref fail, ref skip); }
                catch (Exception ex)
                {
                    LemoineLog.Error("ScopeBoxCreator: run aborted", ex);
                    Log(LemoineStrings.T("scopeBoxes.creator.log.error", ex.Message), "fail");
                    fail++;
                }
                Progress(100, pass, fail, skip);
                long issues = LemoineLog.IssuesSince(issues0);
                if (issues > 0) Log(LemoineStrings.T("scopeBoxes.creator.log.nonFatalIssues", issues), "warn");
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
        }

        private void RunBoxes(Document doc, ref int pass, ref int fail, ref int skip)
        {
            var s = ScopeBoxSettings.Instance;

            // ── Seed ──────────────────────────────────────────────────
            var seed = SeedBoxId != ElementId.InvalidElementId ? doc.GetElement(SeedBoxId) : null;
            var seedBb = seed?.get_BoundingBox(null);
            if (seed == null || seedBb == null)
            {
                Log(LemoineStrings.T("scopeBoxes.creator.log.noSeed"), "fail");
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
            Log(LemoineStrings.T("scopeBoxes.creator.log.roomsFound", rooms.Count, sourceDocs.Count), "info");

            var selectedIdSet = new HashSet<long>(SelectedLevelIds.Select(id => id.Value));
            var selLevels     = allLevels.Where(l => selectedIdSet.Contains(l.Id.Value)).ToList();
            if (selLevels.Count == 0)
            {
                Log(LemoineStrings.T("scopeBoxes.creator.log.noLevels"), "fail");
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

            if (Mode == "PerLevel")
            {
                foreach (var lvl in selLevels)
                {
                    var levelRooms = rooms.Where(r => r.LevelName == lvl.Name).ToList();
                    if (levelRooms.Count == 0) continue;
                    var clusters = RoomClusterSearch.ClusterRooms(levelRooms, s.ClusterThreshold);
                    clusters.Sort((a, b) =>
                        b.Average(r => r.CentroidY).CompareTo(a.Average(r => r.CentroidY)));

                    for (int bi = 0; bi < clusters.Count; bi++)
                        specs.Add(MakeSpec(clusters[bi], clusters.Count, bi,
                                           lvl.Name, lvl.Name,
                                           lvl.Elevation, TopAbove(lvl), s.BufferXY));
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
                        : LemoineStrings.T("scopeBoxes.creator.tokens.levelRange", lowest.Name, highest.Name);

                    for (int bi = 0; bi < clusters.Count; bi++)
                        specs.Add(MakeSpec(clusters[bi], clusters.Count, bi,
                                           lowest.Name, range,
                                           lowest.Elevation, TopAbove(highest), s.BufferXY));
                }
            }

            // A silent empty result is indistinguishable from a broken collector — say so.
            if (specs.Count == 0)
            {
                Log(LemoineStrings.T("scopeBoxes.creator.log.noClusters"), "warn");
                return;
            }
            Log(LemoineStrings.T("scopeBoxes.creator.log.planned", specs.Count), "info");

            // ── Duplicate the seed per spec ───────────────────────────
            // Scope box name uniqueness is unverified in the API — pre-check against
            // existing boxes AND earlier boxes in this batch, skip-and-log collisions.
            var takenNames = new HashSet<string>(
                ScopeBoxCreatorScanHandler.CollectScopeBoxes(doc).Select(b => b.Name),
                StringComparer.OrdinalIgnoreCase);

            using (var tx = new Transaction(doc, "Create Scope Boxes"))
            {
                var opts = tx.GetFailureHandlingOptions();
                opts.SetClearAfterRollback(true);
                opts.SetDelayedMiniWarnings(true);
                tx.SetFailureHandlingOptions(opts);
                tx.Start();

                for (int i = 0; i < specs.Count; i++)
                {
                    if (LemoineRun.CancelRequested)
                    {
                        Log(LemoineStrings.T("common.log.stoppedByUser", i, specs.Count), "warn");
                        break;   // fall through to commit — work so far is preserved
                    }

                    var spec = specs[i];

                    if (takenNames.Contains(spec.Name))
                    {
                        Log(LemoineStrings.T("scopeBoxes.creator.log.skipExists", spec.Name), "info");
                        skip++;
                    }
                    else
                    {
                        try
                        {
                            XYZ translation = spec.Center - seedCenter;
                            var copyIds = ElementTransformUtils.CopyElement(doc, seed.Id, translation);
                            var copyId  = copyIds.FirstOrDefault() ?? ElementId.InvalidElementId;
                            var copy    = copyId != ElementId.InvalidElementId ? doc.GetElement(copyId) : null;
                            if (copy == null)
                            {
                                Log(LemoineStrings.T("scopeBoxes.creator.log.copyFailed", spec.Name), "fail");
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
                                        LemoineStrings.T("scopeBoxes.creator.log.renameRefused", rex.Message), rex);
                                }

                                takenNames.Add(spec.Name);
                                pass++;
                                Log(LemoineStrings.T("scopeBoxes.creator.log.created", spec.Name), "pass");

                                // Size mismatch → the user must drag the handles once. Report the
                                // exact required size (position and name are already correct).
                                const double tol = 0.5; // ft
                                if (Math.Abs(spec.Width - seedW) > tol ||
                                    Math.Abs(spec.Depth - seedD) > tol ||
                                    Math.Abs(spec.Height - seedH) > tol)
                                {
                                    Log(LemoineStrings.T("scopeBoxes.creator.log.resizeNeeded",
                                        spec.Name, spec.Width.ToString("0.#"),
                                        spec.Depth.ToString("0.#"), spec.Height.ToString("0.#")), "warn");
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Log(LemoineStrings.T("scopeBoxes.creator.log.failBox", spec.Name, e.Message), "fail");
                            fail++;
                        }
                    }

                    Progress((int)((i + 1) * 90.0 / specs.Count), pass, fail, skip);
                }

                tx.Commit();
            }

            Log(LemoineStrings.T("scopeBoxes.creator.log.complete", pass, skip, fail), "pass");
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

            string ResolveSlot(string slot, string custom)
            {
                switch (slot)
                {
                    case "Building Letter": return letter;
                    case "Level":           return levelToken;
                    case "Level Range":     return rangeToken;
                    case "Model Name":      return model;
                    case "Custom":          return string.IsNullOrWhiteSpace(custom) ? "" : custom.Trim();
                    default:                return "";
                }
            }

            var parts = new[]
                {
                    ResolveSlot(NamingFront,  NamingFrontCustom),
                    ResolveSlot(NamingCenter, NamingCenterCustom),
                    ResolveSlot(NamingEnd,    NamingEndCustom),
                }
                .Where(p => !string.IsNullOrEmpty(p)).ToList();

            // Safety: nothing resolved (all slots None / blank Custom) → letter + range.
            if (parts.Count == 0) { parts.Add(letter); parts.Add(rangeToken); }

            // Single cluster: a bare letter slot adds noise only when it's the sole
            // distinguisher — keep it regardless for stable re-runs (A stays A).
            return new BoxSpec
            {
                Name = string.Join(" - ", parts),
                X0 = x0, Y0 = y0, X1 = x1, Y1 = y1,
                ZBot = zBot, ZTop = zTop,
            };
        }

        private void Log(string t, string s) => PushLog?.Invoke(t, s);
        private void Progress(int p, int pa, int f, int s) => OnProgress?.Invoke(p, pa, f, s);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
