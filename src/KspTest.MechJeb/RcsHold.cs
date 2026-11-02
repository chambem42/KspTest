using System;
using System.Collections;
using System.IO;
using System.Text;
using MuMech;

namespace KspTest.MechJeb
{
    /// <summary>
    /// Attitude hold in orbit at high warp, with the RcsInputPerStep patch (ModuleRCS reading its inputs every physics
    /// step) on and off: the part of Rcs_RP1_OrbitHold / Rcs_Stock_OrbitHold after the vessel is in orbit, RCS on and
    /// propellant infinite. The vessel is held with MechJeb SmartASS (orbital prograde by default,
    /// target=normal_plus/radial_plus/...) or, with hold=sas, stock SAS (sasMode, default Prograde). Each segment settles
    /// at 1x for settleSec (20) real seconds, then warps for segGameSec (600) game seconds (at most segMaxRealSec, 150,
    /// real) and measures, every physics step, MechJeb's attitude error, the nose's angle off orbital prograde, the
    /// angular rate, the control inputs and RCS thrust, plus the orbit it ends on (RCS that fights itself translates the
    /// vessel). Segments (comma-separated, default x1,tc8,tc12,btw8,btw12): x1 = no warp; tcN = TimeControl hyper-warp at
    /// N (physics accuracy 1: the 1x step, several per frame); btwN = physics warp at N, as BetterTimeWarp's physics
    /// presets make it (its non-lossless mode only swaps the physics warp table: the step grows, one per frame). Each
    /// segment is flown once per patch state (patch, default on,off), switched through RcsInputPerStepAddon.Enabled, so
    /// both run in the same session; patch=asis flies each once and leaves the switch alone (a fix built into another
    /// mod, on or off by its settings). With the plugin's RcsTiming counters, the time in ModuleRCS.FixedUpdate and
    /// Update; with profileLog=on on a development player, each segment recorded to profile_&lt;label&gt;.raw. Writes
    /// orbithold.csv (one row per segment) and orbithold_steps.csv (every step, thinned to stepEvery).
    /// </summary>
    public static class RcsHold
    {
        public static IEnumerator Run(TestContext ctx)
        {
            var v = FlightGlobals.ActiveVessel;
            double settleSec = ctx.ParamDouble("settleSec", 20), segGameSec = ctx.ParamDouble("segGameSec", 600), segMaxRealSec = ctx.ParamDouble("segMaxRealSec", 150);
            string[] segments = (ctx.Param("segments", "x1,tc8,tc12,btw8,btw12") ?? "").Split(',');
            string[] patchStates = (ctx.Param("patch", "on,off") ?? "").Split(',');
            int stepEvery = (int)ctx.ParamDouble("stepEvery", 5);

            // hold=sas: stock SAS instead (sasMode, default Prograde; StabilityAssist if the vessel cannot do it), MechJeb
            // released. Its error is measured against orbital prograde (errProg columns) whatever the mode.
            bool sas = ctx.Param("hold", "mechjeb").Equals("sas", StringComparison.OrdinalIgnoreCase);
            string target;
            if (sas)
            {
                Mj.SmartAssOff();
                var sasMode = (VesselAutopilot.AutopilotMode)Enum.Parse(typeof(VesselAutopilot.AutopilotMode), ctx.Param("sasMode", "Prograde"), true);
                v.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
                if (!v.Autopilot.CanSetMode(sasMode))
                    sasMode = VesselAutopilot.AutopilotMode.StabilityAssist;
                v.Autopilot.SetMode(sasMode);
                target = "SAS " + sasMode;
            }
            else
            {
                target = ctx.Param("target", "PROGRADE").ToUpperInvariant();
                Mj.SmartAss((MechJebModuleSmartASS.Target)Enum.Parse(typeof(MechJebModuleSmartASS.Target), target));
            }
            ctx.Record("target", target);
            ctx.Record("massT", Math.Round(v.GetTotalMass(), 3));
            yield return Wait.Seconds(settleSec);

            var patchField = PatchSwitch();
            ctx.Record("patchPresent", patchField != null);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var summary = new StringBuilder("segment,patch,warp,rate,achievedRate,gameSec,realSec,steps,frames,stepsPerFrame,fixedDt,"
                + "errMeanDeg,errRmsDeg,errMaxDeg,errOver5Pct,rateMeanDegS,rateMaxDegS,ctrlMean,rcsMeanKN,rcsImpulseKNs,rcsFiringPct,"
                + "peStartKm,peEndKm,apStartKm,apEndKm,smaChangeM,errProgMeanDeg,errProgMaxDeg,"
                + "frameMs,rcsFixedCallsPerFrame,rcsFixedUsPerCall,rcsFixedMsPerFrame,rcsUpdateCallsPerFrame,rcsUpdateUsPerCall,rcsUpdateMsPerFrame\n");
            // The plugin's RcsTiming counters (time in ModuleRCS.FixedUpdate, the patch included, and in Update), if it has them.
            Type timing = null;
            foreach (var a in AssemblyLoader.loadedAssemblies)
                timing = timing ?? a.assembly.GetType("RcsInputPerStep.RcsTiming", false);
            ctx.Record("rcsTimingPresent", timing != null);
            bool profileLog = ctx.ParamBool("profileLog", false) && UnityEngine.Profiling.Profiler.supported;
            ctx.Record("profilerSupported", UnityEngine.Profiling.Profiler.supported);
            ctx.Record("profileLog", profileLog);
            Directory.CreateDirectory(ctx.ResultsDir);
            Func<string, double> timingValue = name => timing == null ? double.NaN : Convert.ToDouble(timing.GetField(name).GetValue(null));
            var steps = new StringBuilder("segment,patch,step,frame,ut,fixedDt,errDeg,rateDegS,pitch,yaw,roll,rcsKN,errProgDeg\n");
            var samplerObject = new UnityEngine.GameObject("KspTest.OrbitHoldSampler");
            var sampler = samplerObject.AddComponent<RcsHoldSampler>();
            sampler.Steps = steps;
            sampler.StepEvery = Math.Max(1, stepEvery);

            foreach (string seg in segments)
            {
                string mode = seg.StartsWith("tc") ? "tc" : seg.StartsWith("btw") ? "btw" : "x1";
                double rate = mode == "x1" ? 1 : double.Parse(seg.Substring(mode.Length), inv);
                foreach (string patch in patchStates)
                {
                    bool asis = patch.Trim().Equals("asis", StringComparison.OrdinalIgnoreCase);
                    bool on = patch.Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
                    string state = asis ? "asis" : on ? "on" : "off";
                    string label = $"{seg}.{state}";
                    ctx.Step($"Hold {target}: {seg}, patch {state}");
                    if (asis)
                    {
                    }
                    else if (patchField != null)
                        patchField.SetValue(null, on);
                    else if (on)
                    {
                        ctx.Record(label + ".skipped", "RcsInputPerStep not installed");
                        continue;
                    }
                    yield return Wait.Seconds(settleSec);
                    v = FlightGlobals.ActiveVessel;
                    Orbit o0 = v.orbit;
                    double pe0 = o0.PeA, ap0 = o0.ApA, sma0 = o0.semiMajorAxis;
                    if (mode == "tc")
                        Assert.True(Flight.HyperWarp(rate, 1.0), "TimeControl hyper-warp on");
                    else if (mode == "btw")
                        Flight.PhysicsWarpRate(rate);
                    yield return null;
                    sampler.Begin(label);
                    timing?.GetMethod("Reset").Invoke(null, null);
                    timing?.GetField("Enabled").SetValue(null, true);
                    if (profileLog)
                    {
                        UnityEngine.Profiling.Profiler.logFile = Path.Combine(ctx.ResultsDir, $"profile_{label}");
                        UnityEngine.Profiling.Profiler.enableBinaryLog = true;
                        UnityEngine.Profiling.Profiler.enabled = true;
                    }
                    float real0 = UnityEngine.Time.realtimeSinceStartup;
                    double ut0 = Planetarium.GetUniversalTime();
                    while (Planetarium.GetUniversalTime() - ut0 < segGameSec && UnityEngine.Time.realtimeSinceStartup - real0 < segMaxRealSec)
                    {
                        Flight.AssertAlive();
                        yield return null;
                    }
                    sampler.End();
                    timing?.GetField("Enabled").SetValue(null, false);
                    if (profileLog)
                    {
                        UnityEngine.Profiling.Profiler.enabled = false;
                        UnityEngine.Profiling.Profiler.enableBinaryLog = false;
                        UnityEngine.Profiling.Profiler.logFile = "";
                    }
                    double tickUs = 1e6 / System.Diagnostics.Stopwatch.Frequency;
                    double fixedCalls = timingValue("FixedCalls"), updateCalls = timingValue("UpdateCalls");
                    double fixedUs = timingValue("FixedTicks") * tickUs, updateUs = timingValue("UpdateTicks") * tickUs;
                    double gameSec = Planetarium.GetUniversalTime() - ut0, realSec = UnityEngine.Time.realtimeSinceStartup - real0;
                    float fixedDt = UnityEngine.Time.fixedDeltaTime;
                    if (mode == "tc")
                        Flight.HyperWarpOff();
                    else if (mode == "btw")
                        Flight.WarpOff();
                    yield return Wait.Until(() => TimeWarp.CurrentRate <= 1.01 && UnityEngine.Time.timeScale <= 1.01, 60, "back to 1x", shot: false);
                    v = FlightGlobals.ActiveVessel;
                    Orbit o1 = v.orbit;
                    var s = sampler;
                    double T = s.GameTime > 0 ? s.GameTime : double.NaN;
                    double[] row =
                    {
                        rate, gameSec / Math.Max(realSec, 1e-6), gameSec, realSec, s.StepCount, s.FrameCount, s.StepCount / Math.Max(1.0, s.FrameCount), fixedDt,
                        s.ErrInt / T, Math.Sqrt(s.Err2Int / T), s.ErrMax, 100 * s.ErrOver5 / T, s.RateInt / T, s.RateMax, s.CtrlInt / T,
                        s.RcsImpulse / T, s.RcsImpulse, 100 * s.RcsFiring / T,
                        pe0 / 1000, o1.PeA / 1000, ap0 / 1000, o1.ApA / 1000, o1.semiMajorAxis - sma0,
                        s.ProgInt / T, s.ProgMax,
                        1000 * realSec / Math.Max(1.0, s.FrameCount),
                        fixedCalls / Math.Max(1.0, s.FrameCount), fixedUs / Math.Max(1.0, fixedCalls), fixedUs / 1000 / Math.Max(1.0, s.FrameCount),
                        updateCalls / Math.Max(1.0, s.FrameCount), updateUs / Math.Max(1.0, updateCalls), updateUs / 1000 / Math.Max(1.0, s.FrameCount),
                    };
                    summary.Append(seg).Append(',').Append(state).Append(',').Append(mode);
                    foreach (double x in row)
                        summary.Append(',').Append(x.ToString("G6", inv));
                    summary.Append('\n');
                    File.WriteAllText(Path.Combine(ctx.ResultsDir, "orbithold.csv"), summary.ToString());
                    File.WriteAllText(Path.Combine(ctx.ResultsDir, "orbithold_steps.csv"), steps.ToString());
                    ctx.Record(label + ".achievedRate", Math.Round(row[1], 2));
                    ctx.Record(label + ".stepsPerFrame", Math.Round(row[6], 2));
                    ctx.Record(label + ".errMeanDeg", Math.Round(row[8], 3));
                    ctx.Record(label + ".errMaxDeg", Math.Round(row[10], 2));
                    ctx.Record(label + ".errProgMeanDeg", Math.Round(row[23], 3));
                    ctx.Record(label + ".errProgMaxDeg", Math.Round(row[24], 2));
                    ctx.Record(label + ".rcsMeanKN", Math.Round(row[15], 3));
                    ctx.Record(label + ".smaChangeM", Math.Round(row[22], 1));
                    ctx.Record(label + ".frameMs", Math.Round(row[25], 2));
                    ctx.Record(label + ".rcsFixedUsPerCall", Math.Round(row[27], 3));
                    ctx.Record(label + ".rcsFixedMsPerFrame", Math.Round(row[28], 3));
                    ctx.Record(label + ".rcsUpdateUsPerCall", Math.Round(row[30], 3));
                }
            }
            UnityEngine.Object.Destroy(samplerObject);
            if (patchField != null)
                patchField.SetValue(null, true);
            if (sas)
                FlightGlobals.ActiveVessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            else
                Mj.SmartAssOff();
        }

        /// <summary> RcsInputPerStepAddon.Enabled (the test plugin's switch), or null without the plugin. </summary>
        public static System.Reflection.FieldInfo PatchSwitch()
        {
            foreach (var a in AssemblyLoader.loadedAssemblies)
            {
                var t = a.assembly.GetType("RcsInputPerStep.RcsInputPerStepAddon", false);
                var f = t?.GetField("Enabled", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (f != null)
                    return f;
            }
            return null;
        }
    }

    /// <summary>
    /// Every physics step while a segment runs: attitude error, angular rate, inputs and RCS thrust, integrated over game
    /// time; rendered frames are counted to give steps per frame.
    /// </summary>
    public sealed class RcsHoldSampler : UnityEngine.MonoBehaviour
    {
        public StringBuilder Steps;
        public int StepEvery = 5;
        public double GameTime, ErrInt, Err2Int, ErrMax, ErrOver5, RateInt, RateMax, CtrlInt, RcsImpulse, RcsFiring, ProgInt, ProgMax;
        public int StepCount, FrameCount;
        private string label;
        private bool active;

        public void Begin(string segLabel)
        {
            label = segLabel;
            GameTime = ErrInt = Err2Int = ErrMax = ErrOver5 = RateInt = RateMax = CtrlInt = RcsImpulse = RcsFiring = ProgInt = ProgMax = 0;
            StepCount = FrameCount = 0;
            active = true;
        }

        public void End() => active = false;

        private void Update()
        {
            if (active)
                ++FrameCount;
        }

        private void FixedUpdate()
        {
            if (!active)
                return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.packed)
                return;
            MechJebCore core = v.GetMasterMechJeb();
            double dt = UnityEngine.Time.fixedDeltaTime;
            // MechJeb's error only while SmartASS is holding; the nose's angle off orbital prograde always
            double err = core != null && core.Attitude.Enabled ? core.Attitude.attitudeError : double.NaN;
            double prog = Vector3d.Angle(v.ReferenceTransform.up, v.obt_velocity);
            ProgInt += prog * dt;
            ProgMax = Math.Max(ProgMax, prog);
            double rateDeg = v.angularVelocityD.magnitude * 180 / Math.PI;
            double rcs = 0;
            foreach (Part p in v.parts)
                foreach (ModuleRCS m in p.FindModulesImplementing<ModuleRCS>())
                    if (m.thrustForces != null)
                        foreach (float f in m.thrustForces)
                            rcs += f;
            FlightCtrlState s = v.ctrlState;
            double ctrl = Math.Abs(s.pitch) + Math.Abs(s.yaw) + Math.Abs(s.roll);
            GameTime += dt;
            if (!double.IsNaN(err))
            {
                ErrInt += err * dt;
                Err2Int += err * err * dt;
                ErrMax = Math.Max(ErrMax, err);
                if (err > 5) ErrOver5 += dt;
            }
            RateInt += rateDeg * dt;
            RateMax = Math.Max(RateMax, rateDeg);
            CtrlInt += ctrl * dt;
            RcsImpulse += rcs * dt;
            if (rcs > 0.01) RcsFiring += dt;
            if (StepCount++ % StepEvery == 0 && Steps != null)
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                string[] seg = label.Split('.');
                Steps.Append(seg[0]).Append(',').Append(seg[1]).Append(',').Append(StepCount - 1).Append(',').Append(UnityEngine.Time.frameCount).Append(',')
                     .Append(Planetarium.GetUniversalTime().ToString("F3", inv)).Append(',').Append(dt.ToString("F3", inv)).Append(',')
                     .Append(err.ToString("F3", inv)).Append(',').Append(rateDeg.ToString("F4", inv)).Append(',')
                     .Append(s.pitch.ToString("F3", inv)).Append(',').Append(s.yaw.ToString("F3", inv)).Append(',').Append(s.roll.ToString("F3", inv)).Append(',')
                     .Append(rcs.ToString("F3", inv)).Append(',').Append(prog.ToString("F4", inv)).Append('\n');
            }
        }
    }
}
