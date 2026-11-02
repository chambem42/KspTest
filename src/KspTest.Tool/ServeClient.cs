using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

namespace KspTest.Tool
{
    /// <summary>The client of a Serve session (ksptest run Serve): its requests, and plugins loaded into it.</summary>
    internal static class ServeClient
    {
        /// <summary>ksptest serve REQUEST [k=v]...: the answer printed; 1 when it is an error.</summary>
        public static int Request(Args a)
        {
            string request = a.Positional.FirstOrDefault() ?? throw new ArgumentException("which request? ksptest serve help");
            string query = string.Join("&", a.Positional.Skip(1).Select(kv =>
            {
                int eq = kv.IndexOf('=');
                if (eq <= 0)
                    throw new ArgumentException($"'{kv}': requests take k=v arguments");
                return Uri.EscapeDataString(kv.Substring(0, eq)) + "=" + Uri.EscapeDataString(kv.Substring(eq + 1));
            }));
            JsonElement answer = Get(a, request + (query.Length > 0 ? "?" + query : ""));
            Console.WriteLine(JsonSerializer.Serialize(answer, new JsonSerializerOptions { WriteIndented = true }));
            return answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("error", out _) ? 1 : 0;
        }

        /// <summary>
        ///     ksptest serve-plugin PROJECT: the project built under a fresh assembly name (Mono neither unloads an assembly
        ///     nor loads a second one of the same name) into bin/serve-plugins/NAME beside it, and loaded with /plugin.  The
        ///     builds beyond the newest five are deleted.  The name goes in as ServePluginName, which the project turns into
        ///     its AssemblyName and OutputPath (tools/ServePlugins/ServePlugins.csproj): AssemblyName or OutDir given on the
        ///     command line would reach the projects it references (the harness) too.  Those are not rebuilt.
        /// </summary>
        public static int Plugin(Args a)
        {
            string project = Path.GetFullPath(a.Positional.FirstOrDefault() ?? throw new ArgumentException("which project? ksptest serve-plugin X.csproj"));
            if (!File.Exists(project))
                throw new FileNotFoundException($"no project at {project}");
            string name = Path.GetFileNameWithoutExtension(project) + "." + DateTime.Now.ToString("yyyyMMddHHmmss");
            string root = Path.Combine(Path.GetDirectoryName(project)!, "bin", "serve-plugins");
            string outDir = Path.Combine(root, name) + Path.DirectorySeparatorChar;
            var psi = new ProcessStartInfo("dotnet")
            {
                Arguments = $"build \"{project}\" --no-dependencies -nologo -v:q -p:ServePluginName={name} {a.Get("build-args")}",
                UseShellExecute = false,
            };
            using (Process build = Process.Start(psi) ?? throw new InvalidOperationException("dotnet did not start"))
            {
                build.WaitForExit();
                if (build.ExitCode != 0)
                    throw new InvalidOperationException($"the build of {project} failed");
            }

            string dll = Path.Combine(outDir, name + ".dll");
            JsonElement answer = Get(a, "plugin?path=" + Uri.EscapeDataString(dll));
            Console.WriteLine(JsonSerializer.Serialize(answer, new JsonSerializerOptions { WriteIndented = true }));
            foreach (string old in Directory.GetDirectories(root).OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(5))
                try { Directory.Delete(old, true); }
                catch (IOException) { }
            return answer.TryGetProperty("error", out _) ? 1 : 0;
        }

        private static JsonElement Get(Args a, string pathAndQuery)
        {
            int port = a.Int("port", 8642);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(a.Int("timeout", 1800)) };
            string body;
            try
            {
                body = http.GetStringAsync($"http://127.0.0.1:{port}/{pathAndQuery}").GetAwaiter().GetResult();
            }
            catch (HttpRequestException e)
            {
                throw new InvalidOperationException($"no Serve session on port {port} ({e.Message}): ksptest run Serve ...");
            }

            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
    }
}
