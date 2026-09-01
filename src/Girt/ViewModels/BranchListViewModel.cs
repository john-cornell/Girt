using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
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
        private readonly HashSet<string> _collapsedLocalFolders = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _collapsedRemoteFolders = new(StringComparer.OrdinalIgnoreCase);

        // The branch->folder grouping itself only changes when branches load or the search
        // text changes; a folder collapse/expand click just re-flattens these cached trees
        // instead of re-grouping every branch from scratch (that was the source of the lag).
        private BranchFolderNode? _localTreeRoot;
        private BranchFolderNode? _remoteTreeRoot;

        public List<GitBranch> AllBranches { get; private set; } = new();
        public ObservableCollection<GitBranch> FilteredLocalBranches { get; } = new();
        public ObservableCollection<GitBranch> FilteredRemoteBranches { get; } = new();
        public ObservableCollection<BranchTreeItem> LocalBranchTree { get; } = new();
        public ObservableCollection<BranchTreeItem> RemoteBranchTree { get; } = new();
        public ObservableCollection<GitBranch> NewBranches { get; } = new();

        public bool HasNewBranches => NewBranches.Count > 0;

        public BranchListViewModel(
            IGitService gitService,
            Func<string> getRepoPath,
            Func<Task> onBranchChanged,
            bool initialGroupBranchesIntoFolders,
            Action<bool> saveGroupBranchesIntoFolders)
        {
            _gitService = gitService;
            _getRepoPath = getRepoPath;
            _onBranchChanged = onBranchChanged;
            _saveGroupBranchesIntoFolders = saveGroupBranchesIntoFolders;
            _groupBranchesIntoFolders = initialGroupBranchesIntoFolders;
        }

        partial void OnFilterTextChanged(string value)
        {
            ApplyFilter();
        }

        [RelayCommand]
        public void ToggleGroupBranchesIntoFolders()
        {
            GroupBranchesIntoFolders = !GroupBranchesIntoFolders;
        }

        [RelayCommand]
        public void ToggleLocalBranchFolder(BranchTreeItem? item)
        {
            if (item == null || !item.IsFolder) return;
            if (!_collapsedLocalFolders.Add(item.FolderPath)) _collapsedLocalFolders.Remove(item.FolderPath);
            RefreshLocalBranchTreeDisplay();
        }

        [RelayCommand]
        public void ToggleRemoteBranchFolder(BranchTreeItem? item)
        {
            if (item == null || !item.IsFolder) return;
            if (!_collapsedRemoteFolders.Add(item.FolderPath)) _collapsedRemoteFolders.Remove(item.FolderPath);
            RefreshRemoteBranchTreeDisplay();
        }

        partial void OnGroupBranchesIntoFoldersChanged(bool value)
        {
            _saveGroupBranchesIntoFolders(value);
            RefreshLocalBranchTreeDisplay();
            RefreshRemoteBranchTreeDisplay();
        }

        public async Task LoadBranchesAsync()
        {
            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            var branches = await _gitService.GetBranchesAsync(repoPath);
            AllBranches = branches.ToList();

            // Detect newly created/discovered branches since last snapshot
            if (_knownBranchNames.Count > 0)
            {
                var newDiscovered = branches.Where(b => !_knownBranchNames.Contains(b.Name)).ToList();
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

            try
            {
                var (success, output) = await _gitService.FetchAllAsync(repoPath);
                if (success)
                {
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
            }
        }

        private void ApplyFilter()
        {
            FilteredLocalBranches.Clear();
            FilteredRemoteBranches.Clear();

            var query = FilterText?.Trim() ?? string.Empty;

            var localMatches = string.IsNullOrEmpty(query)
                ? _allLocalBranches
                : _allLocalBranches.Where(b => b.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

            var localList = localMatches.ToList();
            foreach (var b in localList)
            {
                FilteredLocalBranches.Add(b);
            }

            var remoteMatches = string.IsNullOrEmpty(query)
                ? _allRemoteBranches
                : _allRemoteBranches.Where(b => b.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

            var remoteList = remoteMatches.ToList();
            foreach (var b in remoteList)
            {
                FilteredRemoteBranches.Add(b);
            }

            // Re-group into folders only here (branch data or search text actually changed).
            // Folder collapse/expand clicks re-flatten these cached trees instead of redoing this.
            _localTreeRoot = BuildBranchFolderTree(localList);
            _remoteTreeRoot = BuildBranchFolderTree(remoteList);

            RefreshLocalBranchTreeDisplay();
            RefreshRemoteBranchTreeDisplay();
        }

        private void RefreshLocalBranchTreeDisplay()
        {
            LocalBranchTree.Clear();
            if (GroupBranchesIntoFolders && _localTreeRoot != null)
            {
                foreach (var item in FlattenBranchTree(_localTreeRoot, _collapsedLocalFolders)) LocalBranchTree.Add(item);
            }
        }

        private void RefreshRemoteBranchTreeDisplay()
        {
            RemoteBranchTree.Clear();
            if (GroupBranchesIntoFolders && _remoteTreeRoot != null)
            {
                foreach (var item in FlattenBranchTree(_remoteTreeRoot, _collapsedRemoteFolders)) RemoteBranchTree.Add(item);
            }
        }

        /// <summary>Groups branches into folders by '/' in their display name (so a remote's
        /// "origin/" prefix doesn't itself become a folder). Cheap to call often since it's just
        /// dictionary lookups; the expensive part (this) is cached and only rebuilt when the
        /// underlying branch list actually changes - see FlattenBranchTree for display refreshes.</summary>
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

        /// <summary>Produces a flat, indented list from a cached tree: folders before their
        /// branches, each in first-seen order. A folder whose full path is in
        /// <paramref name="collapsedFolders"/> is shown but its contents are omitted.</summary>
        private static List<BranchTreeItem> FlattenBranchTree(BranchFolderNode root, HashSet<string> collapsedFolders)
        {
            var result = new List<BranchTreeItem>();
            AppendBranchFolder(root, depth: 0, parentPath: "", result, collapsedFolders);
            return result;
        }

        private static void AppendBranchFolder(BranchFolderNode node, int depth, string parentPath, List<BranchTreeItem> result, HashSet<string> collapsedFolders)
        {
            foreach (var folderName in node.ChildFolderOrder)
            {
                var folderPath = parentPath.Length == 0 ? folderName : $"{parentPath}/{folderName}";
                var isCollapsed = collapsedFolders.Contains(folderPath);
                result.Add(new BranchTreeItem { IsFolder = true, DisplayName = folderName, Depth = depth, FolderPath = folderPath, IsCollapsed = isCollapsed });

                if (!isCollapsed)
                {
                    AppendBranchFolder(node.ChildFolders[folderName], depth + 1, folderPath, result, collapsedFolders);
                }
            }

            foreach (var (leafName, branch) in node.Leaves)
            {
                result.Add(new BranchTreeItem { IsFolder = false, DisplayName = leafName, Depth = depth, Branch = branch });
            }
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
        }

        [RelayCommand]
        public async Task DeleteBranchAsync(GitBranch? branch)
        {
            branch ??= SelectedBranch;
            if (branch == null || branch.IsCurrent || branch.IsRemote) return;

            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            var (success, _) = await _gitService.DeleteBranchAsync(repoPath, branch.Name, force: false);
            if (success)
            {
                await LoadBranchesAsync();
            }
        }
    }
}
