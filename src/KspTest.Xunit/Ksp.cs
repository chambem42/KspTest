using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using KspTest.Runner;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace KspTest.Xunit
{
    /// <summary>
    ///     A test that runs in KSP: in the install configured (KSPDIR, or "install" in ksptest.json), or else in a KSP instance
    ///     KspTest makes with the project's mods and the test's <see cref="Mods" />.  KspTest.Xunit's source generator makes
    ///     one for every [UITest] in a test project's in-game assemblies, from that attribute; one can also be written by
    ///     hand, calling Ksp.Run.  Skipped (with the reason) when the configured install is missing or lacks what the test
    ///     <see cref="Requires" />, or when there is no install and KspTest is not set up to make one (ksptest setup).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    [XunitTestCaseDiscoverer("KspTest.Xunit.KspFactDiscoverer", "KspTest.Xunit")]
    [TraitDiscoverer("KspTest.Xunit.KindDiscoverer", "KspTest.Xunit")]
    public sealed class KspFactAttribute : FactAttribute, ITraitAttribute
    {
        private string? _skip;

        public KspFactAttribute()
        {
            string? install = KspSettings.Current.Install;
            if (install == null)
                _skip = Instances.Problem;
            else if (!new KspInstall(install).Exists)
                _skip = $"no KSP install at {install}";
        }

        /// <summary>The in-game test it runs (Namespace.Class.Method), which plans it into a KSP session shared with tests like it.</summary>
        public string? Test { get; set; }

        /// <summary>
        ///     What the test needs, ";" or "," separated: assemblies by [UITest(Requires)]'s tags ("RP-1", "MechJeb", or an
        ///     assembly's name), or files under GameData ("MechJeb2/Plugins/MechJeb2.dll").  A configured install without
        ///     them skips the test; in an instance the harness does.
        /// </summary>
        public string? Requires { get; set; }

        /// <summary>
        ///     Mods the test's instance has besides the project's, ";" separated: CKAN identifiers ("Id" or "Id=version"), or
        ///     local mods, as the path of their GameData/ModName folder.
        /// </summary>
        public string? Mods { get; set; }

        /// <summary>A save template the test project ships (KspTestSave), by name.</summary>
        public string? Save { get; set; }

        /// <summary>Without a Save, the mode of the new game: sandbox (the default), science or career.</summary>
        public string? NewGame { get; set; }

        /// <summary>A KSP session of its own, never shared with other tests that run alike (it leaves the game in a state others must not start from).</summary>
        public bool Isolated { get; set; }

        /// <summary>
        ///     Skipped unless asked for: KSPTEST_EXPLICIT "1" (every explicit test) or a ";" list with this one's method name
        ///     ([UITest(Explicit = true)], for a test that is known to fail until a fix lands, or too long for every run).
        /// </summary>
        public bool Explicit { get; set; }

        private bool ExplicitlyAsked()
        {
            string? asked = Environment.GetEnvironmentVariable("KSPTEST_EXPLICIT");
            if (string.IsNullOrEmpty(asked)) return false;
            if (asked == "1" || asked.Equals("all", StringComparison.OrdinalIgnoreCase)) return true;
            string name = Test?.Substring(Test.LastIndexOf('.') + 1) ?? "";
            return asked.Split(';', ',').Any(a => a.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The test's "Kind" trait (Check, Measure, ...), as [Trait("Kind", ...)] would set it.</summary>
        public string? Kind { get; set; }

        public override string? Skip
        {
            get
            {
                if (_skip == null && Explicit && !ExplicitlyAsked())
                    return "explicit: runs only when asked for (KSPTEST_EXPLICIT=1, or its name)";
                // in an instance, the harness skips a test whose mods are not there (what its instance has is only known later)
                if (_skip != null || KspSettings.Current.Install == null)
                    return _skip;
                var install = new KspInstall(KspSettings.Current.Install);
                string[] missing = Ksp.Missing(install, Requires)
                    .Concat(Ksp.LocalMods(Mods).Select(Path.GetFileName).Where(n => !Directory.Exists(Path.Combine(install.GameData, n)))).ToArray();
                return missing.Length > 0 ? $"the KSP install has no {string.Join(", ", missing)}" : null;
            }
            set => _skip = value;
        }
    }

    /// <summary>Runs in-game tests from xUnit tests.</summary>
    public static class Ksp
    {
        private static readonly object DeployLock = new object();

        /// <summary>
        ///     Runs the in-game test <paramref name="test" /> with the parameters "k=v;k=v" (the settings' params over them), on
        ///     the save template <paramref name="save" /> (a folder the test project ships, by name), or without one in a new
        ///     game.  The project's crafts go into the save.  Writes the report to the test's output, fails the test unless
        ///     it passes, and skips it when the harness does.  With no install configured it runs in the instance for the
        ///     project's mods, the fact's Mods and <paramref name="mods" />, made the first time.  The harness, the
        ///     project's in-game assemblies and (in an instance) the local mods are deployed for every run.
        /// </summary>
        public static RunResult? Run(ITestOutputHelper output, string test, string? parameters = null, string? save = null,
            Action<RunOptions>? configure = null, string? mods = null)
        {
            ITest? itest = TestOf(output) ?? Ambient.Test;
            Batching.Batch? batch = parameters == null && save == null && configure == null && mods == null ? Batching.For(itest) : null;
            Batching.Request? request = batch?.Requests.FirstOrDefault(q => q.UniqueId == itest!.TestCase.UniqueID);
            if (batch == null || request == null || request.Test != test)
            {
                // a call of its own (a fact without a Test, or asking for something else than planned): a KSP session of its own
                IAttributeInfo? fact = itest == null ? null : FactOf(itest.TestCase.TestMethod);
                request = Batching.Request.For(itest?.TestCase.UniqueID ?? test, test, parameters, save ?? fact?.GetNamedArgument<string>("Save"),
                    fact?.GetNamedArgument<string>("NewGame"), string.Join(";", new[] { fact?.GetNamedArgument<string>("Mods"), mods }.Where(m => m != null)),
                    fact?.GetNamedArgument<string>("Requires"), configure);
                batch = new Batching.Batch(request);
            }

            return RunIn(batch, request, output, itest);
        }

        /// <summary>
        ///     The in-game test of the [KspFact] it is called from, the one named as the fact's method (or <paramref name="test" />):
        ///     <c>[KspFact] public void Smoke_MainMenuToVab() => Ksp.Run();</c>, with no output helper to take in a constructor.
        /// </summary>
        public static RunResult? Run(string? parameters = null, string? save = null, Action<RunOptions>? configure = null, string? mods = null,
            [CallerMemberName] string test = "") =>
            Run(Ambient.Output ?? throw new InvalidOperationException("Ksp.Run() runs inside a [KspFact]; elsewhere pass it the test's ITestOutputHelper"),
                test, parameters, save, configure, mods);

        /// <summary>
        ///     A test's part of its batch's KSP session: the first of them to get here starts it (in the background) for them
        ///     all, and each ends as soon as the harness has its result, so dotnet test shows them pass or fail as KSP runs
        ///     them; the session's last test waits for KSP to exit, for the session's report.  Each test's output has the
        ///     session's log since the test before it, and its progress goes to dotnet test's console as it happens
        ///     (<see cref="Live" />).
        /// </summary>
        private static RunResult? RunIn(Batching.Batch batch, Batching.Request request, ITestOutputHelper output, ITest? itest)
        {
            lock (batch)
            {
                batch.Session ??= Task.Run(() => RunSession(batch));
                while (!batch.Finished && !(batch.Planned && (request.SkipReason != null ||
                                                               request != batch.LastRun && batch.Done.ContainsKey(request))))
                    Monitor.Wait(batch);
            }

            foreach (string line in batch.TakeLog())
                output.WriteLine(line);
            if (request.SkipReason != null)
                throw new SkipException(request.SkipReason);
            TestResult? t;
            lock (batch)
                t = batch.Done.TryGetValue(request, out TestResult? done) ? done : batch.Result?[request.Test];
            if (t == null && batch.Error != null)
                throw batch.Error is SkipException skip ? new SkipException(skip.Reason)
                    : batch.Requests.Count == 1 ? batch.Error is XunitException ? batch.Error : new XunitException(batch.Error.Message)
                    : new XunitException($"the KSP session {request.Test} shares failed to start: {batch.Error.Message}");

            RunResult? r = batch.Result;
            output.WriteLine("");
            if (batch.Requests.Count == 1 && r != null)
                output.WriteLine(r.Report());
            else
            {
                if (t != null)
                {
                    Timing.Report(itest, t.DurationSec);
                    output.WriteLine(t.Report().TrimEnd());
                }

                string others = string.Join(", ", batch.Requests.Where(q => q != request).Select(q => q.Test));
                output.WriteLine(r == null
                    ? $"in one KSP session with {others}, still running"
                    : $"in one KSP session with {others}: {r.Status} ({r.DurationSec}s, ended in {r.Scene})" +
                      (r.Exceptions.Count > 0 ? $"; {r.Exceptions.Count} exceptions in KSP.log (first: {r.Exceptions[0]})" : ""));
                output.WriteLine($"  results: {batch.ResultsDir}");
            }

            if (t == null)
                throw Fail(request.Test, $"did not run; the session ended {r?.Status}" + (r?.Crash != null ? $" ({r.Crash})" : ""), batch.ResultsDir);
            if (t.Status == "skipped")
                throw new SkipException(t.Message);
            if (!t.Passed)
                throw Fail(request.Test, $"{t.Status} - {t.Message}", batch.ResultsDir);
            return r;
        }

        /// <summary>A batch's KSP session, on a thread of its own: the install or instance, which tests can run there, and the run.</summary>
        internal static void RunSession(Batching.Batch batch)
        {
            string tag = ShortName(batch.Requests[0].Test) + (batch.Requests.Count > 1 ? $"+{batch.Requests.Count - 1}" : "") +
                         (batch.Shards > 1 ? $" [{batch.Shard}/{batch.Shards}]" : "");
            int clone = 0;
            // progress by the session's first test (and how many more): a line about that test alone needs no prefix
            void Say(string line)
            {
                batch.Log(line);
                Live.Say(line.StartsWith(tag + ":") ? line : $"{tag}: {line}");
            }

            try
            {
                Batching.Request first = batch.Requests[0];
                KspInstall install;
                bool instance;
                // sessions starting side by side: one makes the instance, the others find it made
                lock (InstallLock)
                    (install, instance) = InstallFor(first.Ckan.Concat(first.Local), line =>
                    {
                        if (line.StartsWith("instance "))
                            Say(line);
                        else
                            batch.Log(line);
                    });
                foreach (Batching.Request q in batch.Requests)
                    if ((q.SkipReason = SkipReason(install, instance, q)) != null)
                        Say($"{ShortName(q.Test)}: skipped ({q.SkipReason})");
                List<Batching.Request> runs = batch.Requests.Where(q => q.SkipReason == null).ToList();
                lock (batch)
                {
                    batch.LastRun = runs.LastOrDefault();
                    batch.Planned = true;
                    Monitor.PulseAll(batch);
                }

                if (runs.Count == 0)
                    return;
                RunOptions o = runs[0].Options;
                o.Test = string.Join(",", runs.Select(q => q.Test));
                o.Install = install.Root;
                o.TimeoutSec = runs.Max(q => q.Options.TimeoutSec);
                o.Log = batch.Log;
                o.Progress = Say;
                o.ResultsDirReady = dir => batch.ResultsDir = dir;
                o.TestDone = t =>
                {
                    Batching.Request? q = runs.FirstOrDefault(x => t.Requested == x.Test) ?? runs.FirstOrDefault(x => t.Id == x.Test || t.Test == x.Test);
                    if (q == null)
                        return;
                    string done = $"{t.Test}: {t.Status} ({t.DurationSec:F0} s)";
                    Live.Say(done.StartsWith(tag + ":") ? done : $"{tag}: {done}");
                    lock (batch)
                    {
                        batch.Done[q] = t;
                        Monitor.PulseAll(batch);
                    }
                };
                if (runs.Count > 1)
                    batch.Log($"one KSP session for {runs.Count} tests: {o.Test}");
                if (batch.SideBySide && instance)
                {
                    // beside other sessions: in a thin clone of the instance of its own, holding one of the KSP slots
                    clone = Clones.Take();
                    KspInstall master = install;
                    KspInstall copy;
                    lock (InstallLock)
                        copy = ThinClone.Ensure(master.Root, ThinClone.PathFor(master.Root, Path.GetDirectoryName(master.Root.TrimEnd('\\', '/'))!, clone), batch.Log);
                    Say($"in {Path.GetFileName(copy.Root.TrimEnd('\\', '/'))}, beside up to {KspSettings.Current.MaxKsps - 1} other KSPs");
                    o.Install = copy.Root;
                    o.MaxKsps = KspSettings.Current.MaxKsps;
                    batch.Result = CloneSession(o, master, copy, runs[0].Local);
                }
                else
                {
                    batch.Result = Session(o, install, instance, runs[0].Local);
                }
                Live.Say($"{tag}: KSP session {batch.Result.Status} ({batch.Result.DurationSec:F0} s)");
            }
            catch (Exception e)
            {
                batch.Error = e;
                Say((e is SkipException s ? "skipped: " + s.Reason : "failed: " + e.Message));
            }
            finally
            {
                if (clone > 0)
                    Clones.Give(clone);
                lock (batch)
                {
                    batch.Planned = true;
                    batch.Finished = true;
                    Monitor.PulseAll(batch);
                }
            }
        }

        /// <summary>A test's method name, from its Namespace.Class.Method.</summary>
        private static string ShortName(string test) => test.Substring(test.LastIndexOf('.') + 1);

        /// <summary>
        ///     Why a test cannot run in this install or instance, or null: what it Requires is not there (in an instance, nor
        ///     in the local mods the run copies in), or (in the user's own install, which KspTest does not change) a local mod.
        /// </summary>
        private static string? SkipReason(KspInstall install, bool instance, Batching.Request q)
        {
            if (instance)
            {
                string[] lacking = Missing(install, q.Requires, KspSettings.Current.Overlays.Concat(q.Local));
                return lacking.Length > 0 ? $"the instance has no {string.Join(", ", lacking)}: add the mod that has it to [UITest(Mods)]" : null;
            }

            string[] missing = Missing(install, q.Requires)
                .Concat(q.Local.Select(Path.GetFileName).Where(n => !Directory.Exists(Path.Combine(install.GameData, n)))).ToArray();
            return missing.Length > 0 ? $"the KSP install has no {string.Join(", ", missing)}" : null;
        }

        /// <summary>A test's failure: what happened (its first line) and where the results are.</summary>
        private static XunitException Fail(string test, string what, string? resultsDir) =>
            new XunitException($"{test}: {what.Split('\n')[0]} (results: {resultsDir})");

        /// <summary>The install configured, or the instance for the project's mods (CKAN and local), the harness's and these (made if needed).</summary>
        private static (KspInstall Install, bool Instance) InstallFor(IEnumerable<string> mods, Action<string> log)
        {
            KspSettings s = KspSettings.Current;
            bool instance = s.Install == null;
            KspInstall install = instance
                ? new InstanceManager(Instances.Settings(log)).Ensure(Deployer.HarnessMods.Concat(s.Mods).Concat(s.Overlays).Concat(mods), log)
                : new KspInstall(s.Install!);
            return (install, instance);
        }

        /// <summary>A run's options from the settings, with its save template (a folder the project ships) or new game.</summary>
        internal static RunOptions Options(string test, string? parameters, string? save, string? newGame, Action<RunOptions>? configure)
        {
            KspSettings s = KspSettings.Current;
            var o = new RunOptions
            {
                Test = test,
                SaveTemplate = save == null ? null : Template(save),
                NewGame = newGame ?? "sandbox",
                ResultsRoot = s.Results,
                SaveBackupRoot = s.SaveBackups,
                LockFile = s.LockFile,
                Mute = s.Mute,
                LoadStallSec = s.LoadStallSec,
                KspArgs = s.KspArgs,
                TextureLimit = RunOptions.ParseTextures(s.Textures),
                TimeoutSec = s.TimeoutSec,
                Scope = s.Tests.Keys.ToList(),
                Collect = s.Collect.ToList(),
            }.WithParams(parameters).WithParams(s.Params);
            if (KspSettings.ParseWindow(s.Window) is (int w, int h))
            {
                o.Width = w;
                o.Height = h;
            }
            o.Crafts.AddRange(s.Crafts);
            configure?.Invoke(o);
            return o;
        }

        // a save template is checked in with the tests (KspTestSave), never one of a player's own
        private static string Template(string save)
        {
            string dir = Path.Combine(KspSettings.Current.Saves, save);
            return Directory.Exists(dir) ? dir
                : throw new XunitException($"no save template '{save}' in {KspSettings.Current.Saves}: ship it with the test project (<KspTestSave Include=\"saves/{save}\" />)");
        }

        /// <summary>One KSP run, deployed once it holds the KSP lock; an instance's mods and their versions go into the results beside it.</summary>
        private static RunResult Session(RunOptions o, KspInstall install, bool instance, List<string> local)
        {
            o.Deploy = i => Deploy(i, instance, local, o.Log);
            RunResult r = KspRunner.Run(o);
            string manifest = Path.Combine(install.Root, InstanceManager.ManifestName);
            if (instance && File.Exists(manifest) && Directory.Exists(r.ResultsDir))
                File.Copy(manifest, Path.Combine(r.ResultsDir, "ksp-instance.json"), true);
            return r;
        }

        private static readonly object InstallLock = new object();

        // the instances (by root and local mods) this process has put its local mods into
        private static readonly HashSet<string> MastersDeployed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        ///     One KSP run in a thin clone of an instance, holding a slot beside other KSPs.  The clone's mod folders are the
        ///     instance's (junctions), so the local mods go into the instance itself, once for this process and before the
        ///     first clone's KSP starts; with the slot held, the clone is brought up to date with the instance and gets the
        ///     harness and tests in its own GameData/KspTest.  A ModuleManager cache the clone rebuilt goes back to the
        ///     instance once it has loaded (<see cref="ThinClone.SyncBack" />).
        /// </summary>
        private static RunResult CloneSession(RunOptions o, KspInstall master, KspInstall clone, List<string> local)
        {
            o.Deploy = i =>
            {
                lock (DeployLock)
                {
                    string key = master.Root + "|" + string.Join(";", local.OrderBy(l => l, StringComparer.OrdinalIgnoreCase));
                    if (MastersDeployed.Add(key))
                        Deployer.LocalMods(master, KspSettings.Current.Overlays.Concat(local), o.Log);
                    ThinClone.Refresh(master, i, o.Log);
                    DeployTests(i, o.Log);
                }
            };
            o.Loaded = i => ThinClone.SyncBack(i, master, o.Log);
            RunResult r = KspRunner.Run(o);
            string manifest = Path.Combine(master.Root, InstanceManager.ManifestName);
            if (File.Exists(manifest) && Directory.Exists(r.ResultsDir))
                File.Copy(manifest, Path.Combine(r.ResultsDir, "ksp-instance.json"), true);
            return r;
        }

        /// <summary>The clone numbers (1..KspSettings.MaxKsps) for the sessions of this process running side by side.</summary>
        private static class Clones
        {
            private static readonly object Gate = new object();
            private static readonly SortedSet<int> Free = new SortedSet<int>(Enumerable.Range(1, KspSettings.Current.MaxKsps));

            /// <summary>The lowest free one, waiting for one to be given back.</summary>
            public static int Take()
            {
                lock (Gate)
                {
                    while (Free.Count == 0)
                        Monitor.Wait(Gate);
                    int n = Free.Min;
                    Free.Remove(n);
                    return n;
                }
            }

            public static void Give(int n)
            {
                lock (Gate)
                {
                    Free.Add(n);
                    Monitor.PulseAll(Gate);
                }
            }
        }

        private static readonly ConcurrentDictionary<string, HashSet<string>> Assemblies = new ConcurrentDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // [UITest(Requires)]'s tags for the assemblies they stand for (as KspTest.Env has them); another tag is an assembly's name
        private static readonly Dictionary<string, string> Tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RP-1"] = "RP0", ["RP1"] = "RP0", ["MechJeb"] = "MechJeb2", ["FAR"] = "FerramAerospaceResearch",
        };

        /// <summary>
        ///     What the install lacks of requires (";" or "," separated): a file or folder under GameData (an entry with a
        ///     "/"), or an assembly (a tag, or its name), looked for as a .dll anywhere in GameData, or in the local mods
        ///     about to be copied into it.  The user's own install is looked through once (every [KspFact] asks), an
        ///     instance, which runs change, each time.
        /// </summary>
        internal static string[] Missing(KspInstall install, string? requires, IEnumerable<string>? local = null)
        {
            HashSet<string>? dlls = null;
            var missing = new List<string>();
            foreach (string r in InstanceManager.Split(requires))
            {
                if (r.Contains("/") || r.Contains("\\"))
                {
                    if (!File.Exists(Path.Combine(install.GameData, r)) && !Directory.Exists(Path.Combine(install.GameData, r)))
                        missing.Add(r);
                    continue;
                }

                if (dlls == null)
                {
                    dlls = local == null ? Assemblies.GetOrAdd(install.Root, _ => Dlls(install.GameData)) : Dlls(install.GameData);
                    foreach (string dir in local ?? Enumerable.Empty<string>())
                        if (Directory.Exists(dir))
                            dlls.UnionWith(Dlls(dir));
                }

                if (!dlls.Contains(Tags.TryGetValue(r, out string? asm) ? asm : r))
                    missing.Add(r);
            }

            return missing.ToArray();
        }

        private static HashSet<string> Dlls(string dir) =>
            new HashSet<string>(Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories).Select(Path.GetFileNameWithoutExtension),
                StringComparer.OrdinalIgnoreCase);

        /// <summary>The local mods among mods (";" separated): the entries that are paths.</summary>
        internal static IEnumerable<string> LocalMods(string? mods) =>
            InstanceManager.Split(mods).Where(InstanceManager.IsLocal).Select(m => m.TrimEnd('/', '\\'));

        /// <summary>The [KspFact] on a test method, or null.</summary>
        internal static IAttributeInfo? FactOf(ITestMethod method) =>
            method.Method.GetCustomAttributes(typeof(KspFactAttribute).AssemblyQualifiedName).FirstOrDefault();

        /// <summary>The running test, through xUnit's output helper (which knows it); null for another helper.</summary>
        private static ITest? TestOf(ITestOutputHelper output) =>
            output.GetType().GetField("test", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(output) as ITest;

        /// <summary>
        ///     Every run, with the KSP lock held: the harness and the project's in-game assemblies the install has the mods
        ///     for, anything else in GameData/KspTest removed (<see cref="Deployer.DeployRun" />); in an instance, the project's
        ///     and the tests' local mods, and no others (<see cref="Deployer.LocalMods" />).
        /// </summary>
        private static void Deploy(KspInstall install, bool instance, List<string> local, Action<string> log)
        {
            lock (DeployLock)
            {
                if (instance)
                    Deployer.LocalMods(install, KspSettings.Current.Overlays.Concat(local), log);
                DeployTests(install, log);
            }
        }

        /// <summary>The harness and the project's in-game assemblies the install has the mods for (<see cref="Deployer.DeployRun" />).</summary>
        private static void DeployTests(KspInstall install, Action<string> log)
        {
            var tests = new List<KeyValuePair<string, List<string>>>();
            foreach (KeyValuePair<string, List<string>> t in KspSettings.Current.Tests)
            {
                string[] missing = KspSettings.Current.TestRequires.TryGetValue(t.Key, out List<string>? needs)
                    ? Missing(install, string.Join(";", needs))
                    : new string[0];
                if (missing.Length == 0)
                    tests.Add(t);
                else
                    log($"skip   {t.Key} (this install has no {string.Join(", ", missing)})");
            }

            Deployer.DeployRun(install, null, tests, log);
        }
    }

    /// <summary>The settings KspTest makes instances with (ksptest setup writes them).</summary>
    internal static class Instances
    {
        private const string NotSetUp = "no KSP install configured (KSPDIR, or \"install\" in ksptest.json), and KspTest is not set up to make one: ";

        /// <summary>At test discovery: why tests needing an instance cannot run, or null (read once per process: every [KspFact] asks).</summary>
        public static string? Problem => LazyProblem.Value;

        private static readonly Lazy<string?> LazyProblem = new Lazy<string?>(() =>
        {
            try
            {
                return UserSettings.Load().Complete ? null : NotSetUp + Setup.Hint;
            }
            catch (Exception e)
            {
                return $"KspTest's settings ({UserSettings.FilePath}) could not be read: {e.Message}";
            }
        });

        /// <summary>The complete settings; skips the test without them.</summary>
        public static UserSettings Settings(Action<string> log)
        {
            UserSettings s = UserSettings.Load();
            return s.Complete ? s : throw new SkipException(NotSetUp + Setup.Hint);
        }
    }

    /// <summary>The [KspFact] running on this thread of control and its output (KspTestCase's runner sets them).</summary>
    internal static class Ambient
    {
        private static readonly AsyncLocal<(ITest? Test, ITestOutputHelper? Output)> Current = new AsyncLocal<(ITest?, ITestOutputHelper?)>();

        public static ITest? Test => Current.Value.Test;
        public static ITestOutputHelper? Output => Current.Value.Output;

        public static void Enter(ITest test, ITestOutputHelper output) => Current.Value = (test, output);
        public static void Leave() => Current.Value = default;
    }

    /// <summary>[KspFact(Kind = "...")] as the trait Kind.</summary>
    public sealed class KindDiscoverer : ITraitDiscoverer
    {
        public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
        {
            if (traitAttribute.GetNamedArgument<string>("Kind") is string kind)
                yield return new KeyValuePair<string, string>("Kind", kind);
        }
    }

    /// <summary>Thrown to skip a test from inside it.</summary>
    public sealed class SkipException : Exception
    {
        public SkipException(string reason) : base("$XunitDynamicSkip$" + reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }
}
