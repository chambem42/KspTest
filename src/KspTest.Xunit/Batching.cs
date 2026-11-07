using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using KspTest.Runner;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace KspTest.Xunit
{
    /// <summary>
    ///     The test framework the KspTest.Xunit build steps make every test project use (KspTestBatching=false turns it off):
    ///     xUnit's own, which first plans the selected [KspFact]s into shared KSP sessions (<see cref="Batching" />).
    /// </summary>
    public sealed class KspTestFramework : XunitTestFramework
    {
        public KspTestFramework(IMessageSink messageSink) : base(messageSink) { }

        protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName) =>
            new KspTestFrameworkExecutor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);
    }

    public sealed class KspTestFrameworkExecutor : XunitTestFrameworkExecutor
    {
        public KspTestFrameworkExecutor(AssemblyName assemblyName, ISourceInformationProvider sourceInformationProvider, IMessageSink diagnosticMessageSink)
            : base(assemblyName, sourceInformationProvider, diagnosticMessageSink) { }

        protected override async void RunTestCases(IEnumerable<IXunitTestCase> testCases, IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions)
        {
            List<IXunitTestCase> cases = testCases.ToList();
            Live.Sink = DiagnosticMessageSink;
            Batching.Plan(PlanOrder.Order(cases));
            using var runner = new KspTestAssemblyRunner(TestAssembly, cases, DiagnosticMessageSink, executionMessageSink, executionOptions);
            await runner.RunAsync();
        }
    }

    /// <summary>xUnit's assembly runner, running the tests in <see cref="PlanOrder" /> unless the assembly says another order.</summary>
    internal sealed class KspTestAssemblyRunner : XunitTestAssemblyRunner
    {
        public KspTestAssemblyRunner(ITestAssembly testAssembly, IEnumerable<IXunitTestCase> testCases, IMessageSink diagnosticMessageSink,
            IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions)
            : base(testAssembly, testCases, diagnosticMessageSink, executionMessageSink, executionOptions) { }

        // the orderers come from the assembly's attributes as it starts
        protected override async Task AfterTestAssemblyStartingAsync()
        {
            await base.AfterTestAssemblyStartingAsync();
            if (TestCaseOrderer is DefaultTestCaseOrderer)
                TestCaseOrderer = new PlanOrder();
            if (TestCollectionOrderer is DefaultTestCollectionOrderer)
                TestCollectionOrderer = new PlanOrder();
        }
    }

    /// <summary>
    ///     The order the tests run in, and a shared KSP session runs its tests in, so each reports as KSP finishes it: test
    ///     collections (classes, unless grouped) by name, and a class's tests by name.  (xUnit's own order is a hash's.)
    /// </summary>
    public sealed class PlanOrder : ITestCaseOrderer, ITestCollectionOrderer
    {
        public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases) where TTestCase : ITestCase =>
            testCases.OrderBy(c => c.TestMethod.TestClass.Class.Name, StringComparer.Ordinal)
                .ThenBy(c => c.TestMethod.Method.Name, StringComparer.Ordinal).ThenBy(c => c.UniqueID, StringComparer.Ordinal);

        public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
            testCollections.OrderBy(c => c.DisplayName, StringComparer.Ordinal);

        /// <summary>All the tests, in the order they run: their collections', then theirs within each.</summary>
        internal static IEnumerable<IXunitTestCase> Order(IEnumerable<IXunitTestCase> cases) =>
            cases.OrderBy(c => c.TestMethod.TestClass.TestCollection.DisplayName, StringComparer.Ordinal)
                .ThenBy(c => c.TestMethod.TestClass.Class.Name, StringComparer.Ordinal)
                .ThenBy(c => c.TestMethod.Method.Name, StringComparer.Ordinal).ThenBy(c => c.UniqueID, StringComparer.Ordinal);
    }

    /// <summary>
    ///     Progress to dotnet test's console as it happens, as xUnit diagnostic messages (KspTest.Xunit's xunit.runner.json
    ///     turns them on): a test's own output only shows once it has ended. Each line starts with the time since the run
    ///     started, the test's number (by when it started) of the run's, and the outcomes so far:
    ///     "[0:12:34 57/153 (55 passed, 1 failed)]".
    /// </summary>
    internal static class Live
    {
        public static IMessageSink? Sink;

        // from the run's start: the framework sets Sink before it plans
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly object Gate = new object();
        private static int _total, _numbered, _passed, _failed, _skipped;
        private static readonly Dictionary<string, int> Numbers = new Dictionary<string, int>();
        private static readonly HashSet<string> Counted = new HashSet<string>();

        /// <summary>How many tests the run has: the KSP tests planned (not those skipped before they run).</summary>
        public static void Planned(int total)
        {
            lock (Gate)
                _total = total;
        }

        /// <summary>A line about the run or a session.</summary>
        public static void Say(string message) => Emit(Prefix(null), message);

        /// <summary>
        /// A line about the run as a GitHub Actions warning ("::warning::" at the start of its line, once the CI step has
        /// dropped xUnit's prefix): an annotation on the run, for what passing tests would otherwise hide.
        /// </summary>
        public static void Warn(string message)
        {
            Say(message);
            Sink?.OnMessage(new DiagnosticMessage("::warning::" + message));
        }

        /// <summary>A line about a test, by its method name.</summary>
        public static void Test(string test, string message) => Emit(Prefix(test), $"{test}: {message}");

        /// <summary>A test's outcome (pass, fail or skip), counted once however often it is reported; said too with a message.</summary>
        public static void Outcome(string test, string outcome, string? message = null)
        {
            lock (Gate)
            {
                Number(test);
                if (Counted.Add(test))
                {
                    if (outcome == "pass") _passed++;
                    else if (outcome == "skip") _skipped++;
                    else _failed++;
                }
            }

            if (message != null)
                Test(test, message);
        }

        // under Gate
        private static int Number(string test) => Numbers.TryGetValue(test, out int n) ? n : Numbers[test] = ++_numbered;

        private static string Prefix(string? test)
        {
            lock (Gate)
            {
                int n = test == null ? _numbered : Number(test);
                string skipped = _skipped > 0 ? $", {_skipped} skipped" : "";
                TimeSpan e = Clock.Elapsed;
                return $"[{(int)e.TotalHours}:{e.Minutes:D2}:{e.Seconds:D2} {n}/{Math.Max(_total, _numbered)} ({_passed} passed, {_failed} failed{skipped})]";
            }
        }

        private static void Emit(string prefix, string message) => Sink?.OnMessage(new DiagnosticMessage($"{prefix} {message}"));
    }

    /// <summary>
    ///     Selected [KspFact]s that run alike - the same mods, save, new game and run options - share one KSP session instead
    ///     of starting KSP each, and each still reports its own result.  The plan comes from the facts' attributes (the
    ///     ones made from [UITest]s say all of it); a fact without a Test, or one that asks Ksp.Run for something else, or
    ///     an Isolated one, gets a KSP of its own.
    /// </summary>
    internal static class Batching
    {
        /// <summary>A test to run: what it runs, in what, and why it cannot (once the install is known).</summary>
        internal sealed class Request
        {
            public string UniqueId = "";
            public string Test = "";
            /// <summary>Files under GameData or assemblies (Env's tags) it needs, ";" or "," separated.</summary>
            public string? Requires;
            /// <summary>CKAN identifiers its instance has besides the project's.</summary>
            public List<string> Ckan = new List<string>();
            /// <summary>Its local mods: GameData/ModName folders, copied into its instance for the run.</summary>
            public List<string> Local = new List<string>();
            public RunOptions Options = new RunOptions();
            public string? SkipReason;

            /// <summary>What has to be the same for tests to share a session (each test keeps its own timeout).</summary>
            public string Key => string.Join(";", InstanceManager.Normalize(Ckan)) + "|" +
                                 string.Join(";", Local.Select(l => l.ToLowerInvariant()).OrderBy(l => l, StringComparer.Ordinal)) + "|" +
                                 Options.SessionKey();

            public static Request For(string uniqueId, string test, string? parameters, string? save, string? newGame, string? mods,
                string? requires, Action<RunOptions>? configure)
            {
                var r = new Request { UniqueId = uniqueId, Test = test, Requires = requires };
                foreach (string m in InstanceManager.Split(mods))
                    if (InstanceManager.IsLocal(m))
                        r.Local.Add(Path.GetFullPath(m).TrimEnd('/', '\\'));
                    else
                        r.Ckan.Add(m);
                r.Options = Ksp.Options(test, parameters, save, newGame, configure);
                return r;
            }
        }

        /// <summary>
        ///     Tests that run in one KSP session, in plan order; the session starts (in the background) when the first of
        ///     them runs.  Its fields change under a lock of it, which is pulsed when they do.
        /// </summary>
        internal sealed class Batch
        {
            public readonly List<Request> Requests = new List<Request>();
            /// <summary>Which of its session's shards it is (1-based) and how many there are; 1 of 1 unsplit.</summary>
            public int Shard = 1, Shards = 1;
            /// <summary>Planned to run beside other sessions, in a thin clone of its instance (KspSettings.MaxKsps above 1).</summary>
            public bool SideBySide;
            /// <summary>Its tests' last durations added up (side by side): the longest waiting gets the next KSP slot.</summary>
            public double EstimateSec;
            public Task? Session;
            /// <summary>Which tests skip is known (the install is), and which runs last.</summary>
            public bool Planned;
            public Request? LastRun;
            /// <summary>Each test's result as the harness wrote it.</summary>
            public readonly Dictionary<Request, TestResult> Done = new Dictionary<Request, TestResult>();
            public bool Finished;
            /// <summary>Its tests run in another CI job (KSPTEST_SHARD): each skips, saying so.</summary>
            public string? Elsewhere;
            public RunResult? Result;
            public Exception? Error;
            public string? ResultsDir;
            private readonly List<string> _log = new List<string>();

            /// <summary>The run's sessions numbered as they are made: the planned ones in plan order, then any of a test's own.</summary>
            public readonly int Number = Interlocked.Increment(ref _sessions);
            private static int _sessions;

            public Batch(params Request[] requests) => Requests.AddRange(requests);

            /// <summary>Its name in progress lines: "session 2 (37 tests, shard 2/4)".</summary>
            public string Label => $"session {Number} ({Requests.Count} test{(Requests.Count == 1 ? "" : "s")}" +
                                   (Shards > 1 ? $", shard {Shard}/{Shards})" : ")");

            public void Log(string line)
            {
                lock (_log)
                    _log.Add(line);
            }

            /// <summary>The session's log since the last take.</summary>
            public List<string> TakeLog()
            {
                lock (_log)
                {
                    var lines = new List<string>(_log);
                    _log.Clear();
                    return lines;
                }
            }
        }

        private static readonly Dictionary<string, Batch> ByTest = new Dictionary<string, Batch>();

        public static Batch? For(ITest? test)
        {
            if (test == null)
                return null;
            lock (ByTest)
                return ByTest.TryGetValue(test.TestCase.UniqueID, out Batch? b) ? b : null;
        }

        public static void Plan(IEnumerable<IXunitTestCase> cases)
        {
            var planned = new List<Request>();
            int total = 0;
            foreach (IXunitTestCase c in cases)
            {
                if (!string.IsNullOrEmpty(c.SkipReason))
                    continue;
                IAttributeInfo? fact = Ksp.FactOf(c.TestMethod);
                string? test = fact?.GetNamedArgument<string>("Test");
                if (fact != null && test != null)
                    total++;
                if (fact == null || test == null || fact.GetNamedArgument<bool>("Isolated"))
                    continue;
                try
                {
                    planned.Add(Request.For(c.UniqueID, test, null, fact.GetNamedArgument<string>("Save"), fact.GetNamedArgument<string>("NewGame"),
                        fact.GetNamedArgument<string>("Mods"), fact.GetNamedArgument<string>("Requires"), null));
                }
                catch (Exception)
                {
                    // (a save it names is missing, ...): it runs alone and says so then
                }
            }

            int maxKsps = KspSettings.Current.MaxKsps, job = KspSettings.Current.Shard, jobs = KspSettings.Current.Shards;
            Dictionary<string, double>? took = maxKsps > 1 || jobs > 1
                ? Durations.Load(new[] { KspSettings.Current.Results }.Concat(KspSettings.Current.DurationsFrom))
                : null;
            if (jobs > 1)
            {
                List<Request> mine = ForShard(planned, job, jobs, took!);
                Request[] elsewhere = planned.Where(r => !mine.Contains(r)).ToArray();
                if (elsewhere.Length > 0)
                    lock (ByTest)
                    {
                        var away = new Batch(elsewhere) { Elsewhere = $"runs in another CI job (this is shard {job} of {jobs})" };
                        foreach (Request r in elsewhere)
                            ByTest[r.UniqueId] = away;
                    }

                Live.Say($"shard {job} of {jobs}: {mine.Count} of {planned.Count} tests, " +
                         $"~{mine.Sum(r => took!.TryGetValue(r.Test, out double t) ? t : UnknownSec):F0} s by their last durations");
                // the tests that run in a session of their own run in the first job
                total = mine.Count + (job == 1 ? total - planned.Count : 0);
                planned = mine;
            }

            Live.Planned(total);
            var batches = new List<Batch>();
            lock (ByTest)
                foreach (IGrouping<string, Request> g in planned.GroupBy(r => r.Key))
                {
                    List<List<Request>> shards = maxKsps == 1 ? new List<List<Request>> { g.ToList() } : Split(g.ToList(), maxKsps, took!);
                    if (shards.Count > 1)
                        Live.Say($"{g.Count()} tests that share a session split into {shards.Count} side by side, by their last durations: " +
                                 string.Join(", ", shards.Select(s => $"{s.Count} tests ~{s.Sum(r => took!.TryGetValue(r.Test, out double t) ? t : UnknownSec):F0} s")));
                    for (int i = 0; i < shards.Count; i++)
                    {
                        var b = new Batch(shards[i].ToArray())
                        {
                            Shard = i + 1, Shards = shards.Count, SideBySide = maxKsps > 1,
                            EstimateSec = maxKsps == 1 ? 0 : shards[i].Sum(r => took!.TryGetValue(r.Test, out double t) ? t : UnknownSec),
                        };
                        batches.Add(b);
                        foreach (Request r in shards[i])
                            ByTest[r.UniqueId] = b;
                    }
                }

            // side by side, every session starts now: each waits for a KSP slot, not for xUnit to reach its first test
            // (the longest first, so they ask for a slot first)
            if (maxKsps > 1)
                foreach (Batch b in batches.OrderByDescending(b => b.EstimateSec))
                    lock (b)
                        b.Session ??= Task.Run(() => Ksp.RunSession(b));
        }

        /// <summary>
        ///     The planned tests CI job <paramref name="shard" /> of <paramref name="shards" /> runs: each session's tests split as
        ///     for KSPs side by side, the pieces dealt to the jobs longest first, each to the job with the least so far (the
        ///     lowest on a tie). Every job deals alike from the same durations, so each test runs in exactly one.
        /// </summary>
        internal static List<Request> ForShard(List<Request> planned, int shard, int shards, IReadOnlyDictionary<string, double> took)
        {
            double Sec(Request r) => took.TryGetValue(r.Test, out double t) ? t : UnknownSec;
            var pieces = planned.GroupBy(r => r.Key).OrderBy(g => g.Key, StringComparer.Ordinal)
                .SelectMany(g => Split(g.ToList(), shards, took))
                .Select(p => (Tests: p, Sec: p.Sum(Sec)))
                .OrderByDescending(p => p.Sec).ThenBy(p => p.Tests[0].Test, StringComparer.Ordinal).ToList();
            var load = new double[shards];
            var mine = new HashSet<Request>();
            foreach ((List<Request> tests, double sec) in pieces)
            {
                int job = Array.IndexOf(load, load.Min());
                load[job] += sec;
                if (job == shard - 1)
                    mine.UnionWith(tests);
            }

            return planned.Where(mine.Contains).ToList();
        }

        /// <summary>A shard runs no shorter than this: shorter, its KSP's load is too much of it.</summary>
        internal const double MinShardSec = 120;

        /// <summary>What a test takes when no run of it is on record.</summary>
        internal const double UnknownSec = 30;

        /// <summary>
        ///     A session's tests split into up to maxKsps shards, each a run of them in plan order (tests next to each other
        ///     tend to start from the same scene), of about the same time by their last recorded durations.
        /// </summary>
        internal static List<List<Request>> Split(List<Request> tests, int maxKsps, IReadOnlyDictionary<string, double> took)
        {
            double[] sec = tests.Select(r => took.TryGetValue(r.Test, out double s) ? s : UnknownSec).ToArray();
            double total = sec.Sum();
            int n = Math.Max(1, Math.Min(Math.Min(maxKsps, tests.Count), (int)(total / MinShardSec)));
            var shards = new List<List<Request>> { new List<Request>() };
            double done = 0;
            for (int i = 0; i < tests.Count; i++)
            {
                // a new shard once this one has its share, the test going where it leaves the shard nearer its share
                double share = total * shards.Count / n;
                if (shards.Count < n && shards[shards.Count - 1].Count > 0 && done + sec[i] / 2 > share)
                    shards.Add(new List<Request>());
                shards[shards.Count - 1].Add(tests[i]);
                done += sec[i];
            }

            return shards;
        }
    }

    /// <summary>
    ///     Each test's last recorded duration (by Namespace.Class.Method), from the result.json of the runs in the results
    ///     folders (the run's own, and KspSettings.DurationsFrom).
    /// </summary>
    internal static class Durations
    {
        /// <summary>The newest runs looked at: enough to have every test of a suite, newest first.</summary>
        private const int Runs = 200;

        public static Dictionary<string, double> Load(IEnumerable<string> resultsRoots)
        {
            var took = new Dictionary<string, double>(StringComparer.Ordinal);
            // results folders are named by when they were made (yyyyMMdd-HHmmss-...): newest first, whichever root they are in
            IEnumerable<string> runs = resultsRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(Directory.GetDirectories).OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Take(Runs);
            foreach (string dir in runs)
            {
                if (!File.Exists(Path.Combine(dir, "result.json")))
                    continue;
                try
                {
                    foreach (TestResult t in RunResult.Read(dir, "unknown").Tests)
                        if (t.DurationSec > 0 && (t.Status == "pass" || t.Status == "fail") && !took.ContainsKey(t.Id))
                            took[t.Id] = t.DurationSec;
                }
                catch (Exception)
                {
                    // a result.json being written, or of an older harness: the next runs have it
                }
            }

            return took;
        }
    }
}
