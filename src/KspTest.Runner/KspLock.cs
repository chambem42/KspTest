using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace KspTest.Runner
{
    /// <summary>
    ///     One KSP at a time on this machine (two modded RSS games at once ran the GPU out of memory).  Every launcher shares a
    ///     lock file: "pid=&lt;holder&gt; install=... test=... started=...".  A run waits while any KSP runs, from any install,
    ///     or while another holds the lock, then holds it until its KSP is gone.  A lock whose pid is no longer running is
    ///     stale and taken over; one written by hand without a pid counts as held for 12 hours.  A script that holds the lock
    ///     over several runs passes it on by setting KSPTEST_LOCK_PID to its pid for the runs it starts.
    /// </summary>
    public sealed class KspLock : IDisposable
    {
        public const string InheritedLockVariable = "KSPTEST_LOCK_PID";

        private readonly string _path;
        private readonly int _pid = Process.GetCurrentProcess().Id;
        private bool _held;

        private KspLock(string path)
        {
            _path = path;
        }

        /// <summary>Waits for the lock and takes it (or finds it held by the parent that started this run).</summary>
        public static KspLock Acquire(string path, string description, TimeSpan wait, Action<string> log)
        {
            var l = new KspLock(path);
            int inherited = int.TryParse(Environment.GetEnvironmentVariable(InheritedLockVariable), out int p) ? p : 0;
            DateTime deadline = DateTime.Now + wait, nextNote = DateTime.Now;

            while (true)
            {
                string? holder = null;
                bool heldByParent = false;
                if (File.Exists(path))
                {
                    string text = SafeRead(path);
                    Match m = Regex.Match(text, @"pid=(\d+)");
                    bool live = m.Success ? IsRunning(int.Parse(m.Groups[1].Value)) : DateTime.Now - File.GetLastWriteTime(path) < TimeSpan.FromHours(12);
                    if (live)
                        holder = text.Trim();
                    else
                        TryDelete(path);
                    heldByParent = live && m.Success && inherited != 0 && int.Parse(m.Groups[1].Value) == inherited;
                }

                var others = KspInstall.RunningKsps();
                if (heldByParent && others.Count == 0)
                    return l; // the parent's lock is this run's; the parent removes it

                if (holder == null && others.Count == 0 && l.TryCreate(description))
                    return l;

                if (DateTime.Now > deadline)
                    throw new TimeoutException($"gave up after {wait.TotalSeconds:F0} s waiting for other KSP runs (lock: {holder}; KSP PIDs: {string.Join(",", others.ConvertAll(o => o.Id))})");
                if (DateTime.Now >= nextNote)
                {
                    log(holder != null ? $"waiting for another KSP run to finish: lock held ({holder})"
                        : $"waiting for another KSP run to finish: KSP running ({string.Join(", ", others.ConvertAll(o => KspInstall.PathOf(o) ?? o.Id.ToString()))})");
                    nextNote = DateTime.Now.AddMinutes(5);
                }

                Thread.Sleep(15000);
            }
        }

        private bool TryCreate(string description)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                // CreateNew fails if another run created it since the check
                using var fs = new FileStream(_path, FileMode.CreateNew, FileAccess.Write);
                byte[] bytes = Encoding.ASCII.GetBytes($"pid={_pid} {description} started={DateTime.Now:s}");
                fs.Write(bytes, 0, bytes.Length);
                _held = true;
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// <summary>Removes the lock if this process holds it.</summary>
        public void Dispose()
        {
            if (_held && Regex.IsMatch(SafeRead(_path), $@"pid={_pid}\b"))
                TryDelete(_path);
            _held = false;
        }

        private static bool IsRunning(int pid)
        {
            try { return !Process.GetProcessById(pid).HasExited; }
            catch { return false; }
        }

        private static string SafeRead(string path)
        {
            try { return File.ReadAllText(path); }
            catch { return ""; }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch { /* someone else's to clean up */ }
        }
    }
}
