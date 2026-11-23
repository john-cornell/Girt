using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Girt.Models;
using Girt.Services;
using Girt.ViewModels;
using Xunit;

namespace Girt.Tests
{
    /// <summary>Real git, real repos: a bare "origin", a clone with develop + a feature branch,
    /// and a second clone standing in for a teammate pushing to develop.</summary>
    public class UpdateFromBranchIntegrationTests : IDisposable
    {
        private readonly string _root;
        private readonly string _origin;
        private readonly string _repo;
        private readonly string _teammate;
        private readonly GitCliService _git = new();

        public UpdateFromBranchIntegrationTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "GirtUpdate_" + Guid.NewGuid().ToString("N"));
            _origin = Path.Combine(_root, "origin.git");
            _repo = Path.Combine(_root, "repo");
            _teammate = Path.Combine(_root, "teammate");
            Directory.CreateDirectory(_root);

            Git(_root, "init --bare -b develop origin.git");
            Git(_root, "clone origin.git repo");
            Configure(_repo);
            File.WriteAllText(Path.Combine(_repo, "shared.txt"), "line 1\nline 2\nline 3\n");
            Git(_repo, "add .");
            Git(_repo, "commit -m initial");
            Git(_repo, "push -u origin develop");
            Git(_repo, "checkout -b feature/x");

            Git(_root, "clone origin.git teammate");
            Configure(_teammate);
        }

        private static void Configure(string repo)
        {
            Git(repo, "config user.name \"Girt Tester\"");
            Git(repo, "config user.email \"test@girt.dev\"");
            Git(repo, "config core.autocrlf false");
        }

        private static string Git(string dir, string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return output.Trim();
        }

        private void TeammatePushesToDevelop(string file, string content)
        {
            Git(_teammate, "pull");
            File.WriteAllText(Path.Combine(_teammate, file), content);
            Git(_teammate, "add .");
            Git(_teammate, $"commit -m \"teammate {file}\"");
            Git(_teammate, "push");
        }

        private MainViewModel CreateViewModel() => new(_git, new RecentRepositoriesService(),
            new ThemeService(Path.Combine(_root, "settings.json")))
        {
            RepositoryPath = _repo,
            CurrentBranch = "feature/x",
            ReportErrorAction = (message, _) => throw new Xunit.Sdk.XunitException("Unexpected error dialog: " + message),
            ConfirmAbortConflictAction = (_, _) => true
        };

        [Fact]
        public async Task FastForward_UpdatesLocalBranchWithoutCheckingItOut()
        {
            TeammatePushesToDevelop("teammate.txt", "hi\n");

            var (result, _) = await _git.FastForwardBranchFromUpstreamAsync(_repo, "develop");

            Assert.Equal(FastForwardResult.Updated, result);
            Assert.Equal(Git(_repo, "rev-parse origin/develop"), Git(_repo, "rev-parse develop"));
            Assert.Equal(Git(_teammate, "rev-parse HEAD"), Git(_repo, "rev-parse develop"));
            Assert.Equal("feature/x", Git(_repo, "rev-parse --abbrev-ref HEAD"));
        }

        [Fact]
        public async Task FastForward_LocalCommitsOnBranch_ReportsDivergedAndLeavesItAlone()
        {
            TeammatePushesToDevelop("teammate.txt", "hi\n");
            Git(_repo, "checkout develop");
            File.WriteAllText(Path.Combine(_repo, "local.txt"), "mine\n");
            Git(_repo, "add .");
            Git(_repo, "commit -m local");
            var localDevelop = Git(_repo, "rev-parse develop");
            Git(_repo, "checkout feature/x");

            var (result, _) = await _git.FastForwardBranchFromUpstreamAsync(_repo, "develop");

            Assert.Equal(FastForwardResult.Diverged, result);
            Assert.Equal(localDevelop, Git(_repo, "rev-parse develop"));
        }

        [Fact]
        public async Task FastForward_BranchWithoutUpstream_ReportsNoUpstream()
        {
            Git(_repo, "branch local-only");

            var (result, _) = await _git.FastForwardBranchFromUpstreamAsync(_repo, "local-only");

            Assert.Equal(FastForwardResult.NoUpstream, result);
        }

        [Fact]
        public async Task StashPushAll_ThenPopByHash_RestoresStagedUnstagedAndUntracked()
        {
            File.WriteAllText(Path.Combine(_repo, "shared.txt"), "line 1 staged\nline 2\nline 3\n");
            Git(_repo, "add shared.txt");
            File.WriteAllText(Path.Combine(_repo, "new.txt"), "untracked\n");

            var (ok, hash, _) = await _git.StashPushAllAsync(_repo, MainViewModel.AutoStashMessagePrefix + "develop");
            Assert.True(ok);
            Assert.NotNull(hash);
            Assert.Equal("", Git(_repo, "status --porcelain"));

            // Someone else's stash on top must not get popped instead
            File.WriteAllText(Path.Combine(_repo, "other.txt"), "other\n");
            Git(_repo, "stash push -u -m other");

            var (popped, _) = await _git.StashPopByHashAsync(_repo, hash!);

            Assert.True(popped);
            var status = Git(_repo, "status --porcelain");
            Assert.Contains("M  shared.txt", status);
            Assert.Contains("?? new.txt", status);
            Assert.DoesNotContain("other.txt", status);
            var stashes = await _git.GetStashesAsync(_repo);
            Assert.Single(stashes);
            Assert.Contains("other", stashes[0].Message);
        }

        [Fact]
        public async Task StashPushAll_NothingToSave_ReturnsNoHash()
        {
            var (ok, hash, _) = await _git.StashPushAllAsync(_repo, "nothing");

            Assert.True(ok);
            Assert.Null(hash);
        }

        [Fact]
        public async Task MergeLatest_DirtyFeatureBranch_EndsMergedWithChangesBackAndNoStash()
        {
            TeammatePushesToDevelop("teammate.txt", "hi\n");
            File.WriteAllText(Path.Combine(_repo, "wip.txt"), "work in progress\n");
            File.WriteAllText(Path.Combine(_repo, "shared.txt"), "line 1\nline 2\nline 3\nline 4 wip\n");
            var vm = CreateViewModel();

            await vm.MergeLatestIntoCurrentAsync("develop");

            Assert.Equal("feature/x", Git(_repo, "rev-parse --abbrev-ref HEAD"));
            Assert.Equal(Git(_repo, "rev-parse origin/develop"), Git(_repo, "rev-parse develop"));
            Assert.Contains("feature/x", Git(_repo, "branch --contains develop --list feature/x"));
            Assert.True(File.Exists(Path.Combine(_repo, "teammate.txt")));
            Assert.Equal("work in progress\n", File.ReadAllText(Path.Combine(_repo, "wip.txt")));
            Assert.Contains("line 4 wip", File.ReadAllText(Path.Combine(_repo, "shared.txt")));
            Assert.Equal("", Git(_repo, "stash list"));
            Assert.False(vm.IsMergeConflictDialogOpen);
        }

        [Fact]
        public async Task MergeLatest_Conflict_HoldsStashUntilMergeFinished()
        {
            TeammatePushesToDevelop("shared.txt", "line 1 theirs\nline 2\nline 3\n");
            File.WriteAllText(Path.Combine(_repo, "shared.txt"), "line 1 mine\nline 2\nline 3\n");
            Git(_repo, "commit -am mine");
            File.WriteAllText(Path.Combine(_repo, "wip.txt"), "work in progress\n");
            var vm = CreateViewModel();

            await vm.MergeLatestIntoCurrentAsync("develop");

            Assert.True(vm.IsMergeConflictDialogOpen);
            Assert.Equal("feature/x", vm.ConflictMineLabel);
            Assert.Equal("develop", vm.ConflictTheirsLabel);
            Assert.Single(vm.MergeConflictFiles);
            Assert.False(File.Exists(Path.Combine(_repo, "wip.txt")));
            Assert.NotEqual("", Git(_repo, "stash list"));

            File.WriteAllText(Path.Combine(_repo, "shared.txt"), "line 1 both\nline 2\nline 3\n");
            await vm.MarkConflictFileResolvedAsync(vm.MergeConflictFiles[0]);
            await vm.ContinueMergeAsync();

            Assert.False(vm.IsMergeConflictDialogOpen);
            Assert.Equal("work in progress\n", File.ReadAllText(Path.Combine(_repo, "wip.txt")));
            Assert.Equal("", Git(_repo, "stash list"));
            Assert.Equal(2, Git(_repo, "show -s --format=%P HEAD").Split(' ').Length);
        }

        [Fact]
        public async Task MergeLatest_ConflictAborted_RestoresBranchAndStash()
        {
            TeammatePushesToDevelop("shared.txt", "line 1 theirs\nline 2\nline 3\n");
            File.WriteAllText(Path.Combine(_repo, "shared.txt"), "line 1 mine\nline 2\nline 3\n");
            Git(_repo, "commit -am mine");
            var before = Git(_repo, "rev-parse HEAD");
            File.WriteAllText(Path.Combine(_repo, "wip.txt"), "work in progress\n");
            var vm = CreateViewModel();

            await vm.MergeLatestIntoCurrentAsync("develop");
            await vm.AbortMergeAsync();

            Assert.False(vm.IsMergeConflictDialogOpen);
            Assert.Equal(before, Git(_repo, "rev-parse HEAD"));
            Assert.Equal("work in progress\n", File.ReadAllText(Path.Combine(_repo, "wip.txt")));
            Assert.Equal("", Git(_repo, "stash list"));
        }

        [Fact]
        public async Task MergeLatest_StashPopConflicts_OpensEditorAndFinishDropsStashUnstaged()
        {
            TeammatePushesToDevelop("shared.txt", "line 1 theirs\nline 2\nline 3\n");
            // Uncommitted edit to the same line develop changed - merge is clean (tree was
            // stashed), but putting the stash back conflicts.
            File.WriteAllText(Path.Combine(_repo, "shared.txt"), "line 1 wip\nline 2\nline 3\n");
            var vm = CreateViewModel();

            await vm.MergeLatestIntoCurrentAsync("develop");

            Assert.True(vm.IsMergeConflictDialogOpen);
            Assert.Equal("your stashed changes", vm.ConflictTheirsLabel);
            Assert.NotEqual("", Git(_repo, "stash list"));

            File.WriteAllText(Path.Combine(_repo, "shared.txt"), "line 1 wip and theirs\nline 2\nline 3\n");
            await vm.MarkConflictFileResolvedAsync(vm.MergeConflictFiles[0]);
            await vm.ContinueMergeAsync();

            Assert.False(vm.IsMergeConflictDialogOpen);
            Assert.Equal("", Git(_repo, "stash list"));
            Assert.Equal("M shared.txt", Git(_repo, "status --porcelain"));
        }

        [Fact]
        public async Task GetUntrackedOrigBackups_OnlyUntrackedOrigNextToConflictedFiles()
        {
            File.WriteAllText(Path.Combine(_repo, "tracked.txt.orig"), "real file\n");
            Git(_repo, "add tracked.txt.orig");
            Git(_repo, "commit -m tracked-orig");
            Directory.CreateDirectory(Path.Combine(_repo, "src"));
            File.WriteAllText(Path.Combine(_repo, "src", "a.cs.orig"), "backup\n");
            File.WriteAllText(Path.Combine(_repo, "unrelated.txt.orig"), "backup\n");

            var backups = await _git.GetUntrackedOrigBackupsAsync(_repo, new[] { "src/a.cs", "tracked.txt", "missing.txt" });

            Assert.Equal(new[] { "src/a.cs.orig" }, backups);
        }

        public void Dispose()
        {
            try
            {
                foreach (var file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }
}
