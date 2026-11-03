using System;
using System.Collections;
using MuMech;
using UnityEngine;

namespace KspTest.MechJeb
{
    /// <summary>Registers a per-test cleanup so an autopilot engaged by one test never leaks into the next.</summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class MjIntegration : MonoBehaviour
    {
        private void Awake()
        {
            TestRunner.CleanupHooks.Add(() => { if (HighLogic.LoadedSceneIsFlight && Mj.Present) Mj.StopAll(); });
            Destroy(this);
        }
    }

    /// <summary>
    /// MechJeb driver. Talks to the vessel's master MechJebCore the way MJ's own windows and action
    /// groups do: SmartASS via mode/target + Engage(), autopilots via module.Users.Add/Remove.
    /// MJ windows are IMGUI, so Imgui.Click("Engage autopilot", window: "Ascent Guidance") is the
    /// alternative when a test wants to exercise MJ's UI instead of its API.
    /// </summary>
    public static class Mj
    {
        private static readonly object User = new object();   // our identity in MJ user pools

        public static MechJebCore Core
        {
            get
            {
                var v = FlightGlobals.ActiveVessel;
                if (v == null) throw new UITestFailure("Mj: no active vessel");
                var core = v.GetMasterMechJeb();
                if (core == null) throw new UITestFailure($"Mj: vessel '{v.vesselName}' has no MechJebCore module");
                return core;
            }
        }

        public static bool Present => FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.GetMasterMechJeb() != null;

        // ---- SmartASS ----

        public static void SmartAss(MechJebModuleSmartASS.Mode mode, MechJebModuleSmartASS.Target target)
        {
            var sa = Core.GetComputerModule<MechJebModuleSmartASS>();
            sa.mode = mode;
            sa.target = target;
            sa.Engage();
            Log.Info($"SmartASS {mode}/{target}");
        }

        public static void SmartAss(MechJebModuleSmartASS.Target target)
        {
            var mode = target == MechJebModuleSmartASS.Target.SURFACE ? MechJebModuleSmartASS.Mode.SURFACE : MechJebModuleSmartASS.Mode.ORBITAL;
            SmartAss(mode, target);
        }

        /// <summary>Surface heading/pitch/roll hold (degrees).</summary>
        public static void SmartAssSurface(double heading, double pitch, double roll = 0)
        {
            var sa = Core.GetComputerModule<MechJebModuleSmartASS>();
            sa.mode = MechJebModuleSmartASS.Mode.SURFACE;
            sa.target = MechJebModuleSmartASS.Target.SURFACE;
            sa.srfHdg.Val = heading;
            sa.srfPit.Val = pitch;
            sa.srfRol.Val = roll;
            sa.Engage();
            Log.Info($"SmartASS surface hdg={heading} pit={pitch} rol={roll}");
        }

        /// <summary>
        /// Surface prograde with pitch/yaw/roll offsets (SmartASS "SVEL+"): pitch is the angle of attack,
        /// positive nose-up from the surface velocity; roll 0 keeps the roof toward local up.
        /// </summary>
        public static void SmartAssSurfaceProgradeOffset(double pitch, double yaw = 0, double roll = 0, bool forceRoll = true, bool retrograde = false)
        {
            var sa = Core.GetComputerModule<MechJebModuleSmartASS>();
            sa.mode = MechJebModuleSmartASS.Mode.SURFACE;
            // SVEL- for a capsule flying heat shield first; the offsets mean the same thing about that axis
            sa.target = retrograde ? MechJebModuleSmartASS.Target.SURFACE_RETROGRADE : MechJebModuleSmartASS.Target.SURFACE_PROGRADE;
            sa.srfVelPit.Val = pitch;
            sa.srfVelYaw.Val = yaw;
            sa.srfVelRol.Val = roll;
            sa.forcePitch = sa.forceYaw = true;
            sa.forceRol = forceRoll;   // the ROL box; without it MJ leaves roll free
            sa.Engage();
            Log.Info($"SmartASS surface {(retrograde ? "retrograde" : "prograde")} offset pit={pitch} yaw={yaw} rol={roll}");
        }

        public static void SmartAssOff()
        {
            var sa = Core.GetComputerModule<MechJebModuleSmartASS>();
            sa.mode = MechJebModuleSmartASS.Mode.ORBITAL;
            sa.target = MechJebModuleSmartASS.Target.OFF;
            sa.Engage();
            Log.Info("SmartASS off");
        }

        // ---- Ascent guidance ----

        public static MechJebModuleAscentBaseAutopilot AscentModule => Core.Ascent;

        /// <summary>
        /// Configures and engages ascent guidance (altitude in metres, inclination in degrees). Like a
        /// player, it then presses "stage" if the rocket is still sitting on the pad with no thrust - MJ
        /// only stages by itself for a timed (launch-to-LAN) launch.
        /// </summary>
        public static void Ascent(double orbitAltitudeM, double inclinationDeg, AscentType? type = null, bool autostage = true, bool stageToLaunch = true)
        {
            var core = Core;
            var s = core.AscentSettings;
            if (type.HasValue) s.AscentType = type.Value;
            s.DesiredOrbitAltitude.Val = orbitAltitudeM;
            s.DesiredInclination.Val = inclinationDeg;
            s.Autostage = autostage;
            var ap = core.Ascent;
            ap.Users.Add(User);
            if (autostage) core.Staging.Users.Add(User);   // the Autostage setter is a no-op when the flag was already set
            Log.Info($"ascent guidance engaged: {s.AscentType} alt={orbitAltitudeM:F0} m inc={inclinationDeg} autostage={autostage} status='{ap.Status}'");
            var v = FlightGlobals.ActiveVessel;
            if (stageToLaunch && (v.situation == Vessel.Situations.PRELAUNCH || v.Landed) && MjCompat.ThrustAvailable(core.VesselState) < 0.001)
            {
                Ctrl.SetThrottle(1f);
                Ctrl.Stage();
            }
        }

        public static void AscentOff()
        {
            Core.Ascent.Users.Remove(User);
            Log.Info("ascent guidance disengaged");
        }

        public static string AscentStatus => Core.Ascent.Status;

        /// <summary>Waits until MJ disengages (orbit reached) or the orbit is within tolerance of the target.</summary>
        public static IEnumerator WaitAscentDone(double targetAltM, double tolM = 5000, double timeoutSec = 900)
        {
            float lastLog = 0;
            yield return Wait.Until(() =>
            {
                Flight.AssertAlive();
                if (Time.realtimeSinceStartup - lastLog > 15)
                {
                    lastLog = Time.realtimeSinceStartup;
                    Log.Info($"ascent: {AscentStatus} | {Flight.Telemetry()}");
                }
                var v = FlightGlobals.ActiveVessel;
                bool orbit = v.orbit.PeA > targetAltM - tolM && v.orbit.ApA > targetAltM - tolM;
                return orbit || (!Core.Ascent.Enabled && v.orbit.PeA > 0);
            }, timeoutSec, $"ascent to {targetAltM / 1000:F0} km orbit");
        }

        // ---- Landing guidance ----

        public static void LandAt(CelestialBody body, double lat, double lon)
        {
            var core = Core;
            core.Target.SetPositionTarget(body, lat, lon);
            core.Landing.LandAtPositionTarget(User);
            Log.Info($"landing guidance: target {lat:F4},{lon:F4} on {body.bodyName}");
        }

        public static void LandSomewhere()
        {
            var core = Core;
            core.Landing.StopLanding();
            core.Landing.LandUntargeted(User);
            Log.Info("landing guidance: untargeted");
        }

        public static void StopLanding()
        {
            Core.Landing.StopLanding();
        }

        public static string LandingStatus => Core.Landing.Status;

        public static IEnumerator WaitLanded(double timeoutSec = 1800)
        {
            float lastLog = 0;
            yield return Wait.Until(() =>
            {
                Flight.AssertAlive();
                if (Time.realtimeSinceStartup - lastLog > 15)
                {
                    lastLog = Time.realtimeSinceStartup;
                    Log.Info($"landing: {LandingStatus} | {Flight.Telemetry()}");
                }
                return FlightGlobals.ActiveVessel.LandedOrSplashed;
            }, timeoutSec, "vessel landed");
        }

        /// <summary>Great-circle distance in metres between the vessel and a lat/lon on its main body.</summary>
        public static double DistanceTo(double lat, double lon)
        {
            var v = FlightGlobals.ActiveVessel;
            var b = v.mainBody;
            double d = Vector3d.Distance(b.GetWorldSurfacePosition(v.latitude, v.longitude, 0), b.GetWorldSurfacePosition(lat, lon, 0));
            return 2 * b.Radius * Math.Asin(Math.Min(1, d / (2 * b.Radius)));
        }

        public static void StopAll()
        {
            try
            {
                var core = Core;
                core.Ascent.Users.Remove(User);
                core.Landing.StopLanding();
                SmartAssOff();
            }
            catch (UITestFailure) { }
        }
    }
}
