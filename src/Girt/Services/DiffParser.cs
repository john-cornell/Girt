using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Girt.Models;

namespace Girt.Services
{
    public static class DiffParser
    {
        private static readonly Regex ChunkHeaderRegex = new(@"^@@\s+-(\d+)(?:,(\d+))?\s+\+(\d+)(?:,(\d+))?\s+@@", RegexOptions.Compiled);

        // Git commands request a huge unified-context window (see GitCliService), so long runs
        // of unchanged lines arrive in full. Collapse the middle of any run longer than this,
        // keeping a few lines of context visible on each edge for readability.
        private const int ContextEdgeLines = 3;
        private const int CollapseThreshold = ContextEdgeLines * 2 + 4;

        public static List<DiffLine> ParseUnifiedDiff(string diffText)
        {
            var lines = ParseUnifiedDiffRaw(diffText);
            return CollapseLongContextRuns(lines);
        }

        private static List<DiffLine> ParseUnifiedDiffRaw(string diffText)
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

        private static List<DiffLine> CollapseLongContextRuns(List<DiffLine> lines)
        {
            var result = new List<DiffLine>();
            var nextGroupId = 0;
            var i = 0;

            while (i < lines.Count)
            {
                if (lines[i].Type != DiffLineType.Context)
                {
                    result.Add(lines[i]);
                    i++;
                    continue;
                }

                var start = i;
                while (i < lines.Count && lines[i].Type == DiffLineType.Context) i++;
                var runLength = i - start;

                if (runLength <= CollapseThreshold)
                {
                    for (var j = start; j < i; j++) result.Add(lines[j]);
                    continue;
                }

                for (var j = start; j < start + ContextEdgeLines; j++) result.Add(lines[j]);

                var groupId = nextGroupId++;
                var hidden = lines.GetRange(start + ContextEdgeLines, runLength - ContextEdgeLines * 2);
                foreach (var h in hidden) h.CollapseGroupId = groupId;

                result.Add(new DiffLine
                {
                    Type = DiffLineType.CollapsedContext,
                    Text = DescribeHiddenLines(hidden.Count),
                    HiddenLines = hidden,
                    CollapseGroupId = groupId
                });

                for (var j = i - ContextEdgeLines; j < i; j++) result.Add(lines[j]);
            }

            return result;
        }

        private static string DescribeHiddenLines(int count) =>
            $"⋯ {count} unchanged line{(count == 1 ? "" : "s")} ⋯";

        /// <summary>Expands the given collapsed placeholder in place, or re-collapses the
        /// section a given (currently expanded) line belongs to. No-op for any other line.</summary>
        public static void ToggleCollapsedSection(ObservableCollection<DiffLine> diffLines, DiffLine? clicked)
        {
            if (clicked == null) return;

            if (clicked.Type == DiffLineType.CollapsedContext)
            {
                ExpandSection(diffLines, clicked);
            }
            else if (clicked.CollapseGroupId.HasValue)
            {
                CollapseGroup(diffLines, clicked.CollapseGroupId.Value);
            }
        }

        public static void ExpandAllCollapsedSections(ObservableCollection<DiffLine> diffLines)
        {
            for (var i = diffLines.Count - 1; i >= 0; i--)
            {
                if (diffLines[i].Type == DiffLineType.CollapsedContext)
                {
                    ExpandSection(diffLines, diffLines[i]);
                }
            }
        }

        private static void ExpandSection(ObservableCollection<DiffLine> diffLines, DiffLine placeholder)
        {
            var idx = diffLines.IndexOf(placeholder);
            if (idx < 0 || placeholder.HiddenLines == null) return;

            diffLines.RemoveAt(idx);
            for (var k = 0; k < placeholder.HiddenLines.Count; k++)
            {
                diffLines.Insert(idx + k, placeholder.HiddenLines[k]);
            }
        }

        private static void CollapseGroup(ObservableCollection<DiffLine> diffLines, int groupId)
        {
            var start = -1;
            var count = 0;
            for (var i = 0; i < diffLines.Count; i++)
            {
                if (diffLines[i].CollapseGroupId == groupId)
                {
                    if (start == -1) start = i;
                    count++;
                }
                else if (start != -1)
                {
                    break;
                }
            }

            if (start == -1) return;

            var hidden = new List<DiffLine>();
            for (var i = start; i < start + count; i++) hidden.Add(diffLines[i]);
            for (var i = 0; i < count; i++) diffLines.RemoveAt(start);

            diffLines.Insert(start, new DiffLine
            {
                Type = DiffLineType.CollapsedContext,
                Text = DescribeHiddenLines(hidden.Count),
                HiddenLines = hidden,
                CollapseGroupId = groupId
            });
        }
    }
}
