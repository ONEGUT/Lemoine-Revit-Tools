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

    /// <summary>Everything a target resolver needs, gathered once per run on the Revit thread.</summary>
    public sealed class ResolveContext
    {
        public Document HostDoc { get; set; } = null!;
        public View View { get; set; } = null!;
        public ViewProjection Projection { get; set; } = null!;
        public List<SourceDoc> Sources { get; set; } = new List<SourceDoc>();
        public AutoDimensionConfig Config { get; set; } = new AutoDimensionConfig();

        /// <summary>Sink for "couldn't get a link reference" reports (non-fatal).</summary>
        public Action<string> ReportMissingLink { get; set; } = _ => { };
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
