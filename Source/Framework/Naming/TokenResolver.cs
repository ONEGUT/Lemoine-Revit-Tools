using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace LemoineTools.Framework.Naming
{
    /// <summary>
    /// Single resolution engine for every naming-token pattern in the plugin. Replaces
    /// the per-tool <c>TokenInput.Resolve(dict)</c> / hand-rolled <c>string.Replace</c>
    /// callers with one implementation that: resolves built-ins, resolves user-defined
    /// parameter tokens (GUID-first), reports every miss instead of going silent, and
    /// centralizes the degenerate-name guard that used to live only in Bulk Export.
    /// </summary>
    public static class TokenResolver
    {
        // Matches "{Key}" or "{u:Key}" — never a bare '{' or '}' from unrelated text.
        private static readonly Regex TokenPattern =
            new Regex(@"\{(?:u:)?[A-Za-z][A-Za-z0-9_]*\}", RegexOptions.Compiled);

        /// <summary>
        /// Resolves every <c>{Token}</c> in <paramref name="pattern"/> against <paramref name="ctx"/>.
        /// Single regex pass — never sequential <c>string.Replace</c>, so a resolved VALUE that
        /// happens to contain brace text is never re-substituted. An unknown token is left as
        /// literal text and reported once via <paramref name="onWarn"/>.
        /// </summary>
        public static string Resolve(string pattern, TokenContext ctx, Action<string>? onWarn = null)
        {
            if (string.IsNullOrEmpty(pattern)) return pattern ?? "";
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));

            var warned = new HashSet<string>(StringComparer.Ordinal);

            return TokenPattern.Replace(pattern, m =>
            {
                string full = m.Value;
                string key  = full.Substring(1, full.Length - 2);
                string? replacement = ResolveToken(key, ctx, onWarn, warned);
                return replacement ?? full; // unknown token -> keep literal text
            });
        }

        /// <summary>
        /// Post-guard for a resolved name: a result with no usable character is a FAILURE, not a
        /// fallback. Reports it (onWarn + DiagnosticsLog.Warn) and substitutes a deterministic
        /// name — <c>ctx.Target.Name</c>, else <c>"item-" + ctx.Target.Id</c>, else the caller's
        /// own <paramref name="fallback"/> (used when no Target element exists yet at resolution
        /// time, e.g. a sheet name resolved before <c>ViewSheet.Create</c>).
        /// </summary>
        public static string GuardDegenerate(string resolved, TokenContext ctx, string fallback, Action<string>? onWarn)
        {
            if (HasAlnum(resolved)) return resolved;

            string chosen;
            string? targetName = ctx?.Target?.Name;
            if (HasAlnum(targetName)) chosen = targetName!;
            else if (ctx?.Target != null) chosen = "item-" + ctx.Target.Id.Value;
            else if (HasAlnum(fallback)) chosen = fallback;
            else chosen = "item-0";

            string msg = AppStrings.T("naming.resolver.degenerate", resolved ?? "", chosen);
            onWarn?.Invoke(msg);
            DiagnosticsLog.Warn("TokenResolver.GuardDegenerate", msg);
            return chosen;
        }

        /// <summary>Replaces filesystem-illegal characters with '_' and trims. Falls back to
        /// "name" only if sanitizing somehow still leaves nothing (callers normally run
        /// <see cref="GuardDegenerate"/> first, which already guarantees an alnum result).</summary>
        public static string SanitizeFilename(string name)
        {
            name ??= "";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim();
            return name.Length > 0 ? name : "name";
        }

        private static bool HasAlnum(string? s) => !string.IsNullOrEmpty(s) && s.Any(char.IsLetterOrDigit);

        // ── Per-token resolution ─────────────────────────────────────────────────

        // Returns null only to signal "unknown token — keep the literal braced text".
        // Every known-token path returns a string (possibly "").
        private static string? ResolveToken(string key, TokenContext ctx, Action<string>? onWarn, HashSet<string> warned)
        {
            if (ctx.Computed.TryGetValue(key, out var computed))
                return computed ?? "";

            var def = NamingTokenRegistry.Find(key);
            if (def == null)
            {
                WarnOnce(onWarn, warned, key, AppStrings.T("naming.resolver.unknownToken", "{" + key + "}"));
                return null;
            }

            if (def.Origin == TokenOrigin.UserParameter)
                return ResolveUserParameter(def, ctx, onWarn, warned);

            if (def.Origin == TokenOrigin.Computed)
            {
                // The tool declared this Computed token in its vocabulary but didn't supply a
                // value for this item — a tool-wiring issue, not a user-actionable data gap, so
                // it goes to diagnostics only (not the run's Output log).
                DiagnosticsLog.Warn("TokenResolver", $"Computed token '{def.Key}' had no value supplied");
                return def.FallbackText;
            }

            return ResolveBuiltIn(def, ctx) ?? "";
        }

        private static string? ResolveBuiltIn(TokenDefinition def, TokenContext ctx)
        {
            try
            {
                switch (def.Key)
                {
                    case "SheetNumber":   return (ctx.Target as ViewSheet)?.SheetNumber;
                    case "SheetName":     return ctx.Target?.Name;
                    case "Revision":      return ctx.Target?.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION)?.AsString();
                    case "IssueDate":     return ctx.Target?.get_Parameter(BuiltInParameter.SHEET_ISSUE_DATE)?.AsString();

                    case "ViewName":      return ctx.Target?.Name;
                    case "ViewType":      return (ctx.Target as View)?.ViewType.ToString();
                    case "Level":         return (ctx.Target as View)?.GenLevel?.Name;

                    case "CurrentName":   return ctx.Target?.Name;

                    case "SourceViewName":
                    case "ParentViewName": return ctx.Source?.Name;
                    case "TemplateName":   return ctx.Source?.Name;
                    case "LinkName":       return ctx.Source?.Name;

                    case "ProjectNumber": return ctx.Doc?.ProjectInformation?.get_Parameter(BuiltInParameter.PROJECT_NUMBER)?.AsString();
                    case "ProjectName":   return ctx.Doc?.ProjectInformation?.get_Parameter(BuiltInParameter.PROJECT_NAME)?.AsString();

                    case "Year":  return DateTime.Now.ToString("yyyy");
                    case "Month": return DateTime.Now.ToString("MM");
                    case "Day":   return DateTime.Now.ToString("dd");

                    default: return null;
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"TokenResolver: resolve built-in '{def.Key}'", ex);
                return null;
            }
        }

        // GUID-first parameter read (see CLAUDE.md "Writing a value to a sheet's shared parameter
        // by name is unreliable"): bind by ParameterGuid when set, else GetParameters(name)
        // preferring a String-storage match — never a bare LookupParameter, which silently picks
        // the wrong duplicate.
        private static string ResolveUserParameter(TokenDefinition def, TokenContext ctx, Action<string>? onWarn, HashSet<string> warned)
        {
            Element? subject = def.Subject switch
            {
                TokenSubject.Source      => ctx.Source,
                TokenSubject.ProjectInfo => ctx.Doc?.ProjectInformation,
                _                        => ctx.Target,
            };

            if (subject == null)
            {
                WarnOnce(onWarn, warned, def.Key,
                    AppStrings.T("naming.resolver.missingSubject", def.Braced, def.Label));
                return def.FallbackText;
            }

            try
            {
                Parameter? p = null;
                if (def.ParameterGuid.HasValue)
                {
                    p = subject.get_Parameter(def.ParameterGuid.Value);
                }
                else if (!string.IsNullOrEmpty(def.ParameterName))
                {
                    var matches = subject.GetParameters(def.ParameterName);
                    p = matches.FirstOrDefault(x => x.StorageType == StorageType.String) ?? matches.FirstOrDefault();
                }

                if (p == null || !p.HasValue)
                {
                    WarnOnce(onWarn, warned, def.Key,
                        AppStrings.T("naming.resolver.missingParam", def.Braced, def.ParameterName ?? "", subject.Name ?? "", def.FallbackText));
                    return def.FallbackText;
                }

                string? value = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
                if (string.IsNullOrEmpty(value))
                {
                    WarnOnce(onWarn, warned, def.Key,
                        AppStrings.T("naming.resolver.missingParam", def.Braced, def.ParameterName ?? "", subject.Name ?? "", def.FallbackText));
                    return def.FallbackText;
                }
                return value;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"TokenResolver: resolve user token '{def.Key}'", ex);
                WarnOnce(onWarn, warned, def.Key,
                    AppStrings.T("naming.resolver.missingParam", def.Braced, def.ParameterName ?? "", subject.Name ?? "", def.FallbackText));
                return def.FallbackText;
            }
        }

        private static void WarnOnce(Action<string>? onWarn, HashSet<string> warned, string key, string message)
        {
            if (!warned.Add(key)) return;
            onWarn?.Invoke(message);
            DiagnosticsLog.Warn("TokenResolver", message);
        }
    }
}
