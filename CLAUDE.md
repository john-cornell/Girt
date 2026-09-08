# Project rules

- No speculative abstractions. Don't add interfaces, config knobs, extension points, or
  generalized helpers for a use case that doesn't exist yet. Build for what's actually asked.
- Prefer quality, simplicity, robustness, scalability, and long-term maintainability over
  cleverness or shortcuts.

# Performance architecture

Before editing `GitCliService`, `MainViewModel`, `CommitHistoryViewModel`, `BranchListViewModel`,
or `WorkingChangesViewModel`, read `AIREADME.md` at the repo root. It documents specific
performance fixes (ConfigureAwait, avoiding ObservableCollection Reset notifications, incremental
graph layout, targeted refresh helpers, stale computed-property notification) that are easy to
silently regress. Don't reintroduce the patterns it calls out.
