using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Girt.Models;
using Girt.Services;

namespace Girt.ViewModels
{
    /// <summary>Backs the "review before you push" dialog: the combined diff of everything
    /// reachable from HEAD but not yet on the upstream branch (@{u}..HEAD).</summary>
    public partial class UnpushedChangesViewModel : ObservableObject, IDiffLineHost
    {
        private readonly IGitService _gitService;
        private readonly Func<string> _getRepoPath;
        private readonly Func<bool> _getIgnoreWhitespace;

        [ObservableProperty]
        private bool _isOpen;

        [ObservableProperty]
        private GitFileDiff? _selectedFile;

        [ObservableProperty]
        private bool _isLoadingFiles;

        [ObservableProperty]
        private bool _isLoadingDiff;

        public ObservableCollection<GitFileDiff> ChangedFiles { get; } = new();
        public ObservableCollection<DiffLine> DiffLines { get; } = new();

        public UnpushedChangesViewModel(IGitService gitService, Func<string> getRepoPath, Func<bool>? getIgnoreWhitespace = null)
        {
            _gitService = gitService;
            _getRepoPath = getRepoPath;
            _getIgnoreWhitespace = getIgnoreWhitespace ?? (() => false);
        }

        [RelayCommand]
        public async Task OpenAsync()
        {
            IsOpen = true;
            ChangedFiles.Clear();
            DiffLines.Clear();
            SelectedFile = null;

            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            IsLoadingFiles = true;
            try
            {
                var files = await _gitService.GetUnpushedDiffAsync(repoPath);
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

        [RelayCommand]
        public void Close()
        {
            IsOpen = false;
        }

        partial void OnSelectedFileChanged(GitFileDiff? value)
        {
            _ = LoadFileDiffAsync(value);
        }

        // Public so toggling "Ignore Whitespace Changes" can re-fetch the currently displayed
        // diff immediately instead of only applying the next time a file is clicked.
        public Task RefreshDiffAsync() => LoadFileDiffAsync(SelectedFile);

        private async Task LoadFileDiffAsync(GitFileDiff? file)
        {
            DiffLines.Clear();
            if (file == null) return;

            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            IsLoadingDiff = true;
            try
            {
                var rawDiff = await _gitService.GetRawUnpushedFileDiffAsync(repoPath, file.Path, _getIgnoreWhitespace());
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

        public void ToggleDiffSection(DiffLine? line)
        {
            DiffParser.ToggleCollapsedSection(DiffLines, line);
        }

        public void ExpandAllDiffSections()
        {
            DiffParser.ExpandAllCollapsedSections(DiffLines);
        }
    }
}
