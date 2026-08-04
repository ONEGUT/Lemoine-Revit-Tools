# Plan — Bulk Export: Sets, Ordering & Set-Aware PDF Output

**Status:** proposal — awaiting approval, answers to §3, and **Task 0** (§4).
**Revision:** v2, after an adversarial review of v1. The review and what it changed are in
Appendix A; every finding is traceable to a section here.

**The user's hard constraint:** *no functionality is lost when it comes to export types.* PDF, DWG,
NWC and IFC — every option currently exposed — survives unchanged. §12 is the checklist that
enforces it.

---

## 1. What was asked for

> "It doesn't work well for exporting large PDF sets. I need to be able to easily select and group
> sheets together, order them and print the multiple sets as one set or separate."

Three capabilities: **group**, **order**, **one-or-separate**. Today the tool has a weak version of
the first and none of the other two.

---

## 2. What is actually broken (evidence, not opinion)

Read: `Source/Tools/Export/BulkExportViewModel.cs`, `BulkExportEventHandler.cs`,
`BulkExportPrintSetHandler.cs`, `PrintSetModels.cs`, `ExportOptionsFactory.cs`,
`Source/Framework/Controls/Input/BrowserTreePicker.xaml.cs`, `Source/Framework/BrowserTree.cs`.

### 2.1 The tool cannot express an order at all

`BrowserTreePicker.SelectedIds` is a `HashSet<long>` (`BrowserTreePicker.xaml.cs:37,42`) and
`SelectionChanged` hands that set out directly (`:130`, `:419`). The ViewModel materialises it in
enumeration order (`BulkExportViewModel.cs:331-338`), the handler consumes that order
(`BulkExportEventHandler.cs:116-119`), and the combined PDF call passes it through unchanged
(`:410`).

`HashSet<T>` enumeration order is unspecified — it tracks insertion until a removal frees a slot a
later add reuses, i.e. it holds until the user unchecks something and then quietly stops. **No UI
anywhere in the tool lets a user state an order.**

Two separate consequences, worth keeping apart because Task 0 resolves only the second:

- **Per-item outputs** (per-sheet PDF, DWG, NWC, IFC): filenames and any sequence numbering derive
  from this order. Broken today, fixable entirely on our side.
- **Combined PDF page order**: depends on whether Revit honours the id list — **unverified, see §4.**

### 2.2 Grouping requires writing to the Revit model

Groups can only be existing `ViewSheetSet` print sets (`BuildS2`, `:399-485`). Creating one runs a
transaction that permanently adds a `ViewSheetSet` to the project
(`BulkExportPrintSetHandler.cs:53-62`) — which syncs to central on a workshared model. Three
throwaway packs for one issue should not mean three permanent model elements.

### 2.3 Print-set membership is unordered *and* unorderable

`ViewSheetSet.Views` is a `ViewSet`; `Collect` reads it in whatever order Revit yields
(`BulkExportPrintSetHandler.cs:88`). No reorder UI exists. The grouped path cannot produce an
ordered issue PDF either.

### 2.4 "One set or separate" does not exist — and the Combine toggle is silently dead

In print-set mode PDF is hardcoded `combine: true`, once per set (`BulkExportEventHandler.cs:301`).
`_combinePdf` is read from S4 and passed to the handler, but `ExportPrintSetMode` never consults
`CombinePdf`. **The Combine toggle does nothing whenever any print set is checked.**

### 2.5 Two export paths, and one ignores the other's input

`PrintSets.Count > 0` routes to `ExportPrintSetMode`, which explicitly does not honour the Step 1
selection (`:255-258`) — while NWC/IFC in the same run *do* come from the Step 1 selection
(`:174-187`). One run, two contradictory notions of what is being exported. It is papered over
with a note, which is evidence the model is wrong, not that the note suffices.

### 2.6 A set's combined PDF is named after an arbitrary member

With a pattern override, the whole set's file is named by resolving that pattern against
`memberIds[0]` (`:300`). A 40-sheet Architectural pack is named after whichever sheet Revit listed
first. No set-level tokens exist.

### 2.7 Re-running silently overwrites the previous issue

`MakeUniqueName` (`:691-709`) de-duplicates within a single run only. Nothing checks the disk.
Export "Tender Issue", export it again next week, and the first issue is gone without a word. On a
300-file deliverable that is data loss, not a nicety.

### 2.8 A large combined export reports nothing

CLAUDE.md requires a run-log line every ~5%. A combined PDF is one `doc.Export` counted as one op
(`:393`), so the log goes silent for minutes, `RunState.CancelRequested` is unreachable mid-call,
and there is no elapsed time and no warning that cancel won't bite.

### 2.9 Nothing about a set survives the window closing

`_selectedPrintSetIds`, `_patternOverrides`, `_pdfOverrides`, `_dwgOverrides`
(`BulkExportViewModel.cs:130-133`) are in-memory fields. Re-issuing next month means rebuilding
every override by hand.

### 2.10 Prior art — what was tried and deleted

Commit `056eb09` replaced a hand-rolled pack editor with Revit print sets. The deleted
`SheetPackLayoutEditor` (366 lines) was a two-column shuttle with Up/Down buttons keyed by
**sheet-number strings** — it broke on renumber, could not hold views, and read the selection at
construction (the exact `IStepAware` bug CLAUDE.md documents).

**This plan is not a revert.** §8.2 states the four design constraints that make the new editor a
different thing, so a reviewer can check them rather than take it on trust.

---

## 3. Decisions — settled

D0, D1, D2 and D4 were answered by the user. D1 and D2 went **against** my recommendation; the
design below implements what was chosen, with the risks I raised converted into mitigations rather
than re-argued.

### D0 — Structural only ✅

The complaint is grouping, ordering and one-or-separate. Speed at 300 sheets is acceptable, so no
throughput investigation. §7.4's honest progress reporting still applies — appearing hung is a
usability problem even when the run time is fine.

### D1 — Assign while selecting ✅ (option A)

A **target-set control above the Step 1 tree**; checking a sheet files it into the active set. One
pass through the tree, no staging column, no second list to shuttle through.

The objection I raised stands and has to be designed out: **the target set is modal state, and
invisible modal state is how 40 sheets end up in the wrong set.** So the design (§8.1) makes it
loud rather than assuming the user will remember:

1. The target is a **persistent labelled bar**, not a small dropdown tucked in a corner.
2. Each set gets an **accent colour**, shared by the target bar and the badges.
3. **Every checked row in the tree carries a badge naming the set it went into** — this is the
   whole mitigation. State that is visible on the row it applies to is no longer modal state.
4. A **live count** on the target bar (`Architectural — 42`).

(3) requires a small, optional, backwards-compatible addition to `BrowserTreePicker` — see §8.1.

Consequence for Step 2: it is no longer where assignment happens. It becomes review, reorder,
rename and overrides, with move-between-sets kept only as a way to fix mistakes.

### D2 — Extensible Storage in the model ✅ (option 2)

Sets travel with the model and the whole team shares them automatically — which is exactly what
"re-issue next month" needs, and it removes the machine-local weakness of a sidecar.

The costs I raised are real and are handled in §5.3 rather than waved away: it needs a transaction
and write access on every save, it puts tool data in the project, and on a workshared model the
storage element syncs to central and can be checked out by another user. The mitigations are:
**write only on explicit Save and on Run** (never per drag), **a `DataStorage` element rather than
`ProjectInfo`**, **one serialized string field** instead of fighting ES's field types, and **the
tool stays fully usable in memory when the document cannot be written**.

### D3 — What happens when the output file already exists?

Today: silent overwrite (§2.7). Proposal — an **If a file exists** control on the Output step:
`Overwrite` (default, matching Revit) / `Skip` / `Add suffix`, and the count surfaced as a
**`ReviewWarning` banner** — not a summary row — so it reads as an alert rather than another value
in a list: *"12 of 214 files already exist in this folder and will be overwritten."*

**Recommendation: as described. Confirm Overwrite is the right default.** *(still open — the only
decision not yet settled)*

### D4 — Ordering first, then sets ✅

- **Phase 1 — ordering.** Give the selection a defined order (Project Browser order by default) and
  thread it through to the handler. **No UI change**, so no mockup gate — this is a pure defect fix
  and lands as its own reviewable commit.
  Scope is honest only after Task 0: it definitively fixes per-item output order (§2.1, first
  bullet); whether it fixes combined-PDF page order is what Task 0 decides.
- **Phase 2 — sets.** §5–§9. Gated on Task 0 **and** on approved mockups (§9).
- **Phase 3 — the export plan preview.** §10.3.

---

## 4. ⚠ Task 0 — the gate

**Does Revit honour the `ElementId` list order in a combined PDF? This is unverified, and the
"order them" half of the request rests entirely on it.**

I could not verify it here: no .NET SDK and no disassembler in this environment, and CLAUDE.md
forbids confirming Revit API behaviour by string-searching `RevitAPI.dll`. The current code already
depends on the assumption (`BulkExportEventHandler.cs:410`) and nothing in the repo records it ever
being tested.

**The probe (five minutes on Windows).** Take three sheets numbered `A101, A102, A103`. Export
combined with the id list ordered `A103, A101, A102`. Open the PDF and read the page order.

**This forks the design — it is not a footnote with a fallback:**

- **Order honoured** → the plan proceeds exactly as written.
- **Revit sorts internally** → *"multiple sets as one set, in my order"* is **not achievable through
  `Combine=true`**, and no amount of UI fixes that. The honest options then are: (a) per-sheet
  export with a `{Seq}` filename prefix so the folder sorts correctly and any external merge
  preserves order — real value, but not the single file that was asked for; or (b) a managed PDF
  merge, which is a **new decision** about adding a dependency to a codebase whose CLAUDE.md
  deliberately stripped its non-WPF layers. Do not treat (b) as pre-approved.

Run Task 0 **before writing Phase 2.** If the answer is "sorts internally", this plan comes back
for revision rather than being built on a wrong assumption.

---

## 5. The model

New `Source/Tools/Export/ExportSetModels.cs`, absorbing `PrintSetModels.cs`.

```csharp
public sealed class ExportSet             // ordered, named group — the UI's unit of work
{
    public string  Id      { get; set; } = Guid.NewGuid().ToString("N");
    public string  Name    { get; set; } = "";
    public bool    Enabled { get; set; } = true;
    public string  AccentKey { get; set; } = "";  // theme resource key — colours the Step 1
                                                  // target bar and every row badge for this set
    public List<ExportSetMember> Members { get; set; } = new List<ExportSetMember>();  // ORDERED
    public string? PatternOverride   { get; set; }   // null = inherit
    public bool?   PdfOverride       { get; set; }   // null = inherit
    public bool?   DwgOverride       { get; set; }
    public bool?   NwcOverride       { get; set; }   // new — parity with PDF/DWG
    public bool?   IfcOverride       { get; set; }   // new
    public string? SubfolderOverride { get; set; }
}

public sealed class ExportSetMember
{
    public long   IdValue     { get; set; }   // ElementId.Value — the in-session handle
    public string UniqueId    { get; set; }   // the durable persistence key
    public string Label       { get; set; }   // "A101 — Ground Floor", cached for display
    public bool   IsSheet     { get; set; }
    public int    BrowserRank { get; set; }   // DFS index in BrowserTree — default sort key
}

public enum PdfGranularity { PerSheet, PerSet, SingleFile }
```

`PrintSetInfo` stays for Revit print-set interop (§8.3).

### 5.1 The implicit set

**Everything the run exports is a set.** A user who never opens Step 2 has one unnamed set — their
Step 1 selection, in Project Browser order. Today's behaviour, zero extra clicks. This collapses
`ExportIndividualMode` and `ExportPrintSetMode` into one path and deletes §2.5.

### 5.2 Unassigned is visible, warned about, and never a surprise file

Under assign-while-selecting (D1) the target defaults to **All items (no set)** — so a user who
never creates a set checks sheets and they land in **Unassigned**, which *is* the implicit set of
§5.1. That is what keeps `select → Run` a two-touch operation.

Once named sets exist, Unassigned becomes a trap: build "Architectural" and "Structural" out of 214
sheets, forget to switch the target for 7 of them, choose *One PDF per set*, and a third mystery
`Unassigned.pdf` ships to the client. So:

- Unassigned is **enabled by default while no named set exists** — it is the whole run and must
  export.
- The moment the first named set is created it flips to a **warning state**: still listed, still
  toggleable, but the Review step raises *"7 items are not in any set and will export as
  'Unassigned'."*

Nothing is silently dropped and nothing silently ships. With D1 this matters more than it did under
option C, because "forgot to change the target" is precisely the failure mode option A introduces.

### 5.3 Persistence — Extensible Storage (D2)

New `Source/Tools/Export/ExportSetStore.cs` plus an `ExportSetStoreHandler : IExternalEventHandler`
(the write needs Revit's main thread and a transaction).

**Shape**

- One **`DataStorage`** element holding the schema entity — not `ProjectInfo`. `DataStorage` is the
  documented home for tool data with no real host element, and it keeps the project's own
  information element clean.
- A **constant hardcoded `Schema` GUID** with a `Schema.Lookup` guard before `Schema.Create` — the
  same discipline `AutoDimOwnerSchema` already follows in this repo.
- **One string field, `Layout`**, holding the whole set list serialized (XML via `XmlSerializer`,
  matching `BulkExportSettings`). Extensible Storage's field types do not express a list of sets
  each holding an ordered list of members; serializing to one field sidesteps that entirely and
  makes versioning a matter of adding properties to a DTO. A `SchemaVersion` int field sits
  alongside it so a future format change can migrate rather than throw.
- Members store **both** `ElementId.Value` and `UniqueId`. Within one document the id is enough;
  `UniqueId` is the fallback that survives the model being copied or eTransmitted.

**When it writes** — never on a drag. Writing per interaction would mean a transaction per drag,
which is both slow and a sync-to-central storm on a workshared model. It writes on:

1. an explicit **Save sets** action in Step 2, and
2. **Run** (so the layout that produced a deliverable is always recorded).

A dirty indicator on the Step 2 header shows unsaved changes, so "I closed the window and lost it"
is visible before it happens rather than discovered afterwards.

**When it cannot write** — a read-only document, a linked model, no write access, or a
`DataStorage` element checked out by another user. The tool must **stay fully usable in memory**:
the failure is reported once through the run log and `DiagnosticsLog`, the Save button reports it
inline, and nothing is thrown at the dispatcher. A user with a read-only model can still build
sets and export; they just cannot persist them.

**Worksharing** — the `DataStorage` element lives in a workset and syncs to central, so two users
editing sets in their own locals will contend for it, and the second to sync gets the usual Revit
conflict. This is inherent to D2 and is worth stating in the tool's own hint text rather than
discovering in the field. §13.7 verifies it.

---

## 6. Ordering — two mechanisms, not five

Members arrive in browser order, so **most users never reorder anything.** The mechanisms are
therefore kept to the minimum that covers the rest:

- **Drag** — whole-row via the house `ListReorder` / `DragGhost` (`Source/Framework/ListReorder.cs`).
  Reorders set cards and reorders members. No Up/Down buttons, no per-row move-to-top, no drop
  slivers. One drag mechanism, the one the rest of the codebase already uses.
- **Sort bar** on an expanded set — **Browser order** (default, `BrowserNode` DFS rank),
  **Sheet number** (`NaturalOrderComparer.OrdinalIgnoreCase`, so `A.2` precedes `A.17`), **Name**,
  **Reverse**.

Drag handles a 200-row set badly, which is precisely why the sort bar exists and why browser order
is the default — dragging is fine-tuning, not the workflow.

**Member lists are collapsed by default** (`▸ Architectural — 42 sheets`). A step that renders 300
rows on open is a wall, not a step.

Per CLAUDE.md's drag rule: the set-name hit box shrinks to its text (`HorizontalAlignment.Left`) so
the rest of the row stays grabbable.

---

## 7. Handler rework — one path

`BulkExportEventHandler` loses `ExportIndividualMode` / `ExportPrintSetMode` and gains
`ExportSets`. **`ExportOptionsFactory` is not touched** — it is shared with Print View, and changing
it puts a second tool at risk for no gain.

### 7.1 PDF

| Granularity | Calls | Named from |
|---|---|---|
| `PerSheet` | one `Export` per member, `Combine=false` | **item pattern** |
| `PerSet` | one `Export(combine)` per enabled set | **set pattern** |
| `SingleFile` | one `Export(combine)` over every enabled set's members, concatenated in set order | **set pattern** |

`SingleFile` de-duplicates: a sheet in two sets exports once at its first position, with a log line
— *"A101 appears in 2 sets; exported once at position 14."*

### 7.2 DWG

Per member across all enabled sets, de-duplicated by element id. Options, named-setup resolution,
and the missing-setup skip-and-log are unchanged.

### 7.3 NWC / IFC

Per member across all enabled sets, de-duplicated, still 3D-only with the existing skip-and-log and
availability pre-flight.

**Regression guard.** Today NWC/IFC come from the Step 1 selection regardless of print sets. Under
the unified model they come from set membership — identical *only because* of §5.2. A 3D view
selected in Step 1 and never filed into a named set must still export. Explicit test case.

### 7.4 Progress, cancellation, and honesty about the big call

- Per-member loops keep `RunState.CancelRequested` and gain the ~5% batch cadence
  (`RunProgressReporter`).
- A combined call is one uninterruptible `doc.Export`. Log before it — *"Exporting 214 sheets to a
  single PDF — Revit runs this as one operation; it cannot be cancelled once started and may take
  several minutes."* — and the elapsed time after. That is not a fix, it is telling the truth
  instead of appearing hung.
- `Pct()` must weight a combined call by member count, not count it as one unit of one.

### 7.5 Lifetime (CLAUDE.md, non-negotiable)

`Sets` cleared in the handler's `finally` alongside `SelectedIds`/`PrintSets`; every new callback
(including §10.3's `OnPlanReady`) nulled in `BulkExportViewModel.OnWindowClosed`.

---

## 8. UI

Step count and `IConditionalSteps` behaviour are **unchanged**: S1–S9, S4–S7 conditional on their
format, S9 always last. A PDF-only run shows six steps. Restructuring the flow would be churn
against a pattern that works.

**Acceptance criterion, because six steps is only acceptable if most are pass-through:**
*select sheets → Run*, with no intermediate step touched, must produce a correct ordered export.
If any new control makes that untrue, the default is wrong.

### 8.1 S1 — Select & Assign (D1 lands here)

**Phase 1** changes nothing visible: the ViewModel stops materialising the selection from `HashSet`
enumeration order and orders it by `BrowserRank`. Pure defect fix, no UI, no mockup gate.

**Phase 2** adds the target-set bar above the tree. Sheets/Views toggle, `BrowserTreePicker` and
"Show all views" are otherwise untouched.

```
Adding to   ▌ Architectural ▾ ▌ 42 items          + New set
─────────────────────────────────────────────────────────────
  ▾ Sheets
      ☑ A101 — Ground Floor            ▌Architectural
      ☑ A102 — First Floor             ▌Architectural
      ☑ S101 — Foundation Plan         ▌Structural
      ☐ M101 — Level 1 HVAC
```

The four mitigations from D1, concretely:

1. **The bar is persistent and full-width**, carrying the active set's accent colour as a leading
   rule — not a small dropdown that reads as a filter.
2. **Each set has an accent colour**, assigned round-robin from the theme palette, shared by the
   bar and the badges.
3. **Every checked row carries a badge naming its set.** This is the mitigation that matters:
   modal state stops being modal once it is written on the row it governs. A user who filed 40
   sheets into the wrong set sees 40 wrong badges immediately, not on Step 2.
4. **A live count** on the bar.

Changing the target does **not** retarget anything already checked — a row's badge only changes
when that row is re-checked or moved on Step 2. Retroactive reassignment on a dropdown change would
be exactly the invisible bulk mistake option A is prone to.

**`BrowserTreePicker` needs one small addition**, and it must be backwards compatible because the
control is shared (Copy Datums, Align Sheet Views, Place Dependent Views and others use it):

```csharp
/// Optional per-row badge. Null (default) renders rows exactly as today.
public Func<long, RowBadge?>? RowBadgeProvider { get; set; }   // set before SetTree
```

`RowBadge` is a `(string Text, string AccentKey)` pair. Every existing caller leaves it null and is
byte-for-byte unaffected. A `RefreshBadges()` method repaints without a full `SetTree` so checking
a box does not rebuild the tree.

### 8.2 S2 — Sets & Order (review, reorder, fix)

At the top, the answer to *one set or separate*:

```
Output as    ( ) One PDF per sheet    (•) One PDF per set    ( ) One PDF for everything
```

Then drag-reorderable set cards, collapsed:

```
▸  Architectural            42 sheets   [PDF ✓] [DWG –]      ⋯
▸  Structural               18 sheets   [PDF ✓] [DWG –]      ⋯
▾  MEP                      63 sheets   [PDF ✓] [DWG ✓]      ⋯
     Sort: [Browser] [Sheet no.] [Name] [Reverse]
     ⠿ M101 — Level 1 HVAC
     ⠿ M102 — Level 2 HVAC
     …
     ▸ Options   (pattern override · format overrides · subfolder)
⚠  Unassigned                7 items    [PDF ✓]              ⋯
```

Under D1, assignment happened on Step 1 — **this step is review, reorder and repair**, not the
primary path. It carries: the granularity control, set order, member order, rename, per-set
overrides, and moving items between sets to fix a mis-targeted batch.

Actions: **+ New set**, **Auto-group by ▾**, **Move selected to ▾** (kept as the repair path for a
batch filed under the wrong target), **Import from Revit print set ▾**,
**Save set as Revit print set**, **Save sets** *(writes to the model — §5.3)*.

Secondary actions (rename, duplicate, delete, save-as-print-set) live in the card's **expanded
Options panel**, keeping them off the drag-able row per CLAUDE.md. *(Built this way rather than the
sketched "⋯ popup": a `Popup` with `StaysOpen=false` crashes Revit, and hand-rolling a dismissable
one is disproportionate risk for a three-item menu.)* The actions row carries the
**unsaved-changes indicator** from §5.3.

**Four design constraints that make this a different thing from the deleted
`SheetPackLayoutEditor`** (§2.10) — a reviewer should check these specifically:

1. Members are **element ids + `UniqueId`**, never sheet-number strings — survives renumbering.
2. **Views and sheets** are both first-class members.
3. Reorder is **`ListReorder`/`DragGhost`**, not a bespoke shuttle with Up/Down buttons.
4. Content is rebuilt through **`IStepAware.OnStepActivated`**, never read once at construction.

On (4), the critical detail: the sets model lives in the **ViewModel**, and `OnStepActivated`
rebuilds the *UI from the model*. It must never reset the model. Expansion and scroll state live in
the VM too, or every navigation back to S2 collapses everything the user just opened.

**Auto-group is an accelerator, not the headline.** The people who need sets most are the ones whose
models are least tidy, so a feature that assumes tidiness cannot be the main path. Offer:

- **Sheet-number prefix** (default) — `A-`, `S-`, `M-`. Discipline prefixes survive messier models
  than folder organisation does.
- **Project Browser folder** — exact folder structure from `BrowserTree`, ideal for a tidy model.
- **A sheet parameter** — Discipline, Sheet Series, or any string parameter.

It previews before committing — *"This creates 12 sets from 214 sheets. Groups smaller than 3
(4 groups, 7 sheets) merge into 'Other'."* — with a folder-depth control for the browser mode and a
cap that warns rather than silently rendering 40 unusable cards.

**Validation:** `IsValid("S2")` always true — sets are optional. A set with zero live members is
skipped at run time with a log line, never a validation block. `SummaryFor("S2")` reports
*"4 sets · 214 items · one PDF per set"*.

### 8.3 Print-set interop is preserved

- **Import from Revit print set ▾** — builds an `ExportSet` from a `ViewSheetSet`, members sorted
  into browser order on import (fixing §2.3 on the way in).
- **Save set as Revit print set** — the existing `BulkExportPrintSetHandler`, now per set, so a set
  still reaches Revit's own Print dialog when that is what the user wants.

Nothing that exists today is removed.

### 8.4 S3 — Filename & Formats

Format toggles unchanged. Naming becomes **two patterns**, because one pattern cannot name both a
file-per-sheet and a file-per-set (§2.6):

- **Item filename** — per-sheet PDFs, DWG, NWC, IFC. Sheet/view tokens as today, plus `{SetName}`,
  `{Seq}`, `{SetSeq}`.
- **Set filename** — per-set and single-file PDFs. `{SetName}`, `{SetIndex}`, `{SheetCount}`,
  project and date tokens. Sheet tokens are **not offered** — `{SheetNumber}` on a 40-sheet file is
  meaningless.

**Two stacked `TokenInput`s that look identical is exactly the ambiguity CLAUDE.md warns about**, so:

- Each sits under its own section label naming **what it produces**, not an abstraction.
- A **live resolved example** under each, from the current selection — `A101-Ground Floor.pdf` and
  `Tender-Architectural.pdf`. The example, not the label, is what makes it unmistakable.
- The hide rule is applied **symmetrically**: the set box is hidden when granularity is `PerSheet`
  (it names nothing), and the item box is hidden when granularity is `PerSet`/`SingleFile` **and**
  no item-level format (DWG/NWC/IFC) is on. Per CLAUDE.md, hidden — never shown disabled.

New tokens are `TokenOrigin.Computed` `TokenDefinition`s declared beside the ViewModel and passed as
`extraComputed` to `NamingTokenRegistry.TokensFor(...)` — per-tool run values, **not** global
registry entries:

| Token | Meaning |
|---|---|
| `{SetName}` | the containing set's name |
| `{SetIndex}` | 1-based set position, zero-padded to 2 |
| `{Seq}` | 1-based position in the **run's** overall ordered output, zero-padded to the run's width |
| `{SetSeq}` | 1-based position **within its set**, zero-padded to that set's width |
| `{SheetCount}` | member count (set pattern only) |

`{Seq}` and `{SetSeq}` are defined separately because with `SingleFile` a per-set position is
meaningless and one ambiguous token would quietly produce the wrong numbering.

`{Seq}` is also the hedge on §4: a folder of `001-A101…` PDFs sorts in issue order in Explorer and
in any external merge tool, whatever Revit does with combine.

### 8.5 S4 — PDF Settings

**The Combine toggle is removed**, replaced by the granularity control on S2. One control in one
place, and it kills the §2.4 dead-toggle bug.

The obvious objection — *you split PDF configuration across two steps* — is answered without adding
a second control: granularity is a **grouping** decision and belongs next to the sets it operates
on, and `SummaryFor("S4")` includes it, so the collapsed PDF step reads
*"Vector · High · Color · one PDF per set"*. Visible where it matters, single source of truth.

Every other PDF option — placement, zoom, colour depth, raster quality, hidden lines, view links in
blue, replace halftone — is untouched.

**Settings migration:** `BulkExportSettings.CombinePdf` (bool) → `PdfGranularity` (string).
`true → SingleFile`, `false → PerSheet`. `CombinePdf` stays in the DTO as a deprecated field and is
migrated on load, or every existing user's preference is silently lost.

### 8.6 S5–S7 — DWG / NWC / IFC

Untouched. Every option stays exactly where it is.

### 8.7 S8 — Output

Folder picker and split-by-format as today, plus **Put each set in its own subfolder** (with both
on: `<out>\PDF\Architectural\`) and **If a file exists** (D3).

### 8.8 S9 — Review & Run

`ReviewValues` gains a sets row. `ReviewWarning` keeps the NWC/IFC-in-Sheets-mode warning and gains
the overwrite count (D3) and the Unassigned warning (§5.2).

---

## 9. Before any code

Two gates, both from CLAUDE.md and neither optional:

1. **Task 0** (§4) — the combine-order probe. Its answer can invalidate Phase 2's design.
2. **`/revit-navisworks-ui` + rendered mockups approved first.** *"For any UI tweak/build/layout
   change, render a faithful mockup image for approval before writing code."* Three screens, from
   the live `ThemePalette`, iterated as images and not as compiled code:
   - **S1** — the target-set bar and the per-row badges (D1's whole mitigation is visual, so this
     is the one that most needs to be seen before it is built).
   - **S2** — set cards collapsed and expanded, Unassigned in its warning state.
   - **S3** — the two-pattern layout with both live examples.

   The ASCII in §8.1–§8.2 is a sketch, not the deliverable. **Phase 1 needs no mockup** — it has
   no UI change.

---

## 10. Safety and visibility

### 10.1 Pre-flight

Existing: missing titleblocks, NWC exporter availability, DWG setup missing. Added: members that no
longer resolve (*"Set 'Architectural': 3 of 42 members no longer exist"*), a member in more than one
set under `SingleFile`, filename collisions **across** sets, output folder not writable, and files
that already exist (§2.7). Degenerate-name detection is kept as-is.

Per CLAUDE.md, a check that finds zero says so: *"No filename collisions."*

### 10.2 Run log

One line per set and per item, warnings and failures always shown, `info` tone suppressed inside the
handler's own `Log` helper — the existing house rule, applied to the new set loop.

### 10.3 Export plan preview (Phase 3)

A dry run producing the exact ordered output list, shown in Review:

```
PDF · 4 files                          214 sheets
  01  Tender-Architectural.pdf          42 sheets   A101 … A142
  02  Tender-Structural.pdf             18 sheets   S101 … S118
  …
12 files already exist and will be overwritten.
```

Token resolution needs the `Document`, so this is an `ExternalEvent` — a `DryRun` flag on
`BulkExportEventHandler` that builds `List<PlannedOutput>` and calls back instead of exporting. The
same planned list then drives the overwrite scan and the run, so Review shows what executes rather
than a re-derivation that can drift.

---

## 11. Files

| File | Change |
|---|---|
| `Source/Tools/Export/ExportSetModels.cs` | **new** — `ExportSet`, `ExportSetMember`, `PdfGranularity`, `PlannedOutput`; absorbs `PrintSetModels.cs` |
| `Source/Tools/Export/ExportSetStore.cs` | **new** — Extensible Storage schema, `DataStorage` lookup, serialize/deserialize the layout (D2 / §5.3) |
| `Source/Tools/Export/ExportSetStoreHandler.cs` | **new** — `IExternalEventHandler` for the ES write (needs the main thread + a transaction); clears its payload in `finally` |
| `Source/Tools/Export/BulkExportViewModel.Sets.cs` + `.SetsUi.cs` | **new** — the set model logic and the S1/S2 UI. Built as ViewModel partials rather than the planned standalone `SetListEditor` control: a control would have needed a callback surface for state that already lives on the ViewModel, and partials keep it more tool-local, not less |
| `Source/Framework/Controls/Input/BrowserTreePicker.xaml(.cs)` | **+ optional `RowBadgeProvider` and `RefreshBadges()`** (§8.1). Null by default — every existing caller unaffected. Shared control: verify Copy Datums, Align Sheet Views and Place Dependent Views render identically |
| `Source/Tools/Export/BulkExportViewModel.cs` | S1 ordering + target bar; S2 rebuilt; S3 two patterns + tokens; S4 Combine removed; S8 two controls; S9 review; `OnWindowClosed` nulls the store handler's callbacks too |
| `Source/Tools/Export/BulkExportEventHandler.cs` | two paths → `ExportSets`; de-dup; pre-flight; progress weighting; dry-run |
| `Source/Tools/Export/BulkExportSettings.cs` | `PdfGranularity`, `SetSubfolders`, `ExistingFileAction`; `CombinePdf` migration |
| `Source/Tools/Export/PrintSetModels.cs` | deleted (folded in) |
| `Source/Tools/Export/BulkExportPrintSetHandler.cs` | kept; now called per set |
| `Source/Commands/Export/BulkExportCommand.cs` | pass the browser-rank map to the ViewModel |
| `Strings/en/export.bulkExport.json` | keys for every new label, hint and log line |
| `ExportOptionsFactory.cs`, `PrintView*.cs` | **untouched** |

Rough size: ~1,800 lines net across Phases 1–3, most of it `SetListEditor.cs`.

---

## 12. Export-type parity checklist — verified before merge

The user's one hard constraint. Merging two export paths is exactly where an option gets dropped.

**PDF** — paper placement (Center / Offset from Corner) · zoom (Fit to Page / Scale % + percent) ·
colour depth (Color / Grayscale / Black & White) · raster quality (Draft / Low / Medium / High /
Presentation) · hidden lines (Vector / Raster) · view links in blue · replace halftone with thin
lines · combine *(now expressed as granularity — capability preserved and extended)*

**DWG** — named setup resolved via `ExportDWGSettings` with the full `GetDWGExportOptions()` ·
missing-setup skip-and-log · per-element export

**NWC** — coordinates · parameters · convert element properties · divide by level · export links ·
parts · element ids · urls · find missing materials · room geometry · room as attribute · convert
lights · convert linked CAD (with its API-version guard) · faceting factor · 3D-only guard ·
exporter-availability pre-flight

**IFC** — version (IFC2x3 / IFC4) · `FilterViewId` · the export transaction · 3D-only guard

**Cross-cutting** — split by format · filename tokens and degenerate-name fallback · in-run
collision suffixes · result chips per format · every `GetSettingsSpec` entry and its `ApplySettings`
case · Print View's shared use of `ExportOptionsFactory`

---

## 13. Verify on Windows

1. **Task 0 — combined PDF page order (§4).** Blocking.
2. Duplicate `ElementId`s in one `Export` call — throw, or emit once?
3. `PDFExportOptions` members beyond the nine `ExportOptionsFactory` already uses — read the real
   metadata; do not infer from names.
4. A 300-sheet combined export end to end: elapsed time, memory, and whether Revit's own progress
   dialog appears behind the tool window (`RevitFailureCapture` should still capture it).
5. Saving a set as a Revit print set on a **workshared** model.
6. **Extensible Storage round-trip (D2):** create the `DataStorage` element, write a 10-set /
   300-member layout, close and reopen the model, read it back. Confirm the serialized string field
   has no practical size ceiling at that scale — if it does, the format splits across multiple
   fields or elements and §5.3 needs revising.
7. **Extensible Storage on a workshared model:** two users editing sets in separate locals — what
   the second sync reports, and whether the `DataStorage` element's workset is editable by a
   non-owner. Also confirm a **read-only / linked** document degrades to in-memory-only with a
   clear message and no throw.

---

## 14. Out of scope

External-library PDF merging (unless §4 forces the conversation) · stamping and watermarking ·
plotting to a physical printer · transmittals and issue registers · cloud/BIM360 publish · any
change to Print View beyond leaving `ExportOptionsFactory` alone.

---

## Appendix A — Adversarial review of v1, and what changed

Sixteen findings. Eight changed the plan materially; the rest were answered or rejected.

| # | Charge | Outcome |
|---|---|---|
| 1 | **"§10.1 admits the whole feature may be impossible, then specs 1,800 lines on top of it."** And v1 called the HashSet order "the headline defect" — if Revit sorts internally, that claim is wrong for the exact case the user complained about. The plan contradicted itself and moved on. | **Upheld, biggest change.** The risk was promoted to **Task 0** (§4), a gate before Phase 2, with the fork spelled out and the fallback labelled as *not delivering what was asked*. §2.1 now separates per-item ordering (fixable by us) from combined-page ordering (Task 0's call). |
| 2 | **"Five ordering mechanisms for one job"** — drag, three sort buttons, per-row ▲▼, and move-to-top/bottom. The same over-engineering CLAUDE.md rejected for the Legend drop targets. | **Upheld.** Cut to two (§6): drag + sort bar. ▲▼ and move-to-top/bottom deleted. |
| 3 | "Granularity on S2 while PDF options are on S4 splits PDF config across two steps — worse than the bug it fixes." | **Partly upheld.** Control stays on S2 (it is a grouping decision), but `SummaryFor("S4")` now reports it so the PDF step shows it without a duplicate control (§8.5). |
| 4 | **"The Unassigned set is a loaded gun."** Silently exporting leftovers ships a mystery `Unassigned.pdf` to a client. | **Upheld.** §5.2: enabled by default only while no named set exists; flips to a warning state and raises a `ReviewWarning` once named sets appear. |
| 5 | **"Two stacked TokenInputs are the ambiguity CLAUDE.md warns about"** — and v1's hide rule ran in one direction only. | **Upheld.** §8.4: live resolved example under each box as the real disambiguator, and the hide rule made symmetric. |
| 6 | "Auto-group by browser folder assumes a tidy model — the users who need sets have untidy models." | **Upheld.** §8.2: demoted to accelerator, default changed to **sheet-number prefix**, browser folder second. |
| 7 | "You kept nine steps and called it discipline." | **Partly upheld.** Structure stays (conditional steps mean six for PDF-only), but §8 now carries a hard acceptance criterion: *select → Run* with zero intermediate touches must work. |
| 8 | **"`<doc-key>` is hand-waved"** — `doc.PathName` differs per user on a workshared local, so the sets vanish for the exact users targeted. | **Upheld.** D2 now specifies the central model path with a fallback, an identity record inside the file, and a Windows check (§13.6). |
| 9 | "Phase 1 'independently shippable' may ship a fix that fixes nothing visible." | **Upheld.** D4 scopes Phase 1 honestly against Task 0 instead of promising. |
| 10 | **"A new hand-rolled set editor, six weeks after the last one was deleted."** | **Upheld.** §8.2 lists four checkable design constraints that distinguish it, and §11 declines to generalise it into the framework prematurely. |
| 11 | **"Where's the mockup?"** CLAUDE.md requires a rendered mockup before UI code. v1 had ASCII. | **Upheld.** §9 makes `/revit-navisworks-ui` + approved mockups a gate. |
| 12 | "'Overwrite by default plus a Review row' is a receipt, not protection." | **Partly upheld.** Default stays Overwrite (matching Revit), but the count is now a `ReviewWarning` banner, not a summary row (D3). |
| 13 | **"Nothing addresses *why* large sets are slow."** The plan assumes the complaint is structural. | **Upheld.** Added as **D0** — one question, because if the answer is "slow", it is a different investigation. |
| 14 | "S2's `IsValid`/`SummaryFor` are unspecified — does an empty set block the run?" | **Upheld.** Specified in §8.2. |
| 15 | "`{Seq}` padding 'to the set's size' is ambiguous under `SingleFile`." | **Upheld.** Split into `{Seq}` (run-wide) and `{SetSeq}` (within set), §8.4. |
| 16 | "Two token boxes, a granularity radio, an auto-group dialog and a sort bar is a lot of new surface for 'export some PDFs'." | **Noted, rejected.** Every one traces to a stated requirement — but it is why §8's acceptance criterion exists: none of it may be mandatory to get a correct default export. |

### A.1 — Where the user overruled the recommendation

Two of the four decisions went against what I proposed. Both are legitimate calls with real
upsides; recording the trade accepted, so it is a known cost rather than a surprise later.

**D1 — assign while selecting**, over select-then-split. Gains a single pass through the tree and
removes a whole staging column. The accepted cost is the failure mode I flagged: a mis-set target
files a batch into the wrong set. §8.1's four mitigations exist solely to make that failure
visible at the moment it happens — **the per-row badge is not decoration, it is the mitigation**,
and if it gets cut for effort the risk comes straight back. Worth watching in the mockup review.

**D2 — Extensible Storage**, over a sidecar. Gains genuine team sharing, which the sidecar could
only fake with a file on a server. The accepted costs are a transaction and write access on every
save, tool data living in the project, and workset contention between two users editing sets at
once (§5.3, §13.7). The mitigations — write only on Save and Run, `DataStorage` rather than
`ProjectInfo`, and full in-memory operation when the document cannot be written — keep it from
being worse than the `ViewSheetSet` behaviour §2.2 criticises, because unlike a `ViewSheetSet` it
is one element for the whole feature rather than one per group, and it never blocks the export.
