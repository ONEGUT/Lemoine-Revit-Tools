using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Zones;
using LemoineTools.Tools.ScopeBoxes;

namespace LemoineTools.Tools.Zones
{
    // =========================================================================
    // ZoneDiscoverScanHandler — reads the model and PROPOSES a zone structure.
    //
    // Read-only. No transaction, no writes of any kind. Everything it returns is
    // a suggestion the user reviews before anything touches the library.
    //
    // Where each axis comes from:
    //
    //   Levels    the chosen source documents' Levels, reconciled to HOST levels
    //             by NAME first and elevation second — the cross-document
    //             identity rule, and the reason a linked model's levels can be
    //             adopted at all.
    //   Buildings room clusters (union-find over room bounds), which is how a
    //             separated campus or a podium-plus-towers model reveals itself.
    //             One cluster is one building; a single cluster yields one.
    //   Areas     existing scope boxes by default, since that is what a project
    //             already set up has. Room clusters are the fallback for a model
    //             with no boxes drawn yet.
    //
    // A zero result is always REPORTED, never returned as a silent empty list —
    // an empty scan is otherwise indistinguishable from a broken collector, and
    // that silence has hidden real bugs in this repo before.
    // =========================================================================
    public sealed class ZoneDiscoverScanHandler : IExternalEventHandler
    {
        // ── Inputs, set before Raise() ────────────────────────────────────────
        /// <summary>Read the host document as a source too.</summary>
        public bool IncludeHost { get; set; } = true;
        /// <summary>RevitLinkInstance ids whose documents are also read.</summary>
        public List<ElementId> LinkInstIds { get; set; } = new List<ElementId>();

        public bool DiscoverLevels        { get; set; } = true;
        public bool DiscoverBuildings     { get; set; } = true;
        /// <summary>Adopt existing scope boxes as areas. The default source.</summary>
        public bool AreasFromScopeBoxes   { get; set; } = true;
        /// <summary>Derive areas from room clusters. Used when there are no boxes.</summary>
        public bool AreasFromRoomClusters { get; set; }

        /// <summary>Max room-edge gap for cluster merging, feet.</summary>
        public double ClusterThresholdFt { get; set; } = 20.0;
        /// <summary>
        /// XY margin added around a room cluster's bounds, feet. Rooms stop at their bounding
        /// walls, so a zero buffer would cut the enclosing construction out of the area.
        /// </summary>
        public double ClusterBufferFt { get; set; } = 10.0;

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<ZoneDiscoverResult>? OnScanComplete { get; set; }
        public Action<string>?             OnError        { get; set; }

        public string GetName() => "LemoineTools.Tools.Zones.ZoneDiscoverScanHandler";

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app?.ActiveUIDocument?.Document;
                if (doc == null) { OnError?.Invoke("No active document."); return; }
                OnScanComplete?.Invoke(Collect(doc));
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneDiscoverScanHandler: scan", ex);
                OnError?.Invoke(ex.Message);
            }
            finally
            {
                // Static handlers live for the whole Revit session, so the per-run payload is
                // cleared or it outlives the run. ViewModels reassign every input before Raise.
                LinkInstIds = new List<ElementId>();
            }
        }

        internal ZoneDiscoverResult Collect(Document doc)
        {
            var result = new ZoneDiscoverResult();
            var lib    = ZoneSettings.Instance.Library;

            // ── Source documents ──────────────────────────────────────────────
            var sources = new List<(Document Doc, string Label, Transform Tf)>();
            if (IncludeHost) sources.Add((doc, doc.Title ?? "host", Transform.Identity));

            foreach (var id in LinkInstIds ?? new List<ElementId>())
            {
                var li = doc.GetElement(id) as RevitLinkInstance;
                var ld = li?.GetLinkDocument();
                if (ld == null)
                {
                    // An unloaded link is a real condition the user needs to know about, not
                    // something to skip quietly — its levels simply will not appear.
                    result.Notes.Add($"A selected link could not be read (unloaded or missing) and was skipped.");
                    continue;
                }
                if (sources.Any(s => s.Doc.Equals(ld))) continue;
                sources.Add((ld, ld.Title ?? li!.Name, li!.GetTotalTransform()));
            }

            if (sources.Count == 0)
            {
                result.Notes.Add("No source documents were selected, so nothing could be discovered.");
                return result;
            }

            // Host levels are the reconciliation target for every discovered level.
            var hostLevels = new List<Level>();
            try
            {
                hostLevels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.Elevation).ToList();
            }
            catch (Exception ex) { DiagnosticsLog.Error("ZoneDiscover: collect host levels", ex); }

            if (hostLevels.Count == 0)
                result.Notes.Add("The host document has no levels, so discovered levels cannot be reconciled to it.");

            // ── Buildings, from room clusters ─────────────────────────────────
            List<List<RoomInfo>> clusters = new List<List<RoomInfo>>();
            if (DiscoverBuildings || AreasFromRoomClusters)
            {
                try
                {
                    var rooms = RoomClusterSearch.CollectRooms(doc, sources.Select(s => s.Doc));
                    RoomClusterSearch.AssignHostLevelsByElevation(
                        rooms, hostLevels, RoomClusterSearch.LevelMatchToleranceFt);

                    if (rooms.Count == 0)
                        result.Notes.Add("Found 0 placed rooms in the selected sources — buildings and room-based areas cannot be derived.");
                    else
                        clusters = RoomClusterSearch.ClusterRooms(rooms, ClusterThresholdFt);
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Error("ZoneDiscover: room clustering", ex);
                    result.Notes.Add("Room clustering failed; buildings and room-based areas were not derived.");
                }
            }

            var buildingNames = new List<string>();
            if (DiscoverBuildings)
            {
                for (int i = 0; i < Math.Max(clusters.Count, 1); i++)
                {
                    string letter = RoomClusterSearch.BldgLetter(i);
                    string name   = clusters.Count <= 1 ? "Building" : $"Building {letter}";
                    buildingNames.Add(name);

                    var existing = lib.Buildings.FirstOrDefault(
                        b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

                    result.Buildings.Add(new ZoneBuildingProposal
                    {
                        Name       = name,
                        Code       = clusters.Count <= 1 ? "" : letter,
                        Action     = existing == null ? ZoneProposalAction.Add : ZoneProposalAction.Unchanged,
                        ExistingId = existing?.Id ?? "",
                        Accepted   = existing == null,
                        Provenance = clusters.Count <= 1
                            ? "one room cluster"
                            : $"room cluster {letter} ({clusters[i].Count} rooms)",
                    });
                }
            }
            string defaultBuilding = buildingNames.FirstOrDefault()
                                     ?? lib.Buildings.FirstOrDefault()?.Name ?? "";

            // ── Levels ────────────────────────────────────────────────────────
            if (DiscoverLevels)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int found = 0;

                foreach (var src in sources)
                {
                    List<Level> levels;
                    try
                    {
                        levels = new FilteredElementCollector(src.Doc)
                            .OfClass(typeof(Level)).Cast<Level>()
                            .OrderBy(l => l.Elevation).ToList();
                    }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Error($"ZoneDiscover: collect levels from '{src.Label}'", ex);
                        result.Notes.Add($"Could not read levels from '{src.Label}'.");
                        continue;
                    }

                    if (levels.Count == 0)
                    {
                        result.Notes.Add($"Found 0 levels in '{src.Label}'.");
                        continue;
                    }
                    found += levels.Count;

                    foreach (var lv in levels)
                    {
                        // Elevation in HOST coordinates, so a linked level compares against the
                        // host on the same axis.
                        double worldElev = src.Tf.OfPoint(new XYZ(0, 0, lv.Elevation)).Z;

                        string hostName = MatchHostLevel(lv.Name, worldElev, hostLevels, out bool byElevation);
                        if (string.IsNullOrEmpty(hostName))
                        {
                            result.Notes.Add(
                                $"'{lv.Name}' from '{src.Label}' has no matching host level " +
                                "(by name or elevation) and was not proposed.");
                            continue;
                        }
                        if (!seen.Add(hostName)) continue;   // one proposal per host level

                        var existing = lib.Levels.FirstOrDefault(
                            x => string.Equals(x.HostLevelName, hostName, StringComparison.OrdinalIgnoreCase));

                        result.Levels.Add(new ZoneLevelProposal
                        {
                            Name               = hostName,
                            HostLevelName      = hostName,
                            ElevationFt        = Math.Round(worldElev, 4),
                            SourceDoc          = src.Label,
                            BuildingName       = defaultBuilding,
                            MatchedByElevation = byElevation,
                            Action             = existing == null ? ZoneProposalAction.Add : ZoneProposalAction.Unchanged,
                            ExistingId         = existing?.Id ?? "",
                            Accepted           = existing == null,
                            Provenance         = byElevation
                                ? $"{src.Label} · matched to host by elevation"
                                : src.Label,
                        });
                    }
                }

                if (found == 0) result.Notes.Add("Found 0 levels across every selected source.");
            }

            // ── Areas ─────────────────────────────────────────────────────────
            if (AreasFromScopeBoxes)
            {
                var boxes = ZoneScopeBoxSync.CollectBoxes(doc).Where(b => b.HasBounds).ToList();
                if (boxes.Count == 0)
                    result.Notes.Add("Found 0 scope boxes in the host document.");

                foreach (var b in boxes.OrderBy(b => b.Name, NaturalOrderComparer.OrdinalIgnoreCase))
                {
                    var existing = lib.Areas.FirstOrDefault(
                        a => string.Equals(a.ScopeBoxName, b.Name, StringComparison.OrdinalIgnoreCase));

                    bool changed = existing != null &&
                                   (!existing.HasExtents ||
                                    Math.Abs(existing.MinX - b.MinX) > 1e-4 ||
                                    Math.Abs(existing.MinY - b.MinY) > 1e-4 ||
                                    Math.Abs(existing.MaxX - b.MaxX) > 1e-4 ||
                                    Math.Abs(existing.MaxY - b.MaxY) > 1e-4);

                    result.Areas.Add(new ZoneAreaProposal
                    {
                        Name         = b.Name,
                        BuildingName = defaultBuilding,
                        ScopeBoxName = b.Name,
                        MinX = b.MinX, MinY = b.MinY, MaxX = b.MaxX, MaxY = b.MaxY,
                        HasExtents   = true,
                        Action       = existing == null ? ZoneProposalAction.Add
                                     : (changed ? ZoneProposalAction.Update : ZoneProposalAction.Unchanged),
                        ExistingId   = existing?.Id ?? "",
                        // An Update re-adopts extents, which can move stored placements — so it
                        // is offered, never pre-ticked.
                        Accepted     = existing == null,
                        Provenance   = $"scope box · {b.WidthFt:0.#}' × {b.DepthFt:0.#}'",
                    });
                }
            }

            if (AreasFromRoomClusters && clusters.Count > 0)
            {
                for (int i = 0; i < clusters.Count; i++)
                {
                    var (x0, y0, x1, y1) = RoomClusterSearch.ClusterBoundsXY(clusters[i], ClusterBufferFt);
                    string name = clusters.Count == 1 ? "Area 1" : $"Area {i + 1}";
                    if (result.Areas.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
                        continue;   // a scope box already claimed this name

                    var existing = lib.Areas.FirstOrDefault(
                        a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

                    result.Areas.Add(new ZoneAreaProposal
                    {
                        Name         = name,
                        BuildingName = clusters.Count <= 1 ? defaultBuilding : $"Building {RoomClusterSearch.BldgLetter(i)}",
                        MinX = x0, MinY = y0, MaxX = x1, MaxY = y1,
                        HasExtents   = true,
                        Action       = existing == null ? ZoneProposalAction.Add : ZoneProposalAction.Unchanged,
                        ExistingId   = existing?.Id ?? "",
                        Accepted     = existing == null,
                        Provenance   = $"room cluster · {clusters[i].Count} rooms",
                    });
                }
            }

            if (result.TotalProposals == 0)
                result.Notes.Add("Nothing was found to propose. Check the selected sources.");

            return result;
        }

        /// <summary>
        /// Reconciles a source level to a host level. NAME first, elevation second — the
        /// recorded cross-document rule, because a linked model's level is a different element
        /// in a different document and its id means nothing here.
        /// Returns "" when neither matches.
        /// </summary>
        private static string MatchHostLevel(string name, double worldElevation,
                                             List<Level> hostLevels, out bool byElevation)
        {
            byElevation = false;
            if (hostLevels == null || hostLevels.Count == 0) return "";

            var byName = hostLevels.FirstOrDefault(
                h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase));
            if (byName != null) return byName.Name;

            var nearest = hostLevels
                .OrderBy(h => Math.Abs(h.Elevation - worldElevation))
                .FirstOrDefault();
            if (nearest != null &&
                Math.Abs(nearest.Elevation - worldElevation) <= RoomClusterSearch.LevelMatchToleranceFt)
            {
                byElevation = true;
                return nearest.Name;
            }
            return "";
        }
    }
}
