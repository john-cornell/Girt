using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Girt.Services
{
    public class RecentRepositoriesService
    {
        private readonly string _storagePath;
        private const int MaxRecentCount = 10;

        public RecentRepositoriesService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var girtDir = Path.Combine(appData, "Girt");
            Directory.CreateDirectory(girtDir);
            _storagePath = Path.Combine(girtDir, "recent_repos.json");
        }

        public List<string> LoadRecentRepositories()
        {
            try
            {
                if (File.Exists(_storagePath))
                {
                    var json = File.ReadAllText(_storagePath);
                    var list = JsonSerializer.Deserialize<List<string>>(json);
                    if (list != null)
                    {
                        return list.Where(Directory.Exists).Distinct().Take(MaxRecentCount).ToList();
                    }
                }
            }
            catch
            {
                // Fallback gracefully on read failure
            }

            return new List<string>();
        }

        public void AddRepository(string path)
        {
            try
            {
                var list = LoadRecentRepositories();
                list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                list.Insert(0, path);

                var trimmed = list.Take(MaxRecentCount).ToList();
                var json = JsonSerializer.Serialize(trimmed, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_storagePath, json);
            }
            catch
            {
                // Ignore storage failures
            }
        }
    }
}
