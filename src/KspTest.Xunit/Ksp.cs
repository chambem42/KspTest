using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KspTest.Runner;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace KspTest.Xunit
{
    /// <summary>A test that runs in KSP: skipped (with the reason) when no KSP install is configured.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    [XunitTestCaseDiscoverer("Xunit.Sdk.FactDiscoverer", "xunit.execution.{Platform}")]
    public sealed class KspFactAttribute : FactAttribute
    {
        public KspFactAttribute()
        {
            string? install = KspSettings.Current.Install;
            if (install == null)
                Skip = "no KSP install configured (KSPDIR, or \"install\" in ksptest.json)";
            else if (!new KspInstall(install).Exists)
                Skip = $"no KSP install at {install}";
        }
    }

    /// <summary>Runs in-game tests from xUnit tests.</summary>
    public static class Ksp
    {
        private static readonly object DeployLock = new object();
        private static bool _deployed;

        /// <summary>
        ///     Runs the in-game test(s) <paramref name="test" /> (a name, or several comma separated: one KSP session) with
        ///     the parameters "k=v;k=v", on the save template <paramref name="save" /> (a folder, or its name under the
        ///     settings' saves).  Writes the report to the test's output, and fails the test unless they all pass or are
        ///     skipped.  The harness and the project's in-game assemblies are deployed into the install the first time.
        /// </summary>
        public static RunResult Run(ITestOutputHelper output, string test, string? parameters = null, string save = "stock-sandbox",
            Action<RunOptions>? configure = null)
        {
            KspSettings s = KspSettings.Current;
            if (s.Install == null)
                throw new SkipException("no KSP install configured");
            var install = new KspInstall(s.Install);
            Deploy(install, output);

            var o = new RunOptions
            {
                Test = test,
                Install = install.Root,
                SaveTemplate = Directory.Exists(save) ? save : Path.Combine(s.Saves, save),
                ResultsRoot = s.Results,
                SaveBackupRoot = s.SaveBackups,
                LockFile = s.LockFile,
                Mute = s.Mute,
                LoadStallSec = s.LoadStallSec,
                TimeoutSec = s.TimeoutSec,
                Log = line => output.WriteLine(line),
            }.WithParams(parameters);
            configure?.Invoke(o);

            RunResult r = KspRunner.Run(o);
            output.WriteLine("");
            output.WriteLine(r.Report());
            if (!r.Passed)
            {
                TestResult? failed = r.Tests.FirstOrDefault(t => !t.Passed);
                throw new XunitException($"{test}: {r.Status}" + (failed != null ? $" - {failed.Test}: {failed.Message.Split('\n')[0]}" : "") +
                                         $" (results: {r.ResultsDir})");
            }

            return r;
        }

        private static void Deploy(KspInstall install, ITestOutputHelper output)
        {
            lock (DeployLock)
            {
                if (_deployed)
                    return;
                if (install.Running().Count > 0)
                    output.WriteLine($"KSP is running from {install.Root}: deploying anyway (it loads plugins at startup)");
                Deployer.DeployHarness(install, null, output.WriteLine);
                foreach (KeyValuePair<string, List<string>> t in KspSettings.Current.Tests)
                    Deployer.DeployTests(install, t.Key, t.Value, output.WriteLine);
                _deployed = true;
            }
        }
    }

    /// <summary>Thrown to skip a test from inside it.</summary>
    public sealed class SkipException : Exception
    {
        public SkipException(string reason) : base("$XunitDynamicSkip$" + reason) { }
    }
}
