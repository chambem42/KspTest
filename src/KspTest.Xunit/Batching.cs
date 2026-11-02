using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
    ///     turns them on): a test's own output only shows once it has ended.
    /// </summary>
    internal static class Live
    {
        public static IMessageSink? Sink;

        public static void Say(string message) => Sink?.OnMessage(new DiagnosticMessage(message));
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
            public Task? Session;
            /// <summary>Which tests skip is known (the install is), and which runs last.</summary>
            public bool Planned;
            public Request? LastRun;
            /// <summary>Each test's result as the harness wrote it.</summary>
            public readonly Dictionary<Request, TestResult> Done = new Dictionary<Request, TestResult>();
            public bool Finished;
            public RunResult? Result;
            public Exception? Error;
            public string? ResultsDir;
            private readonly List<string> _log = new List<string>();

            public Batch(params Request[] requests) => Requests.AddRange(requests);

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
            foreach (IXunitTestCase c in cases)
            {
                if (!string.IsNullOrEmpty(c.SkipReason))
                    continue;
                IAttributeInfo? fact = Ksp.FactOf(c.TestMethod);
                string? test = fact?.GetNamedArgument<string>("Test");
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

            lock (ByTest)
                foreach (IGrouping<string, Request> g in planned.GroupBy(r => r.Key))
                {
                    var b = new Batch(g.ToArray());
                    foreach (Request r in g)
                        ByTest[r.UniqueId] = b;
                }
        }
    }
}
