using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace KspTest.Xunit
{
    /// <summary>
    ///     The time xUnit reports for a test that shared a KSP session: its own time in the game, where it would otherwise
    ///     be the whole session's for the first of them to run and none for the rest.  A test run in a KSP of its own keeps
    ///     the time it took.  [KspFact]'s discoverer makes the test cases whose runner asks here.
    /// </summary>
    internal static class Timing
    {
        private static readonly ConcurrentDictionary<string, decimal> Seconds = new ConcurrentDictionary<string, decimal>();

        public static void Report(ITest? test, double seconds)
        {
            if (test != null)
                Seconds[test.TestCase.UniqueID] = (decimal)seconds;
        }

        public static decimal? Reported(ITest test) => Seconds.TryGetValue(test.TestCase.UniqueID, out decimal s) ? s : (decimal?)null;
    }

    public sealed class KspFactDiscoverer : FactDiscoverer
    {
        public KspFactDiscoverer(IMessageSink diagnosticMessageSink) : base(diagnosticMessageSink) { }

        protected override IXunitTestCase CreateTestCase(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo factAttribute) =>
            new KspTestCase(DiagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(), discoveryOptions.MethodDisplayOptionsOrDefault(), testMethod);
    }

    public sealed class KspTestCase : XunitTestCase
    {
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Called by the de-serializer; should only be called by deriving classes for de-serialization purposes")]
        public KspTestCase() { }

        public KspTestCase(IMessageSink diagnosticMessageSink, TestMethodDisplay defaultMethodDisplay, TestMethodDisplayOptions defaultMethodDisplayOptions,
            ITestMethod testMethod) : base(diagnosticMessageSink, defaultMethodDisplay, defaultMethodDisplayOptions, testMethod) { }

        public override async Task<RunSummary> RunAsync(IMessageSink diagnosticMessageSink, IMessageBus messageBus, object[] constructorArguments,
            ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource)
        {
            var bus = new SkipBus(messageBus);
            RunSummary summary = await new Runner(this, DisplayName, SkipReason, constructorArguments, TestMethodArguments, bus, aggregator,
                cancellationTokenSource).RunAsync();
            summary.Failed -= bus.Skipped;
            summary.Skipped += bus.Skipped;
            return summary;
        }

        /// <summary>
        ///     xUnit 2 reports a test that threw <see cref="SkipException" /> as failed (its "$XunitDynamicSkip$" message is
        ///     xUnit 3's convention): this bus turns that failure into a skip with the exception's reason.
        /// </summary>
        private sealed class SkipBus : IMessageBus
        {
            private static readonly string SkipType = typeof(SkipException).FullName!;
            private readonly IMessageBus inner;
            public int Skipped;

            public SkipBus(IMessageBus inner) => this.inner = inner;

            public bool QueueMessage(IMessageSinkMessage message)
            {
                if (message is ITestFailed failed && failed.ExceptionTypes.Length > 0 && failed.ExceptionTypes[0] == SkipType)
                {
                    Skipped++;
                    string reason = failed.Messages[0] ?? "";
                    int at = reason.IndexOf("$XunitDynamicSkip$", StringComparison.Ordinal);
                    if (at >= 0) reason = reason.Substring(at + "$XunitDynamicSkip$".Length);
                    return inner.QueueMessage(new TestSkipped(failed.Test, reason));
                }
                return inner.QueueMessage(message);
            }

            public void Dispose() { }   // the runner's bus outlives the test case
        }

        private sealed class Runner : XunitTestCaseRunner
        {
            public Runner(IXunitTestCase testCase, string displayName, string skipReason, object[] constructorArguments, object[] testMethodArguments,
                IMessageBus messageBus, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource)
                : base(testCase, displayName, skipReason, constructorArguments, testMethodArguments, messageBus, aggregator, cancellationTokenSource) { }

            protected override XunitTestRunner CreateTestRunner(ITest test, IMessageBus messageBus, Type testClass, object[] constructorArguments,
                MethodInfo testMethod, object[] testMethodArguments, string skipReason, IReadOnlyList<BeforeAfterTestAttribute> beforeAfterAttributes,
                ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource) =>
                new TestRunner(test, messageBus, testClass, constructorArguments, testMethod, testMethodArguments, skipReason, beforeAfterAttributes,
                    aggregator, cancellationTokenSource);
        }

        private sealed class TestRunner : XunitTestRunner
        {
            public TestRunner(ITest test, IMessageBus messageBus, Type testClass, object[] constructorArguments, MethodInfo testMethod,
                object[] testMethodArguments, string skipReason, IReadOnlyList<BeforeAfterTestAttribute> beforeAfterAttributes,
                ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource)
                : base(test, messageBus, testClass, constructorArguments, testMethod, testMethodArguments, skipReason, beforeAfterAttributes,
                    aggregator, cancellationTokenSource) { }

            // the test and an output helper of its own for Ksp.Run(), so a test class needs no constructor to take one
            protected override async Task<Tuple<decimal, string>> InvokeTestAsync(ExceptionAggregator aggregator)
            {
                var output = new TestOutputHelper();
                output.Initialize(MessageBus, Test);
                Ambient.Enter(Test, output);
                try
                {
                    Tuple<decimal, string> r = await base.InvokeTestAsync(aggregator);
                    return Tuple.Create(r.Item1, r.Item2 + output.Output);
                }
                finally
                {
                    Ambient.Leave();
                    output.Uninitialize();
                }
            }

            protected override async Task<decimal> InvokeTestMethodAsync(ExceptionAggregator aggregator)
            {
                decimal took = await base.InvokeTestMethodAsync(aggregator);
                return Timing.Reported(Test) ?? took;
            }
        }
    }
}
