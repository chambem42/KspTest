using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace KspTest.Xunit
{
    /// <summary>
    ///     Where and how the in-game tests run, from ksptest.json beside the test assembly and the environment (which wins):
    ///     <code>
    ///     { "install": "C:/Games/KSP", "saves": "saves", "results": "results", "lockFile": "...", "mute": true,
    ///       "saveBackups": "...", "loadStallSec": 600, "timeoutSec": 3600 }
    ///     </code>
    ///     KSPDIR, KSPTEST_SAVES, KSPTEST_RESULTS, KSPTEST_LOCK_FILE, KSPTEST_MUTE.  Relative paths are from the test assembly.
    /// </summary>
    public sealed class KspSettings
    {
        public string? Install;
        public string Saves = "saves";
        public string Results = "results";
        public string? SaveBackups;
        public string? LockFile;
        public bool Mute = true;
        public int LoadStallSec = 120;
        public int TimeoutSec = 900;

        /// <summary>The in-game test assemblies to deploy, by name (GameData/KspTest/Tests/&lt;name&gt;): ksp-plugins/&lt;name&gt;/*.dll by default.</summary>
        public Dictionary<string, List<string>> Tests = new Dictionary<string, List<string>>();

        public static readonly KspSettings Current = Load();

        private static KspSettings Load()
        {
            string baseDir = AppContext.BaseDirectory;
            var s = new KspSettings();
            string file = Path.Combine(baseDir, "ksptest.json");
            if (File.Exists(file))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                JsonElement r = doc.RootElement;
                s.Install = Str(r, "install") ?? s.Install;
                s.Saves = Str(r, "saves") ?? s.Saves;
                s.Results = Str(r, "results") ?? s.Results;
                s.SaveBackups = Str(r, "saveBackups") ?? s.SaveBackups;
                s.LockFile = Str(r, "lockFile") ?? s.LockFile;
                if (r.TryGetProperty("mute", out JsonElement m) && (m.ValueKind == JsonValueKind.True || m.ValueKind == JsonValueKind.False))
                    s.Mute = m.GetBoolean();
                if (r.TryGetProperty("loadStallSec", out JsonElement l) && l.ValueKind == JsonValueKind.Number)
                    s.LoadStallSec = l.GetInt32();
                if (r.TryGetProperty("timeoutSec", out JsonElement t) && t.ValueKind == JsonValueKind.Number)
                    s.TimeoutSec = t.GetInt32();
            }

            s.Install = Env("KSPDIR") ?? s.Install;
            s.Saves = Env("KSPTEST_SAVES") ?? s.Saves;
            s.Results = Env("KSPTEST_RESULTS") ?? s.Results;
            s.LockFile = Env("KSPTEST_LOCK_FILE") ?? s.LockFile
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KspTest", "ksp.lock");
            if (Env("KSPTEST_MUTE") is string mute)
                s.Mute = mute == "1" || mute.Equals("true", StringComparison.OrdinalIgnoreCase);

            s.Saves = Path.GetFullPath(Path.Combine(baseDir, s.Saves));
            s.Results = Path.GetFullPath(Path.Combine(baseDir, s.Results));

            // in-game assemblies the build copied beside the tests: ksp-plugins/<name>/*.dll
            string plugins = Path.Combine(baseDir, "ksp-plugins");
            if (Directory.Exists(plugins))
                foreach (string dir in Directory.GetDirectories(plugins))
                    s.Tests[Path.GetFileName(dir)] = new List<string>(Directory.GetFiles(dir, "*.dll"));
            return s;
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static string? Env(string name) =>
            Environment.GetEnvironmentVariable(name) is string v && v.Length > 0 ? v : null;
    }
}
