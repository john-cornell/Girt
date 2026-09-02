using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Girt.Services;

namespace Girt.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly IGitService _gitService;
        private readonly Func<string> _getRepoPath;
        private readonly Action<bool> _saveMinimizeToTray;
        private readonly Action<bool> _saveMinimizeOnClose;
        private readonly Action<bool> _saveFolderExpandOnSingleClick;

        // MinimizeToTray/MinimizeOnClose are the single source of truth for both this Settings
        // page and the tray icon's own context menu (MainWindow.xaml.cs mirrors these instead of
        // keeping its own copy, so toggling either place can never leave the other stale).
        [ObservableProperty]
        private bool _minimizeToTray;

        [ObservableProperty]
        private bool _minimizeOnClose;

        [ObservableProperty]
        private bool _folderExpandOnSingleClick;

        [ObservableProperty]
        private string _globalUserName = string.Empty;

        [ObservableProperty]
        private string _globalUserEmail = string.Empty;

        [ObservableProperty]
        private bool _hasLocalIdentityOverride;

        [ObservableProperty]
        private string _localUserName = string.Empty;

        [ObservableProperty]
        private string _localUserEmail = string.Empty;

        [ObservableProperty]
        private string _identityStatusMessage = string.Empty;

        public SettingsViewModel(
            IGitService gitService,
            Func<string> getRepoPath,
            bool initialMinimizeToTray,
            Action<bool> saveMinimizeToTray,
            bool initialMinimizeOnClose,
            Action<bool> saveMinimizeOnClose,
            bool initialFolderExpandOnSingleClick,
            Action<bool> saveFolderExpandOnSingleClick)
        {
            _gitService = gitService;
            _getRepoPath = getRepoPath;
            _saveMinimizeToTray = saveMinimizeToTray;
            _saveMinimizeOnClose = saveMinimizeOnClose;
            _saveFolderExpandOnSingleClick = saveFolderExpandOnSingleClick;

            _minimizeToTray = initialMinimizeToTray;
            _minimizeOnClose = initialMinimizeOnClose;
            _folderExpandOnSingleClick = initialFolderExpandOnSingleClick;
        }

        partial void OnMinimizeToTrayChanged(bool value) => _saveMinimizeToTray(value);
        partial void OnMinimizeOnCloseChanged(bool value) => _saveMinimizeOnClose(value);
        partial void OnFolderExpandOnSingleClickChanged(bool value) => _saveFolderExpandOnSingleClick(value);

        [RelayCommand]
        public async Task LoadGitIdentityAsync()
        {
            var repoPath = _getRepoPath();
            IdentityStatusMessage = string.Empty;

            GlobalUserName = await _gitService.GetGitConfigValueAsync(repoPath, "user.name", global: true) ?? string.Empty;
            GlobalUserEmail = await _gitService.GetGitConfigValueAsync(repoPath, "user.email", global: true) ?? string.Empty;

            if (string.IsNullOrEmpty(repoPath))
            {
                HasLocalIdentityOverride = false;
                LocalUserName = string.Empty;
                LocalUserEmail = string.Empty;
                return;
            }

            var localName = await _gitService.GetGitConfigValueAsync(repoPath, "user.name", global: false);
            var localEmail = await _gitService.GetGitConfigValueAsync(repoPath, "user.email", global: false);
            HasLocalIdentityOverride = localName != null || localEmail != null;
            LocalUserName = localName ?? string.Empty;
            LocalUserEmail = localEmail ?? string.Empty;
        }

        [RelayCommand]
        public async Task SaveGlobalIdentityAsync()
        {
            var repoPath = _getRepoPath();
            var (nameOk, nameOut) = await _gitService.SetGitConfigValueAsync(repoPath, "user.name", GlobalUserName.Trim(), global: true);
            var (emailOk, emailOut) = await _gitService.SetGitConfigValueAsync(repoPath, "user.email", GlobalUserEmail.Trim(), global: true);
            IdentityStatusMessage = nameOk && emailOk
                ? "Global identity saved."
                : $"Failed to save global identity: {nameOut} {emailOut}".Trim();
        }

        [RelayCommand]
        public async Task SaveLocalIdentityOverrideAsync()
        {
            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            var (nameOk, nameOut) = await _gitService.SetGitConfigValueAsync(repoPath, "user.name", LocalUserName.Trim(), global: false);
            var (emailOk, emailOut) = await _gitService.SetGitConfigValueAsync(repoPath, "user.email", LocalUserEmail.Trim(), global: false);
            HasLocalIdentityOverride = true;
            IdentityStatusMessage = nameOk && emailOk
                ? "Repository override saved."
                : $"Failed to save repository override: {nameOut} {emailOut}".Trim();
        }

        [RelayCommand]
        public async Task RemoveLocalIdentityOverrideAsync()
        {
            var repoPath = _getRepoPath();
            if (string.IsNullOrEmpty(repoPath)) return;

            await _gitService.UnsetLocalGitConfigValueAsync(repoPath, "user.name");
            await _gitService.UnsetLocalGitConfigValueAsync(repoPath, "user.email");
            HasLocalIdentityOverride = false;
            LocalUserName = string.Empty;
            LocalUserEmail = string.Empty;
            IdentityStatusMessage = "Repository override removed - this repo now uses the global identity.";
        }
    }
}
