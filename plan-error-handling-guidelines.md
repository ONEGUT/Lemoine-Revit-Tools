# Plan: Error Handling & Silent Failure Guidelines in CLAUDE.md

## What changes

- `CLAUDE.md` — add a new `## Error Handling & Silent Failure Audit` section

## What the new section covers

1. **Error handling standards** — rules Claude must follow when writing C# code:
   - Never swallow exceptions in empty or log-only catch blocks without notifying the user
   - Always check return values where failure is meaningful
   - Avoid fire-and-forget `Task` calls without `.ContinueWith` or `await`
   - Validate inputs at system boundaries (user input, external APIs, Revit API calls)

2. **Post-change silent failure scan** — after every set of code changes Claude must:
   - Review the diff for patterns that hide failures from the user at runtime
   - Produce a list of findings (file, line, pattern, risk description)
   - Ask the user whether they want a warning added, an exception re-thrown, a log call, or to leave it as-is

## Why

Revit plugins are hard to debug after the fact. Silent failures (swallowed exceptions, unchecked nulls, missing Task awaits) produce confusing behaviour with no obvious cause. This guideline ensures Claude never ships code that hides failures.

## Files touched

| File | Change |
|------|--------|
| `CLAUDE.md` | Add `## Error Handling & Silent Failure Audit` section |

## Branch

`claude/claud-error-handling-silent-failures-Gsg3p`, based from `main`
