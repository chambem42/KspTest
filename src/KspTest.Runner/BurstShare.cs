using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KspTest.Runner
{
    /// <summary>
    ///     KSPBurst unpacks its compiler package (PluginData/KSPBurst@x-com.unity.burst@y, 835 MB) into an install the
    ///     first time it starts there, and an instance is a whole install: each kept a copy of its own (58 of them, 24 GB).
    ///     Shared as a thin clone shares its master's (<see cref="ThinClone" />): the package's folder the install's own,
    ///     its root files too (argfile.txt names the install: shared, every install would recompile in turn), each of its
    ///     subfolders a junction to the one in the instances folder's .shared (the first install's moved there). A subfolder
    ///     whose files differ from the shared one's (their names and sizes) is left as it is, as is an install whose KSP
    ///     is running. Windows only (junctions).
    /// </summary>
    public static class BurstShare
    {
        public const string SharedFolder = ".shared";
        private const string PackagePattern = "KSPBurst@*-com.unity.burst@*";

        /// <summary>The install's compiler packages' subfolders made junctions to the shared ones; the bytes freed.</summary>
        public static long Share(string install, string instancesRoot, Action<string> log)
        {
            string pluginData = Path.Combine(install, "PluginData");
            if (!Directory.Exists(pluginData) || !System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                return 0;
            string name = Path.GetFileName(install);
            long freed = 0;
            foreach (string package in Directory.GetDirectories(pluginData, PackagePattern))
            {
                string pkg = Path.GetFileName(package);
                string shared = Path.Combine(instancesRoot, SharedFolder, pkg);
                try
                {
                    if (new KspInstall(install).Running().Count > 0)
                    {
                        log($"{name}: KSP is running; its {pkg} left as it is");
                        continue;
                    }
                    if (IsJunction(package))
                    {
                        // the whole package linked (as a first version did): the folder made the install's own again, the
                        // shared one's root files (the install's, moved there with it) copied back
                        Directory.Delete(package);
                        Directory.CreateDirectory(package);
                        foreach (string f in Directory.GetFiles(shared))
                            File.Copy(f, Path.Combine(package, Path.GetFileName(f)));
                        log($"{name}: {pkg} was linked whole; its own again, its subfolders linked");
                    }
                    Directory.CreateDirectory(shared);
                    foreach (string sub in Directory.GetDirectories(package))
                    {
                        if (IsJunction(sub))
                            continue;
                        string to = Path.Combine(shared, Path.GetFileName(sub));
                        if (!Directory.Exists(to))
                        {
                            Directory.Move(sub, to);
                            StockBase.Junction(sub, to);
                            log($"{name}: {pkg}/{Path.GetFileName(sub)} moved to {shared}, linked back");
                            continue;
                        }
                        if (!SameFiles(sub, to))
                        {
                            log($"{name}: {pkg}/{Path.GetFileName(sub)} differs from the shared one; left as it is");
                            continue;
                        }
                        long size = Size(sub);
                        StockBase.DeleteTree(sub);
                        StockBase.Junction(sub, to);
                        freed += size;
                    }
                    // a subfolder the shared package has and this one lacks (a whole link undone above): linked
                    foreach (string to in Directory.GetDirectories(shared))
                    {
                        string sub = Path.Combine(package, Path.GetFileName(to));
                        if (!Directory.Exists(sub))
                            StockBase.Junction(sub, to);
                    }
                    if (freed > 0)
                        log($"{name}: {pkg} linked to the shared one ({freed / 1048576.0:F0} MB freed)");
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    log($"{name}: could not share {pkg}: {e.Message}");
                }
            }
            return freed;
        }

        // the same files (relative names, sizes) under both
        private static bool SameFiles(string a, string b)
        {
            Dictionary<string, long> Files(string root) => new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories)
                .ToDictionary(f => f.FullName.Substring(root.Length).TrimStart('\\', '/').ToLowerInvariant(), f => f.Length);
            Dictionary<string, long> fa = Files(a), fb = Files(b);
            return fa.Count == fb.Count && fa.All(kv => fb.TryGetValue(kv.Key, out long n) && n == kv.Value);
        }

        private static long Size(string root) =>
            new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

        private static bool IsJunction(string path) =>
            Directory.Exists(path) && new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
    }
}
