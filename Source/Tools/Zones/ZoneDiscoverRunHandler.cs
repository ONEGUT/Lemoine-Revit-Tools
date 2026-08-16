using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Zones;

namespace LemoineTools.Tools.Zones
{
    // =========================================================================
    // ZoneDiscoverRunHandler — merges accepted proposals into the zone library
    // and writes it into the document.
    //
    // Merge rules, all of them non-destructive:
    //
    //   • Match an existing record and UPDATE it in place, keeping its GUID.
    //     Minting a new id would silently detach every layout group, cell
    //     override and sheet placement that referenced it.
    //   • NEVER delete. A discover pass that removed anything it did not
    //     re-find would destroy hand-authored work the moment a link was
    //     unloaded.
    //   • Re-adopting extents on an existing area is reported when placements
    //     already exist for it, because that is exactly when the drawings move.
    //
    // The library write needs a transaction on the main thread, which is where
    // this handler runs, so the whole merge is committed in one.
    // =========================================================================
    public sealed class ZoneDiscoverRunHandler : IExternalEventHandler
    {
        /// <summary>The reviewed result. Only proposals with Accepted = true are applied.</summary>
        public ZoneDiscoverResult? Result { get; set; }

        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.Zones.ZoneDiscoverRunHandler";

        private void Log(string text, string tone)
        {
            try { PushLog?.Invoke(text, tone); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("ZoneDiscoverRunHandler: log", ex); }
        }

        public void Execute(UIApplication app)
        {
            int added = 0, updated = 0, skipped = 0;
            try
            {
                var doc = app?.ActiveUIDocument?.Document;
                if (doc == null) { Log("No active document.", "fail"); return; }

                var result = Result;
                if (result == null || result.AcceptedCount == 0)
                {
                    Log("Nothing was accepted, so no zones were changed.", "warn");
                    return;
                }

                RevitFailureCapture.BeginRun();
                var lib = ZoneSettings.Instance.Library;

                // ── Buildings first: levels and areas reference them by name ───
                foreach (var p in result.Buildings.Where(x => x.Accepted))
                {
                    if (RunState.CancelRequested) break;
                    var existing = lib.Building(p.ExistingId)
                                   ?? lib.Buildings.FirstOrDefault(b => string.Equals(b.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        existing.Code = string.IsNullOrEmpty(p.Code) ? existing.Code : p.Code;
                        updated++;
                        Log($"Building '{p.Name}' already existed — left in place.", "info");
                    }
                    else
                    {
                        lib.Buildings.Add(new ZoneBuilding
                        {
                            Id = ZoneId.New(), Name = p.Name, Code = p.Code, SortIndex = lib.Buildings.Count,
                        });
                        added++;
                        Log($"Added building '{p.Name}'.", "pass");
                    }
                }

                string BuildingIdFor(string name)
                {
                    if (string.IsNullOrEmpty(name)) return lib.Buildings.FirstOrDefault()?.Id ?? "";
                    var b = lib.Buildings.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                    return b?.Id ?? lib.Buildings.FirstOrDefault()?.Id ?? "";
                }

                // ── Levels ─────────────────────────────────────────────────────
                foreach (var p in result.Levels.Where(x => x.Accepted))
                {
                    if (RunState.CancelRequested) break;
                    var existing = lib.Level(p.ExistingId)
                                   ?? lib.Levels.FirstOrDefault(l => string.Equals(l.HostLevelName, p.HostLevelName, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        // Update in place — the GUID is referenced by cells and area level lists.
                        existing.ElevationFt     = p.ElevationFt;
                        existing.SourceLevelName = p.Name;
                        existing.SourceLinkKey   = p.SourceDoc;
                        updated++;
                        Log($"Level '{p.HostLevelName}' updated from '{p.SourceDoc}'.", "info");
                    }
                    else
                    {
                        lib.Levels.Add(new ZoneLevel
                        {
                            Id              = ZoneId.New(),
                            Name            = p.Name,
                            HostLevelName   = p.HostLevelName,
                            ElevationFt     = p.ElevationFt,
                            SourceLevelName = p.Name,
                            SourceLinkKey   = p.SourceDoc,
                            BuildingId      = BuildingIdFor(p.BuildingName),
                            SortIndex       = lib.Levels.Count,
                        });
                        added++;
                        Log(p.MatchedByElevation
                                ? $"Added level '{p.HostLevelName}' (matched to the host by elevation, not name)."
                                : $"Added level '{p.HostLevelName}'.",
                            p.MatchedByElevation ? "warn" : "pass");
                    }
                }

                // ── Areas ──────────────────────────────────────────────────────
                foreach (var p in result.Areas.Where(x => x.Accepted))
                {
                    if (RunState.CancelRequested) break;
                    var existing = lib.Area(p.ExistingId)
                                   ?? lib.Areas.FirstOrDefault(a =>
                                        (!string.IsNullOrEmpty(p.ScopeBoxName) &&
                                         string.Equals(a.ScopeBoxName, p.ScopeBoxName, StringComparison.OrdinalIgnoreCase))
                                        || string.Equals(a.Name, p.Name, StringComparison.OrdinalIgnoreCase));

                    if (existing != null)
                    {
                        bool hadPlacements = lib.Placements.Any(
                            x => string.Equals(x.AreaId, existing.Id, StringComparison.Ordinal));

                        existing.ScopeBoxName = p.ScopeBoxName;
                        if (p.HasExtents)
                        {
                            existing.MinX = p.MinX; existing.MinY = p.MinY;
                            existing.MaxX = p.MaxX; existing.MaxY = p.MaxY;
                            existing.HasExtents = true;

                            // A GridIntersection anchor is deliberately left alone — not moving
                            // when extents do is the entire reason to choose it.
                            if (existing.AnchorMode == ZoneAnchorMode.ExtentsCentre || !existing.HasAnchor)
                            {
                                existing.AnchorX = (existing.MinX + existing.MaxX) / 2.0;
                                existing.AnchorY = (existing.MinY + existing.MaxY) / 2.0;
                                existing.HasAnchor = true;
                            }
                        }
                        updated++;

                        if (hadPlacements)
                            Log($"Area '{existing.Name}': extents re-adopted. Sheet placements already exist " +
                                "for it, so re-solve them or its views will land where the old extents put them.", "warn");
                        else
                            Log($"Area '{existing.Name}' updated from '{p.ScopeBoxName}'.", "info");
                    }
                    else
                    {
                        var area = new ZoneArea
                        {
                            Id           = ZoneId.New(),
                            Name         = p.Name,
                            BuildingId   = BuildingIdFor(p.BuildingName),
                            Definition   = string.IsNullOrEmpty(p.ScopeBoxName)
                                             ? ZoneExtentMode.RoomCluster : ZoneExtentMode.ScopeBox,
                            ScopeBoxName = p.ScopeBoxName,
                            MinX = p.MinX, MinY = p.MinY, MaxX = p.MaxX, MaxY = p.MaxY,
                            HasExtents   = p.HasExtents,
                            SortIndex    = lib.Areas.Count,
                        };
                        if (p.HasExtents)
                        {
                            area.AnchorX = (area.MinX + area.MaxX) / 2.0;
                            area.AnchorY = (area.MinY + area.MaxY) / 2.0;
                            area.HasAnchor = true;
                        }
                        lib.Areas.Add(area);
                        added++;
                        Log($"Added area '{p.Name}' ({p.WidthFt:0.#}' × {p.DepthFt:0.#}').", "pass");
                    }
                }

                if (RunState.CancelRequested)
                    Log($"Stopped by user — {added + updated} of {result.AcceptedCount} applied; work so far preserved.", "warn");

                // ── Commit the library into the document ───────────────────────
                if (!ZoneStore.CanWrite(doc, out string? why))
                {
                    Log($"Zones could not be saved into the model: {why}. " +
                        "The changes are in memory and will be written when the model becomes writable.", "fail");
                    skipped++;
                }
                else
                {
                    using (var tx = new Transaction(doc, "Lemoine — Discover Zones"))
                    {
                        tx.Start();
                        bool ok = ZoneStore.Write(doc, ZoneSettings.SerializeProjectLibrary());
                        if (ok) { tx.Commit(); Log("Zone library saved into the model.", "pass"); }
                        else
                        {
                            tx.RollBack();
                            Log("The zone library could not be written — nothing was saved.", "fail");
                            skipped++;
                        }
                    }
                }

                foreach (var note in result.Notes) Log(note, "warn");

                Log($"Discover finished: {added} added, {updated} updated.", added + updated > 0 ? "pass" : "warn");
                OnProgress?.Invoke(100, added, skipped, updated);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneDiscoverRunHandler: apply", ex);
                Log($"Discover failed: {ex.Message}", "fail");
                skipped++;
            }
            finally
            {
                try { OnComplete?.Invoke(added, skipped, updated); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("ZoneDiscoverRunHandler: OnComplete", ex); }

                // Clear the per-run payload: this handler is parked on a static for the whole
                // Revit session, so anything left here outlives the run.
                Result = null;
            }
        }
    }
}
