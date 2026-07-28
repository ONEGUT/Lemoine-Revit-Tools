using System.Collections.Generic;

namespace LemoineTools.Tools.Setup
{
    /// <summary>Where the new model is seated after the link type has been re-pointed at it.
    /// Enum tokens are persisted/compared, so they are never externalized; only the display
    /// labels live in <c>replaceLink.json</c>.
    ///
    /// <para>Reloading a <see cref="Autodesk.Revit.DB.RevitLinkType"/> keeps every instance's
    /// transform, so <see cref="KeepPlacement"/> is exact whenever the new file shares the old
    /// file's internal origin (a re-issue of the same model). When it does not, the transform is
    /// still preserved but the geometry lands somewhere else — the two re-seat modes translate
    /// each instance so the new model's own base point returns to where the OLD link's same base
    /// point sat, measured in host coordinates before the swap.</para></summary>
    public enum ReplacePosition { KeepPlacement, ProjectBasePoint, SurveyPoint }

    /// <summary>Where the upgraded replacement file is written before the link type is
    /// re-pointed at it. Enum tokens are persisted/compared — never externalized.</summary>
    public enum ReplaceDestination
    {
        /// <summary>Save over the file the link currently points at — same folder, same name,
        /// so the link's path, path type and displayed name are unchanged by construction.</summary>
        OverwriteLinkedFile,
        /// <summary>Save into the linked file's own folder under a new name, then
        /// <c>LoadFrom</c> the existing type at it.</summary>
        RenameBesideIt,
        /// <summary>Save into a folder the user picks, then <c>LoadFrom</c>.</summary>
        SelectedFolder,
    }

    /// <summary>One link in the host model, as offered by the "existing link" picker. Captured
    /// read-only on the Revit main thread by <see cref="ReplaceLinkCapture"/>.</summary>
    public sealed class HostLinkInfo
    {
        public long   TypeId        { get; set; }
        public string Name          { get; set; } = "";
        /// <summary>User-visible absolute path of the file the link points at. Empty for a
        /// cloud-hosted link (which has no local path to overwrite).</summary>
        public string Path          { get; set; } = "";
        public bool   IsCloud       { get; set; }
        public bool   IsLoaded      { get; set; }
        public int    InstanceCount { get; set; }
        public string Status        { get; set; } = "";

        /// <summary>False when this link cannot be replaced in place at all (cloud-hosted, no
        /// resolvable path, or no placed instance). Such links are still LISTED, with
        /// <see cref="BlockedReason"/> shown, so the user sees why rather than facing a
        /// silently shorter list.</summary>
        public bool    Replaceable   { get; set; } = true;
        public string? BlockedReason { get; set; }

        /// <summary>Set when one of this link's fields could not be read, naming which. The row
        /// is still shown (with whatever resolved), but a placeholder value must never pass for a
        /// real one — the picker prints this so a partial read is visible rather than silent.</summary>
        public string? ReadWarning   { get; set; }
    }

    /// <summary>One queued replacement, UI-side: which link is being replaced and with what.
    /// The new file's version fields stay unset until the read-only
    /// <see cref="UpgradeLinksScanHandler"/> pass fills them in.</summary>
    public sealed class ReplaceRow
    {
        /// <summary>ElementId value of the target <see cref="Autodesk.Revit.DB.RevitLinkType"/>.
        /// 0 = nothing picked yet.</summary>
        public long   TypeId   { get; set; }
        public string LinkName { get; set; } = "";
        public string LinkPath { get; set; } = "";

        /// <summary>Absolute path of the replacement file. Empty until the user browses.</summary>
        public string NewFilePath { get; set; } = "";

        /// <summary>Save-as base name (no extension) for the two non-overwrite destinations.
        /// Ignored by <see cref="ReplaceDestination.OverwriteLinkedFile"/>, which always writes
        /// back to the linked file's own path.</summary>
        public string SaveAsName { get; set; } = "";

        // Filled by the version scan of NewFilePath.
        public string Version         { get; set; } = "?";
        public bool   IsWorkshared    { get; set; }
        public bool   IsCurrent       { get; set; }
        public bool   IsFutureVersion { get; set; }
        public bool   Readable        { get; set; } = true;
        public bool   Scanned         { get; set; }
    }

    /// <summary>One replacement to process, run-side.</summary>
    public sealed class ReplaceItem
    {
        public long   TypeId      { get; set; }
        public string LinkName    { get; set; } = "";
        public string NewFilePath { get; set; } = "";
        public string SaveAsName  { get; set; } = "";
    }

    /// <summary>Everything <see cref="ReplaceLinkRunHandler"/> needs for one run.</summary>
    public sealed class ReplaceLinkSpec
    {
        public List<ReplaceItem>   Items          { get; set; } = new List<ReplaceItem>();
        public ReplaceDestination  Destination    { get; set; } = ReplaceDestination.OverwriteLinkedFile;
        public ReplacePosition     Position       { get; set; } = ReplacePosition.KeepPlacement;
        /// <summary>Absolute folder path — <see cref="ReplaceDestination.SelectedFolder"/> only.</summary>
        public string              SelectedFolder { get; set; } = "";
        /// <summary>Copy the file being replaced into a <c>_Superseded</c> sibling folder,
        /// timestamped, before it is overwritten. The only undo there is.</summary>
        public bool                BackupOriginal { get; set; } = true;
        public bool                AuditOnOpen    { get; set; }
        /// <summary>Measure and log how far each instance's geometry actually moved.</summary>
        public bool                ReportMovement { get; set; } = true;
    }
}
