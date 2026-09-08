using System.Linq;
using System.Text;
using System.Collections.Generic;
using Girt.Models;
using Girt.Services;
using Xunit;

namespace Girt.Tests
{
    public class DiffParserTests
    {
        private const string ModifiedLineDiff = @"diff --git a/test.txt b/test.txt
index 1234567..89abcdef 100644
--- a/test.txt
+++ b/test.txt
@@ -1,3 +1,4 @@
 line 1
-line 2
+line 2 modified
+line 2.5 new
 line 3";
        [Fact]
        public void ParseUnifiedDiff_ParsesAddedAndDeletedLinesCorrectly()
        {
            var rawDiff = @"diff --git a/test.txt b/test.txt
index 1234567..89abcdef 100644
--- a/test.txt
+++ b/test.txt
@@ -1,3 +1,4 @@
 line 1
-line 2
+line 2 modified
+line 2.5 new
 line 3";

            var lines = DiffParser.ParseUnifiedDiff(rawDiff);

            Assert.NotEmpty(lines);
            Assert.Contains(lines, l => l.Type == DiffLineType.Added && l.Text == "+line 2 modified" && l.NewLineNumber == 2);
            Assert.Contains(lines, l => l.Type == DiffLineType.Added && l.Text == "+line 2.5 new" && l.NewLineNumber == 3);
            Assert.Contains(lines, l => l.Type == DiffLineType.Deleted && l.Text == "-line 2" && l.OldLineNumber == 2);
            Assert.Contains(lines, l => l.Type == DiffLineType.Context && l.Text == " line 1" && l.OldLineNumber == 1 && l.NewLineNumber == 1);
        }

        [Fact]
        public void ParseUnifiedDiff_HandlesEmptyStringGracefully()
        {
            var lines = DiffParser.ParseUnifiedDiff("");
            Assert.Empty(lines);
        }

        [Fact]
        public void ReconstructFileContent_NoSelection_ReproducesCurrentWorkingTreeContent()
        {
            var lines = DiffParser.ParseUnifiedDiff(ModifiedLineDiff);

            var content = DiffParser.ReconstructFileContent(lines, "\n", endsWithNewline: false);

            Assert.Equal("line 1\nline 2 modified\nline 2.5 new\nline 3", content);
        }

        [Fact]
        public void ReconstructFileContent_AllChangedLinesSelected_RestoresOriginalContent()
        {
            var lines = DiffParser.ParseUnifiedDiff(ModifiedLineDiff);
            foreach (var line in lines.Where(l => l.Type == DiffLineType.Added || l.Type == DiffLineType.Deleted))
            {
                line.IsSelected = true;
            }

            var content = DiffParser.ReconstructFileContent(lines, "\n", endsWithNewline: false);

            Assert.Equal("line 1\nline 2\nline 3", content);
        }

        [Fact]
        public void ReconstructFileContent_OnlySelectedAddedLineIsDropped_UnselectedAdditionsAreKept()
        {
            var lines = DiffParser.ParseUnifiedDiff(ModifiedLineDiff);
            lines.Single(l => l.Type == DiffLineType.Added && l.Text == "+line 2.5 new").IsSelected = true;

            var content = DiffParser.ReconstructFileContent(lines, "\n", endsWithNewline: false);

            Assert.Equal("line 1\nline 2 modified\nline 3", content);
        }

        [Fact]
        public void ReconstructFileContent_OnlySelectedDeletedLineIsRestored_KeepingUnselectedAdditionsToo()
        {
            var lines = DiffParser.ParseUnifiedDiff(ModifiedLineDiff);
            lines.Single(l => l.Type == DiffLineType.Deleted).IsSelected = true;

            var content = DiffParser.ReconstructFileContent(lines, "\n", endsWithNewline: false);

            // Restoring just the deleted "line 2" while leaving both unselected additions in
            // place is an odd-looking but correct result of reverting only that one line.
            Assert.Equal("line 1\nline 2\nline 2 modified\nline 2.5 new\nline 3", content);
        }

        [Fact]
        public void ReconstructFileContent_EndsWithNewlineTrue_AppendsTrailingNewline()
        {
            var lines = DiffParser.ParseUnifiedDiff(ModifiedLineDiff);

            var content = DiffParser.ReconstructFileContent(lines, "\n", endsWithNewline: true);

            Assert.EndsWith("\n", content);
        }

        [Fact]
        public void ReconstructFileContent_UnwrapsCollapsedContextSections()
        {
            // Girt always requests full-file context, so any real file quickly hits the
            // collapse threshold - reconstruction must unwrap CollapsedContext placeholders
            // back into their real content instead of dropping or literally emitting the
            // "N unchanged lines" placeholder text.
            // CollapseThreshold is ContextEdgeLines*2+4 = 10, and only collapses a run STRICTLY
            // greater than that - 15 lines on each side of the change comfortably clears it.
            var contextLines = Enumerable.Range(1, 30).Select(i => $" context{i}").ToList();
            var sb = new StringBuilder();
            sb.AppendLine("diff --git a/test.txt b/test.txt");
            sb.AppendLine("index 1234567..89abcdef 100644");
            sb.AppendLine("--- a/test.txt");
            sb.AppendLine("+++ b/test.txt");
            sb.AppendLine("@@ -1,30 +1,31 @@");
            foreach (var l in contextLines.Take(15)) sb.AppendLine(l);
            sb.AppendLine("+inserted");
            foreach (var l in contextLines.Skip(15)) sb.AppendLine(l);

            // AppendLine leaves a trailing newline after the last content line, which would
            // parse as one extra (empty) context line - trim it so the fixture represents
            // exactly the intended 31 lines.
            var lines = DiffParser.ParseUnifiedDiff(sb.ToString().TrimEnd('\r', '\n'));
            Assert.Contains(lines, l => l.Type == DiffLineType.CollapsedContext);

            var content = DiffParser.ReconstructFileContent(lines, "\n", endsWithNewline: false);

            var expected = new List<string>();
            expected.AddRange(contextLines.Take(15).Select(l => l.Substring(1)));
            expected.Add("inserted");
            expected.AddRange(contextLines.Skip(15).Select(l => l.Substring(1)));
            Assert.Equal(string.Join("\n", expected), content);
        }
    }
}
