using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace KspTest.Runner
{
    /// <summary>Finds KSP installs and CKAN on this machine, to fill in <see cref="UserSettings" /> nobody gave.</summary>
    public static class GameLocator
    {
        private const string SteamAppFolder = "Kerbal Space Program";
        private const string GogGameId = "1429864849";

        /// <summary>KSP installs: Steam's, GOG's, then the ones CKAN knows.</summary>
        public static List<string> FindGames()
        {
            var found = new List<string>();
            foreach (string library in SteamLibraries())
                found.Add(Path.Combine(library, "steamapps", "common", SteamAppFolder));
            if (Gog() is string gog)
                found.Add(gog);
            found.AddRange(CkanInstances());
            return found.Where(g => SafeExists(g)).Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>CKAN executables on the PATH, where ksptest setup downloads it, or in the usual places: suggestions to offer.</summary>
        public static List<string> FindCkan(IEnumerable<string> gameDirs)
        {
            string exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ckan.exe" : "ckan";
            var dirs = new List<string>();
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(d => d.Length > 0));
            dirs.Add(Path.GetDirectoryName(DownloadedCkan)!);
            dirs.AddRange(gameDirs);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dirs.Add(Path.Combine(home, "Downloads"));
            dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CKAN"));
            var found = new List<string>();
            foreach (string d in dirs)
            {
                foreach (string name in new[] { exe, "ckan.exe" }.Distinct())
                {
                    string p;
                    try { p = Path.GetFullPath(Path.Combine(d, name)); }
                    catch (Exception) { continue; }
                    if (File.Exists(p))
                        found.Add(p);
                }
            }

            return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Where ksptest setup puts the ckan.exe it downloads.</summary>
        public static string DownloadedCkan =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KspTest", "ckan", "ckan.exe");

        /// <summary>
        ///     Fills in the game source the settings lack with the first install found; what it filled, for the log.  CKAN is
        ///     never filled in: which ckan.exe (or a download) is the user's choice (<see cref="Setup" />).
        /// </summary>
        public static string? CompleteGameSource(UserSettings s)
        {
            if (s.GameSource != null)
                return null;
            List<string> games = FindGames();
            if (games.Count == 0)
                return null;
            s.GameSource = games[0];
            return $"gameSource = {s.GameSource}" + (games.Count > 1 ? $" (also found: {string.Join(", ", games.Skip(1))})" : "");
        }

        private static IEnumerable<string> SteamLibraries()
        {
            var roots = new List<string>();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                    if (key?.GetValue("SteamPath") is string p)
                        roots.Add(p);
                }
                catch (Exception) { }

                roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            }
            else
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                roots.Add(Path.Combine(home, ".steam", "steam"));
                roots.Add(Path.Combine(home, ".local", "share", "Steam"));
                roots.Add(Path.Combine(home, "Library", "Application Support", "Steam"));
            }

            var libraries = new List<string>();
            foreach (string root in roots.Where(Directory.Exists))
            {
                libraries.Add(root);
                // other libraries: "path"  "D:\\SteamLibrary"
                string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf))
                    continue;
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }

            return libraries.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string? Gog()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return null;
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\WOW6432Node\GOG.com\Games\{GogGameId}");
                return key?.GetValue("path") as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The game instances in CKAN's own config (any install works as a source: only stock files are taken).</summary>
        private static IEnumerable<string> CkanInstances()
        {
            string config = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CKAN", "config.json")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "CKAN", "config.json");
            var paths = new List<string>();
            try
            {
                if (!File.Exists(config))
                    return paths;
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(config));
                if (doc.RootElement.TryGetProperty("GameInstances", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement i in list.EnumerateArray())
                    {
                        string game = i.TryGetProperty("Game", out JsonElement g) ? g.GetString() ?? "KSP" : "KSP";
                        if (game == "KSP" && i.TryGetProperty("Path", out JsonElement p) && p.GetString() is string path)
                            paths.Add(path);
                    }
            }
            catch (Exception) { }

            return paths;
        }

        private static bool SafeExists(string dir)
        {
            try { return new KspInstall(dir).Exists; }
            catch (Exception) { return false; }
        }
    }
}
