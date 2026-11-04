using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace KspTest.Runner
{
    /// <summary>
    ///     Runs one or more in-game tests in a single KSP session: waits its turn for KSP, resets the test save, writes the
    ///     harness's run.cfg, launches KSP, watches it until it exits and collects result.json, screenshots and KSP.log.
    /// </summary>
    public static class KspRunner
    {
        public static RunResult Run(RunOptions o)
        {
            var install = new KspInstall(o.Install);
            if (!install.Exists)
                throw new FileNotFoundException($"no KSP executable under {install.Root} ({install.Executable})");
            Action<string> log = o.Log;
            // a milestone: in the log, and as progress
            void Say(string line)
            {
                log(line);
                o.Progress?.Invoke(line);
            }

            if (install.Running().Count > 0)
                Say($"KSP is already running from {install.Root}; waiting for it to exit");

            string[] names = RunOptions.TestNames(o.Test);
            string tests = (names.FirstOrDefault() ?? "") + (names.Length > 1 ? $" +{names.Length - 1}" : "");
            using KspLock? kspLock = o.LockFile == null ? null
                : o.MaxKsps > 1 ? KspLock.AcquireSlot(o.LockFile, install, o.MaxKsps, $"test={tests}", TimeSpan.FromSeconds(o.WaitForOtherKspSec), Say)
                : KspLock.Acquire(o.LockFile, $"install={install.Root} test={tests}", TimeSpan.FromSeconds(o.WaitForOtherKspSec), Say);
            // once the tests start: the next slot's KSP may load, and the caller has a look at the loaded install
            void Loaded()
            {
                kspLock?.MarkLoaded();
                o.Loaded?.Invoke(install);
            }

            // with the lock held no other run is deploying into this install or playing in it
            o.Deploy?.Invoke(install);
            if (!install.HarnessDeployed)
                throw new InvalidOperationException($"the KspTest harness is not deployed into {install.Root} (ksptest deploy --install ...)");

            // the first test and how many more (a wildcard's "*" made "_": Windows allows none in a path): screenshots go two
            // folders deeper with long names of their own, and Unity cannot write past Windows' 260 character path limit
            string label = Regex.Replace(names.FirstOrDefault() ?? "run", @"[^A-Za-z0-9_.-]", "_");
            if (label.Length > 40)
                label = label.Substring(0, 40);
            if (names.Length > 1)
                label += $"+{names.Length - 1}";
            // runs side by side can start in the same second: the install (a clone's "@n") tells them apart
            if (o.MaxKsps > 1)
                label += "@" + Regex.Replace(Path.GetFileName(install.Root.TrimEnd('\\', '/')).Split('@').Last(), @"[^A-Za-z0-9_.-]", "_");
            string resultsDir = Path.GetFullPath(Path.Combine(o.ResultsRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{label}"));
            Directory.CreateDirectory(resultsDir);
            o.ResultsDirReady?.Invoke(resultsDir);
            log($"results: {resultsDir}");

            string saveFolder = o.SaveFolder ?? RunOptions.AutoSaveFolder(o.Test);
            string save = Path.Combine(install.Saves, saveFolder);
            log($"save: {save}");
            if (!o.NoReset && o.Test != "list")
                SaveReset.Reset(o.SaveTemplate, save, o.SaveBackupRoot ?? Path.Combine(o.ResultsRoot, "..", "save-backups"), saveFolder, log);
            if (o.Crafts.Count > 0)
                SaveReset.PlaceCrafts(o.Crafts, save, log);

            if (SystemMemory.Commit() is (double used, double limit))
                log($"system commit {used:F1}/{limit:F1} GB ({limit - used:F1} GB free for KSP)");

            var tweaks = new InstallTweaks(install, log);
            DateTime runStarted = DateTime.Now;
            string runCfg = RunCfg(o, saveFolder, resultsDir);
            Process? proc = null;
            string status = "unknown";
            try
            {
                tweaks.DisableQuickStart();
                tweaks.AnswerFirstRunQuestions();
                tweaks.SetWindow(o.Width, o.Height);
                if (o.Mute)
                    tweaks.Mute();

                for (int attempt = 0; ; attempt++)
                {
                    // the harness deletes run.cfg as it reads it (a run it did not live to clean up after must not start
                    // again), so it is written for every start: a retry after a load hang would otherwise start no test
                    Directory.CreateDirectory(Path.GetDirectoryName(install.RunCfg)!);
                    File.WriteAllText(install.RunCfg, runCfg, Encoding.ASCII);
                    proc = Launch(install, o, resultsDir);
                    log($"KSP started (PID {proc.Id}); running {o.Test} ...");
                    o.Progress?.Invoke($"KSP started (PID {proc.Id}), {names.Length} test(s); loading");

                    status = Watch(proc, install, o, resultsDir, attempt, log, Say, Loaded);
                    if (status != "load-hang" || attempt >= o.LoadRetries)
                        break;
                    Say($"starting KSP again ({attempt + 1} of {o.LoadRetries} retries)");
                }
            }
            finally
            {
                tweaks.RestoreQuickStart();
                TryDelete(install.RunCfg);
                // the player's window prefs and volume back once KSP has written its own on exit (left alone while a Keep
                // KSP still runs)
                if (proc != null && !o.Keep)
                    proc.WaitForExit(15000);
                if (proc == null || proc.HasExited)
                {
                    tweaks.RestoreSettings();
                    tweaks.RestoreWindow();
                }

                if (File.Exists(install.KspLog))
                    TryCopy(install.KspLog, Path.Combine(resultsDir, "KSP.log"));
                if (proc == null || proc.HasExited)
                    foreach (string rel in o.Collect)
                    {
                        string from = Path.Combine(install.Root, rel);
                        if (!File.Exists(from))
                        {
                            log($"collect: no {rel} in the install");
                            continue;
                        }
                        string to = Path.Combine(resultsDir, "collected", rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        TryCopy(from, to);
                        log($"collected {rel}");
                    }
            }

            RunResult result = RunResult.Read(resultsDir, status);
            if (result.Status == "no-result" && SystemMemory.CollectCrash(runStarted, resultsDir, install.Root) is string crash)
            {
                result.Status = "crashed";
                result.Crash = crash;
            }

            return result;
        }

        private static string RunCfg(RunOptions o, string saveFolder, string resultsDir)
        {
            var sb = new StringBuilder();
            sb.Append("KSPTEST_RUN\r\n{\r\n");
            sb.Append($"    test = {o.Test}\r\n    scope = {string.Join(",", o.Scope)}\r\n    save = {saveFolder}\r\n    newGame = {o.NewGame}\r\n    resultsDir = {resultsDir}\r\n");
            sb.Append($"    timeoutSec = {o.TimeoutSec}\r\n    loadTimeoutSec = {o.LoadTimeoutSec}\r\n    quitWhenDone = {(o.Keep ? "false" : "true")}\r\n");
            sb.Append($"    width = {o.Width}\r\n    height = {o.Height}\r\n");
            if (o.TextureLimit > 0) sb.Append($"    textureLimit = {o.TextureLimit}\r\n");
            sb.Append("    PARAMS\r\n    {\r\n");
            foreach (var kv in o.Params)
                sb.Append($"        {kv.Key} = {kv.Value}\r\n");
            sb.Append("    }\r\n}\r\n");
            return sb.ToString();
        }

        private static Process Launch(KspInstall install, RunOptions o, string resultsDir)
        {
            // Unity's Player.log is one per user (LocalLow\Squad\Kerbal Space Program): KSPs side by side each write their own
            string playerLog = o.MaxKsps > 1 ? $"-logFile \"{Path.Combine(resultsDir, "Player.log")}\" " : "";
            var psi = new ProcessStartInfo(install.Executable)
            {
                WorkingDirectory = install.Root,
                UseShellExecute = false,
                Arguments = $"-screen-fullscreen 0 -screen-width {o.Width} -screen-height {o.Height} -popupwindow {playerLog}{o.KspArgs}".TrimEnd(),
            };
            // bdwgc reads its settings from the environment at startup
            if (o.GcDivisor > 0)
                psi.Environment["GC_FREE_SPACE_DIVISOR"] = o.GcDivisor.ToString();
            return Process.Start(psi) ?? throw new InvalidOperationException($"could not start {install.Executable}");
        }

        // Watches the game until it exits, returning the runner's own status ("unknown" when the harness reported).  Stalls,
        // checked every 10 s: before the first test starts, KSP.log and KSPCF's texture cache both untouched for LoadStallSec
        // is a load hang (retried); once the tests run, KSP.log and harness.log both untouched for RunStallSec is a frozen game.
        // The harness times the load and each test itself; the runner kills KSP only when it has not, a while past the
        // load's timeout or the running test's (from its TEST line in KSP.log).
        private static string Watch(Process proc, KspInstall install, RunOptions o, string resultsDir, int attempt, Action<string> log, Action<string> say,
                                    Action loaded)
        {
            var reported = new HashSet<string>();
            // KSPCF's FastLoader writes a .dds here per texture while it rebuilds its cache (a long load with KSP.log quiet)
            string kspcfCache = Path.Combine(install.GameData, "KSPCommunityFixes", "PluginData", "TextureCache");
            // ModuleManager patches on a thread of its own, logging only here (minutes with KSP.log quiet when its cache is stale)
            string mmLog = Path.Combine(install.Root, "Logs", "ModuleManager", "ModuleManager.log");
            string harnessLog = Path.Combine(resultsDir, "harness.log");
            string resultFile = Path.Combine(resultsDir, "result.json");
            DateTime started = DateTime.Now;
            DateTime hardDeadline = started.AddSeconds(o.LoadTimeoutSec + HardMarginSec);
            DateTime? quitDeadline = null;
            DateTime nextCheck = started.AddSeconds(10);
            bool begun = false, finished = false;
            var kspLog = new LogFollower(install.KspLog, started);

            var startedTests = new HashSet<string>();

            // result.json, rewritten after every test: each test's result as soon as the harness has it, in the order the
            // tests ran (only those said to have started, until the look's last; a test can end before it is seen to
            // start); only a final status means the harness has asked KSP to quit
            void Results(bool all)
            {
                string json = File.Exists(resultFile) ? SafeRead(resultFile) : "";
                finished = json.Length > 0 && !Regex.IsMatch(json, "\"status\":\\s*\"running\"");
                if (json.Length > 0)
                    foreach (TestResult t in RunResult.ParseTests(json).Where(t => t.Status != "running" && (all || startedTests.Contains(t.Test)) && reported.Add(t.Test)))
                        o.TestDone?.Invoke(t);
            }

            while (!proc.HasExited)
            {
                Thread.Sleep(2000);

                // what KSP.log has added since the last look: each test starting, with its timeout (after the results of
                // those before it), or before the first the harness itself failing to load (a missing dependency, a type
                // that does not load), when no test will start however long KSP runs or however often it is started again
                foreach (string line in kspLog.NewLines())
                {
                    Match test = TestLine.Match(line);
                    if (test.Success)
                    {
                        if (!begun)
                        {
                            o.Progress?.Invoke($"KSP loaded in {(DateTime.Now - started).TotalSeconds:F0} s");
                            loaded();
                        }
                        begun = true;
                        hardDeadline = DateTime.Now.AddSeconds(double.Parse(test.Groups[2].Value, CultureInfo.InvariantCulture) + HardMarginSec);
                        Results(false);
                        startedTests.Add(test.Groups[1].Value);
                        o.Progress?.Invoke($"{test.Groups[1].Value}: started (timeout {test.Groups[2].Value} s)");
                    }
                    else if (!begun && line.StartsWith("[EXC ") && HarnessLine.IsMatch(line))
                    {
                        say($"ERROR: the KspTest harness failed to load: {line}; killing PID {proc.Id}");
                        Kill(proc);
                        return "harness-failed";
                    }
                }

                Results(true);

                if (DateTime.Now > nextCheck)
                {
                    nextCheck = DateTime.Now.AddSeconds(10);
                    DateTime lastWrite = started;
                    foreach (string path in begun ? new[] { install.KspLog, harnessLog } : new[] { install.KspLog, kspcfCache, mmLog })
                    {
                        DateTime w = File.Exists(path) ? File.GetLastWriteTime(path) : Directory.Exists(path) ? Directory.GetLastWriteTime(path) : DateTime.MinValue;
                        if (w > lastWrite)
                            lastWrite = w;
                    }

                    // KSPBurst compiling (its bcl.exe, a minute or more after a mod or install change) with KSP.log quiet
                    if (!begun && Process.GetProcessesByName("bcl").Length > 0)
                        lastWrite = DateTime.Now;

                    double quiet = (DateTime.Now - lastWrite).TotalSeconds;
                    if (!begun && quiet > o.LoadStallSec)
                    {
                        say($"WARNING: KSP hung while loading (KSP.log silent since {lastWrite:HH:mm:ss}); killing PID {proc.Id}");
                        Kill(proc);
                        TryCopy(install.KspLog, Path.Combine(resultsDir, $"KSP.loadhang{attempt + 1}.log"));
                        return "load-hang";
                    }

                    if (begun && !finished && quiet > o.RunStallSec)
                    {
                        say($"WARNING: KSP froze during the test (KSP.log and harness.log silent since {lastWrite:HH:mm:ss}); killing PID {proc.Id}");
                        Kill(proc);
                        return "frozen";
                    }
                }

                // a modded KSP can hang in teardown, so it gets 60 s and is then killed
                if (finished)
                {
                    if (o.Keep)
                        return "unknown"; // done, KSP left open
                    if (quitDeadline == null)
                        quitDeadline = DateTime.Now.AddSeconds(60);
                    else if (DateTime.Now > quitDeadline)
                    {
                        say($"WARNING: KSP did not exit within 60 s of writing result.json; killing PID {proc.Id}");
                        Kill(proc);
                        return "unknown";
                    }
                }

                if (DateTime.Now > hardDeadline)
                {
                    say($"WARNING: the harness did not end the load or test past its timeout; killing KSP PID {proc.Id}");
                    Kill(proc);
                    return "timeout";
                }
            }

            return "unknown";
        }

        // an exception about the harness itself: one of its assemblies by name, or a file under GameData/KspTest (an instance's
        // own folder, %LOCALAPPDATA%/KspTest/instances, is in every path there and says nothing)
        private static readonly Regex HarnessLine = new Regex(@"\bKspTest(\.\w+)*, Version=|[\\/]GameData[\\/]KspTest[\\/]");

        // the harness's line as a test starts (TestRunner.RunOne): "[KspTest] ==== TEST name (starts at Vab, timeout 900s) ===="
        private static readonly Regex TestLine = new Regex(@"\[KspTest\] ==== TEST (\S+) .*timeout (\d+)s\) ====");

        // past a timeout, the harness's time to end the test (screenshot, diagnostics) and write its result
        private const int HardMarginSec = 120;

        /// <summary>
        ///     The lines a log written by another process has added since the last look (a modded load writes tens of MB:
        ///     not read again from the start every time).  Nothing until it is written after since (the previous run's
        ///     log is still there before KSP starts a new one), and from the start again when it is cut short.
        /// </summary>
        private sealed class LogFollower
        {
            private readonly string _path;
            private readonly DateTime _since;
            private long _offset;
            private string _partial = "";

            public LogFollower(string path, DateTime since)
            {
                _path = path;
                _since = since;
            }

            public IEnumerable<string> NewLines()
            {
                var info = new FileInfo(_path);
                if (!info.Exists || info.LastWriteTime <= _since)
                    return Array.Empty<string>();
                if (info.Length < _offset)
                {
                    _offset = 0;
                    _partial = "";
                }

                string text;
                try
                {
                    using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    fs.Seek(_offset, SeekOrigin.Begin);
                    text = new StreamReader(fs).ReadToEnd();
                    _offset = fs.Position;
                }
                catch (IOException)
                {
                    return Array.Empty<string>();
                }

                string[] lines = (_partial + text).Split('\n');
                _partial = lines[lines.Length - 1];   // the line still being written
                return lines.Take(lines.Length - 1).Select(l => l.TrimEnd('\r'));
            }
        }

        private static string SafeRead(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return new StreamReader(fs).ReadToEnd();
            }
            catch (IOException) { return ""; }
        }

        private static void Kill(Process p)
        {
            try { p.Kill(); p.WaitForExit(30000); }
            catch (InvalidOperationException) { /* already gone */ }
        }

        private static void TryCopy(string from, string to)
        {
            try
            {
                using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var dst = File.Create(to);
                src.CopyTo(dst);
            }
            catch (IOException) { }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
        }
    }
}
