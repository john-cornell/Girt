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

        private List<GitBranch> _allLocalBranches = new();
        private List<GitBranch> _allRemoteBranches = new();
        private HashSet<string> _knownBranchNames = new(StringComparer.OrdinalIgnoreCase);

        public List<GitBranch> AllBranches { get; private set; } = new();
        public ObservableCollection<GitBranch> FilteredLocalBranches { get; } = new();
        public ObservableCollection<GitBranch> FilteredRemoteBranches { get; } = new();
        public ObservableCollection<GitBranch> NewBranches { get; } = new();

        public bool HasNewBranches => NewBranches.Count > 0;

        public BranchListViewModel(IGitService gitService, Func<string> getRepoPath, Func<Task> onBranchChanged)
        {
            _gitService = gitService;
            _getRepoPath = getRepoPath;
            _onBranchChanged = onBranchChanged;
        }

        partial void OnFilterTextChanged(string value)
        {
            ApplyFilter();
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

            foreach (var b in localMatches)
            {
                FilteredLocalBranches.Add(b);
            }

            var remoteMatches = string.IsNullOrEmpty(query)
                ? _allRemoteBranches
                : _allRemoteBranches.Where(b => b.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

            foreach (var b in remoteMatches)
            {
                FilteredRemoteBranches.Add(b);
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
