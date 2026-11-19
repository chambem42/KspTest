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
    ///     <c>https://host/owner/repo@commit/GameData/ModName[?build=path/to/Project.csproj][&amp;over]</c> (or file:///path/to/repo@...,
    ///     a local one): the repository
    ///     checked out at that commit (once, in the sources folder beside the instances), its GameData/ModName folder a
    ///     local mod like any other (<see cref="InstanceManager.IsLocal" />; its instance counts it by its folder's name).
    ///     With build, the project is built before the first run that needs it, against the instance it goes into: its
    ///     KSP_Data/Managed and every DLL in its GameData flat in one folder, given as RefsDir, ReferencePath and KSPRoot
    ///     (a project that takes its references by any of those). An SDK-style project builds with dotnet build; a
    ///     solution, solution filter or older project (packages.config and all) with MSBuild, as the mod's own CI would
    ///     (<see cref="MsBuild" />). With over, the folder goes in over the copy CKAN installed instead of replacing it:
    ///     the repository's files and the build's DLLs on top, the rest of CKAN's kept (a release can ship files its
    ///     repository does not have). Pinned to a commit, a run has what it says, wherever it runs.
    /// </summary>
    public static class RepoMod
    {
        private static readonly Regex Spec = new Regex(
            @"^(?<url>(?:https?|file)://[^@\s?]+?)@(?<commit>[0-9a-fA-F]{7,40})/(?<path>[^?\s]+?)/*(?:\?(?<query>\S+))?$");

        // a checkout's marker (its url, commit, project to build and whether it goes over CKAN's copy) and the stamp a
        // finished build leaves
        private const string Marker = ".ksptest-repo", Built = ".ksptest-built";

        // the spec's options: build=Project and over, & separated
        private static (string build, bool over) Options(Match m)
        {
            string build = "";
            bool over = false;
            foreach (string kv in m.Groups["query"].Value.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = kv.IndexOf('=');
                string k = eq < 0 ? kv : kv.Substring(0, eq), v = eq < 0 ? "" : kv.Substring(eq + 1);
                if (k == "build") build = v;
                else if (k == "over") over = v.Length == 0 || v == "true" || v == "1";
                else throw new ArgumentException($"unknown option {k} in {m.Value} (build=Project, over)");
            }
            return (build, over);
        }

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
                throw new ArgumentException($"not a repository mod: {mod} (https://host/owner/repo@commit/GameData/ModName[?build=Project.csproj][&over])");
            string url = m.Groups["url"].Value, commit = m.Groups["commit"].Value.ToLowerInvariant();
            string path = m.Groups["path"].Value.TrimEnd('/');
            (string build, bool over) = Options(m);
            string repo = Path.GetFileNameWithoutExtension(url.TrimEnd('/'));
            string root = SourcesRoot();
            // no "@" in the folder's name: MSBuild escapes it (%40) in the paths of tasks a build loads from packages
            string dir = Path.Combine(root, $"{repo}-{commit}");
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
                    File.WriteAllLines(Path.Combine(tmp, Marker), new[] { url, Git(log, tmp, "rev-parse", "HEAD").Trim(), build, over ? "over" : "" });
                    if (Directory.Exists(dir))
                        StockBase.DeleteTree(dir);
                    Directory.Move(tmp, dir);
                }
                else
                {
                    // the same commit asked for with other options (the checkout is per commit): the marker says these
                    string[] was = File.ReadAllLines(Path.Combine(dir, Marker));
                    string[] now = { was[0], was[1], build, over ? "over" : "" };
                    if (!was.SequenceEqual(now))
                    {
                        File.WriteAllLines(Path.Combine(dir, Marker), now);
                        if ((was.Length > 2 ? was[2] : "") != build)
                            File.Delete(Path.Combine(dir, Built));
                    }
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
                string project = Path.Combine(dir, build);
                bool msbuild = NeedsMsBuild(project);
                log($"{name}: building {build} against {install.Root} ({n} references{(msbuild ? ", MSBuild" : "")})");
                string sep = Path.DirectorySeparatorChar.ToString();
                string[] props = { $"-p:RefsDir={refs}{sep}", $"-p:ReferencePath={refs}{sep}", $"-p:KSPRoot={install.Root}" };
                var r = msbuild
                    ? Run(log, dir, MsBuild(), new[] { project, "-t:Build", "-restore", "-p:RestorePackagesConfig=true", "-p:Configuration=Release", "-nodeReuse:false",
                        "-nologo", "-v:q" }.Concat(props).ToArray())
                    : Run(log, dir, "dotnet", new[] { "build", project, "-c", "Release", "-nologo", "-v", "q" }.Concat(props).ToArray());
                if (r.code != 0)
                    throw new InvalidOperationException($"{name}: building {build} failed (exit {r.code}):\n" +
                                                        string.Join("\n", r.output.Where(l => l.Contains("error")).Take(10)));
                if (!Directory.Exists(folder))
                    throw new DirectoryNotFoundException($"{name}: {build} built, but there is no {folder}");
                File.WriteAllText(Path.Combine(dir, Built), DateTime.UtcNow.ToString("o"));
            }
        }

        /// <summary>
        ///     Whether a project needs MSBuild rather than dotnet build: a solution or solution filter (whose projects may
        ///     be older ones), or a project that is not SDK-style (packages.config, a .NET Framework reference assembly
        ///     build, as many mods' are).
        /// </summary>
        private static bool NeedsMsBuild(string project)
        {
            string ext = Path.GetExtension(project).ToLowerInvariant();
            if (ext == ".sln" || ext == ".slnf")
                return true;
            if (!File.Exists(project))
                throw new FileNotFoundException($"no project to build at {project}");
            string head = File.ReadAllText(project);
            return !Regex.IsMatch(head, @"<Project\s[^>]*\bSdk\s*=", RegexOptions.IgnoreCase) &&
                   !Regex.IsMatch(head, @"<Sdk\s+Name\s*=", RegexOptions.IgnoreCase);
        }

        /// <summary>
        ///     MSBuild: KSPTEST_MSBUILD, else Visual Studio's (vswhere finds it, Build Tools or any edition), else msbuild on
        ///     the path (Mono's, on Linux).
        /// </summary>
        public static string MsBuild()
        {
            if (Environment.GetEnvironmentVariable("KSPTEST_MSBUILD") is string env && env.Length > 0)
                return env;
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string vswhere = Path.Combine(pf86.Length > 0 ? pf86 : "/nonexistent", "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (File.Exists(vswhere))
            {
                var r = Run(_ => { }, null, vswhere, "-latest", "-prerelease", "-products", "*", "-requires", "Microsoft.Component.MSBuild",
                    "-find", @"MSBuild\**\Bin\MSBuild.exe");
                string? found = r.output.Select(l => l.Trim()).FirstOrDefault(File.Exists);
                if (r.code == 0 && found != null)
                    return found;
            }
            return "msbuild";
        }

        /// <summary>
        ///     The folder to put in GameData for a local mod: the folder itself, or for a repository mod with over, CKAN's
        ///     copy (kept in KspTest-pristine the first time, as an overlay keeps it) with the folder's files from the
        ///     repository and the DLLs the build put in it on top, in a folder of the same name. A DLL the build copied in
        ///     that another mod in the install has (a reference) is left out.
        /// </summary>
        public static string Staged(string folder, KspInstall install, Action<string> log)
        {
            string? dir = CheckoutOf(folder);
            if (dir == null)
                return folder;
            string[] marker = File.ReadAllLines(Path.Combine(dir, Marker));
            if (marker.Length < 4 || marker[3].Trim() != "over")
                return folder;

            folder = Path.GetFullPath(folder).TrimEnd('/', '\\');
            string name = Path.GetFileName(folder);
            string target = Path.Combine(install.GameData, name);
            string pristine = Path.Combine(install.Root, "KspTest-pristine", name);
            if (Directory.Exists(target) && !Directory.Exists(pristine))
                Deployer.CopyDir(target, pristine);
            string stage = Path.Combine(dir, ".ksptest-over", name);
            int copied = 0, removed = 0;
            if (Directory.Exists(pristine))
                Deployer.Mirror(pristine, stage, ref copied, ref removed);
            else
            {
                if (Directory.Exists(stage))
                    StockBase.DeleteTree(stage);
                Directory.CreateDirectory(stage);
                log($"{name}: no copy from CKAN to go over (is {name} among the mods?): the repository's alone");
            }

            string rel = folder.Substring(dir.Length).TrimStart('/', '\\').Replace('\\', '/');
            var tracked = new HashSet<string>(
                Git(log, dir, "ls-files", "-z", "--", rel).Split('\0').Where(l => l.Length > 0)
                    .Select(l => Path.GetFullPath(Path.Combine(dir, l))),
                StringComparer.OrdinalIgnoreCase);
            var others = new HashSet<string>(
                Directory.EnumerateFiles(install.GameData, "*.dll", SearchOption.AllDirectories)
                    .Where(f => !f.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                                !f.StartsWith(install.HarnessDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    .Select(Path.GetFileName),
                StringComparer.OrdinalIgnoreCase);
            int over = 0;
            foreach (string f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                bool dll = f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
                if (!tracked.Contains(Path.GetFullPath(f)) && !(dll && !others.Contains(Path.GetFileName(f))))
                    continue;
                string to = Path.Combine(stage, f.Substring(folder.Length).TrimStart('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to, true);
                over++;
            }
            log($"{name}: CKAN's copy with {over} files from {Path.GetFileName(dir)} over it");
            return stage;
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
