using System.Collections.Generic;
using System.Linq;

namespace LemoineTools.Lemoine
{
    /// <summary>
    /// Revit-free snapshot of the source document's Project Browser organization,
    /// captured on the Revit main thread by <c>BrowserTreeCapture</c> when a tool
    /// opens. Consumed by <c>LemoineBrowserTreePicker</c> so view/sheet pickers
    /// mirror the browser's folder titles, nesting, and ordering exactly.
    /// </summary>
    public sealed class LemoineBrowserTree
    {
        /// <summary>
        /// Top-level browser nodes in display order — "Views (Discipline)",
        /// "Legends", "Schedules/Quantities", "Sheets (all sheets)". Roots that
        /// end up with no eligible leaves are hidden by the picker.
        /// </summary>
        public List<LemoineBrowserNode> Roots { get; } = new List<LemoineBrowserNode>();
    }

    /// <summary>
    /// One node of the captured browser tree. A node with a null <see cref="Id"/>
    /// is an organization folder; a node with an Id is a selectable view or sheet.
    /// A view that owns dependent views carries them as <see cref="Children"/>,
    /// exactly as the Project Browser nests them.
    /// </summary>
    public sealed class LemoineBrowserNode
    {
        /// <summary>Folder title, or the leaf's display name (view name / "number - name" for sheets).</summary>
        public string Title { get; set; } = "";

        /// <summary>ElementId.Value of the view/sheet when selectable; null for folders.</summary>
        public long? Id { get; set; }

        /// <summary>True when this leaf is a sheet rather than a view.</summary>
        public bool IsSheet { get; set; }

        public List<LemoineBrowserNode> Children { get; } = new List<LemoineBrowserNode>();

        public bool IsLeaf => Id.HasValue;

        /// <summary>All selectable ids at or below this node (self included when a leaf).</summary>
        public IEnumerable<long> DescendantIds()
        {
            if (Id.HasValue) yield return Id.Value;
            foreach (var id in Children.SelectMany(c => c.DescendantIds()))
                yield return id;
        }
    }
}
