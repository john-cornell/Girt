using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Girt.Models
{
    /// <summary>What `git merge-file` made of a conflicted file. Merged is null when the file
    /// is binary or git failed (Error says why).</summary>
    public class ConflictMergeResult
    {
        public bool IsBinary { get; init; }
        public byte[]? Merged { get; init; }
        public string? Error { get; init; }
    }

    /// <summary>One row of the merge editor: either lines git merged on its own (Common), or a
    /// conflict with mine/base/theirs versions and the result the user picked or typed.
    /// Texts are stored without their final line ending (that's in the *Eol fields), so a
    /// TextBox doesn't show a phantom empty last line.</summary>
    public partial class ConflictBlock : ObservableObject
    {
        public bool IsConflict { get; init; }
        public string MineText { get; init; } = string.Empty;
        public string BaseText { get; init; } = string.Empty;
        public string TheirsText { get; init; } = string.Empty;
        public string MineEol { get; init; } = string.Empty;
        public string TheirsEol { get; init; } = string.Empty;
        public bool MineIsEmpty { get; init; }
        public bool TheirsIsEmpty { get; init; }

        // The file's usual line ending, for anything the user types or joins.
        internal string DocumentEol { get; init; } = "\n";

        [ObservableProperty]
        private string _resultText = string.Empty;

        [ObservableProperty]
        private bool _isResolved;

        // Highlighted as the F8 target
        [ObservableProperty]
        private bool _isCurrent;

        public string ResultEol { get; private set; } = string.Empty;

        // A side that had no lines at all (the other side deleted them) contributes nothing,
        // not an empty line.
        public bool ResultIsEmpty { get; private set; }

        // Typed by the user rather than picked - its line endings get normalised on save.
        public bool WasTyped { get; private set; }

        private bool _settingProgrammatically;

        internal static ConflictBlock Common(string text, string eol, string documentEol)
        {
            var block = new ConflictBlock
            {
                MineText = text,
                TheirsText = text,
                MineEol = eol,
                TheirsEol = eol,
                DocumentEol = documentEol
            };
            block.SetResult(text, eol, isEmpty: false);
            block.IsResolved = true;
            return block;
        }

        partial void OnResultTextChanged(string value)
        {
            if (_settingProgrammatically) return;

            // Typing into a conflict counts as resolving it with whatever was typed
            WasTyped = true;
            ResultIsEmpty = false;
            if (IsConflict && string.IsNullOrEmpty(ResultEol)) ResultEol = DocumentEol;
            IsResolved = true;
        }

        private void SetResult(string text, string eol, bool isEmpty)
        {
            _settingProgrammatically = true;
            try
            {
                ResultText = text;
            }
            finally
            {
                _settingProgrammatically = false;
            }
            ResultEol = eol;
            ResultIsEmpty = isEmpty;
            WasTyped = false;
        }

        [RelayCommand]
        public void UseMine()
        {
            SetResult(MineText, MineEol, MineIsEmpty);
            IsResolved = true;
        }

        [RelayCommand]
        public void UseTheirs()
        {
            SetResult(TheirsText, TheirsEol, TheirsIsEmpty);
            IsResolved = true;
        }

        [RelayCommand]
        public void UseMineThenTheirs() => UseBoth(mineFirst: true);

        [RelayCommand]
        public void UseTheirsThenMine() => UseBoth(mineFirst: false);

        private void UseBoth(bool mineFirst)
        {
            var (firstText, firstEol, firstEmpty) = mineFirst ? (MineText, MineEol, MineIsEmpty) : (TheirsText, TheirsEol, TheirsIsEmpty);
            var (secondText, secondEol, secondEmpty) = mineFirst ? (TheirsText, TheirsEol, TheirsIsEmpty) : (MineText, MineEol, MineIsEmpty);

            if (firstEmpty) SetResult(secondText, secondEol, secondEmpty);
            else if (secondEmpty) SetResult(firstText, firstEol, firstEmpty);
            else SetResult(firstText + (firstEol.Length > 0 ? firstEol : DocumentEol) + secondText, secondEol, isEmpty: false);
            IsResolved = true;
        }

        [RelayCommand]
        public void ResetChoice()
        {
            if (!IsConflict) return;
            SetResult(string.Empty, string.Empty, isEmpty: false);
            IsResolved = false;
        }

        // What this block puts into the saved file
        internal string ComposedText()
        {
            if (ResultIsEmpty) return string.Empty;
            if (!WasTyped) return ResultText + ResultEol;

            // WPF's TextBox types \r\n, so bring typed text in line with the rest of the file.
            // Clearing a conflict completely means "nothing here".
            if (IsConflict && ResultText.Length == 0) return string.Empty;
            var normalised = ResultText.Replace("\r\n", "\n").Replace("\n", DocumentEol);
            return normalised + ResultEol;
        }
    }

    /// <summary>A conflicted file parsed from `git merge-file -p --diff3` output into blocks,
    /// and written back with its original encoding and BOM.</summary>
    public class ConflictDocument
    {
        public const int MarkerSize = 32;

        // Common runs are split so the editor's list can virtualise a big file
        private const int MaxCommonBlockLines = 200;

        private static readonly string StartMarker = new('<', MarkerSize);
        private static readonly string BaseMarker = new('|', MarkerSize);
        private static readonly string SeparatorMarker = new('=', MarkerSize);
        private static readonly string EndMarker = new('>', MarkerSize);

        private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

        public IReadOnlyList<ConflictBlock> Blocks { get; }
        public string Eol { get; }
        private readonly Encoding _encoding;
        private readonly bool _hasBom;

        private ConflictDocument(IReadOnlyList<ConflictBlock> blocks, string eol, Encoding encoding, bool hasBom)
        {
            Blocks = blocks;
            Eol = eol;
            _encoding = encoding;
            _hasBom = hasBom;
        }

        public int UnresolvedCount => Blocks.Count(b => b.IsConflict && !b.IsResolved);

        public static ConflictDocument Parse(byte[] merged)
        {
            var hasBom = merged.Length >= 3 && merged[0] == Utf8Bom[0] && merged[1] == Utf8Bom[1] && merged[2] == Utf8Bom[2];
            var body = hasBom ? merged.AsSpan(3) : merged.AsSpan();

            // Strict UTF-8 first. Anything else gets Latin-1, which maps every byte to a char
            // and back, so the parts nobody touched are written back byte for byte.
            Encoding encoding;
            string text;
            try
            {
                encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                text = encoding.GetString(body);
            }
            catch (DecoderFallbackException)
            {
                encoding = Encoding.Latin1;
                text = encoding.GetString(body);
            }

            var lines = SplitKeepingEols(text);
            var eol = DominantEol(lines);
            var blocks = new List<ConflictBlock>();
            var common = new List<string>();
            List<string>? mine = null, @base = null, theirs = null;
            var section = 0; // 0 common, 1 mine, 2 base, 3 theirs

            void FlushCommon()
            {
                for (var i = 0; i < common.Count; i += MaxCommonBlockLines)
                {
                    var (blockText, blockEol) = JoinLines(common.Skip(i).Take(MaxCommonBlockLines));
                    blocks.Add(ConflictBlock.Common(blockText, blockEol, eol));
                }
                common.Clear();
            }

            foreach (var line in lines)
            {
                var content = line.TrimEnd('\r', '\n');
                if (section == 0 && IsMarker(content, StartMarker))
                {
                    FlushCommon();
                    mine = new(); @base = new(); theirs = new();
                    section = 1;
                }
                else if (section == 1 && IsMarker(content, BaseMarker)) section = 2;
                else if (section is 1 or 2 && content == SeparatorMarker) section = 3;
                else if (section == 3 && IsMarker(content, EndMarker))
                {
                    var (mineText, mineEol) = JoinLines(mine!);
                    var (baseText, _) = JoinLines(@base!);
                    var (theirsText, theirsEol) = JoinLines(theirs!);
                    blocks.Add(new ConflictBlock
                    {
                        IsConflict = true,
                        MineText = mineText,
                        BaseText = baseText,
                        TheirsText = theirsText,
                        MineEol = mineEol,
                        TheirsEol = theirsEol,
                        MineIsEmpty = mine!.Count == 0,
                        TheirsIsEmpty = theirs!.Count == 0,
                        DocumentEol = eol
                    });
                    section = 0;
                }
                else
                {
                    (section switch { 1 => mine!, 2 => @base!, 3 => theirs!, _ => common }).Add(line);
                }
            }

            // Unterminated conflict (shouldn't happen with merge-file output) - keep the raw
            // lines rather than drop anything.
            if (section != 0)
            {
                common.AddRange(mine!);
                common.AddRange(@base!);
                common.AddRange(theirs!);
            }
            FlushCommon();

            return new ConflictDocument(blocks, eol, encoding, hasBom);
        }

        public byte[] Compose()
        {
            if (UnresolvedCount > 0) throw new InvalidOperationException("The file still has unresolved conflicts.");

            var text = string.Concat(Blocks.Select(b => b.ComposedText()));
            var body = _encoding.GetBytes(text);
            return _hasBom ? Utf8Bom.Concat(body).ToArray() : body;
        }

        /// <summary>1-based line number of the first conflict-marker-looking line in a
        /// conflict the user resolved or text they typed, or null if there isn't one. Lines
        /// git merged on its own are left alone - a file can legitimately contain them.</summary>
        public int? FindLeftoverMarkerLine()
        {
            var lineNumber = 1;
            foreach (var block in Blocks)
            {
                var composed = block.ComposedText();
                var checkThisBlock = block.IsConflict || block.WasTyped;
                foreach (var line in SplitKeepingEols(composed))
                {
                    if (checkThisBlock && (line.StartsWith("<<<<<<<", StringComparison.Ordinal) || line.StartsWith(">>>>>>>", StringComparison.Ordinal)))
                    {
                        return lineNumber;
                    }
                    lineNumber++;
                }
            }
            return null;
        }

        private static bool IsMarker(string content, string marker) =>
            content.StartsWith(marker, StringComparison.Ordinal) &&
            (content.Length == marker.Length || content[marker.Length] == ' ');

        private static List<string> SplitKeepingEols(string text)
        {
            var lines = new List<string>();
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                lines.Add(text[start..(i + 1)]);
                start = i + 1;
            }
            if (start < text.Length) lines.Add(text[start..]);
            return lines;
        }

        private static (string Text, string Eol) JoinLines(IEnumerable<string> lines)
        {
            var joined = string.Concat(lines);
            var eol = joined.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : joined.EndsWith('\n') ? "\n" : string.Empty;
            return (joined[..^eol.Length], eol);
        }

        private static string DominantEol(List<string> lines)
        {
            var crlf = lines.Count(l => l.EndsWith("\r\n", StringComparison.Ordinal));
            var lf = lines.Count(l => l.EndsWith('\n')) - crlf;
            return crlf > lf ? "\r\n" : "\n";
        }
    }
}
