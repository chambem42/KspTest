using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KspTest.Runner
{
    /// <summary>
    ///     Copies the harness and test assemblies into an install's GameData/KspTest.  The integrations (KspTest.MechJeb,
    ///     KspTest.RP1) go only where their mod is installed, so nothing type-loads against a missing assembly.  A file
    ///     replaced is kept as &lt;name&gt;.bak.&lt;time&gt;, the 3 newest per file.
    /// </summary>
    public static class Deployer
    {
        /// <summary>The harness this runner was built with, copied beside it from its package (ksptest/gamedata/KspTest).</summary>
        public static string BundledHarness => Path.Combine(AppContext.BaseDirectory, "ksptest", "gamedata", "KspTest");

        // integration assembly -> a file of the mod it needs, relative to GameData
        private static readonly Dictionary<string, string> Requires = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["KspTest.MechJeb.dll"] = Path.Combine("MechJeb2", "Plugins", "MechJeb2.dll"),
            ["KspTest.RP1.dll"] = Path.Combine("RP-1", "Plugins", "RP0.dll"),
        };

        /// <summary>The harness (KspTest.dll, its integrations and .version) from harnessDir into GameData/KspTest/Plugins.</summary>
        public static void DeployHarness(KspInstall install, string? harnessDir, Action<string> log)
        {
            harnessDir ??= BundledHarness;
            if (!Directory.Exists(harnessDir))
                throw new DirectoryNotFoundException($"no harness to deploy at {harnessDir}");
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
                if (Requires.TryGetValue(name, out string? needs) && !File.Exists(Path.Combine(install.GameData, needs)))
                {
                    log($"skip   {name} (its mod is not in this install)");
                    string stale = Path.Combine(plugins, name);
                    if (File.Exists(stale))
                    {
                        File.Delete(stale);
                        log($"removed stale {name}");
                    }

                    continue;
                }

                Copy(f, Path.Combine(plugins, name), log);
                CopyIfThere(Path.ChangeExtension(f, ".pdb"), Path.Combine(plugins, Path.GetFileNameWithoutExtension(name) + ".pdb"));
            }

            string version = Path.Combine(harnessDir, "KspTest.version");
            if (File.Exists(version))
                File.Copy(version, Path.Combine(install.HarnessDir, "KspTest.version"), true);
        }

        /// <summary>A test project's in-game assemblies into GameData/KspTest/Tests/&lt;name&gt;.</summary>
        public static void DeployTests(KspInstall install, string name, IEnumerable<string> assemblies, Action<string> log)
        {
            string dir = Path.Combine(install.HarnessDir, "Tests", name);
            Directory.CreateDirectory(dir);
            foreach (string f in assemblies)
            {
                Copy(f, Path.Combine(dir, Path.GetFileName(f)), log);
                CopyIfThere(Path.ChangeExtension(f, ".pdb"), Path.Combine(dir, Path.GetFileNameWithoutExtension(f) + ".pdb"));
            }
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
            if (File.Exists(from))
                File.Copy(from, to, true);
        }

        private static bool SameFile(string a, string b) =>
            new FileInfo(a).Length == new FileInfo(b).Length && File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));

        private static void PruneBackups(string file)
        {
            string dir = Path.GetDirectoryName(file)!;
            foreach (FileInfo old in new DirectoryInfo(dir).GetFiles(Path.GetFileName(file) + ".bak.*")
                         .OrderByDescending(f => f.LastWriteTime).Skip(3))
                old.Delete();
        }
    }
}
