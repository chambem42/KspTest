using System;
using System.Collections;
using System.IO;
using System.Text;
using KspTest;
using KspTest.MechJeb;
using KspTest.RP1;
using MuMech;

namespace KspTest.Tests.RP1
{
    public static class RcsOrbitTests
    {
        /// <summary>
        /// Attitude hold in orbit at high warp, with the RcsInputPerStep patch on and off (RcsHold.Run: segments, hold,
        /// patch and the rest of its params). The SSTO is put on a circular orbit (altKm, default 160: above the air;
        /// inc 29.6, lan 161) by an RP-1 reentry sim, RCS on, propellant infinite.
        /// </summary>
        [UITest("Rcs_RP1_OrbitHold", Requires = "RP-1,MechJeb", StartsAt = StartState.Vab, Craft = "Mk2 SSTO Big", TimeoutSec = 7200)]
        public static IEnumerator Rcs_RP1_OrbitHold(TestContext ctx)
        {
            Ugui.DismissPopups();
            string craft = ctx.Param("craft", "Mk2 SSTO Big");
            double altKm = ctx.ParamDouble("altKm", 160), inc = ctx.ParamDouble("inc", 29.6), lan = ctx.ParamDouble("lan", 161);

            ctx.Step($"Open {craft} in the VAB");
            yield return Nav.EnsureState(ctx, StartState.Vab, ctx.CraftPath(craft));
            Ugui.DismissPopups();
            ctx.Record("propellantDirect", Rp1.ApplyPropellantLoadDirect(0));

            ctx.Step("Into orbit");
            yield return Rp1.ConfigureSim(new SimSpec
            {
                StartInOrbit = true, Mode = "Reentry", PeKm = 70, Arrival = "Apoapsis", ApKm = 150, EntryAltKm = 140,
                IncDeg = 0, LanDeg = 0, ArgPeDeg = 0, UT = 2019828800, Attitude = "Prograde", LogTelemetry = false, PropellantPct = 0,
            });
            yield return Rp1.StartSim();
            var v = FlightGlobals.ActiveVessel;
            Ctrl.RetractGear();
            Ctrl.SetThrottle(0f);
            yield return Wait.Seconds(5);
            ctx.Record("rcsThrusters", Ctrl.EnableRcs());
            v.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            Flight.IgnoreMaxTemperature(true);
            Flight.InfinitePropellant(true);
            TrajApi.HoldWithSmartASS = false;
            Flight.SetOrbitAtMeanAnomaly(altKm * 1000, altKm * 1000, inc, lan, 0, 0);
            yield return null;
            Flight.PointAtAoA(0);
            yield return RcsHold.Run(ctx);
        }

        /// <summary>
        /// RP-1's avionics lock against RCS at high warp. RP-1 locks RCS on a vessel over its avionics mass limit by
        /// zeroing the vessel's control state around ModuleRCS.Update (RP0.Harmony.PatchModuleRCS), besides taking SAS and
        /// MechJeb off; a fix that moves RCS's input read out of Update would let RCS answer again. The SSTO is put in
        /// orbit and flown by a fly-by-wire callback of its own, as another mod's autopilot (kOS) would be - pitch, yaw
        /// and roll bang-bang at +-0.5 every 2 s, lateral X/Y 0.5, no fore/aft (allowed by an axial lock) - and RCS thrust
        /// is measured every physics step over segGameSec (default 60) game seconds per segment (default
        /// x1,tc12,btw12, as Rcs_RP1_OrbitHold's): first with every ModuleAvionics dead (locked: expect no thrust), then
        /// with them restored (unlocked: the same command fires RCS, the control). Writes avionicslock.csv.
        /// </summary>
        [UITest("Rcs_RP1_AvionicsLock", Requires = "RP-1", StartsAt = StartState.Vab, Craft = "Mk2 SSTO Big", TimeoutSec = 3600)]
        public static IEnumerator Rcs_RP1_AvionicsLock(TestContext ctx)
        {
            Ugui.DismissPopups();
            string craft = ctx.Param("craft", "Mk2 SSTO Big");
            double segGameSec = ctx.ParamDouble("segGameSec", 60), segMaxRealSec = ctx.ParamDouble("segMaxRealSec", 60), settleSec = ctx.ParamDouble("settleSec", 5);
            string[] segments = (ctx.Param("segments", "x1,tc12,btw12") ?? "").Split(',');
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            ctx.Step($"Open {craft} in the VAB");
            yield return Nav.EnsureState(ctx, StartState.Vab, ctx.CraftPath(craft));
            Ugui.DismissPopups();
            ctx.Record("propellantDirect", Rp1.ApplyPropellantLoadDirect(0));

            ctx.Step("Into orbit");
            yield return Rp1.ConfigureSim(new SimSpec
            {
                StartInOrbit = true, Mode = "Reentry", PeKm = 70, Arrival = "Apoapsis", ApKm = 150, EntryAltKm = 140,
                IncDeg = 0, LanDeg = 0, ArgPeDeg = 0, UT = 2019828800, Attitude = "Prograde", LogTelemetry = false, PropellantPct = 0,
            });
            yield return Rp1.StartSim();
            var v = FlightGlobals.ActiveVessel;
            Ctrl.RetractGear();
            Ctrl.SetThrottle(0f);
            yield return Wait.Seconds(5);
            ctx.Record("rcsThrusters", Ctrl.EnableRcs());
            v.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            Flight.IgnoreMaxTemperature(true);
            Flight.InfinitePropellant(true);
            ctx.Record("infiniteElectricity", CheatOptions.InfiniteElectricity);
            TrajApi.HoldWithSmartASS = false;
            Flight.SetOrbitAtMeanAnomaly(160000, 160000, 29.6, 161, 0, 0);
            yield return null;
            Flight.PointAtAoA(0);

            // RP0.ControlLocker (internal): Instance.ShouldLock() -> Unlocked / Axial / Locked
            Type lockerType = null;
            foreach (var a in AssemblyLoader.loadedAssemblies)
                lockerType = lockerType ?? a.assembly.GetType("RP0.ControlLocker", false);
            Assert.True(lockerType != null, "RP0.ControlLocker found");
            var all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
            Func<string> lockLevel = () =>
            {
                object locker = lockerType.GetProperty("Instance", all)?.GetValue(null, null);
                return locker == null ? "no ControlLocker" : lockerType.GetMethod("ShouldLock", all, null, Type.EmptyTypes, null).Invoke(locker, null).ToString();
            };
            Action requestLockCheck = () => GameEvents.onVesselWasModified.Fire(FlightGlobals.ActiveVessel);

            var avionics = new System.Collections.Generic.List<RP0.ModuleAvionics>();
            foreach (Part p in v.parts)
                avionics.AddRange(p.FindModulesImplementing<RP0.ModuleAvionics>());
            ctx.Record("avionicsModules", avionics.Count);
            int uncontrolledCommand = 0;
            foreach (Part p in v.parts)
                if (p.FindModuleImplementing<ModuleCommand>() != null && p.FindModuleImplementing<RP0.ModuleAvionics>() == null)
                    ++uncontrolledCommand;
            ctx.Record("commandPartsWithoutAvionics", uncontrolledCommand);

            // the command, every physics step, as a mod's fly-by-wire autopilot gives it
            FlightInputCallback command = s =>
            {
                double t = Planetarium.GetUniversalTime();
                float sign = Math.Sin(2 * Math.PI * t / 4) >= 0 ? 0.5f : -0.5f;
                s.pitch = sign;
                s.yaw = -sign;
                s.roll = sign;
                s.X = 0.5f;
                s.Y = 0.5f;
            };
            v.OnFlyByWire += command;

            var csv = new StringBuilder("state,segment,lockLevel,rate,achievedRate,stepsPerFrame,gameSec,rcsFiringPct,rcsMeanKN,rcsMaxKN,rcsImpulseKNs\n");
            var samplerObject = new UnityEngine.GameObject("KspTest.AvionicsLockSampler");
            var sampler = samplerObject.AddComponent<RcsHoldSampler>();
            try
            {
                foreach (string state in new[] { "locked", "unlocked" })
                {
                    foreach (var m in avionics)
                        m.dead = state == "locked";
                    requestLockCheck();
                    yield return Wait.Seconds(2);
                    string level = lockLevel();
                    ctx.Record(state + ".lockLevel", level);
                    if (state == "locked")
                        Assert.True(level != "Unlocked", $"avionics lock engaged with the avionics dead (got {level})");
                    else
                        Assert.Equal("Unlocked", level, "avionics lock released with the avionics restored");
                    foreach (string seg in segments)
                    {
                        string mode = seg.StartsWith("tc") ? "tc" : seg.StartsWith("btw") ? "btw" : "x1";
                        double rate = mode == "x1" ? 1 : double.Parse(seg.Substring(mode.Length), inv);
                        string label = $"{state}.{seg}";
                        ctx.Step($"RCS under the fly-by-wire command, avionics {state}, {seg}");
                        yield return Wait.Seconds(settleSec);
                        if (mode == "tc")
                            Assert.True(Flight.HyperWarp(rate, 1.0), "TimeControl hyper-warp on");
                        else if (mode == "btw")
                            Flight.PhysicsWarpRate(rate);
                        yield return null;
                        sampler.Begin(label);
                        double rcsMax = 0;
                        float real0 = UnityEngine.Time.realtimeSinceStartup;
                        double ut0 = Planetarium.GetUniversalTime();
                        while (Planetarium.GetUniversalTime() - ut0 < segGameSec && UnityEngine.Time.realtimeSinceStartup - real0 < segMaxRealSec)
                        {
                            Flight.AssertAlive();
                            double rcs = 0;
                            foreach (Part p in FlightGlobals.ActiveVessel.parts)
                                foreach (ModuleRCS m in p.FindModulesImplementing<ModuleRCS>())
                                    if (m.thrustForces != null)
                                        foreach (float f in m.thrustForces)
                                            rcs += f;
                            rcsMax = Math.Max(rcsMax, rcs);
                            yield return null;
                        }
                        sampler.End();
                        double gameSec = Planetarium.GetUniversalTime() - ut0, realSec = UnityEngine.Time.realtimeSinceStartup - real0;
                        string levelDuring = lockLevel();
                        if (mode == "tc")
                            Flight.HyperWarpOff();
                        else if (mode == "btw")
                            Flight.WarpOff();
                        yield return Wait.Until(() => TimeWarp.CurrentRate <= 1.01 && UnityEngine.Time.timeScale <= 1.01, 60, "back to 1x", shot: false);
                        var s = sampler;
                        double T = s.GameTime > 0 ? s.GameTime : double.NaN;
                        double firingPct = 100 * s.RcsFiring / T, meanKN = s.RcsImpulse / T;
                        csv.Append(state).Append(',').Append(seg).Append(',').Append(levelDuring).Append(',').Append(rate.ToString(inv)).Append(',')
                           .Append((gameSec / Math.Max(realSec, 1e-6)).ToString("F2", inv)).Append(',')
                           .Append((s.StepCount / Math.Max(1.0, s.FrameCount)).ToString("F2", inv)).Append(',').Append(gameSec.ToString("F1", inv)).Append(',')
                           .Append(firingPct.ToString("F2", inv)).Append(',').Append(meanKN.ToString("F4", inv)).Append(',')
                           .Append(rcsMax.ToString("F3", inv)).Append(',').Append(s.RcsImpulse.ToString("F2", inv)).Append('\n');
                        Directory.CreateDirectory(ctx.ResultsDir);
                        File.WriteAllText(Path.Combine(ctx.ResultsDir, "avionicslock.csv"), csv.ToString());
                        ctx.Record(label + ".lockLevel", levelDuring);
                        ctx.Record(label + ".rcsFiringPct", Math.Round(firingPct, 2));
                        ctx.Record(label + ".rcsImpulseKNs", Math.Round(s.RcsImpulse, 2));
                    }
                }
            }
            finally
            {
                UnityEngine.Object.Destroy(samplerObject);
                FlightGlobals.ActiveVessel.OnFlyByWire -= command;
                foreach (var m in avionics)
                    m.dead = false;
            }
        }
    }
}
