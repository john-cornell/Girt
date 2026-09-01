using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
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

        public const string AppVersion = "0.4.1";

        [ObservableProperty]
        private string _repositoryPath = string.Empty;

        [ObservableProperty]
        private string _repositoryName = "No Repository Opened";

        [ObservableProperty]
        private string _currentBranch = "-";

        [ObservableProperty]
        private GitRepoStatus _repoStatus = new();

        [ObservableProperty]
        private ActiveViewMode _currentView = ActiveViewMode.History;

        [ObservableProperty]
        private string _windowTitle = $"Girt v{AppVersion} - Modern Git Client";

        partial void OnRepositoryNameChanged(string value) => UpdateWindowTitle();
        partial void OnCurrentBranchChanged(string value) => UpdateWindowTitle();
        partial void OnRepositoryPathChanged(string value) => UpdateWindowTitle();

        private void UpdateWindowTitle()
        {
            WindowTitle = string.IsNullOrEmpty(RepositoryPath)
                ? $"Girt v{AppVersion} - Modern Git Client"
                : $"Girt v{AppVersion} - {RepositoryName} [{CurrentBranch}] ({RepositoryPath})";
        }

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

            BranchList = new BranchListViewModel(
                _gitService,
                () => RepositoryPath,
                OnBranchChangedAsync,
                _themeService.LoadGroupBranchesIntoFolders(),
                _themeService.SaveGroupBranchesIntoFolders);
            CommitHistory = new CommitHistoryViewModel(_gitService, () => RepositoryPath, OnCommitSelected);
            CommitDetail = new CommitDetailViewModel(_gitService, () => RepositoryPath);
            WorkingChanges = new WorkingChangesViewModel(
                _gitService,
                () => RepositoryPath,
                OnWorkingChangesUpdatedAsync,
                _themeService.LoadPushAfterCommit(),
                _themeService.SavePushAfterCommit);

            // Hook branch selection change to update association view immediately
            BranchList.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(BranchList.SelectedBranch) && BranchList.SelectedBranch != null)
                {
                    CommitHistory.SetBranches(BranchList.AllBranches, BranchList.SelectedBranch.Name);
                }
            };

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
                var branchTask = _gitService.GetCurrentBranchAsync(RepositoryPath);
                var statusTask = _gitService.GetRepoStatusAsync(RepositoryPath);
                var branchListTask = BranchList.LoadBranchesAsync();
                var workingChangesTask = WorkingChanges.LoadChangesAsync();

                await Task.WhenAll(branchTask, statusTask, branchListTask, workingChangesTask);

                CurrentBranch = await branchTask ?? "-";
                RepoStatus = await statusTask;
                CommitHistory.SetBranches(BranchList.AllBranches, CurrentBranch);
                await CommitHistory.LoadCommitsAsync();

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
        public async Task FetchAllAsync()
        {
            if (string.IsNullOrEmpty(RepositoryPath)) return;

            IsLoading = true;
            StatusMessage = "Fetching all remotes...";

            try
            {
                await BranchList.FetchAllAsync();
                await RefreshRepositoryAsync();
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

        // ================= CLIPBOARD COPY COMMANDS =================

        // The Windows clipboard is a single shared, cross-process resource: any other app
        // (clipboard managers, RDP, antivirus scanners) can hold it open for a few
        // milliseconds, which makes OpenClipboard - and so Clipboard.SetText - fail
        // transiently with CLIPBRD_E_CANT_OPEN (0x800401D0). Retry briefly before giving up.
        private static bool TrySetClipboardText(string text)
        {
            const int maxAttempts = 10;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return true;
                }
                catch (COMException) when (attempt < maxAttempts)
                {
                    Thread.Sleep(50);
                }
            }

            return false;
        }

        [RelayCommand]
        public void CopyCommitSha(object? parameter)
        {
            var hash = (parameter as GitCommit)?.Hash ?? CommitHistory.SelectedCommit?.Hash;
            if (!string.IsNullOrEmpty(hash))
            {
                StatusMessage = TrySetClipboardText(hash)
                    ? $"Copied SHA {hash[..Math.Min(7, hash.Length)]} to clipboard."
                    : "Could not copy to clipboard - it's in use by another app. Try again.";
            }
        }

        [RelayCommand]
        public void CopyCommitShortSha(object? parameter)
        {
            var shortHash = (parameter as GitCommit)?.ShortHash ?? CommitHistory.SelectedCommit?.ShortHash;
            if (!string.IsNullOrEmpty(shortHash))
            {
                StatusMessage = TrySetClipboardText(shortHash)
                    ? $"Copied SHA {shortHash} to clipboard."
                    : "Could not copy to clipboard - it's in use by another app. Try again.";
            }
        }

        [RelayCommand]
        public void CopyCommitMessage(object? parameter)
        {
            var msg = (parameter as GitCommit)?.Subject ?? CommitHistory.SelectedCommit?.Subject;
            if (!string.IsNullOrEmpty(msg))
            {
                StatusMessage = TrySetClipboardText(msg)
                    ? "Copied commit message to clipboard."
                    : "Could not copy to clipboard - it's in use by another app. Try again.";
            }
        }

        [RelayCommand]
        public void CopyCommitAuthor(object? parameter)
        {
            var commit = (parameter as GitCommit) ?? CommitHistory.SelectedCommit;
            if (commit != null)
            {
                var text = string.IsNullOrEmpty(commit.AuthorEmail)
                    ? commit.AuthorName
                    : $"{commit.AuthorName} <{commit.AuthorEmail}>";
                StatusMessage = TrySetClipboardText(text)
                    ? $"Copied author '{text}' to clipboard."
                    : "Could not copy to clipboard - it's in use by another app. Try again.";
            }
        }

        [RelayCommand]
        public void CopyBranchName(object? parameter)
        {
            var name = (parameter as GitBranch)?.Name ?? (parameter as string) ?? CurrentBranch;
            if (!string.IsNullOrEmpty(name))
            {
                StatusMessage = TrySetClipboardText(name)
                    ? $"Copied branch name '{name}' to clipboard."
                    : "Could not copy to clipboard - it's in use by another app. Try again.";
            }
        }

        // ================= REVERT & CHERRY-PICK =================
        [RelayCommand]
        public async Task RevertCommitAsync(object? parameter)
        {
            var commit = (parameter as GitCommit) ?? CommitHistory.SelectedCommit;
            if (commit == null || string.IsNullOrEmpty(RepositoryPath)) return;

            var result = MessageBox.Show(
                $"Are you sure you want to revert commit {commit.ShortHash}?\n\nSubject: {commit.Subject}\nAuthor: {commit.AuthorName}\n\nThis will create a new revert commit on branch '{CurrentBranch}'.",
                "Revert Commit",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            IsLoading = true;
            StatusMessage = $"Reverting commit {commit.ShortHash}...";

            try
            {
                var (success, output) = await _gitService.RevertCommitAsync(RepositoryPath, commit.Hash);
                if (success)
                {
                    await RefreshRepositoryAsync();
                    StatusMessage = $"Reverted commit {commit.ShortHash} successfully.";
                }
                else
                {
                    MessageBox.Show($"Failed to revert commit:\n{output}", "Revert Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        public async Task CherryPickCommitAsync(object? parameter)
        {
            var commit = (parameter as GitCommit) ?? CommitHistory.SelectedCommit;
            if (commit == null || string.IsNullOrEmpty(RepositoryPath)) return;

            var result = MessageBox.Show(
                $"Are you sure you want to cherry-pick commit {commit.ShortHash} onto current branch '{CurrentBranch}'?\n\nSubject: {commit.Subject}\nAuthor: {commit.AuthorName}",
                "Cherry-Pick Commit",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            IsLoading = true;
            StatusMessage = $"Cherry-picking commit {commit.ShortHash}...";

            try
            {
                var (success, output) = await _gitService.CherryPickCommitAsync(RepositoryPath, commit.Hash);
                if (success)
                {
                    await RefreshRepositoryAsync();
                    StatusMessage = $"Cherry-picked commit {commit.ShortHash} successfully.";
                }
                else
                {
                    MessageBox.Show($"Failed to cherry-pick commit:\n{output}", "Cherry-Pick Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                IsLoading = false;
            }
        }

        // ================= MERGE & REBASE =================
        [RelayCommand]
        public async Task MergeIntoCurrentBranchAsync(object? parameter)
        {
            await ExecuteMergeAsync(parameter, squash: false, noFf: false);
        }

        [RelayCommand]
        public async Task MergeSquashIntoCurrentBranchAsync(object? parameter)
        {
            await ExecuteMergeAsync(parameter, squash: true, noFf: false);
        }

        [RelayCommand]
        public async Task MergeNoFfIntoCurrentBranchAsync(object? parameter)
        {
            await ExecuteMergeAsync(parameter, squash: false, noFf: true);
        }

        private async Task ExecuteMergeAsync(object? parameter, bool squash, bool noFf)
        {
            var targetRef = parameter switch
            {
                GitCommit c => c.Hash,
                GitBranch b => b.Name,
                string s => s,
                _ => CommitHistory.SelectedCommit?.Hash
            };

            if (string.IsNullOrEmpty(targetRef) || string.IsNullOrEmpty(RepositoryPath)) return;

            var targetDisplay = parameter switch
            {
                GitCommit c => $"commit {c.ShortHash} ({c.Subject})",
                GitBranch b => $"branch '{b.DisplayName}'",
                _ => targetRef
            };

            var modeLabel = squash ? " (Squash)" : noFf ? " (No Fast-Forward)" : "";
            var result = MessageBox.Show(
                $"Merge {targetDisplay} into current branch '{CurrentBranch}'{modeLabel}?",
                "Confirm Merge",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            IsLoading = true;
            StatusMessage = $"Merging {targetRef} into {CurrentBranch}...";

            try
            {
                var (success, output) = await _gitService.MergeAsync(RepositoryPath, targetRef, squash, noFf);
                if (success)
                {
                    await RefreshRepositoryAsync();
                    StatusMessage = $"Merged successfully.";
                }
                else
                {
                    MessageBox.Show($"Merge encountered conflicts or failed:\n{output}", "Merge Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    await RefreshRepositoryAsync();
                }
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        public async Task RebaseCurrentBranchOnAsync(object? parameter)
        {
            var targetRef = parameter switch
            {
                GitCommit c => c.Hash,
                GitBranch b => b.Name,
                string s => s,
                _ => CommitHistory.SelectedCommit?.Hash
            };

            if (string.IsNullOrEmpty(targetRef) || string.IsNullOrEmpty(RepositoryPath)) return;

            var targetDisplay = parameter switch
            {
                GitCommit c => $"commit {c.ShortHash} ({c.Subject})",
                GitBranch b => $"branch '{b.DisplayName}'",
                _ => targetRef
            };

            var result = MessageBox.Show(
                $"Rebase current branch '{CurrentBranch}' on {targetDisplay}?\n\nWarning: This will replay local commits on top of {targetRef}.",
                "Confirm Rebase",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            IsLoading = true;
            StatusMessage = $"Rebasing {CurrentBranch} on {targetRef}...";

            try
            {
                var (success, output) = await _gitService.RebaseAsync(RepositoryPath, targetRef);
                if (success)
                {
                    await RefreshRepositoryAsync();
                    StatusMessage = $"Rebased successfully.";
                }
                else
                {
                    MessageBox.Show($"Rebase encountered conflicts or failed:\n{output}", "Rebase Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    await RefreshRepositoryAsync();
                }
            }
            finally
            {
                IsLoading = false;
            }
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
            else if (parameter is GitBranch branch)
            {
                ResetTargetRef = branch.Name;
                ResetTargetDescription = $"Branch {branch.DisplayName}";
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

        private async Task OnWorkingChangesUpdatedAsync(bool isNewCommit)
        {
            RepoStatus = await _gitService.GetRepoStatusAsync(RepositoryPath);
            if (isNewCommit)
            {
                await RefreshRepositoryAsync();
                if (WorkingChanges.PushAfterCommit)
                {
                    await PushAsync();
                }
            }
        }

        private void OnCommitSelected(GitCommit? commit)
        {
            _ = CommitDetail.SetCommitAsync(commit);
        }
    }
}
