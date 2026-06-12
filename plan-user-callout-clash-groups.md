# Plan — User-Drawn Callouts as Pre-Defined Clash Groups

## Problem

Auto-clustering (`ClashClusterer` single-link proximity) sometimes merges two clash groups the
user considers separate. There is no way to override the grouping today.

## Feature

Before running the Clash Finder, the user draws a callout on the plan view around the clashes
they want grouped together. When the tool runs:

1. Every clash whose anchor falls inside that callout's drawn rectangle becomes one
   **pre-defined group** — it is removed from the parent view (markers deleted, excluded from
   the parent dimension pass) and never participates in automatic clustering or the automatic
   dense-area survey.
2. The tool **adopts** the callout: clears prior Lemoine markers in it, marks the member
   clashes, picks and sets a legible scale (same generous-text-demand calculation as the dense
   tier), resizes it the same way the dense tier does (room-grow — see Decision 1), and
   dimensions inside it with `cropBounded` semantics — exactly like an automatic dense callout.
3. The stale-callout sweep never deletes it (it doesn't carry the `"- Dense "` name prefix),
   and re-runs re-adopt it idempotently.

## Where it slots in

`ClashFinderEventHandler.Execute()` Phase 2, per view, currently does:

```
mark parent view → [dense survey loop → CreateDenseCallouts] → sweep stale → dimension parent + callouts
```

The new pass inserts immediately after parent marking, before the dense loop:

```
mark parent view
→ [NEW] adopt user callouts: build one request per user callout, mark it, delete members'
        parent markers, add to dimViewIds / deferred / cropBounded
→ dense survey loop (naturally ignores claimed clashes — their parent markers are gone,
  and SurveyDenseAreas ingests from the parent's remaining tagged lines)
→ sweep stale → dimension parent + all callouts
```

Because `SurveyDenseAreas` and the parent dimension pass both read the parent view's tagged
cross-lines, deleting the members' parent markers up front makes the exclusion automatic — the
same mechanism the dense tier already relies on between its re-survey passes.

## Changes by file

### 1. `Source/Tools/T05-Clash/AutoDimension/AutoDimensionEngine.cs`
- New method `SurveyUserCallouts(doc, view, cfg, log)` → `List<DenseCalloutRequest>` (reusing
  the existing request type, plus a flag/field `IsUserCallout` and the adopted `ViewId`):
  - Enumerate the parent view's callout viewers (`FilteredElementCollector(doc, view.Id)
    .OfCategory(BuiltInCategory.OST_Viewers)`), map each viewer name to its `ViewPlan`,
    skip templates and any view named with the `"{parent} - Dense "` prefix (those are ours).
    *(API detail to verify on Windows: viewer-name → view mapping is the standard technique;
    fallback is matching `SECTION_PARENT_VIEW_NAME`.)*
  - Compute each callout's **membership rectangle** (see Decision 1 / persistence below),
    project the parent's ingested sources (`SourceIngest.Collect`) to 2D, and claim every
    clash whose anchor lies inside. A callout containing zero clashes is left untouched.
  - No minimum-clash gate (the user drew it deliberately), no demand-ratio gate, and **user
    areas never merge** with each other or with automatic dense areas — separation is the
    whole point of the feature.
  - Scale pick: same `DemandRatio`/`CalloutScales` walk as the dense tier (generous ~11-glyph
    text width).

### 2. `Source/Tools/T05-Clash/ClashFinder/ClashFinderEventHandler.cs`
- New `AdoptUserCallouts(...)` modeled on `CreateDenseCallouts(...)` but **reusing the
  user's existing view** instead of `ViewSection.CreateCallout`:
  - Unpin template (only if a scale change is needed), `TrySetCalloutScale`.
  - Resize crop per Decision 1.
  - `ClearViewMarkers` (Lemoine-tagged elements only), `PlaceInView` each definition,
    **prune** any marker placed in the callout whose group key is not a member (the crop may
    be larger than the membership rect after room growth — non-members must keep living in
    the parent), then `DeleteParentMarkers(member keys)`.
  - Append member keys to `deferred`, callout id to `dimViewIds` + `cropBounded`.
- `SweepStaleCallouts` untouched — user callouts don't match the `"- Dense "` prefix, so they
  are never deleted. Adopted callouts are never renamed.
- Failure of one adoption degrades exactly like a failed dense callout: log it, leave those
  clashes in the parent (chain tier), continue.

### 3. New: `Source/Tools/T05-Clash/ClashShared/UserCalloutSchema.cs` (only if Decision 1 = room-grow)
- Extensible Storage entity stamped on the adopted callout **view** (constant hardcoded
  `Schema` GUID, `Schema.Lookup` guard — same discipline as `AutoDimOwnerSchema`):
  - `MembershipRect` — the rectangle that defines the group (the user's original drawn crop).
  - `LastAppliedRect` — the crop we last wrote (after room growth).
- Re-run reconcile: if the callout's current crop ≠ `LastAppliedRect`, the **user resized it
  by hand** → re-baseline `MembershipRect` to the current crop (the user's edit wins).
  Otherwise membership keeps using the stamped original rect, so room growth on run N never
  silently widens the group on run N+1.
- Not needed at all if Decision 1 = "adopt as drawn" (current crop is always the membership
  rect).

### 4. Settings / UI (per Decision 2)
- `AutoDimensionConfig`: `UserCalloutsEnabled` (and `UserCalloutNameToken` if opt-in is
  name-based).
- One checkbox (+ optional token field) on the Clash Finder wizard's dimensioning step,
  next to the existing dense-callout controls. `/revit-navisworks-ui` skill will be invoked
  before any UI code.

## Decisions needed before implementation

### Decision 1 — How should the adopted callout be resized?
- **A. Room-grow (recommended, matches your "whole room thing")** — grow the crop to the
  containing room(s) + margin like the dense tier, but membership stays locked to the
  *drawn* rectangle (persisted via the ES stamp) and extra markers inside the grown crop are
  pruned. Dimensions get visible grid/slab references; two user callouts in the same room
  stay separate groups even though their crops overlap.
- **B. Adopt as drawn** — only set the scale; never touch the crop. Simplest and fully
  predictable, no ES schema needed, but a tightly drawn callout can starve the dimension pass
  of visible references (`cropBounded`) and dims fail.

### Decision 2 — Which callouts get adopted?
- **A. Opt-in name token (recommended)** — only callouts whose name contains a token (e.g.
  `Clash`, configurable) are adopted. Protects legit drawing callouts that happen to contain
  clashes from being rescaled/resized.
- **B. Adopt every callout containing a clash** when the feature checkbox is on. Zero naming
  friction, but the tool will hijack (rescale, re-crop) unrelated production callouts.

### Decision 3 — Base branch
The session is set up to develop on `claude/clash-callout-clustering-iwrxcg`. Which branch
should it be created from (`main`?)?

## Out of scope
- Non-plan views (the dense tier is plan-only today; user callouts follow).
- Letting a user callout *split* a single physical clash group across two callouts when both
  rectangles contain the same anchor — first containment match wins, logged.
