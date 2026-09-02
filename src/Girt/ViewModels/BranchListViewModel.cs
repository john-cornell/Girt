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
    public partial class BranchListViewModel : ObservableObject
    {
        private readonly IGitService _gitService;
        private readonly Func<string> _getRepoPath;
        private readonly Func<Task> _onBranchChanged;
        private readonly Action<bool> _saveGroupBranchesIntoFolders;

        [ObservableProperty]
        private string _filterText = string.Empty;

        [ObservableProperty]
        private GitBranch? _selectedBranch;

        [ObservableProperty]
        private bool _isLocalExpanded = true;

        [ObservableProperty]
        private bool _isRemoteExpanded = true;

        [ObservableProperty]
        private bool _isFetching;

        [ObservableProperty]
        private string _fetchStatusMessage = string.Empty;

        [ObservableProperty]
        private bool _groupBranchesIntoFolders;

        private List<GitBranch> _allLocalBranches = new();
        private List<GitBranch> _allRemoteBranches = new();
        private HashSet<string> _knownBranchNames = new(StringComparer.OrdinalIgnoreCase);
        private string? _lastRepoPath;

        public List<GitBranch> AllBranches { get; private set; } = new();
        public ObservableCollection<GitBranch> FilteredLocalBranches { get; } = new();
        public ObservableCollection<GitBranch> FilteredRemoteBranches { get; } = new();
        public ObservableCollection<BranchTreeItem> LocalBranchTree { get; } = new();
        public ObservableCollection<BranchTreeItem> RemoteBranchTree { get; } = new();
        public ObservableCollection<GitBranch> NewBranches { get; } = new();

        public bool HasNewBranches => NewBranches.Count > 0;

        private readonly Action<bool, string?> _setBusy;
        private readonly Func<string, HashSet<string>> _loadPinnedBranches;
        private readonly Action<string, IEnumerable<string>> _savePinnedBranches;
        private HashSet<string> _pinnedBranchNames = new(StringComparer.OrdinalIgnoreCase);

        public BranchListViewModel(
            IGitService gitService,
            Func<string> getRepoPath,
            Func<Task> onBranchChanged,
            bool initialGroupBranchesIntoFolders,
            Action<bool> saveGroupBranchesIntoFolders,
            Action<bool, string?> setBusy,
            Func<string, HashSet<string>> loadPinnedBranches,
            Action<string, IEnumerable<string>> savePinnedBranches)
        {
            _gitService = gitService;
            _getRepoPath = getRepoPath;
            _onBranchChanged = onBranchChanged;
            _saveGroupBranchesIntoFolders = saveGroupBranchesIntoFolders;
            _groupBranchesIntoFolders = initialGroupBranchesIntoFolders;
            _setBusy = setBusy;
            _loadPinnedBranches = loadPinnedBranches;
            _savePinnedBranches = savePinnedBranches;
        }

        private CancellationTokenSource? _filterCts;

        // Every keystroke used to re-run ApplyFilter() immediately - full re-filter of every
        // branch plus (in folder view) a full folder-tree rebuild, on the UI thread, on every
        // single character typed or deleted. Debouncing coalesces a burst of keystrokes (typing
        // fast, or holding backspace) into one filter pass after typing actually pauses, and
        // cancels any filter pass that's still waiting when a further keystroke arrives - so
        // there's never more than one pending, and a stale one never runs after the fact.
        partial void OnFilterTextChanged(string value)
        {
            _filterCts?.Cancel();
            _filterCts = new CancellationTokenSource();
            _ = ScheduleApplyFilterAsync(_filterCts.Token);
        }

        private async Task ScheduleApplyFilterAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(120, token);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested) return;
            ApplyFilter();
        }

        [RelayCommand]
        public void ToggleGroupBranchesIntoFolders()
        {
            GroupBranchesIntoFolders = !GroupBranchesIntoFolders;
        }

        partial void OnGroupBranchesIntoFoldersChanged(bool value)
        {
            _saveGroupBranchesIntoFolders(value);
            if (value)
            {
                // Folder trees aren't kept up to date while in flat view (see ApplyLocalFilter/
                // ApplyRemoteFilter), so they may be stale or never built - rebuild on switching in.
                ApplyLocalFilter();
                ApplyRemoteFilter();
            }
            else
            {
                LocalBranchTree.Clear();
                RemoteBranchTree.Clear();
            }
        }

        public async Task LoadBranchesAsync()
        {
            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            var branches = await _gitService.GetBranchesAsync(repoPath);
            AllBranches = branches.ToList();

            // A repo switch means every branch name is "new" relative to the previous repo's
            // snapshot - that's not a real discovery, so reset the baseline instead of diffing.
            var isRepoSwitch = !string.Equals(_lastRepoPath, repoPath, StringComparison.OrdinalIgnoreCase);
            _lastRepoPath = repoPath;

            if (isRepoSwitch)
            {
                NewBranches.Clear();
                OnPropertyChanged(nameof(HasNewBranches));
            }
            else if (_knownBranchNames.Count > 0)
            {
                // A local branch that mirrors an already-known remote branch isn't a fresh
                // discovery - it's that remote branch having just been checked out. Without this,
                // checking out a "new" remote branch would flag its newly-created local mirror as
                // ANOTHER new branch on this very same reload, so the New Branches list never
                // actually shrank - it just swapped the remote entry for a local-looking one.
                var knownRemoteLocalEquivalents = branches
                    .Where(b => b.IsRemote && b.RemoteName != null && b.Name.StartsWith(b.RemoteName + "/") && _knownBranchNames.Contains(b.Name))
                    .Select(b => b.Name.Substring(b.RemoteName!.Length + 1))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Detect newly created/discovered branches since last snapshot
                var newDiscovered = branches
                    .Where(b => !_knownBranchNames.Contains(b.Name))
                    .Where(b => b.IsRemote || !knownRemoteLocalEquivalents.Contains(b.Name))
                    .ToList();
                foreach (var nb in newDiscovered)
                {
                    if (!NewBranches.Any(b => b.Name.Equals(nb.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        NewBranches.Insert(0, nb);
                    }
                }
                OnPropertyChanged(nameof(HasNewBranches));
            }

            _knownBranchNames = branches.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _allLocalBranches = branches.Where(b => !b.IsRemote).ToList();
            _allRemoteBranches = branches.Where(b => b.IsRemote).ToList();

            // A "new" (remote) branch that now has a matching local branch has been checked out -
            // whether via the New Branches panel's own Checkout button (which also removes it
            // immediately) or by any other means, e.g. an external tool that the .git/HEAD watcher
            // then picked up and triggered this reload for. Purging it here means it disappears
            // regardless of how the checkout happened, instead of only on that one code path.
            if (NewBranches.Count > 0)
            {
                var localNames = _allLocalBranches.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                for (var i = NewBranches.Count - 1; i >= 0; i--)
                {
                    // Only remote entries need this translation - a newly-discovered *local*
                    // branch always already satisfies "has a matching local branch" (itself),
                    // which would purge it the instant it's added, before it can ever be seen.
                    var nb = NewBranches[i];
                    if (!nb.IsRemote) continue;

                    var localEquivalent = nb.RemoteName != null && nb.Name.StartsWith(nb.RemoteName + "/")
                        ? nb.Name.Substring(nb.RemoteName.Length + 1)
                        : nb.Name;
                    if (localNames.Contains(localEquivalent))
                    {
                        NewBranches.RemoveAt(i);
                    }
                }
                OnPropertyChanged(nameof(HasNewBranches));
            }

            // Branches are freshly-loaded objects each refresh, so pinned state (which lives
            // in settings, not git) has to be re-stamped onto them every time.
            _pinnedBranchNames = _loadPinnedBranches(repoPath);
            foreach (var b in _allLocalBranches) b.IsPinned = _pinnedBranchNames.Contains(b.Name);
            foreach (var b in _allRemoteBranches) b.IsPinned = _pinnedBranchNames.Contains(b.Name);

            ApplyFilter();
        }

        [RelayCommand]
        public void ClearNewBranches()
        {
            NewBranches.Clear();
            OnPropertyChanged(nameof(HasNewBranches));
        }

        [RelayCommand]
        public async Task FetchAllAsync()
        {
            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            IsFetching = true;
            FetchStatusMessage = "Fetching all remotes...";
            _setBusy(true, "Fetching all remotes...");

            try
            {
                var (success, output) = await _gitService.FetchAllAsync(repoPath);
                if (success)
                {
                    _setBusy(true, "Refreshing branches...");
                    await LoadBranchesAsync();
                    FetchStatusMessage = "Fetch complete.";
                    await _onBranchChanged();
                }
                else
                {
                    FetchStatusMessage = $"Fetch failed: {output}";
                }
            }
            catch (Exception ex)
            {
                FetchStatusMessage = $"Fetch error: {ex.Message}";
            }
            finally
            {
                IsFetching = false;
                _setBusy(false, null);
            }
        }

        // Public so tests (and the deliberate one-shot callers below, like OnGroupBranchesInto
        // FoldersChanged) can force an immediate, synchronous filter pass rather than relying on
        // the debounce timer used for keystroke-driven filtering (see OnFilterTextChanged).
        public void ApplyFilter()
        {
            ApplyLocalFilter();
            ApplyRemoteFilter();
        }

        // Split so a local-only change (e.g. pinning a local branch) doesn't also re-filter,
        // re-sort and rebuild the entire remote side - that redundant work was the source of
        // a visible UI stall on repos with hundreds of branches.
        private void ApplyLocalFilter()
        {
            FilteredLocalBranches.Clear();

            var query = FilterText?.Trim() ?? string.Empty;
            var localMatches = string.IsNullOrEmpty(query)
                ? _allLocalBranches
                : _allLocalBranches.Where(b => b.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

            // Pinned branches float to the top; OrderByDescending is a stable sort, so relative
            // order within "pinned" and within "unpinned" is otherwise unchanged.
            var localList = localMatches.OrderByDescending(b => b.IsPinned).ToList();
            foreach (var b in localList)
            {
                FilteredLocalBranches.Add(b);
            }

            // Re-group into folders only here (branch data or search text actually changed), and
            // only when folder view is actually showing - building the tree is wasted work while
            // the user is looking at the flat list (a fast, hot path for e.g. pinning).
            if (GroupBranchesIntoFolders)
            {
                RebuildBranchTree(LocalBranchTree, localList);
            }
        }

        private void ApplyRemoteFilter()
        {
            FilteredRemoteBranches.Clear();

            var query = FilterText?.Trim() ?? string.Empty;
            var remoteMatches = string.IsNullOrEmpty(query)
                ? _allRemoteBranches
                : _allRemoteBranches.Where(b => b.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

            var remoteList = remoteMatches.OrderByDescending(b => b.IsPinned).ToList();
            foreach (var b in remoteList)
            {
                FilteredRemoteBranches.Add(b);
            }

            if (GroupBranchesIntoFolders)
            {
                RebuildBranchTree(RemoteBranchTree, remoteList);
            }
        }

        /// <summary>Rebuilds a real hierarchical tree (folders own their branches via
        /// BranchTreeItem.Children, consumed by a WPF TreeView's HierarchicalDataTemplate)
        /// instead of a hand-flattened, hand-indented list. Expand/collapse is handled entirely
        /// by the TreeView itself (TreeViewItem.IsExpanded two-way bound to BranchTreeItem.
        /// IsExpanded) - this only needs to carry each folder's expand state across the rebuild,
        /// since the old tree's node instances are discarded.</summary>
        private static void RebuildBranchTree(ObservableCollection<BranchTreeItem> target, IReadOnlyList<GitBranch> branches)
        {
            var expandState = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            SnapshotExpandState(target, expandState);

            // Pinned branches float to the very top as plain leaves - no synthetic "Pinned"
            // folder, and no duplicate left behind in their normal folder position (this is a
            // move, not a shortcut, matching how pinning already behaves in the flat view).
            var pinned = branches.Where(b => b.IsPinned).ToList();
            var unpinned = branches.Where(b => !b.IsPinned).ToList();
            var root = BuildBranchFolderTree(unpinned);

            // Root-level rebuild only - typically a handful of top folders/branches, not the
            // full (possibly deep) tree, so Clear()+ReAdd() here is cheap and doesn't touch any
            // already-rendered nested TreeViewItems below the root.
            target.Clear();
            foreach (var branch in pinned)
            {
                // Full DisplayName (not just the leaf segment) since there's no folder here to
                // give it context - "bugfix/BB-300-baz", not just "BB-300-baz".
                target.Add(new BranchTreeItem { IsFolder = false, DisplayName = branch.DisplayName, Branch = branch });
            }
            foreach (var folderName in root.ChildFolderOrder)
            {
                target.Add(BuildFolderItem(root.ChildFolders[folderName], folderName, parentPath: "", expandState));
            }
            foreach (var (leafName, branch) in root.Leaves)
            {
                target.Add(new BranchTreeItem { IsFolder = false, DisplayName = leafName, Branch = branch });
            }
        }

        private static void SnapshotExpandState(IEnumerable<BranchTreeItem> items, Dictionary<string, bool> map)
        {
            foreach (var item in items)
            {
                if (!item.IsFolder) continue;
                map[item.FolderPath] = item.IsExpanded;
                SnapshotExpandState(item.Children, map);
            }
        }

        private static BranchTreeItem BuildFolderItem(BranchFolderNode node, string folderName, string parentPath, IReadOnlyDictionary<string, bool> expandState)
        {
            var folderPath = parentPath.Length == 0 ? folderName : $"{parentPath}/{folderName}";
            var item = new BranchTreeItem
            {
                IsFolder = true,
                DisplayName = folderName,
                FolderPath = folderPath,
                IsExpanded = !expandState.TryGetValue(folderPath, out var wasExpanded) || wasExpanded
            };
            foreach (var childFolderName in node.ChildFolderOrder)
            {
                item.Children.Add(BuildFolderItem(node.ChildFolders[childFolderName], childFolderName, folderPath, expandState));
            }
            foreach (var (leafName, branch) in node.Leaves)
            {
                item.Children.Add(new BranchTreeItem { IsFolder = false, DisplayName = leafName, Branch = branch });
            }
            return item;
        }

        /// <summary>Groups branches into folders by '/' in their display name (so a remote's
        /// "origin/" prefix doesn't itself become a folder).</summary>
        private static BranchFolderNode BuildBranchFolderTree(IReadOnlyList<GitBranch> branches)
        {
            var root = new BranchFolderNode();

            foreach (var branch in branches)
            {
                var segments = branch.DisplayName.Split('/');
                var node = root;
                for (var i = 0; i < segments.Length - 1; i++)
                {
                    node = node.GetOrAddChildFolder(segments[i]);
                }
                node.Leaves.Add((segments[^1], branch));
            }
            return root;
        }

        private class BranchFolderNode
        {
            public Dictionary<string, BranchFolderNode> ChildFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<string> ChildFolderOrder { get; } = new();
            public List<(string LeafName, GitBranch Branch)> Leaves { get; } = new();

            public BranchFolderNode GetOrAddChildFolder(string name)
            {
                if (!ChildFolders.TryGetValue(name, out var node))
                {
                    node = new BranchFolderNode();
                    ChildFolders[name] = node;
                    ChildFolderOrder.Add(name);
                }
                return node;
            }
        }

        [RelayCommand]
        public async Task CheckoutBranchAsync(GitBranch? branch)
        {
            branch ??= SelectedBranch;
            if (branch == null) return;

            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            _setBusy(true, $"Checking out '{branch.Name}'...");
            try
            {
                var (success, output) = await _gitService.CheckoutBranchAsync(repoPath, branch.Name);
                if (success)
                {
                    // Remove from new branches list once checked out
                    var existingInNew = NewBranches.FirstOrDefault(b => b.Name.Equals(branch.Name, StringComparison.OrdinalIgnoreCase));
                    if (existingInNew != null)
                    {
                        NewBranches.Remove(existingInNew);
                        OnPropertyChanged(nameof(HasNewBranches));
                    }

                    await _onBranchChanged();
                }
                else
                {
                    MessageBox.Show($"Checkout failed:\n{output}", "Checkout Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                _setBusy(false, null);
            }
        }

        [RelayCommand]
        public void TogglePinBranch(GitBranch? branch)
        {
            branch ??= SelectedBranch;
            if (branch == null) return;

            branch.IsPinned = !branch.IsPinned;
            if (branch.IsPinned) _pinnedBranchNames.Add(branch.Name);
            else _pinnedBranchNames.Remove(branch.Name);

            _savePinnedBranches(_getRepoPath(), _pinnedBranchNames);

            if (GroupBranchesIntoFolders)
            {
                // Folder view needs the synthetic "Pinned" folder rebuilt - only for the side
                // the branch actually belongs to.
                if (branch.IsRemote) ApplyRemoteFilter();
                else ApplyLocalFilter();
                return;
            }

            // Flat view: reposition the single row with ObservableCollection.Move instead of
            // Clear()+re-Add() of the whole list. Move raises one "Move" notification that WPF
            // repositions in place; Clear()+re-Add() raises a Reset that forces every visible
            // (virtualized) row - each with its own multi-item ContextMenu - to be torn down and
            // regenerated. That full-list regeneration was the actual source of the pin lag even
            // after splitting local/remote filtering, since either side can still be large.
            var filtered = branch.IsRemote ? FilteredRemoteBranches : FilteredLocalBranches;
            var oldIndex = filtered.IndexOf(branch);
            if (oldIndex < 0) return;

            var pinnedCount = filtered.Count(b => b.IsPinned);
            var newIndex = branch.IsPinned ? 0 : pinnedCount;
            newIndex = Math.Min(newIndex, filtered.Count - 1);
            if (newIndex != oldIndex)
            {
                filtered.Move(oldIndex, newIndex);
            }
        }

        [RelayCommand]
        public async Task DeleteBranchAsync(GitBranch? branch)
        {
            branch ??= SelectedBranch;
            if (branch == null || branch.IsCurrent || branch.IsRemote) return;

            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            _setBusy(true, $"Deleting branch '{branch.Name}'...");
            try
            {
                var (success, _) = await _gitService.DeleteBranchAsync(repoPath, branch.Name, force: false);
                if (success)
                {
                    await LoadBranchesAsync();
                }
            }
            finally
            {
                _setBusy(false, null);
            }
        }
    }
}
