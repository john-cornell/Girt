using System;
using System.Collections.Generic;
using System.Linq;
using Girt.Models;

namespace Girt.Services
{
    public static class GitGraphLayoutEngine
    {
        // Palette of vibrant, distinct modern colors for branch lanes
        public static readonly string[] LaneColors = new[]
        {
            "#3B82F6", // Blue
            "#10B981", // Emerald / Mint
            "#F59E0B", // Amber
            "#EC4899", // Pink
            "#8B5CF6", // Purple
            "#06B6D4", // Cyan
            "#F97316", // Orange
            "#14B8A6", // Teal
            "#A855F7", // Violet
            "#EF4444", // Red
        };

        public static void ComputeGraphLayout(IReadOnlyList<GitCommit> commits)
        {
            if (commits == null || commits.Count == 0) return;

            // Map commit hash to row index and commit object
            var commitMap = new Dictionary<string, GitCommit>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < commits.Count; i++)
            {
                commits[i].RowIndex = i;
                commits[i].Connections.Clear();
                if (!string.IsNullOrEmpty(commits[i].Hash))
                {
                    commitMap[commits[i].Hash] = commits[i];
                }
            }

            // activeLanes[laneIndex] = hash of the commit expected next on that lane
            var activeLanes = new List<string?>();

            for (int rowIndex = 0; rowIndex < commits.Count; rowIndex++)
            {
                var commit = commits[rowIndex];
                
                // Keep track of lanes active BEFORE this commit added its own parents
                var lanesActiveBefore = new HashSet<int>();
                for (int l = 0; l < activeLanes.Count; l++)
                {
                    if (activeLanes[l] != null)
                    {
                        lanesActiveBefore.Add(l);
                    }
                }

                // 1. Find if this commit was expected on an active lane
                int assignedLane = activeLanes.IndexOf(commit.Hash);

                if (assignedLane == -1)
                {
                    // Find first empty slot in activeLanes or add new
                    assignedLane = activeLanes.IndexOf(null);
                    if (assignedLane == -1)
                    {
                        assignedLane = activeLanes.Count;
                        activeLanes.Add(commit.Hash);
                    }
                    else
                    {
                        activeLanes[assignedLane] = commit.Hash;
                    }
                }

                commit.LaneIndex = assignedLane;
                commit.LaneColor = LaneColors[assignedLane % LaneColors.Length];

                // 2. Clear this commit from the active lane slot
                activeLanes[assignedLane] = null;
                lanesActiveBefore.Remove(assignedLane);

                // Set of target lanes used by this commit's parent connections
                var connectedLanes = new HashSet<int>();

                // 3. Process parents
                var parents = commit.ParentHashes;
                if (parents.Count > 0)
                {
                    // First parent continues on the current lane if possible
                    var firstParentHash = parents[0];
                    int firstParentLane = activeLanes.IndexOf(firstParentHash);

                    if (firstParentLane == -1)
                    {
                        // Assign the current lane to the first parent
                        activeLanes[assignedLane] = firstParentHash;
                        firstParentLane = assignedLane;
                    }

                    commit.Connections.Add(new GraphConnection
                    {
                        FromLane = assignedLane,
                        ToLane = firstParentLane,
                        ToRowOffset = 1,
                        Color = commit.LaneColor
                    });
                    connectedLanes.Add(firstParentLane);

                    // Subsequent parents (merges)
                    for (int p = 1; p < parents.Count; p++)
                    {
                        var parentHash = parents[p];
                        int parentLane = activeLanes.IndexOf(parentHash);

                        if (parentLane == -1)
                        {
                            // Find an open slot for the merge parent
                            parentLane = activeLanes.IndexOf(null);
                            if (parentLane == -1)
                            {
                                parentLane = activeLanes.Count;
                                activeLanes.Add(parentHash);
                            }
                            else
                            {
                                activeLanes[parentLane] = parentHash;
                            }
                        }

                        commit.Connections.Add(new GraphConnection
                        {
                            FromLane = assignedLane,
                            ToLane = parentLane,
                            ToRowOffset = 1,
                            Color = LaneColors[parentLane % LaneColors.Length]
                        });
                        connectedLanes.Add(parentLane);
                    }
                }

                // 4. Add pass-through indicators only for other pre-existing active lanes
                foreach (var lane in lanesActiveBefore)
                {
                    if (lane != assignedLane && !connectedLanes.Contains(lane) && activeLanes[lane] != null)
                    {
                        // Lane continues straight down
                        commit.Connections.Add(new GraphConnection
                        {
                            FromLane = lane,
                            ToLane = lane,
                            ToRowOffset = 1,
                            Color = LaneColors[lane % LaneColors.Length],
                            IsPassThrough = true
                        });
                    }
                }

                // Trim trailing nulls from activeLanes list
                while (activeLanes.Count > 0 && activeLanes[activeLanes.Count - 1] == null)
                {
                    activeLanes.RemoveAt(activeLanes.Count - 1);
                }
            }
        }
    }
}
