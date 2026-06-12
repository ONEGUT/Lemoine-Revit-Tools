using Autodesk.Revit.DB;

namespace LemoineTools.Tools.Testing.PlaceDependentViews
{
    /// <summary>
    /// A view the user can select in Step 1 — either a primary view that owns dependent
    /// views (dependents mode) or a composite-mode source-view candidate, where the sub
    /// views are discovered at run time (DepCount &lt; 0 = unknown, no count suffix).
    /// Built on the Revit thread by the command and handed to the view model.
    /// </summary>
    public sealed class ParentViewEntry
    {
        public ElementId Id        { get; }
        public string    Name      { get; }
        public string    TypeLabel { get; }   // ViewType, e.g. "FloorPlan"
        public string    LevelName { get; }   // associated level name, or ""
        public int       DepCount  { get; }   // dependents count, or -1 when not applicable

        public ParentViewEntry(ElementId id, string name, string typeLabel, string levelName, int depCount)
        {
            Id        = id;
            Name      = name      ?? "";
            TypeLabel = typeLabel ?? "";
            LevelName = levelName ?? "";
            DepCount  = depCount;
        }

        /// <summary>Label shown in the multi-select list (unique per view; dep count only when known).</summary>
        public string DisplayLabel => DepCount < 0
            ? Name
            : $"{Name}  ({DepCount} dep{(DepCount == 1 ? "" : "s")})";
    }
}
