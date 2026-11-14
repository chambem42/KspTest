using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace KspTest.Runner
{
    /// <summary>
    ///     A mod from a git repository at a commit, given in a mods list as
    ///     <c>https://host/owner/repo@commit/GameData/ModName[?build=path/to/Project.csproj]</c>: the repository checked
    ///     out at that commit (once, in the sources folder beside the instances), its GameData/ModName folder a local mod
    ///     like any other (<see cref="InstanceManager.IsLocal" />; its instance counts it by its folder's name). With
    ///     build, the project is built (dotnet build, Release) before the first run that needs it, against the instance
    ///     it goes into: its KSP_Data/Managed and every DLL in its GameData flat in one folder, given as RefsDir,
    ///     ReferencePath and KSPRoot (a project that takes its references by any of those). Pinned to a commit, a run
    ///     has what it says, wherever it runs.
    /// </summary>
    public static class RepoMod
    {
        private static readonly Regex Spec = new Regex(
            @"^(?<url>https?://[^@\s?]+?)@(?<commit>[0-9a-fA-F]{7,40})/(?<path>[^?\s]+?)/*(?:\?build=(?<build>\S+))?$");

        // a checkout's marker (its url, commit and project to build) and the stamp a finished build leaves
        private const string Marker = ".ksptest-repo", Built = ".ksptest-built";

        private static readonly Dictionary<string, string> Resolved = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Whether the mod is given as a repository at a commit.</summary>
        public static bool Is(string mod) => Spec.IsMatch(mod.Trim());

        /// <summary>The mod's name, as GameData has it: its folder's.</summary>
        public static string Name(string mod) =>
            Path.GetFileName(Spec.Match(mod.Trim()).Groups["path"].Value.TrimEnd('/'));

        /// <summary>
        ///     The sources folder (KSPTEST_SOURCES, else "sources" beside the instances folder): one checkout per
        ///     repository and commit.
        /// </summary>
        public static string SourcesRoot()
        {
            if (Environment.GetEnvironmentVariable("KSPTEST_SOURCES") is string s && s.Length > 0)
                return Path.GetFullPath(s);
            string instances = UserSettings.Load().Instances ?? UserSettings.DefaultInstances;
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(instances).TrimEnd('/', '\\'))!, "sources");
        }

        /// <summary>
        ///     The mod's folder in its checkout (made if there is none: cloned and checked out at the commit). Its build,
        ///     if it has one, comes later (<see cref="EnsureBuilt" />), against the instance it goes into.
        /// </summary>
        public static string Folder(string mod, Action<string>? log = null)
        {
            mod = mod.Trim();
            lock (Resolved)
                if (Resolved.TryGetValue(mod, out string? known))
                    return known;
            Match m = Spec.Match(mod);
            if (!m.Success)
                throw new ArgumentException($"not a repository mod: {mod} (https://host/owner/repo@commit/GameData/ModName[?build=Project.csproj])");
            string url = m.Groups["url"].Value, commit = m.Groups["commit"].Value.ToLowerInvariant();
            string path = m.Groups["path"].Value.TrimEnd('/'), build = m.Groups["build"].Value;
            string repo = Path.GetFileNameWithoutExtension(url.TrimEnd('/'));
            string root = SourcesRoot();
            string dir = Path.Combine(root, $"{repo}@{commit}");
            Directory.CreateDirectory(root);
            log ??= Console.WriteLine;
            using (FileMutex.Acquire(dir + ".lock", $"another process checking out {repo}@{commit}", TimeSpan.FromMinutes(30), log))
            {
                if (!File.Exists(Path.Combine(dir, Marker)))
                {
                    string tmp = dir + ".tmp";
                    if (Directory.Exists(tmp))
                        StockBase.DeleteTree(tmp);
                    log($"{repo}@{commit}: cloning {url}");
                    Git(log, null, "clone", "--quiet", "--no-checkout", "--filter=blob:none", url, tmp);
                    Git(log, tmp, "-c", "advice.detachedHead=false", "checkout", "--quiet", commit);
                    File.WriteAllLines(Path.Combine(tmp, Marker), new[] { url, Git(log, tmp, "rev-parse", "HEAD").Trim(), build });
                    if (Directory.Exists(dir))
                        StockBase.DeleteTree(dir);
                    Directory.Move(tmp, dir);
                }
            }
            string folder = Path.GetFullPath(Path.Combine(dir, path));
            lock (Resolved)
                Resolved[mod] = folder;
            return folder;
        }

        /// <summary>
        ///     A local mod's folder in a repository checkout with a project to build: built against the install it is going
        ///     into, unless it has been already. Any other folder: nothing done.
        /// </summary>
        public static void EnsureBuilt(string folder, KspInstall install, Action<string> log)
        {
            string? dir = CheckoutOf(folder);
            if (dir == null)
                return;
            string[] marker = File.ReadAllLines(Path.Combine(dir, Marker));
            string build = marker.Length > 2 ? marker[2].Trim() : "";
            if (build.Length == 0 || File.Exists(Path.Combine(dir, Built)))
                return;
            string name = Path.GetFileName(dir);
            using (FileMutex.Acquire(dir + ".lock", $"another process building {name}", TimeSpan.FromMinutes(30), log))
            {
                if (File.Exists(Path.Combine(dir, Built)))
                    return;
                string refs = Path.Combine(dir, ".ksptest-refs");
                int n = Refs(install, refs, Path.GetFullPath(folder));
                log($"{name}: building {build} against {install.Root} ({n} references)");
                string sep = Path.DirectorySeparatorChar.ToString();
                var r = Run(log, dir, "dotnet", "build", Path.Combine(dir, build), "-c", "Release", "-nologo", "-v", "q",
                    $"-p:RefsDir={refs}{sep}", $"-p:ReferencePath={refs}", $"-p:KSPRoot={install.Root}");
                if (r.code != 0)
                    throw new InvalidOperationException($"{name}: building {build} failed (exit {r.code}):\n" +
                                                        string.Join("\n", r.output.Where(l => l.Contains("error")).Take(10)));
                if (!Directory.Exists(folder))
                    throw new DirectoryNotFoundException($"{name}: {build} built, but there is no {folder}");
                File.WriteAllText(Path.Combine(dir, Built), DateTime.UtcNow.ToString("o"));
            }
        }

        /// <summary>The checkout a folder is in (its marker's directory), or null.</summary>
        private static string? CheckoutOf(string folder)
        {
            string root = SourcesRoot();
            for (string? d = Path.GetFullPath(folder); d != null && d.Length > root.Length; d = Path.GetDirectoryName(d))
                if (File.Exists(Path.Combine(d, Marker)))
                    return d;
            return null;
        }

        // the install's Managed assemblies and its GameData's DLLs (the mod's own aside, KspTest's harness aside), flat
        private static int Refs(KspInstall install, string refs, string own)
        {
            if (Directory.Exists(refs))
                StockBase.DeleteTree(refs);
            Directory.CreateDirectory(refs);
            string? managed = new[] { "KSP_x64_Data", "KSP_Data", Path.Combine("KSP.app", "Contents", "Resources", "Data") }
                .Select(d => Path.Combine(install.Root, d, "Managed")).FirstOrDefault(Directory.Exists);
            if (managed == null)
                throw new DirectoryNotFoundException($"no Managed folder in {install.Root}");
            var dlls = Directory.EnumerateFiles(managed, "*.dll")
                .Concat(Directory.EnumerateFiles(install.GameData, "*.dll", SearchOption.AllDirectories)
                    .Where(f => !f.StartsWith(own + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                                !f.StartsWith(install.HarnessDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
            int n = 0;
            foreach (string f in dlls)
            {
                string to = Path.Combine(refs, Path.GetFileName(f));
                if (File.Exists(to))
                    continue;   // the first of a name (Managed's before a mod's copy of it)
                File.Copy(f, to);
                n++;
            }
            return n;
        }

        private static string Git(Action<string> log, string? cwd, params string[] args)
        {
            var r = Run(log, cwd, "git", args);
            if (r.code != 0)
                throw new InvalidOperationException($"git {string.Join(" ", args)} failed (exit {r.code}): " +
                                                    string.Join("\n", r.output.Skip(Math.Max(0, r.output.Count - 5))));
            return string.Join("\n", r.output);
        }

        private static (int code, List<string> output) Run(Action<string> log, string? cwd, string exe, params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = string.Join(" ", args.Select(Quote)),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (cwd != null)
                psi.WorkingDirectory = cwd;
            var output = new List<string>();
            using var p = new Process { StartInfo = psi };
            DataReceivedEventHandler onLine = (_, e) =>
            {
                if (e.Data == null)
                    return;
                lock (output)
                    output.Add(e.Data);
            };
            p.OutputDataReceived += onLine;
            p.ErrorDataReceived += onLine;
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            return (p.ExitCode, output);
        }

        private static string Quote(string a) => a.Length > 0 && a.IndexOfAny(new[] { ' ', '"', '\t' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
    }
}
