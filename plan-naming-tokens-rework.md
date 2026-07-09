# Naming Tokens Rework — Implementation Spec

**Status: APPROVED. Decisions locked by the user — do not re-ask them:**

1. **Unified UI**: every naming location uses the `TokenInput` pattern textbox. The
   Front/Center/End slot UI (`NamingSlots`) is retired and deleted at the end.
2. **No hardcoded naming values**: every generated name flows through a pattern +
   central resolver. No tool may keep a compile-time name format the user cannot see
   and edit (Explode Views' invisible pattern gets a UI).
3. **Token creation/management lives in a Global Settings page** (no inline creation
   popup for now). The page must be **in-depth about defining parameters** — see §6.
4. **User token definitions are stored globally** (machine-wide XML in
   `%AppData%\LemoineTools\`), not per-project.
5. **All work happens on the current branch** `claude/naming-tokens-rework-ryijol`.

This document is written for a cold-start implementer. Every file to touch, every
class shape, every token, every migration step, and every known project gotcha is
spelled out. Read the referenced source before editing it (repo rule: never generate
from memory).

---

## 1. Current-state inventory (verified against source)

Three divergent mechanisms exist. All vocabularies are compile-time constants.

### 1.1 Mechanism A — `TokenInput` (`{Token}` pattern textbox + chip buttons)

`Source/Framework/Controls/Input/TokenInput.cs`. Ctor takes
`IEnumerable<(string Label, string Token)>`; chips insert at the caret;
`static TokenInput.Resolve(pattern, Dictionary<string,string>)` does sequential
`string.Replace("{Key}", value)` and leaves unknown tokens as literal text.

| Tool | Pattern UI | Value dictionary built at | Tokens | Persisted? |
|---|---|---|---|---|
| **Bulk Export** | `BulkExportViewModel.cs:616` (`BuildS3`), vocabularies at `:106-128` | `BulkExportEventHandler.cs:667` (`BuildTokens`), resolution+degenerate guard at `:700-731` (`ResolveExportName`, `SanitizeFilename`) | Sheets: `SheetNumber SheetName Revision IssueDate ProjectNumber ProjectName Year Month Day` · Views: `ViewName ViewType ProjectNumber ProjectName Year Month Day` | Yes — `BulkExportSettings.cs:27-28` |
| **Bulk Rename** | `BulkRenameViewModel.cs:319,328` (Sequential + Token modes), vocab at `:364-374` (`FieldTokens`) | `BulkRenameRunHandler.cs:75-95` (per-element dicts) → `BulkRenameEngine.Compute/Plan` (`BulkRenameEngine.cs`) | `SheetNumber SheetName` or `ViewName ViewType`, plus `Seq` (padded via `SeqPad`) | No |
| **Place Dependent Views** | `PlaceDependentViewsViewModel.cs:17-23,282` | `PlaceDependentViewsEventHandler.cs:652` (`BuildTokens`), used at `:244` | `ParentViewName ViewType Level SheetNumber` | No |
| **Views Bulk Duplicate** | `ViewsBulkDuplicateViewModel.cs:51-56,193` | `ViewsBulkDuplicateRunHandler.cs:107` | `ViewName ViewType`; default `{ViewName} - Copy` | No |
| **Views By Template** | `ViewsByTemplateViewModel.cs:57-63,202` | `ViewsByTemplateRunHandler.cs:118-124` | `ViewName TemplateName ViewType`; default `{ViewName} - {TemplateName}` | No |
| **Views By Link** | `ViewsByLinkViewModel.cs:45-49,117` | `ViewsByLinkRunHandler.cs:96-97` | `LinkName`; default `{LinkName}` | No |

### 1.2 Mechanism B — `NamingSlots` (Front/Center/End dropdowns + "Custom" text)

`Source/Framework/Controls/Input/NamingSlots.cs` (`NamingSlotsState` +
`NamingSlots : StackPanel`). Tokens are display-string logic identifiers
(`"Level"`, `"Scope Box"`, …) switched on in each run handler. Parts joined with
`" - "`.

| Tool | UI | Resolution | Vocabulary (besides `None`/`Custom`) |
|---|---|---|---|
| **Bulk Views by Level** | `LinkViewsLevelViewModel.cs:64-67,459` | `LinkViewsLevelRunHandler.cs:295-329` (`BuildViewName`; note `Level` resolves to `"L" + levelName`; `AppendViewType` bool appends type label; empty → fallback `L{level} - {box}`) | `Level`, `Scope Box`, `View Type` |
| **Scope Box Creator** | `ScopeBoxCreatorViewModel.cs:49-52,515` | `ScopeBoxCreatorRunHandler.cs:410-457` (`MakeSpec`; empty → `letter + rangeToken`) | `Building Letter`, `Level`, `Level Range`, `Model Name` |
| **Scope Box Manager rename** | `ScopeBoxManagerWindow.xaml.cs:1251-1320` (overlay; resolver inline at `:1281-1289`) | same file — `Current Name` → `b.Name`, `Number` → `(i+1).ToString("00")`; empty → keep old name | `Current Name`, `Number` |
| **Replicate Dependent Views** | `ReplicateDependentViewsViewModel.cs:449-566` — **hand-rolled slot rows, does NOT use the shared `NamingSlots` control** | `ReplicateDependentViewsRunHandler.cs:212-244` (`BuildDepName`/`ResolveSlot`; dep suffix always appended last; `Dep Suffix` token resolves to `""`; all-None → `{target.Name} - {suffix}`) | `Host Level`, `Source View Name`, `Target View Name`, `View Type`, `Dep Suffix` |

### 1.3 Mechanism C — hand-rolled, invisible

| Tool | Where | Detail |
|---|---|---|
| **Explode Views by Trade** | `ExplodeViewByTradeEventHandler.cs:34,299-302` | `NamePattern = "{nn}_{Source} - {Trade}"`, raw `.Replace()`; `ExplodeViewByTradeViewModel.cs` exposes **no UI** for it |

### 1.4 Other relevant infrastructure

- `Source/Framework/AppSettings.cs` — `UISettingsDto` XML persistence pattern
  (`XmlSerializer`, **public DTO required**).
- `Source/Tools/Export/BulkExportSettings.cs` — lazy-singleton settings file pattern
  (`Load()`/`Save()` with `DiagnosticsLog.Swallowed` wrapping). Copy this pattern for
  the new stores.
- `Source/Framework/GlobalSettingsWindow.xaml.cs` — pill nav defined at `:146-156`
  (`_navDefs`), content switch at `:250-263`. Launched by
  `Source/Commands/OpenSettingsCommand.cs` on the Revit main thread (window is a
  singleton on `App.GlobalSettings`).
- `Source/Framework/Controls/` — house controls: `SingleSelect`, `InlineStepper`
  (all numeric input), `TokenInput`.
- Strings: `Strings/en/*.json`, loaded by `Source/Framework/AppStrings.cs`.

---

## 2. Target architecture — new files

All new framework code goes in **`Source/Framework/Naming/`**, namespace
`LemoineTools.Framework.Naming`. Everything except `ParameterCatalog` capture and
`TokenResolver`'s element reads must be **Revit-free** (usable from previews on tool
STA threads with no doc).

### 2.1 `Source/Framework/Naming/TokenModel.cs`

```csharp
namespace LemoineTools.Framework.Naming
{
    /// <summary>Which kind of thing a token applies to. A tool declares the entity
    /// it names; the registry only offers tokens valid for that entity.</summary>
    public enum TokenEntity { Sheet, View, ScopeBox, Any }
    // Any = date/project/sequence tokens valid everywhere.
    // Deliberately small: Link/Level/Trade name *sources* are modeled as
    // tool-computed tokens (see TokenOrigin.Computed), not entities.

    /// <summary>Where a token's value comes from.</summary>
    public enum TokenOrigin
    {
        BuiltIn,     // resolved by TokenResolver itself (dates, project info, element fields)
        Computed,    // value supplied per-item by the running tool (Level Range, Seq, Trade…)
        UserParameter // user-defined: reads a Revit parameter off the subject element
    }

    /// <summary>Which element the token reads from, when a context has both a
    /// target (the thing being named) and a source (parent view / source view / link).</summary>
    public enum TokenSubject { Target, Source, ProjectInfo, Environment }

    /// <summary>One token. Immutable for built-ins; user tokens are DTO-backed.</summary>
    public sealed class TokenDefinition
    {
        public string       Key;           // pattern text without braces: "SheetNumber", "u:SheetSeries"
        public string       Label;         // chip text (AppStrings.T for built-ins; user label for user tokens)
        public TokenOrigin  Origin;
        public TokenSubject Subject;
        public TokenEntity  Entity;        // which entity's pickers list it (Any = all)
        public string?      Description;   // settings-page help line (built-ins: AppStrings key)
        // UserParameter-only:
        public string?      ParameterName; // display + fallback lookup
        public Guid?        ParameterGuid; // authoritative for shared parameters
        public string?      FallbackText;  // substituted when the parameter is missing/empty
    }
}
```

Key rules (enforce in `UserTokenStore.Validate`, §2.3):
- User keys are stored WITHOUT the `u:` prefix in the DTO but always exposed via
  `TokenDefinition.Key` as `u:<name>`; pattern text therefore reads `{u:SheetSeries}`.
  The namespace guarantees a future built-in can never collide with a user token.
- Key charset: `^[A-Za-z][A-Za-z0-9_]*$`, max 40 chars, unique case-insensitively
  among user tokens AND against all built-in keys.

### 2.2 `Source/Framework/Naming/NamingTokenRegistry.cs`

Static catalog. Built-ins are declared once here (labels via
`AppStrings.T("naming.tokens.<key>.label")`); user tokens come from
`UserTokenStore.Instance`.

```csharp
public static class NamingTokenRegistry
{
    /// <summary>All built-in token definitions (fixed at compile time).</summary>
    public static IReadOnlyList<TokenDefinition> BuiltIns { get; }

    /// <summary>Query for a picker: built-ins matching the entity (or Any),
    /// filtered by hasSource (Source-subject tokens only when the tool has a
    /// source element), plus extraComputed (tool-specific Computed tokens the
    /// caller passes in), plus user tokens whose Entity matches.</summary>
    public static List<TokenDefinition> TokensFor(
        TokenEntity entity,
        bool hasSource,
        IReadOnlyList<TokenDefinition>? extraComputed = null);

    /// <summary>Find any token (built-in, computed-from-list, or user) by key.
    /// Returns null when unknown.</summary>
    public static TokenDefinition? Find(string key,
        IReadOnlyList<TokenDefinition>? extraComputed = null);
}
```

**Complete built-in table** (this is normative — implement exactly these):

| Key | Label (en) | Origin | Subject | Entity | Resolution |
|---|---|---|---|---|---|
| `SheetNumber` | Sheet Number | BuiltIn | Target | Sheet | `ViewSheet.SheetNumber` |
| `SheetName` | Sheet Name | BuiltIn | Target | Sheet | `ViewSheet.Name` |
| `Revision` | Revision | BuiltIn | Target | Sheet | `SHEET_CURRENT_REVISION` param `AsString()` |
| `IssueDate` | Issue Date | BuiltIn | Target | Sheet | `SHEET_ISSUE_DATE` param `AsString()` |
| `ViewName` | View Name | BuiltIn | Target | View | `View.Name` |
| `ViewType` | View Type | BuiltIn | Target | View | `View.ViewType.ToString()` — **except** where a tool already computes a friendlier label (`ViewsByTemplateRunHandler.ViewTypeLabel`); those tools pass it as a Computed override with the same key (context override wins, §2.4) |
| `Level` | Level | BuiltIn | Target | View | `View.GenLevel?.Name` (guard try/catch → `DiagnosticsLog.Swallowed`, as `PlaceDependentViewsEventHandler.cs:656` does today) |
| `CurrentName` | Current Name | BuiltIn | Target | Any | `Element.Name` (used by rename-style tools) |
| `SourceViewName` | Source View Name | BuiltIn | Source | View | source `View.Name` / provided source name |
| `ParentViewName` | Parent View | BuiltIn | Source | View | alias of source name for PDV (keep both keys — PDV patterns may be saved with it) |
| `TemplateName` | Template Name | BuiltIn | Source | View | provided by tool (template element name) |
| `LinkName` | Link Name | BuiltIn | Source | Any | provided by tool (`Path.GetFileNameWithoutExtension(linkDoc.Title)`) |
| `ProjectNumber` | Project No. | BuiltIn | ProjectInfo | Any | `doc.ProjectInformation.Number` (same read Bulk Export does today) |
| `ProjectName` | Project Name | BuiltIn | ProjectInfo | Any | `doc.ProjectInformation.Name` |
| `Year` | Year | BuiltIn | Environment | Any | `DateTime.Now.Year` (`"yyyy"`) |
| `Month` | Month | BuiltIn | Environment | Any | `DateTime.Now:MM` |
| `Day` | Day | BuiltIn | Environment | Any | `DateTime.Now:dd` |
| `Seq` | Counter | Computed | Environment | Any | supplied per-item by tools that sequence (Bulk Rename keeps its `SeqStart/Increment/Pad` machinery; other tools supply `(index+1)` zero-padded 2 when they offer it) |

**Tool-specific Computed tokens** (declared as `static readonly TokenDefinition[]`
constants next to each tool's ViewModel and passed via `extraComputed` — they are NOT
in the global registry because they're meaningless elsewhere):

| Tool | Computed tokens (Key → meaning) |
|---|---|
| Bulk Views by Level | `LevelName` → level name (**no** "L" prefix — the legacy prefix moves into the default pattern), `ScopeBox` → crop box name |
| Scope Box Creator | `BuildingLetter`, `LevelName`, `LevelRange`, `ModelName` |
| Scope Box Manager rename | `Number` → `(i+1):00` |
| Replicate Dependent Views | `HostLevel`, `TargetViewName`, `DepSuffix` |
| Explode Views by Trade | `Counter` → the `nn` two-digit index, `SourceView` → source view name, `Trade` → trade label |
| Place Dependent Views | `SheetNumber` here is Computed (the *new* number just allocated by `NextFreeNumber`) — pass it as a context override so it wins over the built-in Sheet read |

Labels for computed tokens: `AppStrings.T("naming.computed.<toolArea>.<key>.label")`.

### 2.3 `Source/Framework/Naming/UserTokenStore.cs`

Persistence + validation for user tokens. Copy the `BulkExportSettings` structure
(lazy singleton, `Load`/`Save`, swallowed-and-logged IO exceptions).

```csharp
/// <remarks>Must be public: XmlSerializer cannot process non-public root types —
/// an internal DTO makes every save/load fail silently (known project bug class).</remarks>
public sealed class UserTokenDto
{
    [XmlAttribute] public string Key           { get; set; } = "";   // WITHOUT "u:"
    [XmlAttribute] public string Label         { get; set; } = "";
    [XmlAttribute] public string Subject       { get; set; } = "Target";      // TokenSubject name
    [XmlAttribute] public string Entity        { get; set; } = "Sheet";       // TokenEntity name
    [XmlAttribute] public string ParameterName { get; set; } = "";
    [XmlAttribute] public string ParameterGuid { get; set; } = "";  // empty when not shared
    [XmlAttribute] public string FallbackText  { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class UserTokensFileDto { public List<UserTokenDto> Tokens = new(); }

public sealed class UserTokenStore
{
    public static UserTokenStore Instance { get; }
    public IReadOnlyList<TokenDefinition> Tokens { get; }   // materialized, "u:" prefixed keys
    public event Action? TokensChanged;                     // settings page edits fire this

    public string? Validate(UserTokenDto candidate, string? originalKey); // null = OK, else error message (AppStrings)
    public void AddOrUpdate(UserTokenDto dto);   // saves + raises TokensChanged
    public void Delete(string key);              // saves + raises TokensChanged
}
```

- File: `%AppData%\LemoineTools\NamingTokens.xml` (same directory the other stores use
  — mirror however `BulkExportSettings` builds its path).
- `TokensChanged` subscribers in windows MUST follow the global-event rule: **named
  handler, detached on `Closed`, `Dispatcher.BeginInvoke` guarded by
  `HasShutdownStarted`** (see CLAUDE.md "Why leaked global-event subscriptions crash
  Revit"). The raiser must walk `GetInvocationList()` with per-subscriber try/catch →
  `DiagnosticsLog.Swallowed`, exactly like `AppSettings.SetTheme` does.

### 2.4 `Source/Framework/Naming/TokenContext.cs` + `TokenResolver.cs`

```csharp
/// <summary>Everything a resolution needs for ONE item. Revit types are optional so
/// previews can run doc-free with only Computed values.</summary>
public sealed class TokenContext
{
    public Document? Doc;
    public Element?  Target;               // element being named (sheet/view/scope box)
    public Element?  Source;               // parent/source element when the tool has one
    /// <summary>Per-item computed values; an entry here OVERRIDES any built-in
    /// resolution for the same key (this is how ViewTypeLabel / new SheetNumber win).</summary>
    public Dictionary<string, string> Computed = new(StringComparer.Ordinal);
}

public static class TokenResolver
{
    /// <summary>Resolves every {Token} in the pattern. Single-pass regex
    /// (\{(u:)?[A-Za-z][A-Za-z0-9_]*\}) — never sequential Replace, so a resolved
    /// VALUE containing brace text is never re-substituted (latent bug in the old
    /// TokenInput.Resolve). Unknown keys stay literal and are reported via onWarn
    /// (once per distinct key per call). onWarn also receives user-token
    /// missing-parameter notices.</summary>
    public static string Resolve(string pattern, TokenContext ctx,
                                 Action<string>? onWarn = null);

    /// <summary>Post-guard for generated names, centralizing the Bulk Export rule:
    /// a result that is empty or has no alphanumeric char is a FAILURE — warn via
    /// onWarn + DiagnosticsLog.Warn and return the deterministic fallback
    /// (target.Name, else "item-" + target.Id, else the supplied fallback).</summary>
    public static string GuardDegenerate(string resolved, TokenContext ctx,
                                         string fallback, Action<string>? onWarn);

    /// <summary>Filename-only sanitation (move SanitizeFilename here from
    /// BulkExportEventHandler; element names get a lighter pass — Revit forbids
    /// \{\}[]|;<>?`~ and : in names, keep the existing per-tool skip-and-log for
    /// setter throws as the authority).</summary>
    public static string SanitizeFilename(string s);
}
```

Resolution order per token: `ctx.Computed[key]` → built-in reads (per §2.2 table,
guarded with try/catch → `DiagnosticsLog.Swallowed` around every Revit read) →
user-parameter read → unknown.

**User-parameter read procedure** (this exact order — the by-name pitfalls are known
project bugs, see CLAUDE.md "Writing a value to a sheet's shared parameter by name is
unreliable"):
1. Pick the subject element (`Subject == Source ? ctx.Source : ctx.Target`;
   `ProjectInfo` → `ctx.Doc?.ProjectInformation`). Subject missing → fallback text +
   warn.
2. If `ParameterGuid` is set: `element.get_Parameter(guid)`.
3. Else: `element.GetParameters(ParameterName)` — prefer a parameter with
   `StorageType.String`, then any readable match. **Never** bare
   `LookupParameter` first (silently picks the wrong duplicate).
4. Value: `AsString()` for String storage; otherwise `AsValueString()` (formats
   through the doc's units); null/empty → `FallbackText` + a single warn per run
   naming the token and the parameter.

**Warn routing**: `onWarn` is the tool's `pushLog(msg, "warn")`. Handlers must
de-duplicate (resolver warns once per distinct key per `Resolve` call; handlers
should additionally keep a run-level `HashSet<string>` so a 500-view run logs each
missing token once, not 500 times). Silence is forbidden (repo standard).

### 2.5 `Source/Framework/Naming/NamingPatternStore.cs`

Last-used pattern per tool, so every tool remembers its scheme like Bulk Export does.

```csharp
public sealed class NamingPatternDto { [XmlAttribute] public string ToolId=""; [XmlAttribute] public string Pattern=""; }
public sealed class NamingPatternsFileDto { public List<NamingPatternDto> Patterns = new(); }

public sealed class NamingPatternStore   // %AppData%\LemoineTools\NamingPatterns.xml
{
    public static NamingPatternStore Instance { get; }
    public string GetOrDefault(string toolId, string defaultPattern);
    public void   Set(string toolId, string pattern);   // saves immediately (settings auto-save rule)
}
```

Tool ids (stable, hardcoded logic tokens — deliberately not externalized):
`views.duplicate`, `views.byTemplate`, `views.byLink`, `views.byLevel`,
`views.replicateDependents`, `views.explodeByTrade`, `scopeBoxes.creator`,
`scopeBoxes.managerRename`, `sheets.placeDependent`, `sheets.bulkRename.token`,
`sheets.bulkRename.seq`. **Bulk Export keeps its own settings file** (patterns
already persisted there; migrating is churn for no gain).

### 2.6 `Source/Framework/Naming/ParameterCatalog.cs`

The settings page needs to list real parameters to bind. Revit reads must happen on
the Revit main thread — the same pattern as
`AutoFiltersSettings.CaptureFilterableCategories(doc)` (capture in the command,
hand the snapshot to the window).

```csharp
public sealed class ParameterCatalogEntry
{
    public string Name;          // parameter display name
    public Guid?  Guid;          // set for shared parameters (ExternalDefinition/SharedParameterElement)
    public string StorageType;   // "String" | "Double" | "Integer" | "ElementId"
    public string OriginLabel;   // "Project parameter" | "Shared parameter" | "Built-in (common)"
    public bool   IsInstance;    // instance vs type binding (display only)
}

public static class ParameterCatalog
{
    /// <summary>MAIN THREAD ONLY. Captures, per entity, every parameter a user
    /// token could bind. Sheets: iterate doc.ParameterBindings (BindingMap
    /// forward iterator) keeping definitions whose CategorySet contains
    /// OST_Sheets; Views: same for OST_Views. For each InternalDefinition try
    /// SharedParameterElement lookup (doc.GetElement(def.Id) as
    /// SharedParameterElement)?.GuidValue for the GUID. ALSO sample a live
    /// element (first ViewSheet / first non-template View) and add its
    /// visible non-built-in parameters not already captured — project params
    /// bound outside the BindingMap iteration and family-borne params show up
    /// this way. De-dupe by (Name, Guid). Sort by Name.
    /// Zero results is reported, not silent: the settings page must show
    /// "No bindable parameters found for <entity>" (repo rule: empty survey
    /// results must say so).</summary>
    public static ParameterCatalogSnapshot Capture(Document doc);
}

public sealed class ParameterCatalogSnapshot
{
    public List<ParameterCatalogEntry> SheetParameters = new();
    public List<ParameterCatalogEntry> ViewParameters  = new();
    public List<ParameterCatalogEntry> ProjectInfoParameters = new(); // from doc.ProjectInformation.Parameters
    public string DocTitle = "";   // shown as "captured from <doc>" in the page
}
```

Wiring: `OpenSettingsCommand.Execute` (runs on the main thread with
`commandData.Application.ActiveUIDocument?.Document`) captures the snapshot when a
doc is open and passes it to `new GlobalSettingsWindow(snapshot)` (add an optional
ctor parameter defaulting to null; keep the existing parameterless path compiling for
any other caller). No doc open → page still works with **manual entry** (§6.4).

---

## 3. `TokenInput` upgrades (`Source/Framework/Controls/Input/TokenInput.cs`)

Keep the class name and file. Changes:

1. **New primary ctor**: `TokenInput(IReadOnlyList<TokenDefinition> tokens, string
   defaultPattern = "")`. Chips render grouped with small section headers
   (`LemoineTextDim`, `LemoineFS_XS`) in fixed order: Target · Source · Project ·
   Date & Counter · **Your tokens** (user tokens; header only when any exist). Chip
   label = `def.Label`; inserted text = `"{" + def.Key + "}"`. Chip tooltip =
   description + (for user tokens) `Parameter: <name>`. Keep the old tuple ctor
   **deleted** — all six call sites migrate in the same phase (grep for
   `new TokenInput(` to be sure none remain).
2. **Built-in preview row**: `void SetPreview(Func<string, string> resolvePattern)` —
   a `TextBlock` (mono font, `LemoineFS_SM`) under the chips re-rendered on every
   `TextChanged` with `resolvePattern(Text)`. Tools pass a closure that builds a
   sample `TokenContext` and calls `TokenResolver.Resolve`. This replaces the six
   per-tool hand-built preview blocks (delete them as each tool migrates).
3. **Reset-to-default affordance**: small `↺` flat button right of the textbox,
   visible when `Text != defaultPattern`, sets `Text = defaultPattern`. (Users will
   now carry persisted patterns; they need a way back. Use
   `char.ConvertFromUtf32(0x21BA)` or plain text "Reset" — do NOT paste a literal
   glyph, Edit-tool rule.)
4. **Remove the dead placeholder code** at `TokenInput.cs:62-79` (the half-finished
   watermark experiment that sets transparent foreground then overrides itself) —
   keep only the `ToolTip = placeholder` behavior.
5. Static `Resolve` (old dictionary version) survives **only** during Phase 1–2 as a
   shim and is deleted in Phase 5 once no caller remains.

WPF rules that apply here (from CLAUDE.md — violating these crashes Revit or breaks
theming): no `Popup` with `StaysOpen=false`; every themed brush via
`SetResourceReference`; `Background = Brushes.Transparent` direct-assign for
hit-testability; this control is used across STA tool windows so no shared static
mutable Freezables (freeze any shared brush/easing).

---

## 4. Per-tool migration (exact steps)

General pattern per tool — replace vocabulary + resolution, keep everything else
(uniqueness checks, skip-and-log, cancel handling) untouched:

1. ViewModel: delete local `NamingTokens`/vocab consts → build chips with
   `NamingTokenRegistry.TokensFor(entity, hasSource, toolComputed)`; initialize
   `Text` from `NamingPatternStore.GetOrDefault(toolId, default)`; on `TextChanged`
   update the field AND `NamingPatternStore.Set(toolId, text)`; wire
   `SetPreview(...)` with a sample context.
2. RunHandler: replace the token `Dictionary` with a `TokenContext` per item;
   `TokenResolver.Resolve(NamePattern, ctx, warn)` +
   `TokenResolver.GuardDegenerate(...)`; keep the handler's existing
   payload-clearing `finally` (add the new pattern/context fields to it).
3. Strings: any new labels/hints to `Strings/en/naming.json`; run-log lines through
   `AppStrings.T` as everywhere else.

### 4.1 Mechanism A tools (already `TokenInput` — low risk, do first)

- **Views Bulk Duplicate** (`ViewsBulkDuplicateViewModel.cs`,
  `ViewsBulkDuplicateRunHandler.cs`): entity `View`, no source, no computed. Default
  pattern stays `{ViewName} - Copy`. Note `ViewsBulkDuplicateViewModel.cs:275` checks
  `_namePattern.Trim() == "{ViewName}"` to warn about self-name collisions — keep,
  it's a pattern-literal check, not a naming value.
- **Views By Template**: entity `View`, source = template →
  `TemplateName` is Source-subject BuiltIn supplied via `ctx.Computed["TemplateName"]
  = template.Name`; keep `ViewTypeLabel` as `ctx.Computed["ViewType"]` override.
- **Views By Link**: entity `View`, source = link; `ctx.Computed["LinkName"]`.
  Existing empty-name fallback (`viewName = linkName`) becomes `GuardDegenerate` with
  fallback `linkName`.
- **Place Dependent Views**: entity `Sheet` (it names sheets), source = parent view
  (`ctx.Source = parent`); `ctx.Computed["SheetNumber"] = <new number>`,
  `["ParentViewName"]`, `["Level"]` as today. **This is the tool where user sheet
  parameters + parent-view parameters shine** — after migration a user token bound to
  a parent-view parameter (`Subject=Source, Entity=View`) and one bound to a sheet
  param both appear. Note: a user Sheet-target token resolves against a sheet that
  was *just created* — its parameter is empty, so fallback text applies; the
  settings-page description field should be used to explain that (document it in the
  token description default hint, §6.4).
- **Bulk Rename**: keep `BulkRenameEngine` fully Revit-free and intact (preview ==
  write guarantee depends on it). Only swap the chip vocabulary
  (`BulkRenameViewModel.cs:364` `FieldTokens`) to registry-fed and extend the
  per-element dictionaries (`BulkRenameRunHandler.cs:75-95` and the VM's preview-side
  equivalent — find it by searching the VM for `SheetNumber`) to include resolved
  user-token values: for each user token in the registry matching the target entity,
  pre-resolve its value per element **in the run handler / VM preview** (both sides,
  same code path — add a small shared helper) and add it to the dict under its
  `u:`-prefixed key. `{Seq}` stays engine-owned. Patterns persist under
  `sheets.bulkRename.token` / `.seq`.
- **Bulk Export**: swap `SheetTokens`/`ViewTokens` (`BulkExportViewModel.cs:106-128`)
  for registry queries (`TokenEntity.Sheet` / `TokenEntity.View`, no source). Move
  `BuildTokens`/`ResolveExportName` (`BulkExportEventHandler.cs:667-731`) onto
  `TokenContext`/`TokenResolver.Resolve` + `GuardDegenerate` +
  `TokenResolver.SanitizeFilename`; delete the local `SanitizeFilename`. Keep the
  views-mode `SheetName`→view-name fallback tokens? **No** — that fallback
  (`BulkExportEventHandler.cs:692-695`) predates mode-aware vocabularies; with the
  registry the Views picker never offers sheet tokens, and an old persisted sheet
  pattern in views mode now warns-and-keeps-literal, which `GuardDegenerate` then
  catches. That is the desired loud behavior. Keep `Hint` text at
  `BulkExportViewModel.cs:1299` in sync or replace it with the chip row itself.
  Patterns keep persisting via `BulkExportSettings` exactly as now.

### 4.2 Mechanism B tools (slots → pattern; behavior-preserving defaults)

For each: delete the slot UI + `NamingSlotsState` usage + the 6 `NamingFront…`
handler properties; add `NamePattern` string property; VM builds a `TokenInput`
with the tool's Computed tokens; handler resolves per item.

| Tool | Default pattern (replicates legacy default slots) | Computed context per item | Legacy fallback → GuardDegenerate fallback |
|---|---|---|---|
| Bulk Views by Level (`LinkViewsLevel*`) | `L{LevelName} - {ScopeBox}` (legacy Front=Level→`L`+name, Center=Scope Box) | `LevelName`, `ScopeBox` (empty string when no box — resolver drops it via warn? No: empty computed value resolves to empty, fine), `ViewType` label | `L{level} - {box}` string, i.e. pass fallback = that composition. **`AppendViewType` checkbox is retired**: fold it into the pattern (`… - {ViewType}`); the VM migration maps a persisted legacy state only if trivially available, otherwise just ship the new default |
| Scope Box Creator | `{BuildingLetter} - {LevelRange}` | `BuildingLetter`, `LevelName`, `LevelRange`, `ModelName` (computed in `MakeSpec`, `ScopeBoxCreatorRunHandler.cs:410-427`) | `letter - range` |
| Scope Box Manager rename | `{CurrentName}` | `CurrentName` (box name), `Number` (`(i+1):00`) | keep old name (legacy: parts empty → `b.Name`) |
| Replicate Dependent Views | `{HostLevel} - {SourceViewName}` | `HostLevel`, `SourceViewName`, `TargetViewName`, `ViewType` | `{target.Name} - {suffix}` |

RDV specifics: delete the hand-rolled `AddSlotRow`/`RdvNamingOptions` block
(`ReplicateDependentViewsViewModel.cs:449-566`) and the `_namingFront…` fields
(`:171-176`) and handler properties (`ReplicateDependentViewsRunHandler.cs:16-22`).
The **dep suffix stays appended after the resolved pattern** exactly as today
(`BuildDepName` keeps `parts.Add(suffix)` semantics → new code:
`resolved + " - "?`… no: legacy joins suffix with `" - "` as a part; new code:
`string.Join(" - ", new[]{resolved, suffix}.Where(nonEmpty))`). Drop the `Dep Suffix`
pseudo-token entirely (it resolved to `""`).

Scope Box Manager rename overlay lives in a **non-StepFlow window**
(`ScopeBoxManagerWindow`) — remember there is no dispatcher safety net there; the
`TokenInput.TextChanged`/preview closures do no I/O so no extra guarding needed, but
do not add timers/async without try/catch (CLAUDE.md STA rule).

After all four migrate: **delete `Source/Framework/Controls/Input/NamingSlots.cs`**
and the `controls.inputs.namingSlots.*` keys in `Strings/en/controls.inputs.json`.
Grep `NamingSlots` must return zero hits before the delete commit.

### 4.3 Explode Views by Trade (new pattern UI)

- `ExplodeViewByTradeEventHandler.cs:34` keeps `NamePattern` but the default moves to
  the shared default constant; `:299-302` `.Replace()` chain →
  `TokenResolver.Resolve` with `ctx.Computed["Counter"|"SourceView"|"Trade"]`.
  Token keys change `nn→Counter`, `Source→SourceView` (no persisted user patterns
  exist yet, safe to rename once; default pattern becomes
  `{Counter}_{SourceView} - {Trade}`).
- VM: add the `TokenInput` + preview to `BuildS3` (options step,
  `ExplodeViewByTradeViewModel.cs:178`) under a section label, entity `View`, only
  the three computed tokens + Any-entity tokens; persist under
  `views.explodeByTrade`.

### 4.4 Explicit non-adopters (do not touch)

Filter/legend naming (`AutoFiltersSettings.MakeFilterName`) is a managed
reuse-by-name convention — tokenizing it would orphan every existing filter.
Print View writes single files through Revit's own save dialog. Ceiling grid tools
name via managed trade config. Leave all of them alone.

---

## 5. Settings page — "Naming" tab in `GlobalSettingsWindow`

### 5.1 Wiring

- Add `("naming", AppStrings.T("globalSettings.nav.naming"))` to `_navDefs`
  (`GlobalSettingsWindow.xaml.cs:146-156`) — position it after `"general"`. The nav
  grid is star-sized per pill; 9 pills fit (labels are short — "Naming").
- Add `case "naming": content = BuildNamingContent(); break;` to the switch at
  `:250-263`.
- New partial: **`Source/Framework/GlobalSettingsWindow.Naming.cs`** (methods shared
  across partials must be `internal`, not `private` — CS0122 rule). Follow the
  section-building idioms of `GlobalSettingsWindow.General.cs` (section labels,
  `HSep`, row shells).
- `OpenSettingsCommand` captures `ParameterCatalog.Capture(doc)` (null-safe when no
  doc) and passes it through the ctor. Store on a field; the Naming partial reads it.

### 5.2 Page layout (top to bottom)

**This is a WPF UI task: invoke `/revit-navisworks-ui` before writing the page, and
render the HTML mockup image for user approval before any code** (repo rule; pull the
real `ThemePalette` values).

1. **Header/intro** — one dim line: what tokens are, where they appear
   ("Tokens are placeholders like `{SheetName}` used by naming fields across the
   tools. Tokens you define here appear in every matching tool.") plus the capture
   source line: "Parameters read from: *<DocTitle>*" or a warning row
   "No document was open — parameter lists unavailable, manual entry only."
2. **Built-in tokens section** (read-only reference) — compact two-column list of
   every built-in: `{Key}` (mono) + label + entity chips (Sheet/View/Any) +
   description. Collapsed by default behind an expander-style toggle row so the page
   leads with the user's own tokens.
3. **Your tokens section** — list of user tokens, one row each:
   `{u:Key}` (mono) · label · entity · subject · bound parameter name (+ "GUID"
   badge when shared-bound) · fallback text preview · Edit / Delete flat buttons.
   Empty state text: "No custom tokens yet — define one below." **Delete** shows an
   inline confirm (swap the row's buttons for "Delete? ✓ ✗" — no popup) and warns
   when the key appears in any persisted pattern: scan `NamingPatternStore` +
   `BulkExportSettings` patterns for `{u:Key}` and list the tool names in the confirm
   text ("Used by: Bulk Export, Place Dependent Views — patterns will keep the
   literal text and warn at run time.").
4. **Editor card** (create + edit share it; in-depth parameter definition is here):
   - **Name (label)** — free text; auto-derives **Key** (strip non-alphanumerics,
     PascalCase) into a read-only mono field with an "edit" toggle for manual key
     override; live-validated via `UserTokenStore.Validate` (duplicate/charset
     errors shown inline in `LemoineDanger`-style text, Save disabled while invalid).
   - **Applies to** — `SingleSelect`: Sheets / Views (maps to `TokenEntity`).
     Switching swaps the parameter list below.
   - **Reads from** — `SingleSelect`: "The element being named (target)" /
     "The source element (parent view, source view, link)" / "Project information".
     A dim caption under it re-explains in context, e.g. for Source: "In tools that
     create from a parent (Place Dependent Views, Views by Template…), the value is
     read from that parent instead of the new element." When Subject=ProjectInfo the
     parameter list swaps to `ProjectInfoParameters`.
   - **Parameter** — the core, in-depth block:
     - Searchable list (TextBox filter above a bordered, self-contained
       `ScrollViewer` — popup-scroll rules don't apply since it's in-page, but wire
       `ControlStyles.WireBubblingScroll`) of `ParameterCatalogEntry` rows: name,
       origin label (Project/Shared/Built-in), storage type chip, instance/type,
       GUID short-form for shared. Selecting fills the binding.
     - **Manual entry fallback** (always available; the only path with no doc):
       parameter name TextBox + optional GUID TextBox (validated `Guid.TryParse`,
       error inline). Caption: "Use this if the parameter isn't in the current
       document. Name-only binding picks the first match — prefer the GUID for
       shared parameters."
     - **Storage-type notice**: selecting a non-String parameter shows a dim note
       "Non-text parameter — the displayed value (as shown in Revit) will be used."
   - **Fallback text** — TextBox + caption "Used when the parameter is missing or
     empty. Leave blank to drop the token from the name."
   - **Description** — optional TextBox, shown as chip tooltip in tools.
   - **Live test row** — when a doc snapshot exists, resolve the token against the
     captured sample element values: capture, per parameter entry, its sample value
     from the sampled sheet/view during `ParameterCatalog.Capture` (add
     `SampleValue` to `ParameterCatalogEntry`) so the settings page can show
     "Sample: *A-101 Series B*" with zero Revit access from the window thread.
   - **Save** button (flat, accent) — `AddOrUpdate` + clear the editor. Per repo
     settings convention there is no page-level Apply; Save-per-token IS the explicit
     action (do not add a second confirm).
5. Section order note: settings windows auto-save (repo rule) — no window-level OK.

### 5.3 Threading & lifetime rules for this page

- The window is created on the Revit main thread by `OpenSettingsCommand` — but
  treat all Revit access as forbidden inside the window anyway; everything comes
  from the captured snapshot (this also keeps the no-doc path trivially safe).
- Subscribe to `UserTokenStore.TokensChanged` only if some other window can edit
  tokens concurrently — nothing else can, so **don't subscribe**; the page owns the
  store while open. Tool windows opened afterwards read the fresh store; already-open
  tool windows keep their chip rows until reopened (same accepted semantics as the
  language switch — document that in the page intro line: "Open tools pick up new
  tokens the next time they're opened.").

---

## 6. Strings

New file **`Strings/en/naming.json`** (JSONC, one file per area convention), keys:

```jsonc
{
  "tokens": { /* built-in labels+descriptions: sheetNumber.label, sheetNumber.desc, … one pair per §2.2 key (camelCase) */ },
  "computed": { /* per-tool computed labels: byLevel.levelName.label, scopeBoxes.buildingLetter.label, … */ },
  "input": {
    "reset": "Reset to default",
    "previewEmpty": "(empty name)",
    "groupTarget": "…", "groupSource": "…", "groupProject": "…", "groupDate": "…", "groupUser": "Your tokens"
  },
  "resolver": {
    "unknownToken": "Unknown token {0} left as-is",
    "missingParam": "Token {0}: parameter '{1}' not found on {2} — used fallback '{3}'",
    "degenerate": "Name pattern '{0}' produced no usable name — used '{1}' instead"
  },
  "settings": { /* every §5.2 label, caption, error, empty-state, confirm string */ }
}
```

Plus `globalSettings.nav.naming` in `Strings/en/globalSettings.json`.

Rules (repo): all user-facing text externalized; token KEYS, tool ids, subject/entity
enum names are logic tokens and stay hardcoded; verify before each commit that every
`AppStrings.T("naming.…")` key referenced in code exists in the JSON (flatten + regex
diff — the repo's standard check); missing keys fail silent-to-English, so the check
is the only guard.

---

## 7. Implementation phases / commit plan

Each phase compiles standalone; unmigrated tools keep working throughout. One commit
per phase minimum, imperative subjects (e.g. "Add naming token registry and
resolver").

1. **Core** — `TokenModel`, `NamingTokenRegistry`, `UserTokenStore`,
   `TokenContext`/`TokenResolver` (+ move/absorb `SanitizeFilename`),
   `NamingPatternStore`, `ParameterCatalog`, `naming.json` skeleton. No callers yet.
2. **TokenInput upgrade** — new ctor, grouped chips, preview hook, reset button,
   dead-placeholder cleanup. Migrate the 6 Mechanism-A tools (§4.1) in the same phase
   (the ctor swap forces it). Delete each tool's local vocab consts + hand-built
   preview blocks.
3. **Settings page** — mockup image → approval → `GlobalSettingsWindow.Naming.cs`,
   nav pill, `OpenSettingsCommand` capture, editor card, validation.
4. **Slot-tool migration** (§4.2) + RDV de-dup + Explode Views UI (§4.3). Ends with
   deleting `NamingSlots.cs` + its strings (grep-clean first).
5. **Cleanup** — delete `TokenInput.Resolve(dict)` shim (grep-clean), stale doc
   comment in `TokenInput` header ("Used by Bulk Export … Create Sheets" — Create
   Sheets doesn't exist), update `LEMOINE_UI.md` component list (TokenInput contract,
   NamingSlots removal) and CLAUDE.md's "Reusable Components" section if it
   references slots.

**After every phase**: run the repo's mandatory **silent-failure scan** on the diff
(empty catches, unawaited tasks, ignored failure returns, unchecked Revit nulls) and
report findings; state "No silent failures detected" when clean.

---

## 8. Gotcha checklist for the implementer (all from CLAUDE.md — these have bitten before)

- **WPF/Revit type aliases** in any file importing both (`WpfGrid`, `WpfVisibility`,
  `WpfPoint`, `RevitColor`…). `Visibility.Visible` inside a Window subclass is
  CS0176 — use `WpfVisibility`.
- **XmlSerializer DTOs must be `public`** — internal roots fail silently and settings
  stick at defaults.
- **Partial-class shared methods** are `internal`, not `private` (CS0122).
- **CS0136**: don't reuse an enclosing local/param name in a nested `foreach`.
- **Edit tool cannot touch `\uXXXX` escapes or literal PUA glyphs** — use a Python
  `str.replace()` script with `(old, new, expected_count)` count-checks for any bulk
  string rewiring; new glyphs via `char.ConvertFromUtf32(0x…)`.
- **No `Popup StaysOpen=false`** ever; the settings-page parameter list is in-page,
  not a popup, deliberately.
- **Global events** (here: `UserTokenStore.TokensChanged` if ever subscribed from a
  window): named handler + `-=` on `Closed` + `BeginInvoke` with shutdown guard;
  raiser walks `GetInvocationList()` with per-subscriber try/catch.
- **Static ExternalEvent handlers** live for the session — new fields
  (`NamePattern`, contexts, catalogs) join the existing `finally` payload-clearing.
- **Every deliberately-swallowed exception** →
  `DiagnosticsLog.Swallowed(context, ex)`; every zero-result survey says so in the
  UI/log.
- **`InlineStepper`** for any numeric input (none expected here; Bulk Rename's seq
  fields already exist — don't rebuild them).
- **Build is Windows-only** (net48/WindowsDesktop SDK) — do NOT attempt `dotnet
  build` on Linux CI; rely on compile-pattern discipline + the user's Windows build.
  All four year configs build from one plain build; watch for the known per-year
  landmines only if touching the csproj (this plan doesn't).
- **View-name/sheet-number setters throw on duplicates** — every existing pre-check /
  skip-and-log around `view.Name =` / `SHEET_NUMBER` set stays exactly where it is;
  the resolver does not take over uniqueness.
- **`ElementId.Value` (long) vs `WorksetId.IntegerValue`** — irrelevant here except:
  don't "modernize" ids while passing through handler code.

---

## 9. Acceptance checklist (verify before calling the work done)

- [ ] Grep `new TokenInput(` → every call site uses the registry ctor.
- [ ] Grep `NamingSlots` → zero hits; file deleted.
- [ ] Grep `TokenInput.Resolve(` → zero hits (shim deleted).
- [ ] Every naming tool: pattern visible, editable, chip-insertable, previewed,
      persisted across window reopen (except Bulk Export which persists via its own
      settings as before).
- [ ] Explode Views S3 shows and honors the pattern.
- [ ] Settings page: create a token bound to a captured sheet parameter → it appears
      in Bulk Export (sheets mode), Bulk Rename (sheets), Place Dependent Views;
      does NOT appear in views-mode pickers. Edit and delete round-trip;
      `NamingTokens.xml` survives Revit restart.
- [ ] No-document launch of settings: page opens, manual entry works, no crash.
- [ ] Missing-parameter run: run log shows one warn per token naming the parameter;
      name uses fallback text; nothing silent.
- [ ] Degenerate pattern (all-empty tokens): warn + deterministic fallback in every
      migrated tool.
- [ ] AppStrings key diff clean for `naming.*` and `globalSettings.nav.naming`.
- [ ] Silent-failure scan reported for the final diff.
