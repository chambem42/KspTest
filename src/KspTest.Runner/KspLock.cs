using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace KspTest.Runner
{
    /// <summary>
    ///     One test run's KSP at a time on this machine.  Every launcher shares a lock file: "pid=&lt;holder&gt; install=...
    ///     test=... started=...".  A run waits while another holds the lock or while a KSP runs from the install it plays in,
    ///     then holds it until its KSP is gone.  A KSP from another folder (someone's game) is not the run's business and
    ///     does not hold it up.  A lock whose pid is no longer running is stale and taken over; one written by hand without a
    ///     pid counts as held for 12 hours.  A script that holds the lock over several runs passes it on by setting
    ///     KSPTEST_LOCK_PID to its pid for the runs it starts.
    ///
    ///     Runs allowing more KSPs at once (<see cref="AcquireSlot" />, each from an install of its own: thin clones) take a
    ///     slot instead, a file beside the lock named for the install ("&lt;lock&gt;.&lt;folder&gt;-&lt;hash&gt;", with a
    ///     "state=loading|loaded" the run moves on once its KSP has loaded).  A slot is taken when no one-KSP run holds the
    ///     lock, no run plays in that install and no KSP runs from it, fewer than the maximum are taken and no other slot's
    ///     KSP is still loading (loads one at a time: a ModuleManager rebuild finished and handed on before the next clone
    ///     loads).  A one-KSP run waits for every slot as for a lock.
    /// </summary>
    public sealed class KspLock : IDisposable
    {
        public const string InheritedLockVariable = "KSPTEST_LOCK_PID";

        private readonly string _path;
        private readonly int _pid = Process.GetCurrentProcess().Id;
        private bool _held;
        private string _text = "";

        private KspLock(string path)
        {
            _path = path;
        }

        /// <summary>Waits for a slot for a run in this install, beside the lock at lockPath, up to maxKsps KSPs at once.</summary>
        public static KspLock AcquireSlot(string lockPath, KspInstall install, int maxKsps, string description, TimeSpan wait, Action<string> log)
        {
            string root = install.Root.TrimEnd('\\', '/');
            // the gate and the slots go beside the lock, whose folder a fresh machine (a CI runner) does not have yet
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(lockPath))!);
            var l = new KspLock(SlotPath(lockPath, root));
            DateTime deadline = DateTime.Now + wait, nextNote = DateTime.Now;
            while (true)
            {
                string? why;
                using (FileMutex.Acquire(lockPath + ".gate", "another run taking a KSP slot", TimeSpan.FromMinutes(1), log))
                {
                    why = SlotBusy(lockPath, install, maxKsps);
                    if (why == null && l.TryCreate($"install={root} state=loading {description}"))
                        return l;
                }

                if (DateTime.Now > deadline)
                    throw new TimeoutException($"gave up after {wait.TotalSeconds:F0} s waiting for a KSP slot: {why}");
                if (DateTime.Now >= nextNote)
                {
                    log($"waiting for a KSP slot: {why}");
                    nextNote = DateTime.Now.AddMinutes(5);
                }

                Thread.Sleep(2000);
            }
        }

        /// <summary>A slot's KSP has loaded: the next slot's may start loading.</summary>
        public void MarkLoaded()
        {
            if (!_held || !_text.Contains("state=loading"))
                return;
            _text = _text.Replace("state=loading", "state=loaded");
            try { File.WriteAllText(_path, _text, Encoding.ASCII); }
            catch (IOException) { /* still loading as far as the others know: they wait for this run instead */ }
        }

        private static string SlotPath(string lockPath, string root)
        {
            uint hash = 2166136261;
            foreach (char c in root.ToLowerInvariant())
                hash = (hash ^ c) * 16777619;
            return $"{lockPath}.{Regex.Replace(Path.GetFileName(root), @"[^A-Za-z0-9_.@-]", "_")}-{hash:x8}";
        }

        // the live slots' texts (stale ones removed)
        private static List<string> LiveSlots(string lockPath)
        {
            var live = new List<string>();
            string dir = Path.GetDirectoryName(Path.GetFullPath(lockPath))!, prefix = Path.GetFileName(lockPath) + ".";
            if (!Directory.Exists(dir))
                return live;
            foreach (string f in Directory.GetFiles(dir, prefix + "*").Where(f => !f.EndsWith(".gate")))
            {
                string text = SafeRead(f);
                Match m = Regex.Match(text, @"pid=(\d+)");
                if (m.Success && IsRunning(int.Parse(m.Groups[1].Value)))
                    live.Add(text);
                else if (m.Success)
                    TryDelete(f);
            }

            return live;
        }

        // why a slot for this install cannot be taken now, or null
        private static string? SlotBusy(string lockPath, KspInstall install, int maxKsps)
        {
            string root = install.Root.TrimEnd('\\', '/');
            if (File.Exists(lockPath))
            {
                string text = SafeRead(lockPath);
                Match m = Regex.Match(text, @"pid=(\d+)");
                if (m.Success ? IsRunning(int.Parse(m.Groups[1].Value)) : DateTime.Now - File.GetLastWriteTime(lockPath) < TimeSpan.FromHours(12))
                    return $"a one-KSP run holds the lock ({text.Trim()})";
            }

            List<string> slots = LiveSlots(lockPath);
            List<string> installs = slots.Select(s => Regex.Match(s, @"install=(.+?) state=").Groups[1].Value).ToList();
            if (installs.Any(i => i.Equals(root, StringComparison.OrdinalIgnoreCase)))
                return $"another run plays in {root}";
            if (slots.Count >= maxKsps)
                return $"{slots.Count} of {maxKsps} slots taken";
            if (slots.FirstOrDefault(s => s.Contains("state=loading")) is string loading)
                return $"a KSP is loading ({loading.Trim()})";
            if (install.Running().FirstOrDefault() is Process here)
                return $"a KSP is running from {root} ({KspInstall.PathOf(here) ?? here.Id.ToString()})";

            return null;
        }

        /// <summary>
        ///     Waits for the lock and takes it (or finds it held by the parent that started this run), for a run that plays
        ///     in install.
        /// </summary>
        public static KspLock Acquire(string path, KspInstall install, string description, TimeSpan wait, Action<string> log)
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

                // only a KSP in the install the run plays in: one from another folder is someone's game
                var others = install.Running();
                if (heldByParent && others.Count == 0)
                    return l; // the parent's lock is this run's; the parent removes it

                // a slot taken for a KSP that has not started yet holds the machine as the lock does
                if (holder == null && LiveSlots(path).FirstOrDefault() is string slot)
                    holder = slot.Trim();

                if (holder == null && others.Count == 0 && l.TryCreate(description))
                    return l;

                if (DateTime.Now > deadline)
                    throw new TimeoutException($"gave up after {wait.TotalSeconds:F0} s waiting for other KSP runs (lock: {holder}; KSP PIDs: {string.Join(",", others.ConvertAll(o => o.Id))})");
                if (DateTime.Now >= nextNote)
                {
                    log(holder != null ? $"waiting for another KSP run to finish: lock held ({holder})"
                        : $"waiting for the KSP running from {install.Root} to exit ({string.Join(", ", others.ConvertAll(o => KspInstall.PathOf(o) ?? o.Id.ToString()))})");
                    nextNote = DateTime.Now.AddMinutes(5);
                }

                Thread.Sleep(2000);   // a check is a file and a process list: cheap, and the next run starts within 2 s of the last
            }
        }

        private bool TryCreate(string description)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                // CreateNew fails if another run created it since the check
                using var fs = new FileStream(_path, FileMode.CreateNew, FileAccess.Write);
                _text = $"pid={_pid} {description} started={DateTime.Now:s}";
                byte[] bytes = Encoding.ASCII.GetBytes(_text);
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
