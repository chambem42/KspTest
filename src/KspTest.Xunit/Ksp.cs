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
    /// <summary>
    ///     A test that runs in KSP: skipped (with the reason) when no KSP install is configured, or the install lacks a file
    ///     the test <see cref="Requires" />.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    [XunitTestCaseDiscoverer("Xunit.Sdk.FactDiscoverer", "xunit.execution.{Platform}")]
    public sealed class KspFactAttribute : FactAttribute
    {
        private string? _skip;

        public KspFactAttribute()
        {
            string? install = KspSettings.Current.Install;
            if (install == null)
                _skip = "no KSP install configured (KSPDIR, or \"install\" in ksptest.json)";
            else if (!new KspInstall(install).Exists)
                _skip = $"no KSP install at {install}";
        }

        /// <summary>Files under GameData the test needs, ";" separated, such as "MechJeb2/Plugins/MechJeb2.dll".</summary>
        public string? Requires { get; set; }

        public override string? Skip
        {
            get
            {
                if (_skip != null || Requires == null)
                    return _skip;
                string gameData = new KspInstall(KspSettings.Current.Install!).GameData;
                string[] missing = Requires.Split(';').Select(r => r.Trim())
                    .Where(r => r.Length > 0 && !File.Exists(Path.Combine(gameData, r)) && !Directory.Exists(Path.Combine(gameData, r))).ToArray();
                return missing.Length > 0 ? $"the KSP install has no {string.Join(", ", missing)}" : null;
            }
            set => _skip = value;
        }
    }

    /// <summary>Runs in-game tests from xUnit tests.</summary>
    public static class Ksp
    {
        private static readonly object DeployLock = new object();
        private static bool _deployed;

        /// <summary>
        ///     Runs the in-game test(s) <paramref name="test" /> (a name, or several comma separated: one KSP session) with
        ///     the parameters "k=v;k=v" (the settings' params over them), on the save template <paramref name="save" /> (a
        ///     folder, or its name under the settings' saves), or without one in a new sandbox game.  The settings' crafts
        ///     (the project's ksp-crafts/ among them) go into the save.  Writes the report to the test's output, and fails
        ///     the test unless they all pass or are skipped.  The harness and the project's in-game assemblies are deployed
        ///     into the install the first time.
        /// </summary>
        public static RunResult Run(ITestOutputHelper output, string test, string? parameters = null, string? save = null,
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
                SaveTemplate = save == null ? null : Directory.Exists(save) ? save : Path.Combine(s.Saves, save),
                ResultsRoot = s.Results,
                SaveBackupRoot = s.SaveBackups,
                LockFile = s.LockFile,
                Mute = s.Mute,
                LoadStallSec = s.LoadStallSec,
                TimeoutSec = s.TimeoutSec,
                Log = line => output.WriteLine(line),
            }.WithParams(parameters).WithParams(s.Params);
            o.Crafts.AddRange(s.Crafts);
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
                {
                    List<string> missing = KspSettings.Current.TestRequires.TryGetValue(t.Key, out List<string>? needs)
                        ? needs.Where(n => !File.Exists(Path.Combine(install.GameData, n))).ToList()
                        : new List<string>();
                    if (missing.Count == 0)
                    {
                        Deployer.DeployTests(install, t.Key, t.Value, output.WriteLine);
                        continue;
                    }

                    output.WriteLine($"skip   {t.Key} (this install has no {string.Join(", ", missing)})");
                    string stale = Path.Combine(install.HarnessDir, "Tests", t.Key);
                    if (Directory.Exists(stale))
                        Directory.Delete(stale, true);
                }
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
