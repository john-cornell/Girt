using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Girt.Models;

namespace Girt.Services
{
    public static class DiffParser
    {
        private static readonly Regex ChunkHeaderRegex = new(@"^@@\s+-(\d+)(?:,(\d+))?\s+\+(\d+)(?:,(\d+))?\s+@@", RegexOptions.Compiled);

        public static List<DiffLine> ParseUnifiedDiff(string diffText)
        {
            var result = new List<DiffLine>();
            if (string.IsNullOrEmpty(diffText)) return result;

            var lines = diffText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            int oldLineNum = 0;
            int newLineNum = 0;
            bool insideChunk = false;

            foreach (var line in lines)
            {
                if (line.StartsWith("@@"))
                {
                    insideChunk = true;
                    var match = ChunkHeaderRegex.Match(line);
                    if (match.Success)
                    {
                        int.TryParse(match.Groups[1].Value, out oldLineNum);
                        int.TryParse(match.Groups[3].Value, out newLineNum);
                    }

                    result.Add(new DiffLine
                    {
                        Type = DiffLineType.Header,
                        Text = line
                    });
                    continue;
                }

                if (!insideChunk)
                {
                    result.Add(new DiffLine
                    {
                        Type = DiffLineType.Header,
                        Text = line
                    });
                    continue;
                }

                if (line.StartsWith("+"))
                {
                    result.Add(new DiffLine
                    {
                        Type = DiffLineType.Added,
                        NewLineNumber = newLineNum++,
                        Text = line
                    });
                }
                else if (line.StartsWith("-"))
                {
                    result.Add(new DiffLine
                    {
                        Type = DiffLineType.Deleted,
                        OldLineNumber = oldLineNum++,
                        Text = line
                    });
                }
                else if (line.StartsWith("\\ No newline at end of file"))
                {
                    result.Add(new DiffLine
                    {
                        Type = DiffLineType.Header,
                        Text = line
                    });
                }
                else
                {
                    result.Add(new DiffLine
                    {
                        Type = DiffLineType.Context,
                        OldLineNumber = oldLineNum++,
                        NewLineNumber = newLineNum++,
                        Text = line.Length > 0 ? line : " "
                    });
                }
            }

            return result;
        }
    }
}
