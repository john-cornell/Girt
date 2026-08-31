using System;
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

            CurrentTheme = LoadSavedTheme();
        }

        public AppTheme LoadSavedTheme()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        return settings.Theme;
                    }
                }
            }
            catch
            {
                // Fallback to default
            }

            return AppTheme.Dark;
        }

        public void SaveTheme(AppTheme theme)
        {
            try
            {
                var settings = new AppSettings { Theme = theme };
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsPath, json);
            }
            catch
            {
                // Ignore save errors
            }
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
