using System;

namespace LemoineTools.Framework.Naming
{
    /// <summary>Which kind of thing a token applies to. A tool declares the entity it
    /// names; the registry only offers tokens valid for that entity. <see cref="Any"/>
    /// covers date/project/sequence tokens that are valid everywhere.</summary>
    public enum TokenEntity { Sheet, View, Any }

    /// <summary>Where a token's value comes from.</summary>
    public enum TokenOrigin
    {
        /// <summary>Resolved by <see cref="TokenResolver"/> itself (dates, project info, element fields).</summary>
        BuiltIn,
        /// <summary>Value supplied per-item by the running tool (Level Range, Seq, Trade…).</summary>
        Computed,
        /// <summary>User-defined: reads a Revit parameter off the subject element.</summary>
        UserParameter,
    }

    /// <summary>Which element a token reads from, when a context has both a target
    /// (the thing being named) and a source (parent view / source view / link).</summary>
    public enum TokenSubject { Target, Source, ProjectInfo, Environment }

    /// <summary>
    /// One naming token. Immutable for built-ins and tool-computed tokens; user
    /// tokens are materialized from <see cref="UserTokenStore"/> DTOs.
    /// </summary>
    public sealed class TokenDefinition
    {
        /// <summary>Pattern text without braces, e.g. <c>"SheetNumber"</c> or <c>"u:SheetSeries"</c>.
        /// User-token keys always carry the <c>"u:"</c> namespace prefix so a future
        /// built-in can never collide with a user token.</summary>
        public string Key { get; }

        /// <summary>Chip/dropdown display text.</summary>
        public string Label { get; }

        public TokenOrigin  Origin  { get; }
        public TokenSubject Subject { get; }

        /// <summary>Which entity's pickers list this token. <see cref="TokenEntity.Any"/> = all.</summary>
        public TokenEntity Entity { get; }

        /// <summary>Settings-page / chip-tooltip help line. May be null.</summary>
        public string? Description { get; }

        // ── UserParameter-only ───────────────────────────────────────────────
        /// <summary>Parameter display name — used for lookup fallback and shown in the UI.</summary>
        public string? ParameterName { get; }

        /// <summary>Shared-parameter GUID — authoritative when set (name lookup can pick
        /// the wrong duplicate; see CLAUDE.md "Writing a value to a sheet's shared parameter by name").</summary>
        public Guid? ParameterGuid { get; }

        /// <summary>Substituted when the parameter is missing or resolves empty.</summary>
        public string FallbackText { get; }

        public TokenDefinition(
            string key, string label, TokenOrigin origin, TokenSubject subject, TokenEntity entity,
            string? description = null, string? parameterName = null, Guid? parameterGuid = null,
            string fallbackText = "")
        {
            Key           = key ?? throw new ArgumentNullException(nameof(key));
            Label         = label ?? key;
            Origin        = origin;
            Subject       = subject;
            Entity        = entity;
            Description   = description;
            ParameterName = parameterName;
            ParameterGuid = parameterGuid;
            FallbackText  = fallbackText ?? "";
        }

        /// <summary>Pattern-ready token text, e.g. <c>"{SheetNumber}"</c>.</summary>
        public string Braced => "{" + Key + "}";
    }
}
