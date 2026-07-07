using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace LemoineTools.Tools.ScopeBoxes
{
    /// <summary>Data for one host level returned by the Scope Box Creator scan.</summary>
    public sealed class ScopeLevelInfo
    {
        public ElementId LevelId           { get; set; } = null!;
        public string    Name              { get; set; } = null!;
        /// <summary>Elevation in internal units (host coordinates), for Z-extent math.</summary>
        public double    ElevationInternal { get; set; }
        /// <summary>Elevation in display feet (rounded), for level labels.</summary>
        public double    ElevationFt       { get; set; }
        public int       RoomCount         { get; set; }
    }

    /// <summary>One existing scope box, for the seed picker.</summary>
    public sealed class ScopeBoxEntry
    {
        public ElementId Id   { get; set; } = null!;
        public string    Name { get; set; } = "";
        public double    WidthFt  { get; set; }
        public double    DepthFt  { get; set; }
        public double    HeightFt { get; set; }
        // World-space bounds (feet) — used by the Manager to test which datums intersect.
        public double    MinX { get; set; }
        public double    MinY { get; set; }
        public double    MinZ { get; set; }
        public double    MaxX { get; set; }
        public double    MaxY { get; set; }
        public double    MaxZ { get; set; }
    }

    /// <summary>
    /// Phase-1 scan for the Scope Box Creator: collects host levels with room counts,
    /// the raw room records (handed to the ViewModel so cluster counts can be recomputed
    /// live as the user changes mode/levels/threshold — pure math, no Revit access),
    /// and the existing scope boxes for the seed picker.
    ///
    /// Read-only — no transaction.
    /// </summary>
    public sealed class ScopeBoxCreatorScanHandler : IExternalEventHandler
    {
        // ── Inputs set before Raise() ─────────────────────────────────
        public bool            IncludeHost { get; set; } = true;
        public List<ElementId> LinkInstIds { get; set; } = new List<ElementId>();
        /// <summary>When true, only the scope-box list is refreshed (fast seed-picker
        /// refresh on step activation, e.g. after the user draws a box mid-session).</summary>
        public bool            BoxesOnly   { get; set; } = false;

        // ── Callbacks ─────────────────────────────────────────────────
        public Action<List<ScopeLevelInfo>, List<RoomInfo>, List<ScopeBoxEntry>>? OnScanComplete { get; set; }
        public Action<List<ScopeBoxEntry>>? OnBoxesLoaded { get; set; }
        public Action<string>?              OnError       { get; set; }

        public string GetName() => "LemoineTools.Tools.ScopeBoxes.ScopeBoxCreatorScanHandler";

        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument.Document;
            try
            {
                var boxes = CollectScopeBoxes(doc);

                if (BoxesOnly)
                {
                    OnBoxesLoaded?.Invoke(boxes);
                    return;
                }

                // Source documents
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

                var countByLevel = rooms
                    .GroupBy(r => r.LevelName, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

                var levels = allLevels.Select(l => new ScopeLevelInfo
                {
                    LevelId           = l.Id,
                    Name              = l.Name,
                    ElevationInternal = l.Elevation,
                    ElevationFt       = Math.Round(UnitUtils.ConvertFromInternalUnits(
                                            l.Elevation, UnitTypeId.Feet), 2),
                    RoomCount         = countByLevel.TryGetValue(l.Name, out var c) ? c : 0,
                }).ToList();

                OnScanComplete?.Invoke(levels, rooms, boxes);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex.Message);
            }
            finally
            {
                // Session-long static handler — drop the scan's payload.
                LinkInstIds = new List<ElementId>();
                BoxesOnly   = false;
            }
        }

        /// <summary>Lists all scope boxes in the host document with their bbox sizes.</summary>
        internal static List<ScopeBoxEntry> CollectScopeBoxes(Document doc)
        {
            var result = new List<ScopeBoxEntry>();
            foreach (var e in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_VolumeOfInterest)
                         .WhereElementIsNotElementType())
            {
                var bb = e.get_BoundingBox(null);
                result.Add(new ScopeBoxEntry
                {
                    Id       = e.Id,
                    Name     = e.Name,
                    WidthFt  = bb != null ? bb.Max.X - bb.Min.X : 0,
                    DepthFt  = bb != null ? bb.Max.Y - bb.Min.Y : 0,
                    HeightFt = bb != null ? bb.Max.Z - bb.Min.Z : 0,
                    MinX = bb?.Min.X ?? 0, MinY = bb?.Min.Y ?? 0, MinZ = bb?.Min.Z ?? 0,
                    MaxX = bb?.Max.X ?? 0, MaxY = bb?.Max.Y ?? 0, MaxZ = bb?.Max.Z ?? 0,
                });
            }
            return result.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
