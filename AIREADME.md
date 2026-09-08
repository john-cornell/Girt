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

## 9. Rebuilding a *root-level* collection with `Clear()`+`ReAdd()` is fine

Rule 2 is about virtualized lists with many visible rows. `BranchListViewModel.RebuildBranchTree`
(the folder-grouped branch `TreeView`'s data source) still does `target.Clear()` + re-add on every
filter/pin/reload — but only at the **root** level (a handful of top folders/branches), never the
full depth of the tree. Nested `BranchTreeItem.Children` collections are never cleared/rebuilt in
place; each rebuild constructs entirely new child collections from scratch and swaps them in via
the root Add, so already-rendered nested `TreeViewItem`s for *unaffected* subtrees just get GC'd
and regenerated - there's no live nested collection getting a spurious `Reset`. If you find
yourself wanting to `Clear()` a large or deeply-nested collection, that's the rule-2 case; a small
root-level list is not.

## 10. `TreeView.IsExpanded` must be explicitly bound in `ItemContainerStyle`

`BranchTreeViewItemStyle` sets `Setter Property="IsExpanded" Value="{Binding IsExpanded, Mode=TwoWay}"`
on the `TreeViewItem` itself. This was missing in an early version of this style: the custom
expander `ToggleButton` inside the `ControlTemplate` bound to `{RelativeSource TemplatedParent},
Path=IsExpanded` (i.e. the *container's* `IsExpanded`), which visually worked (rows expanded and
collapsed fine) but silently never round-tripped to the underlying `BranchTreeItem.IsExpanded`
data property - so `RebuildBranchTree`'s expand-state snapshot/restore across rebuilds was
reading a value that never changed. If you re-template `TreeViewItem` again, keep the
`ItemContainerStyle` Setter as the single place `IsExpanded` reaches the data model; don't rely on
the template's internal toggle alone.

## 11. Settings that both a dialog and other code (tray icon, window chrome) touch need one
owner

`SettingsViewModel.MinimizeToTray`/`MinimizeOnClose` are the single source of truth for both the
Settings dialog and the tray icon's own context menu checkboxes - `MainWindow.xaml.cs` no longer
keeps its own `_minimizeToTray`/`_minimizeOnClose` fields, it reads `_viewModel.Settings.*`
directly and subscribes to `Settings.PropertyChanged` to keep the Forms tray menu's `.Checked`
in sync when the Settings dialog changes it. If a setting is editable from more than one place in
the UI, route both through the same ViewModel property rather than letting each surface keep its
own copy - that's exactly the class of bug rule 6 covers, just for settings instead of counts.

## 12. `Command` + `RelativeSource AncestorType=Window` through a `ContextMenu` is UNRELIABLE —
this has now bitten us at least 4 separate times

**Do not bind `Command` on a `MenuItem` inside a `ContextMenu` via
`RelativeSource FindAncestor, AncestorType=Window`.** A `ContextMenu` renders in its own `Popup`,
and that binding path has silently failed to resolve — no error, no crash, the click just does
nothing — for real, shipped menu items in this exact codebase, repeatedly:

1. `TogglePinBranchCommand` (branch tree pin/unpin) — fixed via `OnTogglePinBranchClicked`.
2. `ToggleGroupBranchesIntoFoldersCommand` (flat/folder view toggle) — fixed via
   `OnToggleGroupBranchesIntoFoldersClicked`.
3. The "Add folder to .gitignore" submenu's `IgnoreFolderCommand` (a nested `ItemsSource`-generated
   submenu, so a second hop deeper) — fixed via `OnIgnoreFolderInWorkingChangesClicked` /
   `OnIgnoreFolderInCommitDetailClicked`.
4. `DeleteBranchCommand` in the branch tree's context menu — worked in the old flat-ListBox tree
   rendering, broke silently after the TreeView conversion (rule 10) despite looking identical to
   sibling items (`MergeIntoCurrentBranchCommand`, `RebaseCurrentBranchOnCommand`,
   `CopyBranchNameCommand`, `CheckoutBranchCommand`) that still appear to work with the exact same
   binding syntax — fixed via `OnDeleteBranchClicked`.

That last one is the important lesson: **this is not consistently reproducible**. Some menu items
using this exact pattern work; others silently don't; and one that worked can silently break after
an unrelated change elsewhere (like re-templating the containing control). Don't treat "it looks
identical to a working one" as proof it's fine, and don't spend time trying to find the precise
Popup/visual-tree condition that makes it fail — the fix is always the same and always cheap:

```csharp
private void OnXClicked(object sender, RoutedEventArgs e)
{
    if (sender is not MenuItem menuItem) return;
    var branch = menuItem.DataContext switch
    {
        GitBranch b => b,
        BranchTreeItem { IsFolder: false } item => item.Branch,
        _ => null
    };
    if (branch != null) _viewModel.BranchList.SomeCommand.Execute(branch);
}
```
`Click="OnXClicked"` in XAML, resolve whatever the command needs off `menuItem.DataContext`
in code-behind, call `.Execute(...)` directly. This works because it never needs to walk back out
through the Popup boundary at all.

**If a `ContextMenu` item's Command silently does nothing when clicked — even if it "should" work,
even if a sibling item with identical-looking binding works fine — convert it to this pattern
first.** Don't assume it's a different bug (a guard, a `CanExecute`, stale state) until you've
ruled this out, since it's now the single most common cause of "the button doesn't work" reports
in this app's whole history. The existing Command+RelativeSource items that still work
(Merge/Rebase/Reset/Copy/Checkout as of this writing) are not proof the pattern is safe — they may
simply not have been hit by whatever timing/nesting condition triggers the failure yet. Per the
"no speculative abstractions" project rule (CLAUDE.md), don't proactively convert them until one
actually breaks — but when one does, this is the fix, immediately, no further investigation
needed.

---

**Before shipping a change to any of the files above:** rebuild, run the full test suite (currently
44 tests, should stay green), and actually feel the app for lag on a large real repo — the tests
lock in correctness, not perceived speed. If you introduce a `Clear()` on a virtualized list's
bound collection, a `RefreshRepositoryAsync()` call after a fast local action, or drop a
`ConfigureAwait(false)` from `GitCliService`, you are reintroducing a bug that was deliberately
fixed. Grep for these patterns if you're unsure.
