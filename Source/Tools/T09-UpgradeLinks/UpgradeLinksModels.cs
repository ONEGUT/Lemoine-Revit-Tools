using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;

namespace LemoineTools.Tools.UpgradeLinks
{
    /// <summary>How a freshly-linked instance is positioned in the host — maps 1:1 to
    /// <see cref="ImportPlacement"/>. Enum tokens are persisted/compared, so they never
    /// get externalized; only the display labels live in <c>upgradeLinks.json</c>.</summary>
    public enum UpgradePlacement { OriginToOrigin, CenterToCenter, SharedCoordinates, Site }

    /// <summary>Where the upgraded copy is written before it is linked.</summary>
    public enum UpgradeDestination { Subfolder, Overwrite, Cloud }

    /// <summary>One queued file, UI-side. <see cref="Version"/> stays "?" until the read-only
    /// scan fills it in; <see cref="Readable"/> false rows are shown greyed and skipped at run.</summary>
    public sealed class UpgradeFileRow
    {
        public string Path { get; set; } = "";
        public string FileName => System.IO.Path.GetFileName(Path);
        public string Folder   => System.IO.Path.GetDirectoryName(Path) ?? "";
        public string Version  { get; set; } = "?";
        public bool   IsWorkshared { get; set; }
        public bool   IsCurrent { get; set; }   // saved-in version already matches this Revit
        public bool   Readable  { get; set; } = true;
        public bool   Scanned   { get; set; }
        public UpgradePlacement Placement { get; set; } = UpgradePlacement.OriginToOrigin;
    }

    /// <summary>Result of the read-only <see cref="BasicFileInfo"/> scan for one path.</summary>
    public sealed class UpgradeFileScan
    {
        public string  Path { get; set; } = "";
        public string  Version { get; set; } = "?";
        public bool    IsWorkshared { get; set; }
        public bool    IsCurrent { get; set; }
        public bool    Readable { get; set; } = true;
        public string? Error { get; set; }
    }

    /// <summary>One file to process: source path + its chosen placement.</summary>
    public sealed class UpgradeFileItem
    {
        public string Path { get; set; } = "";
        public UpgradePlacement Placement { get; set; } = UpgradePlacement.OriginToOrigin;
    }

    /// <summary>Everything the run handler needs — the ordered files, the destination choice and
    /// its parameters, and the open/link toggles.</summary>
    public sealed class UpgradeLinksSpec
    {
        public List<UpgradeFileItem> Files { get; set; } = new List<UpgradeFileItem>();
        public UpgradeDestination Destination { get; set; } = UpgradeDestination.Subfolder;
        public string SubfolderName { get; set; } = "Upgraded Links";
        public bool AuditOnOpen    { get; set; }
        public bool ReloadExisting { get; set; } = true;

        // Cloud (phase 2) — the run handler's cloud branch only fires when CloudReady is true.
        // The Revit API exposes no way to browse ACC folders or read the host's containing folder
        // id, so the command leaves this false until an APS/Forge integration supplies the ids.
        public bool CloudReady     { get; set; }
        public Guid CloudAccountId { get; set; }
        public Guid CloudProjectId { get; set; }
        public string CloudFolderId { get; set; } = "";
    }

    public static class UpgradePlacementMap
    {
        public static ImportPlacement ToImportPlacement(UpgradePlacement p)
        {
            switch (p)
            {
                case UpgradePlacement.CenterToCenter:    return ImportPlacement.Centered;
                case UpgradePlacement.SharedCoordinates: return ImportPlacement.Shared;
                case UpgradePlacement.Site:              return ImportPlacement.Site;
                default:                                 return ImportPlacement.Origin;
            }
        }
    }
}
