using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Girt.Models;
using Girt.Services;
using Xunit;

namespace Girt.Tests
{
    public class GitCliServiceIntegrationTests : IDisposable
    {
        private readonly string _testRepoPath;
        private readonly GitCliService _gitService;

        public GitCliServiceIntegrationTests()
        {
            _testRepoPath = Path.Combine(Path.GetTempPath(), "GirtTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testRepoPath);
            _gitService = new GitCliService();

            RunGit("init -b main");
            RunGit("config user.name \"Girt Tester\"");
            RunGit("config user.email \"test@girt.dev\"");
        }

        private void RunGit(string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = _testRepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            proc.WaitForExit();
        }

        [Fact]
        public async Task GitCliService_CanRetrieveBranchesCommitsAndDiff()
        {
            // 1. Create Initial Commit
            var file1 = Path.Combine(_testRepoPath, "hello.txt");
            File.WriteAllText(file1, "Hello Girt\nLine 2\n");
            RunGit("add hello.txt");
            RunGit("commit -m \"Initial commit\"");

            // 2. Create Feature Branch
            var (createSuccess, _) = await _gitService.CreateBranchAsync(_testRepoPath, "feature/awesome");
            Assert.True(createSuccess);

            // 3. Modify File in Feature Branch
            File.WriteAllText(file1, "Hello Girt\nLine 2 Modified\nLine 3 New\n");
            RunGit("add hello.txt");
            RunGit("commit -m \"Feature commit\"");

            // 4. Test GetBranchesAsync
            var branches = await _gitService.GetBranchesAsync(_testRepoPath);
            Assert.Contains(branches, b => b.Name == "feature/awesome");
            Assert.Contains(branches, b => b.Name == "main");

            // 5. Test GetCommitsAsync
            var commits = await _gitService.GetCommitsAsync(_testRepoPath);
            Assert.Equal(2, commits.Count);
            Assert.Equal("Feature commit", commits[0].Subject);
            Assert.Equal("Initial commit", commits[1].Subject);

            // 6. Test GetCommitDiffAsync
            var diffs = await _gitService.GetCommitDiffAsync(_testRepoPath, commits[0].Hash);
            Assert.Single(diffs);
            Assert.Equal("hello.txt", diffs[0].Path);

            // 7. Test Raw File Diff
            var rawDiff = await _gitService.GetRawFileDiffAsync(_testRepoPath, commits[0].Hash, "hello.txt");
            Assert.Contains("+Line 2 Modified", rawDiff);

            // 8. Test Status & Working Tree Changes
            var file2 = Path.Combine(_testRepoPath, "uncommitted.txt");
            File.WriteAllText(file2, "uncommitted file content");
            
            var workingChanges = await _gitService.GetWorkingTreeChangesAsync(_testRepoPath);
            Assert.Contains(workingChanges.UnstagedFiles, f => f.Path == "uncommitted.txt");

            // 9. Test Staging & Commit via GitCliService
            var (stageSuccess, _) = await _gitService.StageFileAsync(_testRepoPath, "uncommitted.txt");
            Assert.True(stageSuccess);

            var changesAfterStage = await _gitService.GetWorkingTreeChangesAsync(_testRepoPath);
            Assert.Contains(changesAfterStage.StagedFiles, f => f.Path == "uncommitted.txt");

            var (commitSuccess, _) = await _gitService.CommitAsync(_testRepoPath, "Add uncommitted file");
            Assert.True(commitSuccess);

            // 10. Test Soft Reset HEAD
            var (resetSuccess, _) = await _gitService.ResetHeadAsync(_testRepoPath, "HEAD~1", GitResetMode.Soft);
            Assert.True(resetSuccess);

            var commitsAfterReset = await _gitService.GetCommitsAsync(_testRepoPath);
            Assert.Equal(2, commitsAfterReset.Count);

            // 11. Test AddToGitIgnoreAsync
            var file3 = Path.Combine(_testRepoPath, "secret.key");
            File.WriteAllText(file3, "super-secret");
            var (ignoreSuccess, _) = await _gitService.AddToGitIgnoreAsync(_testRepoPath, "secret.key");
            Assert.True(ignoreSuccess);

            var gitIgnoreFile = Path.Combine(_testRepoPath, ".gitignore");
            Assert.True(File.Exists(gitIgnoreFile));
            var gitIgnoreContent = File.ReadAllText(gitIgnoreFile);
            Assert.Contains("secret.key", gitIgnoreContent);
        }

        [Fact]
        public async Task GetRepoStatusAsync_ReportsAheadAndBehindCounts()
        {
            File.WriteAllText(Path.Combine(_testRepoPath, "a.txt"), "1");
            RunGit("add a.txt");
            RunGit("commit -m \"initial\"");

            RunGit("checkout -b feature");
            RunGit("branch --set-upstream-to=main feature");

            // Advance feature by 2 commits (ahead of main).
            File.WriteAllText(Path.Combine(_testRepoPath, "b.txt"), "1");
            RunGit("add b.txt");
            RunGit("commit -m \"feature 1\"");
            File.WriteAllText(Path.Combine(_testRepoPath, "c.txt"), "1");
            RunGit("add c.txt");
            RunGit("commit -m \"feature 2\"");

            // Advance main by 1 commit (feature falls behind it too), then switch back.
            RunGit("checkout main");
            File.WriteAllText(Path.Combine(_testRepoPath, "d.txt"), "1");
            RunGit("add d.txt");
            RunGit("commit -m \"main advance\"");
            RunGit("checkout feature");

            // Exercises the combined "rev-list --left-right --count" parsing that replaced two
            // separate git process spawns for ahead/behind.
            var status = await _gitService.GetRepoStatusAsync(_testRepoPath);

            Assert.True(status.HasUpstream);
            Assert.Equal("main", status.UpstreamBranch);
            Assert.Equal(2, status.AheadCount);
            Assert.Equal(1, status.BehindCount);
        }

        [Fact]
        public async Task AddToGitIgnoreAsync_FolderTarget_IgnoresExactFolderPassedIn()
        {
            // GitIgnoreTarget.Folder now takes the exact folder the caller already picked
            // (see GitWorkingFile.AncestorFolders) rather than guessing a file's immediate
            // parent - callers resolve that themselves before calling in.
            Directory.CreateDirectory(Path.Combine(_testRepoPath, "logs"));
            File.WriteAllText(Path.Combine(_testRepoPath, "logs", "debug.log"), "log contents");
            RunGit("add logs/debug.log");
            RunGit("commit -m \"add log file\"");

            var (success, _) = await _gitService.AddToGitIgnoreAsync(_testRepoPath, "logs", GitIgnoreTarget.Folder);
            Assert.True(success);

            var gitIgnoreContent = File.ReadAllText(Path.Combine(_testRepoPath, ".gitignore"));
            Assert.Contains("logs/", gitIgnoreContent);
            Assert.DoesNotContain("logs/debug.log", gitIgnoreContent);

            // Already-tracked files under the folder should be untracked (git rm -r --cached),
            // which shows up as a staged deletion until the caller commits it.
            var changes = await _gitService.GetWorkingTreeChangesAsync(_testRepoPath);
            Assert.Contains(changes.StagedFiles, f => f.Path == "logs/debug.log" && f.Status == FileStatusType.Deleted);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_testRepoPath))
                {
                    foreach (var file in Directory.GetFiles(_testRepoPath, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    Directory.Delete(_testRepoPath, true);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}
