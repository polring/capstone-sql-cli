using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SqlApp.Interop.SqlEngine;

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

        internal bool IsAutomaticCandidate { get; set; }
    }

    public class AssetDiscoveryService
    {
        internal static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public static List<AssetBundle> DiscoverBundles()
        {
            var visited = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                {
                    if (!visited.Add(dir.FullName)) break;
                    var bundles = DiscoverBundles(Path.Combine(dir.FullName, "assets"));
                    if (bundles.Count != 0) return bundles;
                }
            }
            return [];
        }

        internal static List<AssetBundle> DiscoverBundles(string assetsRoot)
        {
            var bundles = new List<AssetBundle>();
            if (!Directory.Exists(assetsRoot)) return bundles;
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
            foreach (var dir in Directory.EnumerateDirectories(assetsRoot, "*", options))
            {
                var bundle = ReadBundle(dir, automatic: true);
                if (bundle != null) bundles.Add(bundle);
            }
            return bundles.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Name, StringComparer.Ordinal).ToList();
        }

        internal static AssetBundle? GetDefaultBundle(IEnumerable<AssetBundle> bundles, bool dual) =>
            bundles.Where(b => b.IsAutomaticCandidate && b.IsDual == dual)
                .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Name, StringComparer.Ordinal).LastOrDefault();

        public static AssetBundle? AddFolder(IList<AssetBundle> bundles, string dir, bool? dual = null)
        {
            var bundle = ReadBundle(dir, automatic: false, dual);
            if (bundle == null) return null;

            var previous = bundles.FirstOrDefault(b => b.IsDual == bundle.IsDual &&
                b.DirectoryPath.Equals(bundle.DirectoryPath, PathComparison));
            if (previous == null) bundles.Add(bundle);
            else
            {
                // Preserve automatic eligibility when a user reopens a complete discovered bundle.
                bundle.IsAutomaticCandidate = previous.IsAutomaticCandidate && bundle.DbPath.Length > 0 &&
                    (!bundle.IsDual || (bundle.FrontModelPath.Length > 0 && bundle.BackModelPath.Length > 0));
                bundles[bundles.IndexOf(previous)] = bundle;
            }
            return bundle;
        }

        private static AssetBundle? ReadBundle(string dir, bool automatic, bool? dual = null)
        {
            if (!Directory.Exists(dir)) return null;
            dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
            var models = new List<string>(2);
            string db = string.Empty;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    string ext = Path.GetExtension(file);
                    if (ext.Equals(".gguf", StringComparison.OrdinalIgnoreCase))
                    {
                        if (models.Count == 2) return null;
                        models.Add(file);
                    }
                    else if (ext.Equals(".db", StringComparison.OrdinalIgnoreCase))
                    {
                        if (db.Length != 0) return null;
                        db = file;
                    }
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }

            if (automatic && (db.Length == 0 || models.Count == 0)) return null;
            string front = string.Empty, back = string.Empty, single = string.Empty;
            if (models.Count == 2)
            {
                if (dual == false) return null;
                (front, back) = AssignModelRoles(models[0], models[1]);
            }
            else if (models.Count == 1)
            {
                var path = models[0];
                var role = FilenameRole(path);
                if (automatic || dual == false || (dual == null && role == null))
                {
                    if (db.Length == 0) return null;
                    single = path;
                }
                else
                {
                    // An explicitly selected dual stage can run with just its own model.
                    role ??= GgufContract.TryReadModelRole(path);
                    if (role == ModelRole.Front) front = path;
                    else if (role == ModelRole.Back) back = path;
                    else return null;
                }
            }
            else if (db.Length == 0 || dual == false) return null;

            return new AssetBundle
            {
                Name = Path.GetFileName(dir), DirectoryPath = dir, DbPath = db,
                ModelPath = single, FrontModelPath = front, BackModelPath = back,
                IsDual = single.Length == 0, IsAutomaticCandidate = automatic
            };
        }

        private static ModelRole? FilenameRole(string path) => Path.GetFileNameWithoutExtension(path).ToLowerInvariant() switch
        {
            "front" => ModelRole.Front, "back" => ModelRole.Back, _ => null
        };

        private static (string Front, string Back) AssignModelRoles(string first, string second)
        {
            ModelRole? firstRole = FilenameRole(first), secondRole = FilenameRole(second);
            if (firstRole == null && secondRole == null)
                (firstRole, secondRole) = (GgufContract.TryReadModelRole(first), GgufContract.TryReadModelRole(second));

            if (firstRole != secondRole)
                return firstRole == ModelRole.Front || secondRole == ModelRole.Back ? (first, second) : (second, first);
            return StringComparer.Ordinal.Compare(first, second) <= 0 ? (first, second) : (second, first);
        }
    }
}
