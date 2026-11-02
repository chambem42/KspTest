using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace KspTest.Runner
{
    /// <summary>
    ///     The system's memory commit (Windows), and Unity's crash reports.  A modded RSS KSP commits 30 GB or more while it
    ///     loads; once the system's commit total reaches its limit (RAM + pagefile) Unity's allocations fail and it dies with
    ///     an access violation, leaving a crash folder but no KSP.log line saying why.
    /// </summary>
    public static class SystemMemory
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        /// <summary>(commit in use, commit limit) in GB, or null off Windows.</summary>
        public static (double Used, double Limit)? Commit()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return null;
            var s = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref s))
                return null;
            const double gb = 1024.0 * 1024 * 1024;
            return ((s.TotalPageFile - s.AvailPageFile) / gb, s.TotalPageFile / gb);
        }

        private static string CrashRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "Squad", "Kerbal Space Program", "Crashes");

        /// <summary>
        ///     The crash report Unity wrote since <paramref name="since" /> for the KSP in <paramref name="installRoot" />,
        ///     copied into <paramref name="resultsDir" /> (crash-error.log), summarized: what faulted, and whether the system
        ///     was out of commit.  Null when there is none.  Every KSP on the machine writes its reports into the same folder,
        ///     so one whose error.log names another install's executable is not this run's.
        /// </summary>
        public static string? CollectCrash(DateTime since, string resultsDir, string installRoot)
        {
            if (!Directory.Exists(CrashRoot))
                return null;
            string root = Path.GetFullPath(installRoot).TrimEnd('/', '\\');
            foreach (DirectoryInfo dir in new DirectoryInfo(CrashRoot).GetDirectories("Crash_*")
                         .Where(d => d.CreationTime >= since.AddSeconds(-5)).OrderByDescending(d => d.CreationTime))
            {
                string error = Path.Combine(dir.FullName, "error.log");
                if (!File.Exists(error))
                    return $"KSP crashed (report {dir.FullName}, no error.log)";
                string text;
                try { text = File.ReadAllText(error); }
                catch (IOException) { return $"KSP crashed (report {dir.FullName})"; }
                // "D:\Games\KSP\KSP_x64.exe, run by Matt."
                Match exe = Regex.Match(text, @"(?m)^(.+?)[\\/][^\\/]+\.exe, run by");
                if (exe.Success && !string.Equals(Path.GetFullPath(exe.Groups[1].Value.Trim()).TrimEnd('/', '\\'), root, StringComparison.OrdinalIgnoreCase))
                    continue;
                return Summarize(dir, error, text, resultsDir);
            }

            return null;
        }

        private static string Summarize(DirectoryInfo dir, string error, string text, string resultsDir)
        {
            try { File.Copy(error, Path.Combine(resultsDir, "crash-error.log"), true); }
            catch (IOException) { }

            string fault = Regex.Match(text, @"(?m)^.*caused an .*$").Value.Trim();
            string summary = "KSP crashed: " + (fault.Length > 0 ? fault : "see crash-error.log");
            Match commit = Regex.Match(text, @"System Commit Total/Limit/Peak:\s*(\d+)MB/(\d+)MB");
            if (commit.Success)
            {
                double total = double.Parse(commit.Groups[1].Value) / 1024, limit = double.Parse(commit.Groups[2].Value) / 1024;
                summary += $"; system commit {total:F1}/{limit:F1} GB";
                if (total > limit * 0.98)
                    summary += " - out of memory commit: close other programs or enlarge the pagefile";
            }

            return summary + $" ({dir.FullName})";
        }
    }
}
