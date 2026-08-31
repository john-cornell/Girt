using System;
using System.Collections.Generic;

namespace Girt.Models
{
    public enum GitResetMode
    {
        Soft,  // Keeps changes staged in index & working tree
        Mixed, // Keeps changes in working tree, unstaged
        Hard   // Discards all changes, resets index & working tree
    }

    public enum BranchAssociationMode
    {
        ShowAll,            // Display full commit graph without filtering
        DimBeyondTrunk,     // Dim everything except active branch down to trunk fork point (Branch -> Trunk fork)
        HideBeyondTrunk,    // Hide everything except active branch down to trunk fork point (Branch -> Trunk fork)
        DimUnrelated,       // Dim unrelated branch commits while keeping full trunk lineage
        HideUnrelated       // Hide unrelated branch commits while keeping full trunk lineage
    }

    public class GitWorkingFile
    {
        public string Path { get; set; } = string.Empty;
        public string? OldPath { get; set; }
        public bool IsStaged { get; set; }
        public FileStatusType Status { get; set; } = FileStatusType.Modified;

        public string DisplayName => string.IsNullOrEmpty(OldPath) || OldPath == Path 
            ? Path 
            : $"{OldPath} → {Path}";

        public string StatusBadge => Status switch
        {
            FileStatusType.Added => "A",
            FileStatusType.Deleted => "D",
            FileStatusType.Modified => "M",
            FileStatusType.Renamed => "R",
            _ => "U"
        };

        public string StatusColor => Status switch
        {
            FileStatusType.Added => "#10B981",
            FileStatusType.Deleted => "#EF4444",
            FileStatusType.Modified => "#F59E0B",
            FileStatusType.Renamed => "#6366F1",
            _ => "#9CA3AF"
        };
    }

    public class WorkingTreeChanges
    {
        public List<GitWorkingFile> StagedFiles { get; set; } = new();
        public List<GitWorkingFile> UnstagedFiles { get; set; } = new();

        public int TotalCount => StagedFiles.Count + UnstagedFiles.Count;
        public bool HasStaged => StagedFiles.Count > 0;
        public bool HasUnstaged => UnstagedFiles.Count > 0;
    }

    public class GitRepoStatus
    {
        public int UncommittedCount { get; set; }
        public int AheadCount { get; set; }
        public int BehindCount { get; set; }
        public bool HasUpstream { get; set; }
        public string? UpstreamBranch { get; set; }

        public bool HasChangesToCommit => UncommittedCount > 0;
        public bool HasCommitsToPush => AheadCount > 0;
        public bool HasCommitsToPull => BehindCount > 0;
    }

    public class GitBranch
    {
        public string Name { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public bool IsCurrent { get; set; }
        public bool IsRemote { get; set; }
        public string? RemoteName { get; set; }
        public string? UpstreamName { get; set; }
        public string TipCommitHash { get; set; } = string.Empty;
        public string TipCommitSubject { get; set; } = string.Empty;

        public string DisplayName => IsRemote && !string.IsNullOrEmpty(RemoteName) && Name.StartsWith(RemoteName + "/")
            ? Name.Substring(RemoteName.Length + 1)
            : Name;
    }

    public class GitCommit
    {
        public string Hash { get; set; } = string.Empty;
        public string ShortHash => Hash.Length >= 7 ? Hash.Substring(0, 7) : Hash;
        public List<string> ParentHashes { get; set; } = new();
        public string AuthorName { get; set; } = string.Empty;
        public string AuthorEmail { get; set; } = string.Empty;
        public DateTimeOffset Date { get; set; }
        public string RelativeDate { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public List<GitRefBadge> Refs { get; set; } = new();
        
        // Graph rendering metadata computed by GitGraphLayoutEngine
        public int RowIndex { get; set; }
        public int LaneIndex { get; set; }
        public string LaneColor { get; set; } = "#3B82F6";
        public List<GraphConnection> Connections { get; set; } = new();

        // Branch association & dimming state
        public bool IsAssociated { get; set; } = true;
        public bool IsDimmed { get; set; } = false;
        public double DisplayOpacity => IsDimmed ? 0.35 : 1.0;
    }

    public enum GitRefType
    {
        Head,
        LocalBranch,
        RemoteBranch,
        Tag
    }

    public class GitRefBadge
    {
        public string Name { get; set; } = string.Empty;
        public GitRefType RefType { get; set; }
        public bool IsCurrentHead { get; set; }

        public string BackgroundColor => RefType switch
        {
            GitRefType.Head => "#10B981", // Emerald
            GitRefType.LocalBranch => "#3B82F6", // Blue
            GitRefType.RemoteBranch => "#8B5CF6", // Purple
            GitRefType.Tag => "#F59E0B", // Amber
            _ => "#6B7280"
        };
    }

    public class GraphConnection
    {
        public int FromLane { get; set; }
        public int ToLane { get; set; }
        public int ToRowOffset { get; set; }
        public string Color { get; set; } = "#3B82F6";
        public bool IsPassThrough { get; set; }
    }

    public enum DiffLineType
    {
        Context,
        Added,
        Deleted,
        Header
    }

    public class DiffLine
    {
        public DiffLineType Type { get; set; }
        public int? OldLineNumber { get; set; }
        public int? NewLineNumber { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    public enum FileStatusType
    {
        Modified,
        Added,
        Deleted,
        Renamed,
        Untracked
    }

    public class GitFileDiff
    {
        public string Path { get; set; } = string.Empty;
        public string? OldPath { get; set; }
        public FileStatusType Status { get; set; } = FileStatusType.Modified;
        public int Additions { get; set; }
        public int Deletions { get; set; }
        public List<DiffLine> Lines { get; set; } = new();

        public string DisplayName => string.IsNullOrEmpty(OldPath) || OldPath == Path 
            ? Path 
            : $"{OldPath} → {Path}";

        public string StatusBadge => Status switch
        {
            FileStatusType.Added => "A",
            FileStatusType.Deleted => "D",
            FileStatusType.Modified => "M",
            FileStatusType.Renamed => "R",
            _ => "U"
        };

        public string StatusColor => Status switch
        {
            FileStatusType.Added => "#10B981",
            FileStatusType.Deleted => "#EF4444",
            FileStatusType.Modified => "#F59E0B",
            FileStatusType.Renamed => "#6366F1",
            _ => "#9CA3AF"
        };
    }
}
