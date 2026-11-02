using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace KspTest
{
    /// <summary>
    /// A time lapse of the map view: keeps the map open with the camera looking straight down on a target point from
    /// viewAltKm, and saves a screenshot every intervalSec of real time to frames/NNNNN.png under the results folder,
    /// with a row in frames.csv (frame, real time, game time, vessel altitude, predicted impact and its distance from
    /// the target) for captioning afterwards. At touchdown Freeze pauses the game and LandingShots takes the flight
    /// camera's view of where it came down (rows labeled landing_<distance>, with missKm the vessel's own distance from
    /// the target).
    /// </summary>
    public class VideoCapture : MonoBehaviour
    {
        /// <summary>A predicted impact point (latitude, longitude in degrees), or false without one.</summary>
        public delegate bool ImpactReader(out double lat, out double lon);

        /// <summary>Where the vessel is predicted to land, for the frames' miss column: Trajectories', from KspTest.Trajectories.</summary>
        public static ImpactReader Impact;

        private static VideoCapture instance;
        private CelestialBody body;
        private double targetLat, targetLon, viewAltKm, intervalSec;
        private string dir, label;
        private readonly StringBuilder csv = new StringBuilder("frame,realSec,ut,altKm,impactLat,impactLon,missKm,camDot,label\n");
        private int frame;
        private float nextShot, startReal;
        private float pitchSign = 1f, camDot = float.NaN;
        private bool pitchChecked, frozen;

        public static int Frames => instance != null ? instance.frame : 0;

        public static void Begin(string resultsDir, CelestialBody body, double targetLat, double targetLon, double viewAltKm,
                                 double intervalSec, string label)
        {
            End();
            // A window that was minimized when KSP last exited comes back at Windows' minimized size (160 x 28), and
            // the screenshots with it.
            if (Screen.width < 1280 || Screen.height < 720)
            {
                Log.Info($"VideoCapture: screen {Screen.width}x{Screen.height}, setting 1920x1080");
                Screen.SetResolution(1920, 1080, FullScreenMode.Windowed);
            }
            var go = new GameObject("KspTest.VideoCapture");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<VideoCapture>();
            instance.body = body;
            instance.targetLat = targetLat;
            instance.targetLon = targetLon;
            instance.viewAltKm = viewAltKm;
            instance.intervalSec = intervalSec;
            instance.label = label ?? "";
            instance.dir = Path.Combine(resultsDir, "frames");
            Directory.CreateDirectory(instance.dir);
            instance.startReal = Time.realtimeSinceStartup;
            instance.nextShot = instance.startReal;
            TestRunner.HideOverlay = true;
            Textures.HoldNow();   // a low-texture run records with full textures, until End
        }

        /// <summary>Stops capturing and writes frames.csv; returns the number of frames.</summary>
        public static int End()
        {
            if (instance == null)
                return 0;
            Textures.Release();
            if (instance.frozen)
            {
                FlightDriver.SetPause(false, false);
                GameEvents.onShowUI.Fire();
            }
            int n = instance.frame;
            TestRunner.HideOverlay = false;
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(instance.dir), "frames.csv"), instance.csv.ToString());
            Destroy(instance.gameObject);
            instance = null;
            return n;
        }

        private void LateUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || body == null || frozen)
                return;
            if (!MapView.MapIsEnabled)
                MapView.EnterMapView();
            CloseKerbalismWindow();
            AimCamera();
            if (Time.realtimeSinceStartup >= nextShot)
            {
                nextShot += (float)intervalSec;
                if (nextShot < Time.realtimeSinceStartup)
                    nextShot = Time.realtimeSinceStartup + (float)intervalSec;
                Shoot();
            }
        }

        // The camera orbits its pivot (here the body's centre) at heading camHdg (plus the planetarium's rotation) and
        // pitch camPitch, looking back at it; put it on the line from the centre through the target.
        private void AimCamera()
        {
            PlanetariumCamera cam = PlanetariumCamera.fetch;
            if (cam == null || body.MapObject == null)
                return;
            if (cam.target != body.MapObject)
                cam.SetTarget(body.MapObject);
            cam.minPitch = -89.9f * Mathf.Deg2Rad;
            cam.maxPitch = 89.9f * Mathf.Deg2Rad;
            float distance = (float)((body.Radius + viewAltKm * 1000.0) / ScaledSpace.ScaleFactor);
            cam.minDistance = Mathf.Min(cam.minDistance, distance);
            cam.SetDistance(distance);

            Vector3d up = (body.GetWorldSurfacePosition(targetLat, targetLon, 0.0) - body.position).normalized;
            double pitch = Math.Asin(Math.Max(-1.0, Math.Min(1.0, up.y)));
            double yawDeg = Math.Atan2(-up.x, -up.z) * Mathf.Rad2Deg;
            cam.camPitch = pitchSign * (float)pitch;
            cam.camHdg = (float)((yawDeg - Planetarium.InverseRotAngle) * Mathf.Deg2Rad);

            // how well the camera sits over the target (1 = straight above); the pitch's sense is not documented,
            // so if the camera settles on the mirror side, flip it (once)
            Vector3 pivot = ScaledSpace.LocalToScaledSpace(body.position);
            camDot = Vector3.Dot((cam.transform.position - pivot).normalized, (Vector3)up);
            if (!pitchChecked && Time.realtimeSinceStartup - startReal > 3f)
            {
                pitchChecked = true;
                if (camDot < 0.9f)
                {
                    pitchSign = -pitchSign;
                    Log.Info($"VideoCapture: camera off the target (dot {camDot:F3}), pitch sense flipped");
                }
            }
        }

        // Kerbalism opens its body info window the first time a save enters the map view; keep its window shut.
        private static System.Reflection.FieldInfo kerbalismWindow;
        private static System.Reflection.MethodInfo kerbalismClose;
        private static bool kerbalismResolved;

        private static void CloseKerbalismWindow()
        {
            if (!kerbalismResolved)
            {
                kerbalismResolved = true;
                Type ui = Reflect.FindType("KERBALISM.UI");
                kerbalismWindow = ui?.GetField("window", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                kerbalismClose = kerbalismWindow?.FieldType.GetMethod("Close", Type.EmptyTypes);
            }
            try
            {
                object w = kerbalismWindow?.GetValue(null);
                if (w != null)
                    kerbalismClose?.Invoke(w, null);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// At touchdown (called from the flight's end check, before the vessel hits): hyper-warp off and the game paused,
        /// so LandingShots sees the vessel where it came down. End unpauses.
        /// </summary>
        public static void Freeze()
        {
            if (instance == null || instance.frozen)
                return;
            instance.frozen = true;
            Flight.HyperWarpOff();
            if (TimeWarp.CurrentRateIndex > 0)
                TimeWarp.SetRate(0, true);
            FlightDriver.SetPause(true, false);
            Log.Info("VideoCapture: paused at touchdown");
        }

        /// <summary>
        /// Out of the map view with the UI hidden (the flight camera only moves in a pause with the UI hidden), the flight
        /// camera on the vessel from each distance (m), looking down at pitchDeg, one screenshot each.
        /// </summary>
        public static IEnumerator LandingShots(double pitchDeg, params float[] distancesM)
        {
            if (instance == null || !instance.frozen)
                yield break;
            if (MapView.MapIsEnabled)
                MapView.ExitMapView();
            GameEvents.onHideUI.Fire();
            // popup windows (Trajectories') stay up through onHideUI; the game is paused, so IMGUI windows can go too
            // put back however the shots end: a test that times out here goes on (or the next one does) with every mod's
            // windows, and their Update, as they were
            var gui = ShotGui.HideOthers();
            try
            {
                FlightCamera cam = FlightCamera.fetch;
                foreach (float d in distancesM)
                {
                    cam.maxDistance = Mathf.Max(cam.maxDistance, d);
                    cam.SetDistanceImmediate(d);
                    // a moment for the camera's pivot and the terrain to settle
                    float until = Time.realtimeSinceStartup + 1.5f;
                    while (Time.realtimeSinceStartup < until)
                    {
                        cam.camPitch = (float)(pitchDeg * Mathf.Deg2Rad);
                        yield return null;
                    }
                    instance.Shoot("landing_" + d.ToString("F0", CultureInfo.InvariantCulture));
                    // the screenshot is written at the end of a frame
                    for (int i = 0; i < 5; ++i)
                        yield return null;
                }
            }
            finally
            {
                gui.Dispose();
            }
        }

        private void Shoot(string rowLabel = null)
        {
            string path = Path.Combine(dir, frame.ToString("D5", CultureInfo.InvariantCulture) + ".png");
            ScreenCapture.CaptureScreenshot(path);
            Vessel v = FlightGlobals.ActiveVessel;
            double alt = v != null ? v.altitude / 1000.0 : double.NaN;
            double iLat = double.NaN, iLon = double.NaN, missKm = double.NaN;
            if (rowLabel != null)
            {
                // a landing shot: where the vessel itself is
                if (v != null)
                {
                    iLat = v.latitude;
                    iLon = v.longitude;
                    missKm = GreatCircleKm(body, iLat, iLon, targetLat, targetLon);
                }
            }
            else if (Impact != null && Impact(out double la, out double lo))
            {
                iLat = la;
                iLon = lo;
                missKm = GreatCircleKm(body, la, lo, targetLat, targetLon);
            }
            csv.Append(string.Format(CultureInfo.InvariantCulture, "{0},{1:F1},{2:F1},{3:F2},{4:F5},{5:F5},{6:F3},{7:F4},{8}\n",
                frame, Time.realtimeSinceStartup - startReal, Planetarium.GetUniversalTime(), alt, iLat, iLon, missKm, camDot, rowLabel ?? label));
            ++frame;
        }

        private static double GreatCircleKm(CelestialBody b, double lat1, double lon1, double lat2, double lon2)
        {
            double d2r = Math.PI / 180;
            double dLat = (lat2 - lat1) * d2r, dLon = (lon2 - lon1) * d2r;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * d2r) * Math.Cos(lat2 * d2r) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return 2 * b.Radius * Math.Asin(Math.Min(1, Math.Sqrt(a))) / 1000.0;
        }
    }
}
