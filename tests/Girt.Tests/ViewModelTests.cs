using System.Collections.Generic;
using System.Linq;
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
        public bool FetchAllCalled { get; set; }

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
            Changes.UnstagedFiles.Clear();
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
        public Task<(bool Success, string Output)> FetchAllAsync(string repoPath)
        {
            FetchAllCalled = true;
            return Task.FromResult((true, "Fetched all remotes"));
        }
        public Task<string?> GetMergeBaseAsync(string repoPath, string ref1, string ref2) => Task.FromResult<string?>("root123");
        public Task<(bool Success, string Output)> AddToGitIgnoreAsync(string repoPath, string filePath, bool ignoreByExtension = false)
        {
            Changes.UnstagedFiles.RemoveAll(f => f.Path == filePath);
            return Task.FromResult((true, "Ignored"));
        }

        public int StashCount { get; set; }
        public Task<int> GetStashCountAsync(string repoPath) => Task.FromResult(StashCount);
        public Task<(bool Success, string Output)> StashStagedAsync(string repoPath, string? message = null)
        {
            StashCount++;
            Changes.StagedFiles.Clear();
            return Task.FromResult((true, "Stashed staged changes"));
        }
        public Task<(bool Success, string Output)> StashPopAsync(string repoPath)
        {
            if (StashCount > 0) StashCount--;
            return Task.FromResult((true, "Popped top stash"));
        }
        public Task<(bool Success, string Output)> StashApplyAsync(string repoPath)
        {
            return Task.FromResult((true, "Applied top stash"));
        }

        public string? LastRevertedCommit { get; set; }
        public string? LastCherryPickedCommit { get; set; }
        public string? LastMergedRef { get; set; }
        public string? LastRebasedRef { get; set; }

        public Task<(bool Success, string Output)> RevertCommitAsync(string repoPath, string commitHash)
        {
            LastRevertedCommit = commitHash;
            return Task.FromResult((true, "Reverted commit"));
        }

        public Task<(bool Success, string Output)> CherryPickCommitAsync(string repoPath, string commitHash)
        {
            LastCherryPickedCommit = commitHash;
            return Task.FromResult((true, "Cherry-picked commit"));
        }

        public Task<(bool Success, string Output)> MergeAsync(string repoPath, string targetRef, bool squash = false, bool noFf = false)
        {
            LastMergedRef = targetRef;
            return Task.FromResult((true, "Merged ref"));
        }

        public Task<(bool Success, string Output)> RebaseAsync(string repoPath, string targetRef)
        {
            LastRebasedRef = targetRef;
            return Task.FromResult((true, "Rebased ref"));
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
        public async Task BranchListViewModel_DetectsNewBranchesFromFetchOrPull()
        {
            var fakeGit = new FakeGitService
            {
                Branches = new List<GitBranch>
                {
                    new() { Name = "main", IsRemote = false },
                    new() { Name = "origin/main", IsRemote = true, RemoteName = "origin" }
                }
            };

            var vm = new BranchListViewModel(fakeGit, () => @"C:\FakeRepo", () => Task.CompletedTask);
            await vm.LoadBranchesAsync();
            Assert.Empty(vm.NewBranches);
            Assert.False(vm.HasNewBranches);

            // New branch appeared in remote after fetch
            fakeGit.Branches.Add(new GitBranch { Name = "origin/feature/cool-stuff", IsRemote = true, RemoteName = "origin" });
            await vm.LoadBranchesAsync();

            Assert.Single(vm.NewBranches);
            Assert.True(vm.HasNewBranches);
            Assert.Equal("origin/feature/cool-stuff", vm.NewBranches[0].Name);

            // Checkout new branch
            await vm.CheckoutBranchAsync(vm.NewBranches[0]);
            Assert.Empty(vm.NewBranches);
            Assert.False(vm.HasNewBranches);
        }

        [Fact]
        public async Task CommitHistoryViewModel_BranchAssociation_FiltersOrDimsUnrelatedBranches()
        {
            // Topology:
            // trunk (main): root -> c1 -> c2 (tip: main)
            // branch A: c1 -> a1 -> a2 (tip: feature/A)
            // branch B: a2 -> b1 (tip: feature/B)
            // branch X (unrelated): root -> x1 (tip: feature/X)
            var cRoot = new GitCommit { Hash = "root", Subject = "Initial commit" };
            var c1 = new GitCommit { Hash = "c1", ParentHashes = new List<string> { "root" }, Subject = "Trunk commit 1" };
            var c2 = new GitCommit { Hash = "c2", ParentHashes = new List<string> { "c1" }, Subject = "Trunk commit 2" };
            var a1 = new GitCommit { Hash = "a1", ParentHashes = new List<string> { "c1" }, Subject = "A commit 1" };
            var a2 = new GitCommit { Hash = "a2", ParentHashes = new List<string> { "a1" }, Subject = "A commit 2" };
            var b1 = new GitCommit { Hash = "b1", ParentHashes = new List<string> { "a2" }, Subject = "B commit 1" };
            var x1 = new GitCommit { Hash = "x1", ParentHashes = new List<string> { "root" }, Subject = "X commit 1" };

            var fakeGit = new FakeGitService
            {
                Commits = new List<GitCommit> { b1, a2, a1, c2, c1, x1, cRoot },
                Branches = new List<GitBranch>
                {
                    new() { Name = "main", TipCommitHash = "c2" },
                    new() { Name = "feature/A", TipCommitHash = "a2" },
                    new() { Name = "feature/B", TipCommitHash = "b1" },
                    new() { Name = "feature/X", TipCommitHash = "x1" }
                }
            };

            var vm = new CommitHistoryViewModel(fakeGit, () => @"C:\FakeRepo", _ => { });
            vm.SetBranches(fakeGit.Branches, "feature/B");
            await vm.LoadCommitsAsync();

            // Default: ShowAll
            Assert.Equal(7, vm.FilteredCommits.Count);

            // HideBeyondTrunk Mode (Branch to Trunk Fork Point only):
            // Associated should only include: b1, a2, a1, and fork point c1 -> total 4
            // Older trunk (root), ahead trunk (c2), and unrelated (x1) are hidden
            vm.AssociationMode = BranchAssociationMode.HideBeyondTrunk;
            Assert.Equal(4, vm.FilteredCommits.Count);
            Assert.Contains(vm.FilteredCommits, c => c.Hash == "b1");
            Assert.Contains(vm.FilteredCommits, c => c.Hash == "a2");
            Assert.Contains(vm.FilteredCommits, c => c.Hash == "a1");
            Assert.Contains(vm.FilteredCommits, c => c.Hash == "c1");
            Assert.DoesNotContain(vm.FilteredCommits, c => c.Hash == "root");
            Assert.DoesNotContain(vm.FilteredCommits, c => c.Hash == "c2");
            Assert.DoesNotContain(vm.FilteredCommits, c => c.Hash == "x1");

            // DimBeyondTrunk Mode:
            // All 7 commits displayed, but root, c2, x1 are dimmed
            vm.AssociationMode = BranchAssociationMode.DimBeyondTrunk;
            Assert.Equal(7, vm.FilteredCommits.Count);
            Assert.True(vm.FilteredCommits.First(c => c.Hash == "root").IsDimmed);
            Assert.True(vm.FilteredCommits.First(c => c.Hash == "c2").IsDimmed);
            Assert.True(vm.FilteredCommits.First(c => c.Hash == "x1").IsDimmed);
            Assert.False(vm.FilteredCommits.First(c => c.Hash == "b1").IsDimmed);
            Assert.False(vm.FilteredCommits.First(c => c.Hash == "c1").IsDimmed);

            // HideUnrelated Mode (includes full trunk lineage):
            // Associated should include: root, c1, c2 (trunk), a1, a2 (A), b1 (B) -> total 6
            // Unrelated x1 should be hidden
            vm.AssociationMode = BranchAssociationMode.HideUnrelated;
            Assert.Equal(6, vm.FilteredCommits.Count);
            Assert.DoesNotContain(vm.FilteredCommits, c => c.Hash == "x1");
            Assert.Contains(vm.FilteredCommits, c => c.Hash == "b1");
            Assert.Contains(vm.FilteredCommits, c => c.Hash == "a2");
            Assert.Contains(vm.FilteredCommits, c => c.Hash == "c2");

            // DimUnrelated Mode:
            // All 7 commits displayed, but x1 has IsDimmed = true
            vm.AssociationMode = BranchAssociationMode.DimUnrelated;
            Assert.Equal(7, vm.FilteredCommits.Count);
            var xCommit = vm.FilteredCommits.First(c => c.Hash == "x1");
            Assert.True(xCommit.IsDimmed);
            var bCommit = vm.FilteredCommits.First(c => c.Hash == "b1");
            Assert.False(bCommit.IsDimmed);
        }

        [Fact]
        public async Task CommitHistoryViewModel_BranchAssociation_SelectsCommitAndHighlightsMergeAndForkLineage()
        {
            // PR Merge Topology (matching Bitbucket/GitHub PR flow):
            // Trunk: root -> c0 -> c1 -> mergePR ("78a1306") -> tip ("87c5e2a")
            // Feature branch diverged at c0: c0 -> f_wip ("f65af44") -> f_unit ("ac25c3a") -> merged into trunk at mergePR ("78a1306")
            // Unrelated branch: root -> x1
            var root = new GitCommit { Hash = "root", Subject = "Root commit" };
            var c0 = new GitCommit { Hash = "c0", ParentHashes = new List<string> { "root" }, Subject = "Trunk base where feature branched" };
            var c1 = new GitCommit { Hash = "c1", ParentHashes = new List<string> { "c0" }, Subject = "Trunk parallel work" };
            var f_wip = new GitCommit { Hash = "f65af44", ParentHashes = new List<string> { "c0" }, Subject = "Feature WIP" };
            var f_unit = new GitCommit { Hash = "ac25c3a", ParentHashes = new List<string> { "f65af44" }, Subject = "Unit tests" };
            var mergePR = new GitCommit { Hash = "78a1306", ParentHashes = new List<string> { "c1", "ac25c3a" }, Subject = "Merged in feature PR" };
            var tip = new GitCommit { Hash = "87c5e2a", ParentHashes = new List<string> { "78a1306" }, Subject = "Develop tip" };
            var x1 = new GitCommit { Hash = "x1", ParentHashes = new List<string> { "root" }, Subject = "Unrelated feature" };

            var fakeGit = new FakeGitService
            {
                Commits = new List<GitCommit> { tip, mergePR, f_unit, f_wip, c1, c0, x1, root },
                Branches = new List<GitBranch>
                {
                    new() { Name = "develop", TipCommitHash = "87c5e2a" },
                    new() { Name = "feature/PR", TipCommitHash = "f65af44" }
                }
            };

            var vm = new CommitHistoryViewModel(fakeGit, () => @"C:\FakeRepo", _ => { });
            vm.SetBranches(fakeGit.Branches, "develop");
            await vm.LoadCommitsAsync();

            // Set mode to Dim to Trunk Fork
            vm.AssociationMode = BranchAssociationMode.DimBeyondTrunk;

            // User selects f65af44 in the commit history
            vm.SelectedCommit = f_wip;

            // Verified NOT dimmed:
            // 1. Where it split from trunk (c0)
            // 2. All commits on Branch A (f_wip, f_unit)
            // 3. The merge commit into trunk (mergePR)
            Assert.False(c0.IsDimmed);
            Assert.False(f_wip.IsDimmed);
            Assert.False(f_unit.IsDimmed);
            Assert.False(mergePR.IsDimmed);

            // Verified DIMMED:
            // 1. Subsequent trunk commits ahead of merge (tip)
            // 2. Older trunk commits before split (root)
            // 3. Parallel trunk commits (c1)
            // 4. Unrelated branches (x1)
            Assert.True(tip.IsDimmed);
            Assert.True(root.IsDimmed);
            Assert.True(c1.IsDimmed);
            Assert.True(x1.IsDimmed);
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
            vm.ApplyFilter();
            Assert.Equal(2, vm.FilteredCommits.Count);

            // Additional filter by subject
            vm.FilterSubject = "README";
            vm.ApplyFilter();
            Assert.Single(vm.FilteredCommits);
            Assert.Equal("3333333cccccccc", vm.FilteredCommits[0].Hash);

            // Clear
            vm.ClearFilters();
            Assert.Equal(3, vm.FilteredCommits.Count);

            // Filter by SHA
            vm.FilterSha = "2222";
            vm.ApplyFilter();
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

            var vm = new WorkingChangesViewModel(fakeGit, () => @"C:\FakeRepo", _ => Task.CompletedTask);
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

            var vm = new WorkingChangesViewModel(fakeGit, () => @"C:\FakeRepo", _ => Task.CompletedTask);
            await vm.LoadChangesAsync();

            Assert.Single(vm.UnstagedFiles);

            await vm.AddToGitIgnoreAsync(vm.UnstagedFiles[0]);
            Assert.Empty(vm.UnstagedFiles);
        }

        [Fact]
        public async Task WorkingChangesViewModel_StashStaged_And_PopStash_Succeeds()
        {
            var fakeGit = new FakeGitService();
            fakeGit.Changes.StagedFiles.Add(new GitWorkingFile { Path = "Feature.cs", IsStaged = true });

            var vm = new WorkingChangesViewModel(fakeGit, () => @"C:\FakeRepo", _ => Task.CompletedTask);
            await vm.LoadChangesAsync();

            Assert.True(vm.HasStagedFiles);
            Assert.Equal(0, vm.StashCount);
            Assert.False(vm.HasStashes);

            // Stash staged changes
            await vm.StashStagedAsync();
            Assert.Empty(vm.StagedFiles);
            Assert.Equal(1, vm.StashCount);
            Assert.True(vm.HasStashes);

            // Pop stash
            await vm.StashPopAsync();
            Assert.Equal(0, vm.StashCount);
            Assert.False(vm.HasStashes);
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

        [Fact]
        public void MainViewModel_ResetDialog_PreFillsTargetCorrectly()
        {
            var fakeGit = new FakeGitService();
            var recentService = new RecentRepositoriesService();
            var themeService = new ThemeService();
            var mainVm = new MainViewModel(fakeGit, recentService, themeService);

            // Default
            mainVm.ShowResetDialog();
            Assert.True(mainVm.IsResetDialogOpen);
            Assert.Equal("HEAD~1", mainVm.ResetTargetRef);

            // Commit param
            var commit = new GitCommit { Hash = "abc1234567890", Subject = "Fix bug" };
            mainVm.ShowResetDialog(commit);
            Assert.Equal("abc1234567890", mainVm.ResetTargetRef);
            Assert.Contains("abc1234", mainVm.ResetTargetDescription);

            // Branch param
            var branch = new GitBranch { Name = "feature/login", TipCommitHash = "def456" };
            mainVm.ShowResetDialog(branch);
            Assert.Equal("feature/login", mainVm.ResetTargetRef);
        }
    }
}
