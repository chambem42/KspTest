using System;
using System.Collections;
using KspTest;
using UnityEngine;

namespace KspTest.Tests.Stock
{
    /// <summary>
    /// Stock-only reproduction of the world-frame drift. KSP keeps the home body's azimuth in Unity's world
    /// coordinates (CelestialBody.directRotAngle) and the world's compensating turn (Planetarium.InverseRotAngle)
    /// on objects that outlive scenes and are not in the save; "Revert to VAB/SPH" jumps the clock back over the
    /// flight while the editor's inertial pass recomputes the azimuth from the stale value, so every cycle turns
    /// the planet in world coordinates by the planet's rotation over the flight. Stock's solar occlusion between
    /// parts is judged in an axis-aligned frame hung off those world axes, so a vessel sitting on the pad at the
    /// same clock, under the same sun, gets different sunAreaMultipliers from one cycle to the next.
    /// Params: cycles (3), shiftHours (2: warp this long before each revert, 30 deg of Earth's rotation),
    /// sampleSec (20), daylightUT (0 = warp forward to the first sunlight on cycle 1 and to the same UT after).
    /// </summary>
    public static class WorldFrameTests
    {
        [UITest(Kind = "Measure", StartsAt = StartState.Sph, Craft = "UITest_Plane", TimeoutSec = 1800)]
        public static IEnumerator Solar_RevertCycle(TestContext ctx)
        {
            int cycles = (int)ctx.ParamDouble("cycles", 3);
            double shiftHours = ctx.ParamDouble("shiftHours", 2);
            double sampleSec = ctx.ParamDouble("sampleSec", 20);
            double daylightUT = ctx.ParamDouble("daylightUT", 0);
            var facility = Nav.LoadedFacility;   // revert to where the launch came from, so every cycle uses the same site

            for (int c = 1; c <= cycles; ++c)
            {
                string k = "cycle" + c;
                ctx.Step($"[{k}] Launch");
                ctx.Record(k + ".editorUT", Math.Round(Planetarium.GetUniversalTime(), 1));
                ctx.Record(k + ".editorDirectRotAngle", Math.Round(FlightGlobals.GetHomeBody().directRotAngle, 3));
                yield return Nav.Launch(ctx);
                var v = FlightGlobals.ActiveVessel;
                yield return Wait.Seconds(3);
                ctx.Record(k + ".launchUT", Math.Round(Planetarium.GetUniversalTime(), 1));
                ctx.Record(k + ".frame.rotating", v.mainBody.inverseRotation);
                ctx.Record(k + ".frame.directRotAngle", Math.Round(v.mainBody.directRotAngle, 3));
                ctx.Record(k + ".frame.inverseRotAngle", Math.Round(Planetarium.InverseRotAngle, 3));
                RecordBodyFrame(ctx, k + ".frame", v.mainBody);

                // Sample at the same clock every cycle: the revert puts the launch clock back, so one target UT
                // does it. The first cycle finds daylight (half-hour steps) and fixes that UT for the rest.
                if (daylightUT <= 0)
                {
                    ctx.Step($"[{k}] Find daylight");
                    for (int i = 0; i < 48 && !FlightGlobals.ActiveVessel.directSunlight; ++i) yield return Flight.WarpTo(Planetarium.GetUniversalTime() + 1800);
                    daylightUT = Planetarium.GetUniversalTime() + 60;
                    ctx.Record("daylightUT", Math.Round(daylightUT, 1));
                }
                if (Planetarium.GetUniversalTime() < daylightUT - 5) yield return Flight.WarpTo(daylightUT);
                yield return Wait.Seconds(3);
                ctx.Record(k + ".sampleUT", Math.Round(Planetarium.GetUniversalTime(), 1));
                ctx.Record(k + ".directSunlight", v.directSunlight);
                RecordBodyFrame(ctx, k + ".sample", v.mainBody);

                ctx.Step($"[{k}] Sample solar occlusion on the pad");
                yield return Solar.Sample(ctx, k, "solar_revert_cycles.csv", sampleSec, null);
                yield return Snap.Shot(ctx, k + "-pad");

                if (c < cycles)
                {
                    ctx.Step($"[{k}] Warp {shiftHours} h, revert to the editor");
                    yield return Flight.WarpSeconds(shiftHours * 3600);
                    ctx.Record(k + ".revertUT", Math.Round(Planetarium.GetUniversalTime(), 1));
                    yield return Nav.RevertToEditor(ctx, facility);
                }
            }
        }

        // The body's orientation in Unity's world axes, read off its surface rather than directRotAngle, so it holds
        // for mods that build the body frame some other way (TiltEm composes it from Planetarium.Zup): world azimuth
        // of the prime meridian at the equator, and the pole direction.
        private static void RecordBodyFrame(TestContext ctx, string key, CelestialBody body)
        {
            Vector3d pm = (body.GetWorldSurfacePosition(0, 0, 0) - body.position).normalized;
            Vector3d pole = (body.GetWorldSurfacePosition(90, 0, 0) - body.position).normalized;
            ctx.Record(key + ".pmAzimuth", Math.Round(Math.Atan2(pm.z, pm.x) * 180 / Math.PI, 3));
            ctx.Record(key + ".pmY", Math.Round(pm.y, 4));
            ctx.Record(key + ".pole", $"{pole.x:F4},{pole.y:F4},{pole.z:F4}");
            Vector3d zr = Planetarium.right;
            ctx.Record(key + ".planetariumRight", $"{zr.x:F4},{zr.y:F4},{zr.z:F4}");
        }

        /// <summary>
        /// Whether body surfaces and orbits agree after a scene change. Stock places a body's surface through its
        /// BodyFrame (directRotAngle) and everything that orbits - the sun, moons, on-rails vessels - through
        /// Planetarium.Zup (InverseRotAngle); the two only agree while Zup matches InverseRotAngle. Flies a vessel to a
        /// 300 km orbit (above Earth's inverse-rotation altitude, so its flight scene stays inertial), warps at the KSC
        /// (a rotating scene, InverseRotAngle grows by Earth's rotation), then opens the tracking station and flies the
        /// vessel again, both inertial. In each, records the vessel's longitude as the surface reports it against the
        /// longitude its orbit implies (celestial direction minus the body's rotationAngle), and the same for the body's
        /// prime meridian: "*.lonErr" / "*.pmErr" are constants of the game in a consistent world and move by the
        /// stale Zup angle when it is not. First a runway launch warped and recovered, for a rotating scene straight into
        /// the rotating space centre. Params: padWarpHours (2), kscWarpHours (6).
        /// </summary>
        [UITest(Kind = "Measure", StartsAt = StartState.Sph, Craft = "UITest_Plane", TimeoutSec = 1800)]
        public static IEnumerator Frame_TrackingStation(TestContext ctx)
        {
            double kscWarpHours = ctx.ParamDouble("kscWarpHours", 6);
            double padWarpHours = ctx.ParamDouble("padWarpHours", 2);
            string craft = Nav.LoadedCraft;
            ctx.Record("baseUT", Math.Round(Planetarium.GetUniversalTime(), 1));

            // A rotating scene straight into another (the space centre starts rotating, with no inertial pass): a
            // rotating CBUpdate never rewrites BodyFrame, so the space centre keeps whatever the runway left there.
            ctx.Step($"Runway, warp {padWarpHours} h, recover to the Space Center");
            yield return Nav.Launch(ctx);
            yield return Wait.Seconds(3);
            yield return Flight.WarpSeconds(padWarpHours * 3600);
            yield return Wait.Seconds(2);
            RecordConsistency(ctx, "pad", 0);
            yield return Nav.ToSpaceCenter(ctx);
            yield return Wait.Seconds(2);
            RecordConsistency(ctx, "ksc0", 0);
            yield return Nav.EnterEditor(ctx, EditorFacility.SPH, craft);

            ctx.Step("Launch, set a 300 km orbit");
            yield return Nav.Launch(ctx);
            yield return Wait.Seconds(3);
            Flight.SetOrbit(300e3, 300e3, 51.6);
            yield return Wait.Seconds(5);
            uint pid = FlightGlobals.ActiveVessel.persistentId;
            RecordConsistency(ctx, "flight1", pid);

            ctx.Step($"Space Center, warp {kscWarpHours} h");
            yield return Nav.ToSpaceCenter(ctx);
            yield return Wait.Seconds(2);
            RecordConsistency(ctx, "ksc", pid);
            yield return Flight.WarpSeconds(kscWarpHours * 3600);
            yield return Wait.Seconds(2);
            RecordConsistency(ctx, "kscEnd", pid);

            ctx.Step("Tracking station");
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);   // as the KSC buttons do; keeps the warped clock
            HighLogic.LoadScene(GameScenes.TRACKSTATION);
            yield return Wait.Scene(GameScenes.TRACKSTATION, 600);
            yield return Wait.Seconds(3);
            RecordConsistency(ctx, "ts", pid);
            yield return Flight.WarpSeconds(600);
            yield return Wait.Seconds(2);
            RecordConsistency(ctx, "ts2", pid);
            yield return Snap.Shot(ctx, "ts");

            ctx.Step("Fly the vessel from the tracking station");
            int idx = FlightGlobals.Vessels.FindIndex(x => x.persistentId == pid);
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            FlightDriver.StartAndFocusVessel("persistent", idx);
            yield return Wait.FlightReady(600);
            yield return Wait.Seconds(3);
            RecordConsistency(ctx, "flight2", pid);
            yield return Snap.Shot(ctx, "flight2");
        }

        /// <summary>
        /// Leaving a rotating flight must not disturb the outgoing scene. A vessel in a 130 km orbit (below Earth's
        /// inverse-rotation altitude, so the flight rotates) is warped 2 h, which grows InverseRotAngle, unpacked, and
        /// left for the Space Center. Its orbit elements (celestial, independent of the world axes) are recorded before
        /// the request, on every frame the outgoing scene still runs, from an onGameSceneSwitchRequested handler, and
        /// from the tracking station: a loaded vessel's orbit is re-derived from its rigidbody through Zup, so anything
        /// that changes Zup in the outgoing scene turns its LAN by the old angle. Then a warp at the Space Center and a
        /// load of the save from there (the clock jumps back in a rotating scene) for surface/orbit consistency.
        /// Params: orbitKm (130), warpHours (2), kscWarpHours (3).
        /// </summary>
        [UITest(Kind = "Measure", StartsAt = StartState.Sph, Craft = "UITest_Plane", TimeoutSec = 1800)]
        public static IEnumerator Frame_LeaveRotating(TestContext ctx)
        {
            double orbitKm = ctx.ParamDouble("orbitKm", 130);
            double warpHours = ctx.ParamDouble("warpHours", 2);
            double kscWarpHours = ctx.ParamDouble("kscWarpHours", 3);

            ctx.Step($"Launch, {orbitKm} km orbit, warp {warpHours} h");
            yield return Nav.Launch(ctx);
            yield return Wait.Seconds(3);
            Flight.SetOrbit(orbitKm * 1000, orbitKm * 1000, 51.6);
            yield return Wait.Seconds(3);
            Vessel v = FlightGlobals.ActiveVessel;
            uint pid = v.persistentId;
            // TimeWarp.WarpTo right after the Set Orbit cheat sometimes never engages; re-issue it until it does
            double warpUT = Planetarium.GetUniversalTime() + warpHours * 3600;
            for (int attempt = 0; attempt < 5 && Planetarium.GetUniversalTime() < warpUT - 1; ++attempt)
            {
                TimeWarp.fetch.WarpTo(warpUT);
                yield return Wait.Seconds(20);
                if (TimeWarp.CurrentRateIndex > 0)
                    yield return Wait.Until(() => Planetarium.GetUniversalTime() >= warpUT - 1 && TimeWarp.CurrentRateIndex == 0, 600, "warp to UT");
            }
            ctx.Record("warpAttemptsDoneUT", Math.Round(Planetarium.GetUniversalTime(), 1));
            yield return Wait.Until(() => !v.packed && v.mainBody.inverseRotation, 60, "unpacked in the rotating frame");
            yield return Wait.Seconds(2);
            RecordConsistency(ctx, "flight", pid);
            RecordOrbit(ctx, "pre", v.orbit);

            ctx.Step("Leave for the Space Center");
            string atSwitch = null;
            EventData<GameEvents.FromToAction<GameScenes, GameScenes>>.OnEvent onSwitch = _ =>
            {
                Vessel sv = FlightGlobals.Vessels.Find(x => x.persistentId == pid);
                atSwitch = sv?.orbit == null ? "gone" : OrbitString(sv.orbit);
            };
            GameEvents.onGameSceneSwitchRequested.Add(onSwitch);
            double lanMin = double.MaxValue, lanMax = double.MinValue, errMin = double.MaxValue, errMax = double.MinValue;
            int dyingFrames = 0;
            try
            {
                GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
                HighLogic.LoadScene(GameScenes.SPACECENTER);
                // the outgoing scene runs on until the loading buffer replaces it: sample for as long as the vessel lives
                for (int i = 0; i < 600 && v != null && v.orbit != null; ++i)
                {
                    ++dyingFrames;
                    lanMin = Math.Min(lanMin, v.orbit.LAN);
                    lanMax = Math.Max(lanMax, v.orbit.LAN);
                    CelestialBody body = v.mainBody;
                    Vector3d pos = v.orbit.getPositionAtUT(Planetarium.GetUniversalTime());
                    double err = Wrap(body.GetLongitude(pos) - (CelestialLon(pos - body.position) - body.rotationAngle));
                    errMin = Math.Min(errMin, err);
                    errMax = Math.Max(errMax, err);
                    yield return null;
                }
            }
            finally
            {
                GameEvents.onGameSceneSwitchRequested.Remove(onSwitch);
            }
            ctx.Record("dying.frames", dyingFrames);
            if (dyingFrames > 0)
            {
                ctx.Record("dying.LAN", $"{lanMin:F3}..{lanMax:F3}");
                ctx.Record("dying.lonErr", $"{errMin:F3}..{errMax:F3}");
            }
            ctx.Record("atSwitch", atSwitch);
            yield return Wait.Scene(GameScenes.SPACECENTER, 600);
            yield return Wait.Seconds(2);
            Ugui.DismissPopups();
            RecordConsistency(ctx, "ksc", pid);

            ctx.Step("Tracking station");
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            HighLogic.LoadScene(GameScenes.TRACKSTATION);
            yield return Wait.Scene(GameScenes.TRACKSTATION, 600);
            yield return Wait.Seconds(3);
            RecordConsistency(ctx, "ts", pid);
            Vessel tv = FlightGlobals.Vessels.Find(x => x.persistentId == pid);
            if (tv != null) RecordOrbit(ctx, "ts", tv.orbit);

            ctx.Step($"Space Center, warp {kscWarpHours} h, load the save");
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            HighLogic.LoadScene(GameScenes.SPACECENTER);
            yield return Wait.Scene(GameScenes.SPACECENTER, 600);
            yield return Wait.Seconds(2);
            Ugui.DismissPopups();
            RecordConsistency(ctx, "ksc2", pid);
            GamePersistence.SaveGame("uitest_frame", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            yield return Flight.WarpSeconds(kscWarpHours * 3600);
            yield return Wait.Seconds(2);
            RecordConsistency(ctx, "ksc2End", pid);
            Game g = GamePersistence.LoadGame("uitest_frame", HighLogic.SaveFolder, true, false);
            if (g == null) throw new UITestFailure("could not load uitest_frame");
            g.startScene = GameScenes.SPACECENTER;
            HighLogic.CurrentGame = g;
            g.Start();
            yield return Wait.Scene(GameScenes.SPACECENTER, 600);
            yield return Wait.Seconds(3);
            Ugui.DismissPopups();
            RecordConsistency(ctx, "kscLoaded", pid);
        }

        private static string OrbitString(Orbit o) =>
            $"LAN {o.LAN:F3} inc {o.inclination:F3} argPe {o.argumentOfPeriapsis:F3} sma {o.semiMajorAxis:F0}";

        private static void RecordOrbit(TestContext ctx, string key, Orbit o)
        {
            ctx.Record(key + ".orbit", OrbitString(o));
            ctx.Record(key + ".LAN", Math.Round(o.LAN, 3));
        }

        private static void RecordConsistency(TestContext ctx, string key, uint pid)
        {
            CelestialBody body = FlightGlobals.GetHomeBody();
            double ut = Planetarium.GetUniversalTime();
            ctx.Record(key + ".scene", HighLogic.LoadedScene.ToString());
            ctx.Record(key + ".ut", Math.Round(ut, 1));
            ctx.Record(key + ".rotating", body.inverseRotation);
            ctx.Record(key + ".directRotAngle", Math.Round(body.directRotAngle, 3));
            ctx.Record(key + ".inverseRotAngle", Math.Round(Planetarium.InverseRotAngle, 3));
            ctx.Record(key + ".zupAngle", Math.Round(CelestialLon(Vector3d.right), 3));   // world +X in the celestial frame

            Vector3d pm = body.GetWorldSurfacePosition(0, 0, 0) - body.position;
            ctx.Record(key + ".pmErr", Math.Round(Wrap(CelestialLon(pm) - body.rotationAngle), 3));

            Vessel v = pid == 0 ? null : FlightGlobals.Vessels.Find(x => x.persistentId == pid);
            if (v != null && v.orbit != null)
            {
                Vector3d pos = v.orbit.getPositionAtUT(ut);
                double lon = body.GetLongitude(pos);
                ctx.Record(key + ".vesselLon", Math.Round(lon, 3));
                ctx.Record(key + ".lonErr", Math.Round(Wrap(lon - (CelestialLon(pos - body.position) - body.rotationAngle)), 3));
            }
            CelestialBody sun = Planetarium.fetch.Sun;
            ctx.Record(key + ".subsolarLon", Math.Round(body.GetLongitude(sun.position), 3));
        }

        // Longitude of a Unity world direction in the celestial frame (Zup's local axes).
        private static double CelestialLon(Vector3d world)
        {
            Vector3d c = Planetarium.Zup.LocalToWorld(world.xzy);
            return Math.Atan2(c.y, c.x) * 180 / Math.PI;
        }

        private static double Wrap(double deg)
        {
            deg %= 360.0;
            if (deg > 180) deg -= 360;
            if (deg <= -180) deg += 360;
            return deg;
        }
    }
}
