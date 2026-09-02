using System;
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
        private bool _minimizeToTray;
        private bool _minimizeOnClose;

        public MainWindow()
        {
            InitializeComponent();

            var gitService = new GitCliService();
            var recentService = new RecentRepositoriesService();
            _themeService = new ThemeService();

            // Apply saved user theme preference (persisted in %APPDATA%\Girt\settings.json)
            _themeService.ApplyTheme(_themeService.CurrentTheme);
            _minimizeToTray = _themeService.LoadMinimizeToTray();
            _minimizeOnClose = _themeService.LoadMinimizeOnClose();

            _viewModel = new MainViewModel(gitService, recentService, _themeService);
            DataContext = _viewModel;

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

            _minimizeToTrayMenuItem = new Forms.ToolStripMenuItem("Minimize to Tray", null, OnToggleMinimizeToTray) { Checked = _minimizeToTray };
            _minimizeOnCloseMenuItem = new Forms.ToolStripMenuItem("Minimize on Close", null, OnToggleMinimizeOnClose) { Checked = _minimizeOnClose };

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
            _minimizeToTray = !_minimizeToTray;
            _minimizeToTrayMenuItem!.Checked = _minimizeToTray;
            _themeService.SaveMinimizeToTray(_minimizeToTray);
        }

        private void OnToggleMinimizeOnClose(object? sender, EventArgs e)
        {
            _minimizeOnClose = !_minimizeOnClose;
            _minimizeOnCloseMenuItem!.Checked = _minimizeOnClose;
            _themeService.SaveMinimizeOnClose(_minimizeOnClose);
        }

        private void RestoreFromTray()
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
            if (WindowState == WindowState.Minimized && _minimizeToTray)
            {
                Hide();
            }
        }

        // Only intercepts the close (X button) into a tray-hide when that option is checked;
        // otherwise the window closes and the app exits normally.
        private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_isExiting || !_minimizeOnClose) return;

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
            if (sender is not ListBox listBox) return;

            // Handles both the flat branch lists (GitBranch items) and the folder-grouped tree
            // (BranchTreeItem rows) - double-clicking a folder header is a no-op, not a checkout.
            var branch = listBox.SelectedItem switch
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

        private void OnClearIsolationClicked(object sender, RoutedEventArgs e)
        {
            _viewModel.CommitHistory.ClearIsolation();
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
    }
}