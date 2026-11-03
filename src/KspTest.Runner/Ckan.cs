using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace KspTest.Runner
{
    /// <summary>
    ///     CKAN's command line, on a game folder given by path (--gamedir: the instances are not registered with CKAN) and
    ///     with no prompts (--headless).  It shares the user's CKAN download cache.  On Linux and macOS ckan.exe runs on
    ///     Mono.
    /// </summary>
    public sealed class Ckan
    {
        public string Exe { get; }

        public Ckan(string exe)
        {
            Exe = Path.GetFullPath(exe);
            if (!File.Exists(Exe))
                throw new FileNotFoundException($"no CKAN at {Exe}");
        }

        /// <summary>Adds a repository to the game folder's: a name from CKAN's list of them, or "name=url".</summary>
        public void AddRepo(string gameDir, string repo, Action<string> log)
        {
            var args = new List<string> { "repo", "add", "--gamedir", gameDir, "--headless" };
            int eq = repo.IndexOf('=');
            args.AddRange(eq < 0 ? new[] { repo.Trim() } : new[] { repo.Substring(0, eq).Trim(), repo.Substring(eq + 1).Trim() });
            Check(Run(log, args.ToArray()), "repo add " + repo);
        }

        /// <summary>The module list, fresh from CKAN's repositories.</summary>
        public void Update(string gameDir, Action<string> log) => Check(Run(log, "update", "--gamedir", gameDir, "--headless"), "update");

        /// <summary>Installs the mods ("Identifier" or "Identifier=version") and what they depend on, no recommendations.</summary>
        public void Install(string gameDir, IEnumerable<string> mods, Action<string> log)
        {
            var args = new List<string> { "install", "--gamedir", gameDir, "--headless", "--no-recommends" };
            args.AddRange(mods);
            Check(Run(log, args.ToArray()), "install " + string.Join(" ", mods));
        }

        /// <summary>The installed modules and their versions, those installed as dependencies too.</summary>
        public Dictionary<string, string> Installed(string gameDir)
        {
            Result r = Run(_ => { }, "list", "--porcelain", "--gamedir", gameDir, "--headless");
            Check(r, "list");
            // "- Identifier version" (explicitly installed) or "+ Identifier version" (a dependency), "@ ..." autodetected ...
            var mods = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in r.Output)
            {
                Match m = Regex.Match(line, @"^[-+@~?!^X]\s+(\S+)\s+(.+)$");
                if (m.Success)
                    mods[m.Groups[1].Value] = m.Groups[2].Value.Trim();
            }

            return mods;
        }

        public sealed class Result
        {
            public int ExitCode;
            public List<string> Output = new List<string>();
        }

        /// <summary>Runs ckan with the arguments; its output (less the download progress) goes to the log.</summary>
        public Result Run(Action<string> log, params string[] args)
        {
            bool mono = !RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            var psi = new ProcessStartInfo
            {
                FileName = mono ? "mono" : Exe,
                Arguments = string.Join(" ", (mono ? new[] { Exe } : new string[0]).Concat(args).Select(Quote)),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            var result = new Result();
            using var p = new Process { StartInfo = psi };
            DataReceivedEventHandler onLine = (_, e) =>
            {
                if (e.Data == null)
                    return;
                // "12.4 MiB left - 0%", rewritten in place with \r
                string line = e.Data.Split('\r').Last().TrimEnd();
                if (line.Length == 0 || Regex.IsMatch(line, @"\d [KMG]?i?B left - \d+%"))
                    return;
                // CKAN's errors carry their newlines URL-encoded
                line = line.Replace("%0A", "\n    ");
                lock (result)
                    result.Output.Add(line);
                log("ckan: " + line);
            };
            p.OutputDataReceived += onLine;
            p.ErrorDataReceived += onLine;
            p.Start();
            p.StandardInput.Close();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            result.ExitCode = p.ExitCode;
            return result;
        }

        private static void Check(Result r, string what)
        {
            // an install CKAN refused still exits 0 in some versions: its "Install canceled" or an ERROR line says so
            string? error = r.Output.FirstOrDefault(l => Regex.IsMatch(l, @"\bERROR\b|Install canceled"));
            if (r.ExitCode != 0 || error != null)
                throw new InvalidOperationException($"ckan {what} failed (exit {r.ExitCode}): " +
                                                    (error ?? string.Join("\n", r.Output.Skip(Math.Max(0, r.Output.Count - 5)))));
        }

        private static string Quote(string a) =>
            a.Length > 0 && a.IndexOfAny(new[] { ' ', '"', '\t' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
    }
}
