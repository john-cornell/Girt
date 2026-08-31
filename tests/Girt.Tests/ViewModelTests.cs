using System.Collections.Generic;
using System.Threading.Tasks;
using Girt.Models;
using Girt.Services;
using Girt.ViewModels;
using Xunit;

namespace Girt.Tests
{
    public class FakeGitService : IGitService
    {
        public List<GitBranch> Branches { get; set; } = new();
        public List<GitCommit> Commits { get; set; } = new();
        public List<GitFileDiff> DiffFiles { get; set; } = new();
        public string RawDiff { get; set; } = "";
        public string? CurrentBranch { get; set; } = "main";
        public string? RepoRoot { get; set; } = @"C:\FakeRepo";
        public GitRepoStatus Status { get; set; } = new() { UncommittedCount = 2, AheadCount = 1, BehindCount = 0 };
        public WorkingTreeChanges Changes { get; set; } = new();
        public string? LastResetTarget { get; set; }
        public GitResetMode? LastResetMode { get; set; }
        public string? LastCommitMessage { get; set; }

        public Task<string?> GetRepositoryRootAsync(string directoryPath) => Task.FromResult(RepoRoot);
        public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repoPath) => Task.FromResult<IReadOnlyList<GitBranch>>(Branches);
        public Task<IReadOnlyList<GitCommit>> GetCommitsAsync(string repoPath, int maxCount = 1000) => Task.FromResult<IReadOnlyList<GitCommit>>(Commits);
        public Task<IReadOnlyList<GitFileDiff>> GetCommitDiffAsync(string repoPath, string commitHash) => Task.FromResult<IReadOnlyList<GitFileDiff>>(DiffFiles);
        public Task<string> GetRawFileDiffAsync(string repoPath, string commitHash, string filePath) => Task.FromResult(RawDiff);
        public Task<GitRepoStatus> GetRepoStatusAsync(string repoPath) => Task.FromResult(Status);
        public Task<WorkingTreeChanges> GetWorkingTreeChangesAsync(string repoPath) => Task.FromResult(Changes);
        
        public Task<(bool Success, string Output)> CheckoutBranchAsync(string repoPath, string branchName)
        {
            CurrentBranch = branchName;
            return Task.FromResult((true, "Switched to branch"));
        }
        public Task<(bool Success, string Output)> CreateBranchAsync(string repoPath, string branchName, string? startPoint = null)
        {
            Branches.Add(new GitBranch { Name = branchName, IsCurrent = true });
            CurrentBranch = branchName;
            return Task.FromResult((true, "Created branch"));
        }
        public Task<(bool Success, string Output)> DeleteBranchAsync(string repoPath, string branchName, bool force = false)
        {
            Branches.RemoveAll(b => b.Name == branchName);
            return Task.FromResult((true, "Deleted branch"));
        }
        public Task<string?> GetCurrentBranchAsync(string repoPath) => Task.FromResult(CurrentBranch);

        public Task<(bool Success, string Output)> ResetHeadAsync(string repoPath, string targetRef, GitResetMode mode)
        {
            LastResetTarget = targetRef;
            LastResetMode = mode;
            return Task.FromResult((true, $"Reset to {targetRef}"));
        }

        public Task<(bool Success, string Output)> StageFileAsync(string repoPath, string filePath)
        {
            Changes.UnstagedFiles.RemoveAll(f => f.Path == filePath);
            Changes.StagedFiles.Add(new GitWorkingFile { Path = filePath, IsStaged = true });
            return Task.FromResult((true, "Staged"));
        }

        public Task<(bool Success, string Output)> UnstageFileAsync(string repoPath, string filePath)
        {
            Changes.StagedFiles.RemoveAll(f => f.Path == filePath);
            Changes.UnstagedFiles.Add(new GitWorkingFile { Path = filePath, IsStaged = false });
            return Task.FromResult((true, "Unstaged"));
        }

        public Task<(bool Success, string Output)> StageAllAsync(string repoPath)
        {
            foreach (var f in Changes.UnstagedFiles)
            {
                f.IsStaged = true;
                Changes.StagedFiles.Add(f);
            }
            Changes.UnstagedFiles.Clear();
            return Task.FromResult((true, "Staged all"));
        }

        public Task<(bool Success, string Output)> UnstageAllAsync(string repoPath)
        {
            foreach (var f in Changes.StagedFiles)
            {
                f.IsStaged = false;
                Changes.UnstagedFiles.Add(f);
            }
            Changes.StagedFiles.Clear();
            return Task.FromResult((true, "Unstaged all"));
        }

        public Task<(bool Success, string Output)> DiscardChangesAsync(string repoPath, string filePath)
        {
            Changes.UnstagedFiles.RemoveAll(f => f.Path == filePath);
            return Task.FromResult((true, "Discarded"));
        }

        public Task<(bool Success, string Output)> CommitAsync(string repoPath, string message)
        {
            LastCommitMessage = message;
            Changes.StagedFiles.Clear();
            return Task.FromResult((true, "Committed"));
        }

        public Task<string> GetWorkingTreeFileDiffAsync(string repoPath, string filePath, bool isStaged) => Task.FromResult(RawDiff);
        public Task<(bool Success, string Output)> PushAsync(string repoPath) => Task.FromResult((true, "Pushed"));
        public Task<(bool Success, string Output)> PullAsync(string repoPath) => Task.FromResult((true, "Pulled"));
        public Task<(bool Success, string Output)> AddToGitIgnoreAsync(string repoPath, string filePath, bool ignoreByExtension = false)
        {
            Changes.UnstagedFiles.RemoveAll(f => f.Path == filePath);
            return Task.FromResult((true, "Ignored"));
        }
    }

    public class ViewModelTests
    {
        [Fact]
        public async Task BranchListViewModel_FiltersBranchesCorrectly()
        {
            var fakeGit = new FakeGitService
            {
                Branches = new List<GitBranch>
                {
                    new() { Name = "main", IsRemote = false },
                    new() { Name = "feature/login", IsRemote = false },
                    new() { Name = "bugfix/issue-12", IsRemote = false },
                    new() { Name = "origin/main", IsRemote = true, RemoteName = "origin" }
                }
            };

            var vm = new BranchListViewModel(fakeGit, () => @"C:\FakeRepo", () => Task.CompletedTask);
            await vm.LoadBranchesAsync();

            Assert.Equal(3, vm.FilteredLocalBranches.Count);
            Assert.Single(vm.FilteredRemoteBranches);

            vm.FilterText = "login";
            Assert.Single(vm.FilteredLocalBranches);
            Assert.Equal("feature/login", vm.FilteredLocalBranches[0].Name);
            Assert.Empty(vm.FilteredRemoteBranches);
        }

        [Fact]
        public async Task CommitHistoryViewModel_FiltersByIndividualColumns()
        {
            var fakeGit = new FakeGitService
            {
                Commits = new List<GitCommit>
                {
                    new() { Hash = "1111111aaaaaaaa", Subject = "Add login page", AuthorName = "Alice", RelativeDate = "2 days ago" },
                    new() { Hash = "2222222bbbbbbbb", Subject = "Fix navbar styling", AuthorName = "Bob", RelativeDate = "yesterday" },
                    new() { Hash = "3333333cccccccc", Subject = "Update README", AuthorName = "Alice", RelativeDate = "3 hours ago" }
                }
            };

            var vm = new CommitHistoryViewModel(fakeGit, () => @"C:\FakeRepo", _ => { });
            await vm.LoadCommitsAsync();

            Assert.Equal(3, vm.FilteredCommits.Count);

            // Filter by author
            vm.FilterAuthor = "Alice";
            Assert.Equal(2, vm.FilteredCommits.Count);

            // Additional filter by subject
            vm.FilterSubject = "README";
            Assert.Single(vm.FilteredCommits);
            Assert.Equal("3333333cccccccc", vm.FilteredCommits[0].Hash);

            // Clear
            vm.ClearFilters();
            Assert.Equal(3, vm.FilteredCommits.Count);

            // Filter by SHA
            vm.FilterSha = "2222";
            Assert.Single(vm.FilteredCommits);
            Assert.Equal("Bob", vm.FilteredCommits[0].AuthorName);
        }

        [Fact]
        public async Task MainViewModel_ResetHead_ExecutesWithCorrectModeAndTarget()
        {
            var fakeGit = new FakeGitService();
            var recentService = new RecentRepositoriesService();
            var themeService = new ThemeService();
            var mainVm = new MainViewModel(fakeGit, recentService, themeService)
            {
                RepositoryPath = @"C:\FakeRepo"
            };

            mainVm.ShowResetDialog(new GitCommit { Hash = "abc1234", Subject = "Test commit" });
            Assert.True(mainVm.IsResetDialogOpen);
            Assert.Equal("abc1234", mainVm.ResetTargetRef);

            mainVm.SetResetMode("Hard");
            Assert.Equal(GitResetMode.Hard, mainVm.ResetMode);

            await mainVm.ConfirmResetAsync();
            Assert.False(mainVm.IsResetDialogOpen);
            Assert.Equal("abc1234", fakeGit.LastResetTarget);
            Assert.Equal(GitResetMode.Hard, fakeGit.LastResetMode);
        }

        [Fact]
        public async Task WorkingChangesViewModel_StagesAndCommitsSuccessfully()
        {
            var fakeGit = new FakeGitService();
            fakeGit.Changes.UnstagedFiles.Add(new GitWorkingFile { Path = "app.cs", IsStaged = false });
            fakeGit.Changes.UnstagedFiles.Add(new GitWorkingFile { Path = "readme.md", IsStaged = false });

            var vm = new WorkingChangesViewModel(fakeGit, () => @"C:\FakeRepo", () => Task.CompletedTask);
            await vm.LoadChangesAsync();

            Assert.Equal(2, vm.UnstagedFiles.Count);
            Assert.Empty(vm.StagedFiles);

            // Stage one file
            await vm.StageFileAsync(vm.UnstagedFiles[0]);
            Assert.Single(vm.StagedFiles);
            Assert.Single(vm.UnstagedFiles);

            // Commit
            vm.CommitSubject = "Add app files";
            await vm.CommitAsync();

            Assert.Equal("Add app files", fakeGit.LastCommitMessage);
            Assert.Empty(vm.StagedFiles);
        }

        [Fact]
        public async Task WorkingChangesViewModel_AddToGitIgnore_RemovesFileFromWorkingChanges()
        {
            var fakeGit = new FakeGitService();
            fakeGit.Changes.UnstagedFiles.Add(new GitWorkingFile { Path = "debug.log", IsStaged = false });

            var vm = new WorkingChangesViewModel(fakeGit, () => @"C:\FakeRepo", () => Task.CompletedTask);
            await vm.LoadChangesAsync();

            Assert.Single(vm.UnstagedFiles);

            await vm.AddToGitIgnoreAsync(vm.UnstagedFiles[0]);
            Assert.Empty(vm.UnstagedFiles);
        }

        [Fact]
        public void MainViewModel_WindowTitle_ContainsAppVersionAndRepositoryName()
        {
            var fakeGit = new FakeGitService();
            var recentService = new RecentRepositoriesService();
            var themeService = new ThemeService();
            var mainVm = new MainViewModel(fakeGit, recentService, themeService);

            Assert.Contains(MainViewModel.AppVersion, mainVm.WindowTitle);

            mainVm.RepositoryPath = @"C:\Code\MyProject";
            mainVm.RepositoryName = "MyProject";
            mainVm.CurrentBranch = "feature/test";

            Assert.Contains(MainViewModel.AppVersion, mainVm.WindowTitle);
            Assert.Contains("MyProject", mainVm.WindowTitle);
            Assert.Contains("feature/test", mainVm.WindowTitle);
        }
    }
}
