using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Girt.Models;

namespace Girt.Services
{
    public class GitCliService : IGitService
    {
        private const char RecordSeparator = '\x1e';
        private const char FieldSeparator = '\x1f';

        public async Task<string?> GetRepositoryRootAsync(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            {
                return null;
            }

            var (success, output, _) = await RunGitCommandAsync(directoryPath, "rev-parse --show-toplevel");
            if (success && !string.IsNullOrWhiteSpace(output))
            {
                var trimmed = output.Trim().Replace('/', Path.DirectorySeparatorChar);
                return Directory.Exists(trimmed) ? trimmed : directoryPath;
            }

            return null;
        }

        public async Task<string?> GetCurrentBranchAsync(string repoPath)
        {
            var (success, output, _) = await RunGitCommandAsync(repoPath, "rev-parse --abbrev-ref HEAD");
            if (success && !string.IsNullOrWhiteSpace(output))
            {
                var branch = output.Trim();
                return branch == "HEAD" ? "Detached HEAD" : branch;
            }
            return null;
        }

        public async Task<GitRepoStatus> GetRepoStatusAsync(string repoPath)
        {
            var status = new GitRepoStatus();
            if (string.IsNullOrWhiteSpace(repoPath)) return status;

            // 1. Uncommitted changes count (staged + unstaged + untracked)
            var (statusSuccess, statusOutput, _) = await RunGitCommandAsync(repoPath, "status --porcelain=v1 -uall");
            if (statusSuccess && !string.IsNullOrWhiteSpace(statusOutput))
            {
                var lines = statusOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                status.UncommittedCount = lines.Length;
            }

            // 2. Upstream branch info
            var (upSuccess, upOutput, _) = await RunGitCommandAsync(repoPath, "rev-parse --abbrev-ref @{u}");
            if (upSuccess && !string.IsNullOrWhiteSpace(upOutput) && !upOutput.Contains("fatal:"))
            {
                status.HasUpstream = true;
                status.UpstreamBranch = upOutput.Trim();

                // 3. Commits ahead (to push)
                var (aheadSuccess, aheadOutput, _) = await RunGitCommandAsync(repoPath, "rev-list --count @{u}..HEAD");
                if (aheadSuccess && int.TryParse(aheadOutput.Trim(), out var ahead))
                {
                    status.AheadCount = ahead;
                }

                // 4. Commits behind (to pull)
                var (behindSuccess, behindOutput, _) = await RunGitCommandAsync(repoPath, "rev-list --count HEAD..@{u}");
                if (behindSuccess && int.TryParse(behindOutput.Trim(), out var behind))
                {
                    status.BehindCount = behind;
                }
            }

            return status;
        }

        public async Task<WorkingTreeChanges> GetWorkingTreeChangesAsync(string repoPath)
        {
            var result = new WorkingTreeChanges();
            if (string.IsNullOrWhiteSpace(repoPath)) return result;

            var (success, output, _) = await RunGitCommandAsync(repoPath, "status --porcelain=v1 -uall");
            if (!success || string.IsNullOrWhiteSpace(output)) return result;

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.Length < 3) continue;

                var indexCode = line[0];
                var workTreeCode = line[1];
                var rawPath = line.Substring(3).Trim();

                string path = rawPath;
                string? oldPath = null;

                if (rawPath.Contains(" -> "))
                {
                    var parts = rawPath.Split(new[] { " -> " }, StringSplitOptions.None);
                    oldPath = parts[0].Trim('"');
                    path = parts[1].Trim('"');
                }
                else
                {
                    path = rawPath.Trim('"');
                }

                // Staged change (index has M, A, D, R, C)
                if (indexCode != ' ' && indexCode != '?')
                {
                    var status = indexCode switch
                    {
                        'A' => FileStatusType.Added,
                        'D' => FileStatusType.Deleted,
                        'R' => FileStatusType.Renamed,
                        _ => FileStatusType.Modified
                    };

                    result.StagedFiles.Add(new GitWorkingFile
                    {
                        Path = path,
                        OldPath = oldPath,
                        IsStaged = true,
                        Status = status
                    });
                }

                // Unstaged change (work tree has M, D, or untracked ??)
                if (workTreeCode != ' ')
                {
                    var status = workTreeCode switch
                    {
                        '?' => FileStatusType.Untracked,
                        'D' => FileStatusType.Deleted,
                        _ => FileStatusType.Modified
                    };

                    result.UnstagedFiles.Add(new GitWorkingFile
                    {
                        Path = path,
                        OldPath = oldPath,
                        IsStaged = false,
                        Status = status
                    });
                }
            }

            return result;
        }

        public async Task<(bool Success, string Output)> StageFileAsync(string repoPath, string filePath)
        {
            var cleanPath = filePath.Replace("\"", "\\\"");
            var (success, output, error) = await RunGitCommandAsync(repoPath, $"add -- \"{cleanPath}\"");
            return (success, (output + "\n" + error).Trim());
        }

        public async Task<(bool Success, string Output)> UnstageFileAsync(string repoPath, string filePath)
        {
            var cleanPath = filePath.Replace("\"", "\\\"");
            var (success, output, error) = await RunGitCommandAsync(repoPath, $"restore --staged -- \"{cleanPath}\"");
            return (success, (output + "\n" + error).Trim());
        }

        public async Task<(bool Success, string Output)> StageAllAsync(string repoPath)
        {
            var (success, output, error) = await RunGitCommandAsync(repoPath, "add -A");
            return (success, (output + "\n" + error).Trim());
        }

        public async Task<(bool Success, string Output)> UnstageAllAsync(string repoPath)
        {
            var (success, output, error) = await RunGitCommandAsync(repoPath, "restore --staged .");
            return (success, (output + "\n" + error).Trim());
        }

        public async Task<(bool Success, string Output)> DiscardChangesAsync(string repoPath, string filePath)
        {
            var cleanPath = filePath.Replace("\"", "\\\"");
            // Check if untracked
            var fullPath = Path.Combine(repoPath, filePath);
            if (File.Exists(fullPath))
            {
                var (isUntrackedSuccess, untrackedOutput, _) = await RunGitCommandAsync(repoPath, $"ls-files --error-unmatch \"{cleanPath}\"");
                if (!isUntrackedSuccess)
                {
                    // Untracked file -> delete it
                    try
                    {
                        File.Delete(fullPath);
                        return (true, "Deleted untracked file");
                    }
                    catch (Exception ex)
                    {
                        return (false, ex.Message);
                    }
                }
            }

            var (success, output, error) = await RunGitCommandAsync(repoPath, $"restore -- \"{cleanPath}\"");
            return (success, (output + "\n" + error).Trim());
        }

        public async Task<(bool Success, string Output)> CommitAsync(string repoPath, string message)
        {
            var tempMsgFile = Path.Combine(Path.GetTempPath(), $"girt_commit_{Guid.NewGuid():N}.txt");
            try
            {
                await File.WriteAllTextAsync(tempMsgFile, message, Encoding.UTF8);
                var (success, output, error) = await RunGitCommandAsync(repoPath, $"commit -F \"{tempMsgFile}\"");
                return (success, (output + "\n" + error).Trim());
            }
            finally
            {
                if (File.Exists(tempMsgFile))
                {
                    File.Delete(tempMsgFile);
                }
            }
        }

        public async Task<string> GetWorkingTreeFileDiffAsync(string repoPath, string filePath, bool isStaged)
        {
            var cleanPath = filePath.Replace("\"", "\\\"");
            if (isStaged)
            {
                var (success, output, _) = await RunGitCommandAsync(repoPath, $"diff --cached -- \"{cleanPath}\"");
                return success ? output : "";
            }
            else
            {
                var (success, output, _) = await RunGitCommandAsync(repoPath, $"diff -- \"{cleanPath}\"");
                if (success && !string.IsNullOrWhiteSpace(output))
                {
                    return output;
                }

                // If untracked file, format it as a new file diff
                var fullPath = Path.Combine(repoPath, filePath);
                if (File.Exists(fullPath))
                {
                    try
                    {
                        var content = await File.ReadAllTextAsync(fullPath);
                        var sb = new StringBuilder();
                        sb.AppendLine($"diff --git a/{filePath} b/{filePath}");
                        sb.AppendLine("new file mode 100644");
                        sb.AppendLine("--- /dev/null");
                        sb.AppendLine($"+++ b/{filePath}");
                        
                        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                        sb.AppendLine($"@@ -0,0 +1,{lines.Length} @@");
                        foreach (var l in lines)
                        {
                            sb.AppendLine("+" + l);
                        }
                        return sb.ToString();
                    }
                    catch { }
                }

                return "";
            }
        }

        public async Task<(bool Success, string Output)> PushAsync(string repoPath)
        {
            var (success, output, error) = await RunGitCommandAsync(repoPath, "push");
            return (success, (output + "\n" + error).Trim());
        }

        public async Task<(bool Success, string Output)> PullAsync(string repoPath)
        {
            var (success, output, error) = await RunGitCommandAsync(repoPath, "pull");
            return (success, (output + "\n" + error).Trim());
        }

        public async Task<(bool Success, string Output)> FetchAllAsync(string repoPath)
        {
            var (success, output, error) = await RunGitCommandAsync(repoPath, "fetch --all --prune");
            return (success, (output + "\n" + error).Trim());
        }

        public async Task<string?> GetMergeBaseAsync(string repoPath, string ref1, string ref2)
        {
            var (success, output, _) = await RunGitCommandAsync(repoPath, $"merge-base \"{ref1}\" \"{ref2}\"");
            if (success && !string.IsNullOrWhiteSpace(output))
            {
                return output.Trim();
            }
            return null;
        }

        public async Task<(bool Success, string Output)> AddToGitIgnoreAsync(string repoPath, string filePath, bool ignoreByExtension = false)
        {
            if (string.IsNullOrWhiteSpace(repoPath) || string.IsNullOrWhiteSpace(filePath))
            {
                return (false, "Invalid repository or file path.");
            }

            try
            {
                var gitIgnorePath = Path.Combine(repoPath, ".gitignore");
                var pattern = filePath.Replace('\\', '/');

                if (ignoreByExtension)
                {
                    var ext = Path.GetExtension(filePath);
                    if (!string.IsNullOrEmpty(ext))
                    {
                        pattern = $"*{ext}";
                    }
                }

                var existing = File.Exists(gitIgnorePath)
                    ? (await File.ReadAllLinesAsync(gitIgnorePath)).Select(l => l.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (!existing.Contains(pattern))
                {
                    var prefix = (File.Exists(gitIgnorePath) && new FileInfo(gitIgnorePath).Length > 0 && !File.ReadAllText(gitIgnorePath).EndsWith('\n'))
                        ? "\n"
                        : "";
                    await File.AppendAllTextAsync(gitIgnorePath, $"{prefix}{pattern}\n", Encoding.UTF8);
                }

                // Also unstage cached tracking if file was in index
                await RunGitCommandAsync(repoPath, $"rm --cached -- \"{filePath.Replace("\"", "\\\"")}\"");

                return (true, $"Added '{pattern}' to .gitignore");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public async Task<(bool Success, string Output)> StashStagedAsync(string repoPath, string? message = null)
        {
            var msgArg = string.IsNullOrWhiteSpace(message) ? "" : $" -m \"{message.Replace("\"", "\\\"")}\"";
            var (success, output, error) = await RunGitCommandAsync(repoPath, $"stash push --staged{msgArg}");
            if (!success)
            {
                // Fallback for git versions that don't support --staged
                (success, output, error) = await RunGitCommandAsync(repoPath, $"stash push -k{msgArg}");
            }
            return (success, success ? (string.IsNullOrEmpty(output) ? "Stashed staged changes" : output.Trim()) : error);
        }

        public async Task<(bool Success, string Output)> StashPopAsync(string repoPath)
        {
            var (success, output, error) = await RunGitCommandAsync(repoPath, "stash pop");
            return (success, success ? (string.IsNullOrEmpty(output) ? "Popped top stash" : output.Trim()) : error);
        }

        public async Task<(bool Success, string Output)> StashApplyAsync(string repoPath)
        {
            var (success, output, error) = await RunGitCommandAsync(repoPath, "stash apply");
            return (success, success ? (string.IsNullOrEmpty(output) ? "Applied top stash" : output.Trim()) : error);
        }

        public async Task<int> GetStashCountAsync(string repoPath)
        {
            var (success, output, _) = await RunGitCommandAsync(repoPath, "stash list");
            if (!success || string.IsNullOrWhiteSpace(output)) return 0;
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        public async Task<(bool Success, string Output)> ResetHeadAsync(string repoPath, string targetRef, GitResetMode mode)
        {
            var flag = mode switch
            {
                GitResetMode.Soft => "--soft",
                GitResetMode.Hard => "--hard",
                _ => "--mixed"
            };

            var cleanTarget = string.IsNullOrWhiteSpace(targetRef) ? "HEAD~1" : targetRef.Trim();
            var (success, output, error) = await RunGitCommandAsync(repoPath, $"reset {flag} \"{cleanTarget}\"");
            var combined = (output + "\n" + error).Trim();
            return (success, combined);
        }

        public async Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repoPath)
        {
            var branches = new List<GitBranch>();
            var format = "%(HEAD)%09%(refname)%09%(upstream:short)%09%(objectname)%09%(contents:subject)";
            var (success, output, _) = await RunGitCommandAsync(repoPath, $"for-each-ref --format=\"{format}\" refs/heads refs/remotes");

            if (!success || string.IsNullOrWhiteSpace(output))
            {
                return branches;
            }

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var parts = line.Split('\t');
                if (parts.Length < 4) continue;

                var isHead = parts[0].Trim() == "*";
                var refName = parts[1].Trim();
                var upstream = parts.Length > 2 ? parts[2].Trim() : "";
                var commitHash = parts.Length > 3 ? parts[3].Trim() : "";
                var subject = parts.Length > 4 ? parts[4].Trim() : "";

                if (refName.StartsWith("refs/heads/"))
                {
                    var name = refName.Substring("refs/heads/".Length);
                    branches.Add(new GitBranch
                    {
                        Name = name,
                        FullName = refName,
                        IsCurrent = isHead,
                        IsRemote = false,
                        UpstreamName = string.IsNullOrEmpty(upstream) ? null : upstream,
                        TipCommitHash = commitHash,
                        TipCommitSubject = subject
                    });
                }
                else if (refName.StartsWith("refs/remotes/"))
                {
                    var fullRemote = refName.Substring("refs/remotes/".Length);
                    if (fullRemote.EndsWith("/HEAD")) continue;

                    var slashIndex = fullRemote.IndexOf('/');
                    var remoteName = slashIndex > 0 ? fullRemote.Substring(0, slashIndex) : "origin";

                    branches.Add(new GitBranch
                    {
                        Name = fullRemote,
                        FullName = refName,
                        IsCurrent = false,
                        IsRemote = true,
                        RemoteName = remoteName,
                        TipCommitHash = commitHash,
                        TipCommitSubject = subject
                    });
                }
            }

            return branches.OrderByDescending(b => b.IsCurrent).ThenBy(b => b.Name).ToList();
        }

        public async Task<IReadOnlyList<GitCommit>> GetCommitsAsync(string repoPath, int maxCount = 1000)
        {
            var commits = new List<GitCommit>();
            var format = "%H%x1f%P%x1f%an%x1f%ae%x1f%at%x1f%cr%x1f%D%x1f%s%x1f%b%x1e";
            var (success, output, _) = await RunGitCommandAsync(repoPath, $"log --all --topo-order --format=format:\"{format}\" -n {maxCount}");

            if (!success || string.IsNullOrWhiteSpace(output))
            {
                return commits;
            }

            var currentHead = await GetCurrentBranchAsync(repoPath);
            var rawCommits = output.Split(RecordSeparator);
            var rowIndex = 0;

            foreach (var rawCommit in rawCommits)
            {
                if (string.IsNullOrWhiteSpace(rawCommit)) continue;

                var fields = rawCommit.Trim('\r', '\n').Split(FieldSeparator);
                if (fields.Length < 8) continue;

                var hash = fields[0].Trim();
                var parents = fields[1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                var authorName = fields[2];
                var authorEmail = fields[3];
                
                DateTimeOffset date = DateTimeOffset.UtcNow;
                if (long.TryParse(fields[4].Trim(), out var unixSeconds))
                {
                    date = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime();
                }

                var relativeDate = fields[5];
                var refDecorations = fields[6];
                var subject = fields[7];
                var body = fields.Length > 8 ? fields[8].Trim() : "";

                var badges = ParseRefBadges(refDecorations, currentHead);

                commits.Add(new GitCommit
                {
                    Hash = hash,
                    ParentHashes = parents,
                    AuthorName = authorName,
                    AuthorEmail = authorEmail,
                    Date = date,
                    RelativeDate = relativeDate,
                    Subject = subject,
                    Body = body,
                    Refs = badges,
                    RowIndex = rowIndex++
                });
            }

            return commits;
        }

        public async Task<IReadOnlyList<GitFileDiff>> GetCommitDiffAsync(string repoPath, string commitHash)
        {
            var files = new List<GitFileDiff>();
            var (success, output, _) = await RunGitCommandAsync(repoPath, $"show --numstat --format=format: \"{commitHash}\"");
            if (!success || string.IsNullOrWhiteSpace(output))
            {
                return files;
            }

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;

                int.TryParse(parts[0], out var adds);
                int.TryParse(parts[1], out var dels);
                var path = parts[2].Trim();

                var status = FileStatusType.Modified;
                string? oldPath = null;

                if (path.Contains(" => "))
                {
                    status = FileStatusType.Renamed;
                    var arrowIndex = path.IndexOf(" => ", StringComparison.Ordinal);
                    oldPath = path.Substring(0, arrowIndex).Replace("{", "").Trim();
                    path = path.Substring(arrowIndex + 4).Replace("}", "").Trim();
                }
                else if (adds > 0 && dels == 0)
                {
                    status = FileStatusType.Added;
                }
                else if (adds == 0 && dels > 0)
                {
                    status = FileStatusType.Deleted;
                }

                files.Add(new GitFileDiff
                {
                    Path = path,
                    OldPath = oldPath,
                    Status = status,
                    Additions = adds,
                    Deletions = dels
                });
            }

            return files;
        }

        public async Task<string> GetRawFileDiffAsync(string repoPath, string commitHash, string filePath)
        {
            var cleanPath = filePath.Replace("\"", "\\\"");
            var (success, output, _) = await RunGitCommandAsync(repoPath, $"show \"{commitHash}\" -- \"{cleanPath}\"");
            return success ? output : "";
        }

        public async Task<(bool Success, string Output)> CheckoutBranchAsync(string repoPath, string branchName)
        {
            if (branchName.StartsWith("origin/"))
            {
                var localName = branchName.Substring("origin/".Length);
                var trackResult = await RunGitCommandAsync(repoPath, $"checkout --track \"{branchName}\"");
                if (trackResult.Success) return (true, trackResult.Output);
                
                var directResult = await RunGitCommandAsync(repoPath, $"checkout \"{localName}\"");
                return (directResult.Success, directResult.Output + "\n" + directResult.Error);
            }

            var result = await RunGitCommandAsync(repoPath, $"checkout \"{branchName}\"");
            var combined = (result.Output + "\n" + result.Error).Trim();
            return (result.Success, combined);
        }

        public async Task<(bool Success, string Output)> CreateBranchAsync(string repoPath, string branchName, string? startPoint = null)
        {
            var cmd = string.IsNullOrEmpty(startPoint) 
                ? $"checkout -b \"{branchName}\""
                : $"checkout -b \"{branchName}\" \"{startPoint}\"";

            var result = await RunGitCommandAsync(repoPath, cmd);
            var combined = (result.Output + "\n" + result.Error).Trim();
            return (result.Success, combined);
        }

        public async Task<(bool Success, string Output)> DeleteBranchAsync(string repoPath, string branchName, bool force = false)
        {
            var flag = force ? "-D" : "-d";
            var result = await RunGitCommandAsync(repoPath, $"branch {flag} \"{branchName}\"");
            var combined = (result.Output + "\n" + result.Error).Trim();
            return (result.Success, combined);
        }

        private static List<GitRefBadge> ParseRefBadges(string refDecorations, string? currentHead)
        {
            var badges = new List<GitRefBadge>();
            if (string.IsNullOrWhiteSpace(refDecorations)) return badges;

            var items = refDecorations.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawItem in items)
            {
                var item = rawItem.Trim();
                if (string.IsNullOrEmpty(item)) continue;

                if (item.StartsWith("HEAD -> "))
                {
                    var headBranch = item.Substring("HEAD -> ".Length).Trim();
                    badges.Add(new GitRefBadge
                    {
                        Name = headBranch,
                        RefType = GitRefType.Head,
                        IsCurrentHead = true
                    });
                }
                else if (item == "HEAD")
                {
                    badges.Add(new GitRefBadge
                    {
                        Name = "HEAD",
                        RefType = GitRefType.Head,
                        IsCurrentHead = true
                    });
                }
                else if (item.StartsWith("tag: "))
                {
                    badges.Add(new GitRefBadge
                    {
                        Name = item.Substring("tag: ".Length).Trim(),
                        RefType = GitRefType.Tag
                    });
                }
                else if (item.Contains("/"))
                {
                    badges.Add(new GitRefBadge
                    {
                        Name = item,
                        RefType = GitRefType.RemoteBranch
                    });
                }
                else
                {
                    badges.Add(new GitRefBadge
                    {
                        Name = item,
                        RefType = GitRefType.LocalBranch,
                        IsCurrentHead = item == currentHead
                    });
                }
            }

            return badges;
        }

        private static async Task<(bool Success, string Output, string Error)> RunGitCommandAsync(string workingDirectory, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var process = new Process { StartInfo = psi };
                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null) outputBuilder.AppendLine(e.Data);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) errorBuilder.AppendLine(e.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();

                return (process.ExitCode == 0, outputBuilder.ToString(), errorBuilder.ToString());
            }
            catch (Exception ex)
            {
                return (false, string.Empty, ex.Message);
            }
        }
    }
}
