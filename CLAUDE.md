# Lemoine Revit Tools — Claude Guidelines

## Project Overview

This is a Revit plugin built with C# and WPF targeting .NET Framework 4.8. The primary project is `LemoineTools`. See `LEMOINE_UI.md` for the full UI architecture reference.

---

## Branch Workflow — Read Before Any Code Changes

### 1. Always Plan First

Before creating, checking out, or pushing to any branch, Claude must:

1. Write a plan `.md` file to the repo root (e.g., `plan-<description>.md`) covering what files will be changed, what will be added, and why.
2. Summarise the plan in a brief chat message.
3. Ask the user which branch to branch from.
4. **Wait for explicit approval from the user** before touching any branch or writing any code.

Approval means the user has responded with clear confirmation (e.g. "looks good", "go ahead", "yes"). Ambiguous responses are not approval — ask for clarification.

### 2. Branch Naming Convention

Branch names must be a short, lowercase kebab-case description of the change — nothing more.

Examples:
- `color-picker-swatches`
- `settings-tab-layout`
- `update-claude-md`

Rules:
- Lowercase letters, numbers, and hyphens only.
- 3–5 words max.
- Never push directly to `main` or `master`.

### 3. Branch Lifecycle

```
Plan (.md file + chat summary) → Ask which branch to base from → User Approval → Create Branch → Implement → Commit → Push → PR (if requested)
```

- One logical change per branch. Do not bundle unrelated fixes.
- Do not create a pull request unless the user explicitly asks for one.

### 4. Commit Messages

Use the imperative mood, present tense. One subject line, no trailing period.

```
Add recent-swatches row to color picker
Fix settings tab not rendering on first open
Refactor ILemoineTool to support async execute
```

---

## Error Handling & Silent Failure Audit

### Standards for all C# code

- Never swallow exceptions silently — empty `catch` blocks and catch-and-log-only blocks that discard the error are forbidden unless the risk is explicitly acknowledged and the user has been told.
- Always `await` async operations or attach a `.ContinueWith` / `.GetAwaiter().GetResult()` handler. Fire-and-forget `Task` calls that can fail silently are not allowed.
- Check return values where failure is meaningful (e.g. Revit API calls that return `null` on failure, `bool` results indicating success).
- Validate inputs at system boundaries: user input, external APIs, Revit API calls, file I/O. Do not validate internal calls that are already guaranteed by the framework or calling code.

### Post-change silent failure scan

After **every** set of code changes, before reporting the task complete, Claude must:

1. Scan the diff for patterns that hide failures from the user at runtime:
   - Empty or catch-and-discard `catch` blocks
   - Unawaited `Task` / `async void` methods (outside event handlers)
   - Unchecked `null` returns at Revit API or external boundaries
   - Return values that signal failure being ignored
2. Produce a numbered list of findings — file path, approximate line, pattern type, and a one-sentence risk description.
3. Present the list to the user and ask for each finding: **warn**, **rethrow**, **log**, or **leave as-is**.
4. Apply the chosen handling before committing.

If no silent failures are found, state "No silent failures detected" explicitly so the user knows the scan ran.

---

## WPF UI Tasks

For any task that involves building, modifying, or debugging a WPF window or UserControl, invoke the `/revit-navisworks-ui` skill before writing any code. This applies even for small layout fixes.

---

## Key Files

| Path | Purpose |
|------|---------|
| `LEMOINE_UI.md` | UI architecture, design system, component library, and tool contract |
| `LemoineTools.csproj` | Project file (targets .NET Framework 4.8) |
| `Source/` | All C# source and XAML files |
