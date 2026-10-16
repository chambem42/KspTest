using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace KSPUITest
{
    /// <summary>
    /// The per-run manifest written by run-test.ps1 to GameData/KSPUITest/PluginData/run.cfg.
    /// Absent file = harness inert. Format (ConfigNode):
    ///
    ///   KSPUITEST_RUN
    ///   {
    ///       test = Smoke_MainMenuToVab,Launch_StockButton   // one or more, run in order in this session
    ///       save = uitest              // folder under saves/
    ///       resultsDir = C:\...\results\20260913-0142-Smoke_MainMenuToVab
    ///       timeoutSec = 600
    ///       quitWhenDone = true
    ///       width = 1920               // optional windowed resolution forced at start
    ///       height = 1080
    ///       PARAMS { craft = UITest_Rocket }   // free-form test parameters
    ///   }
    /// </summary>
    public class RunConfig
    {
        public static string PluginDataDir => Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KSPUITest", "PluginData");
        public static string FilePath => Path.Combine(PluginDataDir, "run.cfg");

        public string Test;
        public readonly List<string> Tests = new List<string>();
        public string Save = "uitest";
        public string ResultsDir;
        public double TimeoutSec = 600;
        public bool QuitWhenDone = true;
        public int Width;
        public int Height;
        public readonly Dictionary<string, string> Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static RunConfig TryLoad()
        {
            if (!File.Exists(FilePath)) return null;
            var root = ConfigNode.Load(FilePath);
            // consumed on read: a run the runner did not live to clean up after must not start again on the next launch
            try { File.Delete(FilePath); } catch { }
            var node = root?.GetNode("KSPUITEST_RUN") ?? root;
            if (node == null) return null;

            var cfg = new RunConfig();
            cfg.Test = node.GetValue("test");
            foreach (var t in (cfg.Test ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                cfg.Tests.Add(t.Trim());
            if (node.HasValue("save")) cfg.Save = node.GetValue("save");
            cfg.ResultsDir = node.GetValue("resultsDir");
            if (node.HasValue("timeoutSec")) cfg.TimeoutSec = double.Parse(node.GetValue("timeoutSec"), CultureInfo.InvariantCulture);
            if (node.HasValue("quitWhenDone")) cfg.QuitWhenDone = bool.Parse(node.GetValue("quitWhenDone"));
            if (node.HasValue("width")) cfg.Width = int.Parse(node.GetValue("width"), CultureInfo.InvariantCulture);
            if (node.HasValue("height")) cfg.Height = int.Parse(node.GetValue("height"), CultureInfo.InvariantCulture);

            var p = node.GetNode("PARAMS");
            if (p != null)
                foreach (ConfigNode.Value v in p.values)
                    cfg.Params[v.name] = v.value;

            if (string.IsNullOrEmpty(cfg.ResultsDir))
                cfg.ResultsDir = Path.Combine(KSPUtil.ApplicationRootPath, "KSPUITest-results", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            return cfg;
        }
    }
}
