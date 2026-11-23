using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Girt.Models;
using Girt.Services;
using Girt.ViewModels;
using Xunit;

namespace Girt.Tests
{
    public class ConflictDocumentTests
    {
        private static readonly string Start = new('<', ConflictDocument.MarkerSize);
        private static readonly string Base = new('|', ConflictDocument.MarkerSize);
        private static readonly string Sep = new('=', ConflictDocument.MarkerSize);
        private static readonly string End = new('>', ConflictDocument.MarkerSize);

        private static string Conflict(string mine, string @base, string theirs, string eol = "\n") =>
            $"{Start} mine{eol}{mine}{Base} base{eol}{@base}{Sep}{eol}{theirs}{End} theirs{eol}";

        private static ConflictDocument Parse(string text) => ConflictDocument.Parse(Encoding.UTF8.GetBytes(text));
        private static string Compose(ConflictDocument doc) => Encoding.UTF8.GetString(doc.Compose());

        [Fact]
        public void Parse_SplitsCommonAndConflictBlocks()
        {
            var doc = Parse("a\n" + Conflict("mine 1\nmine 2\n", "base\n", "theirs\n") + "z\n");

            Assert.Equal(3, doc.Blocks.Count);
            Assert.Equal("a", doc.Blocks[0].ResultText);
            var conflict = doc.Blocks[1];
            Assert.True(conflict.IsConflict);
            Assert.Equal("mine 1\nmine 2", conflict.MineText);
            Assert.Equal("base", conflict.BaseText);
            Assert.Equal("theirs", conflict.TheirsText);
            Assert.Equal(1, doc.UnresolvedCount);
        }

        [Fact]
        public void Compose_WithUnresolvedConflict_Throws()
        {
            var doc = Parse(Conflict("m\n", "b\n", "t\n"));
            Assert.Throws<InvalidOperationException>(() => doc.Compose());
        }

        [Theory]
        [InlineData("mine", "a\nm\nz\n")]
        [InlineData("theirs", "a\nt\nz\n")]
        [InlineData("mine+theirs", "a\nm\nt\nz\n")]
        [InlineData("theirs+mine", "a\nt\nm\nz\n")]
        public void Compose_EachPick_GivesExpectedFile(string pick, string expected)
        {
            var doc = Parse("a\n" + Conflict("m\n", "b\n", "t\n") + "z\n");
            var conflict = doc.Blocks[1];
            switch (pick)
            {
                case "mine": conflict.UseMine(); break;
                case "theirs": conflict.UseTheirs(); break;
                case "mine+theirs": conflict.UseMineThenTheirs(); break;
                default: conflict.UseTheirsThenMine(); break;
            }

            Assert.Equal(expected, Compose(doc));
        }

        [Fact]
        public void Compose_SideWithNoLines_ContributesNothing()
        {
            var doc = Parse("a\n" + Conflict("", "b\n", "t\n") + "z\n");
            var conflict = doc.Blocks[1];
            Assert.True(conflict.MineIsEmpty);

            conflict.UseMine();
            Assert.Equal("a\nz\n", Compose(doc));

            conflict.UseMineThenTheirs();
            Assert.Equal("a\nt\nz\n", Compose(doc));
        }

        [Fact]
        public void Compose_KeepsCrlfAndBom()
        {
            var text = "a\r\n" + Conflict("m\r\n", "b\r\n", "t\r\n", "\r\n") + "z\r\n";
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(text)).ToArray();
            var doc = ConflictDocument.Parse(bytes);
            doc.Blocks[1].UseTheirs();

            var composed = doc.Compose();

            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, composed.Take(3));
            Assert.Equal("a\r\nt\r\nz\r\n", Encoding.UTF8.GetString(composed.Skip(3).ToArray()));
        }

        [Fact]
        public void TypedResult_IsNormalisedToFileLineEndings_AndResolvesTheConflict()
        {
            var doc = Parse("a\n" + Conflict("m\n", "b\n", "t\n") + "z\n");
            var conflict = doc.Blocks[1];

            // WPF's TextBox types \r\n
            conflict.ResultText = "typed 1\r\ntyped 2";

            Assert.True(conflict.IsResolved);
            Assert.Equal("a\ntyped 1\ntyped 2\nz\n", Compose(doc));
        }

        [Fact]
        public void TypedEmptyResult_RemovesTheConflictLines()
        {
            var doc = Parse("a\n" + Conflict("m\n", "b\n", "t\n") + "z\n");
            doc.Blocks[1].UseMine();
            doc.Blocks[1].ResultText = "";

            Assert.Equal("a\nz\n", Compose(doc));
        }

        [Fact]
        public void ResetChoice_MakesItUnresolvedAgain()
        {
            var doc = Parse(Conflict("m\n", "b\n", "t\n"));
            doc.Blocks[0].UseMine();
            doc.Blocks[0].ResetChoice();

            Assert.False(doc.Blocks[0].IsResolved);
            Assert.Equal(1, doc.UnresolvedCount);
        }

        [Fact]
        public void ShortMarkerLookalikesInTheFile_AreNotTreatedAsConflicts()
        {
            var doc = Parse("Heading\n=======\n<<<<<<< not a conflict\n");

            Assert.DoesNotContain(doc.Blocks, b => b.IsConflict);
            Assert.Equal("Heading\n=======\n<<<<<<< not a conflict\n", Compose(doc));
            // Lines git merged on its own are the file's business
            Assert.Null(doc.FindLeftoverMarkerLine());
        }

        [Fact]
        public void FindLeftoverMarkerLine_FlagsMarkersTypedIntoAResolution()
        {
            var doc = Parse("a\n" + Conflict("m\n", "b\n", "t\n") + "z\n");
            doc.Blocks[1].ResultText = "<<<<<<< HEAD\nm";

            Assert.Equal(2, doc.FindLeftoverMarkerLine());
        }

        [Fact]
        public void NoTrailingNewline_IsKept()
        {
            var doc = Parse("a\n" + Conflict("m\n", "b\n", "t\n") + "z");
            doc.Blocks[1].UseMine();

            Assert.Equal("a\nm\nz", Compose(doc));
        }

        [Fact]
        public void NonUtf8Bytes_RoundTripUnchanged()
        {
            // 0xE9 is é in Latin-1 and invalid on its own in UTF-8
            var prefix = new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\n' };
            var bytes = prefix.Concat(Encoding.ASCII.GetBytes(Conflict("m\n", "b\n", "t\n"))).ToArray();
            var doc = ConflictDocument.Parse(bytes);
            doc.Blocks.Single(b => b.IsConflict).UseMine();

            Assert.Equal(prefix.Concat(Encoding.ASCII.GetBytes("m\n")), doc.Compose());
        }

        [Fact]
        public void BigCommonRuns_AreSplitSoTheListCanVirtualise()
        {
            var lines = string.Concat(Enumerable.Range(0, 450).Select(i => $"line {i}\n"));
            var doc = Parse(lines);

            Assert.Equal(3, doc.Blocks.Count);
            Assert.Equal(lines, Compose(doc));
        }
    }

    public class ConflictEditorViewModelTests
    {
        private static ThemeService IsolatedTheme() =>
            new(Path.Combine(Path.GetTempPath(), $"GirtTestSettings_{Guid.NewGuid():N}.json"));

        private static Task<(MainViewModel Vm, FakeGitService Git)> OpenMergeConflictAsync(params MergeConflictFile[] files) =>
            OpenMergeConflictAsync(_ => { }, files);

        private static async Task<(MainViewModel Vm, FakeGitService Git)> OpenMergeConflictAsync(Action<FakeGitService> setup, params MergeConflictFile[] files)
        {
            var fakeGit = new FakeGitService { NextMergeSucceeds = false, ConflictedFiles = files.ToList() };
            setup(fakeGit);
            var vm = new MainViewModel(fakeGit, new RecentRepositoriesService(), IsolatedTheme())
            {
                RepositoryPath = @"C:\FakeRepo",
                CurrentBranch = "feature/x",
                ReportErrorAction = (message, _) => throw new Xunit.Sdk.XunitException(message)
            };
            await vm.MergeIntoCurrentBranchCommand.ExecuteAsync(new GitBranch { Name = "develop" });
            await vm.ConfirmMergeCommand.ExecuteAsync(null);
            return (vm, fakeGit);
        }

        private static MergeConflictFile Modified(string path) => new() { Path = path, ConflictType = MergeConflictType.BothModified };

        [Fact]
        public async Task OpeningConflicts_LoadsFirstFileWithBranchNames()
        {
            var (vm, git) = await OpenMergeConflictAsync(Modified("a.cs"));

            Assert.Equal("Merging develop into feature/x", vm.MergeConflictDialogTitle);
            Assert.Equal("feature/x", vm.ConflictMineLabel);
            Assert.Equal("develop", vm.ConflictTheirsLabel);
            Assert.True(vm.ConflictEditor.IsTextMerge);
            Assert.Equal(1, vm.ConflictEditor.UnresolvedCount);
            Assert.False(git.LastMineIsTheirsStage);
            Assert.True(vm.ConflictEditor.CurrentBlock?.IsConflict);
        }

        [Fact]
        public async Task SavingFullyResolvedFile_WritesStagesAndMovesToNextFile()
        {
            var (vm, git) = await OpenMergeConflictAsync(Modified("a.cs"), Modified("b.cs"));

            vm.ConflictEditor.Blocks.Single(b => b.IsConflict).UseTheirs();
            await vm.SaveConflictFileCommand.ExecuteAsync(null);

            Assert.Equal("top\ntheirs line\nbottom\n", Encoding.UTF8.GetString(git.WrittenFiles["a.cs"]));
            Assert.Contains(git.Changes.StagedFiles, f => f.Path == "a.cs");
            Assert.True(vm.MergeConflictFiles[0].IsResolved);
            Assert.Equal("b.cs", vm.SelectedConflictFile?.Path);
            Assert.Equal("b.cs", vm.ConflictEditor.File?.Path);
            Assert.False(vm.CanContinueMerge);
            Assert.Equal("1 of 2 file(s) resolved", vm.ConflictProgressText);
        }

        [Fact]
        public async Task PartlyResolvedFile_KeepsItsPicksWhenSwitchingAway()
        {
            var (vm, git) = await OpenMergeConflictAsync(
                g => g.ConflictMergedText["a.cs"] = ConflictBlockText("m1", "t1") + "middle\n" + ConflictBlockText("m2", "t2"),
                Modified("a.cs"), Modified("b.cs"));

            vm.ConflictEditor.Blocks.First(b => b.IsConflict).UseMine();
            vm.SelectedConflictFile = vm.MergeConflictFiles[1];
            vm.SelectedConflictFile = vm.MergeConflictFiles[0];

            Assert.Equal(1, vm.ConflictEditor.UnresolvedCount);
            Assert.True(vm.ConflictEditor.Blocks.First(b => b.IsConflict).IsResolved);
            Assert.False(git.WrittenFiles.ContainsKey("a.cs"));
        }

        private static string ConflictBlockText(string mine, string theirs)
        {
            var m = ConflictDocument.MarkerSize;
            return $"{new string('<', m)} mine\n{mine}\n{new string('|', m)} base\nb\n{new string('=', m)}\n{theirs}\n{new string('>', m)} theirs\n";
        }

        [Fact]
        public async Task F8_WalksConflictsThenRollsIntoNextFile()
        {
            var (vm, _) = await OpenMergeConflictAsync(
                g => g.ConflictMergedText["a.cs"] = ConflictBlockText("m1", "t1") + "middle\n" + ConflictBlockText("m2", "t2"),
                Modified("a.cs"), Modified("b.cs"));

            var conflicts = vm.ConflictEditor.Blocks.Where(b => b.IsConflict).ToList();
            Assert.Same(conflicts[0], vm.ConflictEditor.CurrentBlock);

            vm.NextConflictCommand.Execute(null);
            Assert.Same(conflicts[1], vm.ConflictEditor.CurrentBlock);
            Assert.True(conflicts[1].IsCurrent);
            Assert.False(conflicts[0].IsCurrent);

            vm.NextConflictCommand.Execute(null);
            Assert.Equal("b.cs", vm.SelectedConflictFile?.Path);

            vm.PreviousConflictCommand.Execute(null);
            Assert.Equal("a.cs", vm.SelectedConflictFile?.Path);
            Assert.Same(vm.ConflictEditor.Blocks.Last(b => b.IsConflict), vm.ConflictEditor.CurrentBlock);
        }

        [Fact]
        public async Task Rebase_LoadsWithSidesSwapped_AndKeepMinePicksTheirsStage()
        {
            var fakeGit = new FakeGitService
            {
                NextPullSucceeds = false,
                ConflictedFiles = { Modified("a.cs") },
                Status = new GitRepoStatus { HasUpstream = true, UpstreamBranch = "origin/develop" }
            };
            var vm = new MainViewModel(fakeGit, new RecentRepositoriesService(), IsolatedTheme())
            {
                RepositoryPath = @"C:\FakeRepo",
                CurrentBranch = "feature/x",
                RepoStatus = new GitRepoStatus { HasUpstream = true, UpstreamBranch = "origin/develop" }
            };

            await vm.ConfirmPullRebaseCommand.ExecuteAsync(null);

            Assert.Equal("Rebasing feature/x onto origin/develop", vm.MergeConflictDialogTitle);
            Assert.True(fakeGit.LastMineIsTheirsStage);

            await vm.KeepMineWholeFileCommand.ExecuteAsync(null);

            Assert.Equal(("a.cs", false), Assert.Single(fakeGit.CheckedOutSides));
            Assert.True(vm.CanContinueMerge);
        }

        [Fact]
        public async Task WholeFilePickOnLastFile_ClearsTheStaleEditorSoSaveCantOverwriteIt()
        {
            var (vm, git) = await OpenMergeConflictAsync(Modified("a.cs"));

            await vm.KeepMineWholeFileCommand.ExecuteAsync(null);

            Assert.False(vm.ConflictEditor.IsTextMerge);
            Assert.Empty(vm.ConflictEditor.Blocks);
            await vm.SaveConflictFileCommand.ExecuteAsync(null);
            Assert.Empty(git.WrittenFiles);
        }

        [Fact]
        public async Task TakeTheirsWholeFile_InAMerge_UsesTheirsStage()
        {
            var (vm, git) = await OpenMergeConflictAsync(Modified("a.cs"));

            await vm.TakeTheirsWholeFileCommand.ExecuteAsync(null);

            Assert.Equal(("a.cs", false), Assert.Single(git.CheckedOutSides));
            Assert.True(vm.MergeConflictFiles[0].IsResolved);
        }

        [Fact]
        public async Task DeleteModifyConflict_ExplainsInPlainWordsAndOffersKeepOrDelete()
        {
            var (vm, git) = await OpenMergeConflictAsync(
                new MergeConflictFile { Path = "gone.cs", ConflictType = MergeConflictType.DeletedByThem },
                new MergeConflictFile { Path = "both.cs", ConflictType = MergeConflictType.BothDeleted });

            Assert.True(vm.ConflictEditor.IsOneSided);
            Assert.Equal("develop deleted this file, feature/x changed it.", vm.ConflictEditor.Message);
            Assert.True(vm.ConflictEditor.CanKeepFile);

            await vm.KeepConflictedFileCommand.ExecuteAsync(null);
            Assert.Contains(git.Changes.StagedFiles, f => f.Path == "gone.cs");

            Assert.Equal("both.cs", vm.ConflictEditor.File?.Path);
            Assert.False(vm.ConflictEditor.CanKeepFile);
            await vm.DeleteConflictedFileCommand.ExecuteAsync(null);
            Assert.Equal(new[] { "both.cs" }, git.RemovedFiles);
            Assert.True(vm.CanContinueMerge);
        }

        [Fact]
        public async Task FinishMerge_SavesAnUnsavedResolvedFileFirst()
        {
            var (vm, git) = await OpenMergeConflictAsync(Modified("a.cs"));
            vm.ConflictEditor.Blocks.Single(b => b.IsConflict).UseMine();

            await vm.ContinueMergeCommand.ExecuteAsync(null);

            Assert.True(git.WrittenFiles.ContainsKey("a.cs"));
            Assert.True(git.MergeContinued);
            Assert.False(vm.IsMergeConflictDialogOpen);
        }

        [Fact]
        public async Task Save_RefusesLeftoverMarkers()
        {
            var errors = new List<string>();
            var (vm, git) = await OpenMergeConflictAsync(Modified("a.cs"));
            vm.ReportErrorAction = (message, _) => errors.Add(message);

            vm.ConflictEditor.Blocks.Single(b => b.IsConflict).ResultText = ">>>>>>> develop";
            await vm.SaveConflictFileCommand.ExecuteAsync(null);

            Assert.False(git.WrittenFiles.ContainsKey("a.cs"));
            Assert.Contains("Line 2", Assert.Single(errors));
            Assert.False(vm.MergeConflictFiles[0].IsResolved);
        }
    }

    /// <summary>The editor against real git: conflict, pick, save, finish.</summary>
    public class ConflictEditorIntegrationTests : IDisposable
    {
        private readonly string _repo = Path.Combine(Path.GetTempPath(), "GirtEditor_" + Guid.NewGuid().ToString("N"));

        public ConflictEditorIntegrationTests()
        {
            Directory.CreateDirectory(_repo);
            Git("init -b main");
            Git("config user.name \"Girt Tester\"");
            Git("config user.email \"test@girt.dev\"");
            Git("config core.autocrlf false");
        }

        private string Git(string args)
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = _repo,
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

        private void Commit(string content, string message)
        {
            File.WriteAllBytes(Path.Combine(_repo, "shared.txt"), Encoding.UTF8.GetBytes(content));
            Git("add shared.txt");
            Git($"commit -m \"{message}\"");
        }

        [Fact]
        public async Task CrlfFile_ResolvedInEditor_IsWrittenExactlyAndMergeFinishes()
        {
            Commit("one\r\ntwo\r\nthree\r\n", "base");
            Git("checkout -b feature");
            Commit("one\r\nFEATURE\r\nthree\r\n", "feature");
            Git("checkout main");
            Commit("one\r\nMAIN\r\nthree\r\n", "main");

            var vm = new MainViewModel(new GitCliService(), new RecentRepositoriesService(),
                new ThemeService(Path.Combine(_repo, "..", $"GirtSettings_{Guid.NewGuid():N}.json")))
            {
                RepositoryPath = _repo,
                CurrentBranch = "main",
                ReportErrorAction = (message, _) => throw new Xunit.Sdk.XunitException(message)
            };
            await vm.MergeIntoCurrentBranchCommand.ExecuteAsync(new GitBranch { Name = "feature" });
            await vm.ConfirmMergeCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => vm.ConflictEditor.IsTextMerge);

            var conflict = vm.ConflictEditor.Blocks.Single(b => b.IsConflict);
            Assert.Equal("MAIN", conflict.MineText);
            Assert.Equal("FEATURE", conflict.TheirsText);
            conflict.UseMineThenTheirs();
            await vm.ContinueMergeCommand.ExecuteAsync(null);

            Assert.False(vm.IsMergeConflictDialogOpen);
            Assert.Equal("one\r\nMAIN\r\nFEATURE\r\nthree\r\n", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_repo, "shared.txt"))));
            Assert.Equal(2, Git("show -s --format=%P HEAD").Split(' ').Length);
            Assert.Equal("", Git("status --porcelain"));
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
            Assert.True(condition());
        }

        public void Dispose()
        {
            try
            {
                foreach (var file in Directory.GetFiles(_repo, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(_repo, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }
}
