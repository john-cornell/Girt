using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Girt.Models;
using Girt.ViewModels;

namespace Girt.Controls
{
    public partial class DiffViewerControl : UserControl
    {
        // Click-and-drag range selection state. _dragBaselineSelected is whatever was already
        // selected before this drag started (empty for a plain drag, the existing selection for
        // a Ctrl-drag) - every recompute during the drag is "baseline plus whatever's in the
        // live drag range", so moving the mouse back and forth correctly re-excludes lines that
        // fall back out of range without disturbing a prior, separate selection.
        private DiffLine? _dragAnchorLine;
        private HashSet<DiffLine> _dragBaselineSelected = new();
        private bool _isDragging;

        // Shift-click (no drag) extends from here to the newly clicked line - kept separate
        // from the drag anchor since it should survive across separate clicks/drags, not just
        // within one gesture.
        private DiffLine? _lastAnchorLine;

        public DiffViewerControl()
        {
            InitializeComponent();
        }

        // Driven by code-behind rather than a Command bound through the context menu's
        // RelativeSource, which proved unreliable for reaching a UserControl ancestor's
        // DataContext from inside a popup.
        private void OnToggleDiffSectionClicked(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { DataContext: DiffLine line })
            {
                (DataContext as IDiffLineHost)?.ToggleDiffSection(line);
            }
        }

        private void OnExpandAllDiffSectionsClicked(object sender, RoutedEventArgs e)
        {
            (DataContext as IDiffLineHost)?.ExpandAllDiffSections();
        }

        // Line-level revert selection only applies to WorkingChangesViewModel's unstaged diff
        // (see WorkingChangesViewModel.RevertSelectedLinesAsync) - other DataContexts (commit
        // history, unpushed review, merge-conflict Ours/Theirs previews) show read-only diffs
        // with nothing to revert into, so clicks/drags there are simply ignored. A drag can
        // start or pass over a Context/Header/CollapsedContext row without issue - only
        // Added/Deleted rows within the swept range ever actually get selected (see
        // ApplyRangeSelection), everything else is just ignored as the user drags over it.
        private void OnDiffLineMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border { DataContext: DiffLine line } border) return;
            if (DataContext is not WorkingChangesViewModel vm) return;

            var modifiers = Keyboard.Modifiers;

            if (modifiers.HasFlag(ModifierKeys.Shift) && _lastAnchorLine != null)
            {
                // One-shot range from the last anchor - not a drag, so no mouse capture.
                ApplyRangeSelection(vm.DiffLines, _lastAnchorLine, line, new HashSet<DiffLine>());
                e.Handled = true;
                return;
            }

            _dragBaselineSelected = modifiers.HasFlag(ModifierKeys.Control)
                ? vm.DiffLines.Where(l => l.IsSelected).ToHashSet()
                : new HashSet<DiffLine>();

            if (!modifiers.HasFlag(ModifierKeys.Control))
            {
                foreach (var l in vm.DiffLines) l.IsSelected = false;
            }

            _dragAnchorLine = line;
            _lastAnchorLine = line;
            _isDragging = true;
            ApplyRangeSelection(vm.DiffLines, line, line, _dragBaselineSelected);

            border.CaptureMouse();
            e.Handled = true;
        }

        private void OnDiffLineMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging || _dragAnchorLine == null) return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndDrag(sender as IInputElement);
                return;
            }

            if (DataContext is not WorkingChangesViewModel vm) return;

            // The mouse is captured on the row where the drag started, so `sender`'s own
            // DataContext never changes during the drag - hit-test against the whole
            // ItemsControl to find whichever row is actually under the cursor right now.
            var hitLine = FindDiffLineUnderCursor(e);
            if (hitLine == null) return;

            ApplyRangeSelection(vm.DiffLines, _dragAnchorLine, hitLine, _dragBaselineSelected);
        }

        private void OnDiffLineMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            EndDrag(sender as IInputElement);
        }

        private void EndDrag(IInputElement? capturedElement)
        {
            _isDragging = false;
            _dragAnchorLine = null;
            capturedElement?.ReleaseMouseCapture();
        }

        private DiffLine? FindDiffLineUnderCursor(MouseEventArgs e)
        {
            var position = e.GetPosition(DiffLinesItemsControl);
            var hit = VisualTreeHelper.HitTest(DiffLinesItemsControl, position)?.VisualHit;
            while (hit != null)
            {
                if (hit is FrameworkElement { DataContext: DiffLine line }) return line;
                hit = VisualTreeHelper.GetParent(hit);
            }
            return null;
        }

        // Sets IsSelected for every line to (already selected before this drag) OR (within the
        // [anchor,current] range AND actually revertable) - re-evaluated on every call so
        // dragging back and forth during a single gesture correctly adds and removes lines from
        // just this drag's own tentative range without touching a separate prior selection.
        private static void ApplyRangeSelection(IList<DiffLine> diffLines, DiffLine anchor, DiffLine current, HashSet<DiffLine> baseline)
        {
            var anchorIndex = diffLines.IndexOf(anchor);
            var currentIndex = diffLines.IndexOf(current);
            if (anchorIndex < 0 || currentIndex < 0) return;

            var (start, end) = anchorIndex <= currentIndex ? (anchorIndex, currentIndex) : (currentIndex, anchorIndex);

            for (var i = 0; i < diffLines.Count; i++)
            {
                var line = diffLines[i];
                var inDragRange = i >= start && i <= end && (line.Type == DiffLineType.Added || line.Type == DiffLineType.Deleted);
                line.IsSelected = baseline.Contains(line) || inDragRange;
            }
        }

        private void OnRevertSelectedLinesClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is WorkingChangesViewModel { CanRevertSelectedLines: true } vm)
            {
                vm.RevertSelectedLinesCommand.Execute(null);
            }
        }
    }
}
