using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using KSPUITest;
using KSPUITest.MechJeb;
using UnityEngine;

namespace KSPUITest.Tests.MechJeb
{
    /// <summary>
    /// A/B regression probe of FAR in flight, for running against two FAR builds and diffing the values
    /// (compare-results.ps1). Two layers: the prediction API (FARAPI.CalculateVesselAeroForces) sampled on the runway
    /// over a grid of synthetic states, which is deterministic and exercises the flight-scene voxelization and the
    /// prediction force path; and the forces FAR actually applies while the plane is flown at held pitches, both as
    /// steady-state means and as the ratio to the prediction at the same instantaneous state (which cancels the
    /// flight-to-flight scatter of the state itself).
    /// </summary>
    public static class FarRegressFlightTests
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static double R(double v) => double.IsNaN(v) || double.IsInfinity(v) ? v : Math.Round(v, 5);

        /// <summary>
        /// Params: crafts ("+"-separated, default "Aeris 4A+PoofySkirt"), heading (90), climbToM (800), pitches ("8,3"),
        /// settleSec (12), sampleSec (15), rotateSpeed (60), cruiseThrottle (0.35), disableWheels (1).
        /// </summary>
        [UITest("Far_Regress_Flight", Requires = "MechJeb", StartsAt = StartState.Flight, Craft = "Aeris 4A", CraftDir = "SPH", LaunchSite = "Runway", TimeoutSec = 2400)]
        public static IEnumerator Far_Regress_Flight(TestContext ctx)
        {
            ctx.Record("farBuild", FarSim.FarVersion());
            var grid = new StringBuilder("craft,speed,alpha,beta,altOffset,drag,lift,side,pitchM,yawM,rollM\n");
            var frames = new StringBuilder("craft,hold,missionTime,altitude,srfSpeed,mach,qkPa,aoa,pitchInput,deflectDeg,appDrag,appLift,appSide,appPitchM,appWingM,appBodyM,predDrag,predLift,predPitchM,Cl,Cd,refArea\n");
            var areas = new StringBuilder(FarApi.PartAreasHeader);
            string[] crafts = (ctx.Param("crafts") ?? "Aeris 4A+PoofySkirt").Split('+');
            var failed = new List<string>();
            foreach (string craftRaw in crafts)
            {
                string craft = craftRaw.Trim();
                ctx.Step("Craft " + craft);
                yield return Nav.EnsureState(ctx, StartState.Flight, ctx.CraftPath(craft, "SPH"), "Runway");
                var leg = FlightCraft(ctx, craft, grid, frames, areas);
                string error = null;
                while (true)
                {
                    try { if (!leg.MoveNext()) break; }
                    catch (Exception ex) { error = ex.Message; break; }
                    yield return leg.Current;
                }
                File.WriteAllText(Path.Combine(ctx.ResultsDir, "regress-flight-predict.csv"), grid.ToString());
                File.WriteAllText(Path.Combine(ctx.ResultsDir, "regress-flight-frames.csv"), frames.ToString());
                File.WriteAllText(Path.Combine(ctx.ResultsDir, "regress-flight-areas.csv"), areas.ToString());
                if (error != null)
                {
                    Log.Warn($"craft {craft} failed: {error}");
                    ctx.Record(craft + ".failed", error);
                    failed.Add(craft);
                    Mj.SmartAssOff();
                }
            }
            Assert.True(failed.Count == 0, "every craft flew its sequence (failed: " + string.Join(", ", failed) + ")");
        }

        private static IEnumerator FlightCraft(TestContext ctx, string craft, StringBuilder grid, StringBuilder frames, StringBuilder areas)
        {
            Assert.True(Mj.Present, "vessel has a MechJebCore (MechJebForAll or an MJ part)");
            string k = craft + ".";
            double heading = ctx.ParamDouble("heading", 90), climbTo = ctx.ParamDouble("climbToM", 800);
            double settleSec = ctx.ParamDouble("settleSec", 12), sampleSec = ctx.ParamDouble("sampleSec", 15), rotateSpeed = ctx.ParamDouble("rotateSpeed", 60);
            string[] pitches = (ctx.Param("pitches") ?? "8,3").Split(',');
            double cruiseThrottle = ctx.ParamDouble("cruiseThrottle", 0.35);
            var v = FlightGlobals.ActiveVessel;

            ctx.Step($"{craft}: runway voxelization");
            yield return Wait.Until(() => FarSim.Voxelized(FlightGlobals.ActiveVessel), 90, "FAR voxelization in flight", shot: false);
            yield return Wait.Seconds(6);
            Assert.True(FarApi.VoxelInfo(out double el, out Vector3 bs, out double len, out double mcs), "FAR voxel info");
            FarApi.PartAreas(v.parts, craft, areas, out double areaAll, out double areaFixed);
            ctx.Record(k + "parts", v.parts.Count); ctx.Record(k + "massT", R(v.totalMass));
            ctx.Record(k + "voxel.elementSize", R(el)); ctx.Record(k + "voxel.boundsVolume", R(bs.x * bs.y * bs.z));
            ctx.Record(k + "voxel.length", R(len)); ctx.Record(k + "voxel.maxCrossSection", R(mcs));
            ctx.Record(k + "voxel.areaAll", R(areaAll)); ctx.Record(k + "voxel.areaFixed", R(areaFixed));
            ctx.Record(k + "refArea", R(FarApi.VesselStat(v, "VesselRefArea")));

            ctx.Step($"{craft}: prediction grid on the runway");
            var cases = new List<double[]>();   // speed, alpha, beta, altitude offset
            foreach (double s in new double[] { 60, 150, 250, 400 })
                foreach (double a in new double[] { -2, 0, 4, 8, 15 })
                    cases.Add(new[] { s, a, 0, 0 });
            cases.Add(new double[] { 150, 4, 5, 0 });
            cases.Add(new double[] { 150, 4, -5, 0 });
            cases.Add(new double[] { 250, 4, 0, 6000 });
            cases.Add(new double[] { 400, 4, 0, 12000 });
            foreach (double[] c in cases)
            {
                Vector3 vel = FarSim.BodyVelocity(v, c[0], c[1], c[2]);
                Assert.True(FarSim.Predict(v, vel, v.altitude + c[3], out double d, out double l, out double sd, out double pm, out double ym, out double rm), "FARAPI prediction");
                string tag = $"pred.v{c[0]:0}a{c[1]:0}b{c[2]:0}h{c[3]:0}.";
                ctx.Record(k + tag + "drag", R(d)); ctx.Record(k + tag + "lift", R(l)); ctx.Record(k + tag + "side", R(sd));
                ctx.Record(k + tag + "pitchM", R(pm)); ctx.Record(k + tag + "yawM", R(ym)); ctx.Record(k + tag + "rollM", R(rm));
                grid.Append(string.Join(",", new[] { craft, c[0].ToString(Inv), c[1].ToString(Inv), c[2].ToString(Inv), c[3].ToString(Inv),
                    d.ToString("G9", Inv), l.ToString("G9", Inv), sd.ToString("G9", Inv), pm.ToString("G9", Inv), ym.ToString("G9", Inv), rm.ToString("G9", Inv) })).Append('\n');
            }

            if (ctx.ParamDouble("disableWheels", 1) != 0)
            {
                int wheels = 0;
                foreach (var p in v.parts)
                    foreach (var w in p.FindModulesImplementing<ModuleReactionWheel>()) { w.State = ModuleReactionWheel.WheelState.Disabled; ++wheels; }
                ctx.Record(k + "reactionWheelsDisabled", wheels);
            }
            v.ActionGroups.SetGroup(KSPActionGroup.RCS, false);

            ctx.Step($"{craft}: take off and climb");
            double startAlt = Flight.Altitude;
            Ctrl.Stage();
            Ctrl.SetThrottle(1f);
            Ctrl.Brakes(false);
            Mj.SmartAssSurface(heading, pitch: 0);
            yield return Wait.Until(() => { Flight.AssertAlive(); return Flight.SurfaceSpeed > rotateSpeed; }, 120, $"surface speed > {rotateSpeed} m/s");
            Mj.SmartAssSurface(heading, pitch: 8);
            Ctrl.SetThrottle((float)cruiseThrottle);   // full throttle takes an overpowered jet supersonic within the sampling window
            yield return Wait.Until(() => { Flight.AssertAlive(); return Flight.Altitude - startAlt > climbTo; }, 300, "climb to the sampling altitude");
            Ctrl.Gear(false);
            yield return Wait.Seconds(5);

            foreach (string ps in pitches)
            {
                double pitch = double.Parse(ps.Trim(), Inv);
                ctx.Step($"{craft}: hold {pitch} deg pitch, applied vs predicted aero");
                Mj.SmartAssSurface(heading, pitch: pitch);
                yield return Wait.Seconds(settleSec);
                string hk = k + $"hold{pitch:0}.";
                var acc = new Dictionary<string, List<double>>();
                void Add(string name, double val) { if (!acc.TryGetValue(name, out var l)) acc[name] = l = new List<double>(); l.Add(val); }
                double t0 = FlightGlobals.ActiveVessel.missionTime;
                yield return Wait.Until(() =>
                {
                    var av = FlightGlobals.ActiveVessel;
                    Flight.AssertAlive();
                    FarSim.Applied(av, out double aD, out double aL, out double aS, out double aM, out double aWM, out double aBM, out double defl);
                    FarSim.Predict(av, av.srf_velocity, av.altitude, out double pD, out double pL, out _, out double pM, out _, out _);
                    double cl = FarApi.VesselStat(av, "VesselLiftCoeff"), cd = FarApi.VesselStat(av, "VesselDragCoeff"), refA = FarApi.VesselStat(av, "VesselRefArea");
                    Add("altitude", av.altitude); Add("srfSpeed", av.srfSpeed); Add("mach", av.mach); Add("qkPa", av.dynamicPressurekPa); Add("aoa", Flight.AoA);
                    Add("pitchInput", av.ctrlState.pitch); Add("deflectDeg", defl);
                    Add("appDrag", aD); Add("appLift", aL); Add("appSide", aS); Add("appPitchM", aM); Add("appWingM", aWM); Add("appBodyM", aBM);
                    Add("predDrag", pD); Add("predLift", pL); Add("predPitchM", pM);
                    Add("dragRatio", aD / pD); Add("liftRatio", aL / pL); Add("pitchMDiff", aM - pM);
                    Add("Cl", cl); Add("Cd", cd); Add("LD", cl / cd);
                    frames.Append(string.Join(",", new[] { craft, ps.Trim(), av.missionTime.ToString("F2", Inv), av.altitude.ToString("F1", Inv), av.srfSpeed.ToString("F2", Inv), av.mach.ToString("F4", Inv),
                        av.dynamicPressurekPa.ToString("F4", Inv), Flight.AoA.ToString("F3", Inv), av.ctrlState.pitch.ToString("F4", Inv), defl.ToString("F3", Inv),
                        aD.ToString("F3", Inv), aL.ToString("F3", Inv), aS.ToString("F3", Inv), aM.ToString("F3", Inv), aWM.ToString("F3", Inv), aBM.ToString("F3", Inv),
                        pD.ToString("F3", Inv), pL.ToString("F3", Inv), pM.ToString("F3", Inv), cl.ToString("F5", Inv), cd.ToString("F5", Inv), refA.ToString("F3", Inv) })).Append('\n');
                    return av.missionTime - t0 > sampleSec;
                }, sampleSec + 30, "sampling window", shot: false);
                ctx.Record(hk + "samples", acc["aoa"].Count);
                var means = new Dictionary<string, double>();
                foreach (var kv in acc)
                {
                    double n = 0, s = 0, s2 = 0;
                    foreach (double x in kv.Value) if (!double.IsNaN(x) && !double.IsInfinity(x)) { ++n; s += x; s2 += x * x; }
                    double mean = n > 0 ? s / n : double.NaN;
                    means[kv.Key] = mean;
                    ctx.Record(hk + kv.Key, R(mean));
                    if (kv.Key == "dragRatio" || kv.Key == "liftRatio" || kv.Key == "aoa" || kv.Key == "appPitchM" || kv.Key == "mach")
                        ctx.Record(hk + kv.Key + ".sd", R(n > 1 ? Math.Sqrt(Math.Max(0, s2 / n - mean * mean)) : double.NaN));
                }
                // ratio of the means (the per-frame lift ratio blows up through zero lift)
                ctx.Record(hk + "dragRatioOfMeans", R(means["appDrag"] / means["predDrag"]));
                ctx.Record(hk + "liftRatioOfMeans", R(means["appLift"] / means["predLift"]));
            }
            Mj.SmartAssOff();
        }
    }
}
