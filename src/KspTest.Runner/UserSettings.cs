using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace KspTest.Runner
{
    /// <summary>
    ///     This machine's settings for the KSP instances KspTest makes, shared by every project: %APPDATA%/KspTest/settings.json
    ///     (KSPTEST_SETTINGS moves it), the environment over it (KSPTEST_GAME, KSPTEST_CKAN, KSPTEST_INSTANCES,
    ///     KSPTEST_CKAN_REPOS):
    ///     <code>
    ///     { "gameSource": "C:/Program Files (x86)/Steam/steamapps/common/Kerbal Space Program",
    ///       "ckan": "C:/tools/ckan.exe", "instances": "D:/KspTest/instances" }
    ///     </code>
    ///     gameSource is any KSP install, modded or not: only its stock files are used.  ksptest setup asks for the three and
    ///     writes the file (<see cref="Setup" />): CKAN and the instances folder are always the user's choice.
    /// </summary>
    public sealed class UserSettings
    {
        /// <summary>The KSP install the instances' stock game comes from.</summary>
        public string? GameSource;

        /// <summary>CKAN's command line (ckan.exe), which installs the instances' mods.</summary>
        public string? Ckan;

        /// <summary>The folder the instances are made in.</summary>
        public string? Instances;

        /// <summary>
        ///     CKAN repositories new instances use besides CKAN's own, ";" separated: a name from CKAN's list of them
        ///     ("MechJeb2-dev", for MechJeb's dev builds), or name=url (ckanRepos, KSPTEST_CKAN_REPOS).
        /// </summary>
        public string? CkanRepos;

        public static string FilePath =>
            Environment.GetEnvironmentVariable("KSPTEST_SETTINGS") is string p && p.Length > 0
                ? p
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KspTest", "settings.json");

        public static string DefaultInstances =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KspTest", "instances");

        /// <summary>The settings in effect: the file's, the environment's over them.</summary>
        public static UserSettings Load()
        {
            UserSettings s = LoadFile();
            s.GameSource = Env("KSPTEST_GAME") ?? s.GameSource;
            s.Ckan = Env("KSPTEST_CKAN") ?? s.Ckan;
            s.Instances = Env("KSPTEST_INSTANCES") ?? s.Instances;
            s.CkanRepos = Env("KSPTEST_CKAN_REPOS") ?? s.CkanRepos;
            return s;
        }

        /// <summary>All three set: instances can be made without asking anything.</summary>
        public bool Complete => GameSource != null && Ckan != null && Instances != null;

        /// <summary>Only what the file says.</summary>
        public static UserSettings LoadFile()
        {
            var s = new UserSettings();
            if (!File.Exists(FilePath))
                return s;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(FilePath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            s.GameSource = Str(doc.RootElement, "gameSource");
            s.Ckan = Str(doc.RootElement, "ckan");
            s.Instances = Str(doc.RootElement, "instances");
            s.CkanRepos = Str(doc.RootElement, "ckanRepos");
            return s;
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                if (GameSource != null)
                    w.WriteString("gameSource", GameSource);
                if (Ckan != null)
                    w.WriteString("ckan", Ckan);
                if (Instances != null)
                    w.WriteString("instances", Instances);
                if (CkanRepos != null)
                    w.WriteString("ckanRepos", CkanRepos);
                w.WriteEndObject();
            }

            File.WriteAllText(FilePath, Encoding.UTF8.GetString(stream.ToArray()) + "\n");
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Length > 0 ? v.GetString() : null;

        private static string? Env(string name) =>
            Environment.GetEnvironmentVariable(name) is string v && v.Length > 0 ? v : null;
    }
}
