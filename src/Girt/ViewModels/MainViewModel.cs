using System;
using System.Collections.Generic;
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

        public const string AppVersion = "0.4.27";

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

        // Set only around silent/background refreshes (see RefreshRepositorySilentlyAsync) -
        // drives a small, non-blocking status pill instead of the full busy overlay, since
        // those refreshes are real work (git process spawns, parsing, graph layout) that takes
        // genuine wall-clock time even off the UI thread. Without this there was no feedback at
        // all during a silent refresh - it just looked like nothing was happening.
        [ObservableProperty]
        private bool _isBackgroundBusy;

        public bool IsBusy => IsLoading || IsBackgroundBusy;

        partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsBusy));
        partial void OnIsBackgroundBusyChanged(bool value) => OnPropertyChanged(nameof(IsBusy));

        // Off by default: an external change to .git only updates the pills. On: it triggers a
        // full silent refresh (graph, branch list, working changes included).
        [ObservableProperty]
        private bool _autoRefresh;

        partial void OnAutoRefreshChanged(bool value)
        {
            _themeService.SaveAutoRefresh(value);
        }

        [ObservableProperty]
        private string _statusMessage = "Ready";

        [ObservableProperty]
        private bool _pushPillOpensReview;

        partial void OnPushPillOpensReviewChanged(bool value)
        {
            _themeService.SavePushPillOpensReview(value);
        }

        // Pull Choice Dialog State - shown only when the branch has diverged (commits on both
        // sides), since a plain `git pull` there silently creates a merge commit with no
        // warning. A clean fast-forward pull skips this and just happens.
        [ObservableProperty]
        private bool _isPullChoiceDialogOpen;

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
        public UnpushedChangesViewModel UnpushedChanges { get; }

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
                _themeService.SaveGroupBranchesIntoFolders,
                (busy, message) =>
                {
                    IsLoading = busy;
                    if (message != null) StatusMessage = message;
                },
                _themeService.LoadPinnedBranches,
                _themeService.SavePinnedBranches);
            CommitHistory = new CommitHistoryViewModel(_gitService, () => RepositoryPath, OnCommitSelected);
            CommitDetail = new CommitDetailViewModel(_gitService, () => RepositoryPath);
            UnpushedChanges = new UnpushedChangesViewModel(_gitService, () => RepositoryPath);
            WorkingChanges = new WorkingChangesViewModel(
                _gitService,
                () => RepositoryPath,
                OnWorkingChangesUpdatedAsync,
                _themeService.LoadPushAfterCommit(),
                _themeService.SavePushAfterCommit);

            _pushPillOpensReview = _themeService.LoadPushPillOpensReview();
            _autoRefresh = _themeService.LoadAutoRefresh();

            // Hook branch selection change to update association view immediately
            BranchList.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(BranchList.SelectedBranch) && BranchList.SelectedBranch != null)
                {
                    CommitHistory.SetBranches(BranchList.AllBranches, BranchList.SelectedBranch.Name);
                    _ = CommitHistory.ApplyFilterAsync();
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
                StartWatchingGitState(root);
                StartWatchingWorkingTree(root);

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

        private readonly List<FileSystemWatcher> _gitStateWatchers = new();
        private Timer? _gitStateDebounceTimer;

        // Girt only otherwise learns about the repo's state from its own actions - if
        // something else (Git Extensions, the CLI, another tab) changes HEAD, Girt would
        // keep showing a stale current branch until the user hits F5. Watching HEAD closes
        // that gap. Git rewrites HEAD via a lock file + rename, so several fs events can
        // fire for one logical branch switch - "HEAD*" catches both HEAD and HEAD.lock.
        //
        // HEAD alone misses an external pull/merge that fast-forwards the *current* branch:
        // that only moves the branch's ref (refs/heads/<name> or packed-refs) and updates the
        // index, never touching the HEAD file itself. Watching "index" and "refs" (recursively,
        // for the ref files themselves) closes that gap too. All three feed the same debounce
        // timer, which coalesces the burst of events any single git operation produces into one
        // refresh.
        private void StartWatchingGitState(string repoRoot)
        {
            StopWatchingGitState();

            var gitDir = Path.Combine(repoRoot, ".git");
            if (!Directory.Exists(gitDir)) return;

            var headWatcher = new FileSystemWatcher(gitDir) { Filter = "HEAD*", NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
            var indexWatcher = new FileSystemWatcher(gitDir) { Filter = "index", NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
            _gitStateWatchers.Add(headWatcher);
            _gitStateWatchers.Add(indexWatcher);

            var refsDir = Path.Combine(gitDir, "refs");
            if (Directory.Exists(refsDir))
            {
                _gitStateWatchers.Add(new FileSystemWatcher(refsDir) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName });
            }

            foreach (var watcher in _gitStateWatchers)
            {
                watcher.Changed += OnGitStateFileChanged;
                watcher.Created += OnGitStateFileChanged;
                watcher.Renamed += OnGitStateFileChanged;
                watcher.EnableRaisingEvents = true;
            }
        }

        private void StopWatchingGitState()
        {
            foreach (var watcher in _gitStateWatchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Changed -= OnGitStateFileChanged;
                watcher.Created -= OnGitStateFileChanged;
                watcher.Renamed -= OnGitStateFileChanged;
                watcher.Dispose();
            }
            _gitStateWatchers.Clear();

            _gitStateDebounceTimer?.Dispose();
            _gitStateDebounceTimer = null;
        }

        private void OnGitStateFileChanged(object sender, FileSystemEventArgs e)
        {
            _gitStateDebounceTimer?.Dispose();
            _gitStateDebounceTimer = new Timer(_ =>
            {
                // FileSystemWatcher raises events on a ThreadPool thread; ObservableProperty
                // setters must run on the UI thread.
                Application.Current?.Dispatcher.InvokeAsync(async () =>
                {
                    if (IsLoading || JustRanOwnGitCommand()) return;

                    // Something external changed .git - AutoRefresh decides how much to react:
                    // off (default), only the pills (ahead/behind/uncommitted/current branch)
                    // update, cheaply; on, it's a full silent refresh including the graph and
                    // branch list.
                    if (AutoRefresh)
                    {
                        await RefreshRepositorySilentlyAsync();
                    }
                    else
                    {
                        await RefreshPillsOnlyAsync();
                    }
                });
            }, null, dueTime: 300, period: Timeout.Infinite);
        }

        // Stage/unstage/commit/etc. all write to .git/index (and sometimes refs), which this
        // watcher also monitors to catch external pulls - without this, every single-file stage
        // triggered a full extra RefreshRepositoryAsync on top of the operation's own already-
        // correct, lightweight update, because those quick actions don't set IsLoading. A recent
        // git command from GitCliService means the write almost certainly came from Girt itself.
        private bool JustRanOwnGitCommand()
        {
            return DateTime.UtcNow - _gitService.LastCommandCompletedUtc < TimeSpan.FromMilliseconds(1000);
        }

        private FileSystemWatcher? _workingTreeWatcher;
        private Timer? _workingTreeDebounceTimer;

        // Covers file changes that never touch .git at all - e.g. editing a file in another
        // editor while the Changed Files view is open. Scoped to only actually refresh while
        // that view is visible, since a repo-wide recursive watcher fires constantly (builds,
        // IDEs touching timestamps, etc.) and a full working-changes reload isn't free.
        private void StartWatchingWorkingTree(string repoRoot)
        {
            StopWatchingWorkingTree();
            if (!Directory.Exists(repoRoot)) return;

            _workingTreeWatcher = new FileSystemWatcher(repoRoot)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size
            };
            _workingTreeWatcher.Changed += OnWorkingTreeFileChanged;
            _workingTreeWatcher.Created += OnWorkingTreeFileChanged;
            _workingTreeWatcher.Deleted += OnWorkingTreeFileChanged;
            _workingTreeWatcher.Renamed += OnWorkingTreeFileChanged;
            _workingTreeWatcher.EnableRaisingEvents = true;
        }

        private void StopWatchingWorkingTree()
        {
            if (_workingTreeWatcher != null)
            {
                _workingTreeWatcher.EnableRaisingEvents = false;
                _workingTreeWatcher.Changed -= OnWorkingTreeFileChanged;
                _workingTreeWatcher.Created -= OnWorkingTreeFileChanged;
                _workingTreeWatcher.Deleted -= OnWorkingTreeFileChanged;
                _workingTreeWatcher.Renamed -= OnWorkingTreeFileChanged;
                _workingTreeWatcher.Dispose();
                _workingTreeWatcher = null;
            }

            _workingTreeDebounceTimer?.Dispose();
            _workingTreeDebounceTimer = null;
        }

        private void OnWorkingTreeFileChanged(object sender, FileSystemEventArgs e)
        {
            // .git's own churn (objects, logs, index, lock files) fires constantly during any
            // git operation and is already covered by the git-state watcher above - ignore it
            // here so the same change doesn't trigger two separate refresh paths.
            var sep = Path.DirectorySeparatorChar;
            if (e.FullPath.Contains($"{sep}.git{sep}") || e.FullPath.EndsWith($"{sep}.git"))
            {
                return;
            }

            _workingTreeDebounceTimer?.Dispose();
            _workingTreeDebounceTimer = new Timer(_ =>
            {
                Application.Current?.Dispatcher.InvokeAsync(async () =>
                {
                    if (!IsLoading && IsWorkingChangesView && !JustRanOwnGitCommand()) await WorkingChanges.LoadChangesAsync();
                });
            }, null, dueTime: 400, period: Timeout.Infinite);
        }

        // Set only around refreshes the user explicitly asked for (F5, or as part of a longer
        // action like fetch/pull/push/checkout) - the busy overlay is appropriate there, since
        // the user is waiting on the result. Background refreshes triggered by the file
        // watchers go through RefreshRepositorySilentlyAsync instead, which does the same work
        // without it - a routine background sync shouldn't block the whole window.
        [RelayCommand]
        public async Task RefreshRepositoryAsync()
        {
            if (string.IsNullOrEmpty(RepositoryPath)) return;

            IsLoading = true;
            StatusMessage = "Refreshing repository...";

            try
            {
                await DoRefreshRepositoryAsync();
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

        private async Task RefreshRepositorySilentlyAsync()
        {
            if (string.IsNullOrEmpty(RepositoryPath) || IsBackgroundBusy) return;

            IsBackgroundBusy = true;
            try
            {
                await DoRefreshRepositoryAsync();
            }
            catch
            {
                // Silent by design - a background sync failing shouldn't interrupt whatever
                // the user is doing. It'll just try again on the next detected change.
            }
            finally
            {
                IsBackgroundBusy = false;
            }
        }

        // Targeted, cheap refreshes for local actions where synthesizing the exact new state
        // locally isn't safe to do (reset can drop an arbitrary number of commits; merge/rebase
        // can introduce several) - these keep the pills (and, where relevant, working changes or
        // the branch list) accurate with 2-3 git calls instead of the full multi-part refresh.
        // The commit graph itself is the one thing left stale until F5 or AutoRefresh for these
        // specific actions - Commit and Revert are handled separately since a single new commit
        // *can* be spliced in locally (see CommitHistory.PrependLocalCommit).
        private async Task RefreshPillsOnlyAsync()
        {
            if (string.IsNullOrEmpty(RepositoryPath)) return;
            try
            {
                var statusTask = _gitService.GetRepoStatusAsync(RepositoryPath);
                var branchTask = _gitService.GetCurrentBranchAsync(RepositoryPath);
                await Task.WhenAll(statusTask, branchTask);
                RepoStatus = await statusTask;
                CurrentBranch = await branchTask ?? CurrentBranch;
            }
            catch
            {
                // Silent by design, same as RefreshRepositorySilentlyAsync.
            }
        }

        private async Task RefreshPillsAndWorkingChangesAsync()
        {
            if (string.IsNullOrEmpty(RepositoryPath)) return;
            try
            {
                var statusTask = _gitService.GetRepoStatusAsync(RepositoryPath);
                var workingChangesTask = WorkingChanges.LoadChangesAsync();
                await Task.WhenAll(statusTask, workingChangesTask);
                RepoStatus = await statusTask;
            }
            catch
            {
                // Silent by design, same as RefreshRepositorySilentlyAsync.
            }
        }

        private async Task RefreshPillsAndBranchesAsync()
        {
            if (string.IsNullOrEmpty(RepositoryPath)) return;
            try
            {
                var statusTask = _gitService.GetRepoStatusAsync(RepositoryPath);
                var branchListTask = BranchList.LoadBranchesAsync();
                await Task.WhenAll(statusTask, branchListTask);
                RepoStatus = await statusTask;
            }
            catch
            {
                // Silent by design, same as RefreshRepositorySilentlyAsync.
            }
        }

        private async Task DoRefreshRepositoryAsync()
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
        public async Task PushFromReviewAsync()
        {
            await PushAsync();
            UnpushedChanges.Close();
        }

        [RelayCommand]
        public void ToggleReviewBeforePush()
        {
            PushPillOpensReview = !PushPillOpensReview;
        }

        [RelayCommand]
        public async Task PushPillClickedAsync()
        {
            if (PushPillOpensReview)
            {
                await UnpushedChanges.OpenAsync();
            }
            else
            {
                await PushAsync();
            }
        }

        [RelayCommand]
        public async Task PullAsync()
        {
            if (string.IsNullOrEmpty(RepositoryPath)) return;

            // Diverged (commits on both sides) means a plain pull silently creates a merge
            // commit with no warning - offer the choice instead. A clean fast-forward pull
            // (nothing local to lose) just happens, same as before.
            if (RepoStatus?.HasUpstream == true && RepoStatus.AheadCount > 0 && RepoStatus.BehindCount > 0)
            {
                IsPullChoiceDialogOpen = true;
                return;
            }

            await DoPullAsync(rebase: false);
        }

        [RelayCommand]
        public async Task ConfirmPullMergeAsync()
        {
            IsPullChoiceDialogOpen = false;
            await DoPullAsync(rebase: false);
        }

        [RelayCommand]
        public async Task ConfirmPullRebaseAsync()
        {
            IsPullChoiceDialogOpen = false;
            await DoPullAsync(rebase: true);
        }

        [RelayCommand]
        public void CancelPullChoiceDialog()
        {
            IsPullChoiceDialogOpen = false;
        }

        private async Task DoPullAsync(bool rebase)
        {
            IsLoading = true;
            StatusMessage = rebase ? "Pulling (rebase) from remote..." : "Pulling commits from remote...";

            try
            {
                var (success, output) = await _gitService.PullAsync(RepositoryPath, rebase);
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
                // BranchList.FetchAllAsync already triggers a full RefreshRepositoryAsync (via
                // the onBranchChanged callback) once the fetch succeeds - calling it again here
                // duplicated every piece of refresh work (branches, status, commits, working
                // changes) on every single fetch, which is why fetch/refresh felt so slow.
                await BranchList.FetchAllAsync();
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

            // Local-only and quick - no busy overlay, same as commit/reset/cherry-pick.
            StatusMessage = $"Reverting commit {commit.ShortHash}...";

            var (success, output) = await _gitService.RevertCommitAsync(RepositoryPath, commit.Hash);
            if (success)
            {
                // A revert is itself a new commit - same local-splice treatment as a manual
                // commit rather than any kind of reload.
                var latest = await _gitService.GetCommitsAsync(RepositoryPath, maxCount: 1);
                if (latest.Count > 0)
                {
                    CommitHistory.PrependLocalCommit(latest[0]);
                }
                UpdateRepoStatusLocally(aheadDelta: RepoStatus.HasUpstream ? 1 : 0);
                StatusMessage = $"Reverted commit {commit.ShortHash} successfully.";
            }
            else
            {
                MessageBox.Show($"Failed to revert commit:\n{output}", "Revert Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        public async Task CherryPickCommitAsync(object? parameter)
        {
            var commit = (parameter as GitCommit) ?? CommitHistory.SelectedCommit;
            if (commit == null || string.IsNullOrEmpty(RepositoryPath)) return;

            var result = MessageBox.Show(
                $"Cherry-pick commit {commit.ShortHash} onto current branch '{CurrentBranch}'?\n\nSubject: {commit.Subject}\nAuthor: {commit.AuthorName}\n\nThis applies the change as a local, uncommitted change - you'll review and commit it yourself, same as any other edit.",
                "Cherry-Pick Commit",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            // Local-only (--no-commit) and quick - no busy overlay, same as commit/reset.
            StatusMessage = $"Cherry-picking commit {commit.ShortHash}...";

            var (success, output) = await _gitService.CherryPickCommitAsync(RepositoryPath, commit.Hash);
            if (success)
            {
                // --no-commit lands the change as staged/uncommitted - the working tree is what
                // actually needs to be accurate here, not the (unchanged) commit graph.
                await RefreshPillsAndWorkingChangesAsync();
                StatusMessage = $"Cherry-picked commit {commit.ShortHash} - review and commit when ready.";
                await ShowWorkingChangesViewAsync();
            }
            else
            {
                MessageBox.Show($"Failed to cherry-pick commit:\n{output}", "Cherry-Pick Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

            // Local-only - no busy overlay, same as commit/reset/cherry-pick/revert.
            StatusMessage = $"Merging {targetRef} into {CurrentBranch}...";

            var (success, output) = await _gitService.MergeAsync(RepositoryPath, targetRef, squash, noFf);
            if (success)
            {
                // A merge can introduce any number of commits from the other side - not safe to
                // splice locally like a single new commit, so the graph is the one thing left
                // stale here until F5/AutoRefresh. Working tree and pills still update.
                await RefreshPillsAndWorkingChangesAsync();
                StatusMessage = $"Merged successfully.";
            }
            else
            {
                MessageBox.Show($"Merge encountered conflicts or failed:\n{output}", "Merge Error", MessageBoxButton.OK, MessageBoxImage.Error);
                await RefreshPillsAndWorkingChangesAsync();
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

            // Local-only - no busy overlay, same as commit/reset/cherry-pick/revert/merge.
            StatusMessage = $"Rebasing {CurrentBranch} on {targetRef}...";

            var (success, output) = await _gitService.RebaseAsync(RepositoryPath, targetRef);
            if (success)
            {
                // A rebase replays potentially several commits with new hashes - not safe to
                // splice locally, so the graph is left stale here until F5/AutoRefresh. Working
                // tree and pills still update.
                await RefreshPillsAndWorkingChangesAsync();
                StatusMessage = $"Rebased successfully.";
            }
            else
            {
                MessageBox.Show($"Rebase encountered conflicts or failed:\n{output}", "Rebase Error", MessageBoxButton.OK, MessageBoxImage.Error);
                await RefreshPillsAndWorkingChangesAsync();
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
            // Local-only and near-instant - no busy overlay, same as commit/reset/cherry-pick.
            StatusMessage = $"Creating branch '{branchName}'...";

            var (success, output) = await _gitService.CreateBranchAsync(RepositoryPath, branchName);
            if (success)
            {
                // No working-tree change and no new commit - just the branch list needs it.
                await RefreshPillsAndBranchesAsync();
            }
            else
            {
                MessageBox.Show($"Failed to create branch:\n{output}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

        // Coarse "undo everything unpushed" shortcut from the review-before-push dialog -
        // always targets the upstream branch (exactly what that dialog is comparing against).
        // For resetting to a specific commit/branch instead, ShowResetDialog (the main "↺ Reset
        // HEAD" toolbar button) is the nuanced path.
        [RelayCommand]
        public async Task ResetToUpstreamAsync(string modeStr)
        {
            if (!Enum.TryParse<GitResetMode>(modeStr, true, out var mode)) return;
            if (string.IsNullOrEmpty(RepositoryPath) || RepoStatus?.HasUpstream != true) return;

            var commitWord = RepoStatus.AheadCount == 1 ? "commit" : "commits";
            var message = mode == GitResetMode.Hard
                ? $"Hard reset to {RepoStatus.UpstreamBranch}?\n\nThis permanently discards {RepoStatus.AheadCount} unpushed {commitWord} AND their changes. This cannot be undone."
                : $"Soft reset to {RepoStatus.UpstreamBranch}?\n\nThis undoes {RepoStatus.AheadCount} unpushed {commitWord}, but keeps their changes staged so nothing is lost.";

            var result = MessageBox.Show(
                message,
                mode == GitResetMode.Hard ? "Hard Reset" : "Soft Reset",
                MessageBoxButton.YesNo,
                mode == GitResetMode.Hard ? MessageBoxImage.Warning : MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            UnpushedChanges.Close();
            ResetTargetRef = "@{u}";
            ResetMode = mode;
            await ConfirmResetAsync();
        }

        [RelayCommand]
        public async Task ConfirmResetAsync()
        {
            if (string.IsNullOrEmpty(RepositoryPath)) return;

            var target = ResetTargetRef?.Trim();
            if (string.IsNullOrEmpty(target)) return;

            IsResetDialogOpen = false;
            StatusMessage = $"Resetting HEAD ({ResetMode}) to {target}...";

            // Reset is a fast, local operation like commit - no busy overlay. It can drop an
            // arbitrary number of commits though, so (unlike a single new commit) the graph
            // isn't safe to patch up locally - it's left stale here until F5/AutoRefresh, while
            // pills and working changes (soft/mixed leave the reset commits' changes uncommitted;
            // hard leaves the tree clean) update right away.
            var (success, output) = await _gitService.ResetHeadAsync(RepositoryPath, target, ResetMode);
            if (success)
            {
                await RefreshPillsAndWorkingChangesAsync();
            }
            else
            {
                MessageBox.Show($"Failed to reset HEAD:\n{output}", "Reset Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task OnBranchChangedAsync()
        {
            await RefreshRepositoryAsync();
        }

        private async Task OnWorkingChangesUpdatedAsync(bool isNewCommit)
        {
            if (isNewCommit)
            {
                // The commit already succeeded (WorkingChangesViewModel only calls this after
                // git confirms it did) - "git log -1" fetches just that one commit's real data
                // (hash/author/date/parent), which PrependLocalCommit splices straight into the
                // already-loaded graph. No branches/working-changes/full-log reload at all.
                var latest = await _gitService.GetCommitsAsync(RepositoryPath, maxCount: 1);
                if (latest.Count > 0)
                {
                    CommitHistory.PrependLocalCommit(latest[0]);
                }

                UpdateRepoStatusLocally(aheadDelta: RepoStatus.HasUpstream ? 1 : 0);

                if (WorkingChanges.PushAfterCommit)
                {
                    // Push is a real network round-trip, so it's the one place a full refresh
                    // still makes sense once it completes.
                    await PushAsync();
                }
            }
            else
            {
                // Stage/unstage/discard already update WorkingChanges' own lists optimistically
                // (see WorkingChangesViewModel) - derive the uncommitted count from those
                // instead of asking git again for something we already know.
                UpdateRepoStatusLocally(aheadDelta: 0);
            }
        }

        // Rebuilds RepoStatus from what's already known locally, rather than a git round-trip:
        // uncommitted count comes straight from WorkingChanges' own (already-optimistic) lists,
        // and aheadDelta lets a caller that just committed bump the push count by exactly what
        // it knows changed. RepoStatus has no change notification of its own, so this has to
        // reassign the whole object for the "{Binding RepoStatus.AheadCount}"-style bindings to
        // actually see the update.
        private void UpdateRepoStatusLocally(int aheadDelta)
        {
            RepoStatus = new GitRepoStatus
            {
                UncommittedCount = WorkingChanges.StagedFiles.Count + WorkingChanges.UnstagedFiles.Count,
                AheadCount = Math.Max(0, RepoStatus.AheadCount + aheadDelta),
                BehindCount = RepoStatus.BehindCount,
                HasUpstream = RepoStatus.HasUpstream,
                UpstreamBranch = RepoStatus.UpstreamBranch
            };
        }

        private void OnCommitSelected(GitCommit? commit)
        {
            _ = CommitDetail.SetCommitAsync(commit);
        }
    }
}
