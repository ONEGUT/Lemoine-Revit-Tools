---
name: web-wpf-align
description: Compare a web-ported Lemoine tool against its WPF original and bring it to parity, using an uploaded WPF screenshot as ground truth. Use this whenever the user uploads a screenshot of a WPF tool window, names a tool and asks to compare/align/verify its web version, mentions "parity", "discrepancies", "make the web match", or asks to clean up / improve / review a web tool page (stepflow tool or bespoke window). Also use when the user asks whether the web port's inputs match the WPF original, even without a screenshot yet.
---

# Web ↔ WPF Tool Alignment

Audit one tool's web port against its WPF original, report every discrepancy,
propose cleanups, and (after approval) fix the web side. The WPF screenshot the
user uploads is the ground truth for what the WPF window actually renders;
the WPF ViewModel code is the ground truth for behavior. Never trust memory of
either — read both.

## Inputs

- **Tool name** — one of the ~34 step-flow tools or a bespoke window (Auto
  Filters, Legend Creator, Clash Definitions, Scope Box Manager, Settings…).
- **WPF screenshot(s)** — uploaded image(s) of the real window on Windows.
  If the user named a tool but uploaded no screenshot, ask for one (one
  question, per CLAUDE.md) — but start the code-level audit (Steps 1 and 2)
  while waiting; only the visual-diff half of Step 3 is blocked on the image.
  Multiple screenshots (one per step) are ideal; a single shot of the first
  step still catches chrome/layout drift.

## Step 1 — Locate the sources and prior decisions

1. Web side: `Source/Tools/**/<Tool>WebTool.cs` (the spec builder) — it sits
   next to the WPF `<Tool>ViewModel.cs` and step-content files it ports.
   Bespoke windows live on `WebWindowBase` with their own page
   (`Source/Web/<name>.html` + `Source/Web/lib/<name>.js`).
2. Shared shell: `Source/Web/stepflow.html`, `lib/stepflow.js`, `lib/lemoine.js`,
   `lib/lemoine.css`. These serve **every** tool — a fix that belongs to one
   tool goes in its WebTool spec, not the shell.
3. **Read `web-migration-status.md` §1/§3 and `web-migration-questions.md`
   before flagging anything.** A divergence already logged there is a recorded
   decision (or a known deferral) — present it as "logged decision, revisit?"
   rather than as a newly found bug.
4. Labels: WebTool specs and WPF both resolve text through
   `AppStrings.T(key)` → `Strings/en/*.json`. Compare resolved strings, not keys.

## Step 2 — Render the web version and screenshot it

For a **step-flow tool**: transcribe the tool's init spec from
`<Tool>WebTool.cs` into a JSON file — resolve every `AppStrings.T` key from
`Strings/en/`, and fill options/values with realistic sample data (mirror the
data visible in the user's WPF screenshot where possible, so the two images are
directly comparable). Then:

```bash
python3 .claude/skills/web-wpf-align/scripts/shoot_stepflow.py \
    --spec <scratchpad>/spec.json --out <scratchpad>/shots
```

That renders the real shell (stepflow.js + lemoine.css) in headless Chromium
and writes one PNG per step (it advances by clicking each Confirm button).
Conditional steps: to shoot a non-default mode (e.g. a Bulk Views inner tool),
emit a second spec with that mode's steps unhidden.

For a **bespoke window**: load its page directly (`Source/Web/<name>.html`) —
most have a standalone/demo driver when no bridge is present; if not, write a
small driver HTML in the scratchpad that calls the page's init with mock data,
and screenshot with the same Chromium flags the script uses.

Send the web screenshots to the user (`SendUserFile`) next to their WPF
screenshot so they can follow the comparison.

Rendering gotchas:
- Standalone pages render in the **DarkMono CSS fallback** (`lemoine.css`
  `:root`). Compare layout, inputs, and text — not raw colors — unless the WPF
  screenshot is also DarkMono. To match another theme, override the `--l-*`
  variables in the driver from `ThemePalette`.
- Headless Chromium culls bottom-anchored content — keep driver content in
  normal flow (a spacer div if needed), per the `/revit-navisworks-ui` recipe.

## Step 3 — The audit

Walk the WPF window input-by-input (screenshot first, then the ViewModel code
to catch anything the screenshot crops or a stale build hides). For each step
and each input record:

| Check | What counts as a discrepancy |
|---|---|
| Presence | WPF input with no web twin, or web-only input |
| Control kind | Wrong mapping (e.g. `InlineStepper`→`textField` instead of `stepper`; `BrowserTreePicker`→flat tabs; `MultiSelectTabs` missing Hierarchy/DisabledItems/SingleSelect) |
| Label / hint text | Wording drift vs the resolved `Strings/en` value |
| Options | Missing/extra options, wrong disabled/hidden logic (invalid options must be *hidden*, not disabled — Print View rule), wrong ordering |
| Defaults | Different initial value/checked state |
| Step structure | Different step count, ordering, grouping, required flags, conditional visibility |
| Validation | Web can Run in a state WPF blocks, or vice versa |
| Behavior features | WPF-only live previews, batch edit/merge, drag reorder, pick-from-model flows, log/auto-advance behavior |
| Visual | Spacing, alignment, widths, missing chrome — judged from the two images |

Also skim the WebTool for TODOs/deferred parity notes and check
`web-migration-status.md`'s "Known step-flow divergences" for this tool.

## Step 4 — Report and wait

Deliver one message, two numbered lists (per the porting rules in CLAUDE.md —
improvements are welcome but must be called out, never slipped in), **plus
annotated mockups of the proposed result**:

- Build one mockup page per step showing the layout WITH every proposed change
  applied, on the real `lemoine.css` (link it via `file://`, reuse its `--l-*`
  vars and classes). Tag each change in place with a small orange pill
  (`background:#e8772e`, mono, e.g. "A3 new notice") beside the changed element,
  and finish each page with a legend row per pill (pill + one-line description,
  above a dashed `#e8772e` top border). Screenshot with the same headless-Chromium
  flags the script uses and send them with the report (`SendUserFile`).
- Iterate on the mockup images until the user approves — never on compiled code.
  Size the window generously and re-shoot tighter afterwards; headless Chromium
  clips (not scrolls) content past the window height, so a too-tight height
  silently cuts off the legend/footer.

**A. Parity gaps** — the web must match WPF; each item: what differs, where
(`file:line`), and the fix. These are bugs unless a logged decision covers them.

**B. Proposed cleanups / improvements** — deliberate deviations needing
approval; each item: one-sentence tradeoff + a clear recommendation. Include
UX-philosophy wins (CLAUDE.md "UX Philosophy") where the WPF original itself is
awkward — the goal is "as close as you can to the original, but flag what you'd
improve".

Then stop and wait for the user's pick. A short reply ("do 1 and 3", "all of
A") is final — implement exactly that, no follow-up questions.

## Step 5 — Apply, verify, log

- Prefer fixing in `<Tool>WebTool.cs`; touch `stepflow.js`/`lemoine.js`/CSS only
  for genuinely shared behavior, and consider all 34 consumers before changing
  shell semantics.
- `Source/Web/*.js` is ASCII-only and full of `\uXXXX` escapes — any edit that
  touches such a line goes through a Python `str.replace()` script with
  count-checked `(old, new, expected_count)` tuples, never the Edit tool. Same
  for C# lines carrying `\uXXXX`.
- New user-facing text goes through `AppStrings` + `Strings/en/*.json`; verify
  every referenced key exists (regex-scan the `.cs`, diff against flattened JSON).
- Re-run the screenshot script and deliver before/after images.
- Log each applied deliberate deviation in `web-migration-questions.md`; update
  the tool's row/divergence note in `web-migration-status.md`.
- Run the CLAUDE.md post-change silent-failure scan, then commit on the branch
  the session designates. Note in the summary that C# changes still need a
  Windows build — this repo cannot compile on Linux.
