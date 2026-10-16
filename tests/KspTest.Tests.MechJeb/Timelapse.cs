using System;
using System.IO;
using UnityEngine;

namespace KspTest.Tests.MechJeb
{
    /// <summary>
    /// A time lapse of whatever the screen shows: a screenshot every IntervalSec of real time to frames/NNNNN.png under the
    /// given folder, with a caption panel drawn into the frames (Title, Detail, and a Live line refreshed every frame).
    /// AimAt keeps the flight camera behind the active vessel looking at another vessel. Assemble with ffmpeg:
    /// ffmpeg -framerate 30 -i frames/%05d.png -c:v libx264 -pix_fmt yuv420p out.mp4
    /// </summary>
    public class Timelapse : MonoBehaviour
    {
        private static Timelapse instance;

        public static bool Active => instance != null;
        public static float IntervalSec = 0.5f;
        public static string Title = "", Detail = "";
        public static Func<string> Live;

        // flight camera behind the active vessel, looking at this one, from this far
        public static Vessel AimAt;
        public static float AimDistance = 12;

        private string dir;
        private int frame;
        private float last;
        private GUIStyle titleStyle, detailStyle;

        public static int Frames => instance != null ? instance.frame : 0;

        private static bool cleanupRegistered;

        public static void Begin(string resultsDir, float intervalSec)
        {
            End();
            if (!cleanupRegistered)
            {
                // a test that fails mid-flight leaves no recorder running and the harness overlay back
                TestRunner.CleanupHooks.Add(() => End());
                cleanupRegistered = true;
            }
            var go = new GameObject("KspTest.Timelapse");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<Timelapse>();
            instance.dir = Path.Combine(resultsDir, "frames");
            Directory.CreateDirectory(instance.dir);
            IntervalSec = intervalSec;
            TestRunner.HideOverlay = true;
            Log.Info($"timelapse: frames to {instance.dir} every {intervalSec} s");
        }

        public static void Caption(string title, string detail = "")
        {
            Title = title;
            Detail = detail;
            Log.Info($"timelapse caption: {title} | {detail}");
        }

        public static int End()
        {
            if (instance == null) return 0;
            int n = instance.frame;
            Destroy(instance.gameObject);
            instance = null;
            AimAt = null;
            TestRunner.HideOverlay = false;
            Log.Info($"timelapse: {n} frames");
            return n;
        }

        private void LateUpdate()
        {
            if (AimAt != null && FlightGlobals.ActiveVessel != null && !MapView.MapIsEnabled && FlightCamera.fetch != null)
            {
                // GetWorldPos3D works for an unloaded target too (from its orbit), so the camera tracks it from kilometers out
                Vector3 me = FlightGlobals.ActiveVessel.CoMD, them = AimAt.GetWorldPos3D();
                Vector3 away = (me - them).normalized;
                Vector3 up = (me - FlightGlobals.ActiveVessel.mainBody.position).normalized;
                FlightCamera.fetch.SetCamCoordsFromPosition(me + (away * 0.9f + up * 0.35f).normalized * AimDistance);
            }

            if (Time.realtimeSinceStartup - last < IntervalSec) return;
            last = Time.realtimeSinceStartup;
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"{frame++:D5}.png"));
        }

        private void OnGUI()
        {
            if (titleStyle == null)
            {
                titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 34, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter, richText = true };
                titleStyle.normal.textColor = Color.white;
                detailStyle = new GUIStyle(titleStyle) { fontSize = 22, fontStyle = FontStyle.Normal };
            }
            string live = null;
            try { live = Live?.Invoke(); }
            catch (Exception) { }

            float w = Mathf.Min(1400, Screen.width - 40), x = (Screen.width - w) / 2, y = 90;
            float h = 60 + (string.IsNullOrEmpty(Detail) ? 0 : 34) + (string.IsNullOrEmpty(live) ? 0 : 32);
            var prev = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.55f);
            GUI.DrawTexture(new Rect(x, y - 8, w, h), Texture2D.whiteTexture);
            GUI.color = prev;
            GUI.Label(new Rect(x, y, w, 46), Title, titleStyle);
            y += 50;
            if (!string.IsNullOrEmpty(Detail))
            {
                GUI.Label(new Rect(x, y, w, 32), Detail, detailStyle);
                y += 34;
            }
            if (!string.IsNullOrEmpty(live))
                GUI.Label(new Rect(x, y, w, 30), "<color=#9fe39f>" + live + "</color>", detailStyle);
        }
    }
}
