# Plan — WPF-only distribution cleanup

Strip every WebView2 / HTML-UI construct, the `LemoinePreview` sub-project, and the
accumulated working documents out of the repo so `main` is a clean, shippable
WPF-only Revit plugin.

---

## 0. Branch vs. new repo — recommendation

**Use a branch off `main`. Do not create a new repo.**

| | Branch (recommended) | New repo |
|---|---|---|
| Deletion work required | Identical — same files, same edits | Identical |
| Git history / blame | Preserved | Lost |
| Issues, PRs, CI, existing clones | Preserved | Lost / must be recreated |
| Repo size saved | 0 (`.git` is 9 MB total) | ~nothing meaningful |
| Reversibility | `git revert` | none |

A new repo buys nothing here — the working-tree cleanup is the same either way, and
`.git` is only 9 MB, so there is no size problem to solve. The one thing a new repo
would give (no WebView2 in history) is not worth losing 122 merged PRs of blame.

Branch: `claude/main-distribution-cleanup-tmkqzk` (already sits exactly on `main` @ `f930070`).

---

## 1. What is being removed — measured footprint

| Group | Files | ~LOC |
|---|---:|---:|
| `Source/Framework/Web/` (host, bridge, assets, bespoke web windows) | 24 | 11.6k (incl. assets) |
| `Source/Web/` (HTML/CSS/JS assets) | 23 | ↑ same bucket |
| `*WebTool.cs` ports under `Source/Tools` + `Source/Commands` | 34 | 9.4k |
| Developer-panel commands + debug harnesses | 6 | — |
| **Total deleted** | **~87 files** | **~21k LOC** |
| Commands edited (drop the `if (WebToolLauncher.Enabled)` branch) | 27 | — |

### Verified: no functionality is lost

Every web tool has a live WPF original still in the tree. Checked all 34 ports plus
the 7 bespoke web windows:

- 31 ports map 1:1 to a `*ViewModel.cs` (e.g. `BulkViewsWebTool` → `BulkViewsViewModel`).
- `DeleteFiltersWebTool` → `DeleteFiltersFromProjectViewModel`
- `PushCoordinatesWebTool` → `PushCoordinatesToLinksViewModel`
- `WebScopeBoxManager` → `ScopeBoxManagerWindow.xaml`, `WebClashDefinitions` →
  `ClashDefinitionsWindow.xaml`, `WebAutoFilters` → `FiltersSettingsWindow.xaml`,
  `WebLinkAudit` → `LinkAuditWindow.xaml`, `WebToolsOverview` → `ToolsOverviewWindow.xaml`,
  `WebSettings` → `GlobalSettingsWindow`, `WebLegendCreator` → `LegendCreatorTabContent`.

`Strings/en/*.json` is shared between both stacks — **nothing there is removed**.
`LEMOINE_UI.md` has zero WebView2 references — untouched.

---

## 2. Files deleted

### 2.1 Web framework — whole folder
`Source/Framework/Web/` (all 24): `WebAssets`, `WebAutoFilters`, `WebAutoFiltersWindow`,
`WebBridge`, `WebClashDefinitions`, `WebClashDefinitionsWindow`, `WebHost`, `WebJson`,
`WebLegendCreator`, `WebLegendCreatorWindow`, `WebLinkAuditWindow`, `WebNaming`,
`WebScopeBoxManager`, `WebScopeBoxManagerWindow`, `WebSettings`, `WebSettingsWindow`,
`WebStepFlowWindow`, `WebTool`, `WebToolBase`, `WebToolLauncher`, `WebToolsOverviewWindow`,
`WebUiSettings`, `WebUiThread`, `WebWindowBase`.

### 2.2 Web assets — whole folder
`Source/Web/` (all 23): 9 HTML pages, `lemoine-bridge.js`, `lib/` (10 JS/CSS),
`debug/` (3 harness pages).

### 2.3 Tool ports — 34 `*WebTool.cs`
`Source/Tools/**`: Ceilings (4), CopyFromLink (3), Dimensioning (3), Export (2),
FiltersLegends (2), Modify (5), Setup (4), Sheets (3), Views (8).

### 2.4 Developer panel — commands + harnesses
- `Source/Commands/Debuggers/` → `ToggleWebUiCommand.cs`, `WebPilotCommand.cs`,
  `WebView2TestCommand.cs` (folder becomes empty → removed)
- `Source/Tools/Debuggers/` → `WebPilotTool.cs`, `WebPilotEventHandler.cs`,
  `WebView2TestTool.cs` (folder becomes empty → removed)
- `Source/Commands/FiltersLegends/WebDeleteFiltersCommand.cs`
- `Source/Commands/Setup/WebPushCoordinatesCommand.cs`

### 2.5 LemoinePreview sub-project
- `LemoinePreview/` (6 files: `App.xaml`, `App.xaml.cs`, `DemoTool.cs`,
  `LemoinePreview.csproj`, `PreviewMainWindow.cs` — 100 KB, `PreviewState.cs`)

### 2.6 Dead tooling
- `.claude/skills/web-wpf-align/` — the skill exists only to align web ports to WPF
- `devtools/phase5_namespace_rename.py`, `devtools/phase6_lemoine_rename.py` — spent one-offs

---

## 3. Files edited

### 3.1 `Source/App.cs`
- Drop `WebPilotHandler` / `WebPilotEvent` statics (lines ~172–174) and their
  construction (~317–319).
- Delete the entire **Developer** ribbon panel block (~621–657) — all 5 buttons were
  WebView2. `application.CreateRibbonPanel("Lemoine Tools", "Developer")` goes with it,
  so the plugin ships with Setup → Views → Ceilings → … → Settings only.

### 3.2 27 command files — remove the web branch
Each carries a self-contained block of the shape:
```csharp
if (LemoineTools.Framework.Web.WebToolLauncher.Enabled)
{
    LemoineTools.Framework.Web.WebToolLauncher.Open("key", () => { …web tool graph… });
    return Result.Succeeded;
}
```
Delete the block and any now-unused `using`. The WPF path below it is untouched.
Affected: Ceilings (4), CopyFromLink (3), Dimensioning (4), Export (2), FiltersLegends (3),
Modify (5), Setup (4), Sheets (3), Views (3), plus `OpenOverviewCommand`, `OpenSettingsCommand`.

### 3.3 `LemoineTools.csproj`
- Remove `<WebView2Version>` property and the `Microsoft.Web.WebView2` `PackageReference`.
- Remove the `CopyWebView2Loader` target (~167–175).
- Remove the `CopyWebAssets` target (~194–220).
- Remove the `LemoinePreview\**\*` `Remove=` exclusions (lines 112–115) — the folder is gone.
  Keep the `LemoineNavisworks\**\*` exclusions: CLAUDE.md requires them to stay
  unconditional because an untracked `obj/` from another branch can still poison the build.

### 3.4 `LemoineTools.sln`
Remove the `LemoinePreview` `Project(...)`/`EndProject` pair and its two
`GlobalSection(ProjectConfigurationPlatforms)` lines.

### 3.5 `CLAUDE.md`
- Delete the **WebView2 UI Migration** section wholesale (rules R1–R4, porting rules,
  `plan-webview2-ui-migration.md` pointer).
- In **Edit Tool — C# Unicode Escape Sequences**: drop the `Source/Web/` JS paragraph.
- In **Step Flow — Conditional & Data-Dependent Steps**: drop `stepflow.js` /
  `IWebRunPausable` / web-shell parity mentions; keep the WPF rules.
- In **Crashes & Large Ambiguous Issues**: drop the "WebView2 Test harness" pointer.
- Add a short **Distribution** note recording that the plugin is WPF-only and that
  WebView2 was removed at this commit (so a future session doesn't re-add it).

---

## 4. Non-critical working documents

Root is carrying 17 working `.md` files plus `docs/plans/` (17 more). Proposal —
**delete all of the following**; they are superseded working notes, and every one stays
recoverable from git history:

**Web-specific (delete — dead with the code):**
`plan-webview2-ui-migration.md`, `plan-webview2-testing-menu.md`,
`plan-scopebox-manager-web-port.md`, `web-migration-questions.md`, `web-migration-status.md`

**Completed plans / reviews (delete):**
`plan-create-sheets-place-views-mode.md`, `plan-function-review-framework.md`,
`plan-naming-tokens-rework.md`, `plan-plugin-upgrade-pass.md`,
`plan-push-coordinates-perf.md`, `plan-ribbon-lifecycle-and-repo-restructure.md`,
`review-bulk-export-rename.md`, `review-ceilings.md`, `review-dimensioning.md`,
`review-setup.md`, `review-split-elements.md`, `audit-unused-files.md`,
`docs/plans/` (all 17)

**Kept:** `CLAUDE.md`, `LEMOINE_UI.md`, `docs/TESTING_POLICY.md`,
`devtools/audit_unused_files.py`, `devtools/render_layout_snapshot.py` (still used by
the `/revit-navisworks-ui` mockup workflow), `libs2025-2027/` placeholder READMEs
(needed for the multi-year build).

> **Confirm this list before I delete it** — this is the one part of the plan that is a
> judgement call rather than mechanical.

---

## 5. Verification (Linux limits apply)

This repo **cannot be compiled on Linux** (`UseWPF` + net48 needs
`Microsoft.NET.Sdk.WindowsDesktop`). So verification here is static:

1. `grep -rn "WebView2\|CoreWebView2\|WebToolLauncher\|IWebTool\|Framework.Web"` over
   `--include=*.cs --include=*.csproj --include=*.sln` returns **zero** hits.
2. `grep -rn "LemoinePreview"` returns zero hits outside git history.
3. Every deleted `*WebTool` type name has no remaining reference.
4. Every `AppStrings.T(...)` key referenced in an edited command still resolves in
   `Strings/en/` (the shared JSON is untouched, so this should be a no-op check).
5. Per-file `using` audit on the 27 edited commands — no orphaned
   `using LemoineTools.Framework.Web;`.
6. CLAUDE.md post-change silent-failure scan on the diff.

**A Windows build is required before merge.** The deletions are wide, and only
`dotnet build -c Release2024` on Windows can confirm the tree still compiles. I will
not report this task green on a Linux static check alone.

---

## 6. Commit sequence

One logical change, split into reviewable commits on
`claude/main-distribution-cleanup-tmkqzk`:

1. `Remove WebView2 framework and HTML assets`
2. `Remove web tool ports and web branches from commands`
3. `Remove Developer ribbon panel and WebView2 debug harnesses`
4. `Remove LemoinePreview project`
5. `Drop WebView2 build targets and package reference`
6. `Prune superseded plan and review documents`
7. `Update CLAUDE.md for WPF-only distribution`

No PR unless asked.
