using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KspTest.Runner;

namespace KspTest.Tool
{
    /// <summary>
    ///     ksptest run &lt;tests&gt; --install DIR [--save NAME|DIR | --new-game MODE] [--craft FILE|DIR]... [--saves DIR] [--params "k=v;k=v"] [--keep] [--mute] ...
    ///     ksptest list --install DIR
    ///     ksptest deploy --install DIR [--harness DIR] [--tests NAME=DLL[,DLL...]]
    ///     ksptest setup | instance [--mods "A;B"] | instances [delete NAME...|--all]
    ///     ksptest serve REQUEST [k=v]... | serve-plugin PROJECT
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return 0;
            }

            try
            {
                string command = args[0];
                var a = Args.Parse(args.Skip(1).ToArray());
                switch (command)
                {
                    case "run":
                        return Run(a, a.Positional.FirstOrDefault() ?? throw new ArgumentException("which tests? ksptest run <tests>"));
                    case "list":
                        a.Flags.Add("no-reset");
                        return Run(a, "list");
                    case "deploy":
                        return Deploy(a);
                    case "setup":
                        return SetupCommand(a);
                    case "instance":
                    {
                        KspInstall install = InstallFor(a.With("instance"), out List<string>? local);
                        Deployer.DeployHarness(install, a.Get("harness"), Console.WriteLine);
                        Deployer.LocalMods(install, local!, Console.WriteLine);
                        Console.WriteLine(install.Root);
                        return 0;
                    }
                    case "clone":
                    {
                        // ksptest clone --install DIR --clone N [--clone-root DIR]: made or refreshed, without a run
                        string master = a.Required("install", "KSPDIR");
                        string root = a.Get("clone-root") ?? UserSettings.Load().Instances ?? Path.GetDirectoryName(Path.GetFullPath(master).TrimEnd('\\', '/'))!;
                        Console.WriteLine(ThinClone.Ensure(master, ThinClone.PathFor(master, root, a.Int("clone", 1)), Console.WriteLine).Root);
                        return 0;
                    }
                    case "instances":
                        return Instances(a);
                    case "serve":
                        return ServeClient.Request(a);
                    case "serve-plugin":
                        return ServeClient.Plugin(a);
                    default:
                        throw new ArgumentException($"unknown command '{command}'\n{Usage}");
                }
            }
            catch (Exception e) when (e is ArgumentException || e is FileNotFoundException || e is DirectoryNotFoundException ||
                                      e is InvalidOperationException || e is TimeoutException || e is System.Net.Http.HttpRequestException)
            {
                Console.Error.WriteLine("ksptest: " + e.Message);
                return 2;
            }
        }

        private static int Run(Args a, string test)
        {
            KspInstall install = InstallFor(a, out List<string>? local);
            string? harness = a.Get("harness");
            // --clone N: the run plays in thin clone N of the install, beside KSPs in its other clones (ThinClone)
            KspInstall? master = null;
            int clone = a.Int("clone", 0);
            if (clone > 0)
            {
                if (local != null)
                    throw new ArgumentException("--clone clones an --install, not an --instance");
                master = install;
                string root = a.Get("clone-root") ?? UserSettings.Load().Instances ?? Path.GetDirectoryName(master.Root.TrimEnd('\\', '/'))!;
                install = ThinClone.Ensure(master.Root, ThinClone.PathFor(master.Root, root, clone), Console.WriteLine);
            }

            int maxKsps = a.Int("max-ksps", int.TryParse(Environment.GetEnvironmentVariable("KSPTEST_MAX_KSPS"), out int m) ? m : clone > 0 ? 2 : 1);
            var o = new RunOptions
            {
                Test = test,
                Install = install.Root,
                // an instance is brought up to date with the KSP lock held: this tool's harness, the local mods; a clone with
                // its master as it is once the slot is taken (a cache a clone loading before it rebuilt and handed back)
                Deploy = master != null ? i => ThinClone.Refresh(master, i, Console.WriteLine)
                    : local == null ? null : i =>
                    {
                        Deployer.DeployHarness(i, harness, Console.WriteLine);
                        Deployer.LocalMods(i, local, Console.WriteLine);
                    },
                Loaded = master == null ? null : i => ThinClone.SyncBack(i, master, Console.WriteLine),
                MaxKsps = maxKsps,
                SaveFolder = a.Get("save-folder"),
                NoReset = a.Flags.Contains("no-reset"),
                TimeoutSec = a.Int("timeout", 900),
                Keep = a.Flags.Contains("keep"),
                Width = a.Int("width", 1920),
                Height = a.Int("height", 1080),
                ResultsRoot = a.Get("results") ?? "results",
                SaveBackupRoot = a.Get("save-backups"),
                LoadStallSec = a.Int("load-stall", 120),
                LoadRetries = a.Int("load-retries", 2),
                RunStallSec = a.Int("run-stall", 600),
                GcDivisor = a.Int("gc-divisor", 0),
                KspArgs = a.Get("ksp-args"),
                TextureLimit = RunOptions.ParseTextures(a.Get("textures") ?? Environment.GetEnvironmentVariable("KSPTEST_TEXTURES")),
                Scope = a.All("scope").SelectMany(InstanceManager.Split).ToList(),
                Mute = a.Flags.Contains("mute"),
                WaitForOtherKspSec = a.Int("wait", 7200),
                LockFile = a.Flags.Contains("no-lock") ? null : a.Get("lock") ?? DefaultLockFile,
            }.WithParams(a.Get("params"));

            if (a.Get("new-game") is string mode)
                o.NewGame = mode;   // no template: an emptied save the harness starts a new game in
            else if (!o.NoReset)
            {
                // a template folder, or its name under --saves (default ./saves)
                string save = a.Get("save") ?? "stock-sandbox";
                o.SaveTemplate = Directory.Exists(save) ? save : Path.Combine(a.Get("saves") ?? "saves", save);
            }

            o.Crafts.AddRange(a.All("craft"));

            RunResult r = KspRunner.Run(o);
            Console.WriteLine();
            Console.Write(r.Report());
            return r.Passed ? 0 : 1;
        }

        private static int Deploy(Args a)
        {
            KspInstall install = InstallFor(a, out List<string>? local);
            if (!install.Exists)
                throw new FileNotFoundException($"no KSP executable under {install.Root}");
            if (install.Running().Count > 0)
                throw new InvalidOperationException($"KSP is running from {install.Root}; plugins load at startup, deploy after it exits");
            // NAME=path.dll[,path.dll]: GameData/KspTest gets the harness and these, and nothing else
            var tests = a.All("tests").Select(spec =>
            {
                int eq = spec.IndexOf('=');
                string name = eq > 0 ? spec.Substring(0, eq) : Path.GetFileNameWithoutExtension(spec);
                return new KeyValuePair<string, List<string>>(name, (eq > 0 ? spec.Substring(eq + 1) : spec).Split(',').ToList());
            }).ToList();
            if (local != null)
                Deployer.LocalMods(install, local, Console.WriteLine);
            Deployer.DeployRun(install, a.Get("harness"), tests, Console.WriteLine);
            Console.WriteLine($"done: {install.Root}");
            return 0;
        }

        /// <summary>
        ///     --install (or $KSPDIR), or with --instance the KSP instance for --mods and the --overlay folders (made if
        ///     needed); for an instance, local is its local mods (the --overlay folders and the --mods that are paths), for the
        ///     caller to copy in with the harness, else null.
        /// </summary>
        private static KspInstall InstallFor(Args a, out List<string>? local)
        {
            local = null;
            if (!a.Flags.Contains("instance"))
                return new KspInstall(a.Required("install", "KSPDIR"));
            UserSettings s = UserSettings.Load();
            if (!s.Complete)
                throw new ArgumentException($"KspTest is not set up to make instances ({UserSettings.FilePath}): run ksptest setup");
            List<string> mods = Deployer.HarnessMods.Concat(a.All("mods").SelectMany(InstanceManager.Split)).Concat(a.All("overlay")).ToList();
            local = mods.Where(InstanceManager.IsLocal).ToList();
            return new InstanceManager(s).Ensure(mods, Console.WriteLine);
        }

        private static int Instances(Args a)
        {
            UserSettings s = UserSettings.Load();
            if (s.Instances == null)
                throw new ArgumentException("no instances folder set: run ksptest setup");
            var manager = new InstanceManager(s);
            if (a.Positional.FirstOrDefault() == "share-burst")
            {
                // every instance's KSPBurst compiler package a junction to one shared copy (BurstShare)
                long freed = 0;
                foreach (InstanceInfo i in manager.List().Where(i => !i.Name.Contains('@')))
                    freed += BurstShare.Share(Path.Combine(manager.Root, i.Name), manager.Root, Console.WriteLine);
                Console.WriteLine($"{freed / 1073741824.0:F1} GB freed");
                return 0;
            }
            if (a.Positional.FirstOrDefault() == "prune")
            {
                // instances unused for --days (default 14) and their clones: listed, deleted with --delete
                double days = double.Parse(a.Get("days") ?? "14", System.Globalization.CultureInfo.InvariantCulture);
                List<string> unused = manager.Unused(days);
                Console.WriteLine($"{unused.Count} instance(s) unused for {days:F0} days in {manager.Root}:");
                foreach (string name in unused)
                    Console.WriteLine("  " + name);
                if (a.Flags.Contains("delete"))
                    foreach (string name in unused)
                    {
                        try { manager.Delete(name, Console.WriteLine); }
                        catch (Exception e) { Console.WriteLine($"  {name}: not deleted: {e.Message}"); }
                    }
                return 0;
            }
            if (a.Positional.FirstOrDefault() == "delete")
            {
                IEnumerable<string> names = a.Flags.Contains("all") ? manager.List().Select(i => i.Name) : a.Positional.Skip(1);
                foreach (string name in names)
                    manager.Delete(name, Console.WriteLine);
                return 0;
            }

            Console.WriteLine($"instances in {manager.Root}:");
            foreach (InstanceInfo i in manager.List())
                Console.WriteLine($"  {i.Name,-50} {(i.Ready ? $"last used {i.LastUsed:yyyy-MM-dd HH:mm}" : "unfinished")}" +
                                  (i.Installed.Count > 0 ? "\n      " + string.Join(", ", i.Installed.Select(kv => $"{kv.Key} {kv.Value}")) : ""));
            return 0;
        }

        /// <summary>
        ///     Asks which KSP install to copy the stock game from, whether to use an existing ckan.exe or download the latest,
        ///     and where the instances go; --game, --ckan PATH or --download-ckan, and --instances DIR|default answer them
        ///     without asking.
        /// </summary>
        private static int SetupCommand(Args a)
        {
            UserSettings file = UserSettings.LoadFile();
            Console.WriteLine($"KspTest settings: {UserSettings.FilePath}");
            Console.WriteLine("Tests run in KSP instances KspTest makes from your KSP install, with CKAN installing the mods each needs.\n");

            List<string> games = GameLocator.FindGames();
            string game = a.Get("game") ?? Choose("KSP install to copy the stock game from (any install: its mods are not copied)",
                games.Select(g => (g, g)).ToList(), file.GameSource ?? games.FirstOrDefault(), "another install (type its folder)",
                g => new KspInstall(g).Exists ? null : $"no KSP executable in {g}");

            string? ckan = a.Get("ckan");
            if (ckan == null && !a.Flags.Contains("download-ckan"))
            {
                var options = new List<(string, string)> { ($"download the latest ckan.exe to {GameLocator.DownloadedCkan}", "download") };
                options.AddRange(GameLocator.FindCkan(new[] { game }).Select(c => ($"use {c}", c)));
                string answer = Choose("CKAN, which installs each instance's mods (it shares your CKAN download cache)", options, file.Ckan,
                    "another ckan.exe (type its path)", c => c == "download" || File.Exists(c) ? null : $"no file at {c}");
                ckan = answer == "download" ? null : answer;
            }
            else if (ckan != null && !File.Exists(ckan))
                throw new ArgumentException($"no file at {ckan}");

            string? instances = a.Get("instances");
            if (instances == "default")
                instances = UserSettings.DefaultInstances;
            instances ??= Choose("Where the instances go (tens of MB each plus their mods: a few MB for FAR, GBs for RP-1)",
                new List<(string, string)> { ($"the default folder: {UserSettings.DefaultInstances}", UserSettings.DefaultInstances) },
                file.Instances, "another folder (type its path)", _ => null);

            Setup.Apply(file, new Setup.Answers { GameSource = Path.GetFullPath(game), CkanPath = ckan == null ? null : Path.GetFullPath(ckan),
                Instances = Path.GetFullPath(instances) }, Console.WriteLine);
            return 0;
        }

        /// <summary>A numbered choice, or a path typed in; Enter keeps the current value.  Without a console only the current value will do.</summary>
        private static string Choose(string question, List<(string Label, string Value)> options, string? current, string other,
            Func<string, string?> check)
        {
            Console.WriteLine(question);
            for (int i = 0; i < options.Count; i++)
                Console.WriteLine($"  {i + 1}) {options[i].Label}");
            Console.WriteLine($"  or {other}");
            if (Console.IsInputRedirected)
            {
                if (current == null)
                    throw new ArgumentException("no console to ask on: answer with --game DIR, --ckan PATH or --download-ckan, and --instances DIR|default");
                Console.WriteLine($"  -> {current}\n");
                return current;
            }

            while (true)
            {
                Console.Write(current != null ? $"choice [{current}]: " : "choice: ");
                string input = (Console.ReadLine() ?? "").Trim().Trim('"');
                string? value = input.Length == 0 ? current
                    : int.TryParse(input, out int n) && n >= 1 && n <= options.Count ? options[n - 1].Value
                    : input;
                if (value == null)
                    continue;
                if (check(value) is string problem)
                {
                    Console.WriteLine("  " + problem);
                    continue;
                }

                Console.WriteLine();
                return value;
            }
        }

        /// <summary>The lock every KSP launcher on this machine shares: $KSPTEST_LOCK_FILE, or one per user.</summary>
        public static string DefaultLockFile =>
            Environment.GetEnvironmentVariable("KSPTEST_LOCK_FILE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KspTest", "ksp.lock");

        private const string Usage = @"ksptest - run KspTest in-game tests

  ksptest run <tests> --install DIR [options]   tests: names, comma separated, wildcards allowed, or 'all'
      --save NAME|DIR        save template copied fresh into the run's save (a folder, or its name under --saves)
      --saves DIR            where named templates are (default ./saves)
      --new-game MODE        no template: a new sandbox, science or career game
      --save-folder NAME     the install's save folder to play in (default ksptest-TEST, or ksptest-FIRST+N-HASH for several)
      --craft FILE|DIR       crafts put into the save's Ships/VAB or SPH (repeatable)
      --params ""k=v;k=v""     test parameters
      --timeout SEC          a test's when it sets none (900)      --keep        leave KSP open afterwards
      --mute                 master volume 0 for the run            --no-reset    play in the run's save as it is
      --results DIR          (default ./results)                    --width/--height  window size
      --lock FILE            the shared KSP lock (default $KSPTEST_LOCK_FILE or one per user); --no-lock
      --wait SEC             how long to wait for other KSP runs (default 7200)
      --load-stall/--load-retries/--run-stall SEC, --gc-divisor N, --ksp-args ""ARGS"" (more arguments for KSP, e.g. -batchmode)
      --textures low|full|0-3  low-res textures outside screenshots (~6 GiB less on RP-1; default $KSPTEST_TEXTURES or full)
      --clone N              play in thin clone N of --install (made or refreshed: junctions, private settings/saves/caches),
                             beside KSPs in the other clones; --clone-root DIR (default the instances folder)
      --max-ksps N           KSPs at once, each in its own install (default $KSPTEST_MAX_KSPS, 2 with --clone, else 1)
      --scope ""A;B""          the run's own test assemblies: what all runs, and where short names are looked up first
  ksptest list --install DIR                     the tests deployed in that install
  ksptest deploy --install DIR [--harness DIR] [--tests NAME=a.dll[,b.dll]]...
      the harness (bundled with this tool unless --harness) and these test assemblies into GameData/KspTest/Plugins;
      any other assembly under GameData/KspTest is removed

  --install defaults to $KSPDIR. Instead of --install, run/list/deploy take --instance [--mods ""A;B""] [--overlay DIR]...:
      the KSP instance KspTest makes with those mods: CKAN identifiers, and local mods (the path of a GameData/ModName
      folder, in --mods or as --overlay: built from source, over CKAN's copy if any, or not on CKAN), which are part of
      what the instance is made for, as CKAN's are, and are copied in for each run.

  ksptest setup [--game DIR] [--ckan PATH | --download-ckan] [--instances DIR|default]
      choose the KSP install the instances' stock game is copied from, the CKAN that installs their mods (an existing
      ckan.exe, or the latest downloaded) and the folder they go in; asks for what the options do not answer
  ksptest instance [--mods ""A;B""] [--overlay DIR]...   make (or find) the instance with those mods; prints its folder
  ksptest instances                              list them
  ksptest instances delete NAME... | --all       delete them (the KSP install they link to stays)

  ksptest run Serve ... [--params ""craft=NAME;port=8642""] --timeout 43200
      a KSP session that stays up answering requests (ServeSession): craft loaded into its editor first, if given
  ksptest serve REQUEST [k=v]... [--port 8642] [--timeout 1800]
      one request to the Serve session, its JSON answer printed (exit 1 on an error answer); serve help lists them
  ksptest serve-plugin PROJECT.csproj [--port 8642] [--build-args ""-p:KspDir=DIR ...""]
      builds a project of [ServeCommand] methods under a fresh assembly name (the session can't load one name twice) and
      loads it into the Serve session: new or changed requests without a game restart

  Exit code 0 when the tests pass or are skipped.";
    }

    internal sealed class Args
    {
        public readonly List<string> Positional = new List<string>();
        public readonly HashSet<string> Flags = new HashSet<string>();
        private readonly List<(string Key, string Value)> _values = new List<(string, string)>();

        private static readonly HashSet<string> FlagNames = new HashSet<string> { "keep", "mute", "no-reset", "no-lock", "instance", "all", "download-ckan" };

        public Args With(string flag)
        {
            Flags.Add(flag);
            return this;
        }

        public static Args Parse(string[] args)
        {
            var a = new Args();
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--"))
                {
                    a.Positional.Add(args[i]);
                    continue;
                }

                string key = args[i].Substring(2);
                int eq = key.IndexOf('=');
                if (eq > 0)
                    a._values.Add((key.Substring(0, eq), key.Substring(eq + 1)));
                else if (FlagNames.Contains(key))
                    a.Flags.Add(key);
                else if (i + 1 < args.Length)
                    a._values.Add((key, args[++i]));
                else
                    throw new ArgumentException($"--{key} needs a value");
            }

            return a;
        }

        public string? Get(string key) => _values.LastOrDefault(v => v.Key == key).Value;
        public IEnumerable<string> All(string key) => _values.Where(v => v.Key == key).Select(v => v.Value);
        public int Int(string key, int fallback) => Get(key) is string s ? int.Parse(s) : fallback;

        public string Required(string key, string envFallback) =>
            Get(key) ?? Environment.GetEnvironmentVariable(envFallback) ?? throw new ArgumentException($"--{key} is needed (or ${envFallback})");
    }
}
