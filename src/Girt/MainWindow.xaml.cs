using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Girt.Models;
using Girt.Services;
using Girt.ViewModels;
using Forms = System.Windows.Forms;

namespace Girt
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly ThemeService _themeService;
        private Forms.NotifyIcon? _trayIcon;
        private Forms.ToolStripMenuItem? _minimizeToTrayMenuItem;
        private Forms.ToolStripMenuItem? _minimizeOnCloseMenuItem;
        private bool _isExiting;

        public MainWindow()
        {
            InitializeComponent();

            var gitService = new GitCliService();
            var recentService = new RecentRepositoriesService();
            _themeService = new ThemeService();

            // Apply saved user theme preference (persisted in %APPDATA%\Girt\settings.json)
            _themeService.ApplyTheme(_themeService.CurrentTheme);

            _viewModel = new MainViewModel(gitService, recentService, _themeService);
            DataContext = _viewModel;

            // MinimizeToTray/MinimizeOnClose live on _viewModel.Settings (shared with the
            // Settings dialog) - mirror changes into the tray menu's checkmarks either way.
            _viewModel.Settings.PropertyChanged += OnSettingsPropertyChanged;

            // SelectedCommit's binding to ListView.SelectedItem highlights the row but a
            // virtualized ListView never auto-scrolls to a selection made in code (only to one
            // made by clicking) - without this, selecting a branch far down a long history
            // updates SelectedCommit but the graph visibly doesn't move.
            _viewModel.CommitHistory.PropertyChanged += OnCommitHistoryPropertyChanged;

            InitializeTrayIcon();
            StateChanged += OnWindowStateChanged;
            Closing += OnWindowClosing;

            Loaded += OnWindowLoaded;
        }

        // The tray icon is created once at startup and stays visible for the life of the
        // process, not just while minimized - the point is Girt is always reachable from the
        // notification area, not only when the window happens to be hidden. Whether minimizing/
        // closing actually uses the tray is controlled by the two checkable options below.
        private void InitializeTrayIcon()
        {
            var exePath = Environment.ProcessPath;
            var icon = !string.IsNullOrEmpty(exePath)
                ? System.Drawing.Icon.ExtractAssociatedIcon(exePath)
                : null;

            _trayIcon = new Forms.NotifyIcon
            {
                Icon = icon ?? System.Drawing.SystemIcons.Application,
                Text = "Girt",
                Visible = true
            };
            _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

            _minimizeToTrayMenuItem = new Forms.ToolStripMenuItem("Minimize to Tray", null, OnToggleMinimizeToTray) { Checked = _viewModel.Settings.MinimizeToTray };
            _minimizeOnCloseMenuItem = new Forms.ToolStripMenuItem("Minimize on Close", null, OnToggleMinimizeOnClose) { Checked = _viewModel.Settings.MinimizeOnClose };

            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open Girt", null, (_, _) => RestoreFromTray());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(_minimizeToTrayMenuItem);
            menu.Items.Add(_minimizeOnCloseMenuItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => ExitApplication());
            _trayIcon.ContextMenuStrip = menu;
        }

        private void OnToggleMinimizeToTray(object? sender, EventArgs e)
        {
            _viewModel.Settings.MinimizeToTray = !_viewModel.Settings.MinimizeToTray;
        }

        private void OnToggleMinimizeOnClose(object? sender, EventArgs e)
        {
            _viewModel.Settings.MinimizeOnClose = !_viewModel.Settings.MinimizeOnClose;
        }

        private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(SettingsViewModel.MinimizeToTray):
                    if (_minimizeToTrayMenuItem != null) _minimizeToTrayMenuItem.Checked = _viewModel.Settings.MinimizeToTray;
                    break;
                case nameof(SettingsViewModel.MinimizeOnClose):
                    if (_minimizeOnCloseMenuItem != null) _minimizeOnCloseMenuItem.Checked = _viewModel.Settings.MinimizeOnClose;
                    break;
            }
        }

        private void OnCommitHistoryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CommitHistoryViewModel.SelectedCommit) &&
                _viewModel.CommitHistory.SelectedCommit is { } commit)
            {
                CommitsListView.ScrollIntoView(commit);
            }
        }

        // Internal (not private) so App.xaml.cs can call it as the single-instance activation
        // handler - a second launch's "please come to foreground" request uses the exact same
        // restore logic as the tray icon's own double-click/"Open Girt".
        internal void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void ExitApplication()
        {
            _isExiting = true;
            _trayIcon?.Dispose();
            _trayIcon = null;
            Application.Current.Shutdown();
        }

        // Only drops to the tray on minimize when that option is checked; otherwise this is a
        // no-op and the window minimizes to the taskbar normally.
        private void OnWindowStateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized && _viewModel.Settings.MinimizeToTray)
            {
                Hide();
            }
        }

        // Only intercepts the close (X button) into a tray-hide when that option is checked;
        // otherwise the window closes and the app exits normally.
        private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_isExiting || !_viewModel.Settings.MinimizeOnClose) return;

            e.Cancel = true;
            Hide();
        }

        private async void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            // Try to auto-open current directory or most recent repo
            var currentDir = Directory.GetCurrentDirectory();
            if (Directory.Exists(Path.Combine(currentDir, ".git")))
            {
                await _viewModel.OpenRepositoryAsync(currentDir);
            }
            else if (_viewModel.RecentRepositories.Count > 0)
            {
                await _viewModel.OpenRepositoryAsync(_viewModel.RecentRepositories[0]);
            }
        }

        private async void OnRecentRepoSelected(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox combo && combo.SelectedItem is string path && !string.IsNullOrEmpty(path))
            {
                if (path != _viewModel.RepositoryPath)
                {
                    await _viewModel.OpenRepositoryAsync(path);
                }
            }
        }

        private async void OnLocalBranchDoubleClicked(object sender, MouseButtonEventArgs e)
        {
            // Handles both the flat branch lists (ListBox of GitBranch items) and the
            // folder-grouped tree (TreeView of BranchTreeItem nodes) - double-clicking a folder
            // header is a no-op, not a checkout.
            object? selected = sender switch
            {
                ListBox listBox => listBox.SelectedItem,
                TreeView treeView => treeView.SelectedItem,
                _ => null
            };

            var branch = selected switch
            {
                GitBranch b => b,
                BranchTreeItem { IsFolder: false } item => item.Branch,
                _ => null
            };

            if (branch != null)
            {
                await _viewModel.BranchList.CheckoutBranchAsync(branch);
            }
        }

        // Driven by code-behind rather than a Command bound through the context menu's
        // RelativeSource, which proved unreliable for reaching this command from inside a popup.
        private void OnToggleGroupBranchesIntoFoldersClicked(object sender, RoutedEventArgs e)
        {
            _viewModel.BranchList.ToggleGroupBranchesIntoFolders();
        }

        // Same rationale as above: reaching BranchList.TogglePinBranchCommand through the
        // context menu's RelativeSource binding was unreliable, so this resolves the branch
        // directly off the clicked MenuItem's DataContext instead. Folder rows have no Branch,
        // so pinning a folder is structurally impossible here, not just hidden via Visibility.
        private void OnTogglePinBranchClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem) return;

            var branch = menuItem.DataContext switch
            {
                GitBranch b => b,
                BranchTreeItem { IsFolder: false } item => item.Branch,
                _ => null
            };

            if (branch != null)
            {
                _viewModel.BranchList.TogglePinBranchCommand.Execute(branch);
            }
        }

        // Same rationale as OnTogglePinBranchClicked - Command+RelativeSource to reach
        // DeleteBranchCommand through the branch tree's ContextMenu proved unreliable there
        // (reported as "still nothing happens" even after the tree TreeViewItem template
        // rewrite), so resolve the branch directly off the clicked MenuItem's DataContext.
        private void OnDeleteBranchClicked(object sender, RoutedEventArgs e)
        {
            if (ResolveBranchFromMenuItem(sender) is { } branch)
            {
                _viewModel.BranchList.DeleteBranchCommand.Execute(branch);
            }
        }

        // Same rationale as OnDeleteBranchClicked - these are nested submenu items (an extra
        // Popup layer on top of the branch tree's own ContextMenu Popup), so if anything was
        // going to hit the Command+RelativeSource-through-Popup unreliability documented in
        // AIREADME.md #12, a doubly-nested Popup was always the most likely candidate. Confirmed
        // broken in practice ("This doing nothing" clicking Merge (Default) from the branch
        // tree) - resolve the branch directly off the clicked MenuItem's DataContext instead.
        private void OnMergeDefaultClicked(object sender, RoutedEventArgs e)
        {
            if (ResolveBranchFromMenuItem(sender) is { } branch)
            {
                _viewModel.MergeIntoCurrentBranchCommand.Execute(branch);
            }
        }

        private void OnMergeSquashClicked(object sender, RoutedEventArgs e)
        {
            if (ResolveBranchFromMenuItem(sender) is { } branch)
            {
                _viewModel.MergeSquashIntoCurrentBranchCommand.Execute(branch);
            }
        }

        private void OnMergeNoFfClicked(object sender, RoutedEventArgs e)
        {
            if (ResolveBranchFromMenuItem(sender) is { } branch)
            {
                _viewModel.MergeNoFfIntoCurrentBranchCommand.Execute(branch);
            }
        }

        private static GitBranch? ResolveBranchFromMenuItem(object sender) =>
            sender is MenuItem menuItem
                ? menuItem.DataContext switch
                {
                    GitBranch b => b,
                    BranchTreeItem { IsFolder: false } item => item.Branch,
                    _ => null
                }
                : null;

        // Multi-select bulk actions for the Unstaged/Staged file lists (matching GitExtensions'
        // "Stage selected"/"Delete files" on a multi-selection) - the ListBox itself carries the
        // live selection (SelectionMode="Extended" in XAML), so read it directly off its
        // ContextMenu.PlacementTarget rather than needing a bound SelectedItems collection
        // (ListBox.SelectedItems isn't a dependency property and can't be bound two-way).
        private void OnStageSelectedFilesClicked(object sender, RoutedEventArgs e)
        {
            if (ResolveSelectedWorkingFiles(sender) is { } files)
            {
                _viewModel.WorkingChanges.StageSelectedFilesCommand.Execute(files);
            }
        }

        private void OnUnstageSelectedFilesClicked(object sender, RoutedEventArgs e)
        {
            if (ResolveSelectedWorkingFiles(sender) is { } files)
            {
                _viewModel.WorkingChanges.UnstageSelectedFilesCommand.Execute(files);
            }
        }

        private void OnDiscardSelectedFilesClicked(object sender, RoutedEventArgs e)
        {
            if (ResolveSelectedWorkingFiles(sender) is { } files)
            {
                _viewModel.WorkingChanges.DiscardSelectedFilesCommand.Execute(files);
            }
        }

        private static List<GitWorkingFile>? ResolveSelectedWorkingFiles(object sender)
        {
            if (sender is not MenuItem { Parent: ContextMenu { PlacementTarget: ListBox listBox } }) return null;
            var files = listBox.SelectedItems.Cast<GitWorkingFile>().ToList();
            return files.Count > 0 ? files : null;
        }

        private void OnClearIsolationClicked(object sender, RoutedEventArgs e)
        {
            _viewModel.CommitHistory.ClearIsolation();
        }

        // Same rationale as OnTogglePinBranchClicked: these MenuItems are generated via
        // ItemsSource inside a submenu popup, so their DataContext is the folder path string
        // itself (not the panel's ViewModel) - resolve it directly rather than trying to bind
        // Command back out through the popup boundary.
        private void OnIgnoreFolderInWorkingChangesClicked(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { DataContext: string folderPath })
            {
                _viewModel.WorkingChanges.IgnoreFolderCommand.Execute(folderPath);
            }
        }

        private void OnIgnoreFolderInCommitDetailClicked(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { DataContext: string folderPath })
            {
                _viewModel.CommitDetail.IgnoreFolderCommand.Execute(folderPath);
            }
        }

        // These per-row branch ContextMenus sit inside virtualized ListBoxes, where Visibility
        // bindings that reach back through RelativeSource=Window have proven unreliable (same
        // class of issue as the Command-binding fixes above) - so state-dependent show/hide
        // (folder-vs-flat, dim/hide-vs-already-isolated) is resolved here instead, read fresh
        // every time the menu opens.
        private void OnBranchContextMenuOpened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu menu) return;

            var isFolderRow = menu.DataContext is BranchTreeItem { IsFolder: true };
            var isFolderView = _viewModel.BranchList.GroupBranchesIntoFolders;
            var isIsolated = _viewModel.CommitHistory.IsBranchIsolated;

            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                switch (item.Name)
                {
                    case "GroupIntoFoldersMenuItem":
                        item.Visibility = isFolderView ? Visibility.Collapsed : Visibility.Visible;
                        break;
                    case "ShowAsFlatListMenuItem":
                        item.Visibility = isFolderView ? Visibility.Visible : Visibility.Collapsed;
                        break;
                    case "PinMenuItem":
                        item.Visibility = isFolderRow ? Visibility.Collapsed : Visibility.Visible;
                        break;
                    case "DimToForkMenuItem":
                    case "HideToForkMenuItem":
                        item.Visibility = !isFolderRow && !isIsolated ? Visibility.Visible : Visibility.Collapsed;
                        break;
                    case "ShowAllMenuItem":
                        item.Visibility = !isFolderRow && isIsolated ? Visibility.Visible : Visibility.Collapsed;
                        break;
                }
            }
        }

        // These branch ListBoxes sit inside an outer ScrollViewer but rarely overflow
        // themselves; their own (unused) internal ScrollViewer still swallows the wheel
        // event before it can bubble up, so forward it to the nearest scrollable ancestor.
        private void OnBranchListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || sender is not DependencyObject source) return;

            var scrollViewer = FindAncestorScrollViewer(source);
            if (scrollViewer == null) return;

            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        }

        private static ScrollViewer? FindAncestorScrollViewer(DependencyObject element)
        {
            var parent = VisualTreeHelper.GetParent(element);
            while (parent != null && parent is not ScrollViewer)
            {
                parent = VisualTreeHelper.GetParent(parent);
            }
            return parent as ScrollViewer;
        }

        // Settings > "Expand folders on" picks whether this fires on the first click or
        // requires a second - MouseButtonEventArgs.ClickCount reports both from one event, so
        // there's no need for separate Click/MouseDoubleClick wiring per mode.
        private void OnBranchFolderRowMouseDown(object sender, MouseButtonEventArgs e)
        {
            var requiredClicks = _viewModel.Settings.FolderExpandOnSingleClick ? 1 : 2;
            if (e.ClickCount != requiredClicks) return;
            if (sender is not DependencyObject source) return;

            var treeViewItem = FindAncestorTreeViewItem(source);
            if (treeViewItem == null) return;

            treeViewItem.IsExpanded = !treeViewItem.IsExpanded;
            e.Handled = true;
        }

        private static TreeViewItem? FindAncestorTreeViewItem(DependencyObject element)
        {
            var parent = VisualTreeHelper.GetParent(element);
            while (parent != null && parent is not TreeViewItem)
            {
                parent = VisualTreeHelper.GetParent(parent);
            }
            return parent as TreeViewItem;
        }

        // One-click collapse/expand for the split panels (branch sidebar, commit list/detail,
        // changed-files/diff, working-changes left panel, unstaged/staged) - now that dragging
        // has no min/max limits, a full collapse via drag means hunting for a 0px-wide splitter.
        // Keyed by the DefinitionBase instance itself so multiple panels can share one dictionary.
        private readonly Dictionary<DefinitionBase, GridLength> _collapsedPanelSizes = new();

        private void ToggleColumnCollapse(ColumnDefinition column, Button toggleButton, string expandedGlyph, string collapsedGlyph)
        {
            if (_collapsedPanelSizes.TryGetValue(column, out var saved))
            {
                column.Width = saved;
                _collapsedPanelSizes.Remove(column);
                toggleButton.Content = expandedGlyph;
            }
            else
            {
                _collapsedPanelSizes[column] = column.Width;
                column.Width = new GridLength(0);
                toggleButton.Content = collapsedGlyph;
            }
        }

        private void ToggleRowCollapse(RowDefinition row, Button toggleButton, string expandedGlyph, string collapsedGlyph)
        {
            if (_collapsedPanelSizes.TryGetValue(row, out var saved))
            {
                row.Height = saved;
                _collapsedPanelSizes.Remove(row);
                toggleButton.Content = expandedGlyph;
            }
            else
            {
                _collapsedPanelSizes[row] = row.Height;
                row.Height = new GridLength(0);
                toggleButton.Content = collapsedGlyph;
            }
        }

        private void OnToggleBranchSidebarClicked(object sender, RoutedEventArgs e) =>
            ToggleColumnCollapse(BranchSidebarColumn, (Button)sender, "◂", "▸");

        private void OnToggleCommitListClicked(object sender, RoutedEventArgs e) =>
            ToggleRowCollapse(CommitListRow, (Button)sender, "▴", "▾");

        private void OnToggleChangedFilesClicked(object sender, RoutedEventArgs e) =>
            ToggleColumnCollapse(ChangedFilesColumn, (Button)sender, "◂", "▸");

        private void OnToggleWorkingChangesLeftClicked(object sender, RoutedEventArgs e) =>
            ToggleColumnCollapse(WorkingChangesLeftColumn, (Button)sender, "◂", "▸");

        private void OnToggleStagedFilesClicked(object sender, RoutedEventArgs e) =>
            ToggleRowCollapse(StagedFilesRow, (Button)sender, "▾", "▴");
    }
}