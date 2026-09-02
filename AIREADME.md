# AI README — Performance Architecture (do not regress)

This file documents the performance work done on Girt so it doesn't get silently undone by a
future change. If you're about to touch `GitCliService`, `MainViewModel`, `CommitHistoryViewModel`,
`BranchListViewModel`, or `WorkingChangesViewModel`, read this first.

The explicit product direction from the user: **assume local git actions succeeded and update the
UI immediately; only roll back on failure; only do a full repository refresh when the user asks
(F5) or when `AutoRefresh` is on.** Git Extensions is the performance bar. Every rule below exists
to keep Girt at or below that bar.

## 1. Never let a background await resume on the UI thread by accident

Every internal `await` inside `GitCliService` ends in `.ConfigureAwait(false)`. Without it, WPF's
`DispatcherSynchronizationContext` bounces execution back to the UI thread after every git-process
await, so all the string parsing that follows a git call runs on the UI thread even though the
process itself was async. **Any new method added to `GitCliService` must `.ConfigureAwait(false)`
every internal `await`.** The outer ViewModel-level awaits (in `MainViewModel`, etc.) deliberately
do NOT use `ConfigureAwait(false)` — they need to resume on the UI thread to touch
bound properties/collections.

## 2. Never `Clear()` + re-add into an `ObservableCollection` bound to a virtualized list

`ObservableCollection.Clear()` raises a `NotifyCollectionChangedAction.Reset`, which forces a
virtualized `ListBox`/`ListView` to tear down and regenerate every visible row's container
(bindings, templates, context menus — all of it). This was the root cause of most of the "lag"
in this app before it was fixed:
- Branch pin toggle: fixed by using `ObservableCollection.Move` instead of Clear+ReAdd.
- Commit graph prepend after a local commit/revert: fixed by `FilteredCommits.Insert(0, ...)`
  instead of rebuilding the whole filtered list (see `CommitHistoryViewModel.PrependLocalCommit`).

**Rule: if you're updating one or a few items in a collection bound to a virtualized control,
use `Insert`/`Remove`/`Move` on the exact indices. Only `Clear()` when the whole list is genuinely
being replaced (e.g. a real full reload).**

## 3. `GitCommit` has identity — don't lose it

`GitCommit.Equals`/`GetHashCode` are overridden by `Hash`. Every reload builds entirely new
`GitCommit` instances from a fresh `git log` parse, so without this, selection-tracking code
comparing by reference always thinks the previously-selected commit is "gone", resets
`SelectedCommit` to the top commit, and re-triggers an unwanted diff reload — on every single
refresh, including silent background ones. If you add a new model type that gets rebuilt on every
reload and is used for selection tracking, give it the same treatment.

## 4. Commit graph layout is incremental — don't force a full relayout

`GitGraphLayoutEngine.ComputeGraphLayout` returns the final `activeLanes` state so it's resumable.
`CommitHistoryViewModel.TryBuildIncrementalCommitList` reuses the unchanged tail of the previous
commit list and only lays out the new prefix, verifying the splice is correct before trusting it
(falls back to a full relayout on any verification failure — never a wrong-but-plausible result).
`PrependLocalCommit` is the fast path for "we know exactly one new commit was added locally"
(Commit, Revert). **Don't add a new code path that calls `ComputeGraphLayout` on the full list
when only the top of the list changed** — use or extend the incremental path instead.

## 5. Fast local git actions must NOT trigger a full `RefreshRepositoryAsync`

Reset HEAD, Merge, Rebase, Cherry-pick, Revert, Create-Branch, and Commit are local and don't need
a full 4-part refresh + full commit reload. `MainViewModel` has targeted helpers instead:
- `RefreshPillsOnlyAsync()` — status + current branch only (used by the external-change watcher
  when `AutoRefresh` is off).
- `RefreshPillsAndWorkingChangesAsync()` — status + working-tree (Cherry-pick, Merge, Rebase,
  Reset — history can change in ways that aren't safe to splice locally).
- `RefreshPillsAndBranchesAsync()` — status + branch list (Create-Branch).
- `UpdateRepoStatusLocally(aheadDelta)` — reconstructs `RepoStatus` from
  `WorkingChanges.StagedFiles.Count + UnstagedFiles.Count` and a known ahead/behind delta, with
  zero git calls (used after Commit/Revert alongside `PrependLocalCommit`).
- `RefreshRepositorySilentlyAsync()` / `DoRefreshRepositoryAsync()` — the full refresh, reserved
  for F5, `AutoRefresh`-on external changes, and Pull/Push (which can bring in an unbounded number
  of remote changes that can't be synthesized locally).

**If you add a new command that mutates the repo, pick the narrowest helper above that's actually
safe for what that command can do — don't reach for the full refresh by default.**

## 6. Computed properties driven by a collection need their own change notification

`WorkingChangesViewModel.TotalChangesCount` / `HasStagedFiles` / `HasUnstagedFiles` are computed
from `StagedFiles`/`UnstagedFiles`. They used to only get `OnPropertyChanged` raised manually
inside the old `LoadChangesAsync()`. Once Stage/Unstage/Discard/Commit became fully optimistic
(mutating the collections directly, never calling `LoadChangesAsync()`), those properties went
stale — the lists were right, the header count wasn't. Fixed by subscribing both collections'
`CollectionChanged` in the constructor. **Any new computed property that derives from a mutable
collection needs the same treatment — don't rely on some other method happening to call
`OnPropertyChanged` for it.**

## 7. Destructive/surprising git actions get a confirmation, always

`DiscardChangesAsync`, `StashPopAsync`, `StashApplyAsync` all confirm before running. Stash
pop/apply in particular applies the *top* stash to whatever branch is currently checked out —
with several stashes stacked up from different branches (a real, common state — see `git stash
list` in a long-lived repo), popping the wrong one silently drops unrelated changes onto the
current branch. `ConfirmStashAction` is an overridable `Func<string,bool>` on
`WorkingChangesViewModel` so tests can bypass the real `MessageBox.Show` — **if you add a new
confirmation, follow this pattern (injectable delegate, default = real MessageBox) instead of
calling `MessageBox.Show` directly inline, or a test that exercises the success path will hang.**

## 8. `AutoRefresh` gates the external-change watcher, nothing else

`AutoRefresh` (off by default) only controls what the `.git`-folder `FileSystemWatcher` does when
it sees an external change: full silent refresh if on, pills-only if off. It has no bearing on
what happens after the user's own actions (commit, stage, etc.) — those always follow rule 5,
regardless of `AutoRefresh`.

---

**Before shipping a change to any of the files above:** rebuild, run the full test suite (currently
41 tests, should stay green), and actually feel the app for lag on a large real repo — the tests
lock in correctness, not perceived speed. If you introduce a `Clear()` on a virtualized list's
bound collection, a `RefreshRepositoryAsync()` call after a fast local action, or drop a
`ConfigureAwait(false)` from `GitCliService`, you are reintroducing a bug that was deliberately
fixed. Grep for these patterns if you're unsure.
