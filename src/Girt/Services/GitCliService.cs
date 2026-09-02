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

        // Fetch the whole file as context so the diff viewer can collapse/expand unchanged
        // sections client-side instead of re-running git for every expand.
        private const int FullDiffContextLines = 100000;

        public async Task<string?> GetRepositoryRootAsync(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            {
                return null;
            }

            var (success, output, _) = await RunGitCommandAsync(directoryPath, "rev-parse --show-toplevel").ConfigureAwait(false);
            if (success && !string.IsNullOrWhiteSpace(output))
            {
                var trimmed = output.Trim().Replace('/', Path.DirectorySeparatorChar);
                return Directory.Exists(trimmed) ? trimmed : directoryPath;
            }

            return null;
        }

        public async Task<string?> GetCurrentBranchAsync(string repoPath)
        {
            var (success, output, _) = await RunGitCommandAsync(repoPath, "rev-parse --abbrev-ref HEAD").ConfigureAwait(false);
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

            // Each of these is a separate git.exe process spawn, which has real wall-clock
            // overhead on top of whatever it actually computes - status and the upstream check
            // are independent, so run them concurrently rather than one after another.
            var statusTask = RunGitCommandAsync(repoPath, "status --porcelain=v1 -uall");
            var upstreamTask = RunGitCommandAsync(repoPath, "rev-parse --abbrev-ref @{u}");
            await Task.WhenAll(statusTask, upstreamTask).ConfigureAwait(false);

            var (statusSuccess, statusOutput, _) = await statusTask.ConfigureAwait(false);
            if (statusSuccess && !string.IsNullOrWhiteSpace(statusOutput))
            {
                var lines = statusOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                status.UncommittedCount = lines.Length;
            }

            var (upSuccess, upOutput, _) = await upstreamTask.ConfigureAwait(false);
            if (upSuccess && !string.IsNullOrWhiteSpace(upOutput) && !upOutput.Contains("fatal:"))
            {
                status.HasUpstream = true;
                status.UpstreamBranch = upOutput.Trim();

                // One process spawn instead of two: --left-right --count on the triple-dot range
                // gives "<only in @{u}>\t<only in HEAD>" - i.e. behind and ahead - together.
                var (aheadBehindSuccess, aheadBehindOutput, _) = await RunGitCommandAsync(repoPath, "rev-list --left-right --count @{u}...HEAD").ConfigureAwait(false);
                if (aheadBehindSuccess)
                {
                    var parts = aheadBehindOutput.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                    {
                        if (int.TryParse(parts[0], out var behind)) status.BehindCount = behind;
                        if (int.TryParse(parts[1], out var ahead)) status.AheadCount = ahead;
                    }
                }
            }

            return status;
        }

        public async Task<WorkingTreeChanges> GetWorkingTreeChangesAsync(string repoPath)
        {
            var result = new WorkingTreeChanges();
            if (string.IsNullOrWhiteSpace(repoPath)) return result;

            var (success, output, _) = await RunGitCommandAsync(repoPath, "status --porcelain=v1 -uall").ConfigureAwait(false);
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
            var (success, output, error) = await RunGitCommandAsync(repoPath, $"add -- \"{cleanPath}\"").ConfigureAwait(false);
            return (success, (output + "\n" + error).Trim());
        }

        public async Task<(bool Success, string Output)> UnstageFileAsync(string repoPath, string filePath)
        {
            var cleanPath = filePath.Replace("\"", "\\\"");
            var (success, output, error) = await RunGitCommandAsync(repoPath, $"restore --staged -- \"{cleanPath}\"").ConfigureAwait(false);
            return (success, (output + "\n" + error).Trim());
        }

        public async Task<(bool Success, string Output)> StageAllAsync(string repoPath)
        {
            var (success, output, error) = await RunGitCommandAsync(repoPath, "add -A").ConfigureAwait(false);
            return (success, (output + "\n" + error).Trim());
        }

        public async Task<(bool Success, string Output)> UnstageAllAsync(string repoPath)
        {
            var (success, output, error) = await RunGitCommandAsync(repoPath, "restore --staged .").ConfigureAwait(false);
            return (success, (output + "\n" + error).Trim());
        }

        public async Task<(bool Success, string Output)> DiscardChangesAsync(string repoPath, string filePath)
        {
            var cleanPath = filePath.Replace("\"", "\\\"");
            // Check if untracked
            var fullPath = Path.Combine(repoPath, filePath);
            if (File.Exists(fullPath))
            {
                var (isUntrackedSuccess, untrackedOutput, _) = await RunGitCommandAsync(repoPath, $"ls-files --error-unmatch \"{cleanPath}\"").ConfigureAwait(false);
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

            var (success, output, error) = await RunGitCommandAsync(repoPath, $"restore -- \"{cleanPath}\"").ConfigureAwait(false);
            return (success, (output + "\n" + error).Trim());
        }

        public async Task<(bool Success, string Output)> CommitAsync(string repoPath, string message)
        {
            var tempMsgFile = Path.Combine(Path.GetTempPath(), $"girt_commit_{Guid.NewGuid():N}.txt");
            try
            {
                await File.WriteAllTextAsync(tempMsgFile, message, Encoding.UTF8).ConfigureAwait(false);
                var (success, output, error) = await RunGitCommandAsync(repoPath, $"commit -F \"{tempMsgFile}\"").ConfigureAwait(false);
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
                var (success, output, _) = await RunGitCommandAsync(repoPath, $"diff --unified={FullDiffContextLines} --cached -- \"{cleanPath}\"").ConfigureAwait(false);
                return success ? output : "";
            }
            else
            {
                var (success, output, _) = await RunGitCommandAsync(repoPath, $"diff --unified={FullDiffContextLines} -- \"{cleanPath}\"").ConfigureAwait(false);
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
                        var content = await File.ReadAllTextAsync(fullPath).ConfigureAwait(false);
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
            var (success, output, error) = await RunGitCommandAsync(repoPath, "push").ConfigureAwait(false);
            return (success, (output + "\n" + error).Trim());
        }

        public async Task<(bool Success, string Output)> PullAsync(string repoPath, bool rebase = false)
        {
            var args = rebase ? "pull --rebase" : "pull";
            var (success, output, error) = await RunGitCommandAsync(repoPath, args).ConfigureAwait(false);
            return (success, (output + "\n" + error).Trim());
        }

        public async Task<(bool Success, string Output)> FetchAllAsync(string repoPath)
        {
            var (success, output, error) = await RunGitCommandAsync(repoPath, "fetch --all --prune").ConfigureAwait(false);
            return (success, (output + "\n" + error).Trim());
        }

        public async Task<string?> GetMergeBaseAsync(string repoPath, string ref1, string ref2)
        {
            var (success, output, _) = await RunGitCommandAsync(repoPath, $"merge-base \"{ref1}\" \"{ref2}\"").ConfigureAwait(false);
            if (success && !string.IsNullOrWhiteSpace(output))
            {
                return output.Trim();
            }
            return null;
        }

        public async Task<(bool Success, string Output)> AddToGitIgnoreAsync(string repoPath, string filePath, GitIgnoreTarget target = GitIgnoreTarget.File)
        {
            if (string.IsNullOrWhiteSpace(repoPath) || string.IsNullOrWhiteSpace(filePath))
            {
                return (false, "Invalid repository or file path.");
            }

            try
            {
                var gitIgnorePath = Path.Combine(repoPath, ".gitignore");
                var pattern = filePath.Replace('\\', '/');

                if (target == GitIgnoreTarget.Extension)
                {
                    var ext = Path.GetExtension(filePath);
                    if (!string.IsNullOrEmpty(ext))
                    {
                        pattern = $"*{ext}";
                    }
                }
                else if (target == GitIgnoreTarget.Folder)
                {
                    // filePath is already the exact folder the caller picked (see
                    // GitWorkingFile.AncestorFolders) - just normalize the trailing slash.
                    pattern = $"{pattern.TrimEnd('/')}/";
                }

                var existing = File.Exists(gitIgnorePath)
                    ? (await File.ReadAllLinesAsync(gitIgnorePath).ConfigureAwait(false)).Select(l => l.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (!existing.Contains(pattern))
                {
                    var prefix = (File.Exists(gitIgnorePath) && new FileInfo(gitIgnorePath).Length > 0 && !File.ReadAllText(gitIgnorePath).EndsWith('\n'))
                        ? "\n"
                        : "";
                    await File.AppendAllTextAsync(gitIgnorePath, $"{prefix}{pattern}\n", Encoding.UTF8).ConfigureAwait(false);
                }

                // Also unstage cached tracking - recursively for a folder, since it may
                // contain many already-tracked files that would now match the new rule.
                var rmArgs = target == GitIgnoreTarget.Folder
                    ? $"rm -r --cached -- \"{filePath.Replace("\"", "\\\"")}\""
                    : $"rm --cached -- \"{filePath.Replace("\"", "\\\"")}\"";
                await RunGitCommandAsync(repoPath, rmArgs).ConfigureAwait(false);

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
            var (success, output, error) = await RunGitCommandAsync(repoPath, $"stash push --staged{msgArg}").ConfigureAwait(false);
            if (!success)
            {
                // Fallback for git versions that don't support --staged
                (success, output, error) = await RunGitCommandAsync(repoPath, $"stash push -k{msgArg}").ConfigureAwait(false);
            }
            return (success, success ? (string.IsNullOrEmpty(output) ? "Stashed staged changes" : output.Trim()) : error);
        }

        public async Task<(bool Success, string Output)> StashPopAsync(string repoPath)
        {
            var (success, output, error) = await RunGitCommandAsync(repoPath, "stash pop").ConfigureAwait(false);
            return (success, success ? (string.IsNullOrEmpty(output) ? "Popped top stash" : output.Trim()) : error);
        }

        public async Task<(bool Success, string Output)> StashApplyAsync(string repoPath)
        {
            var (success, output, error) = await RunGitCommandAsync(repoPath, "stash apply").ConfigureAwait(false);
            return (success, success ? (string.IsNullOrEmpty(output) ? "Applied top stash" : output.Trim()) : error);
        }

        public async Task<int> GetStashCountAsync(string repoPath)
        {
            var (success, output, _) = await RunGitCommandAsync(repoPath, "stash list").ConfigureAwait(false);
            if (!success || string.IsNullOrWhiteSpace(output)) return 0;
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        public async Task<string?> GetGitConfigValueAsync(string repoPath, string key, bool global)
        {
            var scope = global ? "--global" : "--local";
            var (success, output, _) = await RunGitCommandAsync(repoPath, $"config {scope} --get {key}").ConfigureAwait(false);
            return success && !string.IsNullOrWhiteSpace(output) ? output.Trim() : null;
        }

        public async Task<(bool Success, string Output)> SetGitConfigValueAsync(string repoPath, string key, string value, bool global)
        {
            var scope = global ? "--global" : "--local";
            var (success, output, error) = await RunGitCommandAsync(repoPath, $"config {scope} {key} \"{value.Replace("\"", "\\\"")}\"").ConfigureAwait(false);
            return (success, (output + error).Trim());
        }

        public async Task<(bool Success, string Output)> UnsetLocalGitConfigValueAsync(string repoPath, string key)
        {
            var (success, output, error) = await RunGitCommandAsync(repoPath, $"config --local --unset {key}").ConfigureAwait(false);
            return (success, (output + error).Trim());
        }

        public async Task<string?> GetTopStashDescriptionAsync(string repoPath)
        {
            var (success, output, _) = await RunGitCommandAsync(repoPath, "stash list -1").ConfigureAwait(false);
            if (!success || string.IsNullOrWhiteSpace(output)) return null;
            var firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return firstLine?.Trim();
        }

        public async Task<(bool Success, string Output)> RevertCommitAsync(string repoPath, string commitHash)
        {
            var (success, output, error) = await RunGitCommandAsync(repoPath, $"revert --no-edit \"{commitHash}\"").ConfigureAwait(false);
            var combined = (output + "\n" + error).Trim();
            return (success, combined);
        }

        public async Task<(bool Success, string Output)> CherryPickCommitAsync(string repoPath, string commitHash)
        {
            // --no-commit applies the change to the working tree/index without committing it -
            // cherry-picked changes land as a local, reviewable, editable change first, the same
            // as any other change the user makes, rather than a commit appearing out of nowhere
            // that's immediately "ready to push" before anyone chose to commit it.
            var (success, output, error) = await RunGitCommandAsync(repoPath, $"cherry-pick --no-commit \"{commitHash}\"").ConfigureAwait(false);
            var combined = (output + "\n" + error).Trim();
            return (success, combined);
        }

        public async Task<(bool Success, string Output)> MergeAsync(string repoPath, string targetRef, bool squash = false, bool noFf = false)
        {
            var flags = "";
            if (squash) flags += " --squash";
            if (noFf) flags += " --no-ff";
            var (success, output, error) = await RunGitCommandAsync(repoPath, $"merge{flags} \"{targetRef}\"").ConfigureAwait(false);
            var combined = (output + "\n" + error).Trim();
            return (success, combined);
        }

        public async Task<(bool Success, string Output)> RebaseAsync(string repoPath, string targetRef)
        {
            var (success, output, error) = await RunGitCommandAsync(repoPath, $"rebase \"{targetRef}\"").ConfigureAwait(false);
            var combined = (output + "\n" + error).Trim();
            return (success, combined);
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
            var (success, output, error) = await RunGitCommandAsync(repoPath, $"reset {flag} \"{cleanTarget}\"").ConfigureAwait(false);
            var combined = (output + "\n" + error).Trim();
            return (success, combined);
        }

        public async Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repoPath)
        {
            var branches = new List<GitBranch>();
            var format = "%(HEAD)%09%(refname)%09%(upstream:short)%09%(objectname)%09%(contents:subject)";
            var (success, output, _) = await RunGitCommandAsync(repoPath, $"for-each-ref --format=\"{format}\" refs/heads refs/remotes").ConfigureAwait(false);

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
            var (success, output, _) = await RunGitCommandAsync(repoPath, $"log --all --topo-order --format=format:\"{format}\" -n {maxCount}").ConfigureAwait(false);

            if (!success || string.IsNullOrWhiteSpace(output))
            {
                return commits;
            }

            var currentHead = await GetCurrentBranchAsync(repoPath).ConfigureAwait(false);
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
            var (success, output, _) = await RunGitCommandAsync(repoPath, $"show --numstat --format=format: \"{commitHash}\"").ConfigureAwait(false);
            return success ? ParseNumstatOutput(output) : new List<GitFileDiff>();
        }

        public async Task<string> GetRawFileDiffAsync(string repoPath, string commitHash, string filePath)
        {
            var cleanPath = filePath.Replace("\"", "\\\"");
            var (success, output, _) = await RunGitCommandAsync(repoPath, $"show --unified={FullDiffContextLines} \"{commitHash}\" -- \"{cleanPath}\"").ConfigureAwait(false);
            return success ? output : "";
        }

        // Everything reachable from HEAD but not yet from the upstream branch - i.e. exactly
        // what a `git push` would send. Callers should only invoke this once they know an
        // upstream exists (GitRepoStatus.HasUpstream), since "@{u}" fails without one.
        public async Task<IReadOnlyList<GitFileDiff>> GetUnpushedDiffAsync(string repoPath)
        {
            var (success, output, _) = await RunGitCommandAsync(repoPath, "diff --numstat @{u}..HEAD").ConfigureAwait(false);
            return success ? ParseNumstatOutput(output) : new List<GitFileDiff>();
        }

        public async Task<string> GetRawUnpushedFileDiffAsync(string repoPath, string filePath)
        {
            var cleanPath = filePath.Replace("\"", "\\\"");
            var (success, output, _) = await RunGitCommandAsync(repoPath, $"diff --unified={FullDiffContextLines} @{{u}}..HEAD -- \"{cleanPath}\"").ConfigureAwait(false);
            return success ? output : "";
        }

        private static List<GitFileDiff> ParseNumstatOutput(string output)
        {
            var files = new List<GitFileDiff>();
            if (string.IsNullOrWhiteSpace(output)) return files;

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

        public async Task<(bool Success, string Output)> CheckoutBranchAsync(string repoPath, string branchName)
        {
            if (branchName.StartsWith("origin/"))
            {
                var localName = branchName.Substring("origin/".Length);
                var trackResult = await RunGitCommandAsync(repoPath, $"checkout --track \"{branchName}\"").ConfigureAwait(false);
                if (trackResult.Success) return (true, trackResult.Output);
                
                var directResult = await RunGitCommandAsync(repoPath, $"checkout \"{localName}\"").ConfigureAwait(false);
                return (directResult.Success, directResult.Output + "\n" + directResult.Error);
            }

            var result = await RunGitCommandAsync(repoPath, $"checkout \"{branchName}\"").ConfigureAwait(false);
            var combined = (result.Output + "\n" + result.Error).Trim();
            return (result.Success, combined);
        }

        public async Task<(bool Success, string Output)> CreateBranchAsync(string repoPath, string branchName, string? startPoint = null)
        {
            var cmd = string.IsNullOrEmpty(startPoint) 
                ? $"checkout -b \"{branchName}\""
                : $"checkout -b \"{branchName}\" \"{startPoint}\"";

            var result = await RunGitCommandAsync(repoPath, cmd).ConfigureAwait(false);
            var combined = (result.Output + "\n" + result.Error).Trim();
            return (result.Success, combined);
        }

        public async Task<(bool Success, string Output)> DeleteBranchAsync(string repoPath, string branchName, bool force = false)
        {
            var flag = force ? "-D" : "-d";
            var result = await RunGitCommandAsync(repoPath, $"branch {flag} \"{branchName}\"").ConfigureAwait(false);
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

        // Every git invocation goes through here, so it's the one place that can tell the
        // difference between "the working tree/git-state watchers just saw OUR OWN write"
        // (e.g. staging a file) and a genuinely external change (another tool, another process).
        private static DateTime _lastCommandCompletedUtc = DateTime.MinValue;
        public DateTime LastCommandCompletedUtc => _lastCommandCompletedUtc;

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

                await process.WaitForExitAsync().ConfigureAwait(false);

                _lastCommandCompletedUtc = DateTime.UtcNow;
                return (process.ExitCode == 0, outputBuilder.ToString(), errorBuilder.ToString());
            }
            catch (Exception ex)
            {
                _lastCommandCompletedUtc = DateTime.UtcNow;
                return (false, string.Empty, ex.Message);
            }
        }
    }
}
