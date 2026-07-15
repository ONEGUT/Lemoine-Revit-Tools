using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;

namespace LemoineTools.Tools.Setup
{
    /// <summary>How a freshly-linked instance is positioned in the host. Enum tokens are
    /// persisted/compared, so they never get externalized; only the display labels live in
    /// <c>upgradeLinks.json</c>. <see cref="RevitLinkInstance.Create"/> only accepts
    /// <see cref="ImportPlacement.Origin"/> and <see cref="ImportPlacement.Shared"/> — the Centered
    /// / Site placements throw "Placement isn't supported." So every mode links at Origin and the
    /// run handler then translates: <see cref="InternalOrigin"/> stays put, <see cref="ProjectBasePoint"/>
    /// / <see cref="SurveyPoint"/> move so the link's own base point lands on the host's, and
    /// <see cref="CenterToCenter"/> moves so the models' extents centers coincide
    /// (see UpgradeLinksRunHandler.ApplyPlacement).</summary>
    public enum UpgradePlacement { InternalOrigin, ProjectBasePoint, CenterToCenter, SurveyPoint }

    /// <summary>Where the upgraded copy is written before it is linked. The UI groups the first
    /// two under a "Local" top-level choice and the third under "Cloud" (shown only when the
    /// host is itself a cloud model). Enum tokens are persisted/compared — never externalized.</summary>
    public enum UpgradeDestination { CurrentLocation, SelectedFolder, Cloud }

    /// <summary>One queued file, UI-side. <see cref="Version"/> stays "?" until the read-only
    /// scan fills it in; <see cref="Readable"/> false rows are shown greyed and skipped at run.</summary>
    public sealed class UpgradeFileRow
    {
        public string Path { get; set; } = "";
        public string Folder   => System.IO.Path.GetDirectoryName(Path) ?? "";
        public string Version  { get; set; } = "?";
        public bool   IsWorkshared { get; set; }
        public bool   IsCurrent { get; set; }   // saved-in version already matches this Revit
        /// <summary>Saved-in version is a LATER Revit release than this one — Revit cannot open
        /// it (not backwards compatible), so the row is treated like an unreadable row.</summary>
        public bool   IsFutureVersion { get; set; }
        public bool   Readable  { get; set; } = true;
        public bool   Scanned   { get; set; }
        public UpgradePlacement Placement { get; set; } = UpgradePlacement.InternalOrigin;

        // Editable "save as" base name (no extension). Defaults to the source file's own name.
        // Ignored in CurrentLocation mode, which always saves back to the file's own original path.
        public string SaveAsName { get; set; } = "";
    }

    /// <summary>Result of the read-only <see cref="BasicFileInfo"/> scan for one path.</summary>
    public sealed class UpgradeFileScan
    {
        public string  Path { get; set; } = "";
        public string  Version { get; set; } = "?";
        public bool    IsWorkshared { get; set; }
        public bool    IsCurrent { get; set; }
        public bool    IsFutureVersion { get; set; }
        public bool    Readable { get; set; } = true;
        public string? Error { get; set; }
    }

    /// <summary>One file to process: source path + its chosen placement.</summary>
    public sealed class UpgradeFileItem
    {
        public string Path { get; set; } = "";
        public UpgradePlacement Placement { get; set; } = UpgradePlacement.InternalOrigin;
        public string SaveAsName { get; set; } = "";
    }

    /// <summary>Everything the run handler needs — the ordered files, the destination choice and
    /// its parameters, and the open/link toggles.</summary>
    public sealed class UpgradeLinksSpec
    {
        public List<UpgradeFileItem> Files { get; set; } = new List<UpgradeFileItem>();
        public UpgradeDestination Destination { get; set; } = UpgradeDestination.CurrentLocation;
        // Absolute folder path — SelectedFolder mode only. Files save directly into it (no
        // auto-created subfolder name); the folder is created if it doesn't already exist.
        public string SelectedFolder { get; set; } = "";
        public bool AuditOnOpen    { get; set; }
        public bool ReloadExisting { get; set; } = true;

        // Cloud — only offered when the host itself is a cloud model (Document.IsModelInCloud).
        // No hub/project/folder ids are harvested or guessed: each file's cloud save goes through
        // Revit's own native "Save As Cloud Model" command (UIApplication.PostCommand), which
        // handles ACC sign-in/browsing/folder-picking itself. See UpgradeLinksRunHandler for the
        // per-file open → activate → post native command → wait-for-user flow.
        public bool CloudReady { get; set; }
    }

}
