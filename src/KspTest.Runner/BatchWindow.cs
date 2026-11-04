using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace KspTest.Runner
{
    /// <summary>
    /// A taskbar entry for a KSP launched with -batchmode, which has no window of its own and so nothing on the taskbar
    /// to say a game is running: a small window (batch-window.ps1, run by Windows PowerShell) with KSP's icon and the
    /// run's name, which shows the test and step running, the results so far and how long KSP has run, has a button that
    /// stops KSP, and closes itself when KSP exits (however it exits: the tests done, the runner's watchdog, a crash).
    /// Windows and an interactive desktop only: not on CI (CI set), nor when KSPTEST_NO_WINDOW is 1.
    /// </summary>
    internal static class BatchWindow
    {
        private const string Resource = "KspTest.Runner.batch-window.ps1";

        public static Process? Start(Process ksp, KspInstall install, RunOptions o, string label, string resultsDir, Action<string> log)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
            if (o.KspArgs == null || o.KspArgs.IndexOf("-batchmode", StringComparison.OrdinalIgnoreCase) < 0) return null;
            if (!Environment.UserInteractive || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) ||
                Environment.GetEnvironmentVariable("KSPTEST_NO_WINDOW") == "1")
                return null;
            try
            {
                string script = WriteScript();
                var psi = new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -STA -File \"{script}\"",
                };
                psi.Environment["KSPTEST_WINDOW_PID"] = ksp.Id.ToString();
                psi.Environment["KSPTEST_WINDOW_TITLE"] = "KSP (batch): " + label;
                psi.Environment["KSPTEST_WINDOW_EXE"] = install.Executable;
                psi.Environment["KSPTEST_WINDOW_INSTALL"] = install.Root;
                psi.Environment["KSPTEST_WINDOW_HARNESS"] = Path.Combine(resultsDir, "harness.log");
                return Process.Start(psi);
            }
            catch (Exception e)
            {
                log("no taskbar window for the batchmode KSP: " + e.Message);
                return null;
            }
        }

        /// <summary>The window closed with its KSP (it closes itself; this is for a runner that ends first).</summary>
        public static void Close(Process? window)
        {
            try
            {
                if (window != null && !window.HasExited)
                    window.Kill();
            }
            catch
            {
                // gone already
            }
        }

        // the script beside the other KspTest files in temp, written again only when this runner's copy differs
        private static string WriteScript()
        {
            string dir = Path.Combine(Path.GetTempPath(), "KspTest");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "batch-window.ps1");
            using Stream s = typeof(BatchWindow).Assembly.GetManifestResourceStream(Resource)
                             ?? throw new InvalidOperationException("the runner has no " + Resource);
            using var r = new StreamReader(s, Encoding.UTF8);
            string text = r.ReadToEnd();
            try
            {
                if (!File.Exists(path) || File.ReadAllText(path) != text)
                    File.WriteAllText(path, text, new UTF8Encoding(true));   // a BOM, so Windows PowerShell reads it as UTF-8
            }
            catch (IOException)
            {
                // a runner beside this one writing it at the same moment: the same script
            }
            return path;
        }
    }
}
