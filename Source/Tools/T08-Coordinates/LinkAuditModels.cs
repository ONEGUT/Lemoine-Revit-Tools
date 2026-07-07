using System.Collections.Generic;

namespace LemoineTools.Tools.Coordinates
{
    /// <summary>One loaded (or not) Revit link instance, as reported by Link Audit.</summary>
    public sealed class LinkAuditRow
    {
        public string Name           { get; set; } = "";
        public long   LinkInstId     { get; set; }
        public string LoadStatus     { get; set; } = "";   // Loaded / Unloaded / Not Found / Hidden / Unknown
        public string Positioning    { get; set; } = "";   // best-effort — see LinkAuditCapture.DetectPositioning
        public bool   Pinned         { get; set; }
        public string WorksetName    { get; set; } = "";   // "—" when the host is not workshared
        public string DisplayMode    { get; set; } = "";   // "By Host View" / a custom LinkVisibility name / "n/a"
        public string AttachmentType { get; set; } = "";   // "Attachment" / "Overlay" / "Unknown"
        public string LastSaved      { get; set; } = "";   // local last-write time, "n/a (cloud)", or "n/a"

        /// <summary>True when this row is worth calling out — not loaded, or display isn't cascading from the host view.</summary>
        public bool IsWarning { get; set; }
    }

    /// <summary>Revit-thread snapshot handed to <see cref="LinkAuditViewModel"/>.</summary>
    public sealed class LinkAuditData
    {
        public List<LinkAuditRow> Rows { get; set; } = new List<LinkAuditRow>();
    }
}
