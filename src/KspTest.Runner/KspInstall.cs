using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace KspTest.Runner
{
    /// <summary>A KSP install on this machine: where its executable, log and settings are.</summary>
    public sealed class KspInstall
    {
        public string Root { get; }

        public KspInstall(string root)
        {
            Root = Path.GetFullPath(root);
        }

        public string GameData => Path.Combine(Root, "GameData");
        public string Saves => Path.Combine(Root, "saves");
        public string SettingsCfg => Path.Combine(Root, "settings.cfg");
        public string KspLog => Path.Combine(Root, "KSP.log");
        public string HarnessDir => Path.Combine(GameData, "KspTest");
        public string RunCfg => Path.Combine(HarnessDir, "PluginData", "run.cfg");

        /// <summary>The game's executable for this OS.</summary>
        public string Executable
        {
            get
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return Path.Combine(Root, "KSP_x64.exe");
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    return Path.Combine(Root, "KSP.app", "Contents", "MacOS", "KSP");
                return Path.Combine(Root, "KSP.x86_64");
            }
        }

        public bool Exists => File.Exists(Executable);

        public bool HarnessDeployed => File.Exists(Path.Combine(HarnessDir, "Plugins", "KspTest.dll"));

        /// <summary>The process names KSP runs under, on any OS.</summary>
        public static readonly string[] ProcessNames = { "KSP_x64", "KSP.x86_64", "KSP" };

        /// <summary>Every KSP running on this machine, from any install.</summary>
        public static List<Process> RunningKsps() =>
            ProcessNames.SelectMany(n => Process.GetProcessesByName(n)).ToList();

        /// <summary>The running KSPs started from this install.</summary>
        public List<Process> Running() =>
            // the root and a separator: C:\Games\KSP is not C:\Games\KSP@1, a clone of it
            RunningKsps().Where(p => PathOf(p)?.StartsWith(Root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true).ToList();

        public static string? PathOf(Process p)
        {
            try { return p.MainModule?.FileName; }
            catch { return null; }
        }

        public override string ToString() => Root;
    }
}
