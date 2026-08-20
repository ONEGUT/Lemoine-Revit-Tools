using System;
using System.Collections.Generic;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Setup
{
    /// <summary>Where a listed cloud model came from. Tokens drive grouping only.</summary>
    public enum CloudModelSource
    {
        /// <summary>A cloud model currently open in this Revit session.</summary>
        OpenDocument,
        /// <summary>A cloud model already linked into the host (or into an open document).</summary>
        ExistingLink,
        /// <summary>Identified by GUIDs the user typed in.</summary>
        Manual,
    }

    /// <summary>
    /// One cloud model that can be used as a replacement, plus everything the run needs to reach
    /// it again — **without** any internal Revit API.
    ///
    /// <para>Two routes exist because Revit 2024 exposes two different public doors:</para>
    /// <list type="bullet">
    /// <item><b>GUID route</b> — <see cref="Region"/> + <see cref="ProjectGuid"/> +
    /// <see cref="ModelGuid"/> rebuild a cloud <c>ModelPath</c> through the public
    /// <c>ModelPathUtils.ConvertCloudGUIDsToCloudPath</c>.</item>
    /// <item><b>Source-link route</b> — <see cref="SourceTypeId"/> names a
    /// <c>RevitLinkType</c> already in the document whose <c>ExternalResourceReference</c> the
    /// run re-reads. Used when the model is only known as an existing link, because
    /// <c>ExternalResourceReference.CreateFromCloudPath</c> is internal and a reference cannot
    /// be manufactured from a path.</item>
    /// </list>
    ///
    /// <para>Revit objects are never held here — the picker window lives on its own STA thread,
    /// so only GUIDs, strings and element-id values cross over.</para>
    /// </summary>
    public sealed class CloudModelItem
    {
        public string           Name   { get; set; } = "";
        public CloudModelSource Source { get; set; } = CloudModelSource.Manual;

        // ── GUID route ────────────────────────────────────────────────────────
        public string Region      { get; set; } = "";
        public Guid   ProjectGuid { get; set; }
        public Guid   ModelGuid   { get; set; }

        /// <summary>True when the GUID route can be taken.</summary>
        public bool HasGuids => ProjectGuid != Guid.Empty && ModelGuid != Guid.Empty;

        // ── Source-link route ─────────────────────────────────────────────────
        /// <summary>ElementId value of a <c>RevitLinkType</c> whose cloud reference this model
        /// IS. 0 when there is none.</summary>
        public long SourceTypeId { get; set; }

        /// <summary>Free-text note about where this entry came from, shown under the row.</summary>
        public string Detail { get; set; } = "";

        /// <summary>Whether the model is workshared — <c>null</c> when it could not be read.
        /// Only an OPEN document exposes this publicly (<c>Document.IsWorkshared</c>); for a
        /// link or a typed GUID it is genuinely unknown, and a badge guessed from "cloud models
        /// are usually workshared" would read as fact. Null means: show nothing.</summary>
        public bool? IsWorkshared { get; set; }

        public bool IsUsable => HasGuids || SourceTypeId != 0;
    }

    /// <summary>
    /// What <see cref="CloudBrowseHandler"/> found, as a <see cref="BrowserTree"/> the existing
    /// <c>BrowserTreePicker</c> renders, plus the map from each leaf's synthetic id to the model.
    ///
    /// <para>The synthetic ids exist because <c>BrowserNode.Id</c> is a <c>long</c> (an ElementId
    /// value for view/sheet trees) while a cloud model is identified by GUIDs. Reusing the
    /// control beats re-rolling a second tree picker, so the index IS the id.</para>
    /// </summary>
    public sealed class CloudScanResult
    {
        public BrowserTree Tree { get; set; } = new BrowserTree();
        public Dictionary<long, CloudModelItem> Models { get; set; } = new Dictionary<long, CloudModelItem>();

        public int OpenCount { get; set; }
        public int LinkCount { get; set; }
        public int Total => OpenCount + LinkCount;
    }
}
