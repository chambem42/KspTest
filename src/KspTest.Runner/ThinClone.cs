using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace KspTest.Runner
{
    /// <summary>
    ///     A thin copy of an install (the master) for a KSP of its own beside the master's or another clone's: tens of MB,
    ///     made in seconds, the same mods, local builds and warm caches as the master.  Everything KSP writes relative to its
    ///     install is private: the root files (settings.cfg, PartDatabase.cfg, KSP.log under the clone as working directory),
    ///     saves, Logs, KSP_x64_Data/Plugins (KSPBurst renames its DLL at every start), GameData's root files (the
    ///     ModuleManager cache) and the GameData folders a run writes into (<see cref="PrivateFolders" />).  The rest is the
    ///     master's: each other root and GameData folder a junction, KSP_x64_Data's asset files hard links.  A mod writing
    ///     into its own folder (settings at exit, logs) writes the master's.  Windows only.
    ///
    ///     <see cref="Refresh" /> brings a clone up to date before a run (new or removed mods, a redeployed harness, a master
    ///     that has re-patched); <see cref="SyncBack" /> hands a cache a clone rebuilt to the master, so the next clones
    ///     start warm instead of each patching again.
    /// </summary>
    public static class ThinClone
    {
        /// <summary>The master's root folders that are the clone's own, empty to begin with.</summary>
        private static readonly string[] OwnRootFolders = { "saves", "Logs", "Screenshots", "thumbs" };

        /// <summary>
        ///     GameData folders copied, not linked: the harness (run.cfg, deployed tests), AtmosphereAutopilot (holds thread.log
        ///     open all session), QuickStart (the runner switches it off per run), Kopernicus' config (rewritten at exit, and
        ///     in ModuleManager's hash).
        /// </summary>
        public static readonly string[] PrivateFolders = { "KspTest", "AtmosphereAutopilot", "QuickMods", "Kopernicus/Config" };

        /// <summary>
        ///     Root folders made real and gone through (files copied, subfolders linked): KSPBurst's state in PluginData
        ///     (.KSPBurst.cache, argfile.txt, plugin_hash.txt), whose compiler arguments name the install, so a shared one has
        ///     every install recompile in turn; its 835 MB compiler package stays linked.
        /// </summary>
        private static readonly string[] RealRootFolders = { "PluginData", "PluginData/KSPBurst@*" };

        /// <summary>The cache ModuleManager writes in GameData's root, ConfigSHA last (it vouches for the rest).</summary>
        private static readonly string[] MmCache = { "ModuleManager.ConfigCache", "ModuleManager.TechTree", "ModuleManager.Physics", "ModuleManager.ConfigSHA" };

        /// <summary>Clone n of the master: &lt;root&gt;/&lt;master's folder name&gt;@n.</summary>
        public static string PathFor(string master, string root, int n) =>
            Path.Combine(root, Path.GetFileName(Path.GetFullPath(master).TrimEnd('\\', '/')) + "@" + n);

        /// <summary>Makes the clone if there is none (its executable is the last thing copied), then refreshes it.</summary>
        public static KspInstall Ensure(string master, string clone, Action<string> log)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                throw new PlatformNotSupportedException("thin clones are Windows only (junctions and hard links)");
            var src = new KspInstall(master);
            if (!src.Exists)
                throw new FileNotFoundException($"no KSP at {src.Root} to clone");
            var dst = new KspInstall(clone);
            Directory.CreateDirectory(Root(dst));
            using (FileMutex.Acquire(Root(dst) + ".lock", $"another process making clone {dst.Root}", TimeSpan.FromMinutes(10), log))
            {
                bool fresh = !dst.Exists;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                Refresh(src, dst, log);
                if (fresh)
                {
                    new InstanceInfo
                    {
                        Name = Path.GetFileName(Root(dst)), GameSource = src.Root, Created = DateTime.Now, LastUsed = DateTime.Now, Ready = true,
                    }.Save(dst.Root);
                    log($"clone {dst.Root} of {src.Root} made in {clock.Elapsed.TotalSeconds:F1} s");
                }
            }

            return dst;
        }

        /// <summary>
        ///     The clone made to match the master as it is now: junctions for new folders (dangling ones removed), the private
        ///     folders and root files copied where the master's are newer, KSP_x64_Data relinked, the master's ModuleManager
        ///     cache and PartDatabase.cfg taken when the master's cache is newer and differs.
        /// </summary>
        public static void Refresh(KspInstall master, KspInstall clone, Action<string> log)
        {
            int linked = 0, copied = 0;
            string src = Root(master), dst = Root(clone);

            foreach (string name in OwnRootFolders)
                Directory.CreateDirectory(Path.Combine(dst, name));
            foreach (string dir in Directory.GetDirectories(src))
            {
                string name = Path.GetFileName(dir);
                if (Skipped(name) || OwnRootFolders.Contains(name, StringComparer.OrdinalIgnoreCase) || name == "GameData" || name == "KSP_x64_Data")
                    continue;
                if (Matches(RealRootFolders, name))
                {
                    int[] c = RootTree(dir, Path.Combine(dst, name), name);
                    linked += c[0];
                    copied += c[1];
                }
                else
                {
                    linked += Link(Path.Combine(dst, name), dir);
                }
            }

            // KSP_x64_Data: its own Plugins (KSPBurst's DLL), the asset files hard-linked
            string data = Path.Combine(src, "KSP_x64_Data"), dataDst = Path.Combine(dst, "KSP_x64_Data");
            Directory.CreateDirectory(dataDst);
            foreach (string dir in Directory.GetDirectories(data))
            {
                string name = Path.GetFileName(dir);
                if (name.Equals("Plugins", StringComparison.OrdinalIgnoreCase))
                    copied += Mirror(dir, Path.Combine(dataDst, name));
                else
                    linked += Link(Path.Combine(dataDst, name), dir);
            }
            foreach (string file in Directory.GetFiles(data))
                copied += HardLink(Path.Combine(dataDst, Path.GetFileName(file)), file);

            // GameData: a junction per mod, the private folders copied, the root files (ModuleManager's cache aside) copied
            string gd = Path.Combine(src, "GameData"), gdDst = Path.Combine(dst, "GameData");
            Directory.CreateDirectory(gdDst);
            int[] counts = Tree(gd, gdDst, "");
            linked += counts[0];
            copied += counts[1];
            foreach (string link in Directory.GetDirectories(gdDst).Where(d => IsJunction(d) && !Directory.Exists(Path.Combine(gd, Path.GetFileName(d)))))
            {
                Directory.Delete(link);   // a junction to a folder the master no longer has
                log($"clone: {Path.GetFileName(link)} is gone from the master; unlinked");
            }

            if (Newer(Path.Combine(gd, "ModuleManager.ConfigSHA"), Path.Combine(gdDst, "ModuleManager.ConfigSHA")))
            {
                foreach (string f in MmCache)
                    CopyIfThere(Path.Combine(gd, f), Path.Combine(gdDst, f));
                CopyIfThere(Path.Combine(src, "PartDatabase.cfg"), Path.Combine(dst, "PartDatabase.cfg"));
                log("clone: took the master's ModuleManager cache and PartDatabase.cfg");
            }

            // the root's files last, the executable among them: a clone without it is unfinished
            foreach (string file in Directory.GetFiles(src))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || name == "KCTVesselSave" || name == "PartDatabase.cfg" && File.Exists(Path.Combine(dst, name)))
                    continue;
                copied += CopyIfNewer(file, Path.Combine(dst, name));
            }

            if (linked + copied > 0)
                log($"clone {dst}: {linked} folders linked, {copied} files copied or linked from {src}");
        }

        /// <summary>
        ///     After a clone's KSP has loaded: when its ModuleManager rebuilt the cache (the clone's ConfigSHA differs from the
        ///     master's and is newer), the cache and PartDatabase.cfg go to the master, for the next clones to start warm.
        ///     Not while a KSP runs from the master itself.
        /// </summary>
        public static void SyncBack(KspInstall clone, KspInstall master, Action<string> log)
        {
            string gd = master.GameData, gdClone = clone.GameData;
            if (!Newer(Path.Combine(gdClone, "ModuleManager.ConfigSHA"), Path.Combine(gd, "ModuleManager.ConfigSHA")))
                return;
            if (master.Running().Count > 0)
            {
                log($"clone: ModuleManager rebuilt its cache; not handed to {master.Root}, where KSP is running");
                return;
            }

            foreach (string f in MmCache)
                CopyIfThere(Path.Combine(gdClone, f), Path.Combine(gd, f));
            CopyIfThere(Path.Combine(clone.Root, "PartDatabase.cfg"), Path.Combine(master.Root, "PartDatabase.cfg"));
            log($"clone: ModuleManager rebuilt its cache; handed to {master.Root} for the next clones");
        }

        // A folder of GameData (rel "" is GameData itself): its files copied, each subfolder a junction, copied whole when
        // private, or made real and gone through when a private folder is inside it.  Returns { linked, copied }.
        private static int[] Tree(string src, string dst, string rel)
        {
            int linked = 0, copied = 0;
            foreach (string file in Directory.GetFiles(src))
            {
                string name = Path.GetFileName(file);
                if (rel == "" && MmCache.Contains(name))
                    continue;   // taken as a whole in Refresh
                copied += CopyIfNewer(file, Path.Combine(dst, name));
            }

            foreach (string dir in Directory.GetDirectories(src))
            {
                string sub = rel == "" ? Path.GetFileName(dir) : rel + "/" + Path.GetFileName(dir);
                string to = Path.Combine(dst, Path.GetFileName(dir));
                if (PrivateFolders.Any(p => p.Equals(sub, StringComparison.OrdinalIgnoreCase)))
                {
                    copied += Mirror(dir, to);
                }
                else if (PrivateFolders.Any(p => p.StartsWith(sub + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    if (IsJunction(to))
                        Directory.Delete(to);
                    Directory.CreateDirectory(to);
                    int[] c = Tree(dir, to, sub);
                    linked += c[0];
                    copied += c[1];
                }
                else
                {
                    linked += Link(to, dir);
                }
            }

            return new[] { linked, copied };
        }

        // a root folder in RealRootFolders: real, its files copied, each subfolder real when listed too, else linked
        private static int[] RootTree(string src, string dst, string rel)
        {
            int linked = 0, copied = 0;
            if (IsJunction(dst))
                Directory.Delete(dst);
            Directory.CreateDirectory(dst);
            foreach (string file in Directory.GetFiles(src))
                copied += CopyIfNewer(file, Path.Combine(dst, Path.GetFileName(file)));
            foreach (string dir in Directory.GetDirectories(src))
            {
                string sub = rel + "/" + Path.GetFileName(dir);
                if (Matches(RealRootFolders, sub))
                {
                    int[] c = RootTree(dir, Path.Combine(dst, Path.GetFileName(dir)), sub);
                    linked += c[0];
                    copied += c[1];
                }
                else
                {
                    linked += Link(Path.Combine(dst, Path.GetFileName(dir)), dir);
                }
            }

            return new[] { linked, copied };
        }

        // a path against patterns whose segments may end in "*"
        private static bool Matches(IEnumerable<string> patterns, string rel) =>
            patterns.Any(p =>
            {
                string[] ps = p.Split('/'), rs = rel.Split('/');
                return ps.Length == rs.Length && ps.Zip(rs, (a, b) => a.EndsWith("*")
                    ? b.StartsWith(a.TrimEnd('*'), StringComparison.OrdinalIgnoreCase)
                    : a.Equals(b, StringComparison.OrdinalIgnoreCase)).All(x => x);
            });

        private static bool Skipped(string name) =>
            name.StartsWith(".") || name.StartsWith("_") || name.IndexOf("bak", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("backup", StringComparison.OrdinalIgnoreCase) >= 0 ||
            new[] { "CKAN", "SteamCache", "steamExport", "Launcher_Data" }.Contains(name, StringComparer.OrdinalIgnoreCase);

        private static string Root(KspInstall i) => i.Root.TrimEnd('\\', '/');

        // 1 when it made the junction, 0 when it was there
        private static int Link(string link, string target)
        {
            if (Directory.Exists(link))
                return 0;
            StockBase.Junction(link, target);
            return 1;
        }

        // copies a folder's files where the source's are newer (a deployed harness, a new QuickStart config): nothing deleted
        private static int Mirror(string src, string dst)
        {
            if (IsJunction(dst))
                Directory.Delete(dst);
            Directory.CreateDirectory(dst);
            int n = 0;
            foreach (string file in Directory.GetFiles(src))
                n += CopyIfNewer(file, Path.Combine(dst, Path.GetFileName(file)));
            foreach (string dir in Directory.GetDirectories(src))
                n += Mirror(dir, Path.Combine(dst, Path.GetFileName(dir)));
            return n;
        }

        private static int CopyIfNewer(string src, string dst)
        {
            if (File.Exists(dst) && File.GetLastWriteTimeUtc(dst) >= File.GetLastWriteTimeUtc(src) && new FileInfo(dst).Length == new FileInfo(src).Length)
                return 0;
            File.Copy(src, dst, true);
            return 1;
        }

        private static void CopyIfThere(string src, string dst)
        {
            if (File.Exists(src))
                File.Copy(src, dst, true);
        }

        // the master's is newer and not the same
        private static bool Newer(string a, string b) =>
            File.Exists(a) && (!File.Exists(b) || File.GetLastWriteTimeUtc(a) > File.GetLastWriteTimeUtc(b) && !File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b)));

        // a hard link (the same file: no space, the master's updates seen) where the volume allows, else a copy
        private static int HardLink(string link, string target)
        {
            if (File.Exists(link))
            {
                if (File.GetLastWriteTimeUtc(link) == File.GetLastWriteTimeUtc(target) && new FileInfo(link).Length == new FileInfo(target).Length)
                    return 0;
                File.Delete(link);
            }

            if (!CreateHardLink(link, target, IntPtr.Zero))
                File.Copy(target, link);
            return 1;
        }

        private static bool IsJunction(string path) =>
            Directory.Exists(path) && new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
    }
}
