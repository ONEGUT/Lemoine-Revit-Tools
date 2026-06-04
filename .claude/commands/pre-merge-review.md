---
name: pre-merge-review
description: >
  Run before every "merge with main". Scans the current branch's commits and
  any open PR comments for new error patterns, preferences, or constraints not
  yet captured in CLAUDE.md. Proposes additions, waits for approval, applies
  them, then proceeds with the merge.
---

# Pre-Merge Review

Runs automatically before every merge. Goal: capture anything learned on this
branch — new errors hit, new preferences surfaced, new Revit constraints
discovered — and fold it into CLAUDE.md before the branch is gone.

---

## Step 1 — Collect branch evidence

Run these in parallel:

1. **Branch commits** — `git log main..HEAD --oneline` to get every commit on
   this branch that hasn't landed on main yet. Read the full commit messages
   (not just subject lines) for error fixes, reverts, and workarounds.

2. **PR comments** — use `mcp__github__pull_request_read` with
   `method: "get_comments"` and `method: "get_review_comments"` on the PR for
   this branch (search via `mcp__github__list_pull_requests` filtered by head
   branch if the PR number is unknown). Look for back-and-forth that reveals a
   preference or a fix that surprised Claude.

3. **Current CLAUDE.md** — read it so you know what's already documented and
   don't propose duplicates.

---

## Step 2 — Identify new findings

Scan the evidence for anything in these categories that is NOT already in
CLAUDE.md:

### Error patterns
- Compile errors (CS codes) that required a fix commit — especially missing
  `using` directives, ambiguous types, wrong API names, access modifiers
- Runtime crashes or Revit hangs that required a revert or workaround

### Preferences
- Decisions the user made when given options ("let's do X", "option 2")
- Explicit rejections ("that's impractical", "the idea of X is stupid")
- Interaction patterns the user corrected more than once

### Revit / WPF constraints
- API calls that don't exist in Revit 2024, have wrong signatures, or crash
- WPF patterns that are unsafe in Revit's hosting context

---

## Step 3 — Propose and apply

If findings exist:

1. List them in chat — one bullet per finding, category labelled (Error /
   Preference / Constraint). Keep each bullet to one sentence.
2. Ask the user: "Anything to drop or reword before I add these?"
3. Wait for confirmation ("looks good", "add it", "skip #2") before editing.
4. Edit CLAUDE.md on the current branch, commit with message:
   `Update CLAUDE.md from pre-merge review`

If no new findings: state "Nothing new to add to CLAUDE.md" and proceed.

---

## Step 5 — Merge

After CLAUDE.md is updated (or confirmed no changes needed):

Use `mcp__github__create_pull_request` and `mcp__github__merge_pull_request`
to create and merge the PR into main. Squash merge. No further confirmation
needed — the user already approved the merge when they said "merge with main".
