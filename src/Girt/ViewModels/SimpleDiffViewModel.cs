using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Girt.Models;
using Girt.Services;

namespace Girt.ViewModels
{
    /// <summary>Minimal read-only diff host for previews that don't need a changed-files list
    /// of their own - e.g. the Ours/Theirs panes in the merge-conflict dialog - reusing
    /// DiffViewerControl's IDiffLineHost contract without dragging in everything
    /// CommitDetailViewModel carries for file navigation.</summary>
    public partial class SimpleDiffViewModel : ObservableObject, IDiffLineHost
    {
        public ObservableCollection<DiffLine> DiffLines { get; } = new();

        public void SetDiff(string rawUnifiedDiff)
        {
            DiffLines.Clear();
            foreach (var line in DiffParser.ParseUnifiedDiff(rawUnifiedDiff))
            {
                DiffLines.Add(line);
            }
        }

        public void ToggleDiffSection(DiffLine? line) => DiffParser.ToggleCollapsedSection(DiffLines, line);
        public void ExpandAllDiffSections() => DiffParser.ExpandAllCollapsedSections(DiffLines);
    }
}
