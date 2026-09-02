Before editing `GitCliService`, `MainViewModel`, `CommitHistoryViewModel`, `BranchListViewModel`,
or `WorkingChangesViewModel`, read `AIREADME.md` at the repo root. It documents specific
performance fixes (ConfigureAwait, avoiding ObservableCollection Reset notifications, incremental
graph layout, targeted refresh helpers, stale computed-property notification) that are easy to
silently regress. Don't reintroduce the patterns it calls out.
