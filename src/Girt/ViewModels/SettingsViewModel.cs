using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Girt.Services;
using Microsoft.Win32;

namespace Girt.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly IGitService _gitService;
        private readonly Func<string> _getRepoPath;
        private readonly Action<bool> _saveMinimizeToTray;
        private readonly Action<bool> _saveMinimizeOnClose;
        private readonly Action<bool> _saveFolderExpandOnSingleClick;
        private readonly Action<bool> _saveEnableTimingLogs;

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
        private bool _enableTimingLogs;

        public string LogFilePath => LogService.LogFilePath;

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

        // ================= SUPPORTING APPS (MERGE TOOL) =================
        // git already ships a correct invocation template (base/local/remote/merged wiring,
        // CLI flags) for every one of these - Girt's job is just to tell git which one to use
        // (merge.tool) and, if it's not on PATH, where to find its .exe
        // (mergetool.<name>.path) - the same mechanism the old kdiff3-only auto-configure
        // offer used, generalized to any tool instead of hardcoding just kdiff3's two known
        // install locations.
        public string[] KnownMergeTools { get; } =
        {
            "kdiff3", "meld", "p4merge", "vscode", "winmerge", "tortoisemerge", "bc", "diffmerge"
        };

        [ObservableProperty]
        private string _mergeToolName = string.Empty;

        [ObservableProperty]
        private string _mergeToolPath = string.Empty;

        [ObservableProperty]
        private string _mergeToolStatusMessage = string.Empty;

        public SettingsViewModel(
            IGitService gitService,
            Func<string> getRepoPath,
            bool initialMinimizeToTray,
            Action<bool> saveMinimizeToTray,
            bool initialMinimizeOnClose,
            Action<bool> saveMinimizeOnClose,
            bool initialFolderExpandOnSingleClick,
            Action<bool> saveFolderExpandOnSingleClick,
            bool initialEnableTimingLogs,
            Action<bool> saveEnableTimingLogs)
        {
            _gitService = gitService;
            _getRepoPath = getRepoPath;
            _saveMinimizeToTray = saveMinimizeToTray;
            _saveMinimizeOnClose = saveMinimizeOnClose;
            _saveFolderExpandOnSingleClick = saveFolderExpandOnSingleClick;
            _saveEnableTimingLogs = saveEnableTimingLogs;

            _minimizeToTray = initialMinimizeToTray;
            _minimizeOnClose = initialMinimizeOnClose;
            _folderExpandOnSingleClick = initialFolderExpandOnSingleClick;
            _enableTimingLogs = initialEnableTimingLogs;
            LogService.EnableTimingLogs = initialEnableTimingLogs;
        }

        partial void OnMinimizeToTrayChanged(bool value) => _saveMinimizeToTray(value);
        partial void OnMinimizeOnCloseChanged(bool value) => _saveMinimizeOnClose(value);
        partial void OnFolderExpandOnSingleClickChanged(bool value) => _saveFolderExpandOnSingleClick(value);

        partial void OnEnableTimingLogsChanged(bool value)
        {
            _saveEnableTimingLogs(value);
            LogService.EnableTimingLogs = value;
        }

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

        [RelayCommand]
        public async Task LoadMergeToolSettingsAsync()
        {
            var repoPath = _getRepoPath();
            MergeToolStatusMessage = string.Empty;

            var toolName = await _gitService.GetGitConfigValueAsync(repoPath, "merge.tool", global: true) ?? string.Empty;
            MergeToolName = toolName;

            MergeToolPath = string.IsNullOrEmpty(toolName)
                ? string.Empty
                : await _gitService.GetGitConfigValueAsync(repoPath, $"mergetool.{toolName}.path", global: true) ?? string.Empty;
        }

        [RelayCommand]
        public async Task SaveMergeToolAsync()
        {
            var name = MergeToolName.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MergeToolStatusMessage = "Enter or pick a merge tool name first.";
                return;
            }

            var repoPath = _getRepoPath();
            var (toolOk, toolOut) = await _gitService.SetGitConfigValueAsync(repoPath, "merge.tool", name, global: true);
            if (!toolOk)
            {
                MergeToolStatusMessage = $"Failed to save merge tool: {toolOut}".Trim();
                return;
            }

            var path = MergeToolPath.Trim();
            if (!string.IsNullOrEmpty(path))
            {
                var (pathOk, pathOut) = await _gitService.SetGitConfigValueAsync(repoPath, $"mergetool.{name}.path", path.Replace('\\', '/'), global: true);
                if (!pathOk)
                {
                    MergeToolStatusMessage = $"Merge tool set to '{name}', but saving its path failed: {pathOut}".Trim();
                    return;
                }
            }

            MergeToolStatusMessage = $"Merge tool set to '{name}'.";
        }

        [RelayCommand]
        public void SetMergeToolPreset(string name)
        {
            // A fresh pick replaces any previous path override - it almost certainly belonged
            // to whatever tool was selected before, not this one.
            MergeToolName = name;
            MergeToolPath = string.Empty;
        }

        [RelayCommand]
        public void BrowseForMergeToolPath()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Locate Merge Tool Executable",
                Filter = "Executable (*.exe)|*.exe|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                MergeToolPath = dialog.FileName;
            }
        }
    }
}
