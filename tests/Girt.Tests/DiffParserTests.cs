using Girt.Models;
using Girt.Services;
using Xunit;

namespace Girt.Tests
{
    public class DiffParserTests
    {
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
    }
}
