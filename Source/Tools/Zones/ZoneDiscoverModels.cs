using System;
using System.Collections.Generic;

namespace LemoineTools.Tools.Zones
{
    // =========================================================================
    // Proposal types for Zone Discover.
    //
    // A proposal is a SUGGESTION, never an applied change. The scan produces
    // them, the user reviews and unticks what they don't want, and only then
    // does the run merge the accepted ones into the library.
    //
    // Every proposal carries its own Action so the review list can say what will
    // actually happen — "add" and "update" read very differently to someone
    // deciding whether to trust a scan, and a scan that silently overwrote an
    // existing area would be the worst possible behaviour here.
    // =========================================================================

    /// <summary>What applying a proposal would do to the library.</summary>
    public enum ZoneProposalAction
    {
        /// <summary>Nothing like it exists yet.</summary>
        Add,
        /// <summary>A record already matches; applying refreshes its discovered fields.</summary>
        Update,
        /// <summary>Already present and identical — listed so the count is honest, ticked off by default.</summary>
        Unchanged,
    }

    public abstract class ZoneProposal
    {
        /// <summary>Ticked in the review list. Unchanged proposals start unticked.</summary>
        public bool Accepted { get; set; } = true;
        public ZoneProposalAction Action { get; set; } = ZoneProposalAction.Add;
        /// <summary>Id of the existing record this would update. Empty for an Add.</summary>
        public string ExistingId { get; set; } = "";
        /// <summary>One-line reason shown beside the row — where this came from.</summary>
        public string Provenance { get; set; } = "";

        public abstract string Label { get; }
    }

    public sealed class ZoneBuildingProposal : ZoneProposal
    {
        public string Name { get; set; } = "";
        public string Code { get; set; } = "";
        public override string Label => Name;
    }

    public sealed class ZoneLevelProposal : ZoneProposal
    {
        public string Name          { get; set; } = "";
        /// <summary>Host level this maps to — a NAME, never an id.</summary>
        public string HostLevelName { get; set; } = "";
        public double ElevationFt   { get; set; }
        public string SourceDoc     { get; set; } = "";
        /// <summary>Name of the building proposal this belongs to, matched up at apply time.</summary>
        public string BuildingName  { get; set; } = "";
        /// <summary>True when the host match was made on elevation because no name matched.</summary>
        public bool   MatchedByElevation { get; set; }

        public override string Label => Name;
    }

    public sealed class ZoneAreaProposal : ZoneProposal
    {
        public string Name         { get; set; } = "";
        public string BuildingName { get; set; } = "";
        /// <summary>Adopted scope box, when discovered from one.</summary>
        public string ScopeBoxName { get; set; } = "";
        public double MinX, MinY, MaxX, MaxY;
        public bool   HasExtents   { get; set; }
        /// <summary>Host level names this area was seen on. Empty means every level.</summary>
        public List<string> LevelNames { get; set; } = new List<string>();

        public double WidthFt => MaxX - MinX;
        public double DepthFt => MaxY - MinY;

        public override string Label => Name;
    }

    /// <summary>Everything one scan found. Empty lists are a real answer and are reported as such.</summary>
    public sealed class ZoneDiscoverResult
    {
        public List<ZoneBuildingProposal> Buildings { get; } = new List<ZoneBuildingProposal>();
        public List<ZoneLevelProposal>    Levels    { get; } = new List<ZoneLevelProposal>();
        public List<ZoneAreaProposal>     Areas     { get; } = new List<ZoneAreaProposal>();

        /// <summary>Human-readable notes the scan wants surfaced (skipped sources, fallbacks used).</summary>
        public List<string> Notes { get; } = new List<string>();

        public int TotalProposals => Buildings.Count + Levels.Count + Areas.Count;
        public int AcceptedCount
        {
            get
            {
                int n = 0;
                foreach (var b in Buildings) if (b.Accepted) n++;
                foreach (var l in Levels)    if (l.Accepted) n++;
                foreach (var a in Areas)     if (a.Accepted) n++;
                return n;
            }
        }
    }
}
