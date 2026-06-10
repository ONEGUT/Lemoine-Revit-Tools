using Autodesk.Revit.DB;

namespace LemoineTools.Tools.Testing.PlaceDependentViews
{
    /// <summary>
    /// A primary view that owns one or more dependent views — the unit the user selects
    /// in Step 1. Built on the Revit thread by the command and handed to the view model.
    /// </summary>
    public sealed class ParentViewEntry
    {
        public ElementId Id        { get; }
        public string    Name      { get; }
        public string    TypeLabel { get; }   // ViewType, e.g. "FloorPlan"
        public string    LevelName { get; }   // associated level name, or ""
        public int       DepCount  { get; }

        public ParentViewEntry(ElementId id, string name, string typeLabel, string levelName, int depCount)
        {
            Id        = id;
            Name      = name      ?? "";
            TypeLabel = typeLabel ?? "";
            LevelName = levelName ?? "";
            DepCount  = depCount;
        }

        /// <summary>Label shown in the multi-select list (unique per view, with dep count).</summary>
        public string DisplayLabel => $"{Name}  ({DepCount} dep{(DepCount == 1 ? "" : "s")})";
    }
}
