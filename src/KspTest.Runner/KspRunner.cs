using System;
using System.Diagnostics;
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
            if (!install.HarnessDeployed)
                throw new InvalidOperationException($"the KspTest harness is not deployed into {install.Root} (ksptest deploy --install ...)");
            Action<string> log = o.Log;

            if (install.Running().Count > 0)
                log($"KSP is already running from {install.Root}; waiting for it to exit");

            using KspLock? kspLock = o.LockFile == null ? null
                : KspLock.Acquire(o.LockFile, $"install={install.Root} test={o.Test}", TimeSpan.FromSeconds(o.WaitForOtherKspSec), log);

            string label = Regex.Replace(string.Join("+", o.Test.Split(',', ';').Select(t => t.Trim())), @"[^A-Za-z0-9_+*.-]", "_");
            if (label.Length > 60)
                label = label.Substring(0, 60);
            string resultsDir = Path.GetFullPath(Path.Combine(o.ResultsRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{label}"));
            Directory.CreateDirectory(resultsDir);

            if (!o.NoReset && o.Test != "list")
            {
                if (o.SaveTemplate == null)
                    throw new ArgumentException("a save template is needed (or NoReset)");
                SaveReset.Reset(o.SaveTemplate, Path.Combine(install.Saves, o.SaveFolder),
                    o.SaveBackupRoot ?? Path.Combine(o.ResultsRoot, "..", "save-backups"), o.SaveFolder, log);
            }

            var tweaks = new InstallTweaks(install, log);
            string runCfg = RunCfg(o, resultsDir);
            Process? proc = null;
            string status = "unknown";
            try
            {
                tweaks.DisableQuickStart();
                tweaks.SetWindow(o.Width, o.Height);
                if (o.Mute)
                    tweaks.Mute();

                for (int attempt = 0; ; attempt++)
                {
                    // the harness deletes run.cfg as it reads it (a run it did not live to clean up after must not start
                    // again), so it is written for every start: a retry after a load hang would otherwise start no test
                    Directory.CreateDirectory(Path.GetDirectoryName(install.RunCfg)!);
                    File.WriteAllText(install.RunCfg, runCfg, Encoding.ASCII);
                    proc = Launch(install, o);
                    log($"KSP started (PID {proc.Id}); waiting up to {o.TimeoutSec} s for {o.Test} ...");

                    status = Watch(proc, install, o, resultsDir, attempt, log);
                    if (status != "load-hang" || attempt >= o.LoadRetries)
                        break;
                    log($"starting KSP again ({attempt + 1} of {o.LoadRetries} retries)");
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
                    tweaks.RestoreWindow();
                    tweaks.RestoreVolume();
                }

                if (File.Exists(install.KspLog))
                    TryCopy(install.KspLog, Path.Combine(resultsDir, "KSP.log"));
            }

            return RunResult.Read(resultsDir, status);
        }

        private static string RunCfg(RunOptions o, string resultsDir)
        {
            var sb = new StringBuilder();
            sb.Append("KSPTEST_RUN\r\n{\r\n");
            sb.Append($"    test = {o.Test}\r\n    save = {o.SaveFolder}\r\n    resultsDir = {resultsDir}\r\n");
            sb.Append($"    timeoutSec = {o.TimeoutSec}\r\n    quitWhenDone = {(o.Keep ? "false" : "true")}\r\n");
            sb.Append($"    width = {o.Width}\r\n    height = {o.Height}\r\n    PARAMS\r\n    {{\r\n");
            foreach (var kv in o.Params)
                sb.Append($"        {kv.Key} = {kv.Value}\r\n");
            sb.Append("    }\r\n}\r\n");
            return sb.ToString();
        }

        private static Process Launch(KspInstall install, RunOptions o)
        {
            var psi = new ProcessStartInfo(install.Executable)
            {
                WorkingDirectory = install.Root,
                UseShellExecute = false,
                Arguments = $"-screen-fullscreen 0 -screen-width {o.Width} -screen-height {o.Height} -popupwindow",
            };
            // bdwgc reads its settings from the environment at startup
            if (o.GcDivisor > 0)
                psi.Environment["GC_FREE_SPACE_DIVISOR"] = o.GcDivisor.ToString();
            return Process.Start(psi) ?? throw new InvalidOperationException($"could not start {install.Executable}");
        }

        // Watches the game until it exits, returning the runner's own status ("unknown" when the harness reported).  Stalls,
        // checked every 10 s: before the first test starts, KSP.log and KSPCF's texture cache both untouched for LoadStallSec
        // is a load hang (retried); once the tests run, KSP.log and harness.log both untouched for RunStallSec is a frozen game.
        private static string Watch(Process proc, KspInstall install, RunOptions o, string resultsDir, int attempt, Action<string> log)
        {
            // KSPCF's FastLoader writes a .dds here per texture while it rebuilds its cache (a long load with KSP.log quiet)
            string kspcfCache = Path.Combine(install.GameData, "KSPCommunityFixes", "PluginData", "TextureCache");
            string harnessLog = Path.Combine(resultsDir, "harness.log");
            string resultFile = Path.Combine(resultsDir, "result.json");
            DateTime started = DateTime.Now;
            // the harness has its own timeout; this is a safety net for hangs before it even loads
            DateTime hardDeadline = started.AddSeconds(o.TimeoutSec + 120);
            DateTime? quitDeadline = null;
            DateTime nextCheck = started.AddSeconds(10);
            bool begun = false, finished = false;

            while (!proc.HasExited)
            {
                Thread.Sleep(2000);
                if (DateTime.Now > nextCheck)
                {
                    nextCheck = DateTime.Now.AddSeconds(10);
                    DateTime lastWrite = started;
                    foreach (string path in begun ? new[] { install.KspLog, harnessLog } : new[] { install.KspLog, kspcfCache })
                    {
                        DateTime w = File.Exists(path) ? File.GetLastWriteTime(path) : Directory.Exists(path) ? Directory.GetLastWriteTime(path) : DateTime.MinValue;
                        if (w > lastWrite)
                            lastWrite = w;
                    }

                    if (!begun && File.Exists(install.KspLog) && File.GetLastWriteTime(install.KspLog) > started && LogHas(install.KspLog, "[KspTest] ==== TEST"))
                        begun = true;
                    double quiet = (DateTime.Now - lastWrite).TotalSeconds;
                    if (!begun && quiet > o.LoadStallSec)
                    {
                        log($"WARNING: KSP hung while loading (KSP.log silent since {lastWrite:HH:mm:ss}); killing PID {proc.Id}");
                        Kill(proc);
                        TryCopy(install.KspLog, Path.Combine(resultsDir, $"KSP.loadhang{attempt + 1}.log"));
                        return "load-hang";
                    }

                    if (begun && !finished && quiet > o.RunStallSec)
                    {
                        log($"WARNING: KSP froze during the test (KSP.log and harness.log silent since {lastWrite:HH:mm:ss}); killing PID {proc.Id}");
                        Kill(proc);
                        return "frozen";
                    }
                }

                // result.json is rewritten after every test; only a final status means the harness has asked KSP to quit.
                // A modded KSP can hang in teardown, so it gets 60 s and is then killed.
                finished = File.Exists(resultFile) && !Regex.IsMatch(SafeRead(resultFile), "\"status\":\\s*\"running\"");
                if (finished)
                {
                    if (o.Keep)
                        return "unknown"; // done, KSP left open
                    if (quitDeadline == null)
                        quitDeadline = DateTime.Now.AddSeconds(60);
                    else if (DateTime.Now > quitDeadline)
                    {
                        log($"WARNING: KSP did not exit within 60 s of writing result.json; killing PID {proc.Id}");
                        Kill(proc);
                        return "unknown";
                    }
                }

                if (DateTime.Now > hardDeadline)
                {
                    log($"WARNING: hard timeout: killing KSP PID {proc.Id}");
                    Kill(proc);
                    return "timeout";
                }
            }

            return "unknown";
        }

        private static bool LogHas(string path, string text)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                string? line;
                while ((line = reader.ReadLine()) != null)
                    if (line.Contains(text))
                        return true;
            }
            catch (IOException) { }

            return false;
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
