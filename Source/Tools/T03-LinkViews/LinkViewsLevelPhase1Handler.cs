using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Tools.ScopeBoxes;
using static LemoineTools.Tools.LinkViews.LinkViewsLevelHelpers;
using static LemoineTools.Tools.ScopeBoxes.RoomClusterSearch;

namespace LemoineTools.Tools.LinkViews
{
    /// <summary>Data for one host level returned from the Phase 1 room scan.</summary>
    public sealed class LevelScanResult
    {
        public ElementId LevelId      { get; set; } = null!;
        public string    Name         { get; set; } = null!;
        public double    ElevationFt  { get; set; }
        public int       RoomCount    { get; set; }
        /// <summary>Source document that contributed rooms to this level (for display grouping).</summary>
        public string    DocumentName { get; set; } = null!;
        /// <summary>
        /// Display name of the source document with the MOST rooms on this level.
        /// Used as the "Model Name" naming token when generating view names.
        /// </summary>
        public string    ModelName    { get; set; } = null!;
    }

    public sealed class LinkViewsLevelPhase1Handler : IExternalEventHandler
    {
        // ── Inputs set before Raise() ─────────────────────────────────
        public bool            IncludeHost { get; set; } = true;
        public List<ElementId> LinkInstIds { get; set; } = new List<ElementId>();

        // ── Callbacks ─────────────────────────────────────────────────
        public Action<List<LevelScanResult>>? OnLevelsLoaded { get; set; }
        public Action<string>?                OnError        { get; set; }

        public string GetName() => "LemoineTools.Tools.LinkViews.LinkViewsLevelPhase1Handler";

        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument.Document;
            try
            {
                var sourcePairs = new List<(Document Doc, string DisplayName)>();
                if (IncludeHost) sourcePairs.Add((doc, "(Host document)"));

                foreach (var id in LinkInstIds)
                {
                    var li = doc.GetElement(id) as RevitLinkInstance;
                    var ld = li?.GetLinkDocument();
                    if (ld == null) continue;
                    if (sourcePairs.Any(p => p.Doc.Equals(ld))) continue;
                    sourcePairs.Add((ld, ld.Title ?? li!.Name));
                }

                var allLevels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.Elevation).ToList();

                // Single-pass: collect rooms per doc and cache — avoids a second CollectRooms call
                var cachedRooms = new Dictionary<string, List<RoomInfo>>(StringComparer.Ordinal);
                foreach (var pair in sourcePairs)
                    cachedRooms[pair.DisplayName] = CollectRooms(doc, new List<Document> { pair.Doc });

                // Reconcile link rooms to host levels by elevation so differently-named link levels
                // still count under the correct host level (and stop generating "(No rooms)" rows).
                foreach (var kv in cachedRooms)
                    AssignHostLevelsByElevation(kv.Value, allLevels, LevelMatchToleranceFt);

                // Accumulate room counts per (levelName, docName)
                var roomCounts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
                foreach (var kv in cachedRooms)
                {
                    foreach (var r in kv.Value)
                    {
                        if (!roomCounts.ContainsKey(r.LevelName))
                            roomCounts[r.LevelName] = new Dictionary<string, int>(StringComparer.Ordinal);
                        var byDoc = roomCounts[r.LevelName];
                        if (!byDoc.ContainsKey(kv.Key)) byDoc[kv.Key] = 0;
                        byDoc[kv.Key]++;
                    }
                }

                // Compute dominant doc (most rooms) per level
                var dominantDoc = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in roomCounts)
                    dominantDoc[kv.Key] = kv.Value.OrderByDescending(d => d.Value).First().Key;

                var results = new List<LevelScanResult>();

                // Build results using the cached room data — no second scan needed
                foreach (var pair in sourcePairs)
                {
                    var rooms   = cachedRooms[pair.DisplayName];
                    var byLevel = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var r in rooms)
                    {
                        if (!byLevel.ContainsKey(r.LevelName)) byLevel[r.LevelName] = 0;
                        byLevel[r.LevelName]++;
                    }

                    foreach (var lvl in allLevels.Where(l => byLevel.ContainsKey(l.Name)))
                    {
                        results.Add(new LevelScanResult
                        {
                            LevelId      = lvl.Id,
                            Name         = lvl.Name,
                            ElevationFt  = Math.Round(UnitUtils.ConvertFromInternalUnits(
                                               lvl.Elevation, UnitTypeId.Feet), 2),
                            RoomCount    = byLevel[lvl.Name],
                            DocumentName = pair.DisplayName,
                            ModelName    = dominantDoc.TryGetValue(lvl.Name, out var dom) ? dom : pair.DisplayName,
                        });
                    }
                }

                // Append fallback entries for host levels that have no rooms in any source doc
                var coveredIds = new HashSet<long>(results.Select(r => r.LevelId.Value));
                foreach (var lvl in allLevels.Where(l => !coveredIds.Contains(l.Id.Value)))
                {
                    results.Add(new LevelScanResult
                    {
                        LevelId      = lvl.Id,
                        Name         = lvl.Name,
                        ElevationFt  = Math.Round(UnitUtils.ConvertFromInternalUnits(
                                           lvl.Elevation, UnitTypeId.Feet), 2),
                        RoomCount    = 0,
                        DocumentName = "(No rooms)",
                        ModelName    = "",
                    });
                }

                OnLevelsLoaded?.Invoke(results);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex.Message);
            }
            finally
            {
                // Session-long static handler (App.LinkViewsLevelPhase1Handler) — drop the scan's payload.
                LinkInstIds = new List<ElementId>();
            }
        }
    }
}
