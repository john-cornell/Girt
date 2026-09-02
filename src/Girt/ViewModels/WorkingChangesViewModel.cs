using System;
using System.Collections.ObjectModel;
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
            Action<bool> savePushAfterCommit)
        {
            _gitService = gitService;
            _getRepoPath = getRepoPath;
            _onRepositoryUpdated = onRepositoryUpdated;
            _savePushAfterCommit = savePushAfterCommit;
            _pushAfterCommit = initialPushAfterCommit;

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

        private async Task LoadFileDiffAsync(GitWorkingFile? file)
        {
            DiffLines.Clear();
            if (file == null) return;

            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            var rawDiff = await _gitService.GetWorkingTreeFileDiffAsync(repoPath, file.Path, file.IsStaged);
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
            StagedFiles.Add(file);

            var (success, output) = await _gitService.StageFileAsync(repoPath, file.Path);
            if (success)
            {
                await _onRepositoryUpdated(false);
            }
            else
            {
                StagedFiles.Remove(file);
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
            UnstagedFiles.Add(file);

            var (success, output) = await _gitService.UnstageFileAsync(repoPath, file.Path);
            if (success)
            {
                await _onRepositoryUpdated(false);
            }
            else
            {
                UnstagedFiles.Remove(file);
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
            UnstagedFiles.Clear();
            foreach (var f in moved)
            {
                f.IsStaged = true;
                if (f.Status == FileStatusType.Untracked) f.Status = FileStatusType.Added;
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
            StagedFiles.Clear();
            foreach (var f in moved)
            {
                f.IsStaged = false;
                if (f.Status == FileStatusType.Added) f.Status = FileStatusType.Untracked;
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
                    f.IsStaged = true;
                    f.Status = originalStatuses[f];
                    StagedFiles.Add(f);
                }
                MessageBox.Show($"Failed to unstage all:\n{output}", "Unstage Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        public async Task DiscardChangesAsync(GitWorkingFile? file)
        {
            file ??= SelectedFile;
            if (file == null) return;

            var result = MessageBox.Show(
                $"Are you sure you want to discard changes in '{file.Path}'?\nThis cannot be undone.",
                "Discard Changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

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
                MessageBox.Show("There are no staged changes to stash. Stage files first.", "No Staged Changes", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
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
                MessageBox.Show("There are no staged changes to commit. Stage files first.", "No Staged Changes", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
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
