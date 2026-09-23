using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

        // Reads the version straight from the assembly (set by <Version> in Girt.csproj) instead
        // of a separately hardcoded string here - a third copy that silently went stale (window
        // title kept reporting 0.4.46 for several releases after the csproj had moved on).
        public static readonly string AppVersion =
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v
                ? $"{v.Major}.{v.Minor}.{v.Build}"
                : "0.0.0";

        [ObservableProperty]
        private string _repositoryPath = string.Empty;

        [ObservableProperty]
        private string _repositoryName = "No Repository Opened";

        [ObservableProperty]
        private string _currentBranch = "-";

        [ObservableProperty]
        private GitRepoStatus _repoStatus = new();

        // RepoStatus is reassigned wholesale rather than mutated (see UpdateRepoStatusLocally),
        // so UpstreamBranchShortName - a computed property derived from it, bound directly by
        // the branch-mismatch push dialog - needs its own notification or the dialog would show
        // whatever it evaluated to at startup (before the first real repo load) forever after.
        partial void OnRepoStatusChanged(GitRepoStatus value) => OnPropertyChanged(nameof(UpstreamBranchShortName));

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

        // Applies to every diff view (commit detail, working tree, unpushed review) via each
        // ViewModel's injected getIgnoreWhitespace delegate - see their Get*DiffAsync calls.
        [ObservableProperty]
        private bool _ignoreWhitespaceInDiffs;

        partial void OnIgnoreWhitespaceInDiffsChanged(bool value)
        {
            _themeService.SaveIgnoreWhitespaceInDiffs(value);

            // Re-fetch whatever diff is currently on screen so the toggle feels immediate
            // instead of only applying the next time a file is clicked.
            _ = CommitDetail.RefreshDiffAsync();
            _ = WorkingChanges.RefreshDiffAsync();
            _ = UnpushedChanges.RefreshDiffAsync();
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

        // Merge Preview Dialog State - shown before a merge actually runs, so the user sees
        // what's about to happen (commits and files coming in) instead of just a bare
        // "are you sure?" prompt.
        [ObservableProperty]
        private bool _isMergePreviewDialogOpen;

        [ObservableProperty]
        private string _mergePreviewSourceDisplay = string.Empty;

        [ObservableProperty]
        private string _mergePreviewModeLabel = string.Empty;

        public ObservableCollection<GitCommit> MergePreviewCommits { get; } = new();
        public ObservableCollection<GitFileDiff> MergePreviewFiles { get; } = new();

        private string? _pendingMergeTargetRef;
        private bool _pendingMergeSquash;
        private bool _pendingMergeNoFf;

        // Merge Conflict Dialog State - shown when MergeAsync fails and `git status` reports
        // unmerged paths. Resolution itself happens in the user's own editor/merge tool
        // (external handoff, not an in-app 3-way editor) - this dialog's job is just to make
        // the conflicted files, and what each side actually changed, visible and trackable.
        [ObservableProperty]
        private bool _isMergeConflictDialogOpen;

        [ObservableProperty]
        private MergeConflictFile? _selectedConflictFile;

        [ObservableProperty]
        private bool _canContinueMerge;

        // The conflict dialog is shared by three producers (an explicit merge, a plain pull,
        // and a rebase pull) - a rebase leaves the repo in a different git state than a merge
        // (mid-rebase vs mid-merge), so Abort/Continue need different underlying commands, and
        // the dialog's own title/button labels are kept in sync with which one is active.
        private bool _conflictIsRebase;

        [ObservableProperty]
        private string _mergeConflictDialogTitle = "⚠ Merge Conflicts";

        [ObservableProperty]
        private string _continueMergeButtonLabel = "Continue Merge";

        [ObservableProperty]
        private string _abortMergeButtonLabel = "Abort Merge";

        public ObservableCollection<MergeConflictFile> MergeConflictFiles { get; } = new();
        public SimpleDiffViewModel OursDiff { get; } = new();
        public SimpleDiffViewModel TheirsDiff { get; } = new();

        partial void OnSelectedConflictFileChanged(MergeConflictFile? value) => _ = LoadConflictDiffsAsync(value);

        // Push Rejected Dialog State - shown when a push fails specifically because the remote
        // has moved on (non-fast-forward), mirroring Git Extensions' "Pull latest changes from
        // remote repository" recovery prompt instead of just dumping the raw git error.
        [ObservableProperty]
        private bool _isPushRejectedDialogOpen;

        // No-Upstream Dialog State - shown when a push fails because the current branch has
        // never been pushed before and has nothing to push to, mirroring Git Extensions'
        // "Publish branch" prompt instead of just dumping the raw git error.
        [ObservableProperty]
        private bool _isPushNoUpstreamDialogOpen;

        // Branch-Name-Mismatch Dialog State - shown when a push fails because the current
        // branch's upstream is a differently-named branch (e.g. a feature branch that's tracking
        // origin/develop) - git itself suggests the two recovery commands this dialog offers as
        // buttons, instead of just dumping the raw "usage:"-style git error.
        [ObservableProperty]
        private bool _isPushBranchMismatchDialogOpen;

        public BranchListViewModel BranchList { get; }
        public CommitHistoryViewModel CommitHistory { get; }
        public CommitDetailViewModel CommitDetail { get; }
        public WorkingChangesViewModel WorkingChanges { get; }
        public UnpushedChangesViewModel UnpushedChanges { get; }
        public SettingsViewModel Settings { get; }

        [ObservableProperty]
        private bool _isSettingsDialogOpen;

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
            CommitDetail = new CommitDetailViewModel(_gitService, () => RepositoryPath, () => IgnoreWhitespaceInDiffs);
            UnpushedChanges = new UnpushedChangesViewModel(_gitService, () => RepositoryPath, () => IgnoreWhitespaceInDiffs);
            WorkingChanges = new WorkingChangesViewModel(
                _gitService,
                () => RepositoryPath,
                OnWorkingChangesUpdatedAsync,
                _themeService.LoadPushAfterCommit(),
                _themeService.SavePushAfterCommit,
                (busy, message) =>
                {
                    IsLoading = busy;
                    if (message != null) StatusMessage = message;
                },
                () => IgnoreWhitespaceInDiffs);
            Settings = new SettingsViewModel(
                _gitService,
                () => RepositoryPath,
                _themeService.LoadMinimizeToTray(),
                _themeService.SaveMinimizeToTray,
                _themeService.LoadMinimizeOnClose(),
                _themeService.SaveMinimizeOnClose,
                _themeService.LoadFolderExpandOnSingleClick(),
                _themeService.SaveFolderExpandOnSingleClick,
                _themeService.LoadEnableTimingLogs(),
                _themeService.SaveEnableTimingLogs);

            _pushPillOpensReview = _themeService.LoadPushPillOpensReview();
            _autoRefresh = _themeService.LoadAutoRefresh();
            _ignoreWhitespaceInDiffs = _themeService.LoadIgnoreWhitespaceInDiffs();

            // Hook branch selection change to update association view immediately, and bring
            // the branch's tip commit into view/selected in the graph - same as clicking a
            // commit directly, so picking a branch actually shows you something happened.
            BranchList.PropertyChanged += async (s, e) =>
            {
                if (e.PropertyName == nameof(BranchList.SelectedBranch) && BranchList.SelectedBranch != null)
                {
                    var selectedBranch = BranchList.SelectedBranch;
                    CommitHistory.SetBranches(BranchList.AllBranches, selectedBranch.Name);
                    await CommitHistory.ApplyFilterAsync();
                    CommitHistory.SelectBranchTip(selectedBranch);
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
                var ensureConfigTask = _gitService.EnsureFastStatusConfigAsync(root);
                var ignoredDirsTask = _gitService.GetIgnoredDirectoryNamesAsync(root, CandidateNoisyDirNames);
                await Task.WhenAll(ensureConfigTask, ignoredDirsTask);
                _ignoredNoisyDirNames = await ignoredDirsTask;

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

            lock (_debounceLock)
            {
                _gitStateDebounceTimer?.Dispose();
                _gitStateDebounceTimer = null;
            }
        }

        // FileSystemWatcher raises events on thread-pool threads, several at once during a
        // burst - without a lock two of them can each dispose the "current" timer and create
        // their own, leaving an orphaned timer that still fires.
        private readonly object _debounceLock = new();

        private void OnGitStateFileChanged(object sender, FileSystemEventArgs e)
        {
            lock (_debounceLock)
            {
                _gitStateDebounceTimer?.Dispose();
                // ObservableProperty setters must run on the UI thread.
                _gitStateDebounceTimer = new Timer(
                    _ => Application.Current?.Dispatcher.InvokeAsync(RunGitStateRefreshAsync),
                    null, dueTime: 300, period: Timeout.Infinite);
            }
        }

        private bool _isGitStateRefreshInFlight;
        private bool _gitStateRefreshPending;

        private async Task RunGitStateRefreshAsync()
        {
            // Checked first on purpose: git status itself can rewrite .git/index (untracked
            // cache / stat refresh), so events that land while Girt's own command is running
            // look exactly like an external change. Treating them as pending would make every
            // refresh schedule another one, forever.
            if (IsLoading || JustRanOwnGitCommand()) return;

            // A change that lands between git calls of a refresh that's already running (e.g.
            // during its graph layout) is queued for one more pass instead of being dropped.
            if (_isGitStateRefreshInFlight)
            {
                _gitStateRefreshPending = true;
                return;
            }

            _isGitStateRefreshInFlight = true;
            try
            {
                do
                {
                    _gitStateRefreshPending = false;

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
                }
                while (_gitStateRefreshPending && !IsLoading);
            }
            finally
            {
                _isGitStateRefreshInFlight = false;
            }
        }

        // Stage/unstage/commit/etc. all write to .git/index (and sometimes refs), which this
        // watcher also monitors to catch external pulls - without this, every single-file stage
        // triggered a full extra RefreshRepositoryAsync on top of the operation's own already-
        // correct, lightweight update, because those quick actions don't set IsLoading. A recent
        // git command from GitCliService means the write almost certainly came from Girt itself.
        // IsCommandInFlight closes a real race on a slow command (git commit on a big repo can
        // take several seconds): git writes HEAD/index/refs well before the process exits, so a
        // watcher event can fire - and this check can run - while the command that caused it is
        // still running, before LastCommandCompletedUtc gets stamped. Without this, a background
        // refresh could fire mid-commit and race the commit's own post-success graph splice
        // (CommitHistory.PrependLocalCommitAsync), corrupting _allCommits with a duplicate entry.
        private bool JustRanOwnGitCommand()
        {
            return _gitService.IsCommandInFlight
                || DateTime.UtcNow - _gitService.LastCommandCompletedUtc < TimeSpan.FromMilliseconds(1000);
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

            _workingTreeRoot = repoRoot;
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

            lock (_debounceLock)
            {
                _workingTreeDebounceTimer?.Dispose();
                _workingTreeDebounceTimer = null;
            }
        }

        // Build/restore output directories - a rebuild or NuGet restore can be thousands of
        // file touches in seconds on a big solution, each one otherwise a reason to re-run git
        // status. Only the ones git actually ignores in this repo get skipped (asked once, on
        // open, via GetIgnoredDirectoryNamesAsync) - so a tracked JS-monorepo `packages/` or a
        // tracked `bin/` scripts folder is still watched, while a gitignored NuGet `packages/`
        // isn't.
        private static readonly string[] CandidateNoisyDirNames =
        {
            "bin", "obj", "node_modules", "packages", ".vs", "TestResults"
        };

        private IReadOnlySet<string> _ignoredNoisyDirNames = new HashSet<string>();
        private string _workingTreeRoot = string.Empty;

        // Only looks at the path *below* the repo root - a repo that itself lives under e.g.
        // C:\packages\... or ...\bin\... would otherwise have every single event ignored.
        private bool ShouldIgnoreWorkingTreeEvent(string fullPath)
        {
            var relative = Path.GetRelativePath(_workingTreeRoot, fullPath);
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                // .git's own churn (objects, logs, index, lock files) fires constantly during
                // any git operation and is already covered by the git-state watcher above.
                if (string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase)) return true;
                if (_ignoredNoisyDirNames.Contains(segment)) return true;
            }
            return false;
        }

        private bool _isWorkingTreeRefreshInFlight;
        private bool _workingTreeRefreshPending;

        private void OnWorkingTreeFileChanged(object sender, FileSystemEventArgs e)
        {
            if (ShouldIgnoreWorkingTreeEvent(e.FullPath)) return;

            lock (_debounceLock)
            {
                _workingTreeDebounceTimer?.Dispose();
                _workingTreeDebounceTimer = new Timer(
                    _ => Application.Current?.Dispatcher.InvokeAsync(RunWorkingTreeRefreshAsync),
                    null, dueTime: 400, period: Timeout.Infinite);
            }
        }

        private async Task RunWorkingTreeRefreshAsync()
        {
            if (IsLoading || !IsWorkingChangesView) return;

            // Checked before JustRanOwnGitCommand on purpose: the refresh's own git status call
            // never writes working-tree files (only .git, which is filtered out above), so an
            // edit that lands while it's running is a genuine outside change - queue one more
            // pass rather than letting "a command is in flight" swallow it.
            if (_isWorkingTreeRefreshInFlight)
            {
                _workingTreeRefreshPending = true;
                return;
            }

            if (JustRanOwnGitCommand()) return;

            _isWorkingTreeRefreshInFlight = true;
            try
            {
                do
                {
                    _workingTreeRefreshPending = false;
                    await WorkingChanges.LoadChangesAsync();

                    // LoadChangesAsync only refreshes WorkingChanges' own StagedFiles/
                    // UnstagedFiles - without this, editing a file externally (another
                    // editor, a build step) while looking at this tab updated the file list
                    // correctly but left the toolbar's "N to commit" pill (RepoStatus.
                    // UncommittedCount) showing whatever it was before, e.g. "0 to commit"
                    // next to a panel full of real unstaged files.
                    UpdateRepoStatusLocally(aheadDelta: 0);
                }
                while (_workingTreeRefreshPending && !IsLoading && IsWorkingChangesView);
            }
            finally
            {
                _isWorkingTreeRefreshInFlight = false;
            }
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
                // An explicit F5 is the user's "no, really, show me the real state" escape hatch -
                // always do the full commit reload rather than trusting the branch-tips fingerprint.
                await DoRefreshRepositoryAsync(allowSkipUnchangedCommits: false);
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
                // A silent background refresh is triggered by *something* in .git changing, but
                // that isn't always something reachable from `git log --all` (a stash push, a
                // config write) - let it skip the expensive log walk and relayout when the
                // branch tips genuinely haven't moved.
                await DoRefreshRepositoryAsync(allowSkipUnchangedCommits: true);
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
        // *can* be spliced in locally (see CommitHistory.PrependLocalCommitAsync).
        // Deliberately doesn't set IsBackgroundBusy: that drives the visible "working"
        // indicator, which is meant for a real background sync, not a quick pill update.
        // Re-entrancy is handled by the caller (RunGitStateRefreshAsync).
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

        private async Task DoRefreshRepositoryAsync(bool allowSkipUnchangedCommits)
        {
            var branchTask = _gitService.GetCurrentBranchAsync(RepositoryPath);
            var statusTask = _gitService.GetRepoStatusAsync(RepositoryPath);
            var branchListTask = BranchList.LoadBranchesAsync();
            var workingChangesTask = WorkingChanges.LoadChangesAsync();
            // Runs alongside the others, so it adds no wall-clock time to the refresh.
            var refsTask = _gitService.GetRefsFingerprintAsync(RepositoryPath);

            await Task.WhenAll(branchTask, statusTask, branchListTask, workingChangesTask, refsTask);

            CurrentBranch = await branchTask ?? "-";
            RepoStatus = await statusTask;
            CommitHistory.SetBranches(BranchList.AllBranches, CurrentBranch);

            // show-ref gives HEAD's hash but not which branch it's on - switching between two
            // branches at the same commit only moves the graph's "HEAD ->" label, so the branch
            // name is part of the fingerprint too.
            var refs = await refsTask;
            var refsFingerprint = string.IsNullOrEmpty(refs) ? null : $"{CurrentBranch}\n{refs}";
            await CommitHistory.LoadCommitsAsync(refsFingerprint, allowSkipUnchangedCommits);

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
            UpdateRepoStatusLocally(aheadDelta: 0);
        }

        // Overridable so tests can auto-confirm without popping a real MessageBox - see
        // WorkingChangesViewModel.ConfirmStashAction for the same pattern; production code
        // never sets this and gets the real Yes/No dialog.
        public Func<string, bool> ConfirmNothingToPushAction { get; set; } =
            message => MessageBox.Show(message, "Nothing To Push", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes;

        [RelayCommand]
        public async Task PushAsync()
        {
            if (string.IsNullOrEmpty(RepositoryPath)) return;

            // `git push` with nothing ahead just reports "Everything up-to-date" - not an
            // error, but if there are uncommitted changes lying around, that "success" pushed
            // literally none of the user's actual work, which reads exactly like a failure.
            // Only trust AheadCount for this when there's an upstream to compare against - a
            // brand-new local branch with no upstream yet has real commits worth pushing even
            // though AheadCount is never computed for it.
            if (RepoStatus?.HasUpstream == true && !RepoStatus.HasCommitsToPush && RepoStatus.HasChangesToCommit)
            {
                var message = $"There's nothing to push yet - you have {RepoStatus.UncommittedCount} uncommitted change(s).\n\nStage them now so you can write a commit message?";
                if (ConfirmNothingToPushAction(message))
                {
                    // Land on Working Changes with everything already staged - all that's left
                    // is typing a commit message, not a second manual "stage all" click.
                    await ShowWorkingChangesViewAsync();
                    await WorkingChanges.StageAllAsync();
                }

                return;
            }

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
                else if (IsNonFastForwardRejection(output))
                {
                    IsPushRejectedDialogOpen = true;
                    StatusMessage = "Push rejected - the remote has commits you don't have locally.";
                }
                else if (IsNoUpstreamRejection(output))
                {
                    IsPushNoUpstreamDialogOpen = true;
                    StatusMessage = "This branch doesn't exist on the remote yet.";
                }
                else if (IsUpstreamBranchNameMismatch(output))
                {
                    IsPushBranchMismatchDialogOpen = true;
                    StatusMessage = "This branch's upstream has a different name.";
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

        private static bool IsNonFastForwardRejection(string output) =>
            output.Contains("[rejected]") && (output.Contains("fetch first") || output.Contains("non-fast-forward"));

        private static bool IsNoUpstreamRejection(string output) =>
            output.Contains("has no upstream branch");

        private static bool IsUpstreamBranchNameMismatch(string output) =>
            output.Contains("The upstream branch of your current branch does not match");

        // "origin/develop" -> "develop" - RepoStatus.UpstreamBranch always comes back
        // remote-qualified (see GitCliService.GetRepoStatusAsync's `rev-parse --abbrev-ref @{u}`),
        // but `git push origin HEAD:<name>` wants just the branch name on the remote side.
        public string? UpstreamBranchShortName
        {
            get
            {
                var upstream = RepoStatus?.UpstreamBranch;
                if (string.IsNullOrEmpty(upstream)) return upstream;
                var slashIndex = upstream.IndexOf('/');
                return slashIndex >= 0 ? upstream[(slashIndex + 1)..] : upstream;
            }
        }

        [RelayCommand]
        public void CancelPushRejectedDialog() => IsPushRejectedDialogOpen = false;

        [RelayCommand]
        public void CancelPushNoUpstreamDialog() => IsPushNoUpstreamDialogOpen = false;

        [RelayCommand]
        public void CancelPushBranchMismatchDialog() => IsPushBranchMismatchDialogOpen = false;

        // Option 1 from git's own suggestion: push HEAD to the differently-named branch that's
        // already configured as upstream, leaving the tracking relationship as-is.
        [RelayCommand]
        public async Task ConfirmPushToUpstreamBranchAsync()
        {
            IsPushBranchMismatchDialogOpen = false;
            if (string.IsNullOrEmpty(RepositoryPath)) return;

            var upstreamShortName = UpstreamBranchShortName;
            if (string.IsNullOrEmpty(upstreamShortName)) return;

            IsLoading = true;
            StatusMessage = $"Pushing to '{upstreamShortName}'...";
            try
            {
                var (success, output) = await _gitService.PushToUpstreamBranchAsync(RepositoryPath, upstreamShortName);
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

        // Option 2 from git's own suggestion: push HEAD to a new branch of the same name on the
        // remote instead, and make that the branch's upstream going forward - same recovery as
        // the "no upstream yet" case, since the net effect (same-named remote branch, tracked)
        // is identical.
        [RelayCommand]
        public async Task ConfirmPushToSameNameBranchAsync()
        {
            IsPushBranchMismatchDialogOpen = false;
            if (string.IsNullOrEmpty(RepositoryPath)) return;

            IsLoading = true;
            StatusMessage = $"Publishing branch '{CurrentBranch}' to origin...";
            try
            {
                var (success, output) = await _gitService.PushSetUpstreamAsync(RepositoryPath, CurrentBranch);
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
        public async Task ConfirmPushSetUpstreamAsync()
        {
            IsPushNoUpstreamDialogOpen = false;
            if (string.IsNullOrEmpty(RepositoryPath)) return;

            IsLoading = true;
            StatusMessage = $"Publishing branch '{CurrentBranch}' to origin...";
            try
            {
                var (success, output) = await _gitService.PushSetUpstreamAsync(RepositoryPath, CurrentBranch);
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
        public async Task ConfirmPushRejectedPullMergeAsync()
        {
            IsPushRejectedDialogOpen = false;
            await PullThenPushAsync(rebase: false);
        }

        [RelayCommand]
        public async Task ConfirmPushRejectedPullRebaseAsync()
        {
            IsPushRejectedDialogOpen = false;
            await PullThenPushAsync(rebase: true);
        }

        private async Task PullThenPushAsync(bool rebase)
        {
            // Only retry the push once the pull actually landed clean - a conflict leaves the
            // conflict dialog open instead, and pushing a half-resolved merge/rebase would be
            // wrong, so this must not fire in that case.
            var pulled = await DoPullAsync(rebase);
            if (pulled)
            {
                await PushAsync();
            }
        }

        [RelayCommand]
        public async Task ConfirmForcePushWithLeaseAsync()
        {
            IsPushRejectedDialogOpen = false;
            if (string.IsNullOrEmpty(RepositoryPath)) return;

            var result = MessageBox.Show(
                "Force push with lease overwrites the remote branch with your local history.\n\n" +
                "Unlike a plain force push, this fails safely if someone else has pushed since your last fetch - " +
                "but it will still discard whatever they pushed if you proceed anyway.\n\nForce push now?",
                "Force Push With Lease",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            IsLoading = true;
            StatusMessage = "Force pushing with lease...";
            try
            {
                var (success, output) = await _gitService.ForcePushWithLeaseAsync(RepositoryPath);
                if (success)
                {
                    await RefreshRepositoryAsync();
                    StatusMessage = "Force push successful!";
                }
                else
                {
                    MessageBox.Show($"Force push failed:\n{output}", "Git Push Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

        // Returns whether the pull landed clean - false both for a hard failure and for a
        // conflict (which opens the conflict dialog instead of failing outright), so callers
        // that chain more work after a pull (see PullThenPushAsync) only proceed on a real
        // success.
        private async Task<bool> DoPullAsync(bool rebase)
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
                    return true;
                }

                // `git pull` is fetch+merge (or fetch+rebase) under the hood, so it can conflict
                // exactly like an explicit merge - route it through the same conflict dialog
                // instead of just dumping the raw git error, remembering which operation is
                // actually in progress so Abort/Continue run the right underlying commands.
                var conflicts = await _gitService.GetConflictedFilesAsync(RepositoryPath);
                if (conflicts.Count > 0)
                {
                    _conflictIsRebase = rebase;
                    MergeConflictDialogTitle = rebase ? "⚠ Rebase Conflicts" : "⚠ Merge Conflicts";
                    ContinueMergeButtonLabel = rebase ? "Continue Rebase" : "Continue Merge";
                    AbortMergeButtonLabel = rebase ? "Abort Rebase" : "Abort Merge";

                    MergeConflictFiles.Clear();
                    foreach (var conflict in conflicts) MergeConflictFiles.Add(conflict);
                    SelectedConflictFile = MergeConflictFiles.FirstOrDefault();
                    CanContinueMerge = false;
                    IsMergeConflictDialogOpen = true;
                    StatusMessage = $"{(rebase ? "Rebase" : "Pull")} stopped - {conflicts.Count} file(s) have conflicts.";
                }
                else
                {
                    MessageBox.Show($"Pull failed:\n{output}", "Git Pull Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                return false;
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

        [RelayCommand]
        public async Task OpenSettingsAsync()
        {
            IsSettingsDialogOpen = true;
            await Settings.LoadGitIdentityAsync();
            await Settings.LoadMergeToolSettingsAsync();
        }

        [RelayCommand]
        public void CloseSettingsDialog()
        {
            IsSettingsDialogOpen = false;
        }

        // ================= CLIPBOARD COPY COMMANDS =================
        // Retry-on-transient-failure logic lives in ClipboardHelper.TrySetText (shared with
        // DiffViewerControl's "Copy Line(s)" - see that class for why the retry matters).

        [RelayCommand]
        public void CopyCommitSha(object? parameter)
        {
            var hash = (parameter as GitCommit)?.Hash ?? CommitHistory.SelectedCommit?.Hash;
            if (!string.IsNullOrEmpty(hash))
            {
                StatusMessage = ClipboardHelper.TrySetText(hash)
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
                StatusMessage = ClipboardHelper.TrySetText(shortHash)
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
                StatusMessage = ClipboardHelper.TrySetText(msg)
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
                StatusMessage = ClipboardHelper.TrySetText(text)
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
                StatusMessage = ClipboardHelper.TrySetText(name)
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
                    await CommitHistory.PrependLocalCommitAsync(latest[0]);
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

        // Gathers what a merge would actually do (incoming commits, changed files) and opens
        // the preview dialog for confirmation - replaces the previous bare Yes/No prompt, which
        // gave no idea what was about to land.
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

            _pendingMergeTargetRef = targetRef;
            _pendingMergeSquash = squash;
            _pendingMergeNoFf = noFf;

            MergePreviewSourceDisplay = targetDisplay;
            MergePreviewModeLabel = squash ? "Squash" : noFf ? "No Fast-Forward" : "Default";

            MergePreviewCommits.Clear();
            MergePreviewFiles.Clear();

            var commitsTask = _gitService.GetCommitsBetweenAsync(RepositoryPath, "HEAD", targetRef);
            var filesTask = _gitService.GetDiffStatBetweenAsync(RepositoryPath, "HEAD", targetRef);
            await Task.WhenAll(commitsTask, filesTask);

            foreach (var commit in await commitsTask) MergePreviewCommits.Add(commit);
            foreach (var file in await filesTask) MergePreviewFiles.Add(file);

            IsMergePreviewDialogOpen = true;
        }

        [RelayCommand]
        public void CancelMergePreviewDialog()
        {
            IsMergePreviewDialogOpen = false;
            _pendingMergeTargetRef = null;
        }

        [RelayCommand]
        public async Task ConfirmMergeAsync()
        {
            var targetRef = _pendingMergeTargetRef;
            IsMergePreviewDialogOpen = false;
            if (string.IsNullOrEmpty(targetRef) || string.IsNullOrEmpty(RepositoryPath)) return;

            // Local-only - no busy overlay, same as commit/reset/cherry-pick/revert.
            StatusMessage = $"Merging {targetRef} into {CurrentBranch}...";

            var (success, output) = await _gitService.MergeAsync(RepositoryPath, targetRef, _pendingMergeSquash, _pendingMergeNoFf);
            _pendingMergeTargetRef = null;

            if (success)
            {
                // A merge can introduce any number of commits from the other side - not safe to
                // splice locally like a single new commit, so the graph is the one thing left
                // stale here until F5/AutoRefresh. Working tree and pills still update.
                await RefreshPillsAndWorkingChangesAsync();
                StatusMessage = "Merged successfully.";
                return;
            }

            var conflicts = await _gitService.GetConflictedFilesAsync(RepositoryPath);
            if (conflicts.Count > 0)
            {
                _conflictIsRebase = false;
                MergeConflictDialogTitle = "⚠ Merge Conflicts";
                ContinueMergeButtonLabel = "Continue Merge";
                AbortMergeButtonLabel = "Abort Merge";

                MergeConflictFiles.Clear();
                foreach (var conflict in conflicts) MergeConflictFiles.Add(conflict);
                SelectedConflictFile = MergeConflictFiles.FirstOrDefault();
                CanContinueMerge = false;
                IsMergeConflictDialogOpen = true;
                StatusMessage = $"Merge stopped - {conflicts.Count} file(s) have conflicts.";
            }
            else
            {
                MessageBox.Show($"Merge failed:\n{output}", "Merge Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            await RefreshPillsAndWorkingChangesAsync();
        }

        private async Task LoadConflictDiffsAsync(MergeConflictFile? file)
        {
            OursDiff.SetDiff("");
            TheirsDiff.SetDiff("");
            if (file == null || string.IsNullOrEmpty(RepositoryPath)) return;

            var (oursDiff, theirsDiff) = await _gitService.GetConflictDiffsAsync(RepositoryPath, file.Path);
            OursDiff.SetDiff(oursDiff);
            TheirsDiff.SetDiff(theirsDiff);
        }

        // Overridable so tests can auto-confirm without popping a real MessageBox - see
        // WorkingChangesViewModel.ConfirmStashAction for the same pattern.
        public Func<string, bool> ConfirmConfigureKDiff3Action { get; set; } =
            message => MessageBox.Show(message, "No Merge Tool Configured", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        // Overridable so tests that exercise the "declined kdiff3 offer" or "some other launch
        // failure" paths don't fall through to a real MessageBox.Show - see AIREADME.md #13:
        // a real, invisible-to-the-agent modal popped here on every such test run and is a
        // hang risk, not just noise.
        public Action<string> ReportMergeToolLaunchFailureAction { get; set; } =
            message => MessageBox.Show(message, "Open In Merge Tool", MessageBoxButton.OK, MessageBoxImage.Error);

        [RelayCommand]
        public async Task OpenConflictFileInEditorAsync(MergeConflictFile? file)
        {
            if (file == null || string.IsNullOrEmpty(RepositoryPath)) return;

            var (success, output) = await _gitService.LaunchMergeToolAsync(RepositoryPath, file.Path);

            // The single most common reason this fails at all: nothing is configured as
            // merge.tool - installing a tool like kdiff3 doesn't register it with git on its
            // own. Offer the one tool most people on Windows already have (or is what
            // GitExtensions itself is commonly pointed at) instead of just failing.
            //
            // The exact wording differs by git version: older git (the Perl/shell mergetool
            // script) says "'merge.tool' is not configured"; git 2.44+'s C builtin instead
            // prints just a usage synopsis ending in "Make sure a merge tool is configured
            // (git config merge.tool)." - match both so this still fires on current git.
            if (!success && (output.Contains("'merge.tool' is not configured") || output.Contains("Make sure a merge tool is configured")) &&
                ConfirmConfigureKDiff3Action("No merge tool is configured yet.\n\nConfigure kdiff3 as your merge tool and try again?"))
            {
                var (configured, configureOutput) = await _gitService.ConfigureKDiff3AsMergeToolAsync(RepositoryPath);
                if (!configured)
                {
                    MessageBox.Show(configureOutput, "Configure Merge Tool", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                (success, output) = await _gitService.LaunchMergeToolAsync(RepositoryPath, file.Path);
            }

            if (!success)
            {
                ReportMergeToolLaunchFailureAction(
                    $"Could not launch your merge tool for '{file.Path}':\n{output}\n\nMake sure a merge tool is configured (git config merge.tool).");
                return;
            }

            // The tool blocks until closed and may have resolved the conflict (or not) -
            // refresh the Ours/Theirs preview so it reflects whatever's on disk now instead of
            // showing the stale pre-merge-tool diff.
            if (SelectedConflictFile == file)
            {
                await LoadConflictDiffsAsync(file);
            }
        }

        // Always available regardless of git state - the merge/rebase this dialog was tracking
        // may have already been finished or aborted outside Girt (another tool, another
        // terminal), in which case Abort/Continue would just fail against a merge that no
        // longer exists and leave the dialog stuck with no way out.
        [RelayCommand]
        public void CloseMergeConflictDialog() => IsMergeConflictDialogOpen = false;

        [RelayCommand]
        public async Task MarkConflictFileResolvedAsync(MergeConflictFile? file)
        {
            if (file == null || string.IsNullOrEmpty(RepositoryPath)) return;

            var (success, output) = await _gitService.StageFileAsync(RepositoryPath, file.Path);
            if (success)
            {
                file.IsResolved = true;
                CanContinueMerge = MergeConflictFiles.Count > 0 && MergeConflictFiles.All(f => f.IsResolved);
            }
            else
            {
                MessageBox.Show($"Could not stage '{file.Path}':\n{output}", "Mark Resolved", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        public async Task ContinueMergeAsync()
        {
            if (!CanContinueMerge || string.IsNullOrEmpty(RepositoryPath)) return;

            var (success, output) = _conflictIsRebase
                ? await _gitService.ContinueRebaseAsync(RepositoryPath)
                : await _gitService.ContinueMergeAsync(RepositoryPath);

            if (success)
            {
                IsMergeConflictDialogOpen = false;
                await RefreshPillsAndWorkingChangesAsync();
                StatusMessage = _conflictIsRebase ? "Rebase completed." : "Merge completed.";
                return;
            }

            // A rebase replays one commit at a time - the very next one can conflict again, so
            // re-check instead of treating this as a hard failure, or a multi-commit conflicted
            // rebase would dead-end after its first file is resolved.
            var conflicts = await _gitService.GetConflictedFilesAsync(RepositoryPath);
            if (conflicts.Count > 0)
            {
                MergeConflictFiles.Clear();
                foreach (var conflict in conflicts) MergeConflictFiles.Add(conflict);
                SelectedConflictFile = MergeConflictFiles.FirstOrDefault();
                CanContinueMerge = false;
                StatusMessage = $"{(_conflictIsRebase ? "Rebase" : "Merge")} stopped - {conflicts.Count} file(s) have conflicts.";
            }
            else
            {
                MessageBox.Show(
                    $"Could not complete the {(_conflictIsRebase ? "rebase" : "merge")}:\n{output}",
                    _conflictIsRebase ? "Continue Rebase" : "Continue Merge",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        public async Task AbortMergeAsync()
        {
            if (string.IsNullOrEmpty(RepositoryPath)) return;

            var opLabel = _conflictIsRebase ? "rebase" : "merge";
            var result = MessageBox.Show(
                $"Abort the {opLabel} and discard all conflict resolution progress?\n\nYour branch will be restored to how it was before the {opLabel} started.",
                _conflictIsRebase ? "Abort Rebase" : "Abort Merge",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            var (success, output) = _conflictIsRebase
                ? await _gitService.AbortRebaseAsync(RepositoryPath)
                : await _gitService.AbortMergeAsync(RepositoryPath);

            if (success)
            {
                IsMergeConflictDialogOpen = false;
                await RefreshPillsAndWorkingChangesAsync();
                StatusMessage = _conflictIsRebase ? "Rebase aborted." : "Merge aborted.";
            }
            else
            {
                MessageBox.Show(
                    $"Could not abort the {opLabel}:\n{output}",
                    _conflictIsRebase ? "Abort Rebase" : "Abort Merge",
                    MessageBoxButton.OK, MessageBoxImage.Error);
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
                // (hash/author/date/parent), which PrependLocalCommitAsync splices straight into
                // the already-loaded graph. No branches/working-changes/full-log reload at all.
                var latest = await _gitService.GetCommitsAsync(RepositoryPath, maxCount: 1);
                if (latest.Count > 0)
                {
                    await CommitHistory.PrependLocalCommitAsync(latest[0]);
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
