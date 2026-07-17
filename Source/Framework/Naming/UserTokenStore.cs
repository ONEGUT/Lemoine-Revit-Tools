using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

namespace LemoineTools.Framework.Naming
{
    /// <summary>
    /// Persisted shape of one user-defined token. Must be <c>public</c> — <see cref="XmlSerializer"/>
    /// throws "only public types can be processed" at construction time for a non-public root, and
    /// because that call is wrapped in try/catch the failure would otherwise be silent (this exact
    /// bug previously reset UI settings on restart — see CLAUDE.md).
    /// </summary>
    public sealed class UserTokenDto
    {
        /// <summary>Key WITHOUT the "u:" namespace prefix — the prefix is added when materializing
        /// into a <see cref="TokenDefinition"/>.</summary>
        [XmlAttribute] public string Key { get; set; } = "";
        [XmlAttribute] public string Label { get; set; } = "";
        /// <summary><see cref="TokenSubject"/> name.</summary>
        [XmlAttribute] public string Subject { get; set; } = nameof(TokenSubject.Target);
        /// <summary><see cref="TokenEntity"/> name.</summary>
        [XmlAttribute] public string Entity { get; set; } = nameof(TokenEntity.Sheet);
        [XmlAttribute] public string ParameterName { get; set; } = "";
        /// <summary>Empty when the parameter is not shared / bound by name only.</summary>
        [XmlAttribute] public string ParameterGuid { get; set; } = "";
        [XmlAttribute] public string FallbackText { get; set; } = "";
        public string Description { get; set; } = "";
    }

    /// <summary>Root element for the user-tokens XML file.</summary>
    [XmlRoot("NamingTokens")]
    public sealed class UserTokensFileDto
    {
        [XmlElement("Token")] public List<UserTokenDto> Tokens { get; set; } = new List<UserTokenDto>();
    }

    /// <summary>
    /// Global (machine-wide) store of user-defined naming tokens, persisted at
    /// <c>%AppData%\LemoineTools\NamingTokens.xml</c>. Mirrors the lazy-singleton +
    /// swallow-and-log persistence pattern used by <c>BulkExportSettings</c>.
    /// </summary>
    public sealed class UserTokenStore
    {
        private static readonly Lazy<UserTokenStore> _lazy = new Lazy<UserTokenStore>(() => new UserTokenStore());
        public static UserTokenStore Instance => _lazy.Value;

        private static readonly Regex KeyPattern = new Regex(@"^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.Compiled);
        private const int MaxKeyLength = 40;

        private List<UserTokenDto> _dtos;

        /// <summary>Raised after tokens are added, updated, or deleted. Any window subscribing
        /// must use a named handler detached on Closed, and marshal with non-blocking
        /// BeginInvoke guarded by HasShutdownStarted — see CLAUDE.md "Why leaked global-event
        /// subscriptions crash Revit".</summary>
        public event Action? TokensChanged;

        private UserTokenStore()
        {
            _dtos = Load();
        }

        /// <summary>Materialized token definitions, "u:"-prefixed, ready for the registry.</summary>
        public IReadOnlyList<TokenDefinition> Tokens =>
            _dtos.Select(ToDefinition).ToList();

        /// <summary>Raw DTOs, for the settings page (editing needs the unprefixed key and GUID text).</summary>
        public IReadOnlyList<UserTokenDto> Raw => _dtos;

        private static TokenDefinition ToDefinition(UserTokenDto dto)
        {
            Enum.TryParse(dto.Subject, out TokenSubject subject);
            Enum.TryParse(dto.Entity, out TokenEntity entity);
            Guid? guid = Guid.TryParse(dto.ParameterGuid, out var g) ? g : (Guid?)null;

            return new TokenDefinition(
                key: "u:" + dto.Key,
                label: dto.Label,
                origin: TokenOrigin.UserParameter,
                subject: subject,
                entity: entity,
                description: dto.Description,
                parameterName: dto.ParameterName,
                parameterGuid: guid,
                fallbackText: dto.FallbackText);
        }

        /// <summary>
        /// Validates a candidate token before save. Returns null when valid, else a
        /// user-facing error message. <paramref name="originalKey"/> is the key being
        /// edited (excluded from the duplicate check) — pass null when creating.
        /// </summary>
        public string? Validate(UserTokenDto candidate, string? originalKey)
        {
            if (candidate == null) return AppStrings.T("naming.settings.errors.invalid");

            string key = (candidate.Key ?? "").Trim();
            if (key.Length == 0)
                return AppStrings.T("naming.settings.errors.keyEmpty");
            if (key.Length > MaxKeyLength)
                return AppStrings.T("naming.settings.errors.keyTooLong", MaxKeyLength);
            if (!KeyPattern.IsMatch(key))
                return AppStrings.T("naming.settings.errors.keyCharset");

            bool collidesBuiltIn = NamingTokenRegistry.BuiltIns
                .Any(b => string.Equals(b.Key, key, StringComparison.OrdinalIgnoreCase));
            if (collidesBuiltIn)
                return AppStrings.T("naming.settings.errors.keyReserved", key);

            bool collidesUser = _dtos.Any(d =>
                !string.Equals(d.Key, originalKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
            if (collidesUser)
                return AppStrings.T("naming.settings.errors.keyDuplicate", key);

            if (string.IsNullOrWhiteSpace(candidate.Label))
                return AppStrings.T("naming.settings.errors.labelEmpty");

            if (!string.IsNullOrEmpty(candidate.ParameterGuid) && !Guid.TryParse(candidate.ParameterGuid, out _))
                return AppStrings.T("naming.settings.errors.guidInvalid");

            if (string.IsNullOrWhiteSpace(candidate.ParameterName) && string.IsNullOrEmpty(candidate.ParameterGuid))
                return AppStrings.T("naming.settings.errors.parameterRequired");

            return null;
        }

        /// <summary>Adds a new token or updates an existing one (matched by <paramref name="originalKey"/>
        /// when renaming, else by <c>candidate.Key</c>). Caller must have already validated. Saves
        /// immediately and raises <see cref="TokensChanged"/> (settings windows auto-save; no separate
        /// Apply step).</summary>
        public void AddOrUpdate(UserTokenDto candidate, string? originalKey = null)
        {
            string matchKey = originalKey ?? candidate.Key;
            int idx = _dtos.FindIndex(d => string.Equals(d.Key, matchKey, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) _dtos[idx] = candidate;
            else _dtos.Add(candidate);

            Save();
            RaiseChanged();
        }

        /// <summary>Deletes the token with the given (unprefixed) key. No-op if absent.</summary>
        public void Delete(string key)
        {
            int removed = _dtos.RemoveAll(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return;

            Save();
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            var handler = TokensChanged;
            if (handler == null) return;
            foreach (Action sub in handler.GetInvocationList())
            {
                try { sub(); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("UserTokenStore.TokensChanged subscriber", ex); }
            }
        }

        // ── Persistence ───────────────────────────────────────────────────────
        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("UserTokenStore: create config directory", ex); }
                return Path.Combine(dir, "NamingTokens.xml");
            }
        }

        private void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(UserTokensFileDto));
                using (var w = new StreamWriter(FilePath))
                    xs.Serialize(w, new UserTokensFileDto { Tokens = _dtos });
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("UserTokenStore.Save", ex); }
        }

        private static List<UserTokenDto> Load()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    var xs = new XmlSerializer(typeof(UserTokensFileDto));
                    using (var r = new StreamReader(path))
                        return ((UserTokensFileDto)xs.Deserialize(r)!).Tokens ?? new List<UserTokenDto>();
                }
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("UserTokenStore.Load", ex); }
            return new List<UserTokenDto>();
        }
    }
}
