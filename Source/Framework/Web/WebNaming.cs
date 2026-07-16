using System;
using System.Collections.Generic;
using System.Linq;
using LemoineTools.Framework.Naming;

namespace LemoineTools.Framework.Web
{
    /// <summary>
    /// Read-model + persistence bridge for the web Naming settings tab (the HTML analogue of
    /// <c>GlobalSettingsWindow.Naming.cs</c>). Builds the token list, built-in reference, and the
    /// full parameter catalog as one payload; the page owns the editor form state and filters the
    /// catalog client-side, round-tripping to C# only to <see cref="Save"/> / <see cref="Delete"/>
    /// (authoritative validation stays here, rule R21). Revit-free: the parameter catalog is the
    /// snapshot captured once on the Revit main thread by OpenSettingsCommand.
    /// </summary>
    public sealed class WebNaming
    {
        private readonly ParameterCatalogSnapshot _snapshot;

        public WebNaming(ParameterCatalogSnapshot? snapshot)
        {
            _snapshot = snapshot ?? new ParameterCatalogSnapshot();
        }

        // ── Read model ────────────────────────────────────────────────────────────
        public Dictionary<string, object?> BuildPayload()
        {
            bool hasDoc = !string.IsNullOrEmpty(_snapshot.DocTitle);

            var tokens = UserTokenStore.Instance.Raw
                .OrderBy(d => d.Label, StringComparer.OrdinalIgnoreCase)
                .Select(TokenRow).ToList();

            var builtIns = NamingTokenRegistry.BuiltIns.Select(d => new Dictionary<string, object?>
            {
                ["braced"] = d.Braced,
                ["label"]  = d.Label,
                ["desc"]   = d.Description ?? "",
                ["entity"] = d.Entity == TokenEntity.Any
                    ? AppStrings.T("naming.settings.builtInEntityAny")
                    : d.Entity == TokenEntity.Sheet
                        ? AppStrings.T("naming.settings.appliesToSheets")
                        : AppStrings.T("naming.settings.appliesToViews"),
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["intro"]        = AppStrings.T("naming.settings.intro"),
                ["source"]       = hasDoc ? AppStrings.T("naming.settings.introSource", _snapshot.DocTitle)
                                          : AppStrings.T("naming.settings.introNoDoc"),
                ["sourceOk"]     = hasDoc,
                ["builtInHeader"]= AppStrings.T("naming.settings.builtInHeader", builtIns.Count),
                ["builtIns"]     = builtIns,
                ["tokens"]       = tokens,
                ["catalog"]      = new Dictionary<string, object?>
                {
                    ["sheet"]   = _snapshot.SheetParameters.Select(CatalogEntry).ToList(),
                    ["view"]    = _snapshot.ViewParameters.Select(CatalogEntry).ToList(),
                    ["project"] = _snapshot.ProjectInfoParameters.Select(CatalogEntry).ToList(),
                },
                ["labels"]       = Labels(),
                // Persisted token-model tokens the page uses to build the editor's enum selects.
                ["entitySheet"]  = nameof(TokenEntity.Sheet),
                ["entityView"]   = nameof(TokenEntity.View),
                ["subjTarget"]   = nameof(TokenSubject.Target),
                ["subjSource"]   = nameof(TokenSubject.Source),
                ["subjProject"]  = nameof(TokenSubject.ProjectInfo),
            };
        }

        private Dictionary<string, object?> TokenRow(UserTokenDto d) => new Dictionary<string, object?>
        {
            ["key"]         = d.Key,
            ["label"]       = d.Label,
            ["subject"]     = d.Subject,
            ["entity"]      = d.Entity,
            ["entityWord"]  = d.Entity == nameof(TokenEntity.Sheet)
                ? AppStrings.T("naming.settings.appliesToSheets") : AppStrings.T("naming.settings.appliesToViews"),
            ["paramName"]   = d.ParameterName,
            ["paramGuid"]   = d.ParameterGuid,
            ["paramWord"]   = !string.IsNullOrEmpty(d.ParameterGuid)
                ? AppStrings.T("naming.settings.paramOrigin.shared") : AppStrings.T("naming.settings.paramOrigin.project"),
            ["fallback"]    = d.FallbackText,
            ["description"] = d.Description,
            ["usedBy"]      = UsedByText(d.Key),   // null when unused
        };

        private static Dictionary<string, object?> CatalogEntry(ParameterCatalogEntry e) => new Dictionary<string, object?>
        {
            ["name"]    = e.Name,
            ["guid"]    = e.Guid?.ToString() ?? "",
            ["origin"]  = e.OriginLabel,
            ["shared"]  = e.Guid.HasValue,
            ["storage"] = e.StorageType,
            ["sample"]  = e.SampleValue,
        };

        // ── Mutations (authoritative validation lives here, rule R21) ───────────────

        /// <summary>Validate + persist. Returns an error string to show in the editor, or null on success.</summary>
        public string? Save(UserTokenDto dto, string? originalKey)
        {
            string? error = UserTokenStore.Instance.Validate(dto, originalKey);
            if (!string.IsNullOrEmpty(error)) return error;
            UserTokenStore.Instance.AddOrUpdate(dto, originalKey);
            return null;
        }

        public void Delete(string key) => UserTokenStore.Instance.Delete(key);

        // Scans every persisted pattern (per-tool + Bulk Export's own settings) for the token's
        // braced form, listing which tools would be left with a stale literal (mirrors the WPF
        // NamingUsedByText). Returns null when nothing references the token.
        private static string? UsedByText(string key)
        {
            string braced = "{u:" + key + "}";
            var users = new List<string>();

            foreach (var (toolId, pattern) in NamingPatternStore.Instance.AllPatterns())
                if (!string.IsNullOrEmpty(pattern) && pattern.Contains(braced))
                    users.Add(toolId);

            var exp = LemoineTools.Tools.BulkExport.BulkExportSettings.Instance;
            if (exp.FilenamePattern?.Contains(braced) == true)     users.Add("export.bulkExport (Sheets)");
            if (exp.ViewFilenamePattern?.Contains(braced) == true) users.Add("export.bulkExport (Views)");

            return users.Count == 0 ? null : AppStrings.T("naming.settings.rowUsedBy", string.Join(", ", users));
        }

        // All user-facing text for the page, resolved here (C# is the single source, rule R15).
        // Values carrying a {0} are string.Format templates the page fills with runtime values.
        private static Dictionary<string, object?> Labels() => new Dictionary<string, object?>
        {
            ["yourTokens"]      = AppStrings.T("naming.settings.yourTokensHeader"),
            ["empty"]           = AppStrings.T("naming.settings.emptyState"),
            ["addNew"]          = AppStrings.T("naming.settings.addNew"),
            ["rowEdit"]         = AppStrings.T("naming.settings.rowEdit"),
            ["rowDelete"]       = AppStrings.T("naming.settings.rowDelete"),
            ["deleteConfirm"]   = AppStrings.T("naming.settings.rowDeleteConfirm"),
            ["deleteYes"]       = AppStrings.T("naming.settings.rowDeleteYes"),
            ["deleteNo"]        = AppStrings.T("naming.settings.rowDeleteNo"),
            ["editorTitleNew"]  = AppStrings.T("naming.settings.editorTitleNew"),
            ["editorTitleEdit"] = AppStrings.T("naming.settings.editorTitleEdit", "{0}"),
            ["fieldName"]       = AppStrings.T("naming.settings.fieldName"),
            ["fieldKey"]        = AppStrings.T("naming.settings.fieldKey"),
            ["fieldKeyEdit"]    = AppStrings.T("naming.settings.fieldKeyEdit"),
            ["fieldAppliesTo"]  = AppStrings.T("naming.settings.fieldAppliesTo"),
            ["appliesSheets"]   = AppStrings.T("naming.settings.appliesToSheets"),
            ["appliesViews"]    = AppStrings.T("naming.settings.appliesToViews"),
            ["fieldReadsFrom"]  = AppStrings.T("naming.settings.fieldReadsFrom"),
            ["readsTarget"]     = AppStrings.T("naming.settings.readsFromTarget"),
            ["readsSource"]     = AppStrings.T("naming.settings.readsFromSource"),
            ["readsProject"]    = AppStrings.T("naming.settings.readsFromProject"),
            ["readsCaption"]    = AppStrings.T("naming.settings.readsFromCaption"),
            ["fieldParameter"]  = AppStrings.T("naming.settings.fieldParameter"),
            ["paramSearch"]     = AppStrings.T("naming.settings.parameterSearchPlaceholder"),
            ["paramNone"]       = AppStrings.T("naming.settings.parameterNoneFound"),
            ["manualName"]      = AppStrings.T("naming.settings.manualNamePlaceholder"),
            ["manualGuid"]      = AppStrings.T("naming.settings.manualGuidPlaceholder"),
            ["manualCaption"]   = AppStrings.T("naming.settings.manualCaption"),
            ["nonString"]       = AppStrings.T("naming.settings.nonStringNotice"),
            ["fieldFallback"]   = AppStrings.T("naming.settings.fieldFallback"),
            ["fallbackPh"]      = AppStrings.T("naming.settings.fallbackPlaceholder"),
            ["fallbackCaption"] = AppStrings.T("naming.settings.fallbackCaption"),
            ["fieldDesc"]       = AppStrings.T("naming.settings.fieldDescription"),
            ["fieldLiveTest"]   = AppStrings.T("naming.settings.fieldLiveTest"),
            ["liveSample"]      = AppStrings.T("naming.settings.liveTestSample", "{0}"),
            ["liveFallback"]    = AppStrings.T("naming.settings.liveTestFallback", "{0}"),
            ["liveNoFallback"]  = AppStrings.T("naming.settings.liveTestNoFallback"),
            ["saveNew"]         = AppStrings.T("naming.settings.saveNew"),
            ["saveUpdate"]      = AppStrings.T("naming.settings.saveUpdate"),
            ["cancelEdit"]      = AppStrings.T("naming.settings.cancelEdit"),
        };
    }
}
