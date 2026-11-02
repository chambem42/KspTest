using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KspTest.Runner
{
    /// <summary>
    ///     Copies the harness and test assemblies into an install's GameData/KspTest/Plugins.  The modules built against a mod
    ///     (KspTest.MechJeb, KspTest.RP1, KspTest.Trajectories, ...) go only where those mods are installed, so nothing
    ///     type-loads against a missing assembly.  A file
    ///     replaced is kept as &lt;name&gt;.bak.&lt;time&gt;, the 3 newest per file.
    /// </summary>
    public static class Deployer
    {
        /// <summary>The harness this runner was built with, copied beside it from its package (ksptest/gamedata/KspTest).</summary>
        public static string BundledHarness => Path.Combine(AppContext.BaseDirectory, "ksptest", "gamedata", "KspTest");

        // module assembly -> a file of each mod it needs, relative to GameData (the modules not here load anywhere)
        private static readonly string MechJeb = Path.Combine("MechJeb2", "Plugins", "MechJeb2.dll");
        private static readonly string Rp1 = Path.Combine("RP-1", "Plugins", "RP0.dll");

        private static readonly Dictionary<string, string[]> Requires = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["KspTest.MechJeb.dll"] = new[] { MechJeb },
            ["KspTest.RP1.dll"] = new[] { Rp1 },
            ["KspTest.Trajectories.MechJeb.dll"] = new[] { MechJeb },
            ["KspTest.Trajectories.RP1.dll"] = new[] { MechJeb, Rp1 },
            ["KspTest.WaypointManager.RP1.dll"] = new[] { Rp1 },
        };

        /// <summary>The mods (CKAN identifiers) the harness needs in every install: Harmony, which it patches the game with.</summary>
        public static readonly string[] HarnessMods = { "Harmony2" };

        private static readonly string HarmonyDll = Path.Combine("000_Harmony", "0Harmony.dll");

        // the installs whose GameData has been looked through for other copies of the run's assemblies
        private static readonly HashSet<string> Scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        ///     GameData/KspTest made exactly what a run needs: the harness and the modules whose mods the install has, and
        ///     the run's test assemblies (tests: name -> files).  All go into Plugins under their own names, so a second
        ///     copy of one replaces the first, and any other assembly under GameData/KspTest is removed: a module that has
        ///     moved to its mod's repo, another project's tests from an earlier run.  The same assembly elsewhere in
        ///     GameData (installed by hand) is only warned about.
        /// </summary>
        public static void DeployRun(KspInstall install, string? harnessDir, IEnumerable<KeyValuePair<string, List<string>>> tests,
            Action<string> log)
        {
            var deployed = new HashSet<string>(DeployHarness(install, harnessDir, log), StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, List<string>> t in tests)
                deployed.UnionWith(DeployTests(install, t.Key, t.Value, log));

            // one place per assembly: Plugins/<name>; any other copy, or an assembly the run does not have, goes
            string plugins = Path.Combine(install.HarnessDir, "Plugins");
            foreach (string f in Directory.GetFiles(install.HarnessDir, "*.*", SearchOption.AllDirectories)
                         .Where(f => (f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) &&
                                     (!deployed.Contains(Path.GetFileNameWithoutExtension(f)) ||
                                      !string.Equals(Path.GetDirectoryName(f), plugins, StringComparison.OrdinalIgnoreCase))))
            {
                File.Delete(f);
                log($"removed {f.Substring(install.GameData.Length + 1)} (not part of this run)");
            }

            // where test assemblies went before Plugins took them all
            string oldTests = Path.Combine(install.HarnessDir, "Tests");
            if (Directory.Exists(oldTests))
                Directory.Delete(oldTests, true);

            // the rest of GameData (tens of thousands of files in an RP-1 install) is looked through once per process
            lock (Scanned)
                if (!Scanned.Add(install.Root))
                    return;
            foreach (string f in Directory.EnumerateFiles(install.GameData, "*.dll", SearchOption.AllDirectories)
                         .Where(f => !f.StartsWith(install.HarnessDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                                     deployed.Contains(Path.GetFileNameWithoutExtension(f))))
                log($"WARNING: {f.Substring(install.GameData.Length + 1)} is also installed; KSP loads only one of the two");
        }

        /// <summary>The harness (KspTest.dll, its modules and .version) from harnessDir into GameData/KspTest/Plugins; the assemblies deployed.</summary>
        public static List<string> DeployHarness(KspInstall install, string? harnessDir, Action<string> log)
        {
            var deployed = new List<string>();
            harnessDir ??= BundledHarness;
            if (!Directory.Exists(harnessDir))
                throw new DirectoryNotFoundException($"no harness to deploy at {harnessDir}");
            // without it KSP drops the harness's runner, and KSP starts with nothing driving it
            if (!File.Exists(Path.Combine(install.GameData, HarmonyDll)))
                throw new InvalidOperationException($"KspTest needs Harmony 2 (GameData/{HarmonyDll.Replace('\\', '/')}; CKAN: Harmony2), which {install.Root} lacks");
            // KspTest's predecessor: each harness stays inert (no patches, no scene changes) without its own run.cfg, so
            // the two can share an install as long as only one is given a run at a time
            string old = Path.Combine(install.GameData, "KSPUITest");
            if (Directory.Exists(old))
                log($"note: {old} (KspTest's predecessor) is installed too; it stays inert during KspTest runs");
            string plugins = Path.Combine(install.HarnessDir, "Plugins");
            Directory.CreateDirectory(plugins);
            Directory.CreateDirectory(Path.Combine(install.HarnessDir, "PluginData"));

            // the harness's files may sit in harnessDir itself (package layout) or in its Plugins folder (repo layout)
            string source = Directory.Exists(Path.Combine(harnessDir, "Plugins")) ? Path.Combine(harnessDir, "Plugins") : harnessDir;
            foreach (string f in Directory.GetFiles(source, "KspTest*.dll").Where(f => !Path.GetFileName(f).StartsWith("KspTest.Tests.", StringComparison.OrdinalIgnoreCase)))
            {
                string name = Path.GetFileName(f);
                if (Requires.TryGetValue(name, out string[]? needs) && needs.Any(n => !File.Exists(Path.Combine(install.GameData, n))))
                {
                    log($"skip   {name} (its mod is not in this install)");
                    // a copy an earlier deploy left (the mod since gone) would fail to load: KSP drops the harness with it
                    foreach (string stale in new[] { name, Path.GetFileNameWithoutExtension(name) + ".pdb" }.Select(n => Path.Combine(plugins, n)).Where(File.Exists))
                    {
                        File.Delete(stale);
                        log($"removed {stale.Substring(install.GameData.Length + 1)} (its mod is not in this install)");
                    }
                    continue;
                }

                Copy(f, Path.Combine(plugins, name), log);
                CopyIfThere(Path.ChangeExtension(f, ".pdb"), Path.Combine(plugins, Path.GetFileNameWithoutExtension(name) + ".pdb"));
                deployed.Add(Path.GetFileNameWithoutExtension(name));
            }

            string version = Path.Combine(harnessDir, "KspTest.version");
            if (File.Exists(version))
                File.Copy(version, Path.Combine(install.HarnessDir, "KspTest.version"), true);
            return deployed;
        }

        /// <summary>A test project's in-game assemblies (name: the project, for the log) into GameData/KspTest/Plugins; the assemblies deployed.</summary>
        public static List<string> DeployTests(KspInstall install, string name, IEnumerable<string> assemblies, Action<string> log)
        {
            string dir = Path.Combine(install.HarnessDir, "Plugins");
            Directory.CreateDirectory(dir);
            var deployed = new List<string>();
            foreach (string f in assemblies)
            {
                Copy(f, Path.Combine(dir, Path.GetFileName(f)), log);
                CopyIfThere(Path.ChangeExtension(f, ".pdb"), Path.Combine(dir, Path.GetFileNameWithoutExtension(f) + ".pdb"));
                deployed.Add(Path.GetFileNameWithoutExtension(f));
            }

            return deployed;
        }

        /// <summary>
        ///     The local mods (GameData/ModName folders: built from source, or not on CKAN) a run in a KspTest instance has,
        ///     mirrored in (<see cref="Overlay" />), and none it does not: a local mod an earlier run put in goes again, CKAN's
        ///     copy back where it was laid over one (KspTest-pristine), else removed.  KspTest-local.txt in the instance says
        ///     which are in.
        /// </summary>
        public static void LocalMods(KspInstall install, IEnumerable<string> folders, Action<string> log)
        {
            List<string> wanted = folders.Select(f => Path.GetFullPath(f).TrimEnd('/', '\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var names = new HashSet<string>(wanted.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            string record = Path.Combine(install.Root, "KspTest-local.txt");
            string[] before = File.Exists(record) ? File.ReadAllLines(record).Where(l => l.Trim().Length > 0).ToArray() : new string[0];
            // what may be in until this is done: an overlay cut short is still undone next time
            File.WriteAllLines(record, before.Union(names, StringComparer.OrdinalIgnoreCase));

            foreach (string old in before.Where(n => !names.Contains(n)))
            {
                string target = Path.Combine(install.GameData, old);
                string pristine = Path.Combine(install.Root, "KspTest-pristine", old);
                if (Directory.Exists(pristine))
                {
                    int copied = 0, removed = 0;
                    Mirror(pristine, target, ref copied, ref removed);
                    log($"restored GameData/{old}: CKAN's copy (this run does not have the local one)");
                }
                else if (Directory.Exists(target))
                {
                    StockBase.DeleteTree(target);
                    log($"removed GameData/{old} (a local mod this run does not have)");
                }
            }

            foreach (string f in wanted)
                Overlay(install, f, log);
            File.WriteAllLines(record, names);
        }

        /// <summary>
        ///     A mod's GameData folder built from source (source: .../GameData/ModName) over the one CKAN installed in a KspTest
        ///     instance: GameData/ModName made the same as it, files added, changed and removed.  The CKAN copy is kept the
        ///     first time in KspTest-pristine/ModName.  Only for instances: in an install of the user's own it would replace their copy.
        /// </summary>
        public static void Overlay(KspInstall install, string source, Action<string> log)
        {
            source = Path.GetFullPath(source).TrimEnd('/', '\\');
            if (!Directory.Exists(source))
                throw new DirectoryNotFoundException($"no folder to overlay at {source} (is the mod built?)");
            string name = Path.GetFileName(source);
            string target = Path.Combine(install.GameData, name);
            string pristine = Path.Combine(install.Root, "KspTest-pristine", name);
            if (Directory.Exists(target) && !Directory.Exists(pristine))
                CopyDir(target, pristine);

            int copied = 0, removed = 0;
            Mirror(source, target, ref copied, ref removed);
            log($"overlay GameData/{name} <- {source}" + (copied + removed > 0 ? $" ({copied} copied, {removed} removed)" : " (unchanged)"));
        }

        private static void Mirror(string from, string to, ref int copied, ref int removed)
        {
            Directory.CreateDirectory(to);
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in Directory.GetFiles(from))
            {
                string name = Path.GetFileName(f), dest = Path.Combine(to, name);
                wanted.Add(name);
                var s = new FileInfo(f);
                var d = new FileInfo(dest);
                if (d.Exists && d.Length == s.Length && d.LastWriteTimeUtc == s.LastWriteTimeUtc)
                    continue;
                File.Copy(f, dest, true);
                copied++;
            }

            foreach (string dir in Directory.GetDirectories(from))
            {
                wanted.Add(Path.GetFileName(dir));
                Mirror(dir, Path.Combine(to, Path.GetFileName(dir)), ref copied, ref removed);
            }

            foreach (string f in Directory.GetFiles(to).Where(f => !wanted.Contains(Path.GetFileName(f))))
            {
                File.Delete(f);
                removed++;
            }

            foreach (string dir in Directory.GetDirectories(to).Where(d => !wanted.Contains(Path.GetFileName(d))))
            {
                StockBase.DeleteTree(dir);
                removed++;
            }
        }

        private static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from))
                File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (string d in Directory.GetDirectories(from))
                CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }

        private static void Copy(string from, string to, Action<string> log)
        {
            if (File.Exists(to))
            {
                if (SameFile(from, to))
                    return;
                File.Copy(to, $"{to}.bak.{DateTime.Now:yyyyMMdd-HHmmss}", true);
                PruneBackups(to);
            }

            File.Copy(from, to, true);
            log($"deploy {Path.GetFileName(from)} -> {to}");
        }

        private static void CopyIfThere(string from, string to)
        {
            if (File.Exists(from) && !(File.Exists(to) && SameFile(from, to)))
                File.Copy(from, to, true);
        }

        // File.Copy keeps the time written, so a copy made earlier matches by size and time without reading either;
        // where the time was not kept, the bytes decide
        private static bool SameFile(string a, string b)
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            return fa.Length == fb.Length &&
                   (fa.LastWriteTimeUtc == fb.LastWriteTimeUtc || File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b)));
        }

        private static void PruneBackups(string file)
        {
            string dir = Path.GetDirectoryName(file)!;
            foreach (FileInfo old in new DirectoryInfo(dir).GetFiles(Path.GetFileName(file) + ".bak.*")
                         .OrderByDescending(f => f.LastWriteTime).Skip(3))
                old.Delete();
        }
    }
}
