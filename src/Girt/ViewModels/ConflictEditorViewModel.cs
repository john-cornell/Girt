using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Girt.Models;
using Girt.Services;

namespace Girt.ViewModels
{
    public enum ConflictEditorMode
    {
        Empty,
        Loading,
        TextMerge,
        Binary,
        OneSided,
        Error
    }

    /// <summary>The Mine | Result | Theirs editor for one conflicted file. Owned by
    /// MainViewModel, which decides which file is loaded and what happens once it's resolved.</summary>
    public partial class ConflictEditorViewModel : ObservableObject
    {
        private readonly IGitService _gitService;
        private readonly Func<string> _getRepoPath;
        private ConflictDocument? _document;

        // Picks made so far, per file, for this conflict session - switching to another file
        // and back keeps them. A half-resolved file can't be written to disk without markers.
        private readonly Dictionary<string, ConflictDocument> _documentsByPath = new(StringComparer.OrdinalIgnoreCase);

        // Replaced wholesale per file rather than Clear()+Add (AIREADME #2)
        [ObservableProperty]
        private IReadOnlyList<ConflictBlock> _blocks = Array.Empty<ConflictBlock>();

        [ObservableProperty]
        private MergeConflictFile? _file;

        [ObservableProperty]
        private ConflictEditorMode _mode = ConflictEditorMode.Empty;

        // Plain-words explanation for files the 3-pane view can't show
        [ObservableProperty]
        private string _message = string.Empty;

        [ObservableProperty]
        private int _unresolvedCount;

        [ObservableProperty]
        private bool _isDirty;

        [ObservableProperty]
        private ConflictBlock? _currentBlock;

        public bool IsTextMerge => Mode == ConflictEditorMode.TextMerge;
        public bool IsOneSided => Mode == ConflictEditorMode.OneSided;
        public bool CanDeleteFile => IsOneSided || Mode == ConflictEditorMode.Binary;
        public bool CanKeepFile => IsOneSided && File?.ConflictType != MergeConflictType.BothDeleted;
        public bool CanPickWholeFile => Mode is ConflictEditorMode.TextMerge or ConflictEditorMode.Binary;

        partial void OnModeChanged(ConflictEditorMode value)
        {
            OnPropertyChanged(nameof(IsTextMerge));
            OnPropertyChanged(nameof(IsOneSided));
            OnPropertyChanged(nameof(CanDeleteFile));
            OnPropertyChanged(nameof(CanKeepFile));
            OnPropertyChanged(nameof(CanPickWholeFile));
        }

        partial void OnCurrentBlockChanged(ConflictBlock? oldValue, ConflictBlock? newValue)
        {
            if (oldValue != null) oldValue.IsCurrent = false;
            if (newValue != null) newValue.IsCurrent = true;
        }

        public ConflictEditorViewModel(IGitService gitService, Func<string> getRepoPath)
        {
            _gitService = gitService;
            _getRepoPath = getRepoPath;
        }

        public void Clear()
        {
            DetachBlocks();
            _document = null;
            File = null;
            Mode = ConflictEditorMode.Empty;
            Message = string.Empty;
            UnresolvedCount = 0;
            IsDirty = false;
        }

        /// <summary>Loads a conflicted file. oneSidedMessage is set by the caller for
        /// delete/modify style conflicts, which have nothing to merge line by line.</summary>
        public async Task LoadAsync(MergeConflictFile file, bool mineIsTheirsStage, string? oneSidedMessage)
        {
            Clear();
            File = file;

            if (_documentsByPath.TryGetValue(file.Path, out var cached))
            {
                ShowDocument(cached, file);
                return;
            }

            if (file.IsResolved)
            {
                Mode = ConflictEditorMode.Error;
                Message = "Resolved. Pick another file, or Finish once they're all done.";
                return;
            }

            if (oneSidedMessage != null)
            {
                Mode = ConflictEditorMode.OneSided;
                Message = oneSidedMessage;
                return;
            }

            Mode = ConflictEditorMode.Loading;
            var result = await _gitService.GetConflictMergeAsync(_getRepoPath(), file.Path, mineIsTheirsStage);

            // The user may have clicked another file while this one was loading
            if (File != file) return;

            if (result.IsBinary)
            {
                Mode = ConflictEditorMode.Binary;
                Message = "This is a binary file, so it can't be merged line by line. Pick a whole side.";
                return;
            }
            if (result.Merged == null)
            {
                Mode = ConflictEditorMode.Error;
                Message = $"Couldn't load this file: {result.Error}";
                return;
            }

            var document = ConflictDocument.Parse(result.Merged);
            _documentsByPath[file.Path] = document;
            ShowDocument(document, file);
        }

        private void ShowDocument(ConflictDocument document, MergeConflictFile file)
        {
            _document = document;
            foreach (var block in document.Blocks) block.PropertyChanged += OnBlockPropertyChanged;
            Blocks = document.Blocks;
            UnresolvedCount = document.UnresolvedCount;
            file.UnresolvedCount = UnresolvedCount;
            Mode = ConflictEditorMode.TextMerge;
            CurrentBlock = document.Blocks.FirstOrDefault(b => b.IsConflict && !b.IsResolved);
        }

        /// <summary>Forgets every cached file - a new conflict session, or a file that was
        /// resolved some other way (whole-file pick, merge tool).</summary>
        public void ForgetAll() => _documentsByPath.Clear();

        public void Forget(string path) => _documentsByPath.Remove(path);

        private void DetachBlocks()
        {
            foreach (var block in Blocks) block.PropertyChanged -= OnBlockPropertyChanged;
            Blocks = Array.Empty<ConflictBlock>();
            CurrentBlock = null;
        }

        private void OnBlockPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ConflictBlock.ResultText) or nameof(ConflictBlock.IsResolved))
            {
                IsDirty = true;
            }
            if (e.PropertyName == nameof(ConflictBlock.IsResolved) && _document != null)
            {
                UnresolvedCount = _document.UnresolvedCount;
                if (File != null) File.UnresolvedCount = UnresolvedCount;
            }
        }

        /// <summary>Moves the highlight to the next (+1) or previous (-1) unresolved conflict
        /// in this file. False when there isn't one that way.</summary>
        public bool MoveToConflict(int direction)
        {
            var conflicts = Blocks.Where(b => b.IsConflict && !b.IsResolved).ToList();
            if (conflicts.Count == 0) return false;

            var currentIndex = CurrentBlock == null ? -1 : IndexOf(CurrentBlock);
            var next = direction > 0
                ? conflicts.FirstOrDefault(b => IndexOf(b) > currentIndex)
                : conflicts.LastOrDefault(b => currentIndex < 0 || IndexOf(b) < currentIndex);
            if (next == null) return false;

            CurrentBlock = next;
            return true;
        }

        public void SelectFirstOrLastConflict(bool last)
        {
            var conflicts = Blocks.Where(b => b.IsConflict && !b.IsResolved);
            CurrentBlock = last ? conflicts.LastOrDefault() : conflicts.FirstOrDefault();
        }

        private int IndexOf(ConflictBlock block)
        {
            for (var i = 0; i < Blocks.Count; i++)
            {
                if (ReferenceEquals(Blocks[i], block)) return i;
            }
            return -1;
        }

        /// <summary>Writes the result to disk. Once nothing is left unresolved it's staged too,
        /// which is what marks the file resolved. Returns an error message, or null.</summary>
        public async Task<string?> SaveAsync()
        {
            var file = File;
            if (_document == null || file == null || !IsDirty) return null;

            // Partly resolved: nothing to write yet. The picks so far stay in the cache.
            if (_document.UnresolvedCount > 0) return null;

            if (_document.FindLeftoverMarkerLine() is { } markerLine)
            {
                return $"Line {markerLine} still looks like a conflict marker (<<<<<<< or >>>>>>>). Fix it before saving.";
            }

            var repoPath = _getRepoPath();
            var (written, writeOutput) = await _gitService.WriteWorkingTreeFileBytesAsync(repoPath, file.Path, _document.Compose());
            if (!written) return $"Couldn't save {file.Path}: {writeOutput}";

            var (staged, stageOutput) = await _gitService.StageFileAsync(repoPath, file.Path);
            if (!staged) return $"Saved {file.Path}, but couldn't stage it: {stageOutput}";

            IsDirty = false;
            file.IsResolved = true;
            return null;
        }
    }
}
