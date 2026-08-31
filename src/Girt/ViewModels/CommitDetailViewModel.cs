using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Girt.Models;
using Girt.Services;

namespace Girt.ViewModels
{
    public partial class CommitDetailViewModel : ObservableObject
    {
        private readonly IGitService _gitService;
        private readonly Func<string> _getRepoPath;

        [ObservableProperty]
        private GitCommit? _commit;

        [ObservableProperty]
        private GitFileDiff? _selectedFile;

        [ObservableProperty]
        private bool _isLoadingFiles;

        [ObservableProperty]
        private bool _isLoadingDiff;

        public ObservableCollection<GitFileDiff> ChangedFiles { get; } = new();
        public ObservableCollection<DiffLine> DiffLines { get; } = new();

        public CommitDetailViewModel(IGitService gitService, Func<string> getRepoPath)
        {
            _gitService = gitService;
            _getRepoPath = getRepoPath;
        }

        public async Task SetCommitAsync(GitCommit? commit)
        {
            Commit = commit;
            ChangedFiles.Clear();
            DiffLines.Clear();
            SelectedFile = null;

            if (commit == null) return;

            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            IsLoadingFiles = true;
            try
            {
                var files = await _gitService.GetCommitDiffAsync(repoPath, commit.Hash);
                foreach (var file in files)
                {
                    ChangedFiles.Add(file);
                }

                if (ChangedFiles.Count > 0)
                {
                    SelectedFile = ChangedFiles[0];
                }
            }
            finally
            {
                IsLoadingFiles = false;
            }
        }

        partial void OnSelectedFileChanged(GitFileDiff? value)
        {
            _ = LoadFileDiffAsync(value);
        }

        private async Task LoadFileDiffAsync(GitFileDiff? file)
        {
            DiffLines.Clear();
            if (file == null || Commit == null) return;

            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            IsLoadingDiff = true;
            try
            {
                var rawDiff = await _gitService.GetRawFileDiffAsync(repoPath, Commit.Hash, file.Path);
                var parsedLines = await Task.Run(() => DiffParser.ParseUnifiedDiff(rawDiff));

                foreach (var line in parsedLines)
                {
                    DiffLines.Add(line);
                }
            }
            finally
            {
                IsLoadingDiff = false;
            }
        }

        [CommunityToolkit.Mvvm.Input.RelayCommand]
        public async Task AddToGitIgnoreAsync(GitFileDiff? file)
        {
            file ??= SelectedFile;
            if (file == null) return;

            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            var (success, msg) = await _gitService.AddToGitIgnoreAsync(repoPath, file.Path, ignoreByExtension: false);
            if (success)
            {
                MessageBox.Show($"Added '{file.Path}' to .gitignore", ".gitignore Updated", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        [CommunityToolkit.Mvvm.Input.RelayCommand]
        public async Task IgnoreExtensionAsync(GitFileDiff? file)
        {
            file ??= SelectedFile;
            if (file == null) return;

            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            var (success, msg) = await _gitService.AddToGitIgnoreAsync(repoPath, file.Path, ignoreByExtension: true);
            if (success)
            {
                var ext = System.IO.Path.GetExtension(file.Path);
                MessageBox.Show($"Added '*{ext}' to .gitignore", ".gitignore Updated", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
