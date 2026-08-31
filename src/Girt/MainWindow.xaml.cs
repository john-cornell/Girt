using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Girt.Models;
using Girt.Services;
using Girt.ViewModels;

namespace Girt
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;

        public MainWindow()
        {
            InitializeComponent();

            var gitService = new GitCliService();
            var recentService = new RecentRepositoriesService();
            var themeService = new ThemeService();

            // Apply saved user theme preference (persisted in %APPDATA%\Girt\settings.json)
            themeService.ApplyTheme(themeService.CurrentTheme);

            _viewModel = new MainViewModel(gitService, recentService, themeService);
            DataContext = _viewModel;

            Loaded += OnWindowLoaded;
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
            if (sender is ListBox listBox && listBox.SelectedItem is GitBranch branch)
            {
                await _viewModel.BranchList.CheckoutBranchAsync(branch);
            }
        }
    }
}