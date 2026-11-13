using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SqlApp.Services
{
    public class AssetBundle
    {
        public string Name { get; init; } = string.Empty;
        public string DirectoryPath { get; init; } = string.Empty;
        public string ModelPath { get; internal set; } = string.Empty;
        public string DbPath { get; internal set; } = string.Empty;
        public string FrontModelPath { get; internal set; } = string.Empty;
        public string BackModelPath { get; internal set; } = string.Empty;
        public bool IsDual { get; init; }
        public bool HasPreprocessor => false;

        public int Stage { get; set; }
        public bool IsReleaseCandidate => Name.Contains("-rc", StringComparison.OrdinalIgnoreCase);
    }

    public class AssetDiscoveryService
    {
        private static readonly Regex StageRegex = new(@"stage(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        internal static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private static string FindAssetsRoot()
        {
            // Search upwards from current directory and AppContext.BaseDirectory to find the project root "assets" folder
            string[] startDirs = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };

            foreach (var start in startDirs)
            {
                try
                {
                    var dir = new DirectoryInfo(start);
                    while (dir != null)
                    {
                        string candidate = Path.Combine(dir.FullName, "assets");
                        if (Directory.Exists(candidate))
                        {
                            // An empty checkout contains only the Git directory marker.
                            if (Directory.EnumerateFileSystemEntries(candidate).Any(path => Path.GetFileName(path) != ".gitkeep"))
                            {
                                return Path.GetFullPath(candidate);
                            }
                        }
                        dir = dir.Parent;
                    }
                }
                catch { }
            }

            return Path.GetFullPath("assets");
        }

        public static AssetBundle? AddFolder(IList<AssetBundle> bundles, string dir)
        {
            var bundle = CreateBundleFromDirectory(dir);
            if (bundle == null) return null;

            var existing = bundles.FirstOrDefault(b => b.DirectoryPath.Equals(bundle.DirectoryPath, PathComparison));
            if (existing == null)
            {
                bundles.Add(bundle);
                return bundle;
            }

            // A renewed folder is a new snapshot. In-flight runs retain the original paths.
            bundles[bundles.IndexOf(existing)] = bundle;
            return bundle;
        }

        private static AssetBundle? CreateBundleFromDirectory(string dir)
        {
            if (!Directory.Exists(dir)) return null;
            dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

            string front = FindFile(dir, "front.gguf");
            string back = FindFile(dir, "back.gguf");
            string modelPath = FindFile(dir, "model.gguf");
            string dbPath = FindFile(dir, "shop.db");
            bool schemaOnly = dbPath.Length != 0 && !Directory.EnumerateFiles(dir, "*.gguf").Any();
            bool dual = front.Length > 0 || back.Length > 0 || schemaOnly;
            if (dual && modelPath.Length > 0) return null;
            if (!dual && modelPath.Length == 0)
            {
                // Preserve legacy bundles without assigning a role to an ambiguous file.
                string[] candidates = Directory.EnumerateFiles(dir, "*.gguf").Take(2).ToArray();
                if (candidates.Length != 1) return null;
                modelPath = candidates[0];
            }
            if (!dual && dbPath.Length == 0)
                dbPath = Directory.EnumerateFiles(dir, "*.db").Order(StringComparer.Ordinal).FirstOrDefault() ?? string.Empty;

            if (!dual && dbPath.Length == 0)
            {
                return null;
            }

            string dirName = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(dirName)) dirName = dir;

            var bundle = new AssetBundle
            {
                Name = dirName,
                DirectoryPath = dir,
                IsDual = dual,
                ModelPath = modelPath,
                DbPath = dbPath,
                FrontModelPath = front,
                BackModelPath = back
            };

            var stageMatch = StageRegex.Match(dirName);
            if (stageMatch.Success && int.TryParse(stageMatch.Groups[1].Value, out int s))
            {
                bundle.Stage = s;
            }

            return bundle;
        }

        private static string FindFile(string dir, string name)
        {
            string path = Path.Combine(dir, name);
            return File.Exists(path) ? path : string.Empty;
        }

        public static List<AssetBundle> DiscoverBundles()
        {
            var bundles = new List<AssetBundle>();
            string assetsRoot = FindAssetsRoot();

            if (!Directory.Exists(assetsRoot))
            {
                return bundles;
            }

            foreach (var dir in Directory.GetDirectories(assetsRoot))
            {
                var bundle = CreateBundleFromDirectory(dir);
                if (bundle != null)
                {
                    bundles.Add(bundle);
                }
            }

            return bundles
                .OrderByDescending(b => b.Stage)
                .ThenBy(b => b.IsReleaseCandidate)
                .ThenBy(b => b.Name, StringComparer.Ordinal)
                .ToList();
        }
    }
}
