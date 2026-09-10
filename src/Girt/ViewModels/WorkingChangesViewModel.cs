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

namespace Girt.ViewModels
{
    public partial class WorkingChangesViewModel : ObservableObject, IDiffLineHost
    {
        private readonly IGitService _gitService;
        private readonly Func<string> _getRepoPath;
        private readonly Func<bool, Task> _onRepositoryUpdated;
        private readonly Action<bool> _savePushAfterCommit;
        private readonly Func<bool> _getIgnoreWhitespace;

        [ObservableProperty]
        private GitWorkingFile? _selectedFile;

        [ObservableProperty]
        private string _commitMessage = string.Empty;

        [ObservableProperty]
        private int _stashCount;

        [ObservableProperty]
        private bool _hasStashes;

        public string CommitSubject
        {
            get => CommitMessage;
            set => CommitMessage = value;
        }

        public string CommitDescription
        {
            get => string.Empty;
            set
            {
                if (!string.IsNullOrEmpty(value))
                {
                    CommitMessage = string.IsNullOrEmpty(CommitMessage) ? value : $"{CommitMessage}\n\n{value}";
                }
            }
        }

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private bool _pushAfterCommit;

        public ObservableCollection<GitWorkingFile> StagedFiles { get; } = new();
        public ObservableCollection<GitWorkingFile> UnstagedFiles { get; } = new();
        public ObservableCollection<DiffLine> DiffLines { get; } = new();

        public int TotalChangesCount => StagedFiles.Count + UnstagedFiles.Count;
        public bool HasStagedFiles => StagedFiles.Count > 0;
        public bool HasUnstagedFiles => UnstagedFiles.Count > 0;

        public WorkingChangesViewModel(
            IGitService gitService,
            Func<string> getRepoPath,
            Func<bool, Task> onRepositoryUpdated,
            bool initialPushAfterCommit,
            Action<bool> savePushAfterCommit,
            Func<bool>? getIgnoreWhitespace = null)
        {
            _gitService = gitService;
            _getRepoPath = getRepoPath;
            _onRepositoryUpdated = onRepositoryUpdated;
            _savePushAfterCommit = savePushAfterCommit;
            _pushAfterCommit = initialPushAfterCommit;
            _getIgnoreWhitespace = getIgnoreWhitespace ?? (() => false);

            // TotalChangesCount/HasStagedFiles/HasUnstagedFiles are computed from these two
            // collections but raise no notification of their own - they used to only update
            // via an explicit OnPropertyChanged call inside LoadChangesAsync, which meant every
            // optimistic mutation elsewhere (stage/unstage/discard/commit moving files between
            // the lists directly, without a reload) left them stale: the lists themselves were
            // correct, but the "N" header bound to TotalChangesCount kept showing whatever it
            // was before. Subscribing here catches every mutation path automatically.
            StagedFiles.CollectionChanged += (_, _) => NotifyChangeCountsUpdated();
            UnstagedFiles.CollectionChanged += (_, _) => NotifyChangeCountsUpdated();
        }

        private void NotifyChangeCountsUpdated()
        {
            OnPropertyChanged(nameof(TotalChangesCount));
            OnPropertyChanged(nameof(HasStagedFiles));
            OnPropertyChanged(nameof(HasUnstagedFiles));
        }

        partial void OnPushAfterCommitChanged(bool value)
        {
            _savePushAfterCommit(value);
        }

        public async Task LoadChangesAsync()
        {
            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            IsLoading = true;
            try
            {
                var changesTask = _gitService.GetWorkingTreeChangesAsync(repoPath);
                var stashCountTask = _gitService.GetStashCountAsync(repoPath);

                await Task.WhenAll(changesTask, stashCountTask);

                var changes = await changesTask;
                StashCount = await stashCountTask;
                HasStashes = StashCount > 0;
                
                StagedFiles.Clear();
                foreach (var f in changes.StagedFiles) StagedFiles.Add(f);

                UnstagedFiles.Clear();
                foreach (var f in changes.UnstagedFiles) UnstagedFiles.Add(f);

                if (SelectedFile != null)
                {
                    await LoadFileDiffAsync(SelectedFile);
                }
                else if (UnstagedFiles.Count > 0)
                {
                    SelectedFile = UnstagedFiles[0];
                }
                else if (StagedFiles.Count > 0)
                {
                    SelectedFile = StagedFiles[0];
                }
                else
                {
                    DiffLines.Clear();
                }
            }
            finally
            {
                IsLoading = false;
            }
        }

        partial void OnSelectedFileChanged(GitWorkingFile? value)
        {
            _ = LoadFileDiffAsync(value);
        }

        // Public so toggling "Ignore Whitespace Changes" can re-fetch the currently displayed
        // diff immediately instead of only applying the next time a file is clicked.
        public Task RefreshDiffAsync() => LoadFileDiffAsync(SelectedFile);

        private async Task LoadFileDiffAsync(GitWorkingFile? file)
        {
            DiffLines.Clear();
            if (file == null) return;

            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            var rawDiff = await _gitService.GetWorkingTreeFileDiffAsync(repoPath, file.Path, file.IsStaged, _getIgnoreWhitespace());
            var lines = await Task.Run(() => DiffParser.ParseUnifiedDiff(rawDiff));

            foreach (var l in lines)
            {
                DiffLines.Add(l);
            }
        }

        public void ToggleDiffSection(DiffLine? line)
        {
            DiffParser.ToggleCollapsedSection(DiffLines, line);
        }

        public void ExpandAllDiffSections()
        {
            DiffParser.ExpandAllCollapsedSections(DiffLines);
        }

        // Line-level revert only makes sense against the unstaged diff (working tree vs index) -
        // reverting a selected line there means "make the working tree match the index again for
        // just this line". A staged file's diff is index-vs-HEAD, where "revert" would mean
        // something different (unstage just that line), which isn't implemented here. Selection
        // can include Context lines too (useful for copying a range), so this specifically
        // requires a selected Added/Deleted line - selecting only unchanged context has nothing
        // to revert.
        private bool HasRevertableSelection => DiffLines.Any(l => l.IsSelected && l.Type is DiffLineType.Added or DiffLineType.Deleted);

        public bool CanRevertSelectedLines => SelectedFile is { IsStaged: false } && HasRevertableSelection;

        [RelayCommand]
        public async Task RevertSelectedLinesAsync()
        {
            if (SelectedFile == null || SelectedFile.IsStaged) return;
            if (!HasRevertableSelection) return;

            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            var fullPath = Path.Combine(repoPath, SelectedFile.Path);
            string existingContent;
            try
            {
                existingContent = await File.ReadAllTextAsync(fullPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not read '{SelectedFile.Path}':\n{ex.Message}", "Revert Selected Lines", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var newline = existingContent.Contains("\r\n") ? "\r\n" : "\n";
            var endsWithNewline = existingContent.EndsWith(newline);
            var newContent = DiffParser.ReconstructFileContent(DiffLines, newline, endsWithNewline);

            var (success, output) = await _gitService.WriteWorkingTreeFileAsync(repoPath, SelectedFile.Path, newContent);
            if (success)
            {
                // The file may now be fully clean (drops out of UnstagedFiles) or still dirty
                // with a smaller diff - a full reload is the only way to know which, unlike the
                // optimistic list moves used by Stage/Unstage above.
                await LoadChangesAsync();
                await _onRepositoryUpdated(false);
            }
            else
            {
                MessageBox.Show($"Failed to revert selected lines:\n{output}", "Revert Selected Lines", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // git status reports at most one line per path per side (staged/unstaged), but Girt
        // models a file that's partly staged and partly still dirty as two separate
        // GitWorkingFile instances - one per side. Moving one of those instances into a list
        // that already holds the other (e.g. staging the unstaged half of an "MM" file) would
        // otherwise leave two rows for the same path. Collapse to one row, like GitExtensions.
        private static GitWorkingFile? RemoveExistingEntry(ObservableCollection<GitWorkingFile> collection, GitWorkingFile file)
        {
            var existing = collection.FirstOrDefault(f => f.Path == file.Path);
            if (existing != null) collection.Remove(existing);
            return existing;
        }

        [RelayCommand]
        public async Task StageFileAsync(GitWorkingFile? file)
        {
            file ??= SelectedFile;
            if (file == null) return;

            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            // Optimistic: staging a file that's already listed as changed practically never
            // fails, and this is the single most-clicked action in this panel - move it across
            // immediately instead of waiting on a git round-trip + full re-list, and roll back
            // only if the git command actually fails.
            var originalStatus = file.Status;
            UnstagedFiles.Remove(file);
            file.IsStaged = true;
            if (file.Status == FileStatusType.Untracked) file.Status = FileStatusType.Added;
            var displacedStaged = RemoveExistingEntry(StagedFiles, file);
            StagedFiles.Add(file);

            var (success, output) = await _gitService.StageFileAsync(repoPath, file.Path);
            if (success)
            {
                await _onRepositoryUpdated(false);
            }
            else
            {
                StagedFiles.Remove(file);
                if (displacedStaged != null) StagedFiles.Add(displacedStaged);
                file.IsStaged = false;
                file.Status = originalStatus;
                UnstagedFiles.Add(file);
                MessageBox.Show($"Failed to stage file:\n{output}", "Stage Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        public async Task UnstageFileAsync(GitWorkingFile? file)
        {
            file ??= SelectedFile;
            if (file == null) return;

            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            var originalStatus = file.Status;
            StagedFiles.Remove(file);
            file.IsStaged = false;
            if (file.Status == FileStatusType.Added) file.Status = FileStatusType.Untracked;
            var displacedUnstaged = RemoveExistingEntry(UnstagedFiles, file);
            UnstagedFiles.Add(file);

            var (success, output) = await _gitService.UnstageFileAsync(repoPath, file.Path);
            if (success)
            {
                await _onRepositoryUpdated(false);
            }
            else
            {
                UnstagedFiles.Remove(file);
                if (displacedUnstaged != null) UnstagedFiles.Add(displacedUnstaged);
                file.IsStaged = true;
                file.Status = originalStatus;
                StagedFiles.Add(file);
                MessageBox.Show($"Failed to unstage file:\n{output}", "Unstage Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        public async Task StageAllAsync()
        {
            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;
            if (UnstagedFiles.Count == 0) return;

            var moved = UnstagedFiles.ToList();
            var originalStatuses = moved.ToDictionary(f => f, f => f.Status);
            var displacedStaged = new Dictionary<GitWorkingFile, GitWorkingFile?>();
            UnstagedFiles.Clear();
            foreach (var f in moved)
            {
                f.IsStaged = true;
                if (f.Status == FileStatusType.Untracked) f.Status = FileStatusType.Added;
                displacedStaged[f] = RemoveExistingEntry(StagedFiles, f);
                StagedFiles.Add(f);
            }

            var (success, output) = await _gitService.StageAllAsync(repoPath);
            if (success)
            {
                await _onRepositoryUpdated(false);
            }
            else
            {
                foreach (var f in moved)
                {
                    StagedFiles.Remove(f);
                    if (displacedStaged[f] != null) StagedFiles.Add(displacedStaged[f]!);
                    f.IsStaged = false;
                    f.Status = originalStatuses[f];
                    UnstagedFiles.Add(f);
                }
                MessageBox.Show($"Failed to stage all:\n{output}", "Stage Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        public async Task UnstageAllAsync()
        {
            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;
            if (StagedFiles.Count == 0) return;

            var moved = StagedFiles.ToList();
            var originalStatuses = moved.ToDictionary(f => f, f => f.Status);
            var displacedUnstaged = new Dictionary<GitWorkingFile, GitWorkingFile?>();
            StagedFiles.Clear();
            foreach (var f in moved)
            {
                f.IsStaged = false;
                if (f.Status == FileStatusType.Added) f.Status = FileStatusType.Untracked;
                displacedUnstaged[f] = RemoveExistingEntry(UnstagedFiles, f);
                UnstagedFiles.Add(f);
            }

            var (success, output) = await _gitService.UnstageAllAsync(repoPath);
            if (success)
            {
                await _onRepositoryUpdated(false);
            }
            else
            {
                foreach (var f in moved)
                {
                    UnstagedFiles.Remove(f);
                    if (displacedUnstaged[f] != null) UnstagedFiles.Add(displacedUnstaged[f]!);
                    f.IsStaged = true;
                    f.Status = originalStatuses[f];
                    StagedFiles.Add(f);
                }
                MessageBox.Show($"Failed to unstage all:\n{output}", "Unstage Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Overridable so tests can auto-confirm without popping a real MessageBox - see
        // ConfirmStashAction for the same pattern, and AIREADME.md #13 for why this matters (a
        // command hitting a real, non-injectable MessageBox.Show under the test host is a
        // genuine hang risk, not just "slow").
        public Func<string, bool> ConfirmDiscardAction { get; set; } =
            message => MessageBox.Show(message, "Discard Changes", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        [RelayCommand]
        public async Task DiscardChangesAsync(GitWorkingFile? file)
        {
            file ??= SelectedFile;
            if (file == null) return;

            if (!ConfirmDiscardAction($"Are you sure you want to discard changes in '{file.Path}'?\nThis cannot be undone.")) return;

            await DiscardChangesNoConfirmAsync(file);
        }

        private async Task DiscardChangesNoConfirmAsync(GitWorkingFile file)
        {
            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            // Optimistic: discarding removes the file from view either way (reverted to a
            // clean/untracked-deleted state), so drop it immediately and restore it if the
            // discard actually fails.
            UnstagedFiles.Remove(file);

            var (success, error) = await _gitService.DiscardChangesAsync(repoPath, file.Path);
            if (success)
            {
                await _onRepositoryUpdated(false);
            }
            else
            {
                UnstagedFiles.Add(file);
                MessageBox.Show($"Failed to discard changes: {error}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Multi-select bulk actions (see MainWindow.xaml.cs's ResolveSelectedWorkingFiles) -
        // GitExtensions' own file list supports selecting several files and staging/discarding
        // them together instead of one at a time.
        [RelayCommand]
        public async Task StageSelectedFilesAsync(List<GitWorkingFile>? files)
        {
            if (files == null || files.Count == 0) return;
            foreach (var file in files)
            {
                await StageFileAsync(file);
            }
        }

        [RelayCommand]
        public async Task UnstageSelectedFilesAsync(List<GitWorkingFile>? files)
        {
            if (files == null || files.Count == 0) return;
            foreach (var file in files)
            {
                await UnstageFileAsync(file);
            }
        }

        [RelayCommand]
        public async Task DiscardSelectedFilesAsync(List<GitWorkingFile>? files)
        {
            if (files == null || files.Count == 0) return;

            // One confirmation for the whole batch, not one per file.
            if (!ConfirmDiscardAction($"Are you sure you want to discard changes in {files.Count} file(s)?\nThis cannot be undone.")) return;

            foreach (var file in files)
            {
                await DiscardChangesNoConfirmAsync(file);
            }
        }

        [RelayCommand]
        public async Task AddToGitIgnoreAsync(GitWorkingFile? file)
        {
            file ??= SelectedFile;
            if (file == null) return;

            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            var (success, msg) = await _gitService.AddToGitIgnoreAsync(repoPath, file.Path, GitIgnoreTarget.File);
            if (success)
            {
                await LoadChangesAsync();
                await _onRepositoryUpdated(false);
            }
            else
            {
                MessageBox.Show($"Failed to update .gitignore: {msg}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        public async Task IgnoreExtensionAsync(GitWorkingFile? file)
        {
            file ??= SelectedFile;
            if (file == null) return;

            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            var (success, msg) = await _gitService.AddToGitIgnoreAsync(repoPath, file.Path, GitIgnoreTarget.Extension);
            if (success)
            {
                await LoadChangesAsync();
                await _onRepositoryUpdated(false);
            }
            else
            {
                MessageBox.Show($"Failed to update .gitignore: {msg}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        public async Task IgnoreFolderAsync(string? folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;

            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            var (success, msg) = await _gitService.AddToGitIgnoreAsync(repoPath, folderPath, GitIgnoreTarget.Folder);
            if (success)
            {
                await LoadChangesAsync();
                await _onRepositoryUpdated(false);
            }
            else
            {
                MessageBox.Show($"Failed to update .gitignore: {msg}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        public async Task StashStagedAsync()
        {
            if (StagedFiles.Count == 0)
            {
                if (UnstagedFiles.Count == 0)
                {
                    MessageBox.Show("There are no changes to stash.", "No Staged Changes", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (!ConfirmStageAllFirstAction("There are no staged changes to stash yet.\n\nStage all changes now and stash them?"))
                {
                    return;
                }

                await StageAllAsync();
                if (StagedFiles.Count == 0) return; // Stage All failed - it already reported why.
            }

            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            var message = string.IsNullOrWhiteSpace(CommitMessage)
                ? $"Staged changes ({DateTime.Now:yyyy-MM-dd HH:mm:ss})"
                : CommitMessage.Trim();

            IsLoading = true;
            try
            {
                var (success, output) = await _gitService.StashStagedAsync(repoPath, message);
                if (success)
                {
                    await LoadChangesAsync();
                    await _onRepositoryUpdated(false);
                }
                else
                {
                    MessageBox.Show($"Stash failed:\n{output}", "Stash Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                IsLoading = false;
            }
        }

        // Overridable so tests can auto-confirm without popping a real MessageBox; production
        // code never sets this and gets the real Yes/No dialog.
        public Func<string, bool> ConfirmStashAction { get; set; } =
            message => MessageBox.Show(message, "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        // Same pattern as ConfirmStashAction/ConfirmNothingToPushAction (MainViewModel) - shown
        // by Commit/Stash Staged when there's nothing staged but there are unstaged changes to
        // offer staging, instead of just telling the user to go stage things themselves first.
        public Func<string, bool> ConfirmStageAllFirstAction { get; set; } =
            message => MessageBox.Show(message, "No Staged Changes", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        [RelayCommand]
        public async Task StashPopAsync()
        {
            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            // Pop applies the top stash to whatever branch is currently checked out - if that
            // stash was made on a different branch (the common case with several stashes
            // stacked up), it silently lands changes meant for elsewhere. Show what's about to
            // be applied before doing it, same as Discard's confirmation.
            var topStash = await _gitService.GetTopStashDescriptionAsync(repoPath);
            var message = topStash != null
                ? $"Pop the top stash onto the current working tree?\n\n{topStash}\n\nThis applies it and removes it from the stash list."
                : "Pop the top stash onto the current working tree?";
            if (!ConfirmStashAction(message)) return;

            IsLoading = true;
            try
            {
                var (success, output) = await _gitService.StashPopAsync(repoPath);
                if (success)
                {
                    await LoadChangesAsync();
                    await _onRepositoryUpdated(false);
                }
                else
                {
                    MessageBox.Show($"Pop stash failed:\n{output}", "Pop Stash Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        public async Task StashApplyAsync()
        {
            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            var topStash = await _gitService.GetTopStashDescriptionAsync(repoPath);
            var message = topStash != null
                ? $"Apply the top stash onto the current working tree?\n\n{topStash}\n\nThe stash stays in the list afterwards."
                : "Apply the top stash onto the current working tree?";
            if (!ConfirmStashAction(message)) return;

            IsLoading = true;
            try
            {
                var (success, output) = await _gitService.StashApplyAsync(repoPath);
                if (success)
                {
                    await LoadChangesAsync();
                    await _onRepositoryUpdated(false);
                }
                else
                {
                    MessageBox.Show($"Apply stash failed:\n{output}", "Apply Stash Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        public async Task CommitAsync()
        {
            var message = CommitMessage?.Trim();
            if (string.IsNullOrEmpty(message))
            {
                MessageBox.Show("Please enter a commit message.", "Commit Message Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (StagedFiles.Count == 0)
            {
                if (UnstagedFiles.Count == 0)
                {
                    MessageBox.Show("There are no changes to commit.", "No Staged Changes", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!ConfirmStageAllFirstAction("There are no staged changes to commit yet.\n\nStage all changes now and commit them?"))
                {
                    return;
                }

                await StageAllAsync();
                if (StagedFiles.Count == 0) return; // Stage All failed - it already reported why.
            }

            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            IsLoading = true;
            try
            {
                var (success, output) = await _gitService.CommitAsync(repoPath, message);
                if (success)
                {
                    // Optimistic: a commit commits exactly what was staged, so assume success
                    // and clear the message/staged list immediately rather than waiting on
                    // LoadChangesAsync's round-trip to confirm what we already know. The
                    // heavier repository-wide refresh runs after, and silently (see
                    // MainViewModel.OnWorkingChangesUpdatedAsync) so it doesn't block the UI.
                    CommitMessage = string.Empty;
                    StagedFiles.Clear();

                    await LoadChangesAsync();
                    await _onRepositoryUpdated(true); // Is new commit
                }
                else
                {
                    MessageBox.Show($"Commit failed:\n{output}", "Commit Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                IsLoading = false;
            }
        }
    }
}
