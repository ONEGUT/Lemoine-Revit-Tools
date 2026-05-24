# Lemoine Revit Tools — Claude Guidelines

## Project Overview

This is a Revit plugin built with C# and WPF targeting .NET Framework 4.8. The primary project is `LemoineTools`. See `LEMOINE_UI.md` for the full UI architecture reference.

---

## Branch Workflow — Read Before Any Code Changes

### 1. Always Plan First

Before creating, checking out, or pushing to any branch, Claude must:

1. Describe the full implementation plan in chat — what files will be changed, what will be added, and why.
2. **Wait for explicit approval from the user** before touching any branch or writing any code.

Approval means the user has responded with clear confirmation (e.g. "looks good", "go ahead", "yes"). Ambiguous responses are not approval — ask for clarification.

### 2. Branch Naming Convention

All feature/fix branches must follow this pattern:

```
<type>/<short-kebab-description>
```

| Type | When to use |
|------|-------------|
| `feature/` | New functionality or UI component |
| `fix/` | Bug fix |
| `refactor/` | Internal restructuring with no behavior change |
| `chore/` | Tooling, config, docs, or dependency updates |

Examples:
- `feature/color-picker-recent-swatches`
- `fix/settings-tab-layout`
- `refactor/tool-settings-interface`
- `chore/update-claude-md`

Rules:
- Use lowercase letters, numbers, and hyphens only — no spaces, underscores, or slashes beyond the type prefix.
- Keep the description concise (3–6 words).
- Never push directly to `main` or `master`.

### 3. Branch Lifecycle

```
Plan → User Approval → Create Branch → Implement → Commit → Push → PR (if requested)
```

- Always branch off the latest `main` unless the user specifies otherwise.
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

## WPF UI Tasks

For any task that involves building, modifying, or debugging a WPF window or UserControl, invoke the `/revit-navisworks-ui` skill before writing any code. This applies even for small layout fixes.

---

## Key Files

| Path | Purpose |
|------|---------|
| `LEMOINE_UI.md` | UI architecture, design system, component library, and tool contract |
| `LemoineTools.csproj` | Project file (targets .NET Framework 4.8) |
| `Source/` | All C# source and XAML files |
