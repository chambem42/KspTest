using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using KspTest;
using UnityEngine;

namespace KspTest.Tests.Stock
{
    /// <summary>FAR's prediction API against FAR in flight, the flight taken as the gold standard (see FarFlightProbe).</summary>
    public static class FarFlightTests
    {
        private static readonly FarFlightProbe.Condition[] NoiseFloorConditions =
        {
            new FarFlightProbe.Condition { Name = "subsonic", Mach = 0.5, AltM = 3000, AoADeg = 5 },
            new FarFlightProbe.Condition { Name = "transonic", Mach = 0.95, AltM = 8000, AoADeg = 4 },
            new FarFlightProbe.Condition { Name = "supersonic", Mach = 2.0, AltM = 14000, AoADeg = 5 },
            new FarFlightProbe.Condition { Name = "highAoA", Mach = 1.5, AltM = 12000, AoADeg = 25 },
            new FarFlightProbe.Condition { Name = "mach4", Mach = 4.0, AltM = 22000, AoADeg = 8 },
            new FarFlightProbe.Condition { Name = "hypersonic", Mach = 7.0, AltM = 38000, AoADeg = 20 },
        };

        /// <summary>
        /// How close FAR's prediction API gets to FAR in flight when both see the same state, per condition and per
        /// component: the noise floor that tolerances for the per-fix tests are set from. The Aeris 4A (wings,
        /// intakes, elevons) is put flying at each condition (over the ocean east of KSC, heading east), held at its
        /// angle of attack with no rotation and its control surfaces locked flat, and for `frames` physics frames
        /// after `settle` the applied force and torque are compared with the prediction for the state the vessel
        /// measured. Writes noise_floor.md (summary) and noise_floor.csv (every frame). Params: frames (50),
        /// settle (25), only (condition names, '+'-separated).
        /// </summary>
        [UITest("Far_FlightVsApi_NoiseFloor", StartsAt = StartState.Flight, Craft = "Aeris 4A", CraftDir = "SPH", LaunchSite = "Runway", TimeoutSec = 1200)]
        public static IEnumerator Far_FlightVsApi_NoiseFloor(TestContext ctx)
        {
            yield return Wait.Seconds(5);   // FAR voxelizes the craft after load
            ctx.Record("predictionApi", FarApi.HasPredictAt ? "CalculateVesselAeroForcesAt" : "CalculateVesselAeroForces (altitude only)");
            var inv = CultureInfo.InvariantCulture;
            int frames = (int)ctx.ParamDouble("frames", 50), settle = (int)ctx.ParamDouble("settle", 25);
            string[] only = ctx.Param("only")?.Split('+');
            FarFlightProbe.Condition[] conditions = NoiseFloorConditions
                .Where(c => only == null || only.Any(o => o.Trim().Equals(c.Name, StringComparison.OrdinalIgnoreCase))).ToArray();
            Assert.True(conditions.Length > 0, "a condition to run");
            foreach (var c in conditions) { c.Lat = 0; c.Lon = -60; }

            Flight.IgnoreMaxTemperature(true);
            Ctrl.SetThrottle(0f);
            Vessel v = FlightGlobals.ActiveVessel;
            v.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            ctx.Record("surfacesLocked", FarApi.SetMaxDeflect(v, 0));

            // The gear comes up in the air (on the runway it would drop the plane on its belly), and FAR revoxelizes
            // the changed shape before anything is measured.
            ctx.Step("Up in the air, gear up");
            yield return FarFlightProbe.Place(conditions[0]);
            ctx.Record("gearRetracted", Ctrl.RetractGear());
            yield return Wait.Seconds(8);
            v = FlightGlobals.ActiveVessel;
            double length = VesselLength(v);
            ctx.Record("vesselLengthM", Math.Round(length, 2));
            ctx.Record("massT", Math.Round(v.GetTotalMass(), 3));

            var md = new StringBuilder();
            md.Append("# FAR prediction API vs flight: noise floor\n\n");
            md.Append($"Vessel {v.vesselName}, {v.parts.Count} parts, length {length:F1} m; {frames} frames per condition after {settle} to settle; control surfaces locked flat.\n\n");
            md.Append("Force components in kN, moments in kN m. flight: mean ± frame-to-frame SD of what FAR applied. ");
            md.Append("pred - flight: mean ± SD of the prediction's error at the same state. %: the mean error over the mean total force ");
            md.Append("(moments: over total force × vessel length).\n\n");
            var csv = new StringBuilder();
            foreach (var c in conditions)
            {
                ctx.Step(c.ToString());
                yield return FarFlightProbe.Place(c);
                var samples = new List<FarFlightProbe.Sample>();
                yield return FarFlightProbe.Hold(c, settle, frames, samples);
                Assert.True(samples.Count > frames / 2, $"{c.Name}: samples taken ({samples.Count})");
                string rows = FarFlightProbe.Csv(c, samples);
                csv.Append(csv.Length == 0 ? rows : rows.Substring(rows.IndexOf('\n') + 1));

                FarFlightProbe.Stats(samples.Select(s => s.Mach), out double mach, out _);
                FarFlightProbe.Stats(samples.Select(s => s.AoADeg), out double aoa, out double aoaSd);
                FarFlightProbe.Stats(samples.Select(s => s.QkPa), out double q, out _);
                FarFlightProbe.Stats(samples.Select(s => s.ForceKN), out double force, out _);
                FarFlightProbe.Stats(samples.Select(s => s.AltM), out double alt, out _);
                md.Append($"## {c.Name}\n\nMach {mach:F3}, AoA {aoa:F2} ± {aoaSd:F3} deg, {alt / 1000:F2} km, q {q:F2} kPa, |F| {force:F2} kN, {samples.Count} frames\n\n");
                md.Append("| component | flight | pred - flight | % |\n|---|---|---|---|\n");
                for (int k = 0; k < FarFlightProbe.ComponentNames.Length; ++k)
                {
                    int kk = k;
                    FarFlightProbe.Stats(samples.Select(s => s.Flight[kk]), out double fm, out double fsd);
                    FarFlightProbe.Stats(samples.Select(s => s.Predicted[kk] - s.Flight[kk]), out double em, out double esd);
                    double scale = k < 3 ? force : force * length;
                    double pct = scale > 0 ? 100 * em / scale : double.NaN;
                    md.Append($"| {FarFlightProbe.ComponentNames[k]} | {fm.ToString("G4", inv)} ± {fsd.ToString("G2", inv)} | ");
                    md.Append($"{em.ToString("+0.####;-0.####", inv)} ± {esd.ToString("G2", inv)} | {pct.ToString("+0.00;-0.00", inv)} |\n");
                    ctx.Record($"{c.Name}.{FarFlightProbe.ComponentNames[k]}ErrPct", Math.Round(pct, 3));
                }
                md.Append('\n');
                ctx.Record($"{c.Name}.mach", Math.Round(mach, 3));
                ctx.Record($"{c.Name}.aoaDeg", Math.Round(aoa, 2));
                ctx.Record($"{c.Name}.forceKN", Math.Round(force, 3));
            }
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "noise_floor.md"), md.ToString());
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "noise_floor.csv"), csv.ToString());
        }

        /// <summary>
        /// Whether putting the vessel at a condition (FarFlightProbe.Place) measures what flying there does. The Aeris 4A
        /// takes off and flies each point on AtmosphereAutopilot's cruise (course 90, the altitude, the speed from the
        /// Mach) until steady, then at that point: A, free flight, AA flying and the surfaces moving; B, AA off, A's
        /// mean inputs held (so the surfaces sit where A had them; surfaces=flat locks them neutral) and the attitude
        /// pinned at A's angle of attack, in place; C, the vessel placed at the state A measured (its airspeed) and
        /// held as B; D, placed at A's place, speed and heading and trimmed there from a neutral start
        /// (FarFlightProbe.Trim; trim, trimAoA0, trimPitch0, trimThrottle0), its trim recorded against A's. The same
        /// comparison (prediction at the measured state against what FAR applied) in all four; if C and D agree with A
        /// and B, placement and trimming stand in for flying. B, C and D hold the attitude as
        /// FarFlightProbe.HoldMode says (param hold: torque, steer or teleport). Trajectories is paused. Writes
        /// flown_vs_placed.md and .csv. Params: craft, points ("3000:0.5+8000:0.95", altitude m : Mach), frames (50),
        /// settle (25), steadySec (15), hold (torque), airStart (false: take off from the runway; true: placed flying
        /// level at the first point with the gear up and handed to AA, for craft the takeoff does not suit - A is still
        /// taken only after AA has held the point steady for steadySec). flight_trace.csv records every physics frame
        /// from takeoff (FlightTrace); trim_sweep.csv is FAR's prediction on the runway before takeoff, surfaces
        /// neutral and gear down, over AoA at a few speeds at 500 m.
        /// </summary>
        [UITest("Far_FlownVsPlaced", StartsAt = StartState.Flight, Craft = "Aeris 4A", CraftDir = "SPH", LaunchSite = "Runway", TimeoutSec = 2400)]
        public static IEnumerator Far_FlownVsPlaced(TestContext ctx)
        {
            Assert.True(Aa.Present, "AtmosphereAutopilot is installed");
            var inv = CultureInfo.InvariantCulture;
            int frames = (int)ctx.ParamDouble("frames", 50), settle = (int)ctx.ParamDouble("settle", 25);
            double steadySec = ctx.ParamDouble("steadySec", 15);
            ctx.Record("predictionApi", FarApi.HasPredictAt ? "CalculateVesselAeroForcesAt" : "CalculateVesselAeroForces (altitude only)");
            Flight.IgnoreMaxTemperature(true);
            bool[] trajectories = FarFlightProbe.QuietOtherPredictors();
            FarFlightProbe.HoldMode = ctx.Param("hold", "torque");
            ctx.Record("holdMode", FarFlightProbe.HoldMode);
            yield return Wait.Seconds(5);
            if (FlightGlobals.ActiveVessel.vesselName == "Stearwing A300" && ctx.Param("surfaceFix", "true") == "true")
                StearwingSurfaces(ctx, FlightGlobals.ActiveVessel);
            TrimSweep(ctx, FlightGlobals.ActiveVessel);
            FlightTrace.Phase = "start";
            FlightTrace.Begin(Path.Combine(ctx.ResultsDir, "flight_trace.csv"));

            if (ctx.Param("airStart", "false") == "true")
            {
                ctx.Step("Air start at the first point");
                string[] first = (ctx.Param("points", "3000:0.5+8000:0.95") ?? "").Split('+')[0].Split(':');
                var start = new FarFlightProbe.Condition
                {
                    Name = "air start", AltM = double.Parse(first[0], inv), Mach = double.Parse(first[1], inv), AoADeg = 3, Lat = 0, Lon = -60,
                };
                Ctrl.Sas(false);
                Ctrl.Stage();
                yield return FarFlightProbe.Place(start);
                Flight.PointAtAoA(start.AoADeg);
                Ctrl.RetractGear();
                Ctrl.SetThrottle(1f);
            }
            else
            {
                // AA does not fly a vessel on the ground: at rotate speed, MechJeb's SmartASS (when installed, else the
                // stick) holds heading 90 and rotatePitchDeg up until clearly airborne and climbing, then AA's cruise
                // to the first point takes over.
                ctx.Step("Take off");
                Ctrl.Attach();
                Ctrl.Sas(false);
                Ctrl.Brakes(false);
                Ctrl.SetThrottle(1f);
                Ctrl.Stage();
                FlightTrace.Phase = "roll";
                double rotate = ctx.ParamDouble("rotateMs", 90);
                yield return Wait.Until(() => { Flight.AssertAlive(); return Flight.SurfaceSpeed > rotate; }, 120, "rotate speed");
                Type mj = null;
                foreach (var la in AssemblyLoader.loadedAssemblies)
                    if (la.assembly.GetName().Name == "KspTest.MechJeb")
                        mj = la.assembly.GetType("KspTest.MechJeb.Mj");
                bool smartAss = mj != null && ctx.Param("smartass", "true") != "false";
                ctx.Record("takeoffHold", smartAss ? "SmartASS" : "stick");
                FlightTrace.Phase = smartAss ? "smartass" : "stick";
                if (smartAss)
                    mj.GetMethod("SmartAssSurface").Invoke(null, new object[] { 90.0, ctx.ParamDouble("rotatePitchDeg", 10), 0.0 });
                else
                    Ctrl.Set(pitch: (float)ctx.ParamDouble("rotatePitch", 0.3));
                double startAlt = Flight.Altitude;
                yield return Wait.Until(() => { Flight.AssertAlive(); return Flight.Altitude - startAlt > 25 && FlightGlobals.ActiveVessel.verticalSpeed > 1; }, 60, "airborne and climbing");
                string[] first = (ctx.Param("points", "3000:0.5+8000:0.95") ?? "").Split('+')[0].Split(':');
                double firstAlt = double.Parse(first[0], inv), firstMach = double.Parse(first[1], inv);
                CelestialBody b0 = FlightGlobals.ActiveVessel.mainBody;
                double p0 = b0.GetPressure(firstAlt);
                if (smartAss)
                    mj.GetMethod("SmartAssOff").Invoke(null, null);
                Aa.Cruise(FlightGlobals.ActiveVessel, firstAlt, firstMach * b0.GetSpeedOfSound(p0, b0.GetDensity(p0, b0.GetTemperature(firstAlt))), 90);
                Ctrl.Set(pitch: 0f);
                Ctrl.Detach();
                FlightTrace.Phase = "aa";
                int whole = FlightGlobals.ActiveVessel.parts.Count;
                yield return Wait.Until(() => { AssertWhole(whole); return Flight.Altitude - startAlt > 150; }, 120, "150 m up");
                Ctrl.Gear(false);
                FlightTrace.Phase = "aa-gearup";
            }
            int partCount = FlightGlobals.ActiveVessel.parts.Count;
            ctx.Record("parts", partCount);

            var md = new StringBuilder("# Flown vs placed: FAR prediction API against flight\n\n");
            md.Append("Mean (pred - flight) as % of the mean total force (moments: of total force × vessel length), ± its SD over the frames. ");
            md.Append("A: free flight on AtmosphereAutopilot. B: AA off, surfaces locked, attitude pinned at A's AoA, in place. ");
            md.Append("C: placed at A's measured state, held as B. flight SD: FAR's own frame-to-frame spread in B, same units.\n\n");
            var csv = new StringBuilder();
            foreach (string point in (ctx.Param("points", "3000:0.5+8000:0.95") ?? "").Split('+'))
            {
                string[] pm = point.Split(':');
                double altM = double.Parse(pm[0], inv), mach = double.Parse(pm[1], inv);
                Vessel v = FlightGlobals.ActiveVessel;
                CelestialBody body = v.mainBody;
                double p = body.GetPressure(altM);
                double speed = mach * body.GetSpeedOfSound(p, body.GetDensity(p, body.GetTemperature(altM)));
                string name = $"{altM / 1000:0.#}km-M{mach:0.##}";

                ctx.Step($"Fly to {name} on AtmosphereAutopilot cruise");
                Aa.Cruise(v, altM, speed, 90);
                double steadySince = double.NaN;
                yield return Wait.Until(() =>
                {
                    AssertWhole(partCount);
                    Vessel w = FlightGlobals.ActiveVessel;
                    bool steady = Math.Abs(w.altitude - altM) < 60 && Math.Abs(w.srfSpeed - speed) < 4 && Math.Abs(w.verticalSpeed) < 3;
                    double now = Planetarium.GetUniversalTime();
                    if (!steady) steadySince = double.NaN;
                    else if (double.IsNaN(steadySince)) steadySince = now;
                    return !double.IsNaN(steadySince) && now - steadySince >= steadySec;
                }, 900, $"steady at {name}", shot: false);

                Assert.True(FlightGlobals.ActiveVessel.parts.Count == partCount, $"{name}: vessel whole ({FlightGlobals.ActiveVessel.parts.Count} of {partCount} parts)");
                FlightTrace.End();
                ctx.Step($"{name}: A free flight");
                var cond = new FarFlightProbe.Condition { Name = name, Mach = mach, AltM = altM };
                var a = new List<FarFlightProbe.Sample>();
                yield return FarFlightProbe.Hold(cond, 0, frames, a, pin: false);
                v = FlightGlobals.ActiveVessel;
                FarFlightProbe.Stats(a.Select(s => s.AoADeg), out double aoa, out _);
                cond.AoADeg = aoa;
                cond.Mach = v.mach;
                cond.AltM = v.altitude;
                cond.Lat = v.latitude;
                cond.Lon = v.longitude;
                cond.HeadingDeg = HeadingDeg(v);
                cond.SpeedMs = Mean(a, s => s.Airspeed);   // the airspeed A flew, not a Mach through stock's base temperature
                // the throttle the engines ran at, not AA's mean command (they lag it)
                double throttle = a.All(s => !double.IsNaN(s.EngineThrottle)) ? Mean(a, s => s.EngineThrottle) : Mean(a, s => s.Throttle), inPitch = Mean(a, s => s.CtrlPitch), inYaw = Mean(a, s => s.CtrlYaw), inRoll = Mean(a, s => s.CtrlRoll);
                ctx.Record($"{name}.A.inputs", string.Format(inv, "pitch {0:+0.000;-0.000}, yaw {1:+0.000;-0.000}, roll {2:+0.000;-0.000}, throttle {3:F3}", inPitch, inYaw, inRoll, throttle));

                // B and C fly on A's mean inputs, so the surfaces sit where A had them; surfaces=flat locks them
                // neutral instead, as the probe first did.
                bool flat = ctx.Param("surfaces", "held") == "flat";
                ctx.Step($"{name}: B pinned in place");
                Aa.Off(v);
                Ctrl.Attach();
                Ctrl.Set(pitch: flat ? 0f : (float)inPitch, yaw: flat ? 0f : (float)inYaw, roll: flat ? 0f : (float)inRoll);
                Ctrl.SetThrottle((float)throttle);
                var saved = FarApi.SaveMaxDeflects(v);
                if (flat) FarApi.SetMaxDeflect(v, 0);
                var b = new List<FarFlightProbe.Sample>();
                yield return FarFlightProbe.Hold(cond, settle, frames, b);

                ctx.Step($"{name}: C placed");
                yield return FarFlightProbe.Place(cond);
                var c = new List<FarFlightProbe.Sample>();
                yield return FarFlightProbe.Hold(cond, settle, frames, c);
                FarApi.RestoreMaxDeflects(FlightGlobals.ActiveVessel, saved);

                // D: placed at A's place, speed and heading and trimmed there from a neutral start (trimAoA0,
                // trimPitch0, trimThrottle0), knowing nothing of A's attitude or inputs - how a condition nobody flew
                // gets its state. Its trim should land on A's.
                var d = new List<FarFlightProbe.Sample>();
                var trim = new FarFlightProbe.TrimResult();
                var dc = new FarFlightProbe.Condition
                {
                    Name = name + " trimmed", Mach = cond.Mach, AltM = cond.AltM, SpeedMs = cond.SpeedMs, Lat = cond.Lat, Lon = cond.Lon,
                    HeadingDeg = cond.HeadingDeg, AoADeg = ctx.ParamDouble("trimAoA0", 2),
                };
                if (ctx.Param("trim", "true") == "true")
                {
                    ctx.Step($"{name}: D placed and trimmed");
                    yield return FarFlightProbe.Trim(dc, ctx.ParamDouble("trimPitch0", 0), ctx.ParamDouble("trimThrottle0", 0.5), trim);
                    foreach (string line in trim.Steps)
                        ctx.Record($"{name}.D.trim", line);
                    ctx.Record($"{name}.D.vsA", string.Format(inv,
                        "{0} after {1}: AoA {2:F2} vs A {3:F2}, pitch {4:+0.000;-0.000} vs A {5:+0.000;-0.000}, throttle {6:F3} vs A {7:F3}",
                        trim.Converged ? "converged" : "NOT converged", trim.Iterations, trim.AoADeg, aoa, trim.Pitch, inPitch, trim.Throttle, throttle));
                    dc.AoADeg = trim.AoACommanded;
                    Ctrl.Set(pitch: (float)trim.Pitch, yaw: 0f, roll: 0f);
                    Ctrl.SetThrottle((float)trim.Throttle);
                    yield return FarFlightProbe.Place(dc);
                    yield return FarFlightProbe.Hold(dc, settle, frames, d);
                }
                Ctrl.Detach();   // the next point is AA's again

                double length = VesselLength(FlightGlobals.ActiveVessel);
                md.Append($"## {name}\n\nA: Mach {Mean(a, s => s.Mach):F3}, AoA {aoa:F2} deg, {Mean(a, s => s.AltM) / 1000:F2} km, |F| {Mean(a, s => s.ForceKN):F1} kN, throttle {throttle:F2}. ");
                md.Append($"B: Mach {Mean(b, s => s.Mach):F3}, AoA {Mean(b, s => s.AoADeg):F2}. C: Mach {Mean(c, s => s.Mach):F3}, AoA {Mean(c, s => s.AoADeg):F2}, {Mean(c, s => s.AltM) / 1000:F2} km. ");
                md.Append(flat ? "B and C: surfaces locked flat. " : string.Format(inv, "B and C on A's inputs: pitch {0:+0.000;-0.000}, yaw {1:+0.000;-0.000}, roll {2:+0.000;-0.000}. ", inPitch, inYaw, inRoll));
                if (d.Count > 0)
                    md.Append(string.Format(inv, "D: trimmed ({0} iterations{1}) to AoA {2:F2}, pitch {3:+0.000;-0.000}, throttle {4:F3}; Mach {5:F3}, {6:F2} km.",
                                            trim.Iterations, trim.Converged ? "" : ", not converged", Mean(d, s => s.AoADeg), trim.Pitch, trim.Throttle, Mean(d, s => s.Mach), Mean(d, s => s.AltM) / 1000));
                md.Append("\n\n| component | A free | B pinned | C placed | D trimmed | flight SD |\n|---|---|---|---|---|---|\n");
                for (int k = 0; k < FarFlightProbe.ComponentNames.Length; ++k)
                {
                    string dk = d.Count > 0 ? ErrPct(d, k, length) : "-";
                    md.Append($"| {FarFlightProbe.ComponentNames[k]} | {ErrPct(a, k, length)} | {ErrPct(b, k, length)} | {ErrPct(c, k, length)} | {dk} | {FlightSdPct(b, k, length)} |\n");
                    ctx.Record($"{name}.{FarFlightProbe.ComponentNames[k]}", $"A {ErrPct(a, k, length)}  B {ErrPct(b, k, length)}  C {ErrPct(c, k, length)}  D {dk}");
                }
                md.Append('\n');
                string[] tags = { "A", "B", "C", "D" };
                List<FarFlightProbe.Sample>[] lists = { a, b, c, d };
                for (int t = 0; t < 4; ++t)
                {
                    string rows = FarFlightProbe.Csv(new FarFlightProbe.Condition { Name = name + "-" + tags[t] }, lists[t]);
                    csv.Append(csv.Length == 0 ? rows : rows.Substring(rows.IndexOf('\n') + 1));
                }
                File.WriteAllText(Path.Combine(ctx.ResultsDir, "flown_vs_placed.md"), md.ToString());
                File.WriteAllText(Path.Combine(ctx.ResultsDir, "flown_vs_placed.csv"), csv.ToString());
            }
            Aa.Off(FlightGlobals.ActiveVessel);
            FarFlightProbe.ResumeOtherPredictors(trajectories);
        }

        /// <summary>
        /// The Stearwing A300 is built for stock aero, every surface on 100 % pitch, yaw and roll; under FAR its wing
        /// elevons trim it by dumping lift. Set it up as a FAR pilot would: the outermost surface on each wing
        /// ailerons only (roll 100), the Big-S tail fin rudder only (yaw 100), the FAT-455 tail fins elevators only
        /// (pitch 100, max deflection 40), every other surface (flaps and spoilers to be) off.
        /// </summary>
        private static void StearwingSurfaces(TestContext ctx, Vessel v)
        {
            Vector3 right = v.ReferenceTransform.right, com = v.CoM;
            var surfaces = v.parts.Select(p => new { Part = p, Module = FarApi.ControlSurface(p) }).Where(s => s.Module != null).ToList();
            double outer = surfaces.Max(s => Math.Abs(Vector3.Dot(s.Part.transform.position - com, right)));
            var log = new List<string>();
            foreach (var s in surfaces)
            {
                double span = Vector3.Dot(s.Part.transform.position - com, right);
                string role;
                if (s.Part.name == "wingShuttleRudder") { FarApi.SetControlAxes(s.Module, 0, 100, 0); role = "rudder"; }
                else if (s.Part.name == "airlinerTailFin") { FarApi.SetControlAxes(s.Module, 100, 0, 0, 40); role = "elevator"; }
                else if (Math.Abs(span) > outer - 0.5) { FarApi.SetControlAxes(s.Module, 0, 0, 100); role = "aileron"; }
                else { FarApi.SetControlAxes(s.Module, 0, 0, 0); role = "off"; }
                log.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1:+0.0;-0.0} m {2}", s.Part.name, span, role));
            }
            ctx.Record("surfaces", string.Join(", ", log));
        }

        // Alive and in one piece: a crash leaves the wreck floating, which Flight.AssertAlive accepts.
        private static void AssertWhole(int parts)
        {
            Flight.AssertAlive();
            int now = FlightGlobals.ActiveVessel.parts.Count;
            if (now < parts)
            {
                FlightTrace.End();
                throw new UITestFailure($"vessel broke up ({now} of {parts} parts) at {Flight.Altitude:F0} m, {Flight.SurfaceSpeed:F0} m/s");
            }
        }

        /// <summary>
        /// FAR's prediction for the vessel as it sits (surfaces as they are, gear as it is) flying level at 500 m over
        /// its position, over AoA -4..16 at 100, 150 and 200 m/s: lift, drag and pitch moment about the CoM (nose-up
        /// positive), with the weight. Where the moment crosses zero is the trim AoA with the surfaces neutral; its
        /// slope says whether the airframe is statically stable (negative) or not. Writes trim_sweep.csv and records
        /// the trim and the AoA at which lift meets the weight for each speed.
        /// </summary>
        private static void TrimSweep(TestContext ctx, Vessel v)
        {
            var ic = CultureInfo.InvariantCulture;
            Vector3 nose = v.ReferenceTransform.up, roof = -v.ReferenceTransform.forward;
            Vector3 pitchAxis = FarApi.PitchUpAxis(v);
            double weight = v.totalMass * FlightGlobals.getGeeForceAtPosition(v.CoMD).magnitude;
            double ut = Planetarium.GetUniversalTime();
            var csv = new StringBuilder("speed,aoaDeg,liftKN,dragKN,pitchKNm,weightKN\n");
            foreach (double speed in new[] { 100.0, 150.0, 200.0 })
            {
                double prevA = double.NaN, prevM = double.NaN, prevL = double.NaN, trim = double.NaN, level = double.NaN, slope = double.NaN;
                for (double a = -4; a <= 16.001; a += 1)
                {
                    double r = a * Math.PI / 180;
                    Vector3 vel = (float)speed * ((float)Math.Cos(r) * nose - (float)Math.Sin(r) * roof);
                    if (!FarApi.PredictAtFull(v, vel, v.latitude, v.longitude, 500, ut, out Vector3 f, out Vector3 t)) return;
                    Vector3 velN = vel.normalized;
                    double drag = -Vector3.Dot(f, velN), lift = Vector3.Dot(f, Vector3.ProjectOnPlane(roof, velN).normalized);
                    double m = Vector3.Dot(t, pitchAxis);
                    csv.Append(string.Format(ic, "{0},{1},{2:F2},{3:F2},{4:F1},{5:F2}\n", speed, a, lift, drag, m, weight));
                    if (!double.IsNaN(prevM) && double.IsNaN(trim) && Math.Sign(m) != Math.Sign(prevM))
                    {
                        trim = prevA + (a - prevA) * prevM / (prevM - m);
                        slope = m - prevM;
                    }
                    if (!double.IsNaN(prevL) && double.IsNaN(level) && prevL < weight && lift >= weight)
                        level = prevA + (a - prevA) * (weight - prevL) / (lift - prevL);
                    prevA = a; prevM = m; prevL = lift;
                }
                ctx.Record($"trim.{speed:F0}ms", string.Format(ic, "trim AoA {0:F1} deg (dM/dAoA {1:F0} kN m/deg), lift = weight at AoA {2:F1} deg", trim, slope, level));
            }
            ctx.Record("trim.weightKN", weight.ToString("F1", ic));
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "trim_sweep.csv"), csv.ToString());
        }

        // Where the gold standard is flown: the same airframe condition at different places (latitude and the far side
        // of the planet change stock Kerbin's temperature) and across the Mach range.
        private static readonly FarFlightProbe.Condition[] CrossTargets =
        {
            new FarFlightProbe.Condition { Name = "subsonic", Mach = 0.5, AltM = 3000, AoADeg = 5, Lat = 0, Lon = -60 },
            new FarFlightProbe.Condition { Name = "subsonic-lat60", Mach = 0.5, AltM = 3000, AoADeg = 5, Lat = 60, Lon = -60 },
            new FarFlightProbe.Condition { Name = "subsonic-farside", Mach = 0.5, AltM = 3000, AoADeg = 5, Lat = 0, Lon = 120 },
            new FarFlightProbe.Condition { Name = "supersonic", Mach = 2.0, AltM = 14000, AoADeg = 5, Lat = 0, Lon = -60 },
            new FarFlightProbe.Condition { Name = "hypersonic", Mach = 7.0, AltM = 38000, AoADeg = 20, Lat = 0, Lon = -60 },
        };

        // The targets as a craft flies them: the listed ones, except that the subsonic three go where the craft can
        // hold level flight - the Stearwing A300 cannot below about 190 m/s (its elevators run out), so it flies them at
        // Mach 0.8, as it cruises. The names and places stay, so the tables line up.
        private static FarFlightProbe.Condition[] CrossTargetsFor(string craft) => CrossTargets.Select(c => craft == "Stearwing A300" && c.Name.StartsWith("subsonic")
            ? new FarFlightProbe.Condition { Name = c.Name, Mach = 0.8, AltM = c.AltM, AoADeg = 0, Lat = c.Lat, Lon = c.Lon, HeadingDeg = c.HeadingDeg }
            : c).ToArray();

        // Where the prediction is asked from: somewhere else, flying something else. Vacuum is where FAR's flight
        // path stops updating the wings, so they keep whatever the last prediction left. Slow-high: slow and nose-high,
        // as on an approach - where a craft that cannot fly level at that speed ends up, its wings near or past stall.
        private static readonly FarFlightProbe.Condition[] CrossVantages =
        {
            new FarFlightProbe.Condition { Name = "slow", Mach = 0.45, AltM = 1500, AoADeg = 5, Lat = -20, Lon = -30 },
            new FarFlightProbe.Condition { Name = "slow-high", Mach = 0.3, AltM = 1500, AoADeg = 20, Lat = -20, Lon = -30 },
            new FarFlightProbe.Condition { Name = "hypersonic", Mach = 5.0, AltM = 30000, AoADeg = 12, Lat = 30, Lon = 150 },
            new FarFlightProbe.Condition { Name = "vacuum", SpeedMs = 2200, AltM = 75000, AoADeg = 0, Lat = -30, Lon = 30 },
        };

        // The vantages as a craft flies them: slow is the slowest it holds level low down (the Stearwing A300 at Mach 0.7;
        // the Aeris 4A at the listed 0.45), slow-high stays below that on purpose - the edge of the envelope.
        private static FarFlightProbe.Condition[] CrossVantagesFor(string craft) => CrossVantages.Select(c => craft == "Stearwing A300" && c.Name == "slow"
            ? new FarFlightProbe.Condition { Name = c.Name, Mach = 0.7, AltM = c.AltM, AoADeg = 0, Lat = c.Lat, Lon = c.Lon, HeadingDeg = c.HeadingDeg }
            : c).ToArray();

        // How each craft flies each condition, as FarFlightProbe.Trim found it, keyed by CrossTrimKey (the craft, the
        // condition as listed, its surface setup, and how many of its wings FAR has shielded - the Stearwing's two root
        // control surfaces come out shielded or not from one run to the next, a FAR knife edge, and it flies
        // differently each way): Far_CrossCondition flies these without finding them again. A
        // condition not here (or trims=fresh) is trimmed in the run, which writes trims.tsv and trims.cs (entries for
        // this table) to its results; each run also reports how steady the flight at each target was.
        private static readonly Dictionary<string, FarFlightProbe.TrimResult> CrossTrims = new Dictionary<string, FarFlightProbe.TrimResult>
        {
            // Aeris 4A (all-fixes FAR, 20260926-154552)
            { "Aeris 4A|vacuum|M0|2200m/s|75000m|-30,30|AoA0|shielded 0", new FarFlightProbe.TrimResult { AoACommanded = 0, AoADeg = 0, Pitch = 0, Throttle = 0, EngineThrottle = double.NaN, Converged = true, Mode = "no air: inputs centered, throttle closed" } },
            { "Aeris 4A|slow-high|M0.3|0m/s|1500m|-20,-30|AoA20|shielded 0", new FarFlightProbe.TrimResult { AoACommanded = 20, AoADeg = 19.956, Pitch = 1, Throttle = 0.2355, EngineThrottle = 0.2355, Converged = true, Mode = "no level flight within AoA -10 to 25 (at about 25.6): held at AoA 20.0, -0.29 m/s2 vertical; pitch input at +1: +41 kN m left to the hold" } },
            { "Aeris 4A|slow|M0.45|0m/s|1500m|-20,-30|AoA5|shielded 0", new FarFlightProbe.TrimResult { AoACommanded = 8.648, AoADeg = 8.615, Pitch = 0.6788, Throttle = 0.1327, EngineThrottle = 0.1327, Converged = true, Mode = "level" } },
            { "Aeris 4A|hypersonic|M5|0m/s|30000m|30,150|AoA12|shielded 0", new FarFlightProbe.TrimResult { AoACommanded = 16.037, AoADeg = 16.079, Pitch = 0.5042, Throttle = 0, EngineThrottle = double.NaN, Converged = true, Mode = "no thrust: speed changing -1.46 m/s2, throttle closed" } },
            { "Aeris 4A|subsonic-lat60|M0.5|0m/s|3000m|60,-60|AoA5|shielded 0", new FarFlightProbe.TrimResult { AoACommanded = 7.614, AoADeg = 7.586, Pitch = 0.626, Throttle = 0.1186, EngineThrottle = 0.1186, Converged = true, Mode = "level" } },
            { "Aeris 4A|subsonic|M0.5|0m/s|3000m|0,-60|AoA5|shielded 0", new FarFlightProbe.TrimResult { AoACommanded = 8.937, AoADeg = 8.907, Pitch = 0.7013, Throttle = 0.1458, EngineThrottle = 0.1458, Converged = true, Mode = "level" } },
            { "Aeris 4A|subsonic-farside|M0.5|0m/s|3000m|0,120|AoA5|shielded 0", new FarFlightProbe.TrimResult { AoACommanded = 8.65, AoADeg = 8.619, Pitch = 0.6847, Throttle = 0.1398, EngineThrottle = 0.1398, Converged = true, Mode = "level" } },
            { "Aeris 4A|hypersonic|M7|0m/s|38000m|0,-60|AoA20|shielded 0", new FarFlightProbe.TrimResult { AoACommanded = 20, AoADeg = 20.041, Pitch = 0.4092, Throttle = 0, EngineThrottle = double.NaN, Converged = true, Mode = "no thrust: speed changing -1.65 m/s2, throttle closed; no level flight within AoA -10 to 25 (at about -13.2): held at AoA 20.0, +1.30 m/s2 vertical" } },
            { "Aeris 4A|supersonic|M2|0m/s|14000m|0,-60|AoA5|shielded 0", new FarFlightProbe.TrimResult { AoACommanded = 3.983, AoADeg = 3.965, Pitch = 0.4199, Throttle = 0.0897, EngineThrottle = 0.0897, Converged = true, Mode = "level" } },
            // Stearwing A300 (all-fixes FAR, 20260926-155733; hypersonic 20260926-161005)
            { "Stearwing A300|vacuum|M0|2200m/s|75000m|-30,30|AoA0|surfaces set|shielded 14", new FarFlightProbe.TrimResult { AoACommanded = 0, AoADeg = 0, Pitch = 0, Throttle = 0, EngineThrottle = double.NaN, Converged = true, Mode = "no air: inputs centered, throttle closed" } },
            { "Stearwing A300|slow-high|M0.3|0m/s|1500m|-20,-30|AoA20|surfaces set|shielded 14", new FarFlightProbe.TrimResult { AoACommanded = 20, AoADeg = 20.147, Pitch = 1, Throttle = 0.5194, EngineThrottle = 0.5194, Converged = true, Mode = "no level flight within AoA -10 to 25 (at about 37.8): held at AoA 20.0, -2.77 m/s2 vertical; pitch input at +1: +1279 kN m left to the hold" } },
            { "Stearwing A300|slow|M0.7|0m/s|1500m|-20,-30|AoA0|surfaces set|shielded 14", new FarFlightProbe.TrimResult { AoACommanded = -0.336, AoADeg = -0.372, Pitch = 0.4319, Throttle = 0.2761, EngineThrottle = 0.2761, Converged = true, Mode = "level" } },
            { "Stearwing A300|hypersonic|M5|0m/s|30000m|30,150|AoA12|surfaces set|shielded 14", new FarFlightProbe.TrimResult { AoACommanded = 13.683, AoADeg = 13.632, Pitch = 1, Throttle = 0, EngineThrottle = double.NaN, Converged = true, Mode = "no thrust: speed changing -2.84 m/s2, throttle closed; pitch input at +1: +737 kN m left to the hold" } },
            { "Stearwing A300|subsonic-lat60|M0.8|0m/s|3000m|60,-60|AoA0|surfaces set|shielded 14", new FarFlightProbe.TrimResult { AoACommanded = -1.085, AoADeg = -1.123, Pitch = 0.2767, Throttle = 0.2963, EngineThrottle = 0.2963, Converged = true, Mode = "level" } },
            { "Stearwing A300|subsonic|M0.8|0m/s|3000m|0,-60|AoA0|surfaces set|shielded 14", new FarFlightProbe.TrimResult { AoACommanded = -0.6, AoADeg = -0.634, Pitch = 0.3996, Throttle = 0.3209, EngineThrottle = 0.3209, Converged = true, Mode = "level" } },
            { "Stearwing A300|subsonic-farside|M0.8|0m/s|3000m|0,120|AoA0|surfaces set|shielded 14", new FarFlightProbe.TrimResult { AoACommanded = -0.7, AoADeg = -0.736, Pitch = 0.3717, Throttle = 0.3154, EngineThrottle = 0.3154, Converged = true, Mode = "level" } },
            { "Stearwing A300|supersonic|M2|0m/s|14000m|0,-60|AoA5|surfaces set|shielded 14", new FarFlightProbe.TrimResult { AoACommanded = 3.553, AoADeg = 3.521, Pitch = 0.8516, Throttle = 1, EngineThrottle = 1, Converged = true, Mode = "throttle at 1: speed changing -4.35 m/s2" } },
            { "Stearwing A300|hypersonic|M7|0m/s|38000m|0,-60|AoA20|surfaces set|shielded 14", new FarFlightProbe.TrimResult { AoACommanded = 20, AoADeg = 19.951, Pitch = 1, Throttle = 0, EngineThrottle = double.NaN, Converged = true, Mode = "no thrust: speed changing -2.77 m/s2, throttle closed; no level flight within AoA -10 to 25 (at about -12.2): held at AoA 20.0, +1.56 m/s2 vertical; pitch input at +1: +592 kN m left to the hold" } },
            // Stearwing A300 PW (all-fixes FAR, 20260926-170309)
            { "Stearwing A300 PW|vacuum|M0|2200m/s|75000m|-30,30|AoA0|shielded 0", new FarFlightProbe.TrimResult { AoACommanded = 0, AoADeg = 0, Pitch = 0, Throttle = 0, EngineThrottle = double.NaN, Converged = true, Mode = "no air: inputs centered, throttle closed" } },
            { "Stearwing A300 PW|slow-high|M0.3|0m/s|1500m|-20,-30|AoA20|shielded 0", new FarFlightProbe.TrimResult { AoACommanded = 20, AoADeg = 19.271, Pitch = 1, Throttle = 0.4534, EngineThrottle = 0.4534, Converged = true, Mode = "no level flight within AoA -10 to 25 (at about 43.8): held at AoA 20.0, +8.32 m/s2 vertical; pitch input at +1: +2611 kN m left to the hold" } },
            { "Stearwing A300 PW|slow|M0.45|0m/s|1500m|-20,-30|AoA5|shielded 0", new FarFlightProbe.TrimResult { AoACommanded = 4.951, AoADeg = 4.917, Pitch = 0.8305, Throttle = 0.1313, EngineThrottle = 0.1313, Converged = true, Mode = "level" } },
            { "Stearwing A300 PW|hypersonic|M5|0m/s|30000m|30,150|AoA12|shielded 0", new FarFlightProbe.TrimResult { AoACommanded = 12.387, AoADeg = 12.352, Pitch = 1, Throttle = 0, EngineThrottle = double.NaN, Converged = true, Mode = "no thrust: speed changing -2.57 m/s2, throttle closed; pitch input at +1: +293 kN m left to the hold" } },
            { "Stearwing A300 PW|subsonic-lat60|M0.5|0m/s|3000m|60,-60|AoA5|shielded 0", new FarFlightProbe.TrimResult { AoACommanded = 4.427, AoADeg = 4.401, Pitch = 0.7099, Throttle = 0.1405, EngineThrottle = 0.1405, Converged = true, Mode = "level" } },
            { "Stearwing A300 PW|subsonic|M0.5|0m/s|3000m|0,-60|AoA5|shielded 0", new FarFlightProbe.TrimResult { AoACommanded = 5.064, AoADeg = 5.033, Pitch = 0.8697, Throttle = 0.1547, EngineThrottle = 0.1547, Converged = true, Mode = "level" } },
            { "Stearwing A300 PW|subsonic-farside|M0.5|0m/s|3000m|0,120|AoA5|shielded 0", new FarFlightProbe.TrimResult { AoACommanded = 4.93, AoADeg = 4.897, Pitch = 0.8247, Throttle = 0.1508, EngineThrottle = 0.1508, Converged = true, Mode = "level" } },
            { "Stearwing A300 PW|hypersonic|M7|0m/s|38000m|0,-60|AoA20|shielded 0", new FarFlightProbe.TrimResult { AoACommanded = -6.263, AoADeg = -6.215, Pitch = -1, Throttle = 0, EngineThrottle = double.NaN, Converged = true, Mode = "no thrust: speed changing -2.90 m/s2, throttle closed; pitch input at -1: -50 kN m left to the hold" } },
            { "Stearwing A300 PW|supersonic|M2|0m/s|14000m|0,-60|AoA5|shielded 0", new FarFlightProbe.TrimResult { AoACommanded = 4.093, AoADeg = 4.07, Pitch = 0.3994, Throttle = 1, EngineThrottle = 1, Converged = true, Mode = "throttle at 1: speed changing -3.31 m/s2" } },
        };

        private static string CrossTrimKey(Vessel v, FarFlightProbe.Condition c, bool surfacesSet, int shielded) => string.Format(CultureInfo.InvariantCulture,
            "{0}|{1}|M{2}|{3}m/s|{4}m|{5},{6}|AoA{7}{8}|shielded {9}", v.vesselName, c.Name, c.Mach, c.SpeedMs, c.AltM, c.Lat, c.Lon, c.AoADeg,
            surfacesSet ? "|surfaces set" : "", shielded);

        /// <summary>
        /// Whether FAR's prediction for a condition depends on where the vessel is when it asks, which it should not: a
        /// predictor asks about Mach 3 at 20 km while the craft is still in orbit. The craft is flown at each target
        /// and measured, the gold standard, reduced to a mean state (velocity in the vessel's frame, lat/lon/alt, UT);
        /// the prediction for that state is asked at the target itself, then from each vantage (another place, speed,
        /// altitude and angle of attack; vacuum). Every column should be the same number: the same-place error is the
        /// API's own accuracy, and any change from a vantage is the dependence on the vessel's present condition (wings
        /// carrying their last calculation, the atmosphere taken where the vessel is rather than where it is asked for).
        ///
        /// Targets and vantages are flown as the craft would fly them (trim, the default): each at the trim
        /// FarFlightProbe.Trim finds in place (level where it can be, else what it can hold, recorded) - taken from
        /// CrossTrims when it is there, else found in the run and written out for that table - surfaces where that
        /// puts them, engines running at the trim's throttle. The targets are the craft's (CrossTargetsFor: the
        /// subsonic ones where it can fly level). The prediction API evaluates the surfaces as they are and takes no deflection of its
        /// own, so a vantage predicts a target with the vantage's deflection; to keep that apart from the dependence on
        /// the condition, each target is also predicted in place with each vantage's controls (pitch input and
        /// throttle; none in vacuum, where FAR centers the surfaces): "controls" is that part, "rest" (from the vantage
        /// minus in place with its controls) the dependence on the vessel's condition. trim=false flies the listed
        /// angles of attack with the surfaces locked flat and the engines off, as before.
        ///
        /// Targets, vantages and the order targets are predicted in are shuffled by a seed (recorded) so no result
        /// rests on what was measured before it; the reports keep the listed order. Writes cross_condition.md and .csv.
        /// Params: frames (50), settle (25), predictFrames (20), craft (another SPH craft in the save), seed (random),
        /// trim (true), trims (coded; fresh finds every trim again), hold (torque), resettle, resetStall, surfaceFix.
        /// </summary>
        [UITest("Far_CrossCondition", StartsAt = StartState.Flight, Craft = "Aeris 4A", CraftDir = "SPH", LaunchSite = "Runway", TimeoutSec = 3000)]
        public static IEnumerator Far_CrossCondition(TestContext ctx)
        {
            yield return Wait.Seconds(5);
            var crossTargets = CrossTargetsFor(FlightGlobals.ActiveVessel.vesselName);
            var crossVantages = CrossVantagesFor(FlightGlobals.ActiveVessel.vesselName);
            ctx.Record("predictionApi", FarApi.HasPredictAt ? "CalculateVesselAeroForcesAt" : "CalculateVesselAeroForces (altitude only)");
            var inv = CultureInfo.InvariantCulture;
            int frames = (int)ctx.ParamDouble("frames", 50), settle = (int)ctx.ParamDouble("settle", 25);
            int predictFrames = (int)ctx.ParamDouble("predictFrames", 20);
            bool trim = ctx.Param("trim", "true") != "false";
            FarFlightProbe.HoldMode = ctx.Param("hold", "torque");
            ctx.Record("holdMode", FarFlightProbe.HoldMode);
            ctx.Record("trim", trim);
            int seed = (int)ctx.ParamDouble("seed", Environment.TickCount & 0x7fffffff);
            var rng = new System.Random(seed);
            int[] targetOrder = Enumerable.Range(0, crossTargets.Length).OrderBy(_ => rng.Next()).ToArray();
            int[] vantageOrder = Enumerable.Range(0, crossVantages.Length).OrderBy(_ => rng.Next()).ToArray();
            ctx.Record("seed", seed);
            ctx.Record("targetOrder", string.Join(" ", targetOrder.Select(i => crossTargets[i].Name)));
            ctx.Record("vantageOrder", string.Join(" ", vantageOrder.Select(i => crossVantages[i].Name)));

            bool[] trajectories = FarFlightProbe.QuietOtherPredictors();
            ctx.Record("trajectoriesStopped", trajectories != null);
            ctx.Record("predictionCallsWatched", FarCallWatch.EnsurePatched());
            FarCallWatch.Reset();
            Flight.IgnoreMaxTemperature(true);
            Vessel v = FlightGlobals.ActiveVessel;
            v.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            bool surfacesSet = v.vesselName == "Stearwing A300" && ctx.Param("surfaceFix", "true") == "true";
            if (surfacesSet)
                StearwingSurfaces(ctx, v);
            if (trim)
            {
                Ctrl.Attach();
                Ctrl.Set(pitch: 0f, yaw: 0f, roll: 0f);
                Ctrl.SetThrottle(0f);
                Ctrl.Stage();   // engines running, as flying
            }
            else
            {
                Ctrl.SetThrottle(0f);
                ctx.Record("surfacesLocked", FarApi.SetMaxDeflect(v, 0));
            }
            // The craft as loaded: a part lost at any point (aerodynamic failure, terrain) changes every later measurement.
            int partCount = v.parts.Count;
            ctx.Record("parts", partCount);

            // The gear comes up at the gentle condition whatever the order, the attitude held meanwhile: the shuffled
            // first target can be Mach 2, where an unheld airliner with its gear down comes apart.
            ctx.Step("Up in the air, gear up");
            yield return FarFlightProbe.Place(crossTargets[0]);
            ctx.Record("gearRetracted", Ctrl.RetractGear());
            var gearWait = new List<FarFlightProbe.Sample>();
            yield return FarFlightProbe.Hold(crossTargets[0], 400, 0, gearWait, predict: false);
            // Which control surfaces FAR treats as shielded depends on their deflection when their rule last ran; put
            // it in the state FAR's rules give this vessel now, so every run starts the same.
            if (ctx.Param("resettle", "true") != "false")
            {
                var settled = new bool[1];
                yield return FarApi.ResettleShielding(FlightGlobals.ActiveVessel, settled);
                ctx.Record("shieldingResettled", settled[0]);
            }
            v = FlightGlobals.ActiveVessel;
            ctx.Record("shieldedAtStart", FarApi.ShieldedWings(v));
            int shieldedAtStart = FarApi.ShieldedCount(v);
            double length = VesselLength(v);
            ctx.Record("vesselLengthM", Math.Round(length, 2));
            void AssertWhole(string where) =>
                Assert.True(FlightGlobals.ActiveVessel.parts.Count == partCount,
                            $"{where}: vessel whole ({FlightGlobals.ActiveVessel.parts.Count} of {partCount} parts)");
            AssertWhole("gear up");

            // How the craft flies each condition: the listed one, trimmed where it is flown.
            FarFlightProbe.Condition Flown(FarFlightProbe.Condition c, FarFlightProbe.TrimResult tr) => new FarFlightProbe.Condition
            {
                Name = c.Name, Mach = c.Mach, SpeedMs = c.SpeedMs, AltM = c.AltM, Lat = c.Lat, Lon = c.Lon, HeadingDeg = c.HeadingDeg,
                AoADeg = tr != null ? tr.AoACommanded : c.AoADeg,
            };
            string TrimLine(FarFlightProbe.TrimResult tr) => string.Format(inv,
                "{0} after {1}: AoA {2:F2}, pitch {3:+0.000;-0.000}, throttle {4:F3}; residual vertical {5:+0.00;-0.00} m/s2, along {6:+0.00;-0.00} m/s2, hold {7:+0;-0} kN m{8}",
                tr.Mode, tr.Iterations, tr.AoADeg, tr.Pitch, tr.Throttle, tr.VerticalAccel, tr.AlongAccel, tr.HoldPitchKNm, tr.Converged ? "" : " (not converged)");
            // The trim for a condition: from CrossTrims, else found here (and written out for the table).
            bool freshTrims = ctx.Param("trims", "coded") == "fresh";
            var found = new Dictionary<string, FarFlightProbe.TrimResult>();
            IEnumerator GetTrim(FarFlightProbe.Condition c, string label, FarFlightProbe.TrimResult[] into, int i)
            {
                string key = CrossTrimKey(FlightGlobals.ActiveVessel, c, surfacesSet, shieldedAtStart);
                if (!freshTrims && CrossTrims.TryGetValue(key, out FarFlightProbe.TrimResult coded))
                {
                    into[i] = coded;
                    ctx.Record($"{label}.trim", string.Format(inv, "coded: {0}: AoA {1:F2}, pitch {2:+0.000;-0.000}, throttle {3:F3}",
                                                                coded.Mode, coded.AoACommanded, coded.Pitch, coded.Throttle));
                    yield break;
                }
                // Not coded for this shielding: start from the trim coded for another, if any (nearly there).
                string sameCondition = key.Substring(0, key.LastIndexOf("|shielded ", StringComparison.Ordinal));
                var near = freshTrims ? null : CrossTrims.Where(kv => kv.Key.StartsWith(sameCondition + "|shielded ", StringComparison.Ordinal)).Select(kv => kv.Value).FirstOrDefault();
                ctx.Step($"Trim {label}");
                var tr = new FarFlightProbe.TrimResult();
                var start = near == null ? c : new FarFlightProbe.Condition
                {
                    Name = c.Name, Mach = c.Mach, SpeedMs = c.SpeedMs, AltM = c.AltM, Lat = c.Lat, Lon = c.Lon, HeadingDeg = c.HeadingDeg, AoADeg = near.AoACommanded,
                };
                yield return FarFlightProbe.Trim(start, near?.Pitch ?? 0, near?.Throttle ?? 0.5, tr);
                AssertWhole("trim " + label);
                foreach (string line in tr.Steps)
                    ctx.Record($"{label}.trimStep", line);
                ctx.Record($"{label}.trim", TrimLine(tr));
                into[i] = tr;
                found[key] = tr;
                File.WriteAllText(Path.Combine(ctx.ResultsDir, "trims.tsv"),
                                  FarFlightProbe.TrimResult.LineHeader + "\n" + string.Concat(found.Select(kv => kv.Value.ToLine(kv.Key) + "\n")));
                File.WriteAllText(Path.Combine(ctx.ResultsDir, "trims.cs"), string.Concat(found.Select(kv => "            " + kv.Value.ToCode(kv.Key) + "\n")));
            }
            // Flying on a trim: its inputs, the engines caught up at the condition, then placed there.
            IEnumerator FlyTrim(FarFlightProbe.Condition flown, FarFlightProbe.TrimResult tr)
            {
                Ctrl.Set(pitch: (float)tr.Pitch, yaw: 0f, roll: 0f);
                Ctrl.SetThrottle((float)tr.Throttle);
                yield return FarFlightProbe.SpoolAt(flown, tr.EngineThrottle);
                // the surfaces swung to the new inputs before the placement (on rails they stay put), so the vessel
                // arrives with them there; checked again after
                var took = new int[1];
                yield return FarFlightProbe.SettleSurfaces(flown, took);
                yield return FarFlightProbe.Place(flown);
                var after = new int[1];
                yield return FarFlightProbe.SettleSurfaces(flown, after);
                ctx.Record($"{flown.Name}.surfacesSettled", $"{took[0]} frames before placing, {after[0]} after; mean |deflection| {FarApi.MeanAbsDeflection(FlightGlobals.ActiveVessel).ToString("F2", inv)} deg");
            }

            // Each vantage trimmed where it is flown.
            var vantageTrim = new FarFlightProbe.TrimResult[crossVantages.Length];
            if (trim)
                foreach (int vi in vantageOrder)
                    yield return GetTrim(crossVantages[vi], "vantage." + crossVantages[vi].Name, vantageTrim, vi);

            // The gold standard, flown with no prediction called (a prediction can leave state on the wings that the
            // next physics frame reads); then the same with a prediction every frame, the difference being what
            // predicting does to the flight; then the prediction at the same place, and with each vantage's controls.
            // Measured in the shuffled order, kept in the listed one.
            var targetsAt = new FarFlightProbe.Target[crossTargets.Length];
            var targetTrim = new FarFlightProbe.TrimResult[crossTargets.Length];
            var hereAt = new List<double[]>[crossTargets.Length];
            var controlsAt = new List<double[]>[crossTargets.Length][];
            var withPredictionAt = new List<FarFlightProbe.Sample>[crossTargets.Length];
            var flownEquilibrium = new double[crossTargets.Length][];
            ctx.Record("predictionCallsBeforeTargets", FarCallWatch.Summary());
            foreach (int ti in targetOrder)
            {
                var c = crossTargets[ti];
                if (trim)
                    yield return GetTrim(c, c.Name, targetTrim, ti);
                var tt = targetTrim[ti];
                var flown = Flown(c, tt);
                ctx.Step($"Target {flown}");
                if (trim)
                    yield return FlyTrim(flown, tt);
                else
                    yield return FarFlightProbe.Place(flown);
                // Steady flight entered from unstalled flight, as the prediction assumes: FAR's flight stall ratchets
                // and follows the upstream wings, so otherwise it depends on the targets flown before.
                if (ctx.Param("resetStall", "true") != "false")
                    FarApi.ResetStall(FlightGlobals.ActiveVessel);
                var tg = new FarFlightProbe.Target { C = flown };
                FarCallWatch.Reset();
                yield return FarFlightProbe.Hold(flown, settle, frames, tg.Samples, predict: false);
                FarFlightProbe.Equilibrium(tg.Samples, out double eqV, out double eqA, out double eqM);
                flownEquilibrium[ti] = new[] { eqV, eqA, eqM };
                ctx.Record($"{c.Name}.flownEquilibrium", string.Format(inv, "vertical {0:+0.00;-0.00} m/s2, along {1:+0.00;-0.00} m/s2, hold {2:+0;-0} kN m", eqV, eqA, eqM));
                ctx.Record($"{c.Name}.predictionCallsDuringFlight", FarCallWatch.Summary());
                ctx.Record($"{c.Name}.shieldedWings", FarApi.ShieldedWings(FlightGlobals.ActiveVessel));
                ctx.Record($"{c.Name}.stalledWings", FarApi.StalledWings(FlightGlobals.ActiveVessel));
                var leak = new List<FarFlightProbe.Sample>();
                yield return FarFlightProbe.Hold(flown, 0, frames, leak);
                withPredictionAt[ti] = leak;
                Assert.True(tg.Samples.Count > frames / 2, $"{c.Name}: samples taken ({tg.Samples.Count})");
                AssertWhole(c.Name);
                tg.Reduce(FlightGlobals.ActiveVessel.mainBody);
                var at = new[] { new List<double[]>() };
                yield return FarFlightProbe.PredictFrom(flown, 0, predictFrames, new[] { tg }, at);
                targetsAt[ti] = tg;
                hereAt[ti] = at[0];

                // The target from where it is, with each vantage's controls.
                controlsAt[ti] = new List<double[]>[crossVantages.Length];
                if (trim)
                    foreach (int vi in vantageOrder)
                    {
                        var vt = vantageTrim[vi];
                        // the vantage's inputs, engines caught up, at the target's place and state; FAR puts the surfaces
                        // back where the inputs send them over the first frames off rails, so let them get there first
                        yield return FlyTrim(flown, vt);
                        var settleHold = new List<FarFlightProbe.Sample>();
                        yield return FarFlightProbe.Hold(flown, 50, 0, settleHold, predict: false);
                        double deflBefore = FarApi.MeanAbsDeflection(FlightGlobals.ActiveVessel);
                        var withV = new[] { new List<double[]>() };
                        yield return FarFlightProbe.PredictFrom(flown, 0, predictFrames, new[] { tg }, withV);
                        controlsAt[ti][vi] = withV[0];
                        ctx.Record($"{c.Name}.controls.{crossVantages[vi].Name}", string.Format(inv, "pitch {0:+0.000;-0.000}, throttle {1:F3}: mean |deflection| {2:F2} -> {3:F2} deg",
                                                                                             vt.Pitch, vt.Throttle, deflBefore, FarApi.MeanAbsDeflection(FlightGlobals.ActiveVessel)));
                    }
                AssertWhole(c.Name + " with the vantages' controls");
            }
            var targets = targetsAt.ToList();
            var here = hereAt.ToList();
            var withPrediction = withPredictionAt.ToList();

            // Every target predicted from each vantage, flown as trimmed there, the targets in a shuffled order within
            // each frame.
            var fromAt = new List<double[]>[crossVantages.Length][];
            foreach (int vi in vantageOrder)
            {
                var vc = Flown(crossVantages[vi], vantageTrim[vi]);
                ctx.Step($"Vantage {vc}");
                if (trim)
                    yield return FlyTrim(vc, vantageTrim[vi]);
                else
                    yield return FarFlightProbe.Place(vc);
                int[] within = Enumerable.Range(0, targets.Count).OrderBy(_ => rng.Next()).ToArray();
                var into = within.Select(_ => new List<double[]>()).ToArray();
                yield return FarFlightProbe.PredictFrom(vc, Math.Max(settle, 50), predictFrames, within.Select(i => targets[i]).ToList(), into);
                AssertWhole("vantage " + vc.Name);
                ctx.Record($"vantage.{vc.Name}.deflection", FarApi.MeanAbsDeflection(FlightGlobals.ActiveVessel).ToString("F2", inv) + " deg mean |deflection|");
                ctx.Record($"vantage.{vc.Name}.stalledWings", FarApi.StalledWings(FlightGlobals.ActiveVessel));
                ctx.Record($"vantage.{vc.Name}.shieldedWings", FarApi.ShieldedWings(FlightGlobals.ActiveVessel));
                var byTarget = new List<double[]>[targets.Count];
                for (int k = 0; k < within.Length; ++k)
                    byTarget[within[k]] = into[k];
                fromAt[vi] = byTarget;
            }
            var from = fromAt.ToList();

            var md = new StringBuilder("# FAR prediction API: does the answer depend on where the vessel is?\n\n");
            md.Append($"Vessel {v.vesselName}, length {length:F1} m, " + (trim
                ? "flown as trimmed at each target and vantage (surfaces where the trim puts them, engines running). "
                : "control surfaces locked flat, engines off, at the listed angles of attack. "));
            md.Append($"Each target is flown and measured ({frames} frames), then predicted at its mean state (velocity in the vessel's frame, lat/lon/alt, UT) from the target itself and from each vantage.\n\n");
            md.Append("Values: mean (pred - flight) as % of the target's mean total force (moments: of force × vessel length). ");
            md.Append("All columns in a row should match; the 'here' column is the API's own accuracy, a difference from it is the dependence on the vessel's present condition.");
            if (trim)
                md.Append(" The API evaluates the surfaces as they are, so a vantage predicts with its own deflection: the second table splits each vantage's shift into that ('controls': the target predicted in place with the vantage's pitch input and throttle, minus here) and the rest (from the vantage minus in place with its controls), the dependence on the vessel's condition.");
            md.Append("\n\nVantages: " + string.Join("; ", crossVantages.Select(c => $"{c.Name} ({(c.SpeedMs > 0 ? c.SpeedMs.ToString("F0", inv) + " m/s" : "Mach " + c.Mach.ToString("0.##", inv))}, {c.AltM / 1000:0.#} km, lat {c.Lat:0}, lon {c.Lon:0})")) + "\n\n");
            if (trim)
            {
                md.Append("Trims coded in the test unless the iterations column says otherwise; 'flown' is how steady the target was in this run (vertical and along-track acceleration, m/s², and the pitch torque the hold supplied, kN m).\n\n");
                md.Append("| trimmed | how | iterations | AoA | pitch input | throttle | flown: vertical | along | hold |\n|---|---|---|---|---|---|---|---|---|\n");
                void TrimRow(string name, FarFlightProbe.TrimResult tr, double[] eq) =>
                    md.Append(string.Format(inv, "| {0} | {1}{2} | {3} | {4:F2} | {5:+0.000;-0.000} | {6:F3} | {7} | {8} | {9} |\n",
                                            name, tr.Mode, tr.Converged ? "" : " (not converged)", tr.Iterations > 0 ? tr.Iterations.ToString(inv) : "coded",
                                            tr.AoACommanded, tr.Pitch, tr.Throttle,
                                            eq == null ? "-" : eq[0].ToString("+0.00;-0.00", inv), eq == null ? "-" : eq[1].ToString("+0.00;-0.00", inv),
                                            eq == null ? "-" : eq[2].ToString("+0;-0", inv)));
                for (int t = 0; t < crossTargets.Length; ++t) TrimRow("target " + crossTargets[t].Name, targetTrim[t], flownEquilibrium[t]);
                for (int vi = 0; vi < crossVantages.Length; ++vi) TrimRow("vantage " + crossVantages[vi].Name, vantageTrim[vi], null);
                md.Append('\n');
            }
            var csv = new StringBuilder("target,from,component,flight,flightSd,pred,predSd,errPct\n");
            string[] cols = new[] { "here" }.Concat(crossVantages.Select(c => "from " + c.Name)).ToArray();
            for (int t = 0; t < targets.Count; ++t)
            {
                var tg = targets[t];
                md.Append($"## {tg.C.Name}\n\nMach {tg.Samples.Average(s => s.Mach):F3}, {tg.AltM / 1000:F2} km, lat {tg.Lat:F1}, lon {tg.Lon:F1}, AoA {tg.Samples.Average(s => s.AoADeg):F2}, |F| {tg.ForceKN:F2} kN; ");
                md.Append($"density flight {tg.RhoFlight.ToString("G5", inv)}, FAR at the place and time {tg.RhoFar.ToString("G5", inv)} kg/m³\n\n");
                md.Append($"Check on the flight force (FAR's modules summed) from the vessel's acceleration, kN: drag {tg.Flight[0]:F2} vs {tg.Kinematic[0]:F2}, ");
                md.Append($"lift {tg.Flight[1]:F2} vs {tg.Kinematic[1]:F2}, side {tg.Flight[2]:F2} vs {tg.Kinematic[2]:F2}\n\n");
                var lk = withPrediction[t];
                double LeakPct(int k) => lk.Count == 0 ? double.NaN : 100 * (lk.Average(s => s.Flight[k]) - tg.Flight[k]) / (k < 3 ? tg.ForceKN : tg.ForceKN * length);
                md.Append($"Flight with a prediction every frame minus without, % (as below): drag {LeakPct(0):+0.00;-0.00}, lift {LeakPct(1):+0.00;-0.00}, pitch {LeakPct(3):+0.00;-0.00}\n\n");
                ctx.Record($"{tg.C.Name}.predictionLeakPct", $"drag {LeakPct(0):+0.00;-0.00}, lift {LeakPct(1):+0.00;-0.00}, pitch {LeakPct(3):+0.00;-0.00}");
                csv.Append($"{tg.C.Name},leak,drag,,,,,{LeakPct(0).ToString("F3", inv)}\n{tg.C.Name},leak,lift,,,,,{LeakPct(1).ToString("F3", inv)}\n{tg.C.Name},leak,pitch,,,,,{LeakPct(3).ToString("F3", inv)}\n");
                ctx.Record($"{tg.C.Name}.kinematicCheck", $"drag {tg.Flight[0]:F1} vs {tg.Kinematic[0]:F1}, lift {tg.Flight[1]:F1} vs {tg.Kinematic[1]:F1}");

                // err[k][j]: here, then from each vantage; ctl[k][vi]: in place with the vantage's controls.
                var lists = new[] { here[t] }.Concat(from.Select(f => f[t])).ToArray();
                var err = new double[6][];
                var ctl = new double[6][];
                for (int k = 0; k < 6; ++k)
                {
                    double scale = k < 3 ? tg.ForceKN : tg.ForceKN * length;
                    err[k] = new double[lists.Length];
                    ctl[k] = new double[crossVantages.Length];
                    for (int j = 0; j < lists.Length; ++j)
                    {
                        err[k][j] = double.NaN;
                        if (lists[j].Count == 0) continue;
                        int kk = k;
                        FarFlightProbe.Stats(lists[j].Select(a => a[kk]), out double pm, out double psd);
                        err[k][j] = 100 * (pm - tg.Flight[k]) / scale;
                        csv.Append($"{tg.C.Name},{cols[j]},{FarFlightProbe.ComponentNames[k]},{tg.Flight[k].ToString("G6", inv)},{tg.FlightSd[k].ToString("G6", inv)},{pm.ToString("G6", inv)},{psd.ToString("G6", inv)},{err[k][j].ToString("F3", inv)}\n");
                    }
                    for (int vi = 0; vi < crossVantages.Length; ++vi)
                    {
                        ctl[k][vi] = double.NaN;
                        var cl = controlsAt[t]?[vi];
                        if (cl == null || cl.Count == 0) continue;
                        int kk = k;
                        FarFlightProbe.Stats(cl.Select(a => a[kk]), out double pm, out double psd);
                        ctl[k][vi] = 100 * (pm - tg.Flight[k]) / scale;
                        csv.Append($"{tg.C.Name},controls {crossVantages[vi].Name},{FarFlightProbe.ComponentNames[k]},{tg.Flight[k].ToString("G6", inv)},{tg.FlightSd[k].ToString("G6", inv)},{pm.ToString("G6", inv)},{psd.ToString("G6", inv)},{ctl[k][vi].ToString("F3", inv)}\n");
                    }
                }
                md.Append("| component | flight | " + string.Join(" | ", cols) + " | max shift |\n|---|---|" + string.Concat(cols.Select(_ => "---|")) + "---|\n");
                for (int k = 0; k < 6; ++k)
                {
                    double maxShift = Enumerable.Range(1, lists.Length - 1).Where(j => !double.IsNaN(err[k][j])).Select(j => Math.Abs(err[k][j] - err[k][0])).DefaultIfEmpty(0).Max();
                    md.Append($"| {FarFlightProbe.ComponentNames[k]} | {tg.Flight[k].ToString("G4", inv)} | {string.Join(" | ", err[k].Select(x => double.IsNaN(x) ? "-" : x.ToString("+0.00;-0.00", inv)))} | {maxShift:F2} |\n");
                    string rest = "";
                    if (trim)
                    {
                        double maxRest = Enumerable.Range(0, crossVantages.Length).Where(vi => !double.IsNaN(ctl[k][vi]) && !double.IsNaN(err[k][vi + 1]))
                                                   .Select(vi => Math.Abs(err[k][vi + 1] - ctl[k][vi])).DefaultIfEmpty(0).Max();
                        rest = $", max rest {maxRest:F2}";
                    }
                    ctx.Record($"{tg.C.Name}.{FarFlightProbe.ComponentNames[k]}", $"here {err[k][0]:+0.00;-0.00}, max shift {maxShift:F2}{rest}");
                }
                md.Append('\n');
                if (trim)
                {
                    md.Append("| component | " + string.Join(" | ", crossVantages.Select(c => $"{c.Name}: controls | rest")) + " | max rest |\n|---|" + string.Concat(crossVantages.Select(_ => "---|---|")) + "---|\n");
                    for (int k = 0; k < 6; ++k)
                    {
                        var cells = new List<string>();
                        double maxRest = 0;
                        for (int vi = 0; vi < crossVantages.Length; ++vi)
                        {
                            double c = ctl[k][vi] - err[k][0], r = err[k][vi + 1] - ctl[k][vi];
                            cells.Add(double.IsNaN(c) ? "-" : c.ToString("+0.00;-0.00", inv));
                            cells.Add(double.IsNaN(r) ? "-" : r.ToString("+0.00;-0.00", inv));
                            if (!double.IsNaN(r)) maxRest = Math.Max(maxRest, Math.Abs(r));
                        }
                        md.Append($"| {FarFlightProbe.ComponentNames[k]} | {string.Join(" | ", cells)} | {maxRest:F2} |\n");
                    }
                    md.Append('\n');
                }
            }
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "cross_condition.md"), md.ToString());
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "cross_condition.csv"), csv.ToString());
            if (trim) Ctrl.Detach();
            FarFlightProbe.ResumeOtherPredictors(trajectories);
        }

        /// <summary>
        /// Whether a prediction leaves anything behind: FAR's prediction API is called by other mods (Trajectories over a
        /// whole grid of conditions, MechJeb's VesselState) while the vessel flies, so a prediction must neither change
        /// the state the flight reads nor depend on the predictions before it. In one frame, with no physics step in
        /// between: every value field of FAR's per-part state (FarStateSnapshot) is taken; the vessel's own condition
        /// A is predicted; a grid of unrelated conditions (Mach 0.3-8, AoA -10 to 45 deg, 1-50 km, other places) is
        /// predicted; A is predicted again; the state is taken again. Passes only if no field changed and the two
        /// predictions of A are exactly equal. Writes isolation.md (the fields that changed, grouped). Params: craft,
        /// strict (true: fail on any change).
        /// </summary>
        [UITest("Far_PredictionIsolation", StartsAt = StartState.Flight, Craft = "Aeris 4A", CraftDir = "SPH", LaunchSite = "Runway", TimeoutSec = 600)]
        public static IEnumerator Far_PredictionIsolation(TestContext ctx)
        {
            yield return Wait.Seconds(5);
            var inv = CultureInfo.InvariantCulture;
            ctx.Record("predictionApi", FarApi.HasPredictAt ? "CalculateVesselAeroForcesAt" : "CalculateVesselAeroForces (altitude only)");
            bool[] trajectories = FarFlightProbe.QuietOtherPredictors();
            Flight.IgnoreMaxTemperature(true);
            Ctrl.SetThrottle(0f);
            Vessel v = FlightGlobals.ActiveVessel;
            v.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            ctx.Record("surfacesLocked", FarApi.SetMaxDeflect(v, 0));

            var c = CrossTargets[0];
            ctx.Step($"Flying at {c}");
            yield return FarFlightProbe.Place(c);
            ctx.Record("gearRetracted", Ctrl.RetractGear());
            yield return Wait.Seconds(8);
            var settleSamples = new List<FarFlightProbe.Sample>();
            yield return FarFlightProbe.Hold(c, 25, 1, settleSamples, predict: false);

            ctx.Step("Snapshot, predictions, snapshot (one frame)");
            v = FlightGlobals.ActiveVessel;
            CelestialBody body = v.mainBody;
            Vector3 nose = v.ReferenceTransform.up, roof = -v.ReferenceTransform.forward;
            double ut = Planetarium.GetUniversalTime();
            Vector3 velA = v.srf_velocity;
            double latA = v.latitude, lonA = v.longitude, altA = v.altitude;

            Dictionary<string, string> before = FarStateSnapshot.Take(v);
            FarApi.ClearMemo(v);
            FarApi.PredictAtFull(v, velA, latA, lonA, altA, ut, out Vector3 fA0, out Vector3 tA0);
            int n = 0;
            foreach (double mach in new[] { 0.3, 0.9, 2.0, 5.0, 8.0 })
                foreach (double aoa in new[] { -10.0, 0.0, 10.0, 25.0, 45.0 })
                    foreach (double altM in new[] { 1000.0, 20000.0, 50000.0 })
                    {
                        double p = body.GetPressure(altM);
                        double speed = mach * body.GetSpeedOfSound(p, body.GetDensity(p, body.GetTemperature(altM)));
                        double a = aoa * Math.PI / 180;
                        Vector3 vel = (float)speed * ((float)Math.Cos(a) * nose - (float)Math.Sin(a) * roof);
                        FarApi.ClearMemo(v);
                        FarApi.PredictAtFull(v, vel, 30 - n % 60, 150 - 3 * n, altM, ut + 600 * n, out _, out _);
                        ++n;
                    }
            FarApi.ClearMemo(v);
            FarApi.PredictAtFull(v, velA, latA, lonA, altA, ut, out Vector3 fA1, out Vector3 tA1);
            Dictionary<string, string> after = FarStateSnapshot.Take(v);

            List<string> changed = FarStateSnapshot.Diff(before, after);
            // Exact: Unity's Vector3 == is equality within about 1e-5.
            bool sameA = fA0.x.Equals(fA1.x) && fA0.y.Equals(fA1.y) && fA0.z.Equals(fA1.z) &&
                         tA0.x.Equals(tA1.x) && tA0.y.Equals(tA1.y) && tA0.z.Equals(tA1.z);
            int parts = changed.Select(FarStateSnapshot.PartOf).Distinct().Count();
            ctx.Record("fieldsWatched", before.Count);
            ctx.Record("predictions", n + 2);
            ctx.Record("fieldsChanged", changed.Count);
            ctx.Record("partsChanged", parts);
            ctx.Record("changedFields", FarStateSnapshot.Summarize(changed));
            ctx.Record("sameAnswerForA", sameA);
            string V(Vector3 x) => $"({x.x.ToString("R", inv)}, {x.y.ToString("R", inv)}, {x.z.ToString("R", inv)})";
            if (!sameA)
                ctx.Record("answerForA", $"force {V(fA0)} then {V(fA1)}; torque {V(tA0)} then {V(tA1)}");

            var md = new System.Text.StringBuilder("# FAR prediction isolation\n\n");
            md.Append($"Vessel {v.vesselName}, flying at {c} with the control surfaces locked. In one frame: {before.Count} value fields of FAR's per-part state ");
            md.Append($"taken, the vessel's own condition predicted, {n} unrelated conditions predicted, the own condition predicted again, the state taken again.\n\n");
            md.Append($"- fields changed: **{changed.Count}** on {parts} parts\n");
            md.Append($"- same answer for the own condition before and after: **{(sameA ? "yes" : "no")}**\n");
            if (!sameA)
                md.Append($"  - force {V(fA0)} then {V(fA1)} kN; torque {V(tA0)} then {V(tA1)} kN m\n");
            md.Append("\n## Changed fields, by name\n\n");
            foreach (var g in changed.Select(FarStateSnapshot.FieldOf).GroupBy(f => f).OrderByDescending(g => g.Count()))
                md.Append($"- {g.Key}: {g.Count()} parts\n");
            md.Append("\n## Every change\n\n```\n").Append(string.Join("\n", changed.Take(2000))).Append("\n```\n");
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "isolation.md"), md.ToString());
            FarFlightProbe.ResumeOtherPredictors(trajectories);

            if (ctx.Param("strict", "true") != "false")
            {
                Assert.True(changed.Count == 0, $"predictions left FAR's flight state as it was ({changed.Count} fields changed: {FarStateSnapshot.Summarize(changed, 8)})");
                Assert.True(sameA, "the own condition predicted the same before and after the others");
            }
        }

        private struct PredictionInput
        {
            public Vector3 Vel;
            public Vector3d LatLonAlt;
            public double UT;
        }

        /// <summary>
        /// FAR's predictions off the main thread (FARAPI.CaptureVesselSnapshot on the main thread, then
        /// CalculateVesselAeroForcesAt(snapshot, ...) from any thread). Flying at a condition with the surfaces locked:
        /// (1) in the capture frame, the snapshot's answer for the vessel's own condition against the live API's, to
        /// 1e-6 relative (the two reach the same inputs by different float arithmetic; everything else is exact);
        /// (2) every input (the own condition and a grid of unrelated conditions and places) on the main thread and on
        /// one worker thread, exactly equal; (3) 8 worker threads at once, each in its own order, the main thread
        /// waiting inside one frame: every answer exactly the main thread's and FAR's state (FarStateSnapshot) unchanged;
        /// (4) 8 workers predicting for `flyFrames` frames while the vessel flies, the main thread predicting live and
        /// capturing new snapshots meanwhile (its call times recorded): every answer still exact, no exceptions;
        /// (5) the live-vessel API called from a worker refuses (zero); (6) after the gear comes down and FAR
        /// re-voxelizes, the old snapshot is refused and a new one works. Writes threads.md. Params: craft, threads (8),
        /// repeats (3), flyFrames (100), strict (true).
        /// </summary>
        [UITest("Far_PredictionThreads", StartsAt = StartState.Flight, Craft = "Aeris 4A", CraftDir = "SPH", LaunchSite = "Runway", TimeoutSec = 900)]
        public static IEnumerator Far_PredictionThreads(TestContext ctx)
        {
            yield return Wait.Seconds(5);
            Assert.True(FarSnapshotApi.Present, "FAR has the snapshot API (FARAPI.CaptureVesselSnapshot)");
            int threads = (int)ctx.ParamDouble("threads", 8), repeats = (int)ctx.ParamDouble("repeats", 3);
            int flyFrames = (int)ctx.ParamDouble("flyFrames", 100);
            bool[] trajectories = FarFlightProbe.QuietOtherPredictors();
            Flight.IgnoreMaxTemperature(true);
            Ctrl.SetThrottle(0f);
            Vessel v = FlightGlobals.ActiveVessel;
            v.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            FarApi.SetMaxDeflect(v, 0);

            var c = CrossTargets[0];
            ctx.Step($"Flying at {c}");
            yield return FarFlightProbe.Place(c);
            Ctrl.RetractGear();
            yield return Wait.Seconds(8);
            var none = new List<FarFlightProbe.Sample>();
            yield return FarFlightProbe.Hold(c, 25, 1, none, predict: false);

            // ---- the capture frame: everything up to the 8-thread batch happens with no physics step in between
            ctx.Step("Capture frame: snapshot vs live, main thread vs one worker, 8 workers");
            v = FlightGlobals.ActiveVessel;
            CelestialBody body = v.mainBody;
            Vector3 nose = v.ReferenceTransform.up, roof = -v.ReferenceTransform.forward;
            double ut = Planetarium.GetUniversalTime();
            var inputs = new List<PredictionInput>
            {
                new PredictionInput { Vel = v.srf_velocity, LatLonAlt = new Vector3d(v.latitude, v.longitude, v.altitude), UT = ut },
            };
            for (int rep = 0; rep < 4; ++rep)
                foreach (double mach in new[] { 0.3, 0.9, 2.0, 5.0, 8.0 })
                    foreach (double aoa in new[] { -10.0, 0.0, 10.0, 25.0, 45.0 })
                        foreach (double altM in new[] { 1000.0, 20000.0, 50000.0 })
                        {
                            int n = inputs.Count;
                            double p = body.GetPressure(altM);
                            double speed = mach * body.GetSpeedOfSound(p, body.GetDensity(p, body.GetTemperature(altM)));
                            double a = (aoa + 2 * rep) * Math.PI / 180;
                            inputs.Add(new PredictionInput
                            {
                                Vel = (float)speed * ((float)Math.Cos(a) * nose - (float)Math.Sin(a) * roof),
                                LatLonAlt = new Vector3d(60 - (7 * n) % 120, 180 - (13 * n) % 360, altM),
                                UT = ut + 900 * n,
                            });
                        }
            int count = inputs.Count;
            ctx.Record("inputs", count);

            object snap = FarSnapshotApi.Capture(v);
            Assert.True(snap != null, "a snapshot of the flying vessel");
            FarApi.ClearMemo(v);
            FarSnapshotApi.PredictLive(v, inputs[0].Vel, inputs[0].LatLonAlt, inputs[0].UT, out Vector3 fLive, out Vector3 tLive);

            var main = new Vector3[count, 2];
            var ok = new bool[count];
            for (int i = 0; i < count; ++i)
            {
                ok[i] = FarSnapshotApi.Predict(snap, inputs[i].Vel, inputs[i].LatLonAlt, inputs[i].UT, out main[i, 0], out main[i, 1]);
            }
            int refused = ok.Count(b => !b);
            // The live API reads Unity's transforms and KSP's atmosphere functions directly, the snapshot its copies of
            // them computed in C#: the same inputs by different arithmetic, so the two agree to float rounding (about
            // one unit in the last place of a float), not bit for bit. Every other comparison here is exact.
            const double LiveTolerance = 1e-6;
            double relForce = (main[0, 0] - fLive).magnitude / Math.Max(1e-9f, fLive.magnitude);
            double relTorque = (main[0, 1] - tLive).magnitude / Math.Max(1e-9f, tLive.magnitude);
            bool snapMatchesLive = relForce <= LiveTolerance && relTorque <= LiveTolerance;
            ctx.Record("refusedFresh", refused);
            ctx.Record("snapshotMatchesLive", snapMatchesLive);
            ctx.Record("snapshotVsLiveRel", $"force {relForce:E2}, torque {relTorque:E2} (tolerance {LiveTolerance:E0})");

            var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var mismatches = new int[1];
            // Runs every input in the given order `times` times, counting answers that differ from the main thread's.
            void Work(int[] order, int times)
            {
                try
                {
                    for (int r = 0; r < times; ++r)
                        foreach (int i in order)
                        {
                            bool good = FarSnapshotApi.Predict(snap, inputs[i].Vel, inputs[i].LatLonAlt, inputs[i].UT, out Vector3 f, out Vector3 t);
                            if (good != ok[i] || !FarSnapshotApi.Same(f, t, main[i, 0], main[i, 1]))
                                System.Threading.Interlocked.Increment(ref mismatches[0]);
                        }
                }
                catch (Exception e) { errors.Enqueue(e.InnerException?.ToString() ?? e.ToString()); }
            }

            int[] identity = Enumerable.Range(0, count).ToArray();
            var one = new System.Threading.Thread(() => Work(identity, 1));
            one.Start();
            one.Join();
            int oneWorkerMismatches = mismatches[0];
            ctx.Record("oneWorkerMismatches", oneWorkerMismatches);

            Dictionary<string, string> before = FarStateSnapshot.Take(v);
            mismatches[0] = 0;
            var rng = new System.Random(4242);
            var workers = new List<System.Threading.Thread>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int k = 0; k < threads; ++k)
            {
                int[] order = identity.OrderBy(_ => rng.Next()).ToArray();
                workers.Add(new System.Threading.Thread(() => Work(order, repeats)));
            }
            foreach (var w in workers) w.Start();
            foreach (var w in workers) w.Join();
            double frozenBatchMs = sw.Elapsed.TotalMilliseconds;
            List<string> changed = FarStateSnapshot.Diff(before, FarStateSnapshot.Take(v));
            int frozenMismatches = mismatches[0];
            ctx.Record("frozenBatch", $"{threads} threads x {repeats} x {count} in {frozenBatchMs:F0} ms");
            ctx.Record("frozenMismatches", frozenMismatches);
            ctx.Record("frozenFieldsChanged", changed.Count);
            if (changed.Count > 0) ctx.Record("frozenChangedFields", FarStateSnapshot.Summarize(changed));

            // ---- flying: workers keep predicting across frames while the main thread flies and predicts live
            ctx.Step($"{threads} workers for {flyFrames} frames while flying");
            mismatches[0] = 0;
            var stop = new bool[1];
            int calls = 0;
            workers.Clear();
            for (int k = 0; k < threads; ++k)
            {
                int[] order = identity.OrderBy(_ => rng.Next()).ToArray();
                workers.Add(new System.Threading.Thread(() =>
                {
                    while (!System.Threading.Volatile.Read(ref stop[0]))
                    {
                        Work(order, 1);
                        System.Threading.Interlocked.Add(ref calls, order.Length);
                    }
                }));
            }
            foreach (var w in workers) w.Start();
            double maxLiveMs = 0, maxCaptureMs = 0;
            int captures = 0, capturesRefused = 0;
            var fixedWait = new WaitForFixedUpdate();
            for (int frame = 0; frame < flyFrames; ++frame)
            {
                Flight.PointAtAoA(c.AoADeg);
                foreach (Part part in FlightGlobals.ActiveVessel.parts)
                    if (part.Rigidbody != null) part.Rigidbody.angularVelocity = Vector3.zero;
                yield return fixedWait;
                v = FlightGlobals.ActiveVessel;
                var t0 = System.Diagnostics.Stopwatch.StartNew();
                FarApi.ClearMemo(v);
                FarSnapshotApi.PredictLive(v, v.srf_velocity, new Vector3d(v.latitude, v.longitude, v.altitude), Planetarium.GetUniversalTime(), out _, out _);
                maxLiveMs = Math.Max(maxLiveMs, t0.Elapsed.TotalMilliseconds);
                if (frame % 10 == 0)
                {
                    t0 = System.Diagnostics.Stopwatch.StartNew();
                    object s2 = FarSnapshotApi.Capture(v);
                    maxCaptureMs = Math.Max(maxCaptureMs, t0.Elapsed.TotalMilliseconds);
                    ++captures;
                    if (s2 == null || !FarSnapshotApi.Predict(s2, v.srf_velocity, new Vector3d(v.latitude, v.longitude, v.altitude), Planetarium.GetUniversalTime(), out _, out _))
                        ++capturesRefused;
                }
            }
            System.Threading.Volatile.Write(ref stop[0], true);
            foreach (var w in workers) w.Join();
            int flyingMismatches = mismatches[0];
            ctx.Record("flyingCalls", calls);
            ctx.Record("flyingMismatches", flyingMismatches);
            ctx.Record("mainThreadLiveMaxMs", Math.Round(maxLiveMs, 2));
            ctx.Record("mainThreadCaptureMaxMs", Math.Round(maxCaptureMs, 2));
            ctx.Record("capturesWhileFlying", $"{captures}, refused {capturesRefused}");

            // ---- the live API from a worker must refuse
            ctx.Step("Live API from a worker, stale snapshot");
            Vector3 fOff = Vector3.one, tOff = Vector3.one;
            Vessel vw = FlightGlobals.ActiveVessel;
            Vector3 velW = vw.srf_velocity;
            var lla = new Vector3d(vw.latitude, vw.longitude, vw.altitude);
            double utW = Planetarium.GetUniversalTime();
            var off = new System.Threading.Thread(() =>
            {
                try { FarSnapshotApi.PredictLive(vw, velW, lla, utW, out fOff, out tOff); }
                catch (Exception e) { errors.Enqueue("live API off-thread: " + (e.InnerException?.Message ?? e.Message)); }
            });
            off.Start();
            off.Join();
            bool liveRefusedOffThread = fOff == Vector3.zero && tOff == Vector3.zero;
            ctx.Record("liveApiOffThreadRefused", liveRefusedOffThread);

            // ---- stale: the gear changes the shape, FAR re-voxelizes, the old snapshot must be refused
            Ctrl.Gear(true);
            yield return Wait.Seconds(6);
            v = FlightGlobals.ActiveVessel;
            bool staleRefused = !FarSnapshotApi.Predict(snap, inputs[0].Vel, inputs[0].LatLonAlt, inputs[0].UT, out _, out _);
            object fresh = FarSnapshotApi.Capture(v);
            bool freshWorks = fresh != null && FarSnapshotApi.Predict(fresh, v.srf_velocity, new Vector3d(v.latitude, v.longitude, v.altitude), Planetarium.GetUniversalTime(), out _, out _);
            ctx.Record("staleSnapshotRefused", staleRefused);
            ctx.Record("freshSnapshotWorks", freshWorks);
            ctx.Record("exceptions", errors.Count);
            FarFlightProbe.ResumeOtherPredictors(trajectories);

            var md = new System.Text.StringBuilder("# FAR predictions off the main thread\n\n");
            md.Append($"Vessel {v.vesselName}, flying at {c}, surfaces locked; {count} inputs (the own condition and unrelated conditions, places and times).\n\n");
            md.Append("| check | result |\n|---|---|\n");
            md.Append($"| snapshot answer = live answer to {LiveTolerance:E0} relative, own condition, capture frame | {snapMatchesLive} (force {relForce:E2}, torque {relTorque:E2}) |\n");
            md.Append($"| fresh snapshot refused | {refused} of {count} |\n");
            md.Append($"| one worker = main thread | {count - oneWorkerMismatches} of {count} exact |\n");
            md.Append($"| {threads} workers x {repeats}, main thread waiting in one frame | {frozenMismatches} mismatches in {threads * repeats * count}, {frozenBatchMs:F0} ms |\n");
            md.Append($"| FAR flight state changed by that batch | {changed.Count} fields |\n");
            md.Append($"| {threads} workers for {flyFrames} frames while flying | {flyingMismatches} mismatches in {calls} calls |\n");
            md.Append($"| main thread meanwhile: live prediction / capture, max ms | {maxLiveMs:F2} / {maxCaptureMs:F2} |\n");
            md.Append($"| snapshots captured while flying (refused) | {captures} ({capturesRefused}) |\n");
            md.Append($"| live-vessel API from a worker refused | {liveRefusedOffThread} |\n");
            md.Append($"| old snapshot refused after re-voxelization / new one works | {staleRefused} / {freshWorks} |\n");
            md.Append($"| exceptions on workers | {errors.Count} |\n");
            if (errors.Count > 0)
                md.Append("\n## Exceptions\n\n```\n").Append(string.Join("\n---\n", errors.Take(5))).Append("\n```\n");
            if (changed.Count > 0)
                md.Append("\n## Changed fields\n\n```\n").Append(string.Join("\n", changed.Take(500))).Append("\n```\n");
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "threads.md"), md.ToString());

            if (ctx.Param("strict", "true") != "false")
            {
                Assert.True(errors.Count == 0, $"no exceptions on worker threads ({errors.Count}; first: {errors.FirstOrDefault()})");
                Assert.True(snapMatchesLive, $"the snapshot's answer for the own condition is the live API's to {LiveTolerance:E0} in the capture frame (force {relForce:E2}, torque {relTorque:E2})");
                Assert.True(refused == 0, $"a fresh snapshot is not refused ({refused} of {count})");
                Assert.True(oneWorkerMismatches == 0, $"one worker thread gives the main thread's answers ({oneWorkerMismatches} differ)");
                Assert.True(frozenMismatches == 0, $"{threads} concurrent workers give the main thread's answers ({frozenMismatches} differ)");
                Assert.True(changed.Count == 0, $"concurrent predictions leave FAR's flight state as it was ({changed.Count} fields: {FarStateSnapshot.Summarize(changed, 8)})");
                Assert.True(flyingMismatches == 0, $"workers predicting while the vessel flies give the main thread's answers ({flyingMismatches} of {calls} differ)");
                Assert.True(liveRefusedOffThread, "the live-vessel API refuses a worker thread");
                Assert.True(staleRefused && freshWorks, "a snapshot taken before re-voxelization is refused and a new one works");
            }
        }

        private static double Mean(List<FarFlightProbe.Sample> list, Func<FarFlightProbe.Sample, double> f) => list.Count == 0 ? double.NaN : list.Average(f);

        // Mean error ± its SD, % of the mean total force (moments: of force x length).
        private static string ErrPct(List<FarFlightProbe.Sample> list, int k, double length)
        {
            if (list.Count < 2) return "-";
            double f = list.Average(s => s.ForceKN), scale = k < 3 ? f : f * length;
            FarFlightProbe.Stats(list.Select(s => s.Predicted[k] - s.Flight[k]), out double m, out double sd);
            return string.Format(CultureInfo.InvariantCulture, "{0:+0.00;-0.00} ± {1:0.00}", 100 * m / scale, 100 * sd / scale);
        }

        private static string FlightSdPct(List<FarFlightProbe.Sample> list, int k, double length)
        {
            if (list.Count < 2) return "-";
            double f = list.Average(s => s.ForceKN), scale = k < 3 ? f : f * length;
            FarFlightProbe.Stats(list.Select(s => s.Flight[k]), out _, out double sd);
            return (100 * sd / scale).ToString("0.000", CultureInfo.InvariantCulture);
        }

        // Heading of the surface velocity (deg from north).
        private static double HeadingDeg(Vessel v)
        {
            CelestialBody body = v.mainBody;
            Vector3d pos = body.GetWorldSurfacePosition(v.latitude, v.longitude, v.altitude);
            Vector3d north = (body.GetWorldSurfacePosition(v.latitude + 1e-4, v.longitude, v.altitude) - pos).normalized;
            Vector3d east = (body.GetWorldSurfacePosition(v.latitude, v.longitude + 1e-4, v.altitude) - pos).normalized;
            Vector3d vel = v.srf_velocity;
            return Math.Atan2(Vector3d.Dot(vel, east), Vector3d.Dot(vel, north)) * 180 / Math.PI;
        }

        // Extent of the vessel's parts along its nose axis (m), the length the moment errors are scaled by.
        private static double VesselLength(Vessel v)
        {
            Vector3 nose = v.ReferenceTransform.up;
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (Part p in v.parts)
            {
                float d = Vector3.Dot(p.transform.position, nose);
                lo = Mathf.Min(lo, d);
                hi = Mathf.Max(hi, d);
            }
            return hi > lo ? hi - lo : 1.0;
        }
    }
}
