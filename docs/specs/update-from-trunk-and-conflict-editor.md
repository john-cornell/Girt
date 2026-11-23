# Update from develop + in-app merge conflict editor

## Problem

1. Bringing the latest `develop` into a feature branch is five manual steps (stash, checkout
   develop, pull, checkout back, pop, merge). Done several times a day.
2. The merge conflict dialog is hard to use:
   - "YOURS / THEIRS (against common base)" doesn't say which branch is which.
   - "Mark Resolved" is a separate step that's easy to forget or get wrong.
   - No one-click "keep mine" / "take theirs" for a whole file.
   - Two stacked diffs in a fixed 920x620 box is cramped.
   - Anything beyond looking at diffs means leaving Girt for kdiff3.

## Goals

- One action that brings the latest trunk (develop/main) into the current branch, keeping
  uncommitted work safe.
- Resolve conflicts fully inside Girt: a 3-pane Mine | Result | Theirs editor with per-chunk
  picks and free editing.

## Non-Goals

- Changing how Pull/Push/Rebase work outside of the conflict editor they share.
- Syntax highlighting in the merge editor.
- Resolving binary-file conflicts by content (whole-file picks only).

## Part 1 - Update from trunk

### Requirements

- **Trunk** = the first local branch found in the order develop, main, master, trunk. (Not
  the graph's trunk detection, which puts main/master first - in a git-flow repo that would
  offer "Update from master".) Label always shows the real name, e.g. "Update from develop".
- **Toolbar button** next to Pull/Push: "Update from <trunk>". Hidden/disabled when the
  current branch *is* the trunk, when HEAD is detached, or when no trunk is found.
- **Branch context menu** item on any local branch: "Merge latest '<branch>' into current".
  Same flow as the button, with that branch instead of trunk. Use the `Click` + code-behind
  pattern from AIREADME #12, not Command + RelativeSource.
- Flow:
  1. If the working tree has uncommitted changes (staged, unstaged or untracked), stash them
     with a recognisable message: `girt: auto-stash before updating from <branch>`.
  2. `git fetch origin <branch>:<branch>` - updates the local branch **without checking it
     out**. Only fast-forwards.
  3. `git merge <branch>` into the current branch (normal merge, no preview dialog - the
     user asked for it by name; the preview dialog stays for the existing Merge menu items).
  4. On a clean merge: `git stash pop` the auto-stash (only if step 1 made one).
  5. Refresh using the narrowest safe helper (AIREADME #5). The merge can bring in any number
     of commits, so this one needs a history reload too, same as Pull.
- Busy overlay + status text for each step ("Fetching develop...", "Merging...",
  "Restoring your changes...").

### Edge cases

- **Local trunk has commits not on origin** (fetch can't fast-forward): stop before merging,
  pop the auto-stash, tell the user "Local develop has diverged from origin/develop" and do
  nothing else.
- **No remote / no upstream for the trunk**: skip the fetch, merge the local branch, say so
  in the status bar.
- **Fetch fails (network, auth)**: stop, pop the auto-stash, show the git error.
- **Already up to date**: pop the stash, status "Already up to date with develop".
- **Merge conflicts**: open the conflict editor (Part 2). Keep the auto-stash. When the merge
  is finished via the editor, pop it. If the merge is aborted, pop it too (the branch is back
  to how it was, so the stash belongs there).
- **Stash pop conflicts** (after a successful merge): open the same conflict editor. Labels
  are "MINE (<branch>, merged)" vs "THEIRS (your stashed changes)". git keeps the stash
  when a pop conflicts, so nothing is lost. When every file is resolved, finish with
  `git stash drop` of that exact stash (by ref, not "top of stack") and unstage the results
  so the working tree looks like it did before (changes present, not staged).
- **Girt closed or crashed mid-flow**: on repo open, if a `girt: auto-stash before updating`
  stash exists and no merge is in progress, show a pill/notice offering to pop it. Never
  pop automatically.
- Track the auto-stash by its commit hash, not by `stash@{0}`, so a stash made by another
  tool in the meantime can't be popped by mistake (AIREADME #7).

## Part 2 - Conflict editor

Replaces the current MERGE CONFLICTS modal. Used for merge, pull, rebase, cherry-pick and
stash-pop conflicts.

### Layout

```
┌ files ─────┐┌─ MINE (feature/x) ─┬──── RESULT ────┬─ THEIRS (develop) ─┐
│ ✔ a.cs     ││ var x = 1;         │ var x = 2;     │ var x = 2;         │
│ ● b.cs  2  ││ [Use mine]         │  (editable)    │        [Use theirs]│
│ ● c.cs  1  ││                    │                │                    │
├────────────┤│                    │                │                    │
│ 1 of 3 done│└────────────────────┴────────────────┴────────────────────┘
└────────────┘  [Merge tool...]            [Abort]  [Close]  [Finish merge]
```

- Full-window overlay (fills the main window with a margin), not a fixed 920x620 box.
- Header: "Merging develop → feature/BB-123" (or "Rebasing feature/x onto develop",
  "Restoring stashed changes onto feature/x").
- Pane titles use **branch names**, never "ours/theirs". MINE is always the user's work:
  - merge / pull: MINE = current branch (stage 2), THEIRS = incoming (stage 3).
  - rebase: git swaps them - stage 2 is the upstream, stage 3 is the user's commit. MINE =
    stage 3, THEIRS = stage 2.
  - stash pop: MINE = branch (stage 2), THEIRS = stashed changes (stage 3).
- File list: resolved files ticked, unresolved show a count of unresolved chunks. Progress
  line "N of M done".
- Mine / Result / Theirs scroll together, aligned chunk by chunk.

### Building the result

- Pull the three stages to temp files (`git show :1:path`, `:2:`, `:3:`) - already done by
  `GetConflictDiffsAsync`.
- Run `git merge-file -p --diff3 <mine> <base> <theirs>` to get a result with conflict
  markers in a **known format** (doesn't depend on the user's `merge.conflictStyle`), then
  parse it into: plain lines (auto-merged by git, shown as normal text) and conflict chunks
  (mine lines, base lines, theirs lines).
- Keep the file's line endings and encoding when writing it back.

### Per-chunk actions

- **Use mine**, **Use theirs**.
- **Use both - mine first**, **Use both - theirs first**.
- **Free edit**: the result pane is editable. Editing inside an unresolved chunk marks it
  resolved with whatever the user typed.
- A resolved chunk can be reset back to unresolved.
- **F8 / Shift+F8**: jump to next / previous unresolved chunk (across files: at the last
  chunk of a file, F8 moves to the next unresolved file).

### Per-file actions

- **Keep mine (whole file)**, **Take theirs (whole file)** - `git checkout --ours/--theirs`
  (swapped for rebase, as above) + stage. One click, file done.
- **Merge tool...** - existing `LaunchMergeToolAsync`, kept as a fallback. Reload the file
  after the tool closes.
- Delete/modify conflicts (DU/UD/AU/UA/DD): no 3-pane view. Show what happened in plain
  words ("develop deleted this file, you changed it") with "Keep file" / "Delete file".
- Binary files: whole-file picks only.

### Resolving and finishing

- **Save** (Ctrl+S, and automatically when switching file) writes the result to disk.
- When a file has no unresolved chunks left, saving also stages it - it's resolved. No
  separate "Mark Resolved" button.
- If the user edits a resolved file again and saves, it stays staged with the new content.
- **Finish merge** (or "Continue rebase" / "Finish restoring stash") is enabled once every
  file is resolved. Same continue/next-conflict loop as today for rebase.
- **Abort** keeps its confirmation. **Close** stays (merge may have been finished outside
  Girt).
- Leftover markers check: before staging, refuse to stage a file that still contains a
  `<<<<<<<`/`>>>>>>>` line, and say which line.

### Cleaning up .orig files

Merge tools (git mergetool with `mergetool.keepBackup`, kdiff3) leave `<file>.orig`
backups behind.

- After any conflicted merge / rebase / cherry-pick / stash pop **finishes successfully**
  (not on abort), look for `.orig` files to clean up.
- Only these files count: untracked `<path>.orig` where `<path>` was one of the files
  that had a conflict in this operation. Never touch a tracked `.orig` file, and never
  other `.orig` files elsewhere in the repo.
- If there are none, say nothing.
- Otherwise, ask (via an injectable delegate, AIREADME #7/#13): "Delete 3 .orig backup
  files left by the merge tool?" listing the paths, with **Delete** / **Keep** and a
  **Don't ask again** checkbox.
  - Don't ask again + Delete → setting becomes Always delete.
  - Don't ask again + Keep → setting becomes Never delete.
- **Setting** in the Settings dialog: "After a merge, .orig backup files:"
  **Ask** (default) / **Always delete** / **Keep**. Same setting the checkbox writes to, so
  it can be changed back (AIREADME #11 - one owner).
- Always delete still reports it in the status bar ("Deleted 3 .orig files").
- Send files to the Recycle Bin rather than deleting them outright.

## Constraints

- Follow AIREADME.md: `ConfigureAwait(false)` in every new `GitCliService` await, no
  `Clear()`+re-add on the file list when one file changes, confirmations via injectable
  delegates (no inline `MessageBox.Show` reachable from tests - #13).
- Large files (thousands of lines) must open and scroll without freezing - virtualise the
  panes.
- Bump the version and rebuild the installer when shipped.

## Open questions

- Should "Update from trunk" also be offered automatically when the branch is behind trunk
  (a hint pill), or only on click?
- Free edit outside a conflict chunk (in auto-merged lines) - allowed? Proposed: yes, it's
  the user's file.

## Acceptance criteria

- On a feature branch with uncommitted changes, clicking "Update from develop" ends with:
  develop fast-forwarded to origin, develop merged in, uncommitted changes back exactly as
  they were, no stash left over. The checked-out branch never changes.
- Same from the branch context menu for any branch.
- A merge with conflicts opens the editor with real branch names on each pane; a rebase
  conflict shows MINE as the user's own commit.
- Every chunk can be resolved with mine / theirs / both (either order) / typed text; F8
  walks through them.
- A file with all chunks resolved is staged on save with no extra click; "Finish merge"
  completes the merge and then pops the auto-stash.
- Whole-file Keep mine / Take theirs resolve a file in one click.
- Aborting a conflicted update restores the branch and pops the auto-stash.
- A conflicting stash pop opens the editor and ends with the stash dropped and changes
  unstaged.
- After a conflicted merge finishes, leftover `.orig` files for the conflicted paths are
  offered for deletion; "Don't ask again" sets the setting, and the setting can be changed
  back in Settings. Untracked `.orig` files that weren't part of the merge are never touched.
- Tests cover: the update flow (clean, dirty, diverged trunk, fetch failure, conflict,
  abort), chunk parsing from `git merge-file --diff3` output, rebase side-swap, stash-pop
  conflict.
