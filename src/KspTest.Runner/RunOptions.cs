using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace KspTest.Runner
{
    /// <summary>What to run and how: the same knobs run-test.ps1 had.</summary>
    public sealed class RunOptions
    {
        /// <summary>Test names, comma separated, wildcards allowed; "list" discovers the tests deployed; "all" runs them all.</summary>
        public string Test = "";

        /// <summary>The KSP install (its root folder).</summary>
        public string Install = "";

        /// <summary>The run's own test assemblies (by name): what "all" runs, and where a short test name is looked up first.</summary>
        public List<string> Scope = new List<string>();

        /// <summary>Puts what the run needs into the install, once this run holds the KSP lock (so no other run is using it).</summary>
        public Action<KspInstall>? Deploy;

        /// <summary>
        ///     Save template folder copied fresh into the install's saves/&lt;SaveFolder&gt; before the run; null: the folder
        ///     emptied, and the harness starts a NewGame there.
        /// </summary>
        public string? SaveTemplate;

        /// <summary>The mode of the game started without a SaveTemplate: sandbox, science or career.</summary>
        public string NewGame = "sandbox";

        /// <summary>Crafts (.craft files, or folders of them) put into the save's Ships/VAB or SPH after the reset.</summary>
        public List<string> Crafts = new List<string>();

        /// <summary>
        ///     The install's save folder the tests play in; null: one named after the run's tests (<see cref="AutoSaveFolder" />),
        ///     so a run never resets a save another test, or a Serve session, plays in.
        /// </summary>
        public string? SaveFolder;

        /// <summary>Reuse saves/&lt;SaveFolder&gt; as it is.</summary>
        public bool NoReset;

        /// <summary>A test's timeout when its [UITest] sets none (the harness's; the runner follows each test's own).</summary>
        public int TimeoutSec = 900;

        /// <summary>How long KSP may take to load to the main menu.</summary>
        public int LoadTimeoutSec = 900;

        /// <summary>Leave KSP open after the tests.</summary>
        public bool Keep;

        public int Width = 1920;
        public int Height = 1080;

        public Dictionary<string, string> Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Where the run's results folder goes.</summary>
        public string ResultsRoot = "results";

        /// <summary>Where files someone saved in the test save (crafts, subassemblies) are kept before a reset deletes them.</summary>
        public string? SaveBackupRoot;

        /// <summary>
        ///     A KSP whose load goes quiet this long before the first test starts (KSP.log and KSPCF's texture cache both
        ///     unwritten) is killed and started again, up to LoadRetries times.  A modded install now and then hangs in the
        ///     texture load; a healthy RP-1 load never went more than 19 s without a KSP.log line, the long quiet one is
        ///     KSPCF's FastLoader rebuilding its texture cache, which writes the cache as it goes.
        /// </summary>
        public int LoadStallSec = 120;

        public int LoadRetries = 2;

        /// <summary>Once the tests run: KSP.log and the harness's log both silent this long is a frozen game, killed.</summary>
        public int RunStallSec = 600;

        /// <summary>bdwgc's GC_FREE_SPACE_DIVISOR for this KSP (0: leave it unset).</summary>
        public int GcDivisor;

        /// <summary>The install's MASTER_VOLUME at 0 for the run, the player's put back after it.</summary>
        public bool Mute;

        /// <summary>How long to wait for another KSP (any install) or another run holding the lock before giving up.</summary>
        public int WaitForOtherKspSec = 7200;

        /// <summary>More arguments for KSP after the runner's window ones, such as "-batchmode" (draws nothing: faster headless).</summary>
        public string? KspArgs;

        /// <summary>
        ///     Files in the install (paths relative to its root) copied into the run's results folder, under collected/, once
        ///     KSP has exited: what a run leaves in the game, such as a coverage recorder's report, kept per run before the
        ///     next run's deploy writes over the install.
        /// </summary>
        public List<string> Collect = new List<string>();

        /// <summary>
        ///     Unity's masterTextureLimit the harness holds outside screenshots ("textures": full = 0, low = 3, or 0-3): each
        ///     step halves textures' resolution, so low takes ~6 GiB off an RP-1 instance. Screenshots switch to full.
        /// </summary>
        public int TextureLimit;

        /// <summary>
        ///     How many KSPs may run at once, each from an install of its own (thin clones): above 1 the run takes a slot of the
        ///     lock (<see cref="KspLock.AcquireSlot" />) instead of the whole machine, and KSP writes its Player.log into the results.
        /// </summary>
        public int MaxKsps = 1;

        /// <summary>
        ///     The harness's waits and test timeouts stretched by this (run.cfg timeoutScale): for a run known to be slow, such
        ///     as an instrumented build beside other KSPs, instead of every wait made long enough for it.  1: as written.
        /// </summary>
        public double TimeoutScale = 1;

        /// <summary>Once the tests start (KSP has loaded): a clone hands a ModuleManager cache it rebuilt to its master.</summary>
        public Action<KspInstall>? Loaded;

        /// <summary>"full", "low" or a limit 0-3 as <see cref="TextureLimit" />; null or empty: full.</summary>
        public static int ParseTextures(string? s)
        {
            if (string.IsNullOrWhiteSpace(s) || s!.Equals("full", StringComparison.OrdinalIgnoreCase)) return 0;
            if (s.Equals("low", StringComparison.OrdinalIgnoreCase)) return 3;
            if (int.TryParse(s, out int n) && n >= 0 && n <= 3) return n;
            throw new ArgumentException($"textures '{s}': full, low or 0-3");
        }

        /// <summary>The lock file every KSP launcher on this machine shares; null: no locking.</summary>
        public string? LockFile;

        /// <summary>Progress and warnings.</summary>
        public Action<string> Log = Console.WriteLine;

        /// <summary>
        ///     The run's milestones as they happen, also in Log: waiting for another KSP, KSP started, each test starting and
        ///     ending, KSP stopped by the runner.
        /// </summary>
        public Action<string>? Progress;

        /// <summary>Each test's result as soon as the harness has written it, while the session goes on.</summary>
        public Action<TestResult>? TestDone;

        /// <summary>Each test starting, by its method name, with its timeout in seconds (instead of a Progress line when set).</summary>
        public Action<string, int>? TestStarted;

        /// <summary>The run's results folder, once it is made.</summary>
        public Action<string>? ResultsDirReady;

        /// <summary>"key=value;key=value" into Params.</summary>
        public RunOptions WithParams(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return this;
            foreach (string kv in text!.Split(';', ','))
            {
                int eq = kv.IndexOf('=');
                if (eq > 0)
                    Params[kv.Substring(0, eq).Trim()] = kv.Substring(eq + 1).Trim();
            }

            return this;
        }

        /// <summary>
        ///     What has to be the same for runs to share one KSP session: every option but the per-run ones (Test, Scope,
        ///     TimeoutSec, Install, Deploy, Log and the other callbacks), which a shared session gets from its runs (TimeoutSec: the longest).  A new
        ///     option goes here too.
        /// </summary>
        public static string[] TestNames(string test) =>
            test.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToArray();

        /// <summary>
        ///     The save folder of a run without a SaveFolder: ksptest-TEST for one test; for several, the first, how many more and
        ///     a hash of them all (sorted, so their order does not matter): ksptest-FIRST+N-HASH. A character a folder name
        ///     cannot have (a wildcard's *) becomes _, and the name is cut at 40, as the results folder's is.
        /// </summary>
        public static string AutoSaveFolder(string test)
        {
            string[] names = TestNames(test);
            string first = Regex.Replace(names.FirstOrDefault() ?? "run", @"[^A-Za-z0-9_.-]", "_");
            if (first.Length > 40)
                first = first.Substring(0, 40);
            if (names.Length <= 1)
                return "ksptest-" + first;
            uint hash = 2166136261;   // FNV-1a: the same on every run, unlike string.GetHashCode
            foreach (char c in string.Join(",", names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)).ToLowerInvariant())
                hash = (hash ^ c) * 16777619;
            return $"ksptest-{first}+{names.Length - 1}-{hash:x8}";
        }

        public string SessionKey() =>
            string.Join("|", SaveTemplate, NewGame, SaveFolder, NoReset, Keep, Width, Height, Mute, GcDivisor, KspArgs, TextureLimit, MaxKsps, TimeoutScale, LoadStallSec, LoadTimeoutSec, LoadRetries,
                RunStallSec, WaitForOtherKspSec, ResultsRoot, SaveBackupRoot, LockFile, string.Join(",", Crafts),
                string.Join(";", Params.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key + "=" + kv.Value)));
    }
}
