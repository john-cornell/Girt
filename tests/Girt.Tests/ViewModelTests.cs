using System;
using System.Collections.Generic;
using System.IO;
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
        public bool? LastRequestedIgnoreWhitespace { get; set; }
        public string? LastDiffAgainstRef { get; set; }
        public Task<string> GetRawFileDiffAsync(string repoPath, string commitHash, string filePath, bool ignoreWhitespace = false, string? diffAgainstRef = null)
        {
            LastRequestedIgnoreWhitespace = ignoreWhitespace;
            LastDiffAgainstRef = diffAgainstRef;
            return Task.FromResult(RawDiff);
        }
        public Task<IReadOnlyList<GitFileDiff>> GetUnpushedDiffAsync(string repoPath) => Task.FromResult<IReadOnlyList<GitFileDiff>>(DiffFiles);
        public Task<string> GetRawUnpushedFileDiffAsync(string repoPath, string filePath, bool ignoreWhitespace = false)
        {
            LastRequestedIgnoreWhitespace = ignoreWhitespace;
            return Task.FromResult(RawDiff);
        }
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

        public Task<string> GetWorkingTreeFileDiffAsync(string repoPath, string filePath, bool isStaged, bool ignoreWhitespace = false)
        {
            LastRequestedIgnoreWhitespace = ignoreWhitespace;
            return Task.FromResult(RawDiff);
        }

        public string? LastWrittenFilePath { get; set; }
        public string? LastWrittenFileContent { get; set; }
        public Task<(bool Success, string Output)> WriteWorkingTreeFileAsync(string repoPath, string filePath, string content)
        {
            LastWrittenFilePath = filePath;
            LastWrittenFileContent = content;
            return Task.FromResult((true, "File updated"));
        }
        public bool NextPushSucceeds { get; set; } = true;
        public string NextPushOutput { get; set; } = "Pushed";
        public bool NextPullSucceeds { get; set; } = true;
        public bool PushCalled { get; set; }
        public Task<(bool Success, string Output)> PushAsync(string repoPath)
        {
            PushCalled = true;
            return Task.FromResult((NextPushSucceeds, NextPushOutput));
        }
        public bool PushSetUpstreamCalled { get; set; }
        public string? LastPushSetUpstreamBranch { get; set; }
        public Task<(bool Success, string Output)> PushSetUpstreamAsync(string repoPath, string branchName)
        {
            PushSetUpstreamCalled = true;
            LastPushSetUpstreamBranch = branchName;
            return Task.FromResult((NextPushSucceeds, NextPushOutput));
        }
        public bool PushToUpstreamBranchCalled { get; set; }
        public string? LastPushToUpstreamBranchName { get; set; }
        public Task<(bool Success, string Output)> PushToUpstreamBranchAsync(string repoPath, string upstreamBranchName)
        {
            PushToUpstreamBranchCalled = true;
            LastPushToUpstreamBranchName = upstreamBranchName;
            return Task.FromResult((NextPushSucceeds, NextPushOutput));
        }
        public Task<(bool Success, string Output)> PullAsync(string repoPath, bool rebase = false) => Task.FromResult(NextPullSucceeds ? (true, "Pulled") : (false, "Pull failed"));
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

        public Task<(bool Success, string Output)> RebaseAsync(string repoPath, string targetRef)
        {
            LastRebasedRef = targetRef;
            return Task.FromResult((true, "Rebased ref"));
        }

        public List<GitCommit> CommitsBetween { get; set; } = new();
        public List<GitFileDiff> DiffStatBetween { get; set; } = new();
        public List<MergeConflictFile> ConflictedFiles { get; set; } = new();
        public string ConflictOursDiff { get; set; } = "";
        public string ConflictTheirsDiff { get; set; } = "";
        public bool MergeAborted { get; set; }
        public bool MergeContinued { get; set; }
        public bool NextMergeSucceeds { get; set; } = true;

        public Task<(bool Success, string Output)> MergeAsync(string repoPath, string targetRef, bool squash, bool noFf)
        {
            LastMergedRef = targetRef;
            return Task.FromResult((NextMergeSucceeds, NextMergeSucceeds ? "Merged ref" : "CONFLICT (content): Merge conflict"));
        }

        public Task<IReadOnlyList<GitCommit>> GetCommitsBetweenAsync(string repoPath, string fromRef, string toRef, int maxCount = 200) =>
            Task.FromResult<IReadOnlyList<GitCommit>>(CommitsBetween);

        public Task<IReadOnlyList<GitFileDiff>> GetDiffStatBetweenAsync(string repoPath, string fromRef, string toRef) =>
            Task.FromResult<IReadOnlyList<GitFileDiff>>(DiffStatBetween);

        public Task<IReadOnlyList<MergeConflictFile>> GetConflictedFilesAsync(string repoPath) =>
            Task.FromResult<IReadOnlyList<MergeConflictFile>>(ConflictedFiles);

        public Task<(string OursDiff, string TheirsDiff)> GetConflictDiffsAsync(string repoPath, string filePath) =>
            Task.FromResult((ConflictOursDiff, ConflictTheirsDiff));

        public string? LastMergeToolFilePath { get; set; }
        public bool NextMergeToolSucceeds { get; set; } = true;
        public string NextMergeToolOutput { get; set; } = "No merge tool configured";
        public Task<(bool Success, string Output)> LaunchMergeToolAsync(string repoPath, string filePath)
        {
            LastMergeToolFilePath = filePath;
            return Task.FromResult((NextMergeToolSucceeds, NextMergeToolSucceeds ? "Merge tool exited" : NextMergeToolOutput));
        }

        public bool ConfigureKDiff3Called { get; set; }
        public bool NextConfigureKDiff3Succeeds { get; set; } = true;
        public Task<(bool Success, string Output)> ConfigureKDiff3AsMergeToolAsync(string repoPath)
        {
            ConfigureKDiff3Called = true;
            if (NextConfigureKDiff3Succeeds)
            {
                // Simulates the retry succeeding now that merge.tool is actually configured.
                NextMergeToolSucceeds = true;
            }
            return Task.FromResult((NextConfigureKDiff3Succeeds, NextConfigureKDiff3Succeeds ? "Configured merge.tool = kdiff3" : "kdiff3.exe not found"));
        }

        public Task<(bool Success, string Output)> AbortMergeAsync(string repoPath)
        {
            MergeAborted = true;
            return Task.FromResult((true, "Aborted"));
        }

        public Task<(bool Success, string Output)> ContinueMergeAsync(string repoPath)
        {
            MergeContinued = true;
            return Task.FromResult((true, "Merge continued"));
        }

        public bool RebaseAborted { get; set; }
        public bool RebaseContinued { get; set; }
        public bool ForcePushWithLeaseCalled { get; set; }
        public bool NextForcePushSucceeds { get; set; } = true;

        public Task<(bool Success, string Output)> AbortRebaseAsync(string repoPath)
        {
            RebaseAborted = true;
            return Task.FromResult((true, "Rebase aborted"));
        }

        public Task<(bool Success, string Output)> ContinueRebaseAsync(string repoPath)
        {
            RebaseContinued = true;
            return Task.FromResult((true, "Rebase continued"));
        }

        public Task<(bool Success, string Output)> ForcePushWithLeaseAsync(string repoPath)
        {
            ForcePushWithLeaseCalled = true;
            return Task.FromResult((NextForcePushSucceeds, NextForcePushSucceeds ? "Force pushed" : "Force push failed"));
        }
    }

    public class ViewModelTests
    {
        // ThemeService defaults to the real, shared %APPDATA%\Girt\settings.json - the same
        // file the actual installed app uses. Every test must use this instead of `new
        // ThemeService()` directly, or it silently reads/writes the user's real settings and
        // cross-contaminates other tests that happen to run against the same file.
        private static ThemeService CreateIsolatedThemeService() =>
            new(Path.Combine(Path.GetTempPath(), $"GirtTestSettings_{Guid.NewGuid():N}.json"));

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
            var themeService = CreateIsolatedThemeService();
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
        public async Task WorkingChangesViewModel_StageSelectedFiles_StagesEveryGivenFile()
        {
            var fakeGit = new FakeGitService();
            fakeGit.Changes.UnstagedFiles.Add(new GitWorkingFile { Path = "a.cs" });
            fakeGit.Changes.UnstagedFiles.Add(new GitWorkingFile { Path = "b.cs" });
            fakeGit.Changes.UnstagedFiles.Add(new GitWorkingFile { Path = "c.cs" });

            var vm = new WorkingChangesViewModel(fakeGit, () => @"C:\FakeRepo", _ => Task.CompletedTask, false, _ => { });
            await vm.LoadChangesAsync();

            var toStage = vm.UnstagedFiles.Where(f => f.Path != "c.cs").ToList();
            await vm.StageSelectedFilesCommand.ExecuteAsync(toStage);

            Assert.Equal(2, vm.StagedFiles.Count);
            Assert.Single(vm.UnstagedFiles);
            Assert.Equal("c.cs", vm.UnstagedFiles[0].Path);
        }

        [Fact]
        public async Task WorkingChangesViewModel_UnstageSelectedFiles_UnstagesEveryGivenFile()
        {
            var fakeGit = new FakeGitService();
            fakeGit.Changes.StagedFiles.Add(new GitWorkingFile { Path = "a.cs", IsStaged = true });
            fakeGit.Changes.StagedFiles.Add(new GitWorkingFile { Path = "b.cs", IsStaged = true });

            var vm = new WorkingChangesViewModel(fakeGit, () => @"C:\FakeRepo", _ => Task.CompletedTask, false, _ => { });
            await vm.LoadChangesAsync();

            await vm.UnstageSelectedFilesCommand.ExecuteAsync(vm.StagedFiles.ToList());

            Assert.Empty(vm.StagedFiles);
            Assert.Equal(2, vm.UnstagedFiles.Count);
        }

        [Fact]
        public async Task WorkingChangesViewModel_DiscardSelectedFiles_ConfirmsOnceThenDiscardsAll()
        {
            var fakeGit = new FakeGitService();
            fakeGit.Changes.UnstagedFiles.Add(new GitWorkingFile { Path = "a.cs" });
            fakeGit.Changes.UnstagedFiles.Add(new GitWorkingFile { Path = "b.cs" });

            var confirmCallCount = 0;
            var vm = new WorkingChangesViewModel(fakeGit, () => @"C:\FakeRepo", _ => Task.CompletedTask, false, _ => { })
            {
                ConfirmDiscardAction = _ => { confirmCallCount++; return true; }
            };
            await vm.LoadChangesAsync();

            await vm.DiscardSelectedFilesCommand.ExecuteAsync(vm.UnstagedFiles.ToList());

            Assert.Equal(1, confirmCallCount);
            Assert.Empty(vm.UnstagedFiles);
        }

        [Fact]
        public async Task WorkingChangesViewModel_DiscardSelectedFiles_ConfirmNo_DiscardsNothing()
        {
            var fakeGit = new FakeGitService();
            fakeGit.Changes.UnstagedFiles.Add(new GitWorkingFile { Path = "a.cs" });

            var vm = new WorkingChangesViewModel(fakeGit, () => @"C:\FakeRepo", _ => Task.CompletedTask, false, _ => { })
            {
                ConfirmDiscardAction = _ => false
            };
            await vm.LoadChangesAsync();

            await vm.DiscardSelectedFilesCommand.ExecuteAsync(vm.UnstagedFiles.ToList());

            Assert.Single(vm.UnstagedFiles);
        }

        [Fact]
        public async Task WorkingChangesViewModel_Commit_NoStagedFiles_ConfirmYes_StagesAllThenCommits()
        {
            var fakeGit = new FakeGitService();
            fakeGit.Changes.UnstagedFiles.Add(new GitWorkingFile { Path = "app.cs", IsStaged = false });

            var vm = new WorkingChangesViewModel(fakeGit, () => @"C:\FakeRepo", _ => Task.CompletedTask, false, _ => { });
            vm.ConfirmStageAllFirstAction = _ => true;
            await vm.LoadChangesAsync();
            Assert.Empty(vm.StagedFiles);

            vm.CommitSubject = "Add app file";
            await vm.CommitAsync();

            Assert.Equal("Add app file", fakeGit.LastCommitMessage);
            Assert.Empty(vm.StagedFiles);
            Assert.Empty(vm.UnstagedFiles);
        }

        [Fact]
        public async Task WorkingChangesViewModel_Commit_NoStagedFiles_ConfirmNo_DoesNotStageOrCommit()
        {
            var fakeGit = new FakeGitService();
            fakeGit.Changes.UnstagedFiles.Add(new GitWorkingFile { Path = "app.cs", IsStaged = false });

            var vm = new WorkingChangesViewModel(fakeGit, () => @"C:\FakeRepo", _ => Task.CompletedTask, false, _ => { });
            vm.ConfirmStageAllFirstAction = _ => false;
            await vm.LoadChangesAsync();

            vm.CommitSubject = "Add app file";
            await vm.CommitAsync();

            Assert.Null(fakeGit.LastCommitMessage);
            Assert.Empty(vm.StagedFiles);
            Assert.Single(vm.UnstagedFiles);
        }

        [Fact]
        public async Task WorkingChangesViewModel_StashStaged_NoStagedFiles_ConfirmYes_StagesAllThenStashes()
        {
            var fakeGit = new FakeGitService();
            fakeGit.Changes.UnstagedFiles.Add(new GitWorkingFile { Path = "app.cs", IsStaged = false });

            var vm = new WorkingChangesViewModel(fakeGit, () => @"C:\FakeRepo", _ => Task.CompletedTask, false, _ => { });
            vm.ConfirmStageAllFirstAction = _ => true;
            await vm.LoadChangesAsync();
            Assert.Empty(vm.StagedFiles);

            await vm.StashStagedAsync();

            Assert.Equal(1, vm.StashCount);
            Assert.Empty(vm.StagedFiles);
            Assert.Empty(vm.UnstagedFiles);
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
            var themeService = CreateIsolatedThemeService();
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
            var themeService = CreateIsolatedThemeService();
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
        public async Task MainViewModel_SelectingBranch_SelectsItsTipCommitInTheGraph()
        {
            var tip = new GitCommit { Hash = "feat2", ParentHashes = new List<string> { "feat1" }, Subject = "Feature commit 2" };
            var feat1 = new GitCommit { Hash = "feat1", ParentHashes = new List<string> { "root1" }, Subject = "Feature commit 1" };
            var root1 = new GitCommit { Hash = "root1", Subject = "Root" };
            var fakeGit = new FakeGitService
            {
                Commits = new List<GitCommit> { tip, feat1, root1 }
            };

            var recentService = new RecentRepositoriesService();
            var themeService = CreateIsolatedThemeService();
            var mainVm = new MainViewModel(fakeGit, recentService, themeService)
            {
                RepositoryPath = @"C:\FakeRepo"
            };
            await mainVm.CommitHistory.LoadCommitsAsync();
            // Loading auto-selects the top-of-list commit ("feat2") - pick a branch pointing
            // somewhere else in the history so selecting it is a real, observable change.
            Assert.Equal("feat2", mainVm.CommitHistory.SelectedCommit?.Hash);

            // The BranchList->CommitHistory hook (MainViewModel's constructor) runs as an async
            // void PropertyChanged handler, same fire-and-forget shape as
            // WorkingChangesViewModel.OnSelectedFileChanged elsewhere in this file - give it a
            // moment to actually finish before asserting.
            mainVm.BranchList.SelectedBranch = new GitBranch { Name = "feature/x", TipCommitHash = "root1" };
            await Task.Delay(50);

            Assert.Equal("root1", mainVm.CommitHistory.SelectedCommit?.Hash);
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
            var themeService = CreateIsolatedThemeService();
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
        public async Task MainViewModel_IgnoreWhitespaceInDiffs_PropagatesAndRefreshesCurrentDiff()
        {
            var fakeGit = new FakeGitService();
            fakeGit.Changes.UnstagedFiles.Add(new GitWorkingFile { Path = "a.cs" });

            var recentService = new RecentRepositoriesService();
            var themeService = CreateIsolatedThemeService();
            var mainVm = new MainViewModel(fakeGit, recentService, themeService)
            {
                RepositoryPath = @"C:\FakeRepo"
            };

            await mainVm.WorkingChanges.LoadChangesAsync();
            Assert.False(fakeGit.LastRequestedIgnoreWhitespace);

            // Toggling re-fetches the currently displayed diff immediately, with the new flag.
            mainVm.IgnoreWhitespaceInDiffs = true;
            await Task.Delay(50); // RefreshDiffAsync is fired-and-forgotten from the partial method.

            Assert.True(fakeGit.LastRequestedIgnoreWhitespace);
        }

        [Fact]
        public void GitCommit_IsCurrentHead_TrueOnlyWhenARefBadgeIsMarkedCurrent()
        {
            var noRefs = new GitCommit { Hash = "aaa" };
            var otherBranch = new GitCommit
            {
                Hash = "bbb",
                Refs = new List<GitRefBadge> { new() { Name = "feature/x", RefType = GitRefType.LocalBranch, IsCurrentHead = false } }
            };
            var checkedOut = new GitCommit
            {
                Hash = "ccc",
                Refs = new List<GitRefBadge> { new() { Name = "develop", RefType = GitRefType.LocalBranch, IsCurrentHead = true } }
            };

            Assert.False(noRefs.IsCurrentHead);
            Assert.False(otherBranch.IsCurrentHead);
            Assert.True(checkedOut.IsCurrentHead);
        }

        [Fact]
        public async Task MainViewModel_ShowWorkingChangesView_SyncsUncommittedCountPill()
        {
            // Regression: switching to the Working Changes tab (or the working-tree file
            // watcher's debounced refresh while already on it) used to call
            // WorkingChanges.LoadChangesAsync() without also updating RepoStatus.
            // UncommittedCount - the file list was correct, but the toolbar's "N to commit"
            // pill kept showing whatever stale value it had before (e.g. "0 to commit" next to
            // a panel full of real unstaged files).
            var fakeGit = new FakeGitService
            {
                Status = new GitRepoStatus { HasUpstream = true, UncommittedCount = 0 }
            };
            fakeGit.Changes.UnstagedFiles.Add(new GitWorkingFile { Path = "a.cs" });
            fakeGit.Changes.UnstagedFiles.Add(new GitWorkingFile { Path = "b.cs" });
            fakeGit.Changes.StagedFiles.Add(new GitWorkingFile { Path = "c.cs", IsStaged = true });

            var recentService = new RecentRepositoriesService();
            var themeService = CreateIsolatedThemeService();
            var mainVm = new MainViewModel(fakeGit, recentService, themeService)
            {
                RepositoryPath = @"C:\FakeRepo"
            };
            mainVm.RepoStatus = fakeGit.Status;

            Assert.Equal(0, mainVm.RepoStatus.UncommittedCount);

            await mainVm.ShowWorkingChangesViewAsync();

            Assert.Equal(3, mainVm.RepoStatus.UncommittedCount);
        }

        [Fact]
        public async Task MainViewModel_PullAsync_OpensChoiceDialogWhenDivergedButPullsDirectlyOnFastForward()
        {
            var fakeGit = new FakeGitService
            {
                Status = new GitRepoStatus { HasUpstream = true, AheadCount = 2, BehindCount = 3 }
            };
            var recentService = new RecentRepositoriesService();
            var themeService = CreateIsolatedThemeService();
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
        public async Task MainViewModel_PushRejected_NonFastForward_OpensRecoveryDialogInsteadOfPlainError()
        {
            var fakeGit = new FakeGitService
            {
                NextPushSucceeds = false,
                NextPushOutput = "! [rejected]        develop -> develop (fetch first)\nerror: failed to push some refs"
            };
            var mainVm = new MainViewModel(fakeGit, new RecentRepositoriesService(), CreateIsolatedThemeService())
            {
                RepositoryPath = @"C:\FakeRepo"
            };

            await mainVm.PushCommand.ExecuteAsync(null);

            Assert.True(mainVm.IsPushRejectedDialogOpen);
        }

        [Fact]
        public async Task MainViewModel_PushRejected_NoUpstream_OpensPublishBranchDialogInsteadOfPlainError()
        {
            var fakeGit = new FakeGitService
            {
                NextPushSucceeds = false,
                NextPushOutput = "fatal: The current branch feature-x has no upstream branch.\n" +
                                 "To push the current branch and set the remote as upstream, use\n\n" +
                                 "    git push --set-upstream origin feature-x"
            };
            var mainVm = new MainViewModel(fakeGit, new RecentRepositoriesService(), CreateIsolatedThemeService())
            {
                RepositoryPath = @"C:\FakeRepo",
                CurrentBranch = "feature-x"
            };

            await mainVm.PushCommand.ExecuteAsync(null);

            Assert.True(mainVm.IsPushNoUpstreamDialogOpen);
            Assert.False(mainVm.IsPushRejectedDialogOpen);
        }

        [Fact]
        public async Task MainViewModel_ConfirmPushSetUpstream_PublishesCurrentBranchToOrigin()
        {
            var fakeGit = new FakeGitService
            {
                NextPushSucceeds = false,
                NextPushOutput = "fatal: The current branch feature-x has no upstream branch."
            };
            var mainVm = new MainViewModel(fakeGit, new RecentRepositoriesService(), CreateIsolatedThemeService())
            {
                RepositoryPath = @"C:\FakeRepo",
                CurrentBranch = "feature-x"
            };
            await mainVm.PushCommand.ExecuteAsync(null);
            Assert.True(mainVm.IsPushNoUpstreamDialogOpen);

            fakeGit.NextPushSucceeds = true;
            await mainVm.ConfirmPushSetUpstreamCommand.ExecuteAsync(null);

            Assert.True(fakeGit.PushSetUpstreamCalled);
            Assert.Equal("feature-x", fakeGit.LastPushSetUpstreamBranch);
            Assert.False(mainVm.IsPushNoUpstreamDialogOpen);
        }

        [Fact]
        public async Task MainViewModel_PushAsync_UpstreamBranchNameMismatch_OpensBranchMismatchDialog()
        {
            var fakeGit = new FakeGitService
            {
                NextPushSucceeds = false,
                NextPushOutput = "fatal: The upstream branch of your current branch does not match\n" +
                                 "the name of your current branch.  To push to the upstream branch\n" +
                                 "on the remote, use\n\n    git push origin HEAD:develop\n"
            };
            var mainVm = new MainViewModel(fakeGit, new RecentRepositoriesService(), CreateIsolatedThemeService())
            {
                RepositoryPath = @"C:\FakeRepo",
                CurrentBranch = "BB-19962-optimize-fee-updates",
                RepoStatus = new GitRepoStatus { UpstreamBranch = "origin/develop" }
            };

            await mainVm.PushCommand.ExecuteAsync(null);

            Assert.True(mainVm.IsPushBranchMismatchDialogOpen);
            Assert.False(mainVm.IsPushNoUpstreamDialogOpen);
            Assert.False(mainVm.IsPushRejectedDialogOpen);
            Assert.Equal("develop", mainVm.UpstreamBranchShortName);
        }

        [Fact]
        public async Task MainViewModel_ConfirmPushToUpstreamBranch_PushesToConfiguredUpstreamName()
        {
            var fakeGit = new FakeGitService
            {
                NextPushSucceeds = false,
                NextPushOutput = "fatal: The upstream branch of your current branch does not match"
            };
            var mainVm = new MainViewModel(fakeGit, new RecentRepositoriesService(), CreateIsolatedThemeService())
            {
                RepositoryPath = @"C:\FakeRepo",
                CurrentBranch = "BB-19962-optimize-fee-updates",
                RepoStatus = new GitRepoStatus { UpstreamBranch = "origin/develop" }
            };
            await mainVm.PushCommand.ExecuteAsync(null);
            Assert.True(mainVm.IsPushBranchMismatchDialogOpen);

            fakeGit.NextPushSucceeds = true;
            await mainVm.ConfirmPushToUpstreamBranchCommand.ExecuteAsync(null);

            Assert.True(fakeGit.PushToUpstreamBranchCalled);
            Assert.Equal("develop", fakeGit.LastPushToUpstreamBranchName);
            Assert.False(mainVm.IsPushBranchMismatchDialogOpen);
        }

        [Fact]
        public async Task MainViewModel_ConfirmPushToSameNameBranch_PublishesCurrentBranchToOrigin()
        {
            var fakeGit = new FakeGitService
            {
                NextPushSucceeds = false,
                NextPushOutput = "fatal: The upstream branch of your current branch does not match"
            };
            var mainVm = new MainViewModel(fakeGit, new RecentRepositoriesService(), CreateIsolatedThemeService())
            {
                RepositoryPath = @"C:\FakeRepo",
                CurrentBranch = "BB-19962-optimize-fee-updates",
                RepoStatus = new GitRepoStatus { UpstreamBranch = "origin/develop" }
            };
            await mainVm.PushCommand.ExecuteAsync(null);
            Assert.True(mainVm.IsPushBranchMismatchDialogOpen);

            fakeGit.NextPushSucceeds = true;
            await mainVm.ConfirmPushToSameNameBranchCommand.ExecuteAsync(null);

            Assert.True(fakeGit.PushSetUpstreamCalled);
            Assert.Equal("BB-19962-optimize-fee-updates", fakeGit.LastPushSetUpstreamBranch);
            Assert.False(mainVm.IsPushBranchMismatchDialogOpen);
        }

        [Fact]
        public async Task MainViewModel_PushRejected_PullMerge_AutoRetriesPushOnceClean()
        {
            var fakeGit = new FakeGitService
            {
                NextPushSucceeds = false,
                NextPushOutput = "! [rejected]        develop -> develop (fetch first)"
            };
            var mainVm = new MainViewModel(fakeGit, new RecentRepositoriesService(), CreateIsolatedThemeService())
            {
                RepositoryPath = @"C:\FakeRepo"
            };

            await mainVm.PushCommand.ExecuteAsync(null);
            Assert.True(mainVm.IsPushRejectedDialogOpen);

            // Once the pull lands clean, the push should be retried automatically instead of
            // leaving the user to notice and push again themselves.
            fakeGit.NextPushSucceeds = true;
            await mainVm.ConfirmPushRejectedPullMergeCommand.ExecuteAsync(null);

            Assert.False(mainVm.IsPushRejectedDialogOpen);
            Assert.False(mainVm.IsMergeConflictDialogOpen);
        }

        [Fact]
        public async Task MainViewModel_PushRejected_PullConflict_DoesNotAutoRetryPush()
        {
            var fakeGit = new FakeGitService
            {
                NextPushSucceeds = false,
                NextPushOutput = "! [rejected]        develop -> develop (fetch first)",
                NextPullSucceeds = false,
                ConflictedFiles = { new MergeConflictFile { Path = "a.cs", ConflictType = MergeConflictType.BothModified } }
            };
            var mainVm = new MainViewModel(fakeGit, new RecentRepositoriesService(), CreateIsolatedThemeService())
            {
                RepositoryPath = @"C:\FakeRepo"
            };

            await mainVm.PushCommand.ExecuteAsync(null);
            await mainVm.ConfirmPushRejectedPullMergeCommand.ExecuteAsync(null);

            // The conflict dialog should be open, and the push must not have been retried
            // against a half-resolved merge - if it had, the still-failing fake push would have
            // reopened the push-rejected dialog instead.
            Assert.True(mainVm.IsMergeConflictDialogOpen);
            Assert.False(mainVm.IsPushRejectedDialogOpen);
        }

        [Fact]
        public async Task MainViewModel_PullRebase_OnConflict_UsesRebaseAbortAndContinueNotMerge()
        {
            var fakeGit = new FakeGitService
            {
                NextPullSucceeds = false,
                ConflictedFiles = { new MergeConflictFile { Path = "a.cs", ConflictType = MergeConflictType.BothModified } }
            };
            var mainVm = new MainViewModel(fakeGit, new RecentRepositoriesService(), CreateIsolatedThemeService())
            {
                RepositoryPath = @"C:\FakeRepo"
            };

            await mainVm.ConfirmPullRebaseCommand.ExecuteAsync(null);
            Assert.True(mainVm.IsMergeConflictDialogOpen);
            Assert.Equal("Abort Rebase", mainVm.AbortMergeButtonLabel);
            Assert.Equal("Continue Rebase", mainVm.ContinueMergeButtonLabel);

            await mainVm.MarkConflictFileResolvedCommand.ExecuteAsync(mainVm.MergeConflictFiles[0]);
            Assert.True(mainVm.CanContinueMerge);

            await mainVm.ContinueMergeCommand.ExecuteAsync(null);

            Assert.True(fakeGit.RebaseContinued);
            Assert.False(fakeGit.MergeContinued);
        }

        [Fact]
        public async Task MainViewModel_PushAsync_NothingToPushWithUncommittedChanges_DoesNotCallGitPush()
        {
            var fakeGit = new FakeGitService();
            var mainVm = new MainViewModel(fakeGit, new RecentRepositoriesService(), CreateIsolatedThemeService())
            {
                RepositoryPath = @"C:\FakeRepo",
                RepoStatus = new GitRepoStatus { HasUpstream = true, AheadCount = 0, UncommittedCount = 3 }
            };
            // Avoids a real MessageBox.Show - see WorkingChangesViewModel's ConfirmStashAction
            // for the same test-only override pattern.
            mainVm.ConfirmNothingToPushAction = _ => false;

            await mainVm.PushCommand.ExecuteAsync(null);

            Assert.False(fakeGit.PushCalled);
        }

        [Fact]
        public async Task MainViewModel_PushAsync_NothingToPushWithUncommittedChanges_ConfirmYes_StagesAllAndSwitchesToWorkingChangesView()
        {
            var fakeGit = new FakeGitService();
            fakeGit.Changes.UnstagedFiles.Add(new GitWorkingFile { Path = "a.cs" });
            fakeGit.Changes.UnstagedFiles.Add(new GitWorkingFile { Path = "b.cs" });
            var mainVm = new MainViewModel(fakeGit, new RecentRepositoriesService(), CreateIsolatedThemeService())
            {
                RepositoryPath = @"C:\FakeRepo",
                RepoStatus = new GitRepoStatus { HasUpstream = true, AheadCount = 0, UncommittedCount = 3 },
                ConfirmNothingToPushAction = _ => true
            };

            await mainVm.PushCommand.ExecuteAsync(null);

            Assert.False(fakeGit.PushCalled);
            Assert.Equal(ActiveViewMode.WorkingChanges, mainVm.CurrentView);
            // Confirming should stage everything, not just navigate and leave the user to do it
            // themselves - all that should be left is typing a commit message.
            Assert.Empty(mainVm.WorkingChanges.UnstagedFiles);
            Assert.Equal(2, mainVm.WorkingChanges.StagedFiles.Count);
        }

        [Fact]
        public async Task MainViewModel_PushAsync_NothingAheadButNoUncommittedChanges_StillPushesNormally()
        {
            // "Everything up-to-date" is a legitimate, harmless push attempt here - only the
            // combination of nothing-ahead AND uncommitted changes should be intercepted.
            var fakeGit = new FakeGitService();
            var mainVm = new MainViewModel(fakeGit, new RecentRepositoriesService(), CreateIsolatedThemeService())
            {
                RepositoryPath = @"C:\FakeRepo",
                RepoStatus = new GitRepoStatus { HasUpstream = true, AheadCount = 0, UncommittedCount = 0 }
            };

            await mainVm.PushCommand.ExecuteAsync(null);

            Assert.True(fakeGit.PushCalled);
        }

        [Fact]
        public async Task MainViewModel_PushAsync_NoUpstreamYet_StillPushesEvenWithUncommittedChanges()
        {
            // A brand-new local branch with no upstream never gets a real AheadCount computed
            // (see GitCliService.GetRepoStatusAsync) - don't misread that as "nothing to push"
            // and block a legitimate first push just because the working tree is also dirty.
            var fakeGit = new FakeGitService();
            var mainVm = new MainViewModel(fakeGit, new RecentRepositoriesService(), CreateIsolatedThemeService())
            {
                RepositoryPath = @"C:\FakeRepo",
                RepoStatus = new GitRepoStatus { HasUpstream = false, AheadCount = 0, UncommittedCount = 3 }
            };

            await mainVm.PushCommand.ExecuteAsync(null);

            Assert.True(fakeGit.PushCalled);
        }

        [Fact]
        public async Task WorkingChangesViewModel_RevertSelectedLines_RewritesOnlySelectedLines()
        {
            var fakeGit = new FakeGitService
            {
                RawDiff = @"diff --git a/test.txt b/test.txt
index 1234567..89abcdef 100644
--- a/test.txt
+++ b/test.txt
@@ -1,3 +1,4 @@
 line 1
-line 2
+line 2 modified
+line 2.5 new
 line 3"
            };

            var tempDir = Path.Combine(Path.GetTempPath(), $"GirtRevertTest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                await File.WriteAllTextAsync(Path.Combine(tempDir, "test.txt"), "line 1\nline 2 modified\nline 2.5 new\nline 3");

                var vm = new WorkingChangesViewModel(fakeGit, () => tempDir, _ => Task.CompletedTask, false, _ => { });
                var file = new GitWorkingFile { Path = "test.txt", IsStaged = false, Status = FileStatusType.Modified };

                vm.SelectedFile = file;
                await Task.Delay(50); // fire-and-forget LoadFileDiffAsync from OnSelectedFileChanged

                Assert.False(vm.CanRevertSelectedLines);

                vm.DiffLines.Single(l => l.Type == DiffLineType.Added && l.Text == "+line 2.5 new").IsSelected = true;
                Assert.True(vm.CanRevertSelectedLines);

                await vm.RevertSelectedLinesCommand.ExecuteAsync(null);

                Assert.Equal("test.txt", fakeGit.LastWrittenFilePath);
                Assert.Equal("line 1\nline 2 modified\nline 3", fakeGit.LastWrittenFileContent);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public async Task WorkingChangesViewModel_SelectingOnlyContextLines_DoesNotEnableRevert()
        {
            // Selection now covers Context lines too (useful for copying a range that includes
            // unchanged lines), so CanRevertSelectedLines must specifically require a selected
            // Added/Deleted line - selecting only unchanged context has nothing to revert, even
            // though DiffLines.Any(l => l.IsSelected) would still be true.
            var fakeGit = new FakeGitService
            {
                RawDiff = @"diff --git a/test.txt b/test.txt
index 1234567..89abcdef 100644
--- a/test.txt
+++ b/test.txt
@@ -1,3 +1,4 @@
 line 1
-line 2
+line 2 modified
+line 2.5 new
 line 3"
            };

            var tempDir = Path.Combine(Path.GetTempPath(), $"GirtRevertTest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                await File.WriteAllTextAsync(Path.Combine(tempDir, "test.txt"), "line 1\nline 2 modified\nline 2.5 new\nline 3");

                var vm = new WorkingChangesViewModel(fakeGit, () => tempDir, _ => Task.CompletedTask, false, _ => { });
                var file = new GitWorkingFile { Path = "test.txt", IsStaged = false, Status = FileStatusType.Modified };

                vm.SelectedFile = file;
                await Task.Delay(50);

                vm.DiffLines.Single(l => l.Type == DiffLineType.Context && l.Text == " line 1").IsSelected = true;

                Assert.False(vm.CanRevertSelectedLines);

                await vm.RevertSelectedLinesCommand.ExecuteAsync(null);

                Assert.Null(fakeGit.LastWrittenFilePath);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public async Task WorkingChangesViewModel_RevertSelectedLines_DoesNothingForStagedFiles()
        {
            // A staged file's diff is index-vs-HEAD, not working-tree-vs-index - "revert" there
            // would mean something different (line-level unstage), which isn't implemented, so
            // this must be a no-op rather than corrupt the working tree file.
            var fakeGit = new FakeGitService
            {
                RawDiff = "diff --git a/x b/x\nindex 1234567..89abcdef 100644\n--- a/x\n+++ b/x\n@@ -1 +1 @@\n-old\n+new"
            };
            var vm = new WorkingChangesViewModel(fakeGit, () => @"C:\FakeRepo", _ => Task.CompletedTask, false, _ => { });
            var file = new GitWorkingFile { Path = "x", IsStaged = true, Status = FileStatusType.Modified };

            vm.SelectedFile = file;
            await Task.Delay(50);
            foreach (var l in vm.DiffLines) l.IsSelected = true;

            Assert.False(vm.CanRevertSelectedLines);

            await vm.RevertSelectedLinesCommand.ExecuteAsync(null);

            Assert.Null(fakeGit.LastWrittenFilePath);
        }

        [Fact]
        public void SettingsViewModel_TogglesPersistViaInjectedSavers()
        {
            var savedMinimizeToTray = new List<bool>();
            var savedMinimizeOnClose = new List<bool>();
            var savedFolderClickMode = new List<bool>();
            var savedEnableTimingLogs = new List<bool>();

            var vm = new SettingsViewModel(
                new FakeGitService(), () => @"C:\FakeRepo",
                true, v => savedMinimizeToTray.Add(v),
                false, v => savedMinimizeOnClose.Add(v),
                true, v => savedFolderClickMode.Add(v),
                false, v => savedEnableTimingLogs.Add(v));

            // Initial values come from the injected loaders, with no save triggered yet.
            Assert.True(vm.MinimizeToTray);
            Assert.False(vm.MinimizeOnClose);
            Assert.True(vm.FolderExpandOnSingleClick);
            Assert.False(vm.EnableTimingLogs);
            Assert.Empty(savedMinimizeToTray);

            vm.MinimizeToTray = false;
            vm.MinimizeOnClose = true;
            vm.FolderExpandOnSingleClick = false;
            vm.EnableTimingLogs = true;

            Assert.Equal(new[] { false }, savedMinimizeToTray);
            Assert.Equal(new[] { true }, savedMinimizeOnClose);
            Assert.Equal(new[] { false }, savedFolderClickMode);
            Assert.Equal(new[] { true }, savedEnableTimingLogs);
            // Toggling the setting also flips the live static flag LogService.Timing reads -
            // no separate app-restart step needed to take effect.
            Assert.True(LogService.EnableTimingLogs);

            vm.EnableTimingLogs = false;
            Assert.False(LogService.EnableTimingLogs);
        }

        [Fact]
        public async Task SettingsViewModel_GitIdentity_LoadsGlobalAndDetectsLocalOverride()
        {
            var fakeGit = new FakeGitService();
            fakeGit.GlobalConfig["user.name"] = "Global Name";
            fakeGit.GlobalConfig["user.email"] = "global@example.com";

            var vm = new SettingsViewModel(fakeGit, () => @"C:\FakeRepo", true, _ => { }, true, _ => { }, true, _ => { }, false, _ => { });
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

        [Fact]
        public async Task SettingsViewModel_MergeTool_LoadsSavesAndSupportsPresetPicker()
        {
            var fakeGit = new FakeGitService();
            var vm = new SettingsViewModel(fakeGit, () => @"C:\FakeRepo", true, _ => { }, true, _ => { }, true, _ => { }, false, _ => { });

            // Nothing configured yet.
            await vm.LoadMergeToolSettingsAsync();
            Assert.Equal(string.Empty, vm.MergeToolName);
            Assert.Equal(string.Empty, vm.MergeToolPath);

            // Clicking a preset chip sets the name and clears any stale path from a
            // previously-selected tool.
            vm.MergeToolPath = @"C:\old\tool.exe";
            vm.SetMergeToolPresetCommand.Execute("meld");
            Assert.Equal("meld", vm.MergeToolName);
            Assert.Equal(string.Empty, vm.MergeToolPath);

            // Saving writes merge.tool globally, and only writes mergetool.<name>.path when a
            // path override was actually given.
            await vm.SaveMergeToolCommand.ExecuteAsync(null);
            Assert.Equal("meld", fakeGit.GlobalConfig["merge.tool"]);
            Assert.DoesNotContain("mergetool.meld.path", fakeGit.GlobalConfig.Keys);

            vm.MergeToolPath = @"C:\Program Files\Meld\meld.exe";
            await vm.SaveMergeToolCommand.ExecuteAsync(null);
            Assert.Equal("C:/Program Files/Meld/meld.exe", fakeGit.GlobalConfig["mergetool.meld.path"]);

            // A later load round-trips both values back out.
            var vm2 = new SettingsViewModel(fakeGit, () => @"C:\FakeRepo", true, _ => { }, true, _ => { }, true, _ => { }, false, _ => { });
            await vm2.LoadMergeToolSettingsAsync();
            Assert.Equal("meld", vm2.MergeToolName);
            Assert.Equal("C:/Program Files/Meld/meld.exe", vm2.MergeToolPath);
        }

        [Fact]
        public async Task MainViewModel_MergeIntoCurrentBranch_ShowsPreviewBeforeMerging()
        {
            var fakeGit = new FakeGitService
            {
                CommitsBetween = { new GitCommit { Hash = "abc1234", Subject = "Add feature" } },
                DiffStatBetween = { new GitFileDiff { Path = "a.cs", Additions = 3, Deletions = 1 } }
            };
            var mainVm = new MainViewModel(fakeGit, new RecentRepositoriesService(), CreateIsolatedThemeService())
            {
                RepositoryPath = @"C:\FakeRepo"
            };

            await mainVm.MergeIntoCurrentBranchCommand.ExecuteAsync(new GitBranch { Name = "feature/x" });

            // The preview must be populated and awaiting confirmation - the merge itself
            // shouldn't have run yet.
            Assert.True(mainVm.IsMergePreviewDialogOpen);
            Assert.Single(mainVm.MergePreviewCommits);
            Assert.Single(mainVm.MergePreviewFiles);
            Assert.Null(fakeGit.LastMergedRef);

            await mainVm.ConfirmMergeCommand.ExecuteAsync(null);

            Assert.False(mainVm.IsMergePreviewDialogOpen);
            Assert.Equal("feature/x", fakeGit.LastMergedRef);
        }

        [Fact]
        public async Task MainViewModel_ConfirmMerge_OnConflict_OpensConflictDialogInsteadOfPlainError()
        {
            var fakeGit = new FakeGitService
            {
                NextMergeSucceeds = false,
                ConflictedFiles =
                {
                    new MergeConflictFile { Path = "a.cs", ConflictType = MergeConflictType.BothModified },
                    new MergeConflictFile { Path = "b.cs", ConflictType = MergeConflictType.BothModified }
                }
            };
            var mainVm = new MainViewModel(fakeGit, new RecentRepositoriesService(), CreateIsolatedThemeService())
            {
                RepositoryPath = @"C:\FakeRepo"
            };

            await mainVm.MergeIntoCurrentBranchCommand.ExecuteAsync(new GitBranch { Name = "feature/x" });
            await mainVm.ConfirmMergeCommand.ExecuteAsync(null);

            Assert.True(mainVm.IsMergeConflictDialogOpen);
            Assert.Equal(2, mainVm.MergeConflictFiles.Count);
            Assert.False(mainVm.CanContinueMerge);
        }

        [Fact]
        public async Task MainViewModel_OpenConflictFileInEditor_LaunchesConfiguredMergeToolNotShellOpen()
        {
            // Regression: this used to Process.Start(UseShellExecute:true) the bare conflicted
            // file, which either hit Explorer's "Open with..." picker (no file association) or
            // launched a tool with just one file, showing an empty setup dialog instead of a
            // real base/ours/theirs merge session. Must go through `git mergetool` instead, so
            // whatever the user has configured as merge.tool gets the correct file wiring.
            var fakeGit = new FakeGitService
            {
                NextMergeSucceeds = false,
                NextMergeToolSucceeds = true,
                ConflictedFiles = { new MergeConflictFile { Path = "a.cs", ConflictType = MergeConflictType.BothModified } }
            };
            var mainVm = new MainViewModel(fakeGit, new RecentRepositoriesService(), CreateIsolatedThemeService())
            {
                RepositoryPath = @"C:\FakeRepo"
            };

            await mainVm.MergeIntoCurrentBranchCommand.ExecuteAsync(new GitBranch { Name = "feature/x" });
            await mainVm.ConfirmMergeCommand.ExecuteAsync(null);

            await mainVm.OpenConflictFileInEditorCommand.ExecuteAsync(mainVm.MergeConflictFiles[0]);

            Assert.Equal("a.cs", fakeGit.LastMergeToolFilePath);
        }

        [Fact]
        public async Task MainViewModel_OpenConflictFileInEditor_NotConfigured_ConfirmYes_ConfiguresKDiff3AndRetries()
        {
            var fakeGit = new FakeGitService
            {
                NextMergeSucceeds = false,
                NextMergeToolSucceeds = false,
                NextMergeToolOutput = "This message is displayed because 'merge.tool' is not configured.",
                ConflictedFiles = { new MergeConflictFile { Path = "a.cs", ConflictType = MergeConflictType.BothModified } }
            };
            var mainVm = new MainViewModel(fakeGit, new RecentRepositoriesService(), CreateIsolatedThemeService())
            {
                RepositoryPath = @"C:\FakeRepo",
                ConfirmConfigureKDiff3Action = _ => true
            };

            await mainVm.MergeIntoCurrentBranchCommand.ExecuteAsync(new GitBranch { Name = "feature/x" });
            await mainVm.ConfirmMergeCommand.ExecuteAsync(null);

            await mainVm.OpenConflictFileInEditorCommand.ExecuteAsync(mainVm.MergeConflictFiles[0]);

            Assert.True(fakeGit.ConfigureKDiff3Called);
            // The fake simulates configuring having fixed it - the command should have retried
            // and this time reached the merge tool successfully.
            Assert.Equal("a.cs", fakeGit.LastMergeToolFilePath);
        }

        [Fact]
        public async Task MainViewModel_OpenConflictFileInEditor_NotConfigured_NewGitWording_ConfiguresKDiff3AndRetries()
        {
            // git 2.44+'s C builtin mergetool prints a different message than the old
            // Perl/shell script did - just a usage synopsis ending in this line, with no
            // "'merge.tool' is not configured" text anywhere. Real output seen from git
            // 2.51 when nothing is configured.
            var fakeGit = new FakeGitService
            {
                NextMergeSucceeds = false,
                NextMergeToolSucceeds = false,
                NextMergeToolOutput = "usage: git mergetool [--tool=tool] [--tool-help]\n\nMake sure a merge tool is configured (git config merge.tool).",
                ConflictedFiles = { new MergeConflictFile { Path = "a.cs", ConflictType = MergeConflictType.BothModified } }
            };
            var mainVm = new MainViewModel(fakeGit, new RecentRepositoriesService(), CreateIsolatedThemeService())
            {
                RepositoryPath = @"C:\FakeRepo",
                ConfirmConfigureKDiff3Action = _ => true
            };

            await mainVm.MergeIntoCurrentBranchCommand.ExecuteAsync(new GitBranch { Name = "feature/x" });
            await mainVm.ConfirmMergeCommand.ExecuteAsync(null);

            await mainVm.OpenConflictFileInEditorCommand.ExecuteAsync(mainVm.MergeConflictFiles[0]);

            Assert.True(fakeGit.ConfigureKDiff3Called);
            Assert.Equal("a.cs", fakeGit.LastMergeToolFilePath);
        }

        [Fact]
        public async Task MainViewModel_OpenConflictFileInEditor_NotConfigured_ConfirmNo_DoesNotConfigure()
        {
            var fakeGit = new FakeGitService
            {
                NextMergeSucceeds = false,
                NextMergeToolSucceeds = false,
                NextMergeToolOutput = "This message is displayed because 'merge.tool' is not configured.",
                ConflictedFiles = { new MergeConflictFile { Path = "a.cs", ConflictType = MergeConflictType.BothModified } }
            };
            var mainVm = new MainViewModel(fakeGit, new RecentRepositoriesService(), CreateIsolatedThemeService())
            {
                RepositoryPath = @"C:\FakeRepo",
                ConfirmConfigureKDiff3Action = _ => false,
                // Declining the offer falls through to the generic "could not launch" report -
                // stub it out so this doesn't pop a real MessageBox during the test run.
                ReportMergeToolLaunchFailureAction = _ => { }
            };

            await mainVm.MergeIntoCurrentBranchCommand.ExecuteAsync(new GitBranch { Name = "feature/x" });
            await mainVm.ConfirmMergeCommand.ExecuteAsync(null);

            await mainVm.OpenConflictFileInEditorCommand.ExecuteAsync(mainVm.MergeConflictFiles[0]);

            Assert.False(fakeGit.ConfigureKDiff3Called);
        }

        [Fact]
        public async Task MainViewModel_OpenConflictFileInEditor_OtherFailure_DoesNotOfferToConfigureKDiff3()
        {
            var fakeGit = new FakeGitService
            {
                NextMergeSucceeds = false,
                NextMergeToolSucceeds = false,
                NextMergeToolOutput = "fatal: git-mergetool--lib not found",
                ConflictedFiles = { new MergeConflictFile { Path = "a.cs", ConflictType = MergeConflictType.BothModified } }
            };
            var mainVm = new MainViewModel(fakeGit, new RecentRepositoriesService(), CreateIsolatedThemeService())
            {
                RepositoryPath = @"C:\FakeRepo",
                // Would confirm if asked - proves the gate is the specific "not configured"
                // message, not just any failure.
                ConfirmConfigureKDiff3Action = _ => true,
                // Falls straight through to the generic failure report - stub it out so this
                // doesn't pop a real MessageBox during the test run.
                ReportMergeToolLaunchFailureAction = _ => { }
            };

            await mainVm.MergeIntoCurrentBranchCommand.ExecuteAsync(new GitBranch { Name = "feature/x" });
            await mainVm.ConfirmMergeCommand.ExecuteAsync(null);

            await mainVm.OpenConflictFileInEditorCommand.ExecuteAsync(mainVm.MergeConflictFiles[0]);

            Assert.False(fakeGit.ConfigureKDiff3Called);
        }

        [Fact]
        public async Task MainViewModel_CloseMergeConflictDialog_AlwaysClosesRegardlessOfGitState()
        {
            var fakeGit = new FakeGitService
            {
                NextMergeSucceeds = false,
                ConflictedFiles = { new MergeConflictFile { Path = "a.cs", ConflictType = MergeConflictType.BothModified } }
            };
            var mainVm = new MainViewModel(fakeGit, new RecentRepositoriesService(), CreateIsolatedThemeService())
            {
                RepositoryPath = @"C:\FakeRepo"
            };

            await mainVm.MergeIntoCurrentBranchCommand.ExecuteAsync(new GitBranch { Name = "feature/x" });
            await mainVm.ConfirmMergeCommand.ExecuteAsync(null);
            Assert.True(mainVm.IsMergeConflictDialogOpen);

            // Simulates the merge having been resolved/aborted outside Girt - Abort/Continue
            // would now fail against a merge that no longer exists, but Close must still work.
            mainVm.CloseMergeConflictDialogCommand.Execute(null);

            Assert.False(mainVm.IsMergeConflictDialogOpen);
        }

        [Fact]
        public async Task MainViewModel_MarkConflictFileResolved_EnablesContinueOnlyOnceAllResolved()
        {
            var fakeGit = new FakeGitService
            {
                NextMergeSucceeds = false,
                ConflictedFiles =
                {
                    new MergeConflictFile { Path = "a.cs", ConflictType = MergeConflictType.BothModified },
                    new MergeConflictFile { Path = "b.cs", ConflictType = MergeConflictType.BothModified }
                }
            };
            var mainVm = new MainViewModel(fakeGit, new RecentRepositoriesService(), CreateIsolatedThemeService())
            {
                RepositoryPath = @"C:\FakeRepo"
            };

            await mainVm.MergeIntoCurrentBranchCommand.ExecuteAsync(new GitBranch { Name = "feature/x" });
            await mainVm.ConfirmMergeCommand.ExecuteAsync(null);

            var first = mainVm.MergeConflictFiles[0];
            var second = mainVm.MergeConflictFiles[1];

            await mainVm.MarkConflictFileResolvedCommand.ExecuteAsync(first);
            Assert.True(first.IsResolved);
            Assert.False(mainVm.CanContinueMerge);

            await mainVm.MarkConflictFileResolvedCommand.ExecuteAsync(second);
            Assert.True(second.IsResolved);
            Assert.True(mainVm.CanContinueMerge);

            await mainVm.ContinueMergeCommand.ExecuteAsync(null);
            Assert.False(mainVm.IsMergeConflictDialogOpen);
            Assert.True(fakeGit.MergeContinued);
        }

        [Fact]
        public async Task CommitDetailViewModel_MergeCommit_DiffsAgainstFirstParent()
        {
            // Regression: `git show <merge-commit> -- path` returns nothing for a merge
            // commit (2+ parents) - git can't pick a side without -m/-c - so the changed-files
            // list populated fine (numstat/combined-diff summaries still work) while every
            // per-file diff silently came back empty. See GitCliService.GetRawFileDiffAsync.
            var fakeGit = new FakeGitService
            {
                RawDiff = "diff --git a/b.txt b/b.txt\nindex 1234567..89abcdef 100644\n--- a/b.txt\n+++ b/b.txt\n@@ -0,0 +1 @@\n+b"
            };
            var vm = new CommitDetailViewModel(fakeGit, () => @"C:\FakeRepo");

            var mergeCommit = new GitCommit
            {
                Hash = "merge123",
                ParentHashes = new List<string> { "parentA", "parentB" }
            };
            fakeGit.DiffFiles.Add(new GitFileDiff { Path = "b.txt" });

            await vm.SetCommitAsync(mergeCommit);
            // SelectedFile's setter fires LoadFileDiffAsync fire-and-forget (see
            // OnSelectedFileChanged) - its DiffParser.ParseUnifiedDiff call runs on a real
            // thread-pool hop (Task.Run), so it isn't guaranteed done yet at this point.
            await Task.Delay(50);

            Assert.Equal("parentA", fakeGit.LastDiffAgainstRef);
            Assert.NotEmpty(vm.DiffLines);

            // A normal, single-parent commit shouldn't pass a parent ref at all - `git show`
            // already handles that case correctly on its own.
            fakeGit.DiffFiles.Clear();
            fakeGit.DiffFiles.Add(new GitFileDiff { Path = "a.txt" });
            var normalCommit = new GitCommit { Hash = "normal123", ParentHashes = new List<string> { "parentA" } };
            await vm.SetCommitAsync(normalCommit);
            await Task.Delay(50);

            Assert.Null(fakeGit.LastDiffAgainstRef);
        }

        [Fact]
        public async Task MainViewModel_AbortMerge_CallsGitAbortAndClosesConflictDialog()
        {
            var fakeGit = new FakeGitService
            {
                NextMergeSucceeds = false,
                ConflictedFiles = { new MergeConflictFile { Path = "a.cs", ConflictType = MergeConflictType.BothModified } }
            };
            var mainVm = new MainViewModel(fakeGit, new RecentRepositoriesService(), CreateIsolatedThemeService())
            {
                RepositoryPath = @"C:\FakeRepo"
            };

            await mainVm.MergeIntoCurrentBranchCommand.ExecuteAsync(new GitBranch { Name = "feature/x" });
            await mainVm.ConfirmMergeCommand.ExecuteAsync(null);
            Assert.True(mainVm.IsMergeConflictDialogOpen);

            // AbortMergeAsync shows a Yes/No MessageBox first in the real app - it's tested
            // indirectly here by calling the git-service abort path directly, since the
            // MessageBox itself can't be driven from a unit test.
            var (success, _) = await fakeGit.AbortMergeAsync(mainVm.RepositoryPath);
            Assert.True(success);
            Assert.True(fakeGit.MergeAborted);
        }
    }
}
