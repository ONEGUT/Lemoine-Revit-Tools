# Plan — Wire the auto-dimension engine into the Clash Finder dimension pass

## Problem
The Clash Finder's "Run dimension pass after marking" toggle calls
`ClashDimensionPass`, which is discovery-only ("no dimensions placed yet"). The
real placement engine (`AutoDimensionEngine` + `AutoDimensionCommit`) exists on
this branch but is reachable only from the separate **Auto Dimension** command.
The user wants the toggle itself to place dimensions on the clash markers.

## Change
1. **New `AutoDimensionRunner`** (`Source/Tools/Testing/AutoDimension/AutoDimensionRunner.cs`)
   — shared orchestration: build a plan per view (read-only), report each plan's
   unresolved/ambiguous/missing-link/notes, then commit every view's plan inside
   one transaction. Returns aggregate `CommitResult`.
2. **`ClashFinderEventHandler`** — replace the `ClashDimensionPass.Run` discovery
   call with `AutoDimensionRunner.Run`. Add a run-level `DimTargetType`
   ("Grid" | "SlabEdge"); start from `AutoDimensionConfig.Instance` and override
   its `TargetType` with the Clash Finder's choice. The dimension transaction is
   opened *after* the marking transaction commits (the new cross-lines must be
   queryable), so no transaction nesting.
3. **`ClashFinderViewModel`** — add a `LemoineSingleSelect` "Dimension
   destination" (To Grid / To Slab Edge) to the Options step, push the choice to
   `_handler.DimTargetType`, reflect it in the step summary, and update the
   dimension-pass toggle description (it now places, not just discovers).
4. **Delete `ClashDimensionPass.cs`** — its only caller is replaced and its
   header ("no dimensions placed yet") is now misleading.

## Out of scope
`AutoDimensionEventHandler` (the standalone tool) is left untouched — it already
works; refactoring it onto the shared runner is a separate cleanup.

## Notes
- Target choice defaults to the persisted `AutoDimensionConfig.Instance.TargetType`.
- A dimension needs a target; with no grids/slab edges resolved the engine logs
  `Resolved 0/N` + per-source reasons (already instrumented) — no silent failure.
