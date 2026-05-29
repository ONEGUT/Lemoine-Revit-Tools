# Plan — Silent-Failure Remediation (revised: "never fail unnoticed")

## Your requirement
> "My biggest concern is something not working right and me not noticing. If
> there are failures it's important I know — so I can restructure the code to
> prevent failures, or know when I can't use certain functions."

So: **no catch may be truly silent.** Every swallow must leave a record you can
review. But blanket *rethrow* is wrong — it would break the legitimate fallback
chains (`SafeName` tries `.Name`, then a param) and mask the real error inside
cleanup blocks (`tx.RollBack()`). The correct tool is **log-everywhere into one
durable, reviewable place**, plus louder surfacing for batch operations.

## What exists today
- No global logger. Each tool handler has a local `PushLog(text, status)` →
  StepFlow window "Output Log" tab. Only live while that tool runs.
- 7 `Debug.WriteLine` calls total; no log file. Catches outside handlers (settings,
  controls, helpers, value-resolution) report **nowhere** → these are the true
  blind spots.

---

## Design — `LemoineLog` (new, central diagnostic sink)

New file: `Source/Lemoine/LemoineLog.cs` — static, Revit-free (so LemoinePreview
can use it too).

Responsibilities:
1. **Durable file** — append timestamped entries to
   `%APPDATA%\LemoineTools\diagnostics.log` (thread-safe via a lock; rolls when
   over ~1 MB so it never grows unbounded).
2. **In-memory ring** — last ~500 entries for fast in-app viewing.
3. **Live forwarding** — if a tool's StepFlow log is open, registered sink gets the
   entry too, so failures show live during a run.
4. **API (one obvious method):**
   ```csharp
   LemoineLog.Swallowed(string context, Exception ex);   // best-effort catch
   LemoineLog.Warn(string context, string detail);        // non-exception notice
   ```
   `context` is a short human phrase: e.g. `"ApplyTemplate: view 'L2 Power'"`.

### How each catch is converted (all 105)
`catch { }`  →  `catch (Exception ex) { LemoineLog.Swallowed("<what failed>", ex); }`

- **Best-effort Revit ops / value-resolution / IO / UI / cleanup** — log via
  `Swallowed`. Behavior unchanged (still falls through), but now recorded.
- **Settings `Save()` (~9)** — log via `Swallowed`; a failed save is now visible.

### Batch-loop ops get louder (your "batch ops aren't benign")
Per-element ops that currently swallow inside a loop over views/walls/ceilings
(e.g. `ViewTemplateId`, `SetCategoryHidden`, `AddFilter`, `SetSubDisc`) will, in
addition to `LemoineLog.Swallowed`:
- increment the tool's existing `fail`/`skip` counter, and
- push a line to that tool's StepFlow log (`PushLog`) so the end-of-run summary
  reports e.g. *"3 of 120 views: view template could not be applied (see log)."*

This is the key behavioural change: a half-configured result is now reported, not
hidden. Tools without a StepFlow log (rare) still get the file record.

### Catch-and-log "Fatal" blocks (~17)
Keep the logging; also route through `LemoineLog` so they land in the durable file.
Rename the contradictory "Fatal" label (it logs then continues) to "error".

---

## Viewing the log
- Always available as the file at `%APPDATA%\LemoineTools\diagnostics.log`.
- Live in the StepFlow "Output Log" during any tool run.
- **Proposed affordance:** an "Open diagnostics log" button in the global Settings
  window footer (opens the file with the OS default handler). This is a small WPF
  change — I'll invoke the `/revit-navisworks-ui` skill before touching that window.
  *(Optional — say if you'd rather skip the button and just use the file path.)*

---

## Scope / sequencing (one commit per step, reviewable)
1. Add `LemoineLog.cs` + unit-free self-test via LemoinePreview wiring.
2. Convert the ~16 non-handler blind-spot catches (settings, controls, helpers).
3. Convert handler/loop catches + add fail-count surfacing for batch ops.
4. Route the catch-and-log "Fatal" blocks through LemoineLog; relabel.
5. (If approved) Settings-window "Open diagnostics log" button — via UI skill.

## Risks / notes
- Volume: if a Revit op fails for every element in a 500-item batch, that's 500 log
  lines — but that is exactly the signal you want ("this function isn't working").
  The ring buffer + file roll keep it bounded; batch summary collapses it to a count.
- Thread safety: Revit ops are main-thread; UI/persistence can be off-thread → the
  file writer locks.
- No logic rewrites; the only behavioural change is *reporting* (and fail-counting
  in batch loops). Fallback paths are untouched.

## Branch
Continue on `claude/codebase-health-review-D8NAN`.

## Post-change
Run the mandatory silent-failure scan each step (expect clean — we are *adding*
reporting, not removing it). Commit per step; push. No PR unless requested.
