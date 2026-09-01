using System.Windows;
using System.Windows.Controls;
using Girt.Models;
using Girt.ViewModels;

namespace Girt.Controls
{
    public partial class DiffViewerControl : UserControl
    {
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
    }
}
