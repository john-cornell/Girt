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

            if (value != null && (AssociationMode == BranchAssociationMode.DimBeyondTrunk || AssociationMode == BranchAssociationMode.DimUnrelated))
            {
                UpdateDimmedStatesOnly();
            }
        }

        partial void OnFilterSubjectChanged(string value) => ScheduleApplyFilter();
        partial void OnFilterAuthorChanged(string value) => ScheduleApplyFilter();
        partial void OnFilterDateChanged(string value) => ScheduleApplyFilter();
        partial void OnFilterShaChanged(string value) => ScheduleApplyFilter();

        partial void OnAssociationModeChanged(BranchAssociationMode value)
        {
            ApplyFilterSync();
        }

        [RelayCommand]
        public void SetAssociationMode(string modeName)
        {
            if (Enum.TryParse<BranchAssociationMode>(modeName, true, out var mode))
            {
                AssociationMode = mode;
            }
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

        private void UpdateDimmedStatesOnly()
        {
            if (_allCommits.Count == 0) return;

            var currentMode = AssociationMode;
            if (currentMode == BranchAssociationMode.ShowAll) return;

            var includeFullTrunk = currentMode == BranchAssociationMode.DimUnrelated;
            var targetAnchor = SelectedCommit?.Hash ?? ActiveBranchName;

            var associated = ComputeAssociatedCommitHashes(_allCommits, _allBranches, targetAnchor, includeFullTrunk, out _);

            foreach (var c in FilteredCommits)
            {
                var isAssoc = associated.Contains(c.Hash);
                c.IsAssociated = isAssoc;
                c.IsDimmed = !isAssoc;
            }
        }

        private List<GitCommit> ComputeFilteredMatches()
        {
            if (_allCommits.Count == 0) return new List<GitCommit>();

            var subjectQuery = FilterSubject?.Trim() ?? string.Empty;
            var authorQuery = FilterAuthor?.Trim() ?? string.Empty;
            var dateQuery = FilterDate?.Trim() ?? string.Empty;
            var shaQuery = FilterSha?.Trim() ?? string.Empty;
            var currentMode = AssociationMode;
            var targetAnchor = SelectedCommit?.Hash ?? ActiveBranchName;

            var isHideMode = currentMode == BranchAssociationMode.HideUnrelated || currentMode == BranchAssociationMode.HideBeyondTrunk;
            var isDimMode = currentMode == BranchAssociationMode.DimUnrelated || currentMode == BranchAssociationMode.DimBeyondTrunk;
            var includeFullTrunk = currentMode == BranchAssociationMode.HideUnrelated || currentMode == BranchAssociationMode.DimUnrelated;

            var associatedHashes = currentMode == BranchAssociationMode.ShowAll
                ? null
                : ComputeAssociatedCommitHashes(_allCommits, _allBranches, targetAnchor, includeFullTrunk, out _);

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

        public HashSet<string> ComputeAssociatedCommitHashes(
            IReadOnlyList<GitCommit> allCommits,
            IReadOnlyList<GitBranch> branches,
            string? targetAnchor,
            bool includeFullTrunk,
            out string detectedTrunk)
        {
            var commitLookup = allCommits.ToDictionary(c => c.Hash, StringComparer.OrdinalIgnoreCase);
            detectedTrunk = "main";

            // 1. Identify Trunk Branch Tip Commit Hash
            string? trunkHash = null;
            var trunkCandidateNames = new[] { "main", "master", "develop", "trunk", "origin/main", "origin/master", "origin/develop" };
            foreach (var candidate in trunkCandidateNames)
            {
                var match = branches.FirstOrDefault(b => b.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
                if (match != null && !string.IsNullOrEmpty(match.TipCommitHash))
                {
                    trunkHash = match.TipCommitHash;
                    detectedTrunk = match.Name;
                    break;
                }
            }

            if (string.IsNullOrEmpty(trunkHash) && branches.Count > 0)
            {
                var firstWithUpstream = branches.FirstOrDefault(b => !string.IsNullOrEmpty(b.UpstreamName));
                if (firstWithUpstream != null)
                {
                    trunkHash = firstWithUpstream.TipCommitHash;
                    detectedTrunk = firstWithUpstream.Name;
                }
                else
                {
                    trunkHash = branches[0].TipCommitHash;
                    detectedTrunk = branches[0].Name;
                }
            }

            // 2. Resolve Target Commit Hash
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

            var associated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(targetHash)) return associated;

            // 3. Trunk First-Parent Backbone & Full Trunk Ancestors
            var trunkMainLine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var trunkAncestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(trunkHash))
            {
                trunkMainLine = TraverseFirstParents(trunkHash, commitLookup);
                TraverseAncestors(trunkHash, commitLookup, trunkAncestors);
            }

            // 4. Target Commit Ancestors
            var targetAncestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            TraverseAncestors(targetHash, commitLookup, targetAncestors);

            // 5. Find Fork Point on Trunk Backbone
            string? forkPointHash = null;
            if (trunkMainLine.Contains(targetHash))
            {
                // Target is directly on trunk backbone
                forkPointHash = targetHash;
            }
            else
            {
                var queue = new Queue<string>();
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                queue.Enqueue(targetHash);

                while (queue.Count > 0)
                {
                    var hash = queue.Dequeue();
                    if (!visited.Add(hash)) continue;

                    if (trunkMainLine.Contains(hash))
                    {
                        forkPointHash = hash;
                        break; // Found fork point on trunk backbone
                    }

                    if (commitLookup.TryGetValue(hash, out var commit) && commit.ParentHashes != null)
                    {
                        foreach (var p in commit.ParentHashes)
                        {
                            if (!visited.Contains(p)) queue.Enqueue(p);
                        }
                    }
                }
            }

            // Branch segment = all ancestors of target that are not on trunkMainLine, plus target, plus fork point
            var branchSpecific = targetAncestors.Except(trunkMainLine).ToHashSet(StringComparer.OrdinalIgnoreCase);
            branchSpecific.Add(targetHash);

            foreach (var h in branchSpecific)
            {
                associated.Add(h);
            }

            if (!string.IsNullOrEmpty(forkPointHash))
            {
                associated.Add(forkPointHash);
            }

            // 6. Find all descendants of the branch segment (e.g. PR merge commits like '78a1306', child commits)
            var nonForkBranchCommits = branchSpecific.Except(new[] { forkPointHash ?? "" }).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ancestorCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var commit in allCommits)
            {
                if (associated.Contains(commit.Hash)) continue;

                if (!ancestorCache.TryGetValue(commit.Hash, out var ancestors))
                {
                    ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    TraverseAncestors(commit.Hash, commitLookup, ancestors);
                    ancestorCache[commit.Hash] = ancestors;
                }

                // If commit descends from target commit or any side branch commit
                if (ancestors.Contains(targetHash) || ancestors.Overlaps(nonForkBranchCommits))
                {
                    associated.Add(commit.Hash);
                }
            }

            // If includeFullTrunk mode is enabled, also add the rest of trunk history
            if (includeFullTrunk)
            {
                foreach (var h in trunkAncestors)
                {
                    associated.Add(h);
                }
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
