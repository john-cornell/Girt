using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Girt.Models;
using Girt.Services;
using Microsoft.Win32;

namespace Girt.ViewModels
{
    public enum ActiveViewMode
    {
        History,
        WorkingChanges
    }

    public partial class MainViewModel : ObservableObject
    {
        private readonly IGitService _gitService;
        private readonly RecentRepositoriesService _recentReposService;
        private readonly ThemeService _themeService;

        public const string AppVersion = "v0.1.2";

        public string WindowTitle => string.IsNullOrEmpty(RepositoryPath)
            ? $"Girt {AppVersion} - Git Client"
            : $"Girt {AppVersion} - {RepositoryName} [{CurrentBranch}]";

        [ObservableProperty]
        private string _repositoryPath = string.Empty;

        [ObservableProperty]
        private string _repositoryName = "No Repository Opened";

        [ObservableProperty]
        private string _currentBranch = "-";

        partial void OnRepositoryPathChanged(string value) => OnPropertyChanged(nameof(WindowTitle));
        partial void OnRepositoryNameChanged(string value) => OnPropertyChanged(nameof(WindowTitle));
        partial void OnCurrentBranchChanged(string value) => OnPropertyChanged(nameof(WindowTitle));

        [ObservableProperty]
        private GitRepoStatus _repoStatus = new();

        [ObservableProperty]
        private ActiveViewMode _currentView = ActiveViewMode.History;

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private string _statusMessage = "Ready";

        // Create Branch Dialog State
        [ObservableProperty]
        private bool _isCreateBranchDialogOpen;

        [ObservableProperty]
        private string _newBranchName = string.Empty;

        // Reset HEAD Dialog State
        [ObservableProperty]
        private bool _isResetDialogOpen;

        [ObservableProperty]
        private string _resetTargetRef = "HEAD~1";

        [ObservableProperty]
        private string _resetTargetDescription = "Undo last commit (HEAD~1)";

        [ObservableProperty]
        private GitResetMode _resetMode = GitResetMode.Soft;

        public BranchListViewModel BranchList { get; }
        public CommitHistoryViewModel CommitHistory { get; }
        public CommitDetailViewModel CommitDetail { get; }
        public WorkingChangesViewModel WorkingChanges { get; }

        public ObservableCollection<string> RecentRepositories { get; } = new();

        public bool IsHistoryView => CurrentView == ActiveViewMode.History;
        public bool IsWorkingChangesView => CurrentView == ActiveViewMode.WorkingChanges;

        public MainViewModel(IGitService gitService, RecentRepositoriesService recentReposService, ThemeService themeService)
        {
            _gitService = gitService;
            _recentReposService = recentReposService;
            _themeService = themeService;

            BranchList = new BranchListViewModel(_gitService, () => RepositoryPath, OnBranchChangedAsync);
            CommitHistory = new CommitHistoryViewModel(_gitService, () => RepositoryPath, OnCommitSelected);
            CommitDetail = new CommitDetailViewModel(_gitService, () => RepositoryPath);
            WorkingChanges = new WorkingChangesViewModel(_gitService, () => RepositoryPath, OnWorkingChangesUpdatedAsync);

            LoadRecentRepositories();
        }

        partial void OnCurrentViewChanged(ActiveViewMode value)
        {
            OnPropertyChanged(nameof(IsHistoryView));
            OnPropertyChanged(nameof(IsWorkingChangesView));
        }

        private void LoadRecentRepositories()
        {
            RecentRepositories.Clear();
            var list = _recentReposService.LoadRecentRepositories();
            foreach (var repo in list)
            {
                RecentRepositories.Add(repo);
            }
        }

        public async Task OpenRepositoryAsync(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath)) return;

            IsLoading = true;
            StatusMessage = "Detecting Git repository...";

            try
            {
                var root = await _gitService.GetRepositoryRootAsync(directoryPath);
                if (string.IsNullOrEmpty(root))
                {
                    StatusMessage = "Selected folder is not a valid Git repository.";
                    MessageBox.Show("The selected folder is not inside a Git repository.", "Not a Git Repository", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                RepositoryPath = root;
                RepositoryName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                _recentReposService.AddRepository(root);
                LoadRecentRepositories();

                await RefreshRepositoryAsync();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        public async Task RefreshRepositoryAsync()
        {
            if (string.IsNullOrEmpty(RepositoryPath)) return;

            IsLoading = true;
            StatusMessage = "Refreshing repository...";

            try
            {
                CurrentBranch = await _gitService.GetCurrentBranchAsync(RepositoryPath) ?? "-";
                RepoStatus = await _gitService.GetRepoStatusAsync(RepositoryPath);
                await BranchList.LoadBranchesAsync();
                await CommitHistory.LoadCommitsAsync();
                await WorkingChanges.LoadChangesAsync();
                StatusMessage = $"Loaded {CommitHistory.FilteredCommits.Count} commits, {RepoStatus.UncommittedCount} uncommitted changes.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error refreshing: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        public void ShowHistoryView()
        {
            CurrentView = ActiveViewMode.History;
        }

        [RelayCommand]
        public async Task ShowWorkingChangesViewAsync()
        {
            CurrentView = ActiveViewMode.WorkingChanges;
            await WorkingChanges.LoadChangesAsync();
        }

        [RelayCommand]
        public async Task PushAsync()
        {
            if (string.IsNullOrEmpty(RepositoryPath)) return;

            IsLoading = true;
            StatusMessage = "Pushing commits to remote...";

            try
            {
                var (success, output) = await _gitService.PushAsync(RepositoryPath);
                if (success)
                {
                    await RefreshRepositoryAsync();
                    StatusMessage = "Push successful!";
                }
                else
                {
                    MessageBox.Show($"Push failed:\n{output}", "Git Push Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        public async Task PullAsync()
        {
            if (string.IsNullOrEmpty(RepositoryPath)) return;

            IsLoading = true;
            StatusMessage = "Pulling commits from remote...";

            try
            {
                var (success, output) = await _gitService.PullAsync(RepositoryPath);
                if (success)
                {
                    await RefreshRepositoryAsync();
                    StatusMessage = "Pull successful!";
                }
                else
                {
                    MessageBox.Show($"Pull failed:\n{output}", "Git Pull Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        public async Task OpenFolderPickerAsync()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select Git Repository Directory"
            };

            if (dialog.ShowDialog() == true)
            {
                await OpenRepositoryAsync(dialog.FolderName);
            }
        }

        [RelayCommand]
        public async Task OpenRecentRepositoryAsync(string path)
        {
            if (!string.IsNullOrEmpty(path))
            {
                await OpenRepositoryAsync(path);
            }
        }

        [RelayCommand]
        public void ToggleTheme()
        {
            _themeService.ToggleTheme();
        }

        // Branch Creation Modal
        [RelayCommand]
        public void ShowCreateBranchDialog()
        {
            NewBranchName = string.Empty;
            IsCreateBranchDialogOpen = true;
        }

        [RelayCommand]
        public void CancelCreateBranchDialog()
        {
            IsCreateBranchDialogOpen = false;
        }

        [RelayCommand]
        public async Task ConfirmCreateBranchAsync()
        {
            var branchName = NewBranchName?.Trim();
            if (string.IsNullOrEmpty(branchName)) return;

            IsCreateBranchDialogOpen = false;
            IsLoading = true;
            StatusMessage = $"Creating branch '{branchName}'...";

            try
            {
                var (success, output) = await _gitService.CreateBranchAsync(RepositoryPath, branchName);
                if (success)
                {
                    await RefreshRepositoryAsync();
                }
                else
                {
                    MessageBox.Show($"Failed to create branch:\n{output}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                IsLoading = false;
            }
        }

        // Reset HEAD Modal
        [RelayCommand]
        public void ShowResetDialog(object? parameter = null)
        {
            if (parameter is GitCommit commit)
            {
                ResetTargetRef = commit.Hash;
                ResetTargetDescription = $"Commit {commit.ShortHash}: {commit.Subject}";
            }
            else
            {
                ResetTargetRef = "HEAD~1";
                ResetTargetDescription = "Undo last commit (HEAD~1)";
            }

            ResetMode = GitResetMode.Soft;
            IsResetDialogOpen = true;
        }

        [RelayCommand]
        public void SetResetMode(string modeStr)
        {
            if (Enum.TryParse<GitResetMode>(modeStr, true, out var mode))
            {
                ResetMode = mode;
            }
        }

        [RelayCommand]
        public void CancelResetDialog()
        {
            IsResetDialogOpen = false;
        }

        [RelayCommand]
        public async Task ConfirmResetAsync()
        {
            if (string.IsNullOrEmpty(RepositoryPath)) return;

            var target = ResetTargetRef?.Trim();
            if (string.IsNullOrEmpty(target)) return;

            IsResetDialogOpen = false;
            IsLoading = true;
            StatusMessage = $"Resetting HEAD ({ResetMode}) to {target}...";

            try
            {
                var (success, output) = await _gitService.ResetHeadAsync(RepositoryPath, target, ResetMode);
                if (success)
                {
                    await RefreshRepositoryAsync();
                }
                else
                {
                    MessageBox.Show($"Failed to reset HEAD:\n{output}", "Reset Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task OnBranchChangedAsync()
        {
            await RefreshRepositoryAsync();
        }

        private async Task OnWorkingChangesUpdatedAsync()
        {
            RepoStatus = await _gitService.GetRepoStatusAsync(RepositoryPath);
            await CommitHistory.LoadCommitsAsync();
        }

        private void OnCommitSelected(GitCommit? commit)
        {
            _ = CommitDetail.SetCommitAsync(commit);
        }
    }
}
