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

        public ObservableCollection<GitCommit> FilteredCommits { get; } = new();

        public CommitHistoryViewModel(IGitService gitService, Func<string> getRepoPath, Action<GitCommit?> onCommitSelected)
        {
            _gitService = gitService;
            _getRepoPath = getRepoPath;
            _onCommitSelected = onCommitSelected;
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
            IsolatedTargetHash = hash;
            IsolatedTargetDescription = desc.Length > 30 ? desc[..27] + "..." : desc;
            IsBranchIsolated = true;
            AssociationMode = BranchAssociationMode.DimBeyondTrunk;

            var associated = ComputeBranchOnlyAssociatedHashes(_allCommits, _allBranches, hash);
            foreach (var c in FilteredCommits)
            {
                var isAssoc = associated.Contains(c.Hash);
                c.IsAssociated = isAssoc;
                c.IsDimmed = !isAssoc;
            }
        }

        [RelayCommand]
        public void HideToFork(object? parameter)
        {
            var hash = (parameter as GitCommit)?.Hash ?? (parameter as GitBranch)?.TipCommitHash ?? (parameter as string) ?? SelectedCommit?.Hash;
            if (string.IsNullOrEmpty(hash)) return;

            var desc = (parameter as GitCommit)?.Subject ?? (parameter as GitBranch)?.DisplayName ?? hash[..Math.Min(7, hash.Length)];
            IsolatedTargetHash = hash;
            IsolatedTargetDescription = desc.Length > 30 ? desc[..27] + "..." : desc;
            IsBranchIsolated = true;
            AssociationMode = BranchAssociationMode.HideBeyondTrunk;

            ApplyFilterSync();
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

        public void SetBranches(IReadOnlyList<GitBranch> branches, string currentBranch)
        {
            _allBranches = branches.ToList();
            if (!string.IsNullOrEmpty(currentBranch))
            {
                ActiveBranchName = currentBranch;
            }
            ApplyFilterCore();
        }

        public async Task LoadCommitsAsync()
        {
            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            IsLoading = true;
            try
            {
                var commits = await _gitService.GetCommitsAsync(repoPath, maxCount: 1000);
                _allCommits = commits.ToList();

                ApplyFilterCore();

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
            else
            {
                GitGraphLayoutEngine.ComputeGraphLayout(_allCommits);
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

            if (FilteredCommits.Count > 0 && (SelectedCommit == null || !FilteredCommits.Contains(SelectedCommit)))
            {
                SelectedCommit = FilteredCommits[0];
            }
        }

        /// <summary>
        /// Branch Only (Dim/Hide to Trunk Fork):
        /// ONLY illuminates:
        /// 1. The split commit on trunk (where branch branched off)
        /// 2. All commits on this branch
        /// 3. Any child branches branching off this branch (A -> B)
        /// 4. The merge commit if merged back into trunk
        /// Everything else (all other trunk history, other branches) is DIMMED.
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

            // If target itself is directly on trunk mainline
            if (trunkMainLine.Contains(targetHash))
            {
                associated.Add(targetHash);
                return associated;
            }

            // 4. Target is on a side branch:
            // Traverse backwards to find the fork point on trunk (WHERE IT SPLIT)
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
                    if (forkPointHash == null)
                    {
                        forkPointHash = hash; // The split commit on trunk
                    }
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
