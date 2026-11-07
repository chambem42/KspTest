using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using KspTest.Runner;

namespace KspTest.Xunit
{
    /// <summary>
    ///     Where and how the in-game tests run, from ksptest.json beside the test assembly and the environment (which wins):
    ///     <code>
    ///     { "install": "C:/Games/KSP", "results": "results", "lockFile": "...", "mute": true,
    ///       "saveBackups": "...", "loadStallSec": 600, "timeoutSec": 3600,
    ///       "crafts": ["C:/my crafts"], "params": "crafts=My Plane+My Other Plane", "mods": ["Kopernicus"] }
    ///     </code>
    ///     KSPDIR, KSPTEST_RESULTS, KSPTEST_LOCK_FILE, KSPTEST_MUTE, KSPTEST_CRAFTS (paths, ";" separated),
    ///     KSPTEST_PARAMS, KSPTEST_MODS, KSPTEST_WINDOW, KSPTEST_COLLECT.  Relative paths are from the test assembly.  crafts go into every run's save beside
    ///     the test project's own (ksp-crafts/), and params override the tests' own: to fly the tests on your own vehicles.
    ///     With no install, the tests run in a KSP instance KspTest makes (<see cref="InstanceManager" />) with the
    ///     project's KspMod items (ksptest-mods.txt) and the settings' mods, plus the test's own ([KspFact(Mods)]): CKAN
    ///     identifiers, and local mods (paths; the project's in ksptest-overlays.txt) copied in for each run.  Save templates are the
    ///     project's own (KspTestSave items, copied to ksp-saves/), never a player's.
    /// </summary>
    public sealed class KspSettings
    {
        public string? Install;
        /// <summary>The save templates the test project ships (KspTestSave items): ksp-saves/ beside the tests.</summary>
        public string Saves = "ksp-saves";
        public string Results = "results";
        public string? SaveBackups;
        public string? LockFile;
        public bool Mute = true;
        public int LoadStallSec = 120;
        /// <summary>A test's timeout when its [UITest] sets none.</summary>
        public int TimeoutSec = 900;

        /// <summary>
        ///     KSPs at once ("maxKsps", KSPTEST_MAX_KSPS): above 1, a long shared session is split into shards that run side
        ///     by side, each in a thin clone of its instance (<see cref="Batching" />).  1 (the default) runs one at a time.
        /// </summary>
        public int MaxKsps = 1;

        /// <summary>
        ///     Which share of the tests this run is, of how many (KSPTEST_SHARD "k/N"): CI jobs splitting the tests between
        ///     them, each running the tests <see cref="Batching" /> deals it and skipping the rest. 1 of 1 runs them all.
        /// </summary>
        public int Shard = 1, Shards = 1;

        /// <summary>
        ///     More results folders the shards' durations come from ("durationsFrom", KSPTEST_DURATIONS ";"-separated) besides
        ///     the run's own: runs alike kept elsewhere, such as earlier coverage runs, whose tests take many times as long.
        /// </summary>
        public List<string> DurationsFrom = new List<string>();

        /// <summary>
        ///     The in-game waits and test timeouts stretched by this ("timeoutScale", KSPTEST_TIMEOUT_SCALE): for a run known
        ///     to be slow, such as a coverage build with several KSPs at once.  1 (the default): as the tests have them.
        /// </summary>
        public double TimeoutScale = 1;

        /// <summary>More arguments for KSP ("kspArgs", KSPTEST_KSP_ARGS), such as "-batchmode".</summary>
        public string? KspArgs;

        /// <summary>Low-res textures outside screenshots ("textures", KSPTEST_TEXTURES): full, low or 0-3.</summary>
        public string? Textures;

        /// <summary>The game's window, and so the screenshots' size ("window", KSPTEST_WINDOW): "1280x960"; 1920x1080 by default.</summary>
        public string? Window;

        /// <summary>Crafts (files or folders) put into every run's save: the test project's ksp-crafts/, then the settings'.</summary>
        public List<string> Crafts = new List<string>();

        /// <summary>CKAN identifiers every run's instance has: the project's KspMod items, then the settings'.</summary>
        public List<string> Mods = new List<string>();

        /// <summary>
        ///     Local mods every run's instance has (the project's KspOverlay items and KspMod items that are paths, then the
        ///     settings'): GameData/ModName folders, built from source (over CKAN's copy, if any) or not on CKAN.  Like the
        ///     CKAN mods they decide which instance a run gets.
        /// </summary>
        public List<string> Overlays = new List<string>();

        /// <summary>"k=v;k=v" applied over every run's parameters.</summary>
        public string? Params;

        /// <summary>
        ///     Files in the install (relative to its root) each run copies into its results folder's collected/ once KSP has
        ///     exited ("collect" in ksptest.json, KSPTEST_COLLECT ";"-separated): a coverage recorder's report, say.
        /// </summary>
        public List<string> Collect = new List<string>();

        /// <summary>The in-game test assemblies to deploy, by name (GameData/KspTest/Tests/&lt;name&gt;): ksp-plugins/&lt;name&gt;/*.dll by default.</summary>
        public Dictionary<string, List<string>> Tests = new Dictionary<string, List<string>>();

        /// <summary>Per test assembly, files under GameData it needs (ksp-requires.txt): without them it is not deployed.</summary>
        public Dictionary<string, List<string>> TestRequires = new Dictionary<string, List<string>>();

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
                s.Results = Str(r, "results") ?? s.Results;
                s.SaveBackups = Str(r, "saveBackups") ?? s.SaveBackups;
                s.LockFile = Str(r, "lockFile") ?? s.LockFile;
                if (r.TryGetProperty("mute", out JsonElement m) && (m.ValueKind == JsonValueKind.True || m.ValueKind == JsonValueKind.False))
                    s.Mute = m.GetBoolean();
                if (r.TryGetProperty("loadStallSec", out JsonElement l) && l.ValueKind == JsonValueKind.Number)
                    s.LoadStallSec = l.GetInt32();
                if (r.TryGetProperty("timeoutSec", out JsonElement t) && t.ValueKind == JsonValueKind.Number)
                    s.TimeoutSec = t.GetInt32();
                if (r.TryGetProperty("maxKsps", out JsonElement mk) && mk.ValueKind == JsonValueKind.Number)
                    s.MaxKsps = mk.GetInt32();
                if (r.TryGetProperty("timeoutScale", out JsonElement ts) && ts.ValueKind == JsonValueKind.Number)
                    s.TimeoutScale = ts.GetDouble();
                if (r.TryGetProperty("durationsFrom", out JsonElement df) && df.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement e in df.EnumerateArray())
                        if (e.GetString() is string dir)
                            s.DurationsFrom.Add(dir);
                if (r.TryGetProperty("crafts", out JsonElement c) && c.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement e in c.EnumerateArray())
                        if (e.GetString() is string p)
                            s.Crafts.Add(p);
                s.Params = Str(r, "params");
                s.KspArgs = Str(r, "kspArgs");
                s.Textures = Str(r, "textures");
                s.Window = Str(r, "window");
                if (r.TryGetProperty("collect", out JsonElement col) && col.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement e in col.EnumerateArray())
                        if (e.GetString() is string collected)
                            s.Collect.Add(collected);
                if (r.TryGetProperty("mods", out JsonElement mods) && mods.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement e in mods.EnumerateArray())
                        if (e.GetString() is string mod)
                            s.Mods.Add(mod);
            }

            s.Install = Env("KSPDIR") ?? s.Install;
            s.Results = Env("KSPTEST_RESULTS") ?? s.Results;
            s.LockFile = Env("KSPTEST_LOCK_FILE") ?? s.LockFile
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KspTest", "ksp.lock");
            if (Env("KSPTEST_MUTE") is string mute)
                s.Mute = mute == "1" || mute.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (Env("KSPTEST_CRAFTS") is string crafts)
                s.Crafts.AddRange(crafts.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            s.Params = Env("KSPTEST_PARAMS") ?? s.Params;
            s.KspArgs = Env("KSPTEST_KSP_ARGS") ?? s.KspArgs;
            s.Textures = Env("KSPTEST_TEXTURES") ?? s.Textures;
            s.Window = Env("KSPTEST_WINDOW") ?? s.Window;
            if (int.TryParse(Env("KSPTEST_MAX_KSPS"), out int maxKsps))
                s.MaxKsps = maxKsps;
            s.MaxKsps = Math.Max(1, s.MaxKsps);
            if (Env("KSPTEST_SHARD") is string shard)
            {
                string[] kn = shard.Split('/');
                if (kn.Length != 2 || !int.TryParse(kn[0], out int k) || !int.TryParse(kn[1], out int n) || n < 1 || k < 1 || k > n)
                    throw new InvalidOperationException($"KSPTEST_SHARD '{shard}': expected k/N, 1 <= k <= N");
                (s.Shard, s.Shards) = (k, n);
            }
            if (double.TryParse(Env("KSPTEST_TIMEOUT_SCALE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double scale) && scale > 0)
                s.TimeoutScale = scale;
            if (Env("KSPTEST_DURATIONS") is string durations)
                s.DurationsFrom.AddRange(durations.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            if (Env("KSPTEST_COLLECT") is string collect)
                s.Collect.AddRange(collect.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            s.Mods.AddRange(InstanceManager.Split(Env("KSPTEST_MODS")));
            // the project's KspMod and KspOverlay items, which the build wrote beside the tests
            s.Mods.InsertRange(0, Lines(Path.Combine(baseDir, "ksptest-mods.txt")));
            s.Overlays.AddRange(Lines(Path.Combine(baseDir, "ksptest-overlays.txt")));
            // the settings' mods given as paths are local mods, like a KspMod item's
            s.Overlays.AddRange(s.Mods.Where(InstanceManager.IsLocal).Select(m => Path.GetFullPath(Path.Combine(baseDir, m)).TrimEnd('/', '\\')));
            s.Mods.RemoveAll(InstanceManager.IsLocal);

            s.Saves = Path.GetFullPath(Path.Combine(baseDir, s.Saves));
            s.Results = Path.GetFullPath(Path.Combine(baseDir, s.Results));
            for (int i = 0; i < s.Crafts.Count; i++)
                s.Crafts[i] = Path.GetFullPath(Path.Combine(baseDir, s.Crafts[i]));
            // the test project's own crafts, which the build copied beside the tests
            string ownCrafts = Path.Combine(baseDir, "ksp-crafts");
            if (Directory.Exists(ownCrafts))
                s.Crafts.Insert(0, ownCrafts);

            // in-game assemblies the build copied beside the tests: ksp-plugins/<name>/*.dll
            string plugins = Path.Combine(baseDir, "ksp-plugins");
            if (Directory.Exists(plugins))
                foreach (string dir in Directory.GetDirectories(plugins))
                {
                    s.Tests[Path.GetFileName(dir)] = new List<string>(Directory.GetFiles(dir, "*.dll"));
                    string requires = Path.Combine(dir, "ksp-requires.txt");
                    s.TestRequires[Path.GetFileName(dir)] = File.Exists(requires)
                        ? new List<string>(File.ReadAllLines(requires).Select(l => l.Trim()).Where(l => l.Length > 0))
                        : new List<string>();
                }
            return s;
        }

        /// <summary>"1280x960" as (1280, 960); null when not set.</summary>
        internal static (int Width, int Height)? ParseWindow(string? window)
        {
            if (window == null) return null;
            string[] p = window.ToLowerInvariant().Split('x');
            if (p.Length == 2 && int.TryParse(p[0].Trim(), out int w) && int.TryParse(p[1].Trim(), out int h) && w > 0 && h > 0)
                return (w, h);
            throw new ArgumentException($"window \"{window}\" is not WIDTHxHEIGHT, such as 1280x960");
        }

        private static IEnumerable<string> Lines(string file) =>
            File.Exists(file) ? File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0) : Enumerable.Empty<string>();

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static string? Env(string name) =>
            Environment.GetEnvironmentVariable(name) is string v && v.Length > 0 ? v : null;
    }
}
