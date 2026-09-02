using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Girt.Models;
using Girt.Services;

namespace Girt.ViewModels
{
    public partial class CommitHistoryViewModel : ObservableObject
    {
        private readonly IGitService _gitService;
        private readonly Func<string> _getRepoPath;
        private readonly Action<GitCommit?> _onCommitSelected;
        private readonly Action<string, string> _showMessage;

        [ObservableProperty]
        private string _filterSubject = string.Empty;

        [ObservableProperty]
        private string _filterAuthor = string.Empty;

        [ObservableProperty]
        private string _filterDate = string.Empty;

        [ObservableProperty]
        private string _filterSha = string.Empty;

        [ObservableProperty]
        private GitCommit? _selectedCommit;

        [ObservableProperty]
        private BranchAssociationMode _associationMode = BranchAssociationMode.ShowAll;

        [ObservableProperty]
        private string _activeBranchName = string.Empty;

        [ObservableProperty]
        private string _trunkBranchName = "main";

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private bool _isBranchIsolated;

        [ObservableProperty]
        private string? _isolatedTargetHash;

        [ObservableProperty]
        private string? _isolatedTargetDescription;

        private List<GitCommit> _allCommits = new();
        private List<GitBranch> _allBranches = new();
        private CancellationTokenSource? _filterCts;

        // Tracks which _allCommits instance the graph layout was last computed for, so
        // ComputeFilteredMatches can skip redoing it when only the filter/isolation state
        // changed, not the underlying commit list.
        private List<GitCommit>? _layoutComputedFor;

        public ObservableCollection<GitCommit> FilteredCommits { get; } = new();

        public CommitHistoryViewModel(
            IGitService gitService,
            Func<string> getRepoPath,
            Action<GitCommit?> onCommitSelected,
            Action<string, string>? showMessage = null)
        {
            _gitService = gitService;
            _getRepoPath = getRepoPath;
            _onCommitSelected = onCommitSelected;
            _showMessage = showMessage ?? ((title, message) =>
                MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information));
        }

        partial void OnSelectedCommitChanged(GitCommit? value)
        {
            _onCommitSelected(value);
        }

        partial void OnFilterSubjectChanged(string value) => ScheduleApplyFilter();
        partial void OnFilterAuthorChanged(string value) => ScheduleApplyFilter();
        partial void OnFilterDateChanged(string value) => ScheduleApplyFilter();
        partial void OnFilterShaChanged(string value) => ScheduleApplyFilter();

        [RelayCommand]
        public void DimToFork(object? parameter)
        {
            var hash = (parameter as GitCommit)?.Hash ?? (parameter as GitBranch)?.TipCommitHash ?? (parameter as string) ?? SelectedCommit?.Hash;
            if (string.IsNullOrEmpty(hash)) return;

            var desc = (parameter as GitCommit)?.Subject ?? (parameter as GitBranch)?.DisplayName ?? hash[..Math.Min(7, hash.Length)];

            // Isolating trunk itself makes no sense - there's nothing to isolate it from.
            var associated = ComputeBranchOnlyAssociatedHashes(_allCommits, _allBranches, hash);
            if (associated.Count == 0)
            {
                ShowNothingToIsolateMessage(desc);
                return;
            }

            IsolatedTargetHash = hash;
            IsolatedTargetDescription = desc.Length > 30 ? desc[..27] + "..." : desc;
            IsBranchIsolated = true;
            AssociationMode = BranchAssociationMode.DimBeyondTrunk;

            // GitCommit has no change notification, so mutating IsDimmed on items already in
            // FilteredCommits wouldn't repaint anything - go through the same Clear+re-Add
            // refresh HideToFork uses, which forces WPF to re-read Opacity on fresh containers.
            ApplyFilterSync();
        }

        [RelayCommand]
        public void HideToFork(object? parameter)
        {
            var hash = (parameter as GitCommit)?.Hash ?? (parameter as GitBranch)?.TipCommitHash ?? (parameter as string) ?? SelectedCommit?.Hash;
            if (string.IsNullOrEmpty(hash)) return;

            var desc = (parameter as GitCommit)?.Subject ?? (parameter as GitBranch)?.DisplayName ?? hash[..Math.Min(7, hash.Length)];

            // Isolating trunk itself makes no sense - there's nothing to isolate it from.
            if (ComputeBranchOnlyAssociatedHashes(_allCommits, _allBranches, hash).Count == 0)
            {
                ShowNothingToIsolateMessage(desc);
                return;
            }

            IsolatedTargetHash = hash;
            IsolatedTargetDescription = desc.Length > 30 ? desc[..27] + "..." : desc;
            IsBranchIsolated = true;
            AssociationMode = BranchAssociationMode.HideBeyondTrunk;

            ApplyFilterSync();
        }

        private void ShowNothingToIsolateMessage(string desc)
        {
            var trunkName = ResolveTrunkBranchName(_allBranches) ?? "trunk";
            var shortDesc = desc.Length > 40 ? desc[..37] + "..." : desc;
            _showMessage(
                "Nothing to Isolate",
                $"'{shortDesc}' is already on {trunkName} - there's nothing to isolate it from.\n\n" +
                "Pick a commit or branch that has actually diverged from trunk.");
        }

        private static readonly string[] TrunkCandidateNames =
            { "main", "master", "develop", "trunk", "origin/main", "origin/master", "origin/develop" };

        private static string? ResolveTrunkBranchName(IReadOnlyList<GitBranch> branches)
        {
            foreach (var candidate in TrunkCandidateNames)
            {
                var match = branches.FirstOrDefault(b => b.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
                if (match != null && !string.IsNullOrEmpty(match.TipCommitHash))
                {
                    return match.Name;
                }
            }

            if (branches.Count > 0)
            {
                var firstWithUpstream = branches.FirstOrDefault(b => !string.IsNullOrEmpty(b.UpstreamName));
                return (firstWithUpstream ?? branches[0]).Name;
            }

            return null;
        }

        [RelayCommand]
        public void ClearIsolation()
        {
            IsolatedTargetHash = null;
            IsolatedTargetDescription = null;
            IsBranchIsolated = false;
            AssociationMode = BranchAssociationMode.ShowAll;

            foreach (var c in _allCommits)
            {
                c.IsAssociated = true;
                c.IsDimmed = false;
            }

            ApplyFilterSync();
        }

        [RelayCommand]
        public void ClearFilters()
        {
            _filterCts?.Cancel();
            FilterSubject = string.Empty;
            FilterAuthor = string.Empty;
            FilterDate = string.Empty;
            FilterSha = string.Empty;
            ApplyFilterCore();
        }

        // Deliberately doesn't (re)apply the filter/graph-layout itself - every current caller
        // either immediately awaits LoadCommitsAsync afterward (which does, on fresh commits;
        // recomputing here first against the still-stale _allCommits would just be thrown away)
        // or is expected to call ApplyFilterAsync itself when it needs the display refreshed
        // without a full commit reload (e.g. on branch selection).
        public void SetBranches(IReadOnlyList<GitBranch> branches, string currentBranch)
        {
            _allBranches = branches.ToList();
            if (!string.IsNullOrEmpty(currentBranch))
            {
                ActiveBranchName = currentBranch;
            }
        }

        public async Task LoadCommitsAsync()
        {
            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            IsLoading = true;
            try
            {
                var commits = await _gitService.GetCommitsAsync(repoPath, maxCount: 1000);
                var freshCommits = commits.ToList();
                var previousCommits = _allCommits;

                // ComputeFilteredMatches includes GitGraphLayoutEngine.ComputeGraphLayout over
                // up to 1000 commits - real CPU work, not I/O, so it was blocking the UI thread
                // (and the whole window, mouse included) for however long that took on every
                // single refresh. Task.Run moves it off the UI thread; only the final
                // ObservableCollection update needs to happen back on it. TryBuildIncrementalCommitList
                // additionally tries to avoid redoing that work at all when most of what changed
                // is "a few new commits at the top" rather than the whole history.
                var matches = await Task.Run(() =>
                {
                    _allCommits = TryBuildIncrementalCommitList(previousCommits, freshCommits) ?? freshCommits;
                    return ComputeFilteredMatches();
                });
                ApplyFilteredCommitsList(matches);

                if (FilteredCommits.Count > 0 && SelectedCommit == null)
                {
                    SelectedCommit = FilteredCommits[0];
                }
            }
            finally
            {
                IsLoading = false;
            }
        }

        // Splices a just-created commit straight into the already-loaded graph, without waiting
        // on (or triggering) any kind of broader refresh - this is what lets a commit show up
        // in the graph immediately while still only costing the one cheap "git log -1" call that
        // fetched newCommit's real data (hash/author/date/parent), reusing the exact same
        // verified incremental-splice path a real reload would take for "one new commit at the
        // top". Falls back to a full local relayout only if that verification doesn't hold
        // (which would mean newCommit's parent isn't actually the previous top commit).
        public void PrependLocalCommit(GitCommit newCommit)
        {
            var synthetic = new List<GitCommit> { newCommit };
            synthetic.AddRange(_allCommits);

            var incremental = TryBuildIncrementalCommitList(_allCommits, synthetic);
            if (incremental != null)
            {
                _allCommits = incremental;
            }
            else
            {
                _allCommits = synthetic;
                GitGraphLayoutEngine.ComputeGraphLayout(_allCommits);
                _layoutComputedFor = _allCommits;
            }

            // When nothing is filtered/isolated, the new commit unconditionally belongs at the
            // top of FilteredCommits too - insert it directly (a single Add notification the
            // virtualized list handles by adding one row) instead of going through
            // ApplyFilteredCommitsList, which Clear()s and re-Adds the *entire* list (a Reset
            // notification that tears down and rebuilds every visible row's container - real
            // work regardless of how cheap the layout computation above was). Any active filter
            // or isolation mode falls back to the normal recompute, since whether the new commit
            // actually belongs in view depends on more than just "is it new".
            var noFiltersOrIsolationActive =
                string.IsNullOrEmpty(FilterSubject) && string.IsNullOrEmpty(FilterAuthor) &&
                string.IsNullOrEmpty(FilterDate) && string.IsNullOrEmpty(FilterSha) &&
                !IsBranchIsolated;

            if (noFiltersOrIsolationActive)
            {
                newCommit.IsAssociated = true;
                newCommit.IsDimmed = false;
                FilteredCommits.Insert(0, newCommit);
                if (SelectedCommit == null) SelectedCommit = newCommit;
            }
            else
            {
                ApplyFilteredCommitsList(ComputeFilteredMatches());
            }
        }

        // Avoids redoing the full graph layout on every reload. git log --all is re-parsed into
        // entirely new GitCommit instances every time regardless (that part isn't avoidable
        // without a bigger change to fetch only what's new from git itself) - but the CPU-heavy
        // part is GitGraphLayoutEngine.ComputeGraphLayout, and that only genuinely needs
        // redoing for commits that are actually new. When the previous top commit is still
        // findable in the fresh list with everything after it unchanged, only the new commits
        // above it get laid out; the rest is reused as-is (same objects, so SelectedCommit and
        // anything else already holding a reference to them stays valid). Any history rewrite,
        // reorder, or anything else unexpected just falls back to null - a full relayout, same
        // as before this existed, so this can never leave the graph in a wrong-but-plausible
        // state - it either reuses correctly or doesn't reuse at all.
        private List<GitCommit>? TryBuildIncrementalCommitList(List<GitCommit> previous, List<GitCommit> fresh)
        {
            if (previous.Count == 0 || fresh.Count == 0) return null;

            // Most refreshes aren't triggered by a new commit at all (staging a file, an
            // external tool touching .git, a watcher firing) - if nothing changed, this is free.
            if (fresh.Count == previous.Count)
            {
                var unchanged = true;
                for (var i = 0; i < fresh.Count; i++)
                {
                    if (!string.Equals(fresh[i].Hash, previous[i].Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        unchanged = false;
                        break;
                    }
                }
                if (unchanged)
                {
                    _layoutComputedFor = previous;
                    return previous;
                }
            }

            var oldTopHash = previous[0].Hash;
            var boundary = fresh.FindIndex(c => string.Equals(c.Hash, oldTopHash, StringComparison.OrdinalIgnoreCase));
            if (boundary <= 0) return null;

            // The fetch is capped at 1000 commits, so once history is at that cap, new commits
            // push the oldest ones out of the window - reuse only as much of the old tail as
            // still fits, dropping whatever fell off the end (which the fresh fetch already
            // excludes).
            var overlapLength = Math.Min(previous.Count, fresh.Count - boundary);
            for (var i = 0; i < overlapLength; i++)
            {
                if (!string.Equals(fresh[boundary + i].Hash, previous[i].Hash, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            var newPrefix = fresh.Take(boundary).ToList();
            var finalLanes = GitGraphLayoutEngine.ComputeGraphLayout(newPrefix);

            // previous's layout assumed its own top commit started with a clean slate - lane 0,
            // nothing else active. Reusing it is only valid if the new prefix leaves the graph
            // in that exact state by the time it reaches the old commits.
            var stillActive = finalLanes.Where(l => l != null).ToList();
            if (stillActive.Count != 1 || !string.Equals(stillActive[0], oldTopHash, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var reusedTail = previous.Take(overlapLength).ToList();
            foreach (var c in reusedTail)
            {
                c.RowIndex += boundary;
            }

            var combined = new List<GitCommit>(newPrefix.Count + reusedTail.Count);
            combined.AddRange(newPrefix);
            combined.AddRange(reusedTail);
            _layoutComputedFor = combined;
            return combined;
        }

        public async Task ApplyFilterAsync()
        {
            var matches = await Task.Run(() => ComputeFilteredMatches());
            ApplyFilteredCommitsList(matches);
        }

        public void ApplyFilter()
        {
            ApplyFilterSync();
        }

        public void ApplyFilterSync()
        {
            _filterCts?.Cancel();
            ApplyFilterCore();
        }

        private void ScheduleApplyFilter()
        {
            _filterCts?.Cancel();
            _filterCts = new CancellationTokenSource();
            var token = _filterCts.Token;

            _ = Task.Run(async () =>
            {
                await Task.Delay(80, token);
                if (token.IsCancellationRequested) return;

                var matches = ComputeFilteredMatches();
                if (token.IsCancellationRequested) return;

                var app = Application.Current;
                if (app != null && !app.Dispatcher.CheckAccess())
                {
                    await app.Dispatcher.InvokeAsync(() => ApplyFilteredCommitsList(matches));
                }
                else
                {
                    ApplyFilteredCommitsList(matches);
                }
            }, token);
        }

        private void ApplyFilterCore()
        {
            var matches = ComputeFilteredMatches();
            ApplyFilteredCommitsList(matches);
        }

        private List<GitCommit> ComputeFilteredMatches()
        {
            if (_allCommits.Count == 0) return new List<GitCommit>();

            var subjectQuery = FilterSubject?.Trim() ?? string.Empty;
            var authorQuery = FilterAuthor?.Trim() ?? string.Empty;
            var dateQuery = FilterDate?.Trim() ?? string.Empty;
            var shaQuery = FilterSha?.Trim() ?? string.Empty;

            var isHideMode = IsBranchIsolated && AssociationMode == BranchAssociationMode.HideBeyondTrunk;
            var isDimMode = IsBranchIsolated && AssociationMode == BranchAssociationMode.DimBeyondTrunk;

            HashSet<string>? associatedHashes = null;
            if (IsBranchIsolated && !string.IsNullOrEmpty(IsolatedTargetHash))
            {
                associatedHashes = ComputeBranchOnlyAssociatedHashes(_allCommits, _allBranches, IsolatedTargetHash);
            }

            var matches = new List<GitCommit>();

            foreach (var c in _allCommits)
            {
                var matchesText =
                    (string.IsNullOrEmpty(subjectQuery) || c.Subject.Contains(subjectQuery, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrEmpty(authorQuery) || c.AuthorName.Contains(authorQuery, StringComparison.OrdinalIgnoreCase) || c.AuthorEmail.Contains(authorQuery, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrEmpty(dateQuery) || c.RelativeDate.Contains(dateQuery, StringComparison.OrdinalIgnoreCase) || c.Date.ToString("yyyy-MM-dd").Contains(dateQuery, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrEmpty(shaQuery) || c.ShortHash.Contains(shaQuery, StringComparison.OrdinalIgnoreCase) || c.Hash.Contains(shaQuery, StringComparison.OrdinalIgnoreCase));

                if (!matchesText) continue;

                if (associatedHashes != null)
                {
                    var isAssoc = associatedHashes.Contains(c.Hash);
                    c.IsAssociated = isAssoc;

                    if (isHideMode)
                    {
                        if (!isAssoc) continue;
                        c.IsDimmed = false;
                    }
                    else if (isDimMode)
                    {
                        c.IsDimmed = !isAssoc;
                    }
                }
                else
                {
                    c.IsAssociated = true;
                    c.IsDimmed = false;
                }

                matches.Add(c);
            }

            if (isHideMode)
            {
                GitGraphLayoutEngine.ComputeGraphLayout(matches);
            }
            else if (!ReferenceEquals(_layoutComputedFor, _allCommits))
            {
                // Text-filtering and non-hide isolation toggles call this repeatedly against the
                // same _allCommits instance (nothing about the graph itself changed - only which
                // rows are shown/dimmed), so re-laying it out every time was pure waste. Only
                // recompute when _allCommits has actually been replaced by a reload.
                GitGraphLayoutEngine.ComputeGraphLayout(_allCommits);
                _layoutComputedFor = _allCommits;
            }

            return matches;
        }

        private void ApplyFilteredCommitsList(List<GitCommit> matches)
        {
            FilteredCommits.Clear();
            foreach (var c in matches)
            {
                FilteredCommits.Add(c);
            }

            // GitCommit.Equals is by hash (see that override for why), so this correctly
            // recognizes "the same commit, just a fresh instance from this reload" and leaves
            // SelectedCommit alone - both WPF's own selection-restore-on-Reset (which also goes
            // through Equals) and CommitDetailViewModel's identical hash check stay in sync with
            // this. Only actually reassign when the previously selected commit is genuinely gone
            // (filtered out, or this is the first load).
            if (FilteredCommits.Count == 0)
            {
                SelectedCommit = null;
            }
            else if (SelectedCommit == null || !FilteredCommits.Contains(SelectedCommit))
            {
                SelectedCommit = FilteredCommits[0];
            }
        }

        /// <summary>
        /// Branch Only (Dim/Hide to Trunk Fork):
        /// ONLY illuminates:
        /// 1. All commits on this branch (and, if this branch itself forked off another
        ///    side branch rather than trunk directly, that ancestor branch's commits too)
        /// 2. The single divergence commit on trunk (where this branch's lineage split off)
        /// 3. Any child branches branching off this branch (A -> B)
        /// 4. The merge commit if merged back into trunk
        /// Everything else - all other trunk history, other branches - is DIMMED.
        /// Isolating trunk itself is nonsensical and returns an empty set (no-op signal).
        /// </summary>
        public HashSet<string> ComputeBranchOnlyAssociatedHashes(
            IReadOnlyList<GitCommit> allCommits,
            IReadOnlyList<GitBranch> branches,
            string? targetAnchor)
        {
            var commitLookup = allCommits.ToDictionary(c => c.Hash, StringComparer.OrdinalIgnoreCase);
            var associated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (allCommits.Count == 0) return associated;

            // 1. Identify Trunk Branch Tip
            string? trunkHash = null;
            var trunkCandidateNames = new[] { "main", "master", "develop", "trunk", "origin/main", "origin/master", "origin/develop" };
            foreach (var candidate in trunkCandidateNames)
            {
                var match = branches.FirstOrDefault(b => b.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
                if (match != null && !string.IsNullOrEmpty(match.TipCommitHash))
                {
                    trunkHash = match.TipCommitHash;
                    break;
                }
            }

            if (string.IsNullOrEmpty(trunkHash) && branches.Count > 0)
            {
                var firstWithUpstream = branches.FirstOrDefault(b => !string.IsNullOrEmpty(b.UpstreamName));
                trunkHash = firstWithUpstream?.TipCommitHash ?? branches[0].TipCommitHash;
            }

            // 2. Resolve Selected Target Hash
            string? targetHash = null;
            if (!string.IsNullOrEmpty(targetAnchor))
            {
                if (commitLookup.ContainsKey(targetAnchor))
                {
                    targetHash = targetAnchor;
                }
                else
                {
                    var branchMatch = branches.FirstOrDefault(b => b.Name.Equals(targetAnchor, StringComparison.OrdinalIgnoreCase) ||
                                                                   b.DisplayName.Equals(targetAnchor, StringComparison.OrdinalIgnoreCase));
                    if (branchMatch != null)
                    {
                        targetHash = branchMatch.TipCommitHash;
                    }
                }
            }

            if (string.IsNullOrEmpty(targetHash))
            {
                targetHash = SelectedCommit?.Hash ?? trunkHash;
            }

            if (string.IsNullOrEmpty(targetHash)) return associated;

            // 3. Trunk First-Parent Backbone
            var trunkMainLine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(trunkHash))
            {
                trunkMainLine = TraverseFirstParents(trunkHash, commitLookup);
            }

            // Isolating trunk itself doesn't make sense - nothing to isolate it from.
            // Signal "no-op" with an empty set.
            if (trunkMainLine.Contains(targetHash))
            {
                return associated;
            }

            // 4. Target is on a side branch:
            // Traverse backwards to find the single divergence commit on trunk (WHERE IT SPLIT).
            string? forkPointHash = null;
            var branchAncestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            queue.Enqueue(targetHash);

            while (queue.Count > 0)
            {
                var hash = queue.Dequeue();
                if (!visited.Add(hash)) continue;

                if (trunkMainLine.Contains(hash))
                {
                    forkPointHash ??= hash; // The divergence commit on trunk
                    continue; // Do NOT traverse older trunk commits!
                }

                branchAncestors.Add(hash);

                if (commitLookup.TryGetValue(hash, out var commit) && commit.ParentHashes != null)
                {
                    foreach (var p in commit.ParentHashes)
                    {
                        if (!visited.Contains(p)) queue.Enqueue(p);
                    }
                }
            }

            var branchCoreCommits = new HashSet<string>(branchAncestors, StringComparer.OrdinalIgnoreCase);
            branchCoreCommits.Add(targetHash);

            // 5. Find forward descendants on Branch A and any sub-branches off A (A -> B)
            var branchAllCommits = new HashSet<string>(branchCoreCommits, StringComparer.OrdinalIgnoreCase);
            bool addedAny;
            do
            {
                addedAny = false;
                foreach (var commit in allCommits)
                {
                    if (branchAllCommits.Contains(commit.Hash)) continue;
                    if (commit.ParentHashes == null || commit.ParentHashes.Count == 0) continue;

                    bool hasBranchParent = false;
                    foreach (var p in commit.ParentHashes)
                    {
                        if (branchAllCommits.Contains(p))
                        {
                            hasBranchParent = true;
                            break;
                        }
                    }

                    if (hasBranchParent)
                    {
                        if (trunkMainLine.Contains(commit.Hash))
                        {
                            // Merge commit into trunk: include the merge commit, but do NOT continue down trunk
                            associated.Add(commit.Hash);
                        }
                        else
                        {
                            // Further commit on this branch or sub-branch off A (A -> B)
                            branchAllCommits.Add(commit.Hash);
                            addedAny = true;
                        }
                    }
                }
            } while (addedAny);

            foreach (var h in branchAllCommits)
            {
                associated.Add(h);
            }

            if (!string.IsNullOrEmpty(forkPointHash))
            {
                associated.Add(forkPointHash);
            }

            return associated;
        }

        private static HashSet<string> TraverseFirstParents(string startHash, Dictionary<string, GitCommit> lookup)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(startHash)) return visited;

            var current = startHash;
            while (!string.IsNullOrEmpty(current) && visited.Add(current))
            {
                if (lookup.TryGetValue(current, out var commit) && commit.ParentHashes != null && commit.ParentHashes.Count > 0)
                {
                    current = commit.ParentHashes[0]; // First parent is the main trunk line
                }
                else
                {
                    break;
                }
            }

            return visited;
        }

        private static void TraverseAncestors(string startHash, Dictionary<string, GitCommit> lookup, HashSet<string> visited)
        {
            if (string.IsNullOrEmpty(startHash)) return;

            var queue = new Queue<string>();
            queue.Enqueue(startHash);

            while (queue.Count > 0)
            {
                var hash = queue.Dequeue();
                if (!visited.Add(hash)) continue;

                if (lookup.TryGetValue(hash, out var commit) && commit.ParentHashes != null)
                {
                    foreach (var parent in commit.ParentHashes)
                    {
                        if (!visited.Contains(parent))
                        {
                            queue.Enqueue(parent);
                        }
                    }
                }
            }
        }
    }
}
