using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KspTest.Runner
{
    /// <summary>
    ///     KSP instances made for a set of mods: CKAN identifiers ("Id" or "Id=version"), and local mods (the path of a
    ///     GameData/ModName folder: built from source, or not on CKAN).  An instance is a stock game from the settings'
    ///     gameSource with the CKAN mods and what they depend on installed by CKAN, one per mod set (a local mod by its
    ///     folder name, as a CKAN mod goes by its identifier) and source game build, in a folder of the settings' instances
    ///     named for its mods and a hash (stock-1a2b3c4d, MechJeb2-5e6f7a8b, ...).  The local mods themselves are copied in
    ///     by each run (<see cref="Deployer.LocalMods" />), so it has their latest build.  One that exists and finished
    ///     installing is reused as it is: nothing is updated (delete it to get newer mods).  Making one is locked per
    ///     instance, so test processes asking for the same set at once make it once.
    /// </summary>
    public sealed class InstanceManager
    {
        public const string ManifestName = "KspTest-instance.json";

        private readonly UserSettings _settings;

        public InstanceManager(UserSettings settings)
        {
            if (settings.GameSource == null || settings.Instances == null)
                throw new InvalidOperationException("no KSP install or instances folder to make instances with: " + Setup.Hint);
            _settings = settings;
        }

        public string Root => Path.GetFullPath(_settings.Instances!);

        /// <summary>The instance for these mods (CKAN identifiers, and local mods as paths): made if there is none yet.</summary>
        public KspInstall Ensure(IEnumerable<string> mods, Action<string> log)
        {
            List<string> all = mods.ToList();
            List<string> set = Normalize(all.Where(m => !IsLocal(m)));
            List<string> local = Normalize(all.Where(IsLocal).Select(LocalName));
            var source = new KspInstall(_settings.GameSource!);
            if (!source.Exists)
                throw new FileNotFoundException($"no KSP at {source.Root} (gameSource; {Setup.Hint})");
            string build = GameBuild(source);
            string name = Name(set, local, build, source);
            string dir = Path.Combine(Root, name);
            Directory.CreateDirectory(Root);

            using (FileMutex.Acquire(Path.Combine(Root, name + ".lock"), $"another process making instance {name}", TimeSpan.FromHours(3), log))
            {
                // an instance made before the names lost their "+": renamed, not made again
                string legacy = Path.Combine(Root, Name(set, local, build, source, legacy: true));
                if (legacy != dir && !Directory.Exists(dir) && InstanceInfo.Load(legacy) is InstanceInfo old && old.Ready)
                {
                    log($"instance {name}: renaming {Path.GetFileName(legacy)}");
                    Directory.Move(legacy, dir);
                    old.Name = name;
                    old.Save(dir);
                }

                InstanceInfo? info = InstanceInfo.Load(dir);
                if (info != null && info.Ready)
                {
                    log($"instance {name}: {dir}");
                    info.LastUsed = DateTime.Now;
                    info.Save(dir);
                    return new KspInstall(dir);
                }

                if (Directory.Exists(dir))
                {
                    log($"instance {name}: removing an unfinished one");
                    StockBase.DeleteTree(dir);
                }

                log($"instance {name}: making it in {dir}" + (set.Count + local.Count > 0 ? $" with {string.Join(", ", set.Concat(local))}" : " (stock)"));
                var clock = Stopwatch.StartNew();
                try
                {
                    StockBase.Create(source.Root, dir, log);
                    info = new InstanceInfo
                    {
                        Name = name, Mods = set, Local = local, GameSource = source.Root, GameBuild = build, Created = DateTime.Now, LastUsed = DateTime.Now,
                    };
                    info.Save(dir);
                    if (set.Count > 0)
                    {
                        if (_settings.Ckan == null)
                            throw new InvalidOperationException("no CKAN to install the mods with: " + Setup.Hint);
                        var ckan = new Ckan(_settings.Ckan);
                        foreach (string repo in Split(_settings.CkanRepos))
                            ckan.AddRepo(dir, repo, log);
                        ckan.Update(dir, log);
                        ckan.Install(dir, set, log);
                        info.Installed = ckan.Installed(dir);
                    }

                    info.Ready = true;
                    info.Save(dir);
                }
                catch (Exception)
                {
                    log($"instance {name}: making it failed; removing {dir}");
                    try { StockBase.DeleteTree(dir); }
                    catch (Exception e) { log($"could not remove {dir}: {e.Message}"); }
                    throw;
                }

                log($"instance {name}: ready in {clock.Elapsed.TotalSeconds:F0} s" +
                    (info.Installed.Count > 0 ? ": " + string.Join(", ", info.Installed.Select(kv => $"{kv.Key} {kv.Value}")) : ""));
                return new KspInstall(dir);
            }
        }

        /// <summary>The instances in the instances folder, finished or not.</summary>
        public List<InstanceInfo> List() =>
            !Directory.Exists(Root)
                ? new List<InstanceInfo>()
                : Directory.GetDirectories(Root).Select(d => InstanceInfo.Load(d) ?? new InstanceInfo { Name = Path.GetFileName(d) })
                    .OrderBy(i => i.Name).ToList();

        /// <summary>Deletes an instance (by name): the source's folders it links to stay.</summary>
        public void Delete(string name, Action<string> log)
        {
            string dir = Path.Combine(Root, name);
            if (!Directory.Exists(dir))
                throw new DirectoryNotFoundException($"no instance {name} in {Root}");
            var install = new KspInstall(dir);
            if (install.Running().Count > 0)
                throw new InvalidOperationException($"KSP is running from {dir}");
            using (FileMutex.Acquire(Path.Combine(Root, name + ".lock"), $"another process using instance {name}", TimeSpan.FromMinutes(10), log))
                StockBase.DeleteTree(dir);
            log($"deleted {dir}");
        }

        /// <summary>Trimmed, without duplicates (case ignored), in a stable order.</summary>
        public static List<string> Normalize(IEnumerable<string> mods) =>
            mods.Select(m => m.Trim()).Where(m => m.Length > 0)
                .GroupBy(m => m.ToLowerInvariant()).Select(g => g.First())
                .OrderBy(m => m.ToLowerInvariant(), StringComparer.Ordinal).ToList();

        /// <summary>A local mod is given as the path of its GameData/ModName folder; a CKAN identifier has no slashes.</summary>
        public static bool IsLocal(string mod) => mod.Contains("/") || mod.Contains("\\");

        /// <summary>A local mod's name, as GameData has it: its folder's.</summary>
        public static string LocalName(string path) => Path.GetFileName(path.Trim().TrimEnd('/', '\\'));

        /// <summary>"Id;Id=version, Id" into identifiers.</summary>
        public static IEnumerable<string> Split(string? mods) =>
            (mods ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(m => m.Trim()).Where(m => m.Length > 0);

        private static string GameBuild(KspInstall source)
        {
            // Windows' build has buildID64.txt, Linux's buildID.txt
            string? id = new[] { "buildID64.txt", "buildID.txt" }.Select(f => Path.Combine(source.Root, f)).FirstOrDefault(File.Exists);
            return id != null ? File.ReadAllText(id).Trim() : "unknown";
        }

        private static string Name(List<string> set, List<string> local, string build, KspInstall source, bool legacy = false)
        {
            string dlc = string.Join(",", new[] { "SquadExpansion/MakingHistory", "SquadExpansion/Serenity" }
                .Where(d => Directory.Exists(Path.Combine(source.GameData, d))));
            // without local mods the key is what it was before they counted, so those instances keep their names
            string key = build + "\n" + dlc + "\n" + string.Join("\n", set.Select(m => m.ToLowerInvariant())) +
                         string.Concat(local.Select(m => "\nlocal:" + m.ToLowerInvariant()));
            using var sha = SHA256.Create();
            string hash = string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(key)).Take(4).Select(b => b.ToString("x2")));
            // as many whole mod names as fit, then how many more: FerramAerospaceResearchContinued_1more-6e709356. Only
            // letters, digits and _ . -: RP-1 refuses to start in a path with ' + or &
            string slug = set.Count + local.Count == 0 ? "stock" : "";
            List<string> names = set.Concat(local).Select(m => Regex.Replace(m.Split('=')[0], @"[^A-Za-z0-9_.-]", "_")).ToList();
            int shown = 0;
            while (shown < names.Count && (shown == 0 || slug.Length + 1 + names[shown].Length <= 40))
                slug += (shown == 0 ? "" : legacy ? "+" : "_") + names[shown++];
            if (shown < names.Count)
                slug += legacy ? $"+{names.Count - shown}" : $"_{names.Count - shown}more";
            return $"{slug}-{hash}";
        }
    }

    /// <summary>What an instance is (KspTest-instance.json in its root): what it was made for and from, and what CKAN installed.</summary>
    public sealed class InstanceInfo
    {
        public string Name = "";
        public List<string> Mods = new List<string>();
        /// <summary>The local mods it was made for, by folder name: each run copies them in.</summary>
        public List<string> Local = new List<string>();
        public string GameSource = "";
        public string GameBuild = "";
        public DateTime Created;
        public DateTime LastUsed;
        public bool Ready;

        /// <summary>Every module CKAN installed, dependencies too, with its version.</summary>
        public Dictionary<string, string> Installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static InstanceInfo? Load(string dir)
        {
            string file = Path.Combine(dir, InstanceManager.ManifestName);
            if (!File.Exists(file))
                return null;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                JsonElement r = doc.RootElement;
                var i = new InstanceInfo
                {
                    Name = r.GetProperty("name").GetString() ?? "",
                    GameSource = r.GetProperty("gameSource").GetString() ?? "",
                    GameBuild = r.GetProperty("gameBuild").GetString() ?? "",
                    Created = r.GetProperty("created").GetDateTime(),
                    LastUsed = r.GetProperty("lastUsed").GetDateTime(),
                    Ready = r.GetProperty("ready").GetBoolean(),
                };
                foreach (JsonElement m in r.GetProperty("mods").EnumerateArray())
                    i.Mods.Add(m.GetString()!);
                if (r.TryGetProperty("local", out JsonElement local))
                    foreach (JsonElement m in local.EnumerateArray())
                        i.Local.Add(m.GetString()!);
                foreach (JsonProperty p in r.GetProperty("installed").EnumerateObject())
                    i.Installed[p.Name] = p.Value.GetString() ?? "";
                return i;
            }
            catch (Exception)
            {
                return null;   // unreadable: an unfinished instance
            }
        }

        public void Save(string dir)
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("name", Name);
                w.WriteStartArray("mods");
                foreach (string m in Mods)
                    w.WriteStringValue(m);
                w.WriteEndArray();
                w.WriteStartArray("local");
                foreach (string m in Local)
                    w.WriteStringValue(m);
                w.WriteEndArray();
                w.WriteString("gameSource", GameSource);
                w.WriteString("gameBuild", GameBuild);
                w.WriteString("created", Created);
                w.WriteString("lastUsed", LastUsed);
                w.WriteBoolean("ready", Ready);
                w.WriteStartObject("installed");
                foreach (KeyValuePair<string, string> kv in Installed.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                    w.WriteString(kv.Key, kv.Value);
                w.WriteEndObject();
                w.WriteEndObject();
            }

            File.WriteAllBytes(Path.Combine(dir, InstanceManager.ManifestName), stream.ToArray());
        }
    }
}
