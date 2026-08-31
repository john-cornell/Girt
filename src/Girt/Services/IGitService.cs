using System.Collections.Generic;
using System.Threading.Tasks;
using Girt.Models;

namespace Girt.Services
{
    public interface IGitService
    {
        Task<string?> GetRepositoryRootAsync(string directoryPath);
        Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repoPath);
        Task<IReadOnlyList<GitCommit>> GetCommitsAsync(string repoPath, int maxCount = 1000);
        Task<IReadOnlyList<GitFileDiff>> GetCommitDiffAsync(string repoPath, string commitHash);
        Task<string> GetRawFileDiffAsync(string repoPath, string commitHash, string filePath);
        Task<(bool Success, string Output)> CheckoutBranchAsync(string repoPath, string branchName);
        Task<(bool Success, string Output)> CreateBranchAsync(string repoPath, string branchName, string? startPoint = null);
        Task<(bool Success, string Output)> DeleteBranchAsync(string repoPath, string branchName, bool force = false);
        Task<string?> GetCurrentBranchAsync(string repoPath);
        Task<GitRepoStatus> GetRepoStatusAsync(string repoPath);
        Task<(bool Success, string Output)> ResetHeadAsync(string repoPath, string targetRef, GitResetMode mode);

        // Working tree & Staging / Commit operations
        Task<WorkingTreeChanges> GetWorkingTreeChangesAsync(string repoPath);
        Task<(bool Success, string Output)> StageFileAsync(string repoPath, string filePath);
        Task<(bool Success, string Output)> UnstageFileAsync(string repoPath, string filePath);
        Task<(bool Success, string Output)> StageAllAsync(string repoPath);
        Task<(bool Success, string Output)> UnstageAllAsync(string repoPath);
        Task<(bool Success, string Output)> DiscardChangesAsync(string repoPath, string filePath);
        Task<(bool Success, string Output)> CommitAsync(string repoPath, string message);
        Task<string> GetWorkingTreeFileDiffAsync(string repoPath, string filePath, bool isStaged);
        Task<(bool Success, string Output)> PushAsync(string repoPath);
        Task<(bool Success, string Output)> PullAsync(string repoPath);
        Task<(bool Success, string Output)> FetchAllAsync(string repoPath);
        Task<string?> GetMergeBaseAsync(string repoPath, string ref1, string ref2);
        Task<(bool Success, string Output)> AddToGitIgnoreAsync(string repoPath, string filePath, bool ignoreByExtension = false);
        Task<(bool Success, string Output)> StashStagedAsync(string repoPath, string? message = null);
        Task<(bool Success, string Output)> StashPopAsync(string repoPath);
        Task<(bool Success, string Output)> StashApplyAsync(string repoPath);
        Task<int> GetStashCountAsync(string repoPath);
    }
}
