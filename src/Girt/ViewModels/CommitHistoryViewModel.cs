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
    public partial class CommitHistoryViewModel : ObservableObject
    {
        private readonly IGitService _gitService;
        private readonly Func<string> _getRepoPath;
        private readonly Action<GitCommit?> _onCommitSelected;

        [ObservableProperty]
        private string _filterSubject = string.Empty;

        [ObservableProperty]
        private string _filterAuthor = string.Empty;

        [ObservableProperty]
        private string _filterDate = string.Empty;

        [ObservableProperty]
        private string _filterSha = string.Empty;

        [ObservableProperty]
        private GitCommit? _selectedCommit;

        private List<GitCommit> _allCommits = new();

        public ObservableCollection<GitCommit> FilteredCommits { get; } = new();

        public CommitHistoryViewModel(IGitService gitService, Func<string> getRepoPath, Action<GitCommit?> onCommitSelected)
        {
            _gitService = gitService;
            _getRepoPath = getRepoPath;
            _onCommitSelected = onCommitSelected;
        }

        partial void OnSelectedCommitChanged(GitCommit? value)
        {
            _onCommitSelected(value);
        }

        partial void OnFilterSubjectChanged(string value) => ApplyFilter();
        partial void OnFilterAuthorChanged(string value) => ApplyFilter();
        partial void OnFilterDateChanged(string value) => ApplyFilter();
        partial void OnFilterShaChanged(string value) => ApplyFilter();

        [RelayCommand]
        public void ClearFilters()
        {
            FilterSubject = string.Empty;
            FilterAuthor = string.Empty;
            FilterDate = string.Empty;
            FilterSha = string.Empty;
        }

        public async Task LoadCommitsAsync()
        {
            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            var commits = await _gitService.GetCommitsAsync(repoPath, maxCount: 1000);
            GitGraphLayoutEngine.ComputeGraphLayout(commits);

            _allCommits = commits.ToList();
            ApplyFilter();

            if (FilteredCommits.Count > 0 && SelectedCommit == null)
            {
                SelectedCommit = FilteredCommits[0];
            }
        }

        private void ApplyFilter()
        {
            FilteredCommits.Clear();

            var subjectQuery = FilterSubject?.Trim() ?? string.Empty;
            var authorQuery = FilterAuthor?.Trim() ?? string.Empty;
            var dateQuery = FilterDate?.Trim() ?? string.Empty;
            var shaQuery = FilterSha?.Trim() ?? string.Empty;

            var matches = _allCommits.Where(c =>
                (string.IsNullOrEmpty(subjectQuery) || c.Subject.Contains(subjectQuery, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrEmpty(authorQuery) || c.AuthorName.Contains(authorQuery, StringComparison.OrdinalIgnoreCase) || c.AuthorEmail.Contains(authorQuery, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrEmpty(dateQuery) || c.RelativeDate.Contains(dateQuery, StringComparison.OrdinalIgnoreCase) || c.Date.ToString("yyyy-MM-dd").Contains(dateQuery, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrEmpty(shaQuery) || c.ShortHash.Contains(shaQuery, StringComparison.OrdinalIgnoreCase) || c.Hash.Contains(shaQuery, StringComparison.OrdinalIgnoreCase)));

            foreach (var c in matches)
            {
                FilteredCommits.Add(c);
            }
        }
    }
}
