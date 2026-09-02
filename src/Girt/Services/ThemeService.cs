using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace Girt.Services
{
    public enum AppTheme
    {
        Dark,
        Light,
        System
    }

    public class AppSettings
    {
        public AppTheme Theme { get; set; } = AppTheme.Dark;
        public bool PushAfterCommit { get; set; } = false;
        public bool GroupBranchesIntoFolders { get; set; } = false;

        /// <summary>Whether clicking the "N to push" pill opens a diff review first, rather
        /// than pushing straight away. Defaults to the safer, review-first behavior.</summary>
        public bool PushPillOpensReview { get; set; } = true;

        /// <summary>Whether an external change to .git triggers a full silent refresh (graph,
        /// branches, working changes) instead of just the status pills. Off by default.</summary>
        public bool AutoRefresh { get; set; } = false;

        /// <summary>Whether minimizing the window drops it into the tray instead of the
        /// taskbar. Toggled from the tray icon's own context menu.</summary>
        public bool MinimizeToTray { get; set; } = true;

        /// <summary>Whether closing the window (the X button) hides to tray instead of exiting
        /// the app. Toggled from the tray icon's own context menu.</summary>
        public bool MinimizeOnClose { get; set; } = true;

        /// <summary>Whether clicking a folder row in the branch tree expands/collapses it with
        /// a single click, vs. requiring a double-click (leaves always keep their own
        /// single/double-click behavior for selection/checkout either way).</summary>
        public bool FolderExpandOnSingleClick { get; set; } = true;

        /// <summary>Pinned branch names, keyed by repository root path (pinning is per-repo).</summary>
        public Dictionary<string, List<string>> PinnedBranchesByRepo { get; set; } = new();
    }

    public class ThemeService
    {
        private readonly string _settingsPath;
        public AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;

        public ThemeService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var girtDir = Path.Combine(appData, "Girt");
            Directory.CreateDirectory(girtDir);
            _settingsPath = Path.Combine(girtDir, "settings.json");

            CurrentTheme = LoadSettings().Theme;
        }

        private AppSettings LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        return settings;
                    }
                }
            }
            catch
            {
                // Fallback to default
            }

            return new AppSettings();
        }

        // Settings are stored together in one file, so saving one setting must preserve the
        // others rather than overwrite the whole file with just the field being changed.
        private void SaveSettings(AppSettings settings)
        {
            try
            {
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsPath, json);
            }
            catch
            {
                // Ignore save errors
            }
        }

        public AppTheme LoadSavedTheme() => LoadSettings().Theme;

        public void SaveTheme(AppTheme theme)
        {
            var settings = LoadSettings();
            settings.Theme = theme;
            SaveSettings(settings);
        }

        public bool LoadPushAfterCommit() => LoadSettings().PushAfterCommit;

        public void SavePushAfterCommit(bool value)
        {
            var settings = LoadSettings();
            settings.PushAfterCommit = value;
            SaveSettings(settings);
        }

        public bool LoadGroupBranchesIntoFolders() => LoadSettings().GroupBranchesIntoFolders;

        public void SaveGroupBranchesIntoFolders(bool value)
        {
            var settings = LoadSettings();
            settings.GroupBranchesIntoFolders = value;
            SaveSettings(settings);
        }

        public bool LoadPushPillOpensReview() => LoadSettings().PushPillOpensReview;

        public void SavePushPillOpensReview(bool value)
        {
            var settings = LoadSettings();
            settings.PushPillOpensReview = value;
            SaveSettings(settings);
        }

        public bool LoadAutoRefresh() => LoadSettings().AutoRefresh;

        public void SaveAutoRefresh(bool value)
        {
            var settings = LoadSettings();
            settings.AutoRefresh = value;
            SaveSettings(settings);
        }

        public bool LoadMinimizeToTray() => LoadSettings().MinimizeToTray;

        public void SaveMinimizeToTray(bool value)
        {
            var settings = LoadSettings();
            settings.MinimizeToTray = value;
            SaveSettings(settings);
        }

        public bool LoadMinimizeOnClose() => LoadSettings().MinimizeOnClose;

        public void SaveMinimizeOnClose(bool value)
        {
            var settings = LoadSettings();
            settings.MinimizeOnClose = value;
            SaveSettings(settings);
        }

        public bool LoadFolderExpandOnSingleClick() => LoadSettings().FolderExpandOnSingleClick;

        public void SaveFolderExpandOnSingleClick(bool value)
        {
            var settings = LoadSettings();
            settings.FolderExpandOnSingleClick = value;
            SaveSettings(settings);
        }

        public HashSet<string> LoadPinnedBranches(string repoPath)
        {
            if (!string.IsNullOrEmpty(repoPath) &&
                LoadSettings().PinnedBranchesByRepo.TryGetValue(repoPath, out var pinned))
            {
                return new HashSet<string>(pinned, StringComparer.OrdinalIgnoreCase);
            }
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public void SavePinnedBranches(string repoPath, IEnumerable<string> pinnedBranchNames)
        {
            if (string.IsNullOrEmpty(repoPath)) return;

            var settings = LoadSettings();
            settings.PinnedBranchesByRepo[repoPath] = pinnedBranchNames.ToList();
            SaveSettings(settings);
        }

        public void ApplyTheme(AppTheme theme)
        {
            CurrentTheme = theme;
            SaveTheme(theme);

            var isDark = theme switch
            {
                AppTheme.Dark => true,
                AppTheme.Light => false,
                AppTheme.System => IsWindowsInDarkMode(),
                _ => true
            };

            var themeUri = isDark
                ? new Uri("pack://application:,,,/Themes/ThemeColors.Dark.xaml", UriKind.Absolute)
                : new Uri("pack://application:,,,/Themes/ThemeColors.Light.xaml", UriKind.Absolute);

            var appResources = Application.Current.Resources;
            var existingThemeDict = appResources.MergedDictionaries
                .FirstOrDefault(d => d.Source != null && d.Source.OriginalString.Contains("ThemeColors."));

            var newDict = new ResourceDictionary { Source = themeUri };

            if (existingThemeDict != null)
            {
                var index = appResources.MergedDictionaries.IndexOf(existingThemeDict);
                appResources.MergedDictionaries[index] = newDict;
            }
            else
            {
                appResources.MergedDictionaries.Insert(0, newDict);
            }
        }

        public void ToggleTheme()
        {
            var nextTheme = CurrentTheme switch
            {
                AppTheme.Dark => AppTheme.Light,
                _ => AppTheme.Dark
            };
            ApplyTheme(nextTheme);
        }

        private static bool IsWindowsInDarkMode()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var value = key?.GetValue("AppsUseLightTheme");
                if (value is int intVal)
                {
                    return intVal == 0;
                }
            }
            catch
            {
                // Fallback to dark if registry cannot be read
            }
            return true;
        }
    }
}
