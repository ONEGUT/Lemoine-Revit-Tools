using System.Collections.Generic;

namespace LemoineTools.Tools.Testing.AutoDimension.Core
{
    /// <summary>
    /// Pluggable, injected collision predicate. The layout core knows nothing about Revit;
    /// the I/O layer supplies the obstacle set (existing MEP dimensions, MEP tags/text, the
    /// source lines) as plain paper-space boxes. Architectural / structural halftone
    /// background geometry is deliberately excluded by the supplier — the core never sees it.
    /// </summary>
    public interface ICollisionSource
    {
        /// <summary>
        /// Paper-space obstacle boxes the layout must avoid. Must be returned in a stable,
        /// deterministic order so identical input yields an identical plan.
        /// </summary>
        IReadOnlyList<Box2> Obstacles { get; }
    }

    /// <summary>Simple immutable collision source backed by a pre-built list.</summary>
    public sealed class StaticCollisionSource : ICollisionSource
    {
        public StaticCollisionSource(IReadOnlyList<Box2> obstacles) { Obstacles = obstacles; }
        public IReadOnlyList<Box2> Obstacles { get; }
    }
}
