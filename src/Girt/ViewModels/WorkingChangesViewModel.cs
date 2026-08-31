using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Girt.Models;
using Girt.Services;

namespace Girt.ViewModels
{
    public partial class WorkingChangesViewModel : ObservableObject
    {
        private readonly IGitService _gitService;
        private readonly Func<string> _getRepoPath;
        private readonly Func<Task> _onRepositoryUpdated;

        [ObservableProperty]
        private GitWorkingFile? _selectedFile;

        [ObservableProperty]
        private string _commitSubject = string.Empty;

        [ObservableProperty]
        private string _commitDescription = string.Empty;

        [ObservableProperty]
        private bool _isLoading;

        public ObservableCollection<GitWorkingFile> StagedFiles { get; } = new();
        public ObservableCollection<GitWorkingFile> UnstagedFiles { get; } = new();
        public ObservableCollection<DiffLine> DiffLines { get; } = new();

        public int TotalChangesCount => StagedFiles.Count + UnstagedFiles.Count;
        public bool HasStagedFiles => StagedFiles.Count > 0;
        public bool HasUnstagedFiles => UnstagedFiles.Count > 0;

        public WorkingChangesViewModel(IGitService gitService, Func<string> getRepoPath, Func<Task> onRepositoryUpdated)
        {
            _gitService = gitService;
            _getRepoPath = getRepoPath;
            _onRepositoryUpdated = onRepositoryUpdated;
        }

        public async Task LoadChangesAsync()
        {
            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            IsLoading = true;
            try
            {
                var changes = await _gitService.GetWorkingTreeChangesAsync(repoPath);
                
                StagedFiles.Clear();
                foreach (var f in changes.StagedFiles) StagedFiles.Add(f);

                UnstagedFiles.Clear();
                foreach (var f in changes.UnstagedFiles) UnstagedFiles.Add(f);

                OnPropertyChanged(nameof(TotalChangesCount));
                OnPropertyChanged(nameof(HasStagedFiles));
                OnPropertyChanged(nameof(HasUnstagedFiles));

                if (SelectedFile != null)
                {
                    await LoadFileDiffAsync(SelectedFile);
                }
                else if (StagedFiles.Count > 0)
                {
                    SelectedFile = StagedFiles[0];
                }
                else if (UnstagedFiles.Count > 0)
                {
                    SelectedFile = UnstagedFiles[0];
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
            var lines = DiffParser.ParseUnifiedDiff(rawDiff);

            foreach (var l in lines)
            {
                DiffLines.Add(l);
            }
        }

        [RelayCommand]
        public async Task StageFileAsync(GitWorkingFile? file)
        {
            file ??= SelectedFile;
            if (file == null) return;

            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            var (success, _) = await _gitService.StageFileAsync(repoPath, file.Path);
            if (success)
            {
                await LoadChangesAsync();
                await _onRepositoryUpdated();
            }
        }

        [RelayCommand]
        public async Task UnstageFileAsync(GitWorkingFile? file)
        {
            file ??= SelectedFile;
            if (file == null) return;

            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            var (success, _) = await _gitService.UnstageFileAsync(repoPath, file.Path);
            if (success)
            {
                await LoadChangesAsync();
                await _onRepositoryUpdated();
            }
        }

        [RelayCommand]
        public async Task StageAllAsync()
        {
            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            var (success, _) = await _gitService.StageAllAsync(repoPath);
            if (success)
            {
                await LoadChangesAsync();
                await _onRepositoryUpdated();
            }
        }

        [RelayCommand]
        public async Task UnstageAllAsync()
        {
            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            var (success, _) = await _gitService.UnstageAllAsync(repoPath);
            if (success)
            {
                await LoadChangesAsync();
                await _onRepositoryUpdated();
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

            var (success, error) = await _gitService.DiscardChangesAsync(repoPath, file.Path);
            if (success)
            {
                await LoadChangesAsync();
                await _onRepositoryUpdated();
            }
            else
            {
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

            var (success, msg) = await _gitService.AddToGitIgnoreAsync(repoPath, file.Path, ignoreByExtension: false);
            if (success)
            {
                await LoadChangesAsync();
                await _onRepositoryUpdated();
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

            var (success, msg) = await _gitService.AddToGitIgnoreAsync(repoPath, file.Path, ignoreByExtension: true);
            if (success)
            {
                await LoadChangesAsync();
                await _onRepositoryUpdated();
            }
            else
            {
                MessageBox.Show($"Failed to update .gitignore: {msg}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        public async Task CommitAsync()
        {
            var subject = CommitSubject?.Trim();
            if (string.IsNullOrEmpty(subject))
            {
                MessageBox.Show("Please enter a commit message.", "Commit Message Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (StagedFiles.Count == 0)
            {
                MessageBox.Show("There are no staged changes to commit. Stage your files first.", "No Staged Changes", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            var fullMessage = string.IsNullOrEmpty(CommitDescription?.Trim())
                ? subject
                : $"{subject}\n\n{CommitDescription.Trim()}";

            var (success, output) = await _gitService.CommitAsync(repoPath, fullMessage);
            if (success)
            {
                CommitSubject = string.Empty;
                CommitDescription = string.Empty;
                await LoadChangesAsync();
                await _onRepositoryUpdated();
            }
            else
            {
                MessageBox.Show($"Commit failed:\n{output}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
