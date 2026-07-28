# Plan — Family Modification Tools (A / B / C)

Formalises `familytoolsplancontext.md` into a buildable plan against the **actual**
codebase on `claude/main-distribution-cleanup-tmkqzk` (WPF-only distribution).

| # | Tool | Purpose | Engine |
|---|------|---------|--------|
| A | **Air Terminal Plan Visibility** | Audit (and optionally fix) 3D geometry inside air-terminal families that does not display in plan | `FamilyBatchProcessor` |
| B | **MEP Clearance Standards** | Force clearance geometry onto the correct subcategory / material, and set the matching project Object Styles | `FamilyBatchProcessor` |
| C | **Valve Clearance Zones** | Generate clearance solids from valves down through the ceiling plane | standalone (authors project geometry) |

Decided in the context notes and carried forward: **audit first, opt-in fix. No auto-fix mode in v1.**

---

## 0. Branch

- Base: `claude/main-distribution-cleanup-tmkqzk` (WPF-only; `Source/Web/`, `Source/Framework/Web/`
  and all 34 `*WebTool.cs` ports are gone).
- Working branch: `claude/formal-plan-markdown-dtb6e6` (harness-designated).
- **Consequence of the base choice: there is no web port to build.** Every tool below is WPF-only —
  one `*ViewModel.cs` implementing `IStepFlowTool`, opened in `StepFlowWindow`. This roughly halves
  the UI surface versus the same work on `main`.

---

## 1. Corrections to the context document

`familytoolsplancontext.md` §8 describes an older/assumed hosting model. These are wrong against
`CLAUDE.md` and the current tree, and following them would reintroduce known Revit crashes:

| Context doc says | Reality on this branch |
|---|---|
| `WindowInteropHelper.Owner = ComponentManager.ApplicationWindow` | **Crashes Revit.** `CLAUDE.md` lists `ComponentManager.ApplicationWindow` under *Revit Crash Constraints*, and the HWND owner was **deliberately removed** from `StepFlowWindow` — the window is intentionally not owned. Do not re-add it. |
| `LemoineControlStyles.InjectInto(Resources, scrollBarWidth: 5)` once per window | The class is `ControlStyles` (`Source/Framework/ControlStyles.cs`), and **`StepFlowWindow` already does this**. A tool implementing `IStepFlowTool` never touches styles, themes, or the title bar. |
| `LemoineMultiSelectTabs` with tab-column width hints | The control is `MultiSelectTabs` (`Source/Framework/Controls/Input/MultiSelectTabs.xaml`). Column sizing is internal. Its real contract: subscribe `SelectionChanged` **before** `SetGroups`, plus `SingleSelect`, `Hierarchy`, and `DisabledItems` (set before `SetGroups`). |
| "Cache findings to XML, same persistence pattern as the Sheet Layout templates" | There is no Sheet Layout template store on this branch. The house pattern is a **per-tool `XmlSerializer` singleton** in `%AppData%\LemoineTools\<Tool>Settings.xml`, and the DTO **must be `public`** or every save/load fails silently. That pattern is for *settings*, not per-document findings — see §4.3 for the recommendation. |
| "`ConfigureFailures()` at transaction start" | Revit's own failures/dialogs are already captured process-wide by `RevitFailureCapture` + `RunLogSink`; call `RevitFailureCapture.BeginRun()` / `RunLogSink.Set(pushLog)` at run start instead of hand-rolling failure config. |
| Stamp Tool C output with a **shared parameter** carrying the source `UniqueId` | Recommend **Extensible Storage** instead (`CopyFromLinkStampSchema` / `CopyLinearStampSchema` are the in-repo references). No shared-parameter file to distribute, no schedule pollution, one quick `ExtensibleStorageFilter` pass to read every stamped element. **Deviation flagged for approval — §7.4.** |

Everything in context-doc §2, §3, §5, §6, §7 (API foundations, hard limitations, rule bodies,
ceiling detection) is accurate and is adopted as written.

**Correct hosting model** — copy `Source/Commands/CopyFromLink/CopyFromLinkCommand.cs` verbatim:
dedicated STA thread, `ManualResetEventSlim` so `Execute()` does not return until the window is
shown, `Dispatcher.Run()` pump, `InvokeShutdown()` on `Closed`, static `_window` re-activation guard,
and a `BuildTool` closure so Reset can rebuild the ViewModel.

---

## 2. What already exists that we reuse (no new framework)

Verified present on this branch:

| Need | Existing asset |
|---|---|
| Tool shell, accordion, progress, log, themes, cancel | `IStepFlowTool` + `StepFlowWindow` |
| Final review step | `IReviewableTool` (framework renders `ReviewSummary` automatically) |
| Steps that depend on an earlier step | `IStepAware` + content-refresh callback |
| Hiding a step | `IConditionalSteps` (never the last step) |
| Handler-rooted callback cleanup | `IToolCleanup.OnWindowClosed` |
| Findings pick list | `MultiSelectTabs` (`Hierarchy`, `DisabledItems`, `SingleSelect`) |
| Numeric input | `InlineStepper` — never a raw `TextBox` |
| Folder picker (backup dir) | `FolderBrowser` |
| Subcategory naming | `TokenInput` + `NamingTokenRegistry` / `TokenResolver` / `NamingPatternStore` |
| Colour picking | `SwatchPicker` / `ColorPickerPanel` |
| Idempotent re-run stamps | `CopyFromLinkStampSchema` (pattern to copy) |
| Cancellation + 5 % log cadence | `RunState`, `RunProgressReporter` |
| Revit failure/dialog capture | `RevitFailureCapture`, `RunLogSink` |
| Per-tool persisted settings | `<Tool>Settings.cs` XML singleton + `IToolSettings` / `ToolSettingsSpec` |
| Text externalisation | `AppStrings.T(key)` + `Strings/en/*.json` |

**Confirmed absent:** any family-editing framework. The only `EditFamily` call in the repo is
`CopyLinearScanHandler.ServeFamilyParams` (`Source/Tools/CopyFromLink/CopyLinearScanHandler.cs:152`),
which is a good micro-precedent (`EditFamily` → read → `Close(false)` in `finally`) but not a batch
engine. `FamilyBatchProcessor` is genuinely new code.

Also absent: any `ReferenceIntersector` usage. Tool C's ceiling detection is new ground.

---

## 3. Recommended shape — three ribbon buttons, one engine

Tools A and B are the same engine with different rule bodies. Two packagings are possible:

| Option | Shape | Trade-off |
|---|---|---|
| **A (recommended)** | Three ribbon buttons: *Family Plan Visibility*, *Clearance Standards*, *Valve Clearance Zones*. A and B share `FamilyBatchProcessor` internally. | Matches every other Lemoine tool — one button, one purpose, one step flow. Rule set per tool is fixed, so the steps stay short. |
| B | One "Family Doctor" button with a rule-checkbox step | Fewer buttons, but the step flow becomes generic ("pick rules → pick families → review → fix"), the review step has to be rule-agnostic, and Tool B's *project-side* pass (Object Styles) has nowhere natural to live. |

Recommending **Option A**. Tools A and B ship as separate buttons on a new **Families** ribbon panel;
Tool C also goes there (its output is family-instance geometry, and it is the only other
family-flavoured tool).

**Ribbon placement:** panels are built in coordination-lifecycle order in `App.cs`
(Setup → Copy from Link → Modify → Ceilings → Views → Filters & Legends → Dimensioning → Sheets →
Export → Settings). Families is content-prep, so it slots **after Copy from Link, before Modify**.

> `RibbonPanel.AddStackedItems` takes **exactly 2 or 3** items. Three buttons fits one stacked call
> — or use three plain `panel.AddItem(...)` calls to match the Setup/Copy panels' style.

---

## 4. Shared engine — `FamilyBatchProcessor`

New folder: `Source/Tools/Families/`.

### 4.1 Contracts

Adopted from the context doc, with two changes:

```csharp
public interface IFamilyRule
{
    string Name { get; }
    bool AppliesTo(Family f);                          // category / parameter gate — no famDoc open
    IEnumerable<RuleFinding> Audit(Document famDoc, FamilyRuleContext ctx);
    bool Apply(Document famDoc, RuleFinding f, FamilyRuleContext ctx);   // returns "changed"
}
```

- `FamilyRuleContext` added: carries the **host project `Document`** (Tool B's project-side pass
  needs it), the nesting depth, and the run's `pushLog` sink so a rule can report as it goes.
- `RuleFinding` gains `NestingPath` (`"AirTerminal_600x600 › Neck › Damper"`) — the context doc's
  §3 recursion requirement is unusable in a review list without it.

```csharp
public sealed class RuleFinding
{
    public ElementId FamilyId;        // top-level family in the project
    public string    FamilyName;
    public string    NestingPath;     // "" for a top-level finding
    public string    Category;
    public string    RuleName;
    public Severity  Severity;        // Info | Warning | Violation
    public string    Description;
    public bool      IsAutoFixable;
    public object    FixPayload;      // form ids, target subcategory, …
}
```

`IsAutoFixable = false` for: parameter-associated `IS_VISIBLE_PARAM`, non-cuttable host category,
imported CAD symbols inside the family, shared nested families, voids. These are listed in the
review as **"review manually"** — never silently dropped. (`CLAUDE.md`: a collector that finds zero
items must say so; the same logic applies to a finding we can't act on.)

### 4.2 Pipeline

1. **Collect** `Family` elements in the project → gate on `IsInPlace` (skip + log), `IsEditable`
   (skip + log), and rule `AppliesTo`.
2. **Audit pass** (read-only): `EditFamily` → recurse into nested families → `Audit` →
   `Close(false)` in `finally`. No `SaveAs`, no `LoadFamily` — genuinely non-mutating.
3. **Cache findings** (§4.3).
4. **Review**: `MultiSelectTabs` grouped by `RuleName`, hierarchy = family → nested path.
   Non-fixable findings passed as `DisabledItems` so they are visible but uncheckable.
5. **Fix pass** on the checked subset only: backup `.rfa` → `Apply` → `LoadFamily` with a
   `LemoineFamilyLoadOptions : IFamilyLoadOptions`.
6. **Report** written to `%AppData%\LemoineTools\Reports\` + the run log.

**Transaction ordering is non-negotiable.** `EditFamily` throws if a transaction is open on the
project document. The `ExternalEvent` handler must therefore run family work **outside** any project
transaction, and open a project transaction only afterwards (Tool B's Object Styles pass).

**Cost is 0.5–2 s per family.** Mandatory: `RunProgressReporter` at the house 5 % cadence, and a
`RunState.CancelRequested` check per family with the standard "Stopped by user — N of M processed"
line. **Cancel cannot roll back a completed `LoadFamily`** — it only stops the queue. The Run step
must say so in plain text before the user starts.

### 4.3 Findings cache — decision needed

The context doc says "cache to XML, same as the Sheet Layout templates". That store doesn't exist,
and the per-tool settings XML is the wrong home for per-document findings. Three options:

| Option | Where | Trade-off |
|---|---|---|
| **1 (recommended)** | In-memory on the ViewModel for the window's lifetime; re-audit on reopen | Zero staleness risk. Costs one audit walk per session. Simplest, and matches how every other Lemoine scan tool behaves (`UpgradeLinksScanHandler`, `CopyFromLinkScanHandler`). |
| 2 | `%AppData%\LemoineTools\FamilyAudit\<doc-hash>.xml`, invalidated on Revit version / doc save time | Survives window close, but goes stale the moment anyone edits a family by hand, and staleness is invisible. |
| 3 | Extensible Storage on the project | Survives, travels with the model, but writes to the user's document during a "read-only" audit — unacceptable for an audit pass. |

Recommending **Option 1**, plus an explicit **"Export findings to CSV"** button on the review step so
a long audit can be handed to someone else without re-running it.

### 4.4 Backups

Fix pass only. Before the first `Apply` on a family:
`famDoc.SaveAs(<backupRoot>\<yyyy-MM-dd_HHmm>\<FamilyName>.rfa)`.

`<backupRoot>` is a `FolderBrowser` field on the Options step, persisted in `FamilyToolsSettings`,
defaulting to `%AppData%\LemoineTools\FamilyBackups`. **Loading a family upgrades the `.rfa` to the
current Revit version irreversibly** — the Run step must carry a `WarnBanner` saying so.

---

## 5. Tool A — Air Terminal Plan Visibility

`Source/Tools/Families/PlanVisibility/`

**Rule body:** for each `GenericForm` and nested `FamilyInstance` — read `GetVisibility()`, set the
plan/RCP-cut flag plus the detail-level flags, write back `SetVisibility()`.

> ⚠ **`FamilyElementVisibility` member names have shifted across Revit versions and this repo cannot
> be built on Linux** (`UseWPF` + net48 needs `Microsoft.NET.Sdk.WindowsDesktop`). The exact property
> set is resolved by **probe 1 of the Phase 0 harness** (§10), which reflects the real member surface
> for each Revit year. Do not write the rule body from memory.

**Audit-only findings (reported, never auto-fixed):** void form; `IS_VISIBLE_PARAM` associated to a
family parameter (writing the raw boolean is silently reverted on regeneration); host category not
cuttable (`Category.IsCuttable` is read-only); shared nested family filtered by its own category's
project V/G; imported CAD symbol.

**View-level causes** — view range, hidden-in-view, phase filters, design options, closed worksets,
view filters, plan regions — are outside family control entirely. Tool A should say so once, clearly,
in the review note, because view range is the single most common cause of "the family disappeared in
plan" and a user who fixes 40 families for nothing will not trust the tool again.

**Tool A ships with the fix pass in v1** — decided by the user, who has a specific need for the bulk
modification, not just the report. The audit-first / opt-in-fix structure still stands (nothing is
changed without an explicit checked selection), and the standing warnings apply: backup `.rfa` before
the first `Apply`, and the irreversible-version-upgrade banner on the Run step.

Because the fix pass is in scope, one extra guard is worth building: a **"forms already visible in
plan" count** in the review, so a user can see at a glance how much of the library the run would
actually change. A rule that reports "312 families, 4 need fixing" is very different from one that
reports "312 families, 312 need fixing" — the second usually means the rule is wrong, not the library.

**Steps:** `scope` (category / family picker) → `rules` (which checks to run) → `review` (findings,
checkbox selection, CSV export) → `run` (backup, fix, log).

---

## 6. Tool B — MEP Clearance Standards

`Source/Tools/Families/ClearanceStandards/`

**Two passes. Doing only one is the usual failure mode** — families carry a *subcategory name*; the
colour lives in the project's Object Styles.

**Pass 1 — family side** (no project transaction open):
`famDoc.Settings.Categories.NewSubcategory(...)` → assign every clearance form's `Subcategory` →
assign the material → `LoadFamily`.

**Pass 2 — project side** (its own project transaction, after all family work):
create the matching subcategory → set line colour / weight / pattern in Object Styles → set material
shading, surface pattern, transparency.

> **Material-carried appearance travels with the family; line colour does not.** If the result must
> hold in a model that doesn't run this tool, put it in the material.

**Reuse:** subcategory naming is a natural `TokenInput` consumer — one pattern, resolved by
`TokenResolver`, read by both passes, persisted via `NamingPatternStore`. Colour selection reuses
`SwatchPicker` / `ColorMemory` from the Auto Filters colour work.

**Object Styles caveat carried from `CLAUDE.md`:** a *foreground* pattern **colour** set with no
pattern **id** gives colour with no fill pattern (`<No Override>`); solid fills belong on the
**background**. Putting a solid fill on the foreground hides the element's own linework.

**Steps:** `scope` → `naming` (subcategory pattern via `TokenInput`) → `graphics` (colour, pattern,
material) → `run` (audit → review → fix).

**Open:** does Tool B also need to run against the **on-disk** family library (`OpenDocumentFile` →
edit → `SaveAs`), or only families already loaded in the project? This materially changes the scope
step (`FolderBrowser` + recursive `.rfa` walk vs. a project family picker) and the backup story.
Question #5 in §9.

---

## 7. Tool C — Valve Clearance Zones

`Source/Tools/Families/ValveClearance/`

Generated and plugin-driven, not authored per instance.

### 7.1 The clearance family

Unhosted **Generic Model** (cuttable, so it shows in plan), parametric box, instance parameters
`Width` / `Depth` / `Top Offset` / `Below Ceiling`, on a dedicated subcategory so it can be switched
in view templates and filtered in Navisworks.

**Decision needed:** ship the `.rfa` as an embedded resource loaded on demand, or require the user
to point at one? Recommend **embedded + `LoadFamily` on first run**, with a settings override —
otherwise the tool is unusable out of the box. (An embedded `.rfa` is version-locked to the Revit
year it was authored in; it will need one copy per supported year, matching the `libs2025/`…
`libs2027/` pattern.)

### 7.2 Ceiling detection

```csharp
var ri = new ReferenceIntersector(ceilingFilter, FindReferenceTarget.Face, view3d);
ri.FindReferencesInRevitLinks = true;
var hit  = ri.FindNearest(origin, XYZ.BasisZ.Negate());
double drop = hit.Proximity;   // host coordinates — directly usable
```

`Proximity` is in host coordinates, so no manual link-transform maths for the height calculation.
`GetLinkDocument()` is only needed to read ceiling type/thickness — avoidable if the overshoot is
specified from the top face.

**Traps to encode:**
- The `View3D` must be **non-template** and must actually show the link (loaded, not hidden-in-view,
  worksets open). **Build a dedicated 3D view programmatically** rather than trusting an existing one
  — but note that creating it needs a transaction and adds a view to the user's project. Recommend:
  create → use → delete in the same run, and log that it happened.
- **Soffits and bulkheads are frequently Walls or Generic Models, not Ceilings.** The target filter
  must be a **configurable multi-category list**, or zones punch straight through them.
- No ceiling found → **do not guess.** Log it; the user chooses skip vs. fallback height.

### 7.3 Valve source — saved rule set, not a hardcoded category

Valve modelling varies by team (Pipe Accessories vs. Pipe Fittings vs. Mechanical Equipment). Ship a
**saved rule set**: category (multi) + type-name keywords + optional parameter match, persisted in
`ValveClearanceSettings`. Category picker is `MultiSelectTabs` fed from
`AutoFiltersSettings.CaptureFilterableCategories(doc)` (captured on the Revit main thread in the
command, as `CopyFromLinkCommand` does) with `AutoFiltersSettings.CategorySubcategories` as
`Hierarchy`.

### 7.4 Idempotency — Extensible Storage, not a shared parameter *(deviation, needs approval)*

Context doc §7 says stamp a **shared parameter** with the source valve's `UniqueId` + computed
ceiling elevation. Recommending **Extensible Storage** instead, following
`CopyFromLinkStampSchema.cs` exactly: hardcoded `Schema` GUID (never regenerated), `Schema.Lookup`
guard, fields `Version` / `SourceKey` / `CeilingElevation` / `RunId`, read back in one pass via
`new FilteredElementCollector(doc).WherePasses(new ExtensibleStorageFilter(SchemaGuid))`.

Why: no shared-parameter file to distribute or keep in sync, no schedule/tag pollution, and it is
already the house pattern for exactly this problem (Copy from Link, Copy Linear).

Cost: the stamp is invisible to the user in the Revit UI. If the MEP team needs to *see* or
*schedule* the source valve id, the shared parameter is the right call and I'll build that instead.

Reconcile on re-run: key present + elevation matches → skip; key present + elevation differs →
report **"ceiling moved"** and rebuild; key absent → create; stamped zone whose valve is gone →
delete. Without this the tool is single-use.

**Steps:** `source` (valve rule set) → `ceiling` (target categories, overshoot, no-ceiling policy) →
`box` (footprint sizing) → `run`.

---

## 8. File inventory

### New — shared engine (`Source/Tools/Families/`)
| File | Purpose |
|---|---|
| `FamilyBatchProcessor.cs` | EditFamily → recurse → audit/apply → Close(false) loop; progress, cancel, backup |
| `IFamilyRule.cs` | `IFamilyRule`, `FamilyRuleContext` |
| `FamilyRuleModels.cs` | `RuleFinding`, `Severity`, findings grouping helpers |
| `LemoineFamilyLoadOptions.cs` | `IFamilyLoadOptions` — `OnFamilyFound` / `OnSharedFamilyFound` |
| `FamilyBackupWriter.cs` | Dated backup folder + `SaveAs` |
| `FamilyToolsSettings.cs` | `public` XML singleton (backup root, recursion depth, report folder) |
| `FamilyFindingsExport.cs` | CSV export of the review list |

### New — Tool A (`Source/Tools/Families/PlanVisibility/`)
`PlanVisibilityRule.cs`, `PlanVisibilityViewModel.cs`, `PlanVisibilityAuditHandler.cs`,
`PlanVisibilityFixHandler.cs` *(fix handler only if §5 fix pass is approved)*

### New — Tool B (`Source/Tools/Families/ClearanceStandards/`)
`ClearanceSubcategoryRule.cs`, `ClearanceStandardsViewModel.cs`, `ClearanceStandardsAuditHandler.cs`,
`ClearanceStandardsRunHandler.cs`, `ClearanceObjectStyles.cs`, `ClearanceStandardsSettings.cs`

### New — Tool C (`Source/Tools/Families/ValveClearance/`)
`ValveClearanceViewModel.cs`, `ValveClearanceScanHandler.cs`, `ValveClearanceRunHandler.cs`,
`ValveClearanceGeometry.cs`, `CeilingProbe.cs`, `ValveClearanceStampSchema.cs`,
`ValveClearanceSettings.cs`, `ValveRuleSet.cs`

### New — commands (`Source/Commands/Families/`)
`PlanVisibilityCommand.cs`, `ClearanceStandardsCommand.cs`, `ValveClearanceCommand.cs`
— each a copy of `CopyFromLinkCommand`'s STA-thread shell.

### New — strings (`Strings/en/`)
`families.planVisibility.json`, `families.clearanceStandards.json`, `families.valveClearance.json`,
`families.common.json` (shared engine log lines, backup/upgrade warnings)

### Edited
| File | Change |
|---|---|
| `Source/App.cs` | 3 static handler+`ExternalEvent` pairs per tool; new **Families** ribbon panel between Copy from Link and Modify; 3 `Btn(...)` registrations |
| `Source/Framework/ToolsOverviewCatalog.cs` | New `families` `OverviewCategory` + 3 `OverviewTool` entries |
| `Strings/en/ribbon.json` | Panel + button labels/tips |
| `Strings/en/overview.json` | Category intro + per-tool blurbs/examples |
| `LEMOINE_UI.md` | Document the Families panel and the `IFamilyRule` contract |
| `CLAUDE.md` | Append verified family-API constraints once confirmed on Windows |

`LemoineTools.csproj` needs **no** change — SDK-style globs pick up new `.cs` files automatically.
The embedded clearance `.rfa` (§7.1) does need an `EmbeddedResource` entry, per Revit year.

---

## 9. Decisions needed before implementation

Numbered so they can be answered in one message.

1. **Packaging** — three ribbon buttons on a new Families panel (recommended), or one "Family Doctor"?
2. **Findings cache** — in-memory + CSV export (recommended), or persisted per-document XML?
3. **Tool C stamp** — Extensible Storage (recommended), or the shared parameter the context doc specifies?
4. **Tool C family** — embed the `.rfa` per Revit year (recommended), or require the user to supply one?
5. **Tool B on-disk library** — loaded families only in v1, or `.rfa` folder walk too?
6. **Valve source categories** — which categories does the MEP team actually model valves in? (Seeds the default rule set; the rule set itself is configurable either way. **Probe 4 of the Phase 0 harness answers this empirically** if you run it against a real model.)
7. **Clearance box footprint** — fixed per valve size, per type, or read from a parameter?
8. **Overshoot below ceiling** — one global value, or per valve type?

**Settled:** Tool A ships with the fix pass, not audit-only (§5).

Items 6–8 are the context doc's own open questions and are the only ones that need someone outside
this repo to answer. 1–5 I have recommendations for and can proceed on those defaults if you'd rather
not decide each one.

---

## 10. Suggested phasing

| Phase | Content | Gate |
|---|---|---|
| **0** | **Probe harness — BUILT, awaiting a Windows run** (see below) | Blocks A and C |
| 1 | Shared engine + Tool A (audit + fix) | Proves the batch walk on real families at real cost |
| 2 | Tool B (both passes) | Reuses the engine; adds the project-side transaction |
| 3 | Tool C | Independent of 1–2; can run in parallel if preferred |

### Phase 0 — the probe harness

Phase 0 was originally written as a "verification spike", which is not something that can be done
from here: this repo cannot be built on Linux, and three of the plan's load-bearing assumptions are
only answerable against a live Revit. So Phase 0 is delivered as a **harness you run once on
Windows**, following the `CLAUDE.md` build-a-debugger-first discipline.

| File | Role |
|---|---|
| `Source/Tools/Debuggers/FamilyApiProbeHandler.cs` | All four probes, on the Revit main thread |
| `Source/Tools/Debuggers/FamilyApiProbeViewModel.cs` | `IStepFlowTool` UI — probe toggles, category scope, sample size |
| `Source/Commands/Debuggers/FamilyApiProbeCommand.cs` | STA-thread launcher |
| `Source/App.cs` | Handler pair + a temporary **"Phase 0 (temp)"** ribbon panel behind `ShowFamilyApiProbe` |

**What each probe settles:**

1. **`FamilyElementVisibility` surface** — reflects every property, method, constructor and the
   `FamilyElementVisibilityType` enum for whichever Revit year is running, plus `GenericForm`'s
   visibility/solid/subcategory members. This is the one hard blocker on Tool A's rule body, and
   reflection is the correct instrument because *which members exist* is the actual question.
2. **`Category.IsCuttable`** — dumps every model category. Bounds which categories Tool A can help
   at all (Furniture, Lighting Fixtures, Planting, Entourage, Mass are non-cuttable and read-only).
3. **`EditFamily` walk** — times `EditFamily` per family, counts forms / voids / nested / shared
   instances, counts forms whose `IS_VISIBLE_PARAM` is **parameter-associated** (the silent-revert
   case), and **tests whether `famDoc.EditFamily(nested)` recursion works at all** — the engine's
   whole nesting story depends on that one answer. Projects the median to the full library so we
   know up front whether a full audit is a 2-minute or a 2-hour operation.
4. **`ReferenceIntersector` into links** — builds a dedicated non-template `View3D`, shoots down
   from sample instances, and reports for each hit whether it came from a **linked** document, which
   document, which category, and the `Proximity` drop. Confirms `FindReferencesInRevitLinks` works
   on your models and shows how often soffits come back as Walls or Generic Models instead of
   Ceilings.

**Safety:** read-only on the model apart from one temporary 3D view, created and deleted within the
run; if deletion fails the log names the view so it can be removed by hand. The `EditFamily` walk
opens with `Close(false)` in a `finally` and never saves or loads, so no `.rfa` is touched or
version-upgraded. Cancel is honoured per family.

**Output:** a report at `%AppData%\LemoineTools\Reports\family-api-probe-<timestamp>.txt`, with
summary lines in the run log. Attach that file and I can write Tools A and C against measured facts.

**Deliberate deviation:** the harness's strings are hardcoded rather than routed through
`AppStrings`. `CLAUDE.md` exempts developer diagnostics, and this harness is scheduled for deletion —
adding `Strings/en/` entries for text we are about to remove is waste. Every file carries a
delete-me header naming the other pieces to remove with it.

Phase 1 remains the risky one — if `EditFamily` on a real library is much slower than 2 s/family, the
audit-then-review model needs rethinking before Tools B and C are built on top of it. Probe 3's
projection is what tells us that before any engine code is written.

---

## 11. Standing constraints these tools must honour

Pulled from `CLAUDE.md` — each has broken Revit before.

- No `ComponentManager.ApplicationWindow`, no HWND owner on the tool window.
- Global-event subscriptions use **named** handlers detached on `Closed`, marshalled with
  non-blocking `BeginInvoke` guarded by `Dispatcher.HasShutdownStarted`.
- No `Popup StaysOpen=false`.
- Static handlers clear their per-run payload in a `finally`; ViewModels implement `IToolCleanup` and
  null every parked callback.
- Every deliberately-swallowed exception goes through `DiagnosticsLog.Swallowed(context, ex)` — never
  an empty `catch {}`.
- A collector finding **zero** items logs "No … found" explicitly.
- All user-facing text through `AppStrings.T(...)`; rewiring existing literals uses a count-checked
  Python `str.replace()` script, never the Edit tool.
- Every numeric field is an `InlineStepper`.
- A conditional step is never the last step.
- Post-change silent-failure scan before reporting complete.
- `WorksetId.IntegerValue` (int) vs `ElementId.Value` (long) — if worksets are read anywhere.
- Build and test on **Windows only**; a plain `Debug`/`Release` build fans out to all four Revit years.
