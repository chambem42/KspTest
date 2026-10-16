using System.Collections;
using System.IO;
using UnityEngine;

namespace KSPUITest
{
    /// <summary>
    /// Screenshots into the results dir, one folder per test: screenshots/&lt;test&gt;/NN-[s&lt;k&gt;-&lt;scenario&gt;-]&lt;name&gt;.png.
    /// CaptureScreenshot writes asynchronously, so we wait for the file.
    /// </summary>
    public static class Snap
    {
        /// <summary>Also shoot every completed Wait.Until (off with -Params "waitShots=off").</summary>
        public static bool WaitShots = true;

        public static IEnumerator Shot(TestContext ctx, string name)
        {
            ctx.ShotCount++;
            string sub = Path.Combine("screenshots", Sanitize(ctx.TestName));
            string scen = string.IsNullOrEmpty(ctx.ScenarioName) ? "" : $"s{ctx.ScenarioIndex}-{Sanitize(ctx.ScenarioName)}-";
            string file = Path.Combine(sub, $"{ctx.ShotCount:D2}-{scen}{Sanitize(name)}.png");
            string path = Path.GetFullPath(Path.Combine(ctx.ResultsDir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(path);
            float deadline = Time.realtimeSinceStartup + 10f;
            while (!File.Exists(path) && Time.realtimeSinceStartup < deadline) yield return null;
            if (File.Exists(path))
            {
                ctx.Screenshots.Add(file.Replace('\\', '/'));
                Log.Info("screenshot " + file);
            }
            else
            {
                Log.Warn("screenshot not written: " + path);
            }
        }

        /// <summary>The shot Wait.Until takes when its condition comes true: named after what it waited for.</summary>
        public static IEnumerator WaitShot(string what)
        {
            yield return AutoShot("wait-" + what);
        }

        /// <summary>A shot the drivers take on their own (under the same switch as the wait shots), into the current test.</summary>
        public static IEnumerator AutoShot(string name)
        {
            var ctx = TestRunner.Context;
            if (ctx == null || !WaitShots) yield break;
            if (string.IsNullOrEmpty(name)) name = "auto";
            if (name.Length > 70) name = name.Substring(0, 70);
            yield return Shot(ctx, name);
        }

        private static string Sanitize(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s) sb.Append(c == ' ' || c > 127 ? '_' : c);   // FAR's "AoA x Pe" label uses a multiplication sign that mangles on disk
            return sb.ToString();
        }
    }
}
