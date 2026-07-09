# Plan — Naming Tokens Rework (unified system + user-created tokens)

## Goal

One naming-token system shared by every tool that generates or rewrites names, with
**user-created tokens** backed by element parameters (e.g. a custom shared parameter the
user added to sheets) so the vocabulary is no longer fixed at compile time. Tokens can
read from the **target** element, its **source/parent** element, or **project info**.

---

## 1. Current state — the full inventory

Three divergent mechanisms exist today, each with a hardcoded per-tool vocabulary.

### Mechanism A — `TokenInput` (`{Token}` pattern textbox + chip buttons)

Resolved via `TokenInput.Resolve(pattern, Dictionary<string,string>)`; each run handler
hand-builds its own value dictionary.

| Tool | File | Vocabulary | Pattern persisted? |
|---|---|---|---|
| Bulk Export | `Source/Tools/Export/BulkExportViewModel.cs` | Sheets: `{SheetNumber} {SheetName} {Revision} {IssueDate} {ProjectNumber} {ProjectName} {Year} {Month} {Day}` · Views: `{ViewName} {ViewType} {ProjectNumber} {ProjectName} {Year} {Month} {Day}` | Yes (`BulkExportSettings.xml`) |
| Bulk Rename (sheets/views) | `Source/Tools/Sheets/BulkRename/BulkRenameViewModel.cs` + `BulkRenameEngine.cs` | `{SheetNumber} {SheetName}` or `{ViewName} {ViewType}`, plus `{Seq}` | No |
| Place Dependent Views | `Source/Tools/Sheets/PlaceDependentViews/PlaceDependentViewsViewModel.cs` | `{ParentViewName} {ViewType} {Level} {SheetNumber}` | No |
| Views Bulk Duplicate | `Source/Tools/Views/ViewsBulkDuplicateViewModel.cs` | `{ViewName} {ViewType}` | No |
| Views By Template | `Source/Tools/Views/ViewsByTemplateViewModel.cs` | `{ViewName} {TemplateName} {ViewType}` | No |
| Views By Link | `Source/Tools/Views/ViewsByLinkViewModel.cs` | `{LinkName}` | No |

### Mechanism B — `NamingSlots` (Front / Center / End dropdowns + "Custom" free text)

State in `NamingSlotsState`; token strings are logic identifiers resolved per tool.

| Tool | File | Vocabulary |
|---|---|---|
| Bulk Views by Level | `Source/Tools/Views/LinkViewsLevelViewModel.cs` | `Level, Scope Box, View Type, Custom` |
| Scope Box Creator | `Source/Tools/Views/ScopeBoxes/ScopeBoxCreatorViewModel.cs` | `Building Letter, Level, Level Range, Model Name, Custom` |
| Scope Box Manager (bulk rename) | `Source/Tools/Views/ScopeBoxes/Windows/ScopeBoxManagerWindow.xaml.cs` | `Current Name, Number, Custom` |
| Replicate Dependent Views | `Source/Tools/Views/ReplicateDependentViewsViewModel.cs` | `Host Level, Source View Name, Target View Name, View Type, Dep Suffix, Custom` — **hand-rolled copy of the slot rows, does not even use the shared `NamingSlots` control** |

### Mechanism C — hand-rolled `string.Replace`

| Tool | File | Vocabulary |
|---|---|---|
| Explode Views by Trade | `Source/Tools/Views/ExplodeViews/ExplodeViewByTradeEventHandler.cs` | `{nn} {Source} {Trade}` — pattern is hardcoded (`"{nn}_{Source} - {Trade}"`), **no UI exposes it at all** |

### Problems this inventory shows

1. Vocabularies are compile-time constants — no user-defined tokens anywhere.
2. Only built-in parameters are reachable; a custom/shared sheet or view parameter can
   never feed a name.
3. Two UI paradigms (pattern textbox vs. slot rows) plus one duplicated slot
   implementation (RDV) and one invisible pattern (Explode Views).
4. Token *values* are assembled ad hoc in each run handler — the same concept
   (`{ViewType}`, `{Level}`) is re-implemented repeatedly and can drift.
5. Unknown tokens pass through `TokenInput.Resolve` silently as literal `{Text}`.
6. Only Bulk Export persists its last-used pattern.

---

## 2. Proposed architecture

New folder: `Source/Framework/Naming/`.

### 2.1 `TokenDefinition` + `NamingTokenRegistry`

A single catalog of all tokens — built-ins defined in code, user tokens layered on top.

```csharp
sealed class TokenDefinition
{
    string   Key;            // stable id used in patterns: {SheetNumber}, {u:SheetSeries}
    string   Label;          // chip / dropdown display text (AppStrings for built-ins)
    TokenOrigin Origin;      // BuiltIn | UserParameter
    TokenSubject Subject;    // Target | Source | ProjectInfo | Environment (dates, seq)
    IReadOnlyList<TokenEntity> AppliesTo;  // Sheet, View, ScopeBox, Link, Level, Any…
    // UserParameter only:
    string?  ParameterName;  // display + fallback lookup
    Guid?    ParameterGuid;  // authoritative for shared params (see CLAUDE.md: name
                             // lookup silently picks wrong duplicates — bind by GUID)
    string?  FallbackText;   // used when the parameter is absent/empty (may be "")
}
```

- **User token keys are namespaced** (`{u:SheetSeries}`) so a future built-in can never
  collide with a user token, and patterns stay readable.
- The registry answers `TokensFor(TokenEntity entity, bool hasSource)` so every picker
  only ever shows tokens valid for its context (the existing Bulk Export rule —
  mode-aware vocabulary, never a silent fallback — generalized).

### 2.2 `UserTokenStore` — persistence

- XML via `XmlSerializer` at `%AppData%\LemoineTools\NamingTokens.xml`, same pattern as
  `BulkExportSettings` (DTO **must be `public`** — known XmlSerializer trap).
- Definitions are **global** (machine-level, all projects). Resolution is per-document:
  if the bound parameter doesn't exist in the current document/element, the token
  resolves to its `FallbackText` and the run log gets one `warn` line naming the token —
  never a silent blank.

### 2.3 `TokenContext` + `TokenResolver` — one resolution engine

```csharp
sealed class TokenContext
{
    Document doc;
    Element? Target;        // the element being named (sheet, view, scope box…)
    Element? Source;        // parent view / source view / link, when the tool has one
    int      SequenceIndex; // for {Seq}
    // + prebuilt computed values the tool supplies: Level, LevelRange, TemplateName…
}
```

- `TokenResolver.Resolve(pattern, context)` replaces every registered token:
  - Built-ins route to the existing per-concept code (dates, project info, view type,
    level, etc.) — written **once**, not per handler.
  - User tokens read the parameter from the element indicated by `Subject`
    (GUID-first via `get_Parameter(Guid)`, then `GetParameters(name)` preferring a
    String-storage shared param, then `LookupParameter` last).
  - Unresolvable/unknown tokens: keep the literal `{Text}` in the output **and** push a
    run-log `warn` — visible, not silent.
- Centralized **degenerate-name guard** (moved from Bulk Export): a resolved name that
  is empty or has no alphanumeric character logs a warn and substitutes a deterministic
  fallback (`element.Name`, else element id). All adopting tools get this for free.
- Computed tokens a tool can't supply (e.g. `{Level}` for a legend) simply aren't in
  its registry query — the UI never offers them.

### 2.4 `TokenInput` upgrades (the one pattern editor)

- Chips come from the registry query, grouped (Target · Source · Project · Date/Seq ·
  **Your tokens**).
- A trailing **"＋ New token…"** chip opens a creator popup (`StaysOpen=true` + manual
  dismiss — never `StaysOpen=false`): name the token, pick the subject (target /
  source / project info), pick a parameter from a list enumerated off the live
  document's bindings for that category, optional fallback text. Saves to
  `UserTokenStore`, chip row refreshes.
- Live preview row resolving the current pattern against a sample element (the first
  selected item), same as Bulk Export's existing preview but shared.
- Manage/edit/delete of user tokens also available from a **Naming Tokens** section in
  `GlobalSettingsWindow` (list, edit, delete with "used by pattern X" awareness).

### 2.5 Per-tool pattern persistence

Last-used pattern per tool saved in one small `NamingSettings.xml`
(`toolId → pattern`), so every tool remembers its scheme like Bulk Export already does.
Bulk Export keeps its own settings file (migrating it is churn for no gain); it reads
its patterns as today.

---

## 3. Migration map (tool by tool)

| Tool | Change |
|---|---|
| Bulk Export | Swap chips + resolution to registry/resolver; sheet mode gains user sheet-param tokens; behavior otherwise identical |
| Bulk Rename | Registry-fed chips (incl. user tokens on sheets/views); `{Seq}` becomes a built-in Environment token |
| Place Dependent Views | Registry-fed; `{ParentViewName}` becomes a Source-subject token; sheet-name pattern gains user sheet params |
| Views Bulk Duplicate / By Template / By Link | Registry-fed; no UX change beyond richer vocabulary |
| Bulk Views by Level | Slot tokens re-keyed to registry ids (see open question 2) |
| Scope Box Creator / Manager rename | Same as above |
| Replicate Dependent Views | **Delete the hand-rolled slot rows, use the shared control** — pure debt payoff regardless of question 2's answer |
| Explode Views by Trade | Expose the pattern in the tool UI via `TokenInput` (it already supports `{nn} {Source} {Trade}` internally — the user just can't see or edit it) |

**New adopter candidates found (no naming tokens today):** Explode Views (above) is the
only clear one. Print View writes single files where Revit's own dialog governs naming,
and the remaining creators (ceiling grids, filters, legends) name via their own managed
conventions (`AutoFiltersSettings.MakeFilterName`) that must stay stable — adopting
tokens there would break filter reuse-by-name. So: no other adopters recommended.

---

## 4. Open UX decisions (pick before implementation)

**Q1 — Fate of the Front/Center/End slot UI.** Options:
- **(a) Unify everything on `TokenInput` patterns — recommended.** One mental model,
  free-form separators, user tokens work everywhere with zero extra UI. Slot tools get a
  default pattern equal to their current default slots (e.g. `{BuildingLetter} - {LevelRange}`).
- (b) Keep `NamingSlots` for the four slot tools but feed its dropdowns from the
  registry (user tokens appear as dropdown entries). Lower churn, but two UIs persist
  and slots cap names at three parts.

**Q2 — Where users create tokens.** Options:
- **(a) Both — recommended.** Inline "＋ New token…" chip for in-context creation, plus a
  Global Settings "Naming Tokens" page for edit/delete/overview.
- (b) Inline only (nowhere to manage/delete later).
- (c) Settings page only (breaks the flow — user must leave the tool to add a token).

**Q3 — Token scope.** Options:
- **(a) Global (machine-wide) definitions with per-document fallback — recommended.**
  Simple, works across projects; a missing parameter warns and uses fallback text.
- (b) Per-project definitions stored in the model via Extensible Storage. Travels with
  the model and other users see them, but invisible outside that project and heavier.
  *(Could be a later additive step: "publish token to project".)*

---

## 5. Implementation phases

1. **Framework core** — `TokenDefinition`, `NamingTokenRegistry`, `UserTokenStore`,
   `TokenContext`, `TokenResolver`, degenerate-name guard, `NamingSettings` persistence.
   `TokenInput.Resolve` stays as a thin shim during migration. Strings to
   `Strings/en/naming.json`.
2. **Editor UI** — registry-fed grouped chips, "＋ New token…" creator popup, shared
   preview row, Global Settings "Naming Tokens" section. (Mockup image for approval
   first, per the WPF UI rule.)
3. **Migrate Mechanism A tools** (6 tools) — swap vocabularies/resolution to the
   registry; add per-tool pattern persistence.
4. **Slot tools** (per Q1 outcome) + RDV de-duplication + Explode Views pattern UI.
5. **Silent-failure scan + string-key verification** per CLAUDE.md, then build on
   Windows (all 4 year configs).

Each phase is compilable and shippable on its own; tools not yet migrated keep working
untouched throughout.
