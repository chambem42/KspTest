using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KspTest.Runner;

namespace KspTest.Tool
{
    /// <summary>
    ///     ksptest run &lt;tests&gt; --install DIR [--save NAME|DIR] [--saves DIR] [--params "k=v;k=v"] [--keep] [--mute] ...
    ///     ksptest list --install DIR
    ///     ksptest deploy --install DIR [--harness DIR] [--tests NAME=DLL[,DLL...]]
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
                    default:
                        throw new ArgumentException($"unknown command '{command}'\n{Usage}");
                }
            }
            catch (Exception e) when (e is ArgumentException || e is FileNotFoundException || e is DirectoryNotFoundException ||
                                      e is InvalidOperationException || e is TimeoutException)
            {
                Console.Error.WriteLine("ksptest: " + e.Message);
                return 2;
            }
        }

        private static int Run(Args a, string test)
        {
            var o = new RunOptions
            {
                Test = test,
                Install = a.Required("install", "KSPDIR"),
                SaveFolder = a.Get("save-folder") ?? "ksptest",
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
                Mute = a.Flags.Contains("mute"),
                WaitForOtherKspSec = a.Int("wait", 7200),
                LockFile = a.Flags.Contains("no-lock") ? null : a.Get("lock") ?? DefaultLockFile,
            }.WithParams(a.Get("params"));

            if (!o.NoReset)
            {
                // a template folder, or its name under --saves (default ./saves)
                string save = a.Get("save") ?? "stock-sandbox";
                o.SaveTemplate = Directory.Exists(save) ? save : Path.Combine(a.Get("saves") ?? "saves", save);
            }

            RunResult r = KspRunner.Run(o);
            Console.WriteLine();
            Console.Write(r.Report());
            return r.Passed ? 0 : 1;
        }

        private static int Deploy(Args a)
        {
            var install = new KspInstall(a.Required("install", "KSPDIR"));
            if (!install.Exists)
                throw new FileNotFoundException($"no KSP executable under {install.Root}");
            if (install.Running().Count > 0)
                throw new InvalidOperationException($"KSP is running from {install.Root}; plugins load at startup, deploy after it exits");
            Deployer.DeployHarness(install, a.Get("harness"), Console.WriteLine);
            foreach (string spec in a.All("tests"))
            {
                // NAME=path.dll[,path.dll]
                int eq = spec.IndexOf('=');
                string name = eq > 0 ? spec.Substring(0, eq) : Path.GetFileNameWithoutExtension(spec);
                string[] dlls = (eq > 0 ? spec.Substring(eq + 1) : spec).Split(',');
                Deployer.DeployTests(install, name, dlls, Console.WriteLine);
            }

            Console.WriteLine($"done: {install.Root}");
            return 0;
        }

        /// <summary>The lock every KSP launcher on this machine shares: $KSPTEST_LOCK_FILE, or one per user.</summary>
        public static string DefaultLockFile =>
            Environment.GetEnvironmentVariable("KSPTEST_LOCK_FILE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KspTest", "ksp.lock");

        private const string Usage = @"ksptest - run KspTest in-game tests

  ksptest run <tests> --install DIR [options]   tests: names, comma separated, wildcards allowed, or 'all'
      --save NAME|DIR        save template copied fresh into saves/ksptest (a folder, or its name under --saves)
      --saves DIR            where named templates are (default ./saves)
      --params ""k=v;k=v""     test parameters
      --timeout SEC          the harness's budget (default 900)     --keep        leave KSP open afterwards
      --mute                 master volume 0 for the run            --no-reset    play in saves/ksptest as it is
      --results DIR          (default ./results)                    --width/--height  window size
      --lock FILE            the shared KSP lock (default $KSPTEST_LOCK_FILE or one per user); --no-lock
      --wait SEC             how long to wait for other KSP runs (default 7200)
      --load-stall/--load-retries/--run-stall SEC, --gc-divisor N
  ksptest list --install DIR                     the tests deployed in that install
  ksptest deploy --install DIR [--harness DIR] [--tests NAME=a.dll[,b.dll]]...
      the harness (bundled with this tool unless --harness), and test assemblies into GameData/KspTest/Tests/NAME

  --install defaults to $KSPDIR. Exit code 0 when the tests pass or are skipped.";
    }

    internal sealed class Args
    {
        public readonly List<string> Positional = new List<string>();
        public readonly HashSet<string> Flags = new HashSet<string>();
        private readonly List<(string Key, string Value)> _values = new List<(string, string)>();

        private static readonly HashSet<string> FlagNames = new HashSet<string> { "keep", "mute", "no-reset", "no-lock" };

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
