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

        private List<GitCommit> ComputeFilteredMatches()
        {
            if (_allCommits.Count == 0) return new List<GitCommit>();

            var subjectQuery = FilterSubject?.Trim() ?? string.Empty;
            var authorQuery = FilterAuthor?.Trim() ?? string.Empty;
            var dateQuery = FilterDate?.Trim() ?? string.Empty;
            var shaQuery = FilterSha?.Trim() ?? string.Empty;
            var currentMode = AssociationMode;
            var currentBranch = ActiveBranchName;

            var isHideMode = currentMode == BranchAssociationMode.HideUnrelated || currentMode == BranchAssociationMode.HideBeyondTrunk;
            var isDimMode = currentMode == BranchAssociationMode.DimUnrelated || currentMode == BranchAssociationMode.DimBeyondTrunk;
            var includeFullTrunk = currentMode == BranchAssociationMode.HideUnrelated || currentMode == BranchAssociationMode.DimUnrelated;

            var associatedHashes = currentMode == BranchAssociationMode.ShowAll
                ? null
                : ComputeAssociatedCommitHashes(_allCommits, _allBranches, currentBranch, includeFullTrunk, out _);

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

        private HashSet<string> ComputeAssociatedCommitHashes(
            IReadOnlyList<GitCommit> allCommits,
            IReadOnlyList<GitBranch> branches,
            string activeBranchName,
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

            // 2. Identify Active / Selected Branch Tip Commit Hash
            string? activeHash = null;
            if (!string.IsNullOrEmpty(activeBranchName))
            {
                var activeBranch = branches.FirstOrDefault(b => b.Name.Equals(activeBranchName, StringComparison.OrdinalIgnoreCase) ||
                                                               b.DisplayName.Equals(activeBranchName, StringComparison.OrdinalIgnoreCase));
                if (activeBranch != null)
                {
                    activeHash = activeBranch.TipCommitHash;
                }
            }

            if (string.IsNullOrEmpty(activeHash))
            {
                activeHash = SelectedCommit?.Hash ?? trunkHash;
            }

            var associated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 3. Trunk Ancestors
            var trunkAncestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(trunkHash))
            {
                TraverseAncestors(trunkHash, commitLookup, trunkAncestors);
            }

            // 4. Active Branch Ancestors
            var activeAncestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(activeHash))
            {
                TraverseAncestors(activeHash, commitLookup, activeAncestors);
            }

            if (includeFullTrunk)
            {
                foreach (var h in trunkAncestors) associated.Add(h);
                foreach (var h in activeAncestors) associated.Add(h);

                var branchSpecificAncestors = activeAncestors.Except(trunkAncestors).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (branchSpecificAncestors.Count > 0 || !string.IsNullOrEmpty(activeHash))
                {
                    foreach (var commit in allCommits)
                    {
                        if (associated.Contains(commit.Hash)) continue;

                        var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        TraverseAncestors(commit.Hash, commitLookup, ancestors);
                        if ((!string.IsNullOrEmpty(activeHash) && ancestors.Contains(activeHash)) ||
                            ancestors.Overlaps(branchSpecificAncestors))
                        {
                            associated.Add(commit.Hash);
                        }
                    }
                }
            }
            else
            {
                // Branch to Trunk Fork Point Only:
                string? forkPointHash = null;
                if (!string.IsNullOrEmpty(activeHash) && !string.IsNullOrEmpty(trunkHash))
                {
                    var queue = new Queue<string>();
                    var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    queue.Enqueue(activeHash);

                    while (queue.Count > 0)
                    {
                        var hash = queue.Dequeue();
                        if (!visited.Add(hash)) continue;

                        if (trunkAncestors.Contains(hash))
                        {
                            forkPointHash = hash;
                            break; // Found the trunk fork point
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

                if (activeHash == trunkHash || string.IsNullOrEmpty(forkPointHash))
                {
                    // Active branch is trunk itself or fork point not found
                    foreach (var h in activeAncestors) associated.Add(h);
                }
                else
                {
                    // Only active branch commits down to the fork point on trunk (inclusive)
                    var branchSpecificAncestors = activeAncestors.Except(trunkAncestors).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var h in branchSpecificAncestors) associated.Add(h);
                    associated.Add(forkPointHash); // Include the fork point commit

                    // Plus any child branches that stem from this branch
                    foreach (var commit in allCommits)
                    {
                        if (associated.Contains(commit.Hash)) continue;

                        var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        TraverseAncestors(commit.Hash, commitLookup, ancestors);
                        if ((!string.IsNullOrEmpty(activeHash) && ancestors.Contains(activeHash)) ||
                            ancestors.Overlaps(branchSpecificAncestors))
                        {
                            associated.Add(commit.Hash);
                        }
                    }
                }
            }

            return associated;
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
