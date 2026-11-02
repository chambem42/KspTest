using System;
using System.IO;
using System.Net.Http;
using System.Threading;

namespace KspTest.Runner
{
    /// <summary>
    ///     The first-run questions behind <see cref="UserSettings" />: which KSP install to copy the stock game from, whether
    ///     to use an existing ckan.exe or download the latest, and whether the instances go in the default folder or another.
    ///     ksptest setup asks them on the console and stores the answers here.  A test run cannot ask (dotnet test gives
    ///     the tests no console): without the answers its tests skip with <see cref="Hint" />.
    /// </summary>
    public static class Setup
    {
        public const string Hint =
            "run 'ksptest setup' to choose the KSP install, CKAN and the instances folder (from a KspTest checkout: " +
            "dotnet run --project src/KspTest.Tool -- setup)";

        public const string CkanDownloadUrl = "https://github.com/KSP-CKAN/CKAN/releases/latest/download/ckan.exe";

        /// <summary>The answers: CkanPath null means download one to <see cref="GameLocator.DownloadedCkan" />.</summary>
        public sealed class Answers
        {
            public string GameSource = "";
            public string? CkanPath;
            public string Instances = "";
        }

        /// <summary>Stores the answers in the settings file, downloading CKAN first if asked to.</summary>
        public static void Apply(UserSettings file, Answers a, Action<string> log)
        {
            file.GameSource = a.GameSource;
            file.Ckan = a.CkanPath ?? DownloadCkan(GameLocator.DownloadedCkan, log);
            file.Instances = a.Instances;
            file.Save();
            log($"KspTest settings saved to {UserSettings.FilePath}: gameSource = {file.GameSource}, ckan = {file.Ckan}, instances = {file.Instances}");
        }

        /// <summary>The latest CKAN release's ckan.exe into the file.</summary>
        public static string DownloadCkan(string path, Action<string> log)
        {
            log($"downloading {CkanDownloadUrl} to {path} ...");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string partial = path + ".partial";
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("KspTest");
                using HttpResponseMessage response = http.GetAsync(CkanDownloadUrl).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                using FileStream f = File.Create(partial);
                response.Content.CopyToAsync(f).GetAwaiter().GetResult();
            }

            if (File.Exists(path))
                File.Delete(path);
            File.Move(partial, path);
            log($"CKAN downloaded ({new FileInfo(path).Length / 1048576.0:F1} MB)");
            return path;
        }
    }

    /// <summary>A lock between processes on this machine: a file held open with no sharing, deleted when released.</summary>
    internal sealed class FileMutex : IDisposable
    {
        private readonly FileStream _stream;

        private FileMutex(FileStream stream)
        {
            _stream = stream;
        }

        public static FileMutex Acquire(string path, string waitingFor, TimeSpan timeout, Action<string> log)
        {
            DateTime deadline = DateTime.Now + timeout;
            bool noted = false;
            while (true)
            {
                try
                {
                    return new FileMutex(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
                }
                catch (IOException) when (DateTime.Now < deadline)
                {
                    if (!noted)
                        log($"waiting for {waitingFor} ({path})");
                    noted = true;
                    Thread.Sleep(1000);
                }
                catch (UnauthorizedAccessException) when (DateTime.Now < deadline)
                {
                    Thread.Sleep(1000);
                }
            }
        }

        public void Dispose() => _stream.Dispose();
    }
}
