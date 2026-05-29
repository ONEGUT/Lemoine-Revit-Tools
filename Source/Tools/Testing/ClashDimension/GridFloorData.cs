using System.Collections.Generic;

namespace LemoineTools.Tools.Testing
{
    /// <summary>
    /// Parallel lists of display name / element-id / link-instance-id
    /// for grids and floors collected by ClashDimensionCommand.
    /// linkId == 0 means the element lives in the host document.
    /// </summary>
    public sealed class GridFloorData
    {
        public List<string> GridNames   { get; } = new List<string>();
        public List<long>   GridIds     { get; } = new List<long>();
        public List<long>   GridLinkIds { get; } = new List<long>();

        public List<string> FloorNames   { get; } = new List<string>();
        public List<long>   FloorIds     { get; } = new List<long>();
        public List<long>   FloorLinkIds { get; } = new List<long>();

        public void AddGrid(string name, long id, long linkId)
        {
            GridNames.Add(name);
            GridIds.Add(id);
            GridLinkIds.Add(linkId);
        }

        public void AddFloor(string name, long id, long linkId)
        {
            FloorNames.Add(name);
            FloorIds.Add(id);
            FloorLinkIds.Add(linkId);
        }
    }
}
