using System.Collections;
using System.IO;
using UnityEngine;

namespace KspTest
{
    /// <summary>
    /// Screenshots into the results dir, one folder per test: screenshots/&lt;test&gt;/NN-[s&lt;k&gt;-&lt;scenario&gt;-]&lt;name&gt;.png.
    /// CaptureScreenshot writes asynchronously, so we wait for the file.
    /// </summary>
    public static class Snap
    {
        private const int MaxPath = 259;
        private static bool loggedScale;

        /// <summary>Also shoot every completed Wait.Until (off with -Params "waitShots=off").</summary>
        public static bool WaitShots = true;

        public static IEnumerator Shot(TestContext ctx, string name)
        {
            ctx.ShotCount++;
            string folder = Sanitize(ctx.TestName);
            // the test's folder gives way too, keeping room for a short file name, past Windows' 260 character limit
            int folderRoom = MaxPath - Path.GetFullPath(Path.Combine(ctx.ResultsDir, "screenshots")).Length - 1 - 24;
            if (folder.Length > folderRoom)
                folder = folder.Substring(0, System.Math.Max(8, folderRoom));
            string sub = Path.Combine("screenshots", folder);
            string scen = string.IsNullOrEmpty(ctx.ScenarioName) ? "" : $"s{ctx.ScenarioIndex}-{Sanitize(ctx.ScenarioName)}-";
            string stem = $"{ctx.ShotCount:D2}-{scen}{Sanitize(name)}";
            // Unity's screenshot cannot write past Windows' 260 character path limit: the name gives way
            int room = MaxPath - Path.GetFullPath(Path.Combine(ctx.ResultsDir, sub)).Length - 1 - ".png".Length;
            if (stem.Length > room)
                stem = stem.Substring(0, System.Math.Max(2, room));
            string file = Path.Combine(sub, stem + ".png");
            string path = Path.GetFullPath(Path.Combine(ctx.ResultsDir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            yield return Textures.Hold();   // a low-texture run shoots with full textures
            try
            {
                yield return new WaitForEndOfFrame();
                if (Application.isBatchMode)
                {
                    RenderTexture rt = null;
                    try
                    {
                        rt = RenderCameras();
                        try { SnapGui.Draw(rt); }
                        catch (System.Exception e) { Log.Warn("batchmode IMGUI failed: " + e); }
                        Save(rt, path);
                    }
                    catch (System.Exception e) { Log.Warn("batchmode screenshot failed: " + e.Message); }
                    finally { if (rt != null) RenderTexture.ReleaseTemporary(rt); }
                }
                else
                {
                    ScreenCapture.CaptureScreenshot(path);
                    float deadline = Time.realtimeSinceStartup + 10f;
                    while (!File.Exists(path) && Time.realtimeSinceStartup < deadline) yield return null;
                }
            }
            finally
            {
                Textures.Release();
            }
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

        /// <summary>
        /// A -batchmode shot's size. Batchmode's screen is not the run's window: on Linux it is 640x480 whatever the
        /// arguments, the display or SetResolution say, and on Windows no taller than the desktop. So the shot is the
        /// screen scaled up toward the run's size, keeping the screen's shape so the UI (laid out for the screen) is not
        /// stretched: Linux's 640x480 in a 1280x960 run is drawn at twice the size.
        /// </summary>
        private static (int w, int h, float scale) ShotSize()
        {
            int sw = Screen.width, sh = Screen.height;
            RunConfig cfg = TestRunner.Config;
            float scale = 1f;
            if (cfg != null && cfg.Width > 0 && cfg.Height > 0)
                scale = Mathf.Max(1f, Mathf.Min((float)cfg.Width / sw, (float)cfg.Height / sh));
            return (Mathf.RoundToInt(sw * scale), Mathf.RoundToInt(sh * scale), scale);
        }

        /// <summary>
        /// A screenshot without a screen: -batchmode never draws a frame to the window, so CaptureScreenshot gets nothing
        /// (or black), but the graphics device is still there. Renders every enabled camera that draws to the screen, in
        /// depth order as Unity would, into one texture (KSP's uGUI draws through its UI camera, so it is in the shot), for
        /// SnapGui to draw the IMGUI over.
        /// </summary>
        private static RenderTexture RenderCameras()
        {
            var (w, h, scale) = ShotSize();
            if (scale != 1f && !loggedScale)
            {
                loggedScale = true;
                Log.Info($"batchmode screenshots: screen {Screen.width}x{Screen.height}, shots {w}x{h} ({scale:0.##}x)");
            }
            var cams = new System.Collections.Generic.List<Camera>();
            foreach (Camera c in Camera.allCameras)
                if (c.targetTexture == null)
                    cams.Add(c);
            cams.Sort((a, b) => a.depth.CompareTo(b.depth));

            var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
            rt.name = "KspTest.Snap";
            RenderTexture prev = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                GL.Clear(true, true, Color.black);
                foreach (Camera c in cams)
                {
                    c.targetTexture = rt;
                    try { c.Render(); }
                    finally { c.targetTexture = null; }
                }
            }
            catch
            {
                RenderTexture.ReleaseTemporary(rt);
                throw;
            }
            finally
            {
                RenderTexture.active = prev;
            }
            return rt;
        }

        private static void Save(RenderTexture rt, string path)
        {
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            RenderTexture prev = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply(false);
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = prev == rt ? null : prev;
                Object.Destroy(tex);
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
