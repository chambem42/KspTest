using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace KspTest.Runner
{
    /// <summary>
    ///     A stock KSP in a new folder, from any install (modded or not), for CKAN to install mods into.  The big read-only
    ///     folders (KSP_x64_Data or KSP_Data, MonoBleedingEdge, GameData/Squad and SquadExpansion) are the source's, linked
    ///     (<see cref="LinkFolder" />); the executables and the small stock folders are copied, less any file the source's
    ///     CKAN registry says a mod installed (CKAN refuses to install over a file it did not put there).  Its settings.cfg
    ///     comes along, so the window and graphics are the player's.  Windows and Linux; not macOS yet.
    /// </summary>
    public static class StockBase
    {
        // Windows' and Linux's (KSP.x86_64, UnityPlayer.so, KSP_Data): whichever the source has
        private static readonly string[] Files =
        {
            "KSP_x64.exe", "UnityPlayer.dll", "UnityCrashHandler64.exe", "KSP.x86_64", "UnityPlayer.so", "buildID.txt", "buildID64.txt",
            "readme.txt", "LegalNotice.txt", "Physics.cfg", "settings.cfg",
        };

        private static readonly string[] Linked = { "KSP_x64_Data", "KSP_Data", "MonoBleedingEdge", "GameData/Squad", "GameData/SquadExpansion" };

        private static readonly string[] Copied =
        {
            "Internals", "Missions", "Resources", "sounds", "Ships/VAB", "Ships/SPH", "Ships/@thumbs/VAB", "Ships/@thumbs/SPH",
            "saves/scenarios", "saves/training",
        };

        // stock folders CKAN expects to exist even where the source's are empty
        private static readonly string[] Folders =
        {
            "GameData", "Parts", "Plugins", "saves", "Ships/VAB", "Ships/SPH", "Ships/@thumbs/VAB", "Ships/@thumbs/SPH",
        };

        public static void Create(string source, string dest, Action<string> log)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                throw new PlatformNotSupportedException("KspTest makes instances on Windows and Linux only so far; set KSPDIR to an install to run in");
            var src = new KspInstall(source);
            if (!src.Exists)
                throw new FileNotFoundException($"no KSP at {source} ({src.Executable})");

            HashSet<string> modFiles = CkanOwnedFiles(src.Root);
            Directory.CreateDirectory(dest);
            foreach (string f in Folders)
                Directory.CreateDirectory(Path.Combine(dest, f));
            foreach (string f in Files.Where(f => File.Exists(Path.Combine(src.Root, f))))
                File.Copy(Path.Combine(src.Root, f), Path.Combine(dest, f));
            foreach (string d in Linked.Where(d => Directory.Exists(Path.Combine(src.Root, d))))
                LinkFolder(Path.Combine(dest, d), Path.Combine(src.Root, d));
            int copied = 0, skipped = 0;
            foreach (string d in Copied.Where(d => Directory.Exists(Path.Combine(src.Root, d))))
                CopyTree(src.Root, d, dest, modFiles, ref copied, ref skipped);
            log($"stock game from {src.Root}: {string.Join(", ", Linked.Where(d => Directory.Exists(Path.Combine(dest, d))))} linked, " +
                $"{copied} files copied" + (skipped > 0 ? $", {skipped} the source's mods installed left out" : ""));
        }

        /// <summary>The files (relative, '/' separated) CKAN installed into the install.</summary>
        private static HashSet<string> CkanOwnedFiles(string root)
        {
            var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string registry = Path.Combine(root, "CKAN", "registry.json");
            if (!File.Exists(registry))
                return owned;
            using FileStream s = File.OpenRead(registry);
            using JsonDocument doc = JsonDocument.Parse(s);
            if (doc.RootElement.TryGetProperty("installed_files", out JsonElement files) && files.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty f in files.EnumerateObject())
                    owned.Add(f.Name.Replace('\\', '/'));
            return owned;
        }

        private static void CopyTree(string root, string rel, string dest, HashSet<string> skip, ref int copied, ref int skipped)
        {
            string from = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.Combine(dest, rel));
            foreach (string f in Directory.GetFiles(from))
            {
                string relFile = rel + "/" + Path.GetFileName(f);
                if (skip.Contains(relFile))
                {
                    skipped++;
                    continue;
                }

                File.Copy(f, Path.Combine(dest, relFile));
                copied++;
            }

            foreach (string d in Directory.GetDirectories(from))
                CopyTree(root, rel + "/" + Path.GetFileName(d), dest, skip, ref copied, ref skipped);
        }

        /// <summary>
        ///     The source's folder in the instance without copying it: a junction on Windows; on Linux real folders with
        ///     every file a symbolic link (cp -as), since a linked folder would take KSP's KSP_Data/.. to the source.
        /// </summary>
        public static void LinkFolder(string link, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Junction(link, target);
            else
                Run("cp", $"-as \"{Path.GetFullPath(target)}\" \"{link}\"", $"cp -as {target} {link}");
        }

        /// <summary>A directory junction (no admin rights needed, unlike a symbolic link).</summary>
        public static void Junction(string link, string target) =>
            Run("cmd.exe", $"/c mklink /J \"{link.Replace('/', '\\')}\" \"{target.Replace('/', '\\')}\"", $"mklink /J {link} {target}");

        private static void Run(string exe, string arguments, string what)
        {
            var psi = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using Process p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new IOException($"{what} failed: {output.Trim()}");
        }

        /// <summary>
        ///     Deletes a folder and everything in it, removing junctions and symbolic links without following them: an
        ///     instance's GameData/Squad must not take the source's Squad with it.
        /// </summary>
        public static void DeleteTree(string path)
        {
            var dir = new DirectoryInfo(path);
            if (!dir.Exists)
                return;
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                dir.Delete(false);
                return;
            }

            foreach (DirectoryInfo sub in dir.GetDirectories())
                DeleteTree(sub.FullName);
            foreach (FileInfo f in dir.GetFiles())
            {
                if ((f.Attributes & FileAttributes.ReadOnly) != 0)
                    f.Attributes &= ~FileAttributes.ReadOnly;
                f.Delete();
            }

            dir.Delete(false);
        }
    }
}
