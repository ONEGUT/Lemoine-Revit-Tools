using System;
using System.Xml.Serialization;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Dimensioning
{
    /// <summary>
    /// A saved, named clash definition: two element groups (Group 1 vs Group 2) plus the
    /// marking settings used to draw their clashes. Persisted in the Clash Definitions
    /// library and consumed by the Clash Finder wizard.
    ///
    /// Public + parameterless ctor so <see cref="XmlSerializer"/> can round-trip it
    /// (an internal root type throws "only public types can be processed").
    /// </summary>
    public sealed class ClashDefinition
    {
        /// <summary>Stable identifier (used as the row key in the library UI and for selection).</summary>
        [XmlAttribute] public string Id   { get; set; } = "";

        /// <summary>Human-readable name shown in the sidebar and the finder's picker.</summary>
        [XmlAttribute] public string Name { get; set; } = "New Definition";

        /// <summary>Group 1 — the SOURCE elements whose clashes get coloured/annotated.</summary>
        public ClashGroupSpec Group1 { get; set; } = new ClashGroupSpec();

        /// <summary>Group 2 — the TARGET elements tested against Group 1.</summary>
        public ClashGroupSpec Group2 { get; set; } = new ClashGroupSpec();

        // ── Marking settings (lifted from ClashDimensionSettings) ─────────────
        [XmlAttribute] public double ToleranceMm       { get; set; } = 25.4;
        [XmlAttribute] public string FillStyle         { get; set; } = "Solid";   // "Solid" | "Outline"
        [XmlAttribute] public string FallbackColorHex  { get; set; } = "#FF00FF";
        [XmlAttribute] public string CrossLineTypeName { get; set; } = "";
        [XmlAttribute] public string DimTarget         { get; set; } = "Edge";    // "Edge" | "Centre"
        [XmlAttribute] public bool   ClearPrevious     { get; set; } = true;
        [XmlAttribute] public int    MaxClashes        { get; set; } = 500;

        // ── Phase filtering ────────────────────────────────────────────────────
        /// <summary>"All" (no phase filtering — default, existing definitions unchanged),
        /// "MatchView" (a clash marks in a view only when both elements exist in that view's
        /// phase), or "Specific" (detection culled to <see cref="SpecificPhaseName"/>).</summary>
        [XmlAttribute] public string PhaseMode { get; set; } = "All";

        /// <summary>Host phase name used when <see cref="PhaseMode"/> is "Specific".</summary>
        [XmlAttribute] public string SpecificPhaseName { get; set; } = "";

        /// <summary>Creates a blank definition with a time-derived id ready for editing.</summary>
        public static ClashDefinition NewBlank() => new ClashDefinition
        {
            Id   = "C" + Guid.NewGuid().ToString("N").Substring(0, 7),
            Name = LemoineStrings.T("clashDefinitions.defaults.newName"),
        };
    }
}
