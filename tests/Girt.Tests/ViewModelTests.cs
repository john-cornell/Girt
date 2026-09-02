using System;
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
        public DateTime LastCommandCompletedUtc { get; set; } = DateTime.MinValue;

        public Task<string?> GetRepositoryRootAsync(string directoryPath) => Task.FromResult(RepoRoot);
        public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repoPath) => Task.FromResult<IReadOnlyList<GitBranch>>(Branches);
        public Task<IReadOnlyList<GitCommit>> GetCommitsAsync(string repoPath, int maxCount = 1000) => Task.FromResult<IReadOnlyList<GitCommit>>(Commits);
        public Task<IReadOnlyList<GitFileDiff>> GetCommitDiffAsync(string repoPath, string commitHash) => Task.FromResult<IReadOnlyList<GitFileDiff>>(DiffFiles);
        public Task<string> GetRawFileDiffAsync(string repoPath, string commitHash, string filePath) => Task.FromResult(RawDiff);
        public Task<IReadOnlyList<GitFileDiff>> GetUnpushedDiffAsync(string repoPath) => Task.FromResult<IReadOnlyList<GitFileDiff>>(DiffFiles);
        public Task<string> GetRawUnpushedFileDiffAsync(string repoPath, string filePath) => Task.FromResult(RawDiff);
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

            // Mirrors what a real `git commit` does to the log, so callers exercising
            // MainViewModel's "fetch just the new commit and splice it in" path (GetCommitsAsync
            // with maxCount: 1) see something realistic rather than a stale, unchanged list.
            var parentHash = Commits.Count > 0 ? Commits[0].Hash : null;
            Commits.Insert(0, new GitCommit
            {
                Hash = $"fakecommit{Commits.Count}",
                Subject = message,
                ParentHashes = parentHash != null ? new List<string> { parentHash } : new List<string>(),
                AuthorName = "Test Author",
                Date = DateTimeOffset.UtcNow
            });

            return Task.FromResult((true, "Committed"));
        }

        public Task<string> GetWorkingTreeFileDiffAsync(string repoPath, string filePath, bool isStaged) => Task.FromResult(RawDiff);
        public Task<(bool Success, string Output)> PushAsync(string repoPath) => Task.FromResult((true, "Pushed"));
        public Task<(bool Success, string Output)> PullAsync(string repoPath, bool rebase = false) => Task.FromResult((true, "Pulled"));
        public Task<(bool Success, string Output)> FetchAllAsync(string repoPath)
        {
            FetchAllCalled = true;
            return Task.FromResult((true, "Fetched all remotes"));
        }
        public Task<string?> GetMergeBaseAsync(string repoPath, string ref1, string ref2) => Task.FromResult<string?>("root123");
        public Task<(bool Success, string Output)> AddToGitIgnoreAsync(string repoPath, string filePath, GitIgnoreTarget target = GitIgnoreTarget.File)
        {
            Changes.UnstagedFiles.RemoveAll(f => f.Path == filePath);
            return Task.FromResult((true, "Ignored"));
        }

        public int StashCount { get; set; }
        public Task<int> GetStashCountAsync(string repoPath) => Task.FromResult(StashCount);
        public Task<string?> GetTopStashDescriptionAsync(string repoPath) => Task.FromResult<string?>(StashCount > 0 ? "stash@{0}: WIP on test" : null);

        public Dictionary<string, string> GlobalConfig { get; } = new();
        public Dictionary<string, string> LocalConfig { get; } = new();

        public Task<string?> GetGitConfigValueAsync(string repoPath, string key, bool global)
        {
            var store = global ? GlobalConfig : LocalConfig;
            return Task.FromResult(store.TryGetValue(key, out var value) ? value : null);
        }

        public Task<(bool Success, string Output)> SetGitConfigValueAsync(string repoPath, string key, string value, bool global)
        {
            (global ? GlobalConfig : LocalConfig)[key] = value;
            return Task.FromResult((true, ""));
        }

        public Task<(bool Success, string Output)> UnsetLocalGitConfigValueAsync(string repoPath, string key)
        {
            LocalConfig.Remove(key);
            return Task.FromResult((true, ""));
        }
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

            var vm = new BranchListViewModel(fakeGit, () => @"C:\FakeRepo", () => Task.CompletedTask, false, _ => { }, (_, _) => { }, _ => new HashSet<string>(), (_, _) => { });
            await vm.LoadBranchesAsync();

            Assert.Equal(3, vm.FilteredLocalBranches.Count);
            Assert.Single(vm.FilteredRemoteBranches);

            // Typing is debounced (see OnFilterTextChanged) so a real keystroke doesn't
            // re-filter immediately; ApplyFilter() forces the same pass synchronously here.
            vm.FilterText = "login";
            vm.ApplyFilter();
            Assert.Single(vm.FilteredLocalBranches);
            Assert.Equal("feature/login", vm.FilteredLocalBranches[0].Name);
            Assert.Empty(vm.FilteredRemoteBranches);
        }

        [Fact]
        public async Task BranchListViewModel_GroupBranchesIntoFolders_BuildsIndentedFolderTree()
        {
            var fakeGit = new FakeGitService
            {
                Branches = new List<GitBranch>
                {
                    new() { Name = "feature/BB-100-foo" },
                    new() { Name = "bugfix/BB-300-baz" },
                    new() { Name = "feature/BB-200-bar" },
                    new() { Name = "main" },
                    new() { Name = "develop" }
                }
            };

            var savedValues = new List<bool>();
            var vm = new BranchListViewModel(fakeGit, () => @"C:\FakeRepo", () => Task.CompletedTask, true, v => savedValues.Add(v), (_, _) => { }, _ => new HashSet<string>(), (_, _) => { });
            await vm.LoadBranchesAsync();

            // Root items (in first-seen order): "feature" and "bugfix" folders, each owning
            // their branches as real Children, then the root-level leaves "main" and "develop".
            Assert.Equal(4, vm.LocalBranchTree.Count);

            var featureFolder = vm.LocalBranchTree[0];
            Assert.True(featureFolder.IsFolder);
            Assert.Equal("feature", featureFolder.DisplayName);
            Assert.Equal(2, featureFolder.Children.Count);
            Assert.Equal("BB-100-foo", featureFolder.Children[0].DisplayName);
            Assert.Equal("feature/BB-100-foo", featureFolder.Children[0].Branch?.Name);
            Assert.Equal("BB-200-bar", featureFolder.Children[1].DisplayName);

            var bugfixFolder = vm.LocalBranchTree[1];
            Assert.True(bugfixFolder.IsFolder);
            Assert.Equal("bugfix", bugfixFolder.DisplayName);
            Assert.Single(bugfixFolder.Children);
            Assert.Equal("BB-300-baz", bugfixFolder.Children[0].DisplayName);

            Assert.False(vm.LocalBranchTree[2].IsFolder);
            Assert.Equal("main", vm.LocalBranchTree[2].DisplayName);

            Assert.False(vm.LocalBranchTree[3].IsFolder);
            Assert.Equal("develop", vm.LocalBranchTree[3].DisplayName);

            // Toggling back to flat mode persists the change and empties the tree.
            vm.GroupBranchesIntoFolders = false;
            Assert.Empty(vm.LocalBranchTree);
            Assert.Equal(5, vm.FilteredLocalBranches.Count);
            Assert.Contains(false, savedValues);
        }

        [Fact]
        public async Task BranchListViewModel_TogglePinBranch_MovesToTopOfFlatListAndTreeRoot()
        {
            var fakeGit = new FakeGitService
            {
                Branches = new List<GitBranch>
                {
                    new() { Name = "feature/BB-100-foo" },
                    new() { Name = "bugfix/BB-300-baz" },
                    new() { Name = "main" }
                }
            };

            var savedRepoPaths = new List<string>();
            var savedPinnedSets = new List<List<string>>();
            var vm = new BranchListViewModel(
                fakeGit, () => @"C:\FakeRepo", () => Task.CompletedTask,
                true, _ => { }, (_, _) => { },
                _ => new HashSet<string>(),
                (repoPath, names) =>
                {
                    savedRepoPaths.Add(repoPath);
                    savedPinnedSets.Add(names.ToList());
                });
            await vm.LoadBranchesAsync();

            var target = vm.FilteredLocalBranches.Single(b => b.Name == "bugfix/BB-300-baz");
            vm.TogglePinBranchCommand.Execute(target);

            // Pinned branch floats to the top of the flat list.
            Assert.True(vm.FilteredLocalBranches[0].IsPinned);
            Assert.Equal("bugfix/BB-300-baz", vm.FilteredLocalBranches[0].Name);

            // Persisted immediately against the current repo path.
            Assert.Equal(@"C:\FakeRepo", savedRepoPaths.Last());
            Assert.Contains("bugfix/BB-300-baz", savedPinnedSets.Last());

            // In the tree, the pinned branch floats to the very top as a plain leaf (full
            // DisplayName, since there's no folder context) - no synthetic "Pinned" folder.
            var pinnedLeaf = vm.LocalBranchTree[0];
            Assert.False(pinnedLeaf.IsFolder);
            Assert.Equal("bugfix/BB-300-baz", pinnedLeaf.DisplayName);
            Assert.Equal("bugfix/BB-300-baz", pinnedLeaf.Branch?.Name);

            // It's a move, not a duplicate - "bugfix" had only this one branch, so with it
            // pinned away the folder itself no longer exists at all.
            Assert.DoesNotContain(vm.LocalBranchTree, i => i.IsFolder && i.DisplayName == "bugfix");

            // Unpinning removes it from the top of both the flat list and the tree, and
            // restores it to its normal folder position.
            vm.TogglePinBranchCommand.Execute(target);
            Assert.False(vm.FilteredLocalBranches.First(b => b.Name == "bugfix/BB-300-baz").IsPinned);
            Assert.DoesNotContain(vm.LocalBranchTree, i => !i.IsFolder && i.Branch?.Name == "bugfix/BB-300-baz");
            var bugfixFolderAfterUnpin = vm.LocalBranchTree.Single(i => i.IsFolder && i.DisplayName == "bugfix");
            Assert.Contains(bugfixFolderAfterUnpin.Children, i => i.Branch?.Name == "bugfix/BB-300-baz");
        }

        [Fact]
        public async Task BranchListViewModel_TogglePinBranch_InFlatView_RepositionsWithoutRebuildingList()
        {
            var fakeGit = new FakeGitService
            {
                Branches = new List<GitBranch>
                {
                    new() { Name = "feature/BB-100-foo" },
                    new() { Name = "bugfix/BB-300-baz" },
                    new() { Name = "main" }
                }
            };

            // GroupBranchesIntoFolders = false: exercises the fast Move-based path rather than
            // the full folder-tree rebuild path covered by the test above.
            var vm = new BranchListViewModel(
                fakeGit, () => @"C:\FakeRepo", () => Task.CompletedTask,
                false, _ => { }, (_, _) => { },
                _ => new HashSet<string>(), (_, _) => { });
            await vm.LoadBranchesAsync();

            var originalInstance = vm.FilteredLocalBranches.Single(b => b.Name == "bugfix/BB-300-baz");
            vm.TogglePinBranchCommand.Execute(originalInstance);

            // Same instance floats to the top - Move repositions it rather than the list being
            // rebuilt from a fresh copy.
            Assert.Same(originalInstance, vm.FilteredLocalBranches[0]);
            Assert.True(vm.FilteredLocalBranches[0].IsPinned);

            vm.TogglePinBranchCommand.Execute(originalInstance);
            Assert.False(originalInstance.IsPinned);
            Assert.DoesNotContain(vm.FilteredLocalBranches, b => b.IsPinned);
        }

        [Fact]
        public async Task BranchListViewModel_FolderExpandState_PersistsAcrossTreeRebuild()
        {
            // Expand/collapse itself is now handled entirely by the WPF TreeView (its
            // TreeViewItem.IsExpanded is two-way bound to BranchTreeItem.IsExpanded) - there's
            // no ViewModel command for it any more. What the ViewModel still owns is carrying
            // that expand state across a rebuild (filter/pin/reload), since the old node
            // instances are discarded and rebuilt from scratch each time.
            var fakeGit = new FakeGitService
            {
                Branches = new List<GitBranch>
                {
                    new() { Name = "feature/BB-100-foo" },
                    new() { Name = "feature/BB-200-bar" },
                    new() { Name = "bugfix/BB-300-baz" },
                    new() { Name = "main" }
                }
            };

            var vm = new BranchListViewModel(fakeGit, () => @"C:\FakeRepo", () => Task.CompletedTask, true, _ => { }, (_, _) => { }, _ => new HashSet<string>(), (_, _) => { });
            await vm.LoadBranchesAsync();

            Assert.Equal(3, vm.LocalBranchTree.Count); // feature, bugfix, main
            var featureFolder = vm.LocalBranchTree[0];
            Assert.Equal("feature", featureFolder.DisplayName);
            Assert.True(featureFolder.IsExpanded); // Expanded by default.

            // Simulate the user collapsing it (what TreeViewItem's two-way binding would do),
            // then force a rebuild the same way a filter-text change or pin toggle would.
            featureFolder.IsExpanded = false;
            vm.ApplyFilter();

            var rebuiltFeatureFolder = vm.LocalBranchTree.Single(i => i.DisplayName == "feature");
            Assert.False(rebuiltFeatureFolder.IsExpanded);
            Assert.Equal(2, rebuiltFeatureFolder.Children.Count); // Children are unaffected - still present, just not shown.

            // Re-expanding and rebuilding again round-trips back to true.
            rebuiltFeatureFolder.IsExpanded = true;
            vm.ApplyFilter();
            Assert.True(vm.LocalBranchTree.Single(i => i.DisplayName == "feature").IsExpanded);
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

            var vm = new BranchListViewModel(fakeGit, () => @"C:\FakeRepo", () => Task.CompletedTask, false, _ => { }, (_, _) => { }, _ => new HashSet<string>(), (_, _) => { });
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
        public async Task BranchListViewModel_ExternalCheckoutOfNewBranch_RemovesItFromNewBranchesOnReload()
        {
            var fakeGit = new FakeGitService
            {
                Branches = new List<GitBranch>
                {
                    new() { Name = "main", IsRemote = false },
                    new() { Name = "origin/main", IsRemote = true, RemoteName = "origin" }
                }
            };

            var vm = new BranchListViewModel(fakeGit, () => @"C:\FakeRepo", () => Task.CompletedTask, false, _ => { }, (_, _) => { }, _ => new HashSet<string>(), (_, _) => { });
            await vm.LoadBranchesAsync();

            fakeGit.Branches.Add(new GitBranch { Name = "origin/feature/cool-stuff", IsRemote = true, RemoteName = "origin" });
            await vm.LoadBranchesAsync();
            Assert.Single(vm.NewBranches);

            // Simulate the branch being checked out by an external tool (not via
            // CheckoutBranchAsync) - a new local branch simply appears on the next reload.
            fakeGit.Branches.Add(new GitBranch { Name = "feature/cool-stuff", IsRemote = false });
            await vm.LoadBranchesAsync();

            Assert.Empty(vm.NewBranches);
            Assert.False(vm.HasNewBranches);
        }

        [Fact]
        public async Task BranchListViewModel_SwitchingRepos_DoesNotFloodNewBranches()
        {
            var fakeGit = new FakeGitService
            {
                Branches = new List<GitBranch> { new() { Name = "main" }, new() { Name = "develop" } }
            };

            var currentRepo = @"C:\RepoA";
            var vm = new BranchListViewModel(fakeGit, () => currentRepo, () => Task.CompletedTask, false, _ => { }, (_, _) => { }, _ => new HashSet<string>(), (_, _) => { });

            await vm.LoadBranchesAsync();
            Assert.Empty(vm.NewBranches);

            // Switching to a different repo (with an entirely different branch set) shouldn't
            // treat every one of its branches as "new".
            currentRepo = @"C:\RepoB";
            fakeGit.Branches = new List<GitBranch> { new() { Name = "main" }, new() { Name = "feature/other-repo-thing" } };
            await vm.LoadBranchesAsync();
            Assert.Empty(vm.NewBranches);
            Assert.False(vm.HasNewBranches);

            // A genuinely new branch discovered within the SAME repo still gets flagged.
            fakeGit.Branches.Add(new GitBranch { Name = "feature/actually-new" });
            await vm.LoadBranchesAsync();
            Assert.Single(vm.NewBranches);
            Assert.Equal("feature/actually-new", vm.NewBranches[0].Name);
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

            var infoMessages = new List<(string Title, string Message)>();
            var vm = new CommitHistoryViewModel(fakeGit, () => @"C:\FakeRepo", _ => { }, (title, message) => infoMessages.Add((title, message)));
            vm.SetBranches(fakeGit.Branches, "feature/B");
            await vm.LoadCommitsAsync();

            // Default: ShowAll
            Assert.Equal(7, vm.FilteredCommits.Count);

            // DimToFork Command:
            // All 7 commits displayed, but root, c2, x1 are dimmed - only the divergence
            // point (c1) and this branch's own lineage (b1, a2, a1) stay lit.
            vm.DimToFork(b1);
            Assert.Equal(7, vm.FilteredCommits.Count);
            Assert.True(vm.FilteredCommits.First(c => c.Hash == "root").IsDimmed);
            Assert.True(vm.FilteredCommits.First(c => c.Hash == "c2").IsDimmed);
            Assert.True(vm.FilteredCommits.First(c => c.Hash == "x1").IsDimmed);
            Assert.False(vm.FilteredCommits.First(c => c.Hash == "b1").IsDimmed);
            Assert.False(vm.FilteredCommits.First(c => c.Hash == "c1").IsDimmed);

            // HideToFork Command:
            // Associated should only include: b1, a2, a1, and divergence point c1 -> total 4
            // Older trunk (root), ahead trunk (c2), and unrelated (x1) are hidden
            vm.HideToFork(b1);
            Assert.Equal(4, vm.FilteredCommits.Count);
            Assert.Contains(vm.FilteredCommits, c => c.Hash == "b1");
            Assert.Contains(vm.FilteredCommits, c => c.Hash == "a2");
            Assert.Contains(vm.FilteredCommits, c => c.Hash == "a1");
            Assert.Contains(vm.FilteredCommits, c => c.Hash == "c1");
            Assert.DoesNotContain(vm.FilteredCommits, c => c.Hash == "root");
            Assert.DoesNotContain(vm.FilteredCommits, c => c.Hash == "c2");
            Assert.DoesNotContain(vm.FilteredCommits, c => c.Hash == "x1");

            // ClearIsolation Command:
            vm.ClearIsolation();
            Assert.Equal(7, vm.FilteredCommits.Count);
            Assert.False(vm.FilteredCommits.First(c => c.Hash == "x1").IsDimmed);

            // Isolating trunk itself is disallowed - it's a no-op, and the user is told why
            // instead of it silently doing nothing.
            vm.DimToFork(c2);
            Assert.False(vm.IsBranchIsolated);
            Assert.False(vm.FilteredCommits.First(c => c.Hash == "x1").IsDimmed);
            Assert.Single(infoMessages);
            Assert.Contains("main", infoMessages[0].Message);

            vm.HideToFork(c2);
            Assert.False(vm.IsBranchIsolated);
            Assert.Equal(7, vm.FilteredCommits.Count);
            Assert.Equal(2, infoMessages.Count);
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

            // Right-click DimToFork on f_wip (f65af44)
            vm.DimToFork(f_wip);

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
        public async Task CommitHistoryViewModel_BranchAssociation_NestedBranch_ShowsAncestorBranchAndDivergence()
        {
            // Topology: trunk -> A -> B, B merged back into A (not into trunk).
            // trunk (main): root -> t1 (tip)
            // branch A diverges from trunk at root: root -> a1 -> a2 -> mergeB ("a3") -> a4 (tip: feature/A)
            // branch B diverges from A at a1: a1 -> b1 -> b2, merged into A at mergeB ("a3")
            // Isolating B should show: B's own commits, A's commits (its ancestor branch),
            // and the single divergence point where A split from trunk (root).
            var root = new GitCommit { Hash = "root", Subject = "Root" };
            var t1 = new GitCommit { Hash = "t1", ParentHashes = new List<string> { "root" }, Subject = "Trunk tip" };
            var a1 = new GitCommit { Hash = "a1", ParentHashes = new List<string> { "root" }, Subject = "A commit 1 (B diverges here)" };
            var a2 = new GitCommit { Hash = "a2", ParentHashes = new List<string> { "a1" }, Subject = "A commit 2" };
            var b1 = new GitCommit { Hash = "b1", ParentHashes = new List<string> { "a1" }, Subject = "B commit 1" };
            var b2 = new GitCommit { Hash = "b2", ParentHashes = new List<string> { "b1" }, Subject = "B commit 2" };
            var mergeB = new GitCommit { Hash = "a3", ParentHashes = new List<string> { "a2", "b2" }, Subject = "Merge B into A" };
            var a4 = new GitCommit { Hash = "a4", ParentHashes = new List<string> { "a3" }, Subject = "A commit after merge" };

            var fakeGit = new FakeGitService
            {
                Commits = new List<GitCommit> { t1, a4, mergeB, b2, b1, a2, a1, root },
                Branches = new List<GitBranch>
                {
                    new() { Name = "main", TipCommitHash = "t1" },
                    new() { Name = "feature/A", TipCommitHash = "a4" },
                    new() { Name = "feature/B", TipCommitHash = "b2" }
                }
            };

            var vm = new CommitHistoryViewModel(fakeGit, () => @"C:\FakeRepo", _ => { });
            vm.SetBranches(fakeGit.Branches, "feature/B");
            await vm.LoadCommitsAsync();

            vm.DimToFork(b2);

            // Not dimmed: B's own commits, A's commits (the ancestor branch B forked from),
            // and the single divergence point where A split off trunk (root).
            Assert.False(root.IsDimmed);
            Assert.False(a1.IsDimmed);
            Assert.False(a2.IsDimmed);
            Assert.False(mergeB.IsDimmed);
            Assert.False(a4.IsDimmed);
            Assert.False(b1.IsDimmed);
            Assert.False(b2.IsDimmed);

            // Dimmed: trunk beyond the divergence point.
            Assert.True(t1.IsDimmed);
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
        public async Task CommitHistoryViewModel_SelectedCommit_PreservedAcrossReloadWithFreshInstances()
        {
            var fakeGit = new FakeGitService
            {
                Commits = new List<GitCommit>
                {
                    new() { Hash = "bbb222", Subject = "Second" },
                    new() { Hash = "aaa111", Subject = "First" }
                }
            };

            var vm = new CommitHistoryViewModel(fakeGit, () => @"C:\FakeRepo", _ => { });
            await vm.LoadCommitsAsync();

            vm.SelectedCommit = vm.FilteredCommits.Single(c => c.Hash == "aaa111");

            // A real refresh re-parses `git log` from scratch, producing entirely new GitCommit
            // instances for the same underlying commits - simulate that here rather than
            // reusing the same object references.
            fakeGit.Commits = new List<GitCommit>
            {
                new() { Hash = "bbb222", Subject = "Second" },
                new() { Hash = "aaa111", Subject = "First" }
            };
            await vm.LoadCommitsAsync();

            // Selection should still logically be "aaa111" - not silently reset to the top
            // commit just because the object instances changed.
            Assert.Equal("aaa111", vm.SelectedCommit?.Hash);
        }

        [Fact]
        public async Task CommitHistoryViewModel_IncrementalReload_ReusesUnchangedTailAndLaysOutOnlyNewCommits()
        {
            var top = new GitCommit { Hash = "top1", Subject = "Top", ParentHashes = new List<string> { "mid1" } };
            var mid = new GitCommit { Hash = "mid1", Subject = "Mid", ParentHashes = new List<string> { "root1" } };
            var root = new GitCommit { Hash = "root1", Subject = "Root" };

            var fakeGit = new FakeGitService { Commits = new List<GitCommit> { top, mid, root } };
            var vm = new CommitHistoryViewModel(fakeGit, () => @"C:\FakeRepo", _ => { });
            await vm.LoadCommitsAsync();

            Assert.Equal(3, vm.FilteredCommits.Count);
            var oldTopInstance = vm.FilteredCommits[0];
            var oldMidInstance = vm.FilteredCommits[1];
            var oldRootInstance = vm.FilteredCommits[2];
            Assert.Equal(0, oldTopInstance.LaneIndex);

            // Simulate a real refresh: one brand-new commit lands on top, and the rest of
            // history is re-parsed into entirely new object instances, same as a real `git log`
            // reparse would produce.
            var newTop = new GitCommit { Hash = "top2", Subject = "New top", ParentHashes = new List<string> { "top1" } };
            fakeGit.Commits = new List<GitCommit>
            {
                newTop,
                new() { Hash = "top1", Subject = "Top", ParentHashes = new List<string> { "mid1" } },
                new() { Hash = "mid1", Subject = "Mid", ParentHashes = new List<string> { "root1" } },
                new() { Hash = "root1", Subject = "Root" }
            };

            await vm.LoadCommitsAsync();

            Assert.Equal(4, vm.FilteredCommits.Count);
            Assert.Equal("top2", vm.FilteredCommits[0].Hash);
            Assert.Equal(0, vm.FilteredCommits[0].RowIndex);
            Assert.Equal(0, vm.FilteredCommits[0].LaneIndex);

            // The old rows are the SAME instances as before (not the fresh parse's objects) -
            // proof the tail was reused rather than relaid-out.
            Assert.Same(oldTopInstance, vm.FilteredCommits[1]);
            Assert.Same(oldMidInstance, vm.FilteredCommits[2]);
            Assert.Same(oldRootInstance, vm.FilteredCommits[3]);

            // Row indices shifted by the one new commit prepended; lane assignment untouched.
            Assert.Equal(1, oldTopInstance.RowIndex);
            Assert.Equal(2, oldMidInstance.RowIndex);
            Assert.Equal(3, oldRootInstance.RowIndex);
            Assert.Equal(0, oldTopInstance.LaneIndex);

            // The new commit connects down into the (reused) old top commit's lane.
            Assert.Single(vm.FilteredCommits[0].Connections);
            Assert.Equal(0, vm.FilteredCommits[0].Connections[0].FromLane);
            Assert.Equal(0, vm.FilteredCommits[0].Connections[0].ToLane);
        }

        [Fact]
        public async Task CommitHistoryViewModel_IncrementalReload_FallsBackToFullRelayoutWhenHistoryDiverges()
        {
            var fakeGit = new FakeGitService
            {
                Commits = new List<GitCommit> { new() { Hash = "top1", Subject = "Top" } }
            };
            var vm = new CommitHistoryViewModel(fakeGit, () => @"C:\FakeRepo", _ => { });
            await vm.LoadCommitsAsync();

            // Simulate switching to a completely unrelated branch - nothing in common with
            // what was previously loaded, so the previous top commit is never found.
            fakeGit.Commits = new List<GitCommit> { new() { Hash = "other1", Subject = "Other" } };
            await vm.LoadCommitsAsync();

            Assert.Single(vm.FilteredCommits);
            Assert.Equal("other1", vm.FilteredCommits[0].Hash);
            Assert.Equal(0, vm.FilteredCommits[0].RowIndex);
            Assert.Equal(0, vm.FilteredCommits[0].LaneIndex);
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

            var vm = new WorkingChangesViewModel(fakeGit, () => @"C:\FakeRepo", _ => Task.CompletedTask, false, _ => { });
            await vm.LoadChangesAsync();

            Assert.Equal(2, vm.UnstagedFiles.Count);
            Assert.Empty(vm.StagedFiles);
            Assert.Equal(2, vm.TotalChangesCount);

            // Stage one file
            await vm.StageFileAsync(vm.UnstagedFiles[0]);
            Assert.Single(vm.StagedFiles);
            Assert.Single(vm.UnstagedFiles);

            // TotalChangesCount/HasStagedFiles have no notification of their own - they only
            // stay right because StagedFiles/UnstagedFiles' CollectionChanged is wired up to
            // raise it. This is exactly the "11 changes but 0/0 shown" bug: staging is fully
            // optimistic (no reload), so if that wiring broke, this would still read stale.
            Assert.Equal(2, vm.TotalChangesCount);
            Assert.True(vm.HasStagedFiles);
            Assert.True(vm.HasUnstagedFiles);

            // Commit
            vm.CommitSubject = "Add app files";
            await vm.CommitAsync();

            Assert.Equal("Add app files", fakeGit.LastCommitMessage);
            Assert.Empty(vm.StagedFiles);
            Assert.False(vm.HasStagedFiles);
            Assert.Equal(1, vm.TotalChangesCount);
        }

        [Fact]
        public void WorkingChangesViewModel_PushAfterCommit_LoadsInitialValueAndPersistsChanges()
        {
            var fakeGit = new FakeGitService();
            var savedValues = new List<bool>();

            var vm = new WorkingChangesViewModel(fakeGit, () => @"C:\FakeRepo", _ => Task.CompletedTask, true, v => savedValues.Add(v));

            // Initial value comes from the injected loader, with no save triggered yet.
            Assert.True(vm.PushAfterCommit);
            Assert.Empty(savedValues);

            // Toggling it persists the new value via the injected saver.
            vm.PushAfterCommit = false;
            Assert.Single(savedValues);
            Assert.False(savedValues[0]);
        }

        [Fact]
        public async Task WorkingChangesViewModel_AddToGitIgnore_RemovesFileFromWorkingChanges()
        {
            var fakeGit = new FakeGitService();
            fakeGit.Changes.UnstagedFiles.Add(new GitWorkingFile { Path = "debug.log", IsStaged = false });

            var vm = new WorkingChangesViewModel(fakeGit, () => @"C:\FakeRepo", _ => Task.CompletedTask, false, _ => { });
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

            var vm = new WorkingChangesViewModel(fakeGit, () => @"C:\FakeRepo", _ => Task.CompletedTask, false, _ => { });
            vm.ConfirmStashAction = _ => true; // Pop shows a real MessageBox in production; auto-confirm here.
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
        public async Task WorkingChangesViewModel_StashPop_DoesNothingWhenNotConfirmed()
        {
            var fakeGit = new FakeGitService { StashCount = 1 };
            var vm = new WorkingChangesViewModel(fakeGit, () => @"C:\FakeRepo", _ => Task.CompletedTask, false, _ => { });
            vm.ConfirmStashAction = _ => false; // Simulates the user clicking "No".
            await vm.LoadChangesAsync();

            await vm.StashPopAsync();

            Assert.Equal(1, fakeGit.StashCount); // Untouched - the confirmation gate must short-circuit before calling git.
        }

        [Fact]
        public void GitWorkingFile_AncestorFolders_ListsEveryContainingFolderDeepestFirst()
        {
            var file = new GitWorkingFile { Path = ".codeidx/vault/Api/Test/AddMissing.cs" };

            Assert.Equal(
                new[] { ".codeidx/vault/Api/Test", ".codeidx/vault/Api", ".codeidx/vault", ".codeidx" },
                file.AncestorFolders);
        }

        [Fact]
        public void GitWorkingFile_AncestorFolders_EmptyForTopLevelFile()
        {
            var file = new GitWorkingFile { Path = "README.md" };

            Assert.Empty(file.AncestorFolders);
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

        [Fact]
        public async Task UnpushedChangesViewModel_OpenAsync_LoadsFilesAndDiffThenClose()
        {
            var fakeGit = new FakeGitService
            {
                DiffFiles = new List<GitFileDiff> { new() { Path = "src/Foo.cs", Additions = 3, Deletions = 1 } },
                RawDiff = "diff --git a/src/Foo.cs b/src/Foo.cs\n@@ -1,1 +1,3 @@\n+added line\n"
            };
            var vm = new UnpushedChangesViewModel(fakeGit, () => @"C:\FakeRepo");

            await vm.OpenAsync();

            Assert.True(vm.IsOpen);
            Assert.Single(vm.ChangedFiles);
            Assert.Equal("src/Foo.cs", vm.SelectedFile?.Path);

            vm.Close();
            Assert.False(vm.IsOpen);
        }

        [Fact]
        public async Task MainViewModel_Commit_SplicesNewCommitLocallyAndBumpsAheadCountWithoutFullRefresh()
        {
            var fakeGit = new FakeGitService
            {
                Commits = new List<GitCommit> { new() { Hash = "root1", Subject = "Root" } },
                Status = new GitRepoStatus { HasUpstream = true, AheadCount = 0, BehindCount = 0 }
            };
            fakeGit.Changes.StagedFiles.Add(new GitWorkingFile { Path = "app.cs", IsStaged = true });

            var recentService = new RecentRepositoriesService();
            var themeService = new ThemeService();
            var mainVm = new MainViewModel(fakeGit, recentService, themeService)
            {
                RepositoryPath = @"C:\FakeRepo"
            };
            mainVm.RepoStatus = fakeGit.Status;
            await mainVm.CommitHistory.LoadCommitsAsync();
            await mainVm.WorkingChanges.LoadChangesAsync();

            mainVm.WorkingChanges.CommitMessage = "New feature work";
            await mainVm.WorkingChanges.CommitCommand.ExecuteAsync(null);

            // The new commit shows up in the graph immediately, without a full reload having
            // been asked for.
            Assert.Equal("New feature work", mainVm.CommitHistory.FilteredCommits[0].Subject);
            Assert.Equal("root1", mainVm.CommitHistory.FilteredCommits[1].Hash);

            // The push pill reflects it too, computed locally rather than via another git call.
            Assert.Equal(1, mainVm.RepoStatus.AheadCount);
        }

        [Fact]
        public void BranchTreeItem_CanCheckout_FalseForFoldersAndTheCurrentBranch()
        {
            var folder = new BranchTreeItem { IsFolder = true };
            var currentBranchLeaf = new BranchTreeItem { IsFolder = false, Branch = new GitBranch { Name = "develop", IsCurrent = true } };
            var otherBranchLeaf = new BranchTreeItem { IsFolder = false, Branch = new GitBranch { Name = "feature/x", IsCurrent = false } };

            Assert.False(folder.CanCheckout);
            Assert.False(currentBranchLeaf.CanCheckout);
            Assert.True(otherBranchLeaf.CanCheckout);
        }

        [Fact]
        public async Task MainViewModel_PullAsync_OpensChoiceDialogWhenDivergedButPullsDirectlyOnFastForward()
        {
            var fakeGit = new FakeGitService
            {
                Status = new GitRepoStatus { HasUpstream = true, AheadCount = 2, BehindCount = 3 }
            };
            var recentService = new RecentRepositoriesService();
            var themeService = new ThemeService();
            var mainVm = new MainViewModel(fakeGit, recentService, themeService)
            {
                RepositoryPath = @"C:\FakeRepo"
            };
            mainVm.RepoStatus = fakeGit.Status;

            // Diverged (ahead and behind both > 0) - should offer the choice, not pull yet.
            await mainVm.PullAsync();
            Assert.True(mainVm.IsPullChoiceDialogOpen);

            await mainVm.ConfirmPullRebaseCommand.ExecuteAsync(null);
            Assert.False(mainVm.IsPullChoiceDialogOpen);

            // A clean fast-forward case (nothing ahead) shouldn't prompt at all.
            mainVm.RepoStatus = new GitRepoStatus { HasUpstream = true, AheadCount = 0, BehindCount = 3 };
            await mainVm.PullAsync();
            Assert.False(mainVm.IsPullChoiceDialogOpen);
        }

        [Fact]
        public void SettingsViewModel_TogglesPersistViaInjectedSavers()
        {
            var savedMinimizeToTray = new List<bool>();
            var savedMinimizeOnClose = new List<bool>();
            var savedFolderClickMode = new List<bool>();

            var vm = new SettingsViewModel(
                new FakeGitService(), () => @"C:\FakeRepo",
                true, v => savedMinimizeToTray.Add(v),
                false, v => savedMinimizeOnClose.Add(v),
                true, v => savedFolderClickMode.Add(v));

            // Initial values come from the injected loaders, with no save triggered yet.
            Assert.True(vm.MinimizeToTray);
            Assert.False(vm.MinimizeOnClose);
            Assert.True(vm.FolderExpandOnSingleClick);
            Assert.Empty(savedMinimizeToTray);

            vm.MinimizeToTray = false;
            vm.MinimizeOnClose = true;
            vm.FolderExpandOnSingleClick = false;

            Assert.Equal(new[] { false }, savedMinimizeToTray);
            Assert.Equal(new[] { true }, savedMinimizeOnClose);
            Assert.Equal(new[] { false }, savedFolderClickMode);
        }

        [Fact]
        public async Task SettingsViewModel_GitIdentity_LoadsGlobalAndDetectsLocalOverride()
        {
            var fakeGit = new FakeGitService();
            fakeGit.GlobalConfig["user.name"] = "Global Name";
            fakeGit.GlobalConfig["user.email"] = "global@example.com";

            var vm = new SettingsViewModel(fakeGit, () => @"C:\FakeRepo", true, _ => { }, true, _ => { }, true, _ => { });
            await vm.LoadGitIdentityAsync();

            Assert.Equal("Global Name", vm.GlobalUserName);
            Assert.Equal("global@example.com", vm.GlobalUserEmail);
            Assert.False(vm.HasLocalIdentityOverride);

            // Saving a repo-local override doesn't touch the global config, and is detected as
            // an override on the next load.
            vm.LocalUserName = "Repo Name";
            vm.LocalUserEmail = "repo@example.com";
            await vm.SaveLocalIdentityOverrideCommand.ExecuteAsync(null);
            Assert.True(vm.HasLocalIdentityOverride);
            Assert.Equal("Global Name", fakeGit.GlobalConfig["user.name"]); // Unchanged.

            await vm.LoadGitIdentityAsync();
            Assert.True(vm.HasLocalIdentityOverride);
            Assert.Equal("Repo Name", vm.LocalUserName);

            // Removing it clears the local config and falls back to reporting no override.
            await vm.RemoveLocalIdentityOverrideCommand.ExecuteAsync(null);
            Assert.False(vm.HasLocalIdentityOverride);
            Assert.DoesNotContain("user.name", fakeGit.LocalConfig.Keys);
        }
    }
}
