using System.Collections.Generic;
using Girt.Models;
using Girt.Services;
using Xunit;

namespace Girt.Tests
{
    public class GitGraphLayoutEngineTests
    {
        [Fact]
        public void ComputeGraphLayout_LinearHistory_AssignsSingleLane()
        {
            var commits = new List<GitCommit>
            {
                new() { Hash = "c3", ParentHashes = new() { "c2" }, Subject = "Commit 3" },
                new() { Hash = "c2", ParentHashes = new() { "c1" }, Subject = "Commit 2" },
                new() { Hash = "c1", ParentHashes = new(), Subject = "Commit 1" }
            };

            GitGraphLayoutEngine.ComputeGraphLayout(commits);

            Assert.Equal(0, commits[0].LaneIndex);
            Assert.Equal(0, commits[1].LaneIndex);
            Assert.Equal(0, commits[2].LaneIndex);
            Assert.NotEmpty(commits[0].Connections);
            Assert.NotEmpty(commits[1].Connections);
        }

        [Fact]
        public void ComputeGraphLayout_MergeHistory_CreatesMultipleConnections()
        {
            var commits = new List<GitCommit>
            {
                new() { Hash = "merge", ParentHashes = new() { "main_tip", "feat_tip" }, Subject = "Merge branch feat" },
                new() { Hash = "main_tip", ParentHashes = new() { "root" }, Subject = "Main commit" },
                new() { Hash = "feat_tip", ParentHashes = new() { "root" }, Subject = "Feature commit" },
                new() { Hash = "root", ParentHashes = new(), Subject = "Initial commit" }
            };

            GitGraphLayoutEngine.ComputeGraphLayout(commits);

            Assert.Equal(2, commits[0].Connections.Count);
        }
    }
}
