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
        public string DirectoryPath => Path.GetDirectoryName(ModelPath)!;
        public string ModelPath { get; internal set; } = string.Empty;
        public string DbPath { get; internal set; } = string.Empty;

        public int Stage { get; set; }
        public int Contract { get; set; }
        public bool IsExactContract { get; set; }
    }

    public class AssetDiscoveryService
    {
        private static readonly Regex StageRegex = new(@"stage(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ContractRegex = new(@"contract(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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

            // Other conversations may already reference this folder.
            existing.ModelPath = bundle.ModelPath;
            existing.DbPath = bundle.DbPath;
            return existing;
        }

        private static AssetBundle? CreateBundleFromDirectory(string dir)
        {
            if (!Directory.Exists(dir)) return null;
            dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

            string modelPath = Path.Combine(dir, "model.gguf");
            if (!File.Exists(modelPath))
            {
                modelPath = Directory.EnumerateFiles(dir, "*.gguf").Order(StringComparer.Ordinal).FirstOrDefault() ?? string.Empty;
            }

            string dbPath = Path.Combine(dir, "shop.db");
            if (!File.Exists(dbPath))
            {
                dbPath = Directory.EnumerateFiles(dir, "*.db").Order(StringComparer.Ordinal).FirstOrDefault() ?? string.Empty;
            }

            if (!File.Exists(modelPath) || !File.Exists(dbPath))
            {
                return null;
            }

            string dirName = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(dirName)) dirName = dir;

            var bundle = new AssetBundle
            {
                Name = dirName,
                ModelPath = Path.GetFullPath(modelPath),
                DbPath = Path.GetFullPath(dbPath)
            };

            var stageMatch = StageRegex.Match(dirName);
            if (stageMatch.Success && int.TryParse(stageMatch.Groups[1].Value, out int s))
            {
                bundle.Stage = s;
            }

            var contractMatch = ContractRegex.Match(dirName);
            if (contractMatch.Success && int.TryParse(contractMatch.Groups[1].Value, out int c))
            {
                bundle.Contract = c;
            }

            bundle.IsExactContract = !dirName.Contains("-rc", StringComparison.OrdinalIgnoreCase);

            return bundle;
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

            // Sort: 1) Stage DESC, 2) Contract DESC, 3) Exact contract first
            return bundles
                .OrderByDescending(b => b.Stage)
                .ThenByDescending(b => b.Contract)
                .ThenByDescending(b => b.IsExactContract)
                .ThenBy(b => b.Name, StringComparer.Ordinal)
                .ToList();
        }
    }
}
