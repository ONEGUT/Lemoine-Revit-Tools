using System;
using System.Collections.Generic;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Setup
{
    /// <summary>Which fetch <see cref="CloudBrowseHandler"/> should perform. Tokens are compared
    /// in a switch — never externalized.</summary>
    public enum CloudBrowseRequest
    {
        /// <summary>List the signed-in account's hubs, and the projects of the default hub.</summary>
        Hubs,
        /// <summary>List one hub's projects.</summary>
        Projects,
        /// <summary>Enumerate one project's folder/model tree.</summary>
        Tree,
    }

    /// <summary>One Autodesk Docs hub. Revit-free so the picker window can hold it across threads.
    /// Named <c>…Item</c> to stay clear of <c>Autodesk.Revit.DB.ForgeDM.CloudHub</c>.</summary>
    public sealed class CloudHubItem
    {
        public string Id     { get; set; } = "";
        public string Name   { get; set; } = "";
        public string Region { get; set; } = "";
    }

    /// <summary>One Autodesk Docs project.</summary>
    public sealed class CloudProjectItem
    {
        public string Id     { get; set; } = "";
        public string Name   { get; set; } = "";
        public Guid   Guid   { get; set; }
        public string HubId  { get; set; } = "";
        public string Region { get; set; } = "";
    }

    /// <summary>One cloud model, carrying everything the run needs to rebuild its
    /// <c>ModelPath</c> via <c>ModelPathUtils.ConvertCloudGUIDsToCloudPath</c>.</summary>
    public sealed class CloudModelItem
    {
        public string Name         { get; set; } = "";
        public Guid   ModelGuid    { get; set; }
        public Guid   ProjectGuid  { get; set; }
        public string Region       { get; set; } = "";
        public bool   IsWorkshared { get; set; }
        /// <summary>Folder path inside the project, for display only (e.g. "Project Files / 01 — Arch").</summary>
        public string FolderPath   { get; set; } = "";
    }

    /// <summary>Result of a <see cref="CloudBrowseRequest.Tree"/> fetch: a
    /// <see cref="BrowserTree"/> the existing <c>BrowserTreePicker</c> can render, plus the map
    /// from each leaf's synthetic id back to the real model.
    ///
    /// <para>The synthetic ids exist because <c>BrowserNode.Id</c> is a <c>long</c> (an
    /// ElementId value for view/sheet trees) while a cloud model is identified by GUIDs. Reusing
    /// the control beats re-rolling a second tree picker, so the index IS the id and this map is
    /// the way back.</para></summary>
    public sealed class CloudTreeResult
    {
        public BrowserTree Tree { get; set; } = new BrowserTree();
        public Dictionary<long, CloudModelItem> Models { get; set; } = new Dictionary<long, CloudModelItem>();

        public int FolderCount { get; set; }
        public int ModelCount  { get; set; }

        /// <summary>Set when enumeration hit the traversal guard, so the picker can say the tree
        /// is incomplete rather than presenting a silently truncated one.</summary>
        public bool Truncated { get; set; }
    }
}
