using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LemoineTools.Tools.Testing.AutoDimension.Resolvers
{
    /// <summary>A source document the resolvers scan — the host plus every loaded link.</summary>
    public sealed class SourceDoc
    {
        public Document Doc { get; set; } = null!;
        public RevitLinkInstance? Link { get; set; }      // null = host
        public Transform Transform { get; set; } = Transform.Identity;
    }

    /// <summary>A datum the user picked manually for one view (edge reference + its world line).</summary>
    public sealed class ManualDatum
    {
        /// <summary>Host-usable reference to dimension out to (works for host or linked edges).</summary>
        public Reference Ref { get; set; } = null!;
        /// <summary>A world point on the datum (the pick's global point).</summary>
        public XYZ WorldPoint { get; set; } = XYZ.Zero;
        /// <summary>World direction of the datum edge; null when it could not be read.</summary>
        public XYZ? WorldDir { get; set; }
        /// <summary>Stable identity for stale cleanup / chaining grouping.</summary>
        public string Key { get; set; } = "";
    }

    /// <summary>A floor the user picked to scope slab-edge targeting to (host or linked).</summary>
    public sealed class SlabScope
    {
        /// <summary>Owning link instance id, or InvalidElementId for a host floor.</summary>
        public ElementId LinkInstanceId { get; set; } = ElementId.InvalidElementId;
        /// <summary>The floor's element id within its document.</summary>
        public ElementId FloorId { get; set; } = ElementId.InvalidElementId;
    }

    /// <summary>Everything a target resolver needs, gathered once per run on the Revit thread.</summary>
    public sealed class ResolveContext
    {
        public Document HostDoc { get; set; } = null!;
        public View View { get; set; } = null!;
        public ViewProjection Projection { get; set; } = null!;
        public List<SourceDoc> Sources { get; set; } = new List<SourceDoc>();
        public AutoDimensionConfig Config { get; set; } = new AutoDimensionConfig();

        /// <summary>Measurement axis for the current resolve pass (view-2D unit; +X or +Y).</summary>
        public Core.Vec2 Axis { get; set; } = new Core.Vec2(1, 0);

        /// <summary>User-picked datums for this view (manual-datum target mode only).</summary>
        public List<ManualDatum> Datums { get; set; } = new List<ManualDatum>();

        /// <summary>User-picked floors to scope slab-edge targeting to (empty = scan all floors).</summary>
        public List<SlabScope> SlabScopes { get; set; } = new List<SlabScope>();

        /// <summary>Sink for "couldn't get a link reference" reports (non-fatal).</summary>
        public Action<string> ReportMissingLink { get; set; } = _ => { };

        /// <summary>Progress/step log sink (text, status) — resolvers report cache build here.</summary>
        public Action<string, string> Log { get; set; } = (_, __) => { };
    }

    /// <summary>Outcome of resolving one source line's target. Exactly one state is set.</summary>
    public sealed class ResolvedTarget
    {
        public bool Success { get; set; }

        public Reference? TargetRef { get; set; }
        public Core.Vec2 TargetPoint2d { get; set; }
        public string TargetKey { get; set; } = "";

        /// <summary>Set when resolution failed outright (no candidate).</summary>
        public Core.UnresolvedTarget? Unresolved { get; set; }

        /// <summary>Set when the top two candidates scored within threshold — do not guess.</summary>
        public Core.AmbiguousTarget? Ambiguity { get; set; }

        public static ResolvedTarget Ok(Reference r, Core.Vec2 pt, string key) =>
            new ResolvedTarget { Success = true, TargetRef = r, TargetPoint2d = pt, TargetKey = key };

        public static ResolvedTarget Fail(string sourceKey, Core.TargetType t, string reason) =>
            new ResolvedTarget
            {
                Success = false,
                Unresolved = new Core.UnresolvedTarget { SourceKey = sourceKey, TargetType = t, Reason = reason },
            };
    }

    /// <summary>Resolves one source line to a destination reference. GRID and SLAB_EDGE are
    /// implemented as distinct units behind this interface; the grid path never touches slab logic.</summary>
    public interface ITargetResolver
    {
        ResolvedTarget Resolve(SourceLine source, ResolveContext ctx);
    }
}
