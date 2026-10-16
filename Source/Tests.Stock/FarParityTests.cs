using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KSPUITest;
using UnityEngine;

namespace KSPUITest.Tests.Stock
{
    /// <summary>
    /// FAR flight parity: does a FAR build compute the same flight forces as another, bit for bit? Free flight cannot
    /// say (two runs of one build part ways within seconds), so this flies open loop (FlightReplay). mode=record
    /// places the craft at each segment's start, saves the game there (parity-&lt;segment&gt;.sfs), then loads each save
    /// and flies a scripted sequence of inputs from it - pulls to the stall, rolls, yaw, throttle - recording every
    /// part's rigidbody state each frame (&lt;segment&gt;.rec). mode=replay loads the same saves (param recording = the
    /// record run's results folder) and forces those states back each frame, writing FAR's per-part outputs
    /// (&lt;segment&gt;.out) and its whole part state every 50 frames (&lt;segment&gt;.state). Compare two replays with
    /// scratchpad parity_replay.py: the same build twice shows whether the replay is exact; two builds, whether
    /// the code differs. predict=1 also calls FAR's prediction API after every frame, at another condition (the leak
    /// test: with it the flight must still match a replay without).
    /// Params: mode (record|replay), recording, segments (comma list), seconds (override each segment's length), predict.
    /// </summary>
    public static class FarParityTests
    {
        private sealed class Step
        {
            public double At;
            public float? Pitch, Yaw, Roll, Throttle;
        }

        private sealed class Segment
        {
            public FarFlightProbe.Condition C;
            public float Pitch, Throttle;
            public double Seconds;
            public Step[] Steps;
            public string Name => C.Name;
        }

        // Each starts at a trimmed-looking attitude and input, then works the controls: the sequence is the same
        // everywhere, a pull hard enough to stall low down, both rolls and both yaws, and a throttle change.
        private static Step[] Maneuvers(float pitch, float throttle, bool stall) => new[]
        {
            new Step { At = 0, Pitch = pitch, Yaw = 0, Roll = 0, Throttle = throttle },
            new Step { At = 2, Pitch = 0.5f },
            new Step { At = 3.5, Pitch = -0.5f },
            new Step { At = 5, Pitch = 1f },
            new Step { At = stall ? 8 : 6.5, Pitch = pitch },
            new Step { At = stall ? 10 : 8, Roll = 1f },
            new Step { At = stall ? 11 : 9, Roll = -1f },
            new Step { At = stall ? 12 : 10, Roll = 0f, Yaw = 1f },
            new Step { At = stall ? 13 : 11, Yaw = -1f },
            new Step { At = stall ? 14 : 12, Yaw = 0f, Throttle = 1f - throttle },
            new Step { At = stall ? 16 : 13, Pitch = -0.3f },
        };

        private static List<Segment> Segments() => new List<Segment>
        {
            new Segment { C = new FarFlightProbe.Condition { Name = "subsonic", Mach = 0.5, AltM = 3000, AoADeg = 5, Lat = 0, Lon = -60 },
                          Pitch = 0.3f, Throttle = 0.3f, Seconds = 18, Steps = Maneuvers(0.3f, 0.3f, true) },
            new Segment { C = new FarFlightProbe.Condition { Name = "transonic", Mach = 0.95, AltM = 9000, AoADeg = 2, Lat = 0, Lon = -60 },
                          Pitch = 0f, Throttle = 1f, Seconds = 15, Steps = Maneuvers(0f, 1f, false) },
            new Segment { C = new FarFlightProbe.Condition { Name = "supersonic", Mach = 2.0, AltM = 14000, AoADeg = 4, Lat = 0, Lon = -60 },
                          Pitch = 0.2f, Throttle = 1f, Seconds = 15, Steps = Maneuvers(0.2f, 1f, false) },
            new Segment { C = new FarFlightProbe.Condition { Name = "hypersonic", Mach = 5.0, AltM = 30000, AoADeg = 10, Lat = 30, Lon = 150 },
                          Pitch = 0.5f, Throttle = 0f, Seconds = 15, Steps = Maneuvers(0.5f, 0f, false) },
        };

        [UITest("Far_FlightParity", StartsAt = StartState.Flight, Craft = "Aeris 4A", CraftDir = "SPH", LaunchSite = "Runway", TimeoutSec = 3000)]
        public static IEnumerator Far_FlightParity(TestContext ctx)
        {
            yield return Wait.Seconds(5);
            string mode = ctx.Param("mode", "record");
            bool predict = ctx.Param("predict", "0") == "1";
            double seconds = ctx.ParamDouble("seconds", 0);
            var only = ctx.Param("segments", null)?.Split(',').Select(s => s.Trim()).ToList();
            var segments = Segments().Where(s => only == null || only.Contains(s.Name)).ToList();
            if (seconds > 0) segments.ForEach(s => s.Seconds = seconds);
            FlightReplay.TraceFrames = (int)ctx.ParamDouble("trace", 0);
            string craft = FlightGlobals.ActiveVessel.vesselName;
            ctx.Record("mode", mode);
            ctx.Record("craft", craft);
            ctx.Record("farDll", FarDll());
            Setup();

            string recDir;
            if (mode == "record")
            {
                recDir = ctx.ResultsDir;
                yield return SaveStarts(ctx, segments);
            }
            else
            {
                recDir = ctx.Param("recording", null) ?? throw new UITestFailure("replay needs recording=<the record run's results folder>");
                foreach (Segment s in segments)
                {
                    string sfs = Path.Combine(recDir, "parity-" + s.Name + ".sfs");
                    if (!File.Exists(sfs)) throw new UITestFailure("no " + sfs);
                    File.Copy(sfs, Path.Combine(ctx.SaveDir, "parity-" + s.Name + ".sfs"), true);
                }
            }

            foreach (Segment s in segments)
            {
                ctx.Step($"{mode} {s.Name}");
                string rec = Path.Combine(recDir, s.Name + ".rec");
                string output = Path.Combine(ctx.ResultsDir, s.Name + ".out"), state = Path.Combine(ctx.ResultsDir, s.Name + ".state");
                if (mode == "record")
                {
                    int frames = (int)Math.Round(s.Seconds / TimeWarp.fixedDeltaTime);
                    FlightReplay.PrepareRecord(frames, rec, output, state);
                    Step[] steps = s.Steps;
                    FlightReplay.Script = (frame, st) =>
                    {
                        double t = frame * TimeWarp.fixedDeltaTime;
                        foreach (Step p in steps.Where(p => p.At <= t))
                        {
                            if (p.Pitch.HasValue) st.pitch = p.Pitch.Value;
                            if (p.Yaw.HasValue) st.yaw = p.Yaw.Value;
                            if (p.Roll.HasValue) st.roll = p.Roll.Value;
                            if (p.Throttle.HasValue) st.mainThrottle = p.Throttle.Value;
                        }
                    };
                    FlightReplay.AfterFrame = null;
                }
                else
                {
                    FlightReplay.PrepareReplay(rec, output, state);
                    FlightReplay.Script = null;
                    FlightReplay.AfterFrame = predict ? (Action<int, Vessel>)PredictElsewhere : null;
                }
                yield return RunFromSave(ctx, "parity-" + s.Name);
                ctx.Record(s.Name + ".frames", FlightReplay.Frame);
                ctx.Record(s.Name + ".voxelSwaps", string.Join(" ", FlightReplay.VoxelSwaps));
                ctx.Record(s.Name + ".predictionCalls", FarCallWatch.Summary());
                ctx.Record(s.Name + ".predictionsBlocked", FlightReplay.BlockedPredictions);
                if (FlightReplay.State != FlightReplay.Phase.Done)
                    throw new UITestFailure($"{s.Name}: {FlightReplay.State} {FlightReplay.Error}");
                Vessel v = FlightGlobals.ActiveVessel;
                ctx.Record(s.Name + ".end", string.Format(CultureInfo.InvariantCulture, "{0:F0} m, {1:F0} m/s, Mach {2:F2}, {3} parts",
                                                          v.altitude, v.srfSpeed, v.mach, v.parts.Count));
                yield return Snap.Shot(ctx, "end-" + s.Name);
            }
        }

        /// <summary>
        /// A whole flight as a player would fly it, recorded every frame (FlightTrace: FAR's Cl, Cd, stall fraction, and
        /// Cl, Cd, Cm from the force and torque FAR applied), for plotting one run against another: takeoff on
        /// SmartASS, then AtmosphereAutopilot cruise to each point (alt m : Mach), on to the next once within altTol of
        /// its altitude, then an air start at the hypersonic point (the Aeris cannot fly there) and AA straight down to
        /// the low point, that descent at most maxDescent deg (so it sheds the speed gradually rather than diving with
        /// it). Nothing in FAR or KSP is touched; whatever else is installed runs as it would.
        /// Params: points ("3000:0.5+12000:1.5", alt m : Mach [: altitude tolerance m, else altTol]; "stage" among them stages there), hyper ("30000:5", empty for none), low ("1000:0.5", empty for none), maxClimb (30, AA's default), maxDescent (10),
        /// altTol (3 m), warp (physics warp from takeoff on: 1, 2, 3 or 4), blockPredictions (1: FAR's prediction API does nothing, whoever calls it).
        /// </summary>
        [UITest("Far_FlightProfile", StartsAt = StartState.Flight, Craft = "Aeris 4A", CraftDir = "SPH", LaunchSite = "Runway", TimeoutSec = 3600)]
        public static IEnumerator Far_FlightProfile(TestContext ctx)
        {
            Assert.True(Aa.Present, "AtmosphereAutopilot is installed");
            var inv = CultureInfo.InvariantCulture;
            yield return Wait.Seconds(5);
            ctx.Record("farDll", FarDll());
            Flight.IgnoreMaxTemperature(true);
            // every prediction call counted by caller; with blockPredictions they return without predicting, so
            // nothing a prediction leaves on the wings reaches the flight
            if (!FarCallWatch.EnsurePatched()) throw new UITestFailure("FAR's prediction entry point not found");
            FarCallWatch.Reset();
            FarCallWatch.Block = ctx.Param("blockPredictions", "0") == "1";
            ctx.Record("predictionsBlocked", FarCallWatch.Block);
            Vessel v = FlightGlobals.ActiveVessel;
            int parts = v.parts.Count;
            FlightTrace.Phase = "start";
            FlightTrace.Begin(Path.Combine(ctx.ResultsDir, "flight_trace.csv"));
            try
            {
                ctx.Step("Take off");
                Ctrl.Attach();
                Ctrl.Sas(false);
                Ctrl.Brakes(false);
                Ctrl.SetThrottle(1f);
                Ctrl.Stage();
                FlightTrace.Phase = "roll";
                yield return Wait.Until(() => { Flight.AssertAlive(); return Flight.SurfaceSpeed > ctx.ParamDouble("rotateMs", 90); }, 120, "rotate speed");
                Type mj = AssemblyLoader.loadedAssemblies.Where(la => la.assembly.GetName().Name == "KSPUITest.MechJeb")
                                                        .Select(la => la.assembly.GetType("KSPUITest.MechJeb.Mj")).FirstOrDefault();
                if (mj == null) throw new UITestFailure("the takeoff needs MechJeb's SmartASS (KSPUITest.MechJeb)");
                FlightTrace.Phase = "takeoff";
                mj.GetMethod("SmartAssSurface").Invoke(null, new object[] { 90.0, ctx.ParamDouble("rotatePitchDeg", 10), 0.0 });
                double startAlt = Flight.Altitude;
                yield return Wait.Until(() => { Flight.AssertAlive(); return Flight.Altitude - startAlt > 25 && FlightGlobals.ActiveVessel.verticalSpeed > 1; }, 60, "airborne and climbing");
                mj.GetMethod("SmartAssOff").Invoke(null, null);
                Ctrl.Set(pitch: 0f);
                Ctrl.Detach();
                double warp = ctx.ParamDouble("warp", 1);
                ctx.Record("physicsWarp", warp);
                if (warp > 1) Flight.PhysicsWarpRate(warp);

                double maxClimb = ctx.ParamDouble("maxClimb", 30), maxDescent = ctx.ParamDouble("maxDescent", 10), altTol = ctx.ParamDouble("altTol", 3);
                ctx.Record("maxClimbDeg", maxClimb); ctx.Record("maxDescentDeg", maxDescent);
                // "stage" among the points fires the next stage there (the Aeris's aerospike, for the climb past
                // what its turboramjets reach)
                var points = (ctx.Param("points", "3000:0.5+12000:1.5") ?? "").Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p == "stage" ? null : p.Split(':').Select(x => double.Parse(x, inv)).ToArray()).ToList();
                bool gearUp = false;
                foreach (double[] p in points)
                {
                    if (p == null)
                    {
                        ctx.Step("Stage");
                        Ctrl.Stage();
                        continue;
                    }
                    string name = $"{p[0] / 1000:0.#}km-M{p[1]:0.##}";
                    ctx.Step("Fly to " + name);
                    FlightTrace.Phase = "to " + name;
                    yield return CruiseTo(ctx, p[0], p[1], maxClimb, p.Length > 2 ? p[2] : altTol, parts, name, () =>
                    {
                        if (!gearUp && Flight.Altitude - startAlt > 150) { Ctrl.Gear(false); gearUp = true; }
                    });
                }

                string hyper = ctx.Param("hyper", "30000:5");
                if (!string.IsNullOrEmpty(hyper))
                {
                    double[] h = hyper.Split(':').Select(x => double.Parse(x, inv)).ToArray();
                    ctx.Step($"Air start at {h[0] / 1000:0.#} km, Mach {h[1]:0.#}");
                    FlightTrace.Phase = "placing";
                    Aa.Off(FlightGlobals.ActiveVessel);
                    if (warp > 1) Flight.PhysicsWarp(0);   // the placement goes on rails, which drops warp anyway
                    yield return FarFlightProbe.Place(new FarFlightProbe.Condition { Name = "hypersonic", AltM = h[0], Mach = h[1], AoADeg = 3, Lat = 0, Lon = -60 });
                    if (warp > 1) Flight.PhysicsWarpRate(warp);
                }
                // Straight down to the low point, the descent angle capped so the speed bleeds on the way: at AA's
                // default 30 deg it reached 1 km at Mach 2.7, at 15 deg Mach 2.3 (gravity along the path matching the
                // drag up high), and pulled about 10 g levelling off; at 5 deg it took 400 s. No speed control on the
                // way down: with a speed target AA holds the altitude back while it slows.
                // low empty: the flight ends at the last point
                string lowParam = ctx.Param("low", "1000:0.5");
                if (!string.IsNullOrEmpty(lowParam))
                {
                    double[] low = lowParam.Split(':').Select(x => double.Parse(x, inv)).ToArray();
                    string lowName = $"{low[0] / 1000:0.#}km-M{low[1]:0.##}";
                    ctx.Step("Down to " + lowName);
                    FlightTrace.Phase = "down to " + lowName;
                    Ctrl.SetThrottle(0f);   // AA without speed control leaves the throttle: glide down at idle
                    yield return CruiseTo(ctx, low[0], low[1], maxDescent, altTol, parts, lowName, null, speedControl: false);
                }
            }
            finally
            {
                FlightTrace.End();
                if (FlightGlobals.ActiveVessel != null) Aa.Off(FlightGlobals.ActiveVessel);
                ctx.Record("predictionCalls", FarCallWatch.Summary());
                FarCallWatch.Block = false;
            }
            ctx.Record("parts", $"{FlightGlobals.ActiveVessel.parts.Count} of {parts}");
        }

        // AA cruise to a point (at most maxClimb deg up or down), done once within altTol of its altitude; a point
        // the craft cannot reach within 15 minutes is recorded and left.
        private static IEnumerator CruiseTo(TestContext ctx, double altM, double mach, double maxClimb, double altTol, int parts, string name, Action each, bool speedControl = true)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            CelestialBody body = v.mainBody;
            double p = body.GetPressure(altM);
            double speed = mach * body.GetSpeedOfSound(p, body.GetDensity(p, body.GetTemperature(altM)));
            Aa.Cruise(v, altM, speed, 90, maxClimb, speedControl);
            double t0 = Planetarium.GetUniversalTime(), lastErr = double.NaN;
            bool reached = false;
            yield return Wait.Until(() =>
            {
                Flight.AssertAlive();
                Vessel w = FlightGlobals.ActiveVessel;
                if (w.parts.Count != parts) throw new UITestFailure($"{name}: the vessel lost parts ({w.parts.Count} of {parts})");
                each?.Invoke();
                // within altTol, or crossed it since the last look (climbing fast under warp, a frame can step
                // over the window)
                double err = w.altitude - altM;
                reached = Math.Abs(err) < altTol || !double.IsNaN(lastErr) && Math.Sign(err) != Math.Sign(lastErr);
                lastErr = err;
                return reached || Planetarium.GetUniversalTime() - t0 > 900;
            }, 1200, "at " + name, shot: false);
            Vessel e = FlightGlobals.ActiveVessel;
            ctx.Record(name, string.Format(CultureInfo.InvariantCulture, "{0} after {1:F0} s: {2:F0} m, Mach {3:F2}",
                                           reached ? "reached" : "NOT reached", Planetarium.GetUniversalTime() - t0, e.altitude, e.mach));
        }

        private static string FarDll()
        {
            var a = AssemblyLoader.loadedAssemblies.FirstOrDefault(x => x.name == "FerramAerospaceResearch");
            if (a == null) return "?";
            var fi = new FileInfo(a.path);
            using (var md5 = System.Security.Cryptography.MD5.Create())
            using (var f = fi.OpenRead())
                return BitConverter.ToString(md5.ComputeHash(f)).Replace("-", "").ToLowerInvariant() + " " + fi.LastWriteTime.ToString("s");
        }

        // Nothing in the run may change the vessel (a part lost, or burnt, or out of fuel) or ask FAR for predictions.
        private static void Setup()
        {
            Flight.InfinitePropellant(true);
            Flight.IgnoreMaxTemperature(true);
            CheatOptions.UnbreakableJoints = true;
            CheatOptions.NoCrashDamage = true;
            Reflect.SetStatic(Reflect.FindType("FerramAerospaceResearch.FARDebugValues"), "allowStructuralFailures", false);
            FarFlightProbe.QuietOtherPredictors();
        }

        // Places the craft at each segment's start in turn, gear up, controls and engines as the segment starts, and
        // saves the game there.
        private static IEnumerator SaveStarts(TestContext ctx, List<Segment> segments)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            v.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            Ctrl.Attach();
            Ctrl.Set(pitch: 0f, yaw: 0f, roll: 0f);
            Ctrl.SetThrottle(0f);
            Ctrl.Stage();
            bool gearUp = false;
            foreach (Segment s in segments)
            {
                ctx.Step("save start " + s.Name);
                // Centered controls and settled surfaces in the save: the surfaces move while the vessel waits packed
                // for its voxels, as long as that takes, if the save holds an input; the script's inputs start at
                // frame 0. The engines likewise saved at the throttle they are set to.
                Ctrl.Set(pitch: 0f, yaw: 0f, roll: 0f, throttle: s.Throttle);
                yield return FarFlightProbe.Place(s.C);
                if (!gearUp)
                {
                    v.ActionGroups.SetGroup(KSPActionGroup.Gear, false);
                    gearUp = true;
                    yield return Wait.GameSeconds(4);
                    yield return FarFlightProbe.Place(s.C);
                }
                FarFlightProbe.SetEngineThrottle(v, s.Throttle);
                for (int i = 0; i < 50; ++i) yield return new WaitForFixedUpdate();
                FarFlightProbe.SetEngineThrottle(v, s.Throttle);
                yield return new WaitForFixedUpdate();
                Game game = HighLogic.CurrentGame.Updated();
                string name = "parity-" + s.Name;
                GamePersistence.SaveGame(game, name, HighLogic.SaveFolder, SaveMode.OVERWRITE);
                File.Copy(Path.Combine(ctx.SaveDir, name + ".sfs"), Path.Combine(ctx.ResultsDir, name + ".sfs"), true);
                Log.Info($"saved {name}: {v.altitude:F0} m, {v.srfSpeed:F0} m/s");
            }
            Ctrl.Detach();
        }

        // Loads a flight save, keeps the vessel packed until FAR's voxels are built and idle, then lets FlightReplay
        // take it off rails and run.
        private static IEnumerator RunFromSave(TestContext ctx, string save)
        {
            Ctrl.Detach();
            var node = GamePersistence.LoadSFSFile(save, HighLogic.SaveFolder);
            if (node == null) throw new UITestFailure("could not read save " + save);
            Game game = GamePersistence.LoadGameCfg(node, save, true, false);
            if (game == null || game.flightState == null) throw new UITestFailure("save " + save + " did not load");
            GamePersistence.UpdateScenarioModules(game);
            GameEvents.onGameStatePostLoad.Fire(node);
            var oldDriver = FlightDriver.fetch;
            Log.Info("loading " + save);
            FlightDriver.StartAndFocusVessel(game, game.flightState.activeVesselIdx);
            yield return Wait.Until(() => HighLogic.LoadedSceneIsFlight && FlightDriver.fetch != null && FlightDriver.fetch != oldDriver
                                          && FlightGlobals.ready && FlightGlobals.ActiveVessel != null, 300, "flight loaded from " + save, shot: false);
            Vessel v = FlightGlobals.ActiveVessel;
            if (!v.packed) throw new UITestFailure(save + ": the vessel was off rails before the hold");
            Setup();
            yield return Wait.Until(() => FarIdle(v), 120, "FAR voxels built and idle", shot: false);
            for (int i = 0; i < 25; ++i) yield return new WaitForFixedUpdate();
            if (!FarIdle(v)) throw new UITestFailure(save + ": FAR started another voxel update");
            FlightReplay.Freeze();
            yield return Wait.Until(() => FlightReplay.State == FlightReplay.Phase.Failed || FlightReplay.Voxelized && FarIdle(v), 120,
                                    "FAR voxels rebuilt at the start pose", shot: false);
            if (FlightReplay.State == FlightReplay.Phase.Failed) yield break;
            for (int i = 0; i < 25; ++i) yield return new WaitForFixedUpdate();
            if (!FarIdle(v)) throw new UITestFailure(save + ": FAR started another voxel update");
            FarCallWatch.EnsurePatched();
            FarCallWatch.Reset();
            FlightReplay.Begin();
            yield return Wait.Until(() => FlightReplay.State == FlightReplay.Phase.Done || FlightReplay.State == FlightReplay.Phase.Failed,
                                    FlightReplay.FrameCount * TimeWarp.fixedDeltaTime * 4 + 60, "replay of " + save, shot: false);
        }

        private static bool FarIdle(Vessel v)
        {
            if (!FarSim.Voxelized(v)) return false;
            foreach (VesselModule vm in v.GetComponents<VesselModule>())
                if (vm.GetType().Name == "FARVesselAero")
                {
                    if (TryGet(vm, "_updateQueued") is bool q && q) return false;
                    object aero = TryGet(vm, "_vehicleAero");
                    if (aero == null) return false;
                    if (TryGet(aero, "Voxelizing") is bool z && z) return false;
                    if (TryGet(aero, "CalculationCompleted") is bool c && c) return false;
                    return true;
                }
            return false;
        }

        private static object TryGet(object o, string member)
        {
            try { return Reflect.Get(o, member); }
            catch (UITestFailure) { return null; }
        }

        // The leak test: a prediction at another place, speed and attitude after every frame.
        private static void PredictElsewhere(int frame, Vessel v)
        {
            Vector3 vel = Quaternion.AngleAxis(10f, FarApi.PitchUpAxis(v)) * (Vector3)v.srf_velocity * 2f;
            FlightReplay.AllowPredictions = true;
            try { FarApi.PredictAtFull(v, vel, v.latitude + 5, v.longitude + 20, v.altitude + 8000, Planetarium.GetUniversalTime(), out _, out _); }
            finally { FlightReplay.AllowPredictions = false; }
        }
    }
}
