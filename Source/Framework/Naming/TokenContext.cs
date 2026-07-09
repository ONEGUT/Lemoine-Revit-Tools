using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LemoineTools.Framework.Naming
{
    /// <summary>
    /// Everything <see cref="TokenResolver"/> needs to resolve one item's name.
    /// Revit references are optional so a settings-page or chip-row preview can run
    /// doc-free with only <see cref="Computed"/> values supplied.
    /// </summary>
    public sealed class TokenContext
    {
        /// <summary>Document the target/source elements belong to. Needed for project-info tokens.</summary>
        public Document? Doc { get; set; }

        /// <summary>The element being named (sheet, view, scope box…).</summary>
        public Element? Target { get; set; }

        /// <summary>The parent/source element, when the tool has one (parent view, source view,
        /// link instance…). Null when the tool has no such concept — Source-subject tokens then
        /// simply aren't offered by <see cref="NamingTokenRegistry.TokensFor"/>.</summary>
        public Element? Source { get; set; }

        /// <summary>Per-item values the calling tool computes itself (Level Range, Counter, Trade
        /// label, a just-allocated Sheet Number, a friendlier ViewType label…). An entry here
        /// OVERRIDES any built-in resolution for the same key — this is how a tool-specific
        /// ViewType label or a freshly-minted SheetNumber wins over the generic element read.</summary>
        public Dictionary<string, string> Computed { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
