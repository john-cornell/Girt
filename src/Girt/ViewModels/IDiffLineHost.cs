using Girt.Models;

namespace Girt.ViewModels
{
    /// <summary>Implemented by view models that own a DiffLines collection, so
    /// DiffViewerControl's code-behind can drive expand/collapse without relying on
    /// RelativeSource-through-ContextMenu bindings to reach the right command.</summary>
    public interface IDiffLineHost
    {
        void ToggleDiffSection(DiffLine? line);
        void ExpandAllDiffSections();
    }
}
