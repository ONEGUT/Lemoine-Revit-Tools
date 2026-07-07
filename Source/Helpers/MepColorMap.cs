using System.Collections.Generic;
using RevitColor = Autodesk.Revit.DB.Color;
using System;
using LemoineTools.Framework;

namespace LemoineTools.Helpers
{
    /// <summary>
    /// Shared MEP system-type color map.  Each entry maps a system label to a
    /// list of case-insensitive substring keywords and a Revit
    /// <see cref="RevitColor"/>.  Entries are ordered so that more-specific
    /// terms (e.g. "Chilled Water") appear before broader ones (e.g. "Supply")
    /// to prevent false matches.
    /// </summary>
    public static class MepColorMap
    {
        /// <summary>
        /// Ordered list of (SystemLabel, Keywords, Color) tuples used to match
        /// a filter name to a display color.
        /// </summary>
        public static readonly IReadOnlyList<(string Label, string[] Keywords, RevitColor Color)> Entries
            = new List<(string, string[], RevitColor)>
        {
            // Hydronic / chilled first so "Chilled Water" doesn't match "Supply"
            ("Chilled Water",     new[] { "CW", "Chilled Water", "Chilled", "MPIPE_CW" },               new RevitColor(170, 212, 255)),
            ("Hydronic Supply",   new[] { "Hydronic Supply" },                                           new RevitColor(220, 120,  50)),
            ("Hydronic Return",   new[] { "Hydronic Return" },                                           new RevitColor(120, 200, 255)),
            ("Heated Water",      new[] { "HW", "Heated Water", "Hot Water", "MPIPE_HW" },               new RevitColor(255, 170, 191)),
            ("Refrigerant",       new[] { "REFR", "Refrigerant", "Refrigeration", "MPIPE_REFR" },        new RevitColor(255, 170,  85)),
            // Duct systems
            ("Supply Air",        new[] { "_SA", "Supply Air", "Supply Duct", "Supply", "DUCT_SA" },     new RevitColor(  0,  94, 189)),
            ("Return Air",        new[] { "_RA", "Return Air", "Return Duct", "DUCT_RA" },               new RevitColor(255, 170, 170)),
            ("Outside Air",       new[] { "_OUTA", "Outside Air", "Outdoor Air", "DUCT_OUTA" },          new RevitColor(231, 143,  54)),
            ("Exhaust Air",       new[] { "_EXHA", "Exhaust Air", "Exhaust Duct", "DUCT_EXHA" },         new RevitColor(129,  86, 129)),
            ("Insulation",        new[] { "INSUL", "Insulation", "Insulated" },                          new RevitColor(255, 255, 255)),
            ("Fire Dampers",      new[] { "Damper", "Fire Damper", "DUCT_Dampers" },                     new RevitColor(200,  30,  30)),
            ("VAV",               new[] { "VAV", "Variable Air", "DUCT_VAV" },                           new RevitColor(  0, 132, 189)),
            ("Cassette",          new[] { "Cassette", "Fan Coil", "DUCT_Cassette" },                     new RevitColor( 83, 207, 223)),
            ("Equipment",         new[] { "Equip", "Equipment", "DUCT_Equip" },                          new RevitColor(179, 179, 179)),
            // Gases
            ("Med Gas",           new[] { "MED GAS", "Medical Gas", "Med Gas" },                         new RevitColor(  0, 189, 141)),
            ("Natural Gas",       new[] { "NAT GAS", "Natural Gas", "Gas Line" },                        new RevitColor(255, 198,  26)),
            // Piping
            ("Condensate",        new[] { "Condensate", "Cond", "MPIPE_Condensate" },                    new RevitColor(128, 128, 128)),
            ("Dom Water Cold",    new[] { "DWS C", "Cold Water", "Domestic Cold", "PLUMB_DWS C" },       new RevitColor( 63, 179, 217)),
            ("Dom Water Hot",     new[] { "DWS H", "Domestic Hot", "PLUMB_DWS H" },                     new RevitColor(217,  63,  63)),
            ("Dom Water General", new[] { "PLUMB_DWS", "Domestic Water", "Dom Water" },                  new RevitColor(198, 140,  83)),
            ("Grease Waste",      new[] { "Grease Waste", "Grease Sewer", "PLUMB_Grease" },              new RevitColor( 54, 230,   0)),
            // Vent pipe before Sanitary so "Sanitary Vent" lands here
            ("Vent Pipe",         new[] { "Sanitary Vent", "Grease Vent", "Vent Pipe", "PLUMB_VENT", "Vent" }, new RevitColor(148, 168, 87)),
            ("Sanitary",          new[] { "Sanitary Waste", "Sanitary Sewer", "Sanitary", "Sewer",
                                          "Sump Pump", "Sump Discharge", "Sump", "Pump Discharge",
                                          "PLUMB_SS" },                                                   new RevitColor( 31, 129,   0)),
            ("Storm Secondary",   new[] { "RD S", "Storm Secondary", "Secondary Drain",
                                          "Overflow", "Storm Overflow", "PLUMB_RD S" },                   new RevitColor(189, 126, 173)),
            ("Storm Primary",     new[] { "RD P", "Storm Primary", "Primary Drain",
                                          "Storm Drain", "Roof Drain", "PLUMB_RD P" },                    new RevitColor(234, 170, 255)),
            ("Pneumatic",         new[] { "Pneumatic", "Pneumatic Tube", "Tube" },                        new RevitColor(150, 217, 186)),
            // Electrical / fire / structural
            ("Electrical",        new[] { "ELEC", "Electrical", "Conduit", "Lighting" },                 new RevitColor(244, 244,   6)),
            ("Sprinkler",         new[] { "FIRE", "Sprinkler", "Fire Protection" },                       new RevitColor(200,  20,  20)),
            ("Steel",             new[] { "STEEL", "Steel", "Structural Steel" },                         new RevitColor(159,  56,  45)),
            ("Concrete",          new[] { "Concrete", "CONC", "CMU" },                                    new RevitColor(185, 185, 185)),
            ("Wood",              new[] { "Wood", "Lumber", "Timber" },                                   new RevitColor(196, 156, 102)),
        };

        /// <summary>
        /// Looks up the best matching <see cref="RevitColor"/> for
        /// <paramref name="filterName"/> using case-insensitive substring
        /// matching.  Returns <c>null</c> if no match is found.
        /// </summary>
        public static RevitColor? Match(string filterName, out string? matchedLabel)
        {
            matchedLabel = null;
            if (string.IsNullOrWhiteSpace(filterName)) return null;

            string lower = filterName.ToLowerInvariant();
            foreach (var (label, keywords, color) in Entries)
            {
                foreach (string kw in keywords)
                {
                    if (lower.Contains(kw.ToLowerInvariant()))
                    {
                        matchedLabel = label;
                        return color;
                    }
                }
            }
            return null;
        }

        /// <summary>Converts a "#RRGGBB" hex string to a Revit Color, or null if invalid.</summary>
        public static RevitColor? HexToRevitColor(string? hex)
        {
            try
            {
                hex = (hex ?? "").TrimStart('#');
                if (hex.Length == 6 && int.TryParse(hex,
                    System.Globalization.NumberStyles.HexNumber, null, out int v))
                    return new RevitColor((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
            }
            catch (Exception __lex) { DiagnosticsLog.Swallowed("MepColorMap: parse colour hex", __lex); }
            return null;
        }
    }
}
