using System.Collections.Generic;
using System.Linq;

namespace LemoineTools.Framework.Naming
{
    /// <summary>
    /// Static catalog of every naming token — built-ins declared here, user tokens
    /// layered in from <see cref="UserTokenStore"/>. Tools query <see cref="TokensFor"/>
    /// so a picker only ever offers tokens valid for the entity it names (built-ins
    /// filtered by entity/subject, plus the tool's own <c>Computed</c> tokens, plus
    /// every matching user token).
    /// </summary>
    public static class NamingTokenRegistry
    {
        /// <summary>All built-in token definitions (fixed at compile time). Labels are resolved
        /// fresh on every access (not cached) so a language change picks them up in windows
        /// opened afterward, the same convention as every other AppStrings.T call site.</summary>
        public static IReadOnlyList<TokenDefinition> BuiltIns => BuildBuiltIns();

        private static List<TokenDefinition> BuildBuiltIns()
        {
            string T(string key) => AppStrings.T("naming.tokens." + key + ".label");
            string D(string key) => AppStrings.T("naming.tokens." + key + ".desc");

            return new List<TokenDefinition>
            {
                new TokenDefinition("SheetNumber",  T("sheetNumber"),  TokenOrigin.BuiltIn, TokenSubject.Target,      TokenEntity.Sheet, D("sheetNumber")),
                new TokenDefinition("SheetName",    T("sheetName"),    TokenOrigin.BuiltIn, TokenSubject.Target,      TokenEntity.Sheet, D("sheetName")),
                new TokenDefinition("Revision",     T("revision"),     TokenOrigin.BuiltIn, TokenSubject.Target,      TokenEntity.Sheet, D("revision")),
                new TokenDefinition("IssueDate",    T("issueDate"),    TokenOrigin.BuiltIn, TokenSubject.Target,      TokenEntity.Sheet, D("issueDate")),

                new TokenDefinition("ViewName",     T("viewName"),     TokenOrigin.BuiltIn, TokenSubject.Target,      TokenEntity.View,  D("viewName")),
                new TokenDefinition("ViewType",     T("viewType"),     TokenOrigin.BuiltIn, TokenSubject.Target,      TokenEntity.View,  D("viewType")),
                new TokenDefinition("Level",        T("level"),        TokenOrigin.BuiltIn, TokenSubject.Target,      TokenEntity.View,  D("level")),

                new TokenDefinition("CurrentName",  T("currentName"),  TokenOrigin.BuiltIn, TokenSubject.Target,      TokenEntity.Any,   D("currentName")),

                new TokenDefinition("SourceViewName", T("sourceViewName"), TokenOrigin.BuiltIn, TokenSubject.Source,  TokenEntity.View,  D("sourceViewName")),
                new TokenDefinition("ParentViewName", T("parentViewName"), TokenOrigin.BuiltIn, TokenSubject.Source,  TokenEntity.View,  D("parentViewName")),
                new TokenDefinition("TemplateName",   T("templateName"),   TokenOrigin.BuiltIn, TokenSubject.Source,  TokenEntity.View,  D("templateName")),
                new TokenDefinition("LinkName",       T("linkName"),       TokenOrigin.BuiltIn, TokenSubject.Source,  TokenEntity.Any,   D("linkName")),

                new TokenDefinition("ProjectNumber", T("projectNumber"), TokenOrigin.BuiltIn, TokenSubject.ProjectInfo, TokenEntity.Any, D("projectNumber")),
                new TokenDefinition("ProjectName",   T("projectName"),   TokenOrigin.BuiltIn, TokenSubject.ProjectInfo, TokenEntity.Any, D("projectName")),

                new TokenDefinition("Year",  T("year"),  TokenOrigin.BuiltIn, TokenSubject.Environment, TokenEntity.Any, D("year")),
                new TokenDefinition("Month", T("month"), TokenOrigin.BuiltIn, TokenSubject.Environment, TokenEntity.Any, D("month")),
                new TokenDefinition("Day",   T("day"),   TokenOrigin.BuiltIn, TokenSubject.Environment, TokenEntity.Any, D("day")),

                // Computed: value always supplied by the calling tool via TokenContext.Computed —
                // kept in the global list only because it is common to several tools (counters).
                new TokenDefinition("Seq", T("seq"), TokenOrigin.Computed, TokenSubject.Environment, TokenEntity.Any, D("seq")),
            };
        }

        /// <summary>
        /// Tokens to offer a picker naming <paramref name="entity"/>. Includes matching
        /// built-ins (entity match or <see cref="TokenEntity.Any"/>), <paramref name="extraComputed"/>
        /// (the calling tool's own Computed tokens), and every user token whose entity matches.
        /// Source-subject tokens are only included when <paramref name="hasSource"/> is true, so a
        /// tool with no parent/source element never offers a token it cannot resolve.
        /// </summary>
        public static List<TokenDefinition> TokensFor(
            TokenEntity entity, bool hasSource, IReadOnlyList<TokenDefinition>? extraComputed = null)
        {
            bool EntityMatches(TokenEntity e) => e == TokenEntity.Any || e == entity;
            bool SubjectAllowed(TokenSubject s) => s != TokenSubject.Source || hasSource;

            var result = new List<TokenDefinition>();

            result.AddRange(BuiltIns.Where(t => EntityMatches(t.Entity) && SubjectAllowed(t.Subject)));

            if (extraComputed != null)
                result.AddRange(extraComputed.Where(t => EntityMatches(t.Entity) && SubjectAllowed(t.Subject)));

            result.AddRange(UserTokenStore.Instance.Tokens
                .Where(t => EntityMatches(t.Entity) && SubjectAllowed(t.Subject)));

            return result;
        }

        /// <summary>Finds any token (built-in, tool-computed, or user) by its bare key
        /// (without braces). Returns null when unknown.</summary>
        public static TokenDefinition? Find(string key, IReadOnlyList<TokenDefinition>? extraComputed = null)
        {
            if (string.IsNullOrEmpty(key)) return null;

            var b = BuiltIns.FirstOrDefault(t => t.Key == key);
            if (b != null) return b;

            if (extraComputed != null)
            {
                var c = extraComputed.FirstOrDefault(t => t.Key == key);
                if (c != null) return c;
            }

            return UserTokenStore.Instance.Tokens.FirstOrDefault(t => t.Key == key);
        }
    }
}
