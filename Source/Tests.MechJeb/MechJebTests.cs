using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using KSPUITest;
using KSPUITest.MechJeb;
using MuMech;

namespace KSPUITest.Tests.MechJeb
{
    /// <summary>MechJeb-driven flight tests. Params: craft, orbitAltKm, inclination, ascentType (CLASSIC|PVG), warp.</summary>
    public static class MechJebTests
    {
        [UITest("Mj_SmartAss", Requires = "MechJeb", StartsAt = StartState.Flight, Craft = "UITest_Rocket")]
        public static IEnumerator Mj_SmartAss(TestContext ctx)
        {
            Assert.True(Mj.Present, "vessel has a MechJebCore (MechJebForAll or an MJ part)");
            ctx.Step("SmartASS surface hold on the pad");
            Mj.SmartAssSurface(heading: 90, pitch: 90);
            yield return Wait.Seconds(3);
            var sa = Mj.Core.GetComputerModule<MechJebModuleSmartASS>();
            Assert.Equal(MechJebModuleSmartASS.Target.SURFACE, sa.target, "SmartASS target");
            Assert.True(sa.Enabled || Mj.Core.Attitude.Enabled, "attitude controller engaged");
            ctx.Record("attitudeEnabled", Mj.Core.Attitude.Enabled);
            yield return Snap.Shot(ctx, "smartass");
            Mj.SmartAssOff();
        }

        /// <summary>
        /// FAR's prediction API against the aerodynamic moment FAR actually applies, on a plane held at a pitch by
        /// its control surfaces alone (reaction wheels off): in a steady state the true aero pitching moment is
        /// zero, so whatever FARAPI.CalculateVesselAeroForces reports at that state is its own error. Made for a
        /// fuselage with a CoM offset (a Procedural Parts cone), where the editor/prediction force context placed
        /// body forces at the wrong point. Params: craft (default PoofySkirt), pitch (deg, default 12), heading,
        /// climbToKm (default 2), sampleSec (default 40).
        /// </summary>
        [UITest("Far_ComOffset_Flight", Requires = "MechJeb", StartsAt = StartState.Flight, Craft = "PoofySkirt", CraftDir = "SPH", LaunchSite = "Runway", TimeoutSec = 900)]
        public static IEnumerator Far_ComOffset_Flight(TestContext ctx)
        {
            Assert.True(Mj.Present, "vessel has a MechJebCore (MechJebForAll or an MJ part)");
            var v = FlightGlobals.ActiveVessel;
            int wheels = 0;
            foreach (var p in v.parts)
                foreach (var w in p.FindModulesImplementing<ModuleReactionWheel>()) { w.State = ModuleReactionWheel.WheelState.Disabled; ++wheels; }
            ctx.Record("reactionWheelsDisabled", wheels);
            v.ActionGroups.SetGroup(KSPActionGroup.RCS, false);
            double pitch = ctx.ParamDouble("pitch", 12), heading = ctx.ParamDouble("heading", 90), climbTo = ctx.ParamDouble("climbToKm", 2) * 1000;

            ctx.Step("Take off and climb on the control surfaces alone");
            Ctrl.Stage();
            Ctrl.SetThrottle(1f);
            Ctrl.Brakes(false);
            Mj.SmartAssSurface(heading, pitch: 8);
            yield return Wait.Until(() => Flight.Altitude > climbTo, 240, "climb to the sampling altitude");
            Ctrl.Gear(false);

            ctx.Step($"Hold {pitch} deg pitch and sample FAR's prediction API against the applied aero moment");
            Mj.SmartAssSurface(heading, pitch: pitch);
            yield return Wait.Seconds(15);   // let the attitude and speed settle
            double sampleSec = ctx.ParamDouble("sampleSec", 40);
            var sb = new System.Text.StringBuilder("missionTime,altitude,srfSpeed,mach,dynPreskPa,aoa,pitchInput,pitchRateDegS,trueWingKNm,trueBodyKNm,trueTotalKNm,farApiPitchTorqueKNm,farApiLiftKN,farApiDragKN\n");
            double t0 = v.missionTime, n = 0, sumApi = 0, sumTrue = 0, sumAoa = 0, sumQ = 0, sumMach = 0, sumIn = 0, sumBody = 0, sumWing = 0;
            yield return Wait.Until(() =>
            {
                var av = FlightGlobals.ActiveVessel;
                double tq = FarApi.PredictedPitchTorqueKNm(av, out double apiLift, out double apiDrag);
                double tt = FarApi.TrueAeroPitchMomentKNm(av, out double wk, out double bk);
                double rate = Vector3.Dot(av.angularVelocity, av.ReferenceTransform.InverseTransformDirection(FarApi.PitchUpAxis(av))) * 57.2958;
                sb.Append(string.Join(",", new[] { av.missionTime.ToString("F1"), av.altitude.ToString("F0"), av.srfSpeed.ToString("F1"), av.mach.ToString("F3"), av.dynamicPressurekPa.ToString("F3"),
                    Flight.AoA.ToString("F2"), av.ctrlState.pitch.ToString("F3"), rate.ToString("F3"), wk.ToString("F2"), bk.ToString("F2"), tt.ToString("F2"), tq.ToString("F2"), apiLift.ToString("F2"), apiDrag.ToString("F2") })).Append('\n');
                if (Math.Abs(rate) < 0.5 && !double.IsNaN(tq))
                {
                    ++n; sumApi += tq; sumTrue += tt; sumAoa += Flight.AoA; sumQ += av.dynamicPressurekPa; sumMach += av.mach; sumIn += av.ctrlState.pitch; sumBody += bk; sumWing += wk;
                }
                return av.missionTime - t0 > sampleSec;
            }, sampleSec + 30, "sampling window", shot: false);
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "comoffset-flight.csv"), sb.ToString());
            ctx.Record("samples", n);
            if (n > 0)
            {
                ctx.Record("meanAoA", Math.Round(sumAoa / n, 2)); ctx.Record("meanMach", Math.Round(sumMach / n, 3)); ctx.Record("meanQkPa", Math.Round(sumQ / n, 3));
                ctx.Record("meanPitchInput", Math.Round(sumIn / n, 3));
                ctx.Record("trueWingKNm", Math.Round(sumWing / n, 2)); ctx.Record("trueBodyKNm", Math.Round(sumBody / n, 2)); ctx.Record("trueTotalKNm", Math.Round(sumTrue / n, 2));
                ctx.Record("farApiPitchTorqueKNm", Math.Round(sumApi / n, 2));
            }
            Mj.SmartAssOff();
        }

        /// <summary>
        /// FAR's voxel grid on a flying plane through gear and elevator changes: element size, bounds, per-part face
        /// areas, and the vessel's Cd/Cl through each re-voxelization (the gear animation triggers one). Made for
        /// VehicleVoxel's sizing change, where the element and the areas of the parts that are neither control
        /// surfaces nor wheels should stop moving with the gear state and the elevon deflection. Params: craft
        /// (default PoofySkirt) or crafts (a "+"-separated list, each launched in turn and its values prefixed with
        /// the craft name), heading, climbToM (default 200), pitches (deg, default "8,14,3"), sampleSec (default 6).
        /// </summary>
        [UITest("Far_VoxelSize_Flight", Requires = "MechJeb", StartsAt = StartState.Flight, Craft = "PoofySkirt", CraftDir = "SPH", LaunchSite = "Runway", TimeoutSec = 1800)]
        public static IEnumerator Far_VoxelSize_Flight(TestContext ctx)
        {
            var rows = new System.Text.StringBuilder(FarApi.PartAreasHeader);
            var samples = new System.Text.StringBuilder("sample,gear,pitchHold,elementSize,boundsX,boundsY,boundsZ,boundsVolume,length,maxCrossSection,areaAll,areaFixed,meanDeflectDeg,pitchInput,aoa,mach,qkPa,Cl,Cd,refArea,dragKN\n");
            string[] crafts = ctx.Param("crafts") != null ? ctx.Param("crafts").Split('+') : new[] { (string)null };
            var failed = new System.Collections.Generic.List<string>();
            foreach (string craftName in crafts)
            {
                string prefix = "";
                if (craftName != null)
                {
                    ctx.Step("Craft " + craftName.Trim());
                    yield return Nav.EnsureState(ctx, StartState.Flight, ctx.CraftPath(craftName.Trim(), "SPH"), "Runway");
                    prefix = craftName.Trim() + ".";
                }
                // a craft that will not take off (or breaks) fails its own leg; the others still run and the CSVs
                // hold everything sampled so far
                var leg = VoxelSizeFlightCraft(ctx, prefix, rows, samples);
                string error = null;
                while (true)
                {
                    try { if (!leg.MoveNext()) break; }
                    catch (Exception ex) { error = ex.Message; break; }
                    yield return leg.Current;
                }
                File.WriteAllText(Path.Combine(ctx.ResultsDir, "voxelsize-flight-parts.csv"), rows.ToString());
                File.WriteAllText(Path.Combine(ctx.ResultsDir, "voxelsize-flight.csv"), samples.ToString());
                if (error != null)
                {
                    Log.Warn($"craft {prefix} failed: {error}");
                    ctx.Record(prefix + "failed", error);
                    failed.Add(prefix.TrimEnd('.'));
                    Mj.SmartAssOff();
                }
            }
            Assert.True(failed.Count == 0, "every craft flew its sequence (failed: " + string.Join(", ", failed) + ")");
        }

        private static IEnumerator VoxelSizeFlightCraft(TestContext ctx, string prefix, System.Text.StringBuilder rows, System.Text.StringBuilder samples)
        {
            Assert.True(Mj.Present, "vessel has a MechJebCore (MechJebForAll or an MJ part)");
            double heading = ctx.ParamDouble("heading", 90), climbTo = ctx.ParamDouble("climbToM", 200), sampleSec = ctx.ParamDouble("sampleSec", 6);
            string[] pitches = (ctx.Param("pitches") ?? "8,14,3").Split(',');
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            IEnumerator Sample(string name, string gear, string pitchHold)
            {
                name = prefix + name;
                var v = FlightGlobals.ActiveVessel;
                double n = 0, sIn = 0, sAoa = 0, sMach = 0, sQ = 0, sCl = 0, sCd = 0, sRef = 0, sDefl = 0;
                double t0 = Planetarium.GetUniversalTime();   // missionTime stands still before launch
                yield return Wait.Until(() =>
                {
                    var av = FlightGlobals.ActiveVessel;
                    double defl = 0; int nd = 0;
                    foreach (Part p in av.parts)
                        foreach (PartModule m in p.Modules)
                            if (m.GetType().Name == "FARControllableSurface") { defl += Math.Abs(Convert.ToDouble(Reflect.Get(m, "AoAoffset"))); ++nd; }
                    ++n; sIn += av.ctrlState.pitch; sAoa += Flight.AoA; sMach += av.mach; sQ += av.dynamicPressurekPa;
                    sCl += FarApi.VesselStat(av, "VesselLiftCoeff"); sCd += FarApi.VesselStat(av, "VesselDragCoeff"); sRef += FarApi.VesselStat(av, "VesselRefArea");
                    sDefl += nd > 0 ? defl / nd : 0;
                    return Planetarium.GetUniversalTime() - t0 > sampleSec;
                }, sampleSec + 20, "sampling window " + name, shot: false);
                Assert.True(FarApi.VoxelInfo(out double el, out Vector3 bs, out double len, out double mcs), "FAR voxel info (" + name + ")");
                FarApi.PartAreas(FlightGlobals.ActiveVessel.parts, name, rows, out double areaAll, out double areaFixed);
                double q = sQ / n, cd = sCd / n, refA = sRef / n;
                samples.Append(string.Join(",", new[] { name, gear, pitchHold, el.ToString("F5", inv), bs.x.ToString("F3", inv), bs.y.ToString("F3", inv), bs.z.ToString("F3", inv),
                    (bs.x * bs.y * bs.z).ToString("F3", inv), len.ToString("F3", inv), mcs.ToString("F4", inv), areaAll.ToString("F3", inv), areaFixed.ToString("F3", inv),
                    (sDefl / n).ToString("F2", inv), (sIn / n).ToString("F3", inv), (sAoa / n).ToString("F2", inv), (sMach / n).ToString("F3", inv), q.ToString("F3", inv),
                    (sCl / n).ToString("F4", inv), cd.ToString("F5", inv), refA.ToString("F3", inv), (cd * q * refA).ToString("F2", inv) })).Append('\n');
                ctx.Record(name + ".elementSize", Math.Round(el, 5)); ctx.Record(name + ".boundsVolume", Math.Round(bs.x * bs.y * bs.z, 3));
                ctx.Record(name + ".areaAll", Math.Round(areaAll, 3)); ctx.Record(name + ".areaFixed", Math.Round(areaFixed, 3));
                ctx.Record(name + ".meanDeflectDeg", Math.Round(sDefl / n, 2)); ctx.Record(name + ".Cd", Math.Round(cd, 5)); ctx.Record(name + ".qkPa", Math.Round(q, 3));
                ctx.Record(name + ".gearStates", Ctrl.GearStates());
            }

            IEnumerator Revoxel(bool gearDown)
            {
                // the gear animation is what re-voxelizes the vessel (rate limited); wait for the areas to move, then for the calculation to settle
                FarApi.PartAreas(FlightGlobals.ActiveVessel.parts, null, null, out double before, out _);
                Ctrl.Gear(gearDown);
                double after = before;
                yield return Wait.Until(() => { FarApi.PartAreas(FlightGlobals.ActiveVessel.parts, null, null, out after, out _); return Math.Abs(after - before) > 1e-6; }, 30, "re-voxelization after gear " + (gearDown ? "down" : "up"), shot: false);
                yield return Wait.Seconds(6);
            }

            // autostrut=Root|Heaviest|Grandparent: strut every part before anything is sampled, to see how much of the
            // editor/flight voxel difference is the airframe flexing; the vessel is voxelized again so the runway
            // sample sees the strutted pose
            string autostrut = ctx.Param("autostrut");
            if (autostrut != null)
            {
                var mode = (Part.AutoStrutMode)Enum.Parse(typeof(Part.AutoStrutMode), autostrut, true);
                int n = 0;
                foreach (Part p in FlightGlobals.ActiveVessel.parts)
                {
                    if (p.autoStrutMode == mode) continue;
                    p.autoStrutMode = mode;
                    p.UpdateAutoStrut();
                    ++n;
                }
                ctx.Record(prefix + "autostrut", $"{mode} on {n} parts");
                yield return Wait.Seconds(3);
                FarApi.PartAreas(FlightGlobals.ActiveVessel.parts, null, null, out double before, out _);
                FarApi.RequestRevoxel(FlightGlobals.ActiveVessel);
                // a strutted airframe that did not move leaves the areas as they were; that is a result, not a failure
                double after = before; float t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < 30 && Math.Abs(after - before) < 1e-6) { yield return null; FarApi.PartAreas(FlightGlobals.ActiveVessel.parts, null, null, out after, out _); }
                ctx.Record(prefix + "autostrutRevoxelChangedArea", Math.Round(after - before, 3));
                yield return Wait.Seconds(6);
            }

            ctx.Step("Runway sample");
            yield return Sample("runway", "down", "-");

            ctx.Step("Take off and climb");
            double rotateSpeed = ctx.ParamDouble("rotateSpeed", 60);
            double startAlt = Flight.Altitude;
            Ctrl.Stage();
            Ctrl.SetThrottle(1f);
            Ctrl.Brakes(false);
            Mj.SmartAssSurface(heading, pitch: 0);   // level on the runway until rotation speed: a heavy plane pitched from a standstill drags a wingtip
            yield return Wait.Until(() => { Flight.AssertAlive(); return Flight.SurfaceSpeed > rotateSpeed; }, 120, $"surface speed > {rotateSpeed} m/s");
            Mj.SmartAssSurface(heading, pitch: 8);
            yield return Wait.Until(() => { Flight.AssertAlive(); return Flight.Altitude - startAlt > climbTo; }, 240, "climb to the sampling altitude");

            ctx.Step("Gear and elevator changes");
            foreach (string ps in pitches)
            {
                double pitch = double.Parse(ps.Trim(), inv);
                Mj.SmartAssSurface(heading, pitch: pitch);
                yield return Wait.Seconds(12);   // let the attitude and the trim deflection settle
                yield return Revoxel(false);   // the gear starts down: up first, then down, so each toggle animates
                yield return Sample($"p{pitch:0}-gearUp", "up", ps.Trim());
                yield return Revoxel(true);
                yield return Sample($"p{pitch:0}-gearDown", "down", ps.Trim());
            }
            Mj.SmartAssOff();
        }

        /// <summary>
        /// Part heating through an atmospheric entry, for comparing FAR builds: the plane is put on an entry orbit
        /// with the stock set-orbit cheat, holds an angle of attack on MechJeb, and every part's skin and interior
        /// temperature, convective flux, stock exposed area and FAR voxel area are logged through the entry (parts
        /// over their limit are kept by the ignore-max-temperature cheat). Params: craft (default Aeris 4A), peKm
        /// (default 30), apKm (default 90), aoa (deg, default 10), gear (up|down, default up), startAltKm (default
        /// 69, sampling starts below it), stopAltKm (default 20), stopSpeed (m/s, default 500), physWarp (default 2),
        /// sampleSec (default 0.5), rcs (on|off, default on), holdBandDeg (default 3) and holdMaxOutside (fraction of
        /// samples below 60 km allowed outside the band, default 0.05): a run whose attitude hold leaves the band more
        /// than that is reported as failed, since its heating is not comparable.
        /// </summary>
        [UITest("Far_Heating_Flight", Requires = "MechJeb", StartsAt = StartState.Flight, Craft = "Aeris 4A", CraftDir = "SPH", LaunchSite = "Runway", TimeoutSec = 1800)]
        public static IEnumerator Far_Heating_Flight(TestContext ctx)
        {
            Assert.True(Mj.Present, "vessel has a MechJebCore (MechJebForAll or an MJ part)");
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            double peKm = ctx.ParamDouble("peKm", 30), apKm = ctx.ParamDouble("apKm", 90), aoa = ctx.ParamDouble("aoa", 10);
            bool gearDown = (ctx.Param("gear") ?? "up").Equals("down", StringComparison.OrdinalIgnoreCase);
            double startAlt = ctx.ParamDouble("startAltKm", 69) * 1000, stopAlt = ctx.ParamDouble("stopAltKm", 20) * 1000, stopSpeed = ctx.ParamDouble("stopSpeed", 500);
            double sampleSec = ctx.ParamDouble("sampleSec", 0.5);
            var v = FlightGlobals.ActiveVessel;
            Flight.IgnoreMaxTemperature(true);

            ctx.Step("Set the entry orbit");
            Flight.SetOrbit(peKm * 1000, apKm * 1000);
            yield return Wait.Until(() => FlightGlobals.ActiveVessel.altitude > apKm * 1000 * 0.9, 30, "vessel at the apoapsis");
            yield return Wait.Seconds(2);
            v = FlightGlobals.ActiveVessel;
            ctx.Record("apoapsisKm", Math.Round(v.orbit.ApA / 1000, 1)); ctx.Record("periapsisKm", Math.Round(v.orbit.PeA / 1000, 1));
            Ctrl.Gear(gearDown);
            v.ActionGroups.SetGroup(KSPActionGroup.RCS, !(ctx.Param("rcs") ?? "on").Equals("off", StringComparison.OrdinalIgnoreCase));
            Mj.SmartAssSurfaceProgradeOffset(aoa);
            yield return Wait.Seconds(5);
            yield return Wait.Until(() => !Ctrl.GearStates().Contains("ing"), 30, "gear finished moving", shot: false);
            ctx.Record("gearStates", Ctrl.GearStates());

            ctx.Step("Warp to the atmosphere");
            Flight.Warp(3);
            yield return Wait.Until(() => FlightGlobals.ActiveVessel.altitude < startAlt + 8000, 900, "approach the atmosphere", shot: false);
            Flight.WarpOff();
            yield return Wait.Until(() => TimeWarp.CurrentRate <= 1.01, 30, "off rails", shot: false);
            Mj.SmartAssSurfaceProgradeOffset(aoa);
            Flight.PhysicsWarpRate(ctx.ParamDouble("physWarp", 2));

            ctx.Step("Entry");
            var rows = new System.Text.StringBuilder("t,altitude,srfSpeed,mach,dynPreskPa,aoa,part,persistentId,skinTemp,temp,convFluxKW,skinExposedArea,exposedArea,radiativeArea,farArea\n");
            var peakSkin = new Dictionary<string, double>(); var peakInt = new Dictionary<string, double>(); var partArea = new Dictionary<string, double>();
            double t0 = Planetarium.GetUniversalTime(), nextSample = 0, vesselPeakSkin = 0; int samples = 0;
            double holdBand = ctx.ParamDouble("holdBandDeg", 3), holdMaxOutside = ctx.ParamDouble("holdMaxOutside", 0.05);
            int holdSamples = 0, holdOutside = 0; double aoaSum = 0, aoaSq = 0, aoaMin = double.PositiveInfinity, aoaMax = double.NegativeInfinity;
            yield return Wait.Until(() =>
            {
                var av = FlightGlobals.ActiveVessel;
                if (av == null || av.state == Vessel.State.DEAD) return true;
                double t = Planetarium.GetUniversalTime() - t0;
                if (av.altitude < startAlt && t >= nextSample)
                {
                    nextSample = t + sampleSec; ++samples;
                    double a = Flight.AoA;
                    if (av.altitude < 60000 && !double.IsNaN(a))
                    {
                        ++holdSamples; aoaSum += a; aoaSq += a * a; aoaMin = Math.Min(aoaMin, a); aoaMax = Math.Max(aoaMax, a);
                        if (Math.Abs(a - aoa) > holdBand) ++holdOutside;
                    }
                    string head = string.Join(",", new[] { t.ToString("F1", inv), av.altitude.ToString("F0", inv), av.srfSpeed.ToString("F1", inv), av.mach.ToString("F3", inv), av.dynamicPressurekPa.ToString("F3", inv), Flight.AoA.ToString("F2", inv) });
                    foreach (Part p in av.parts)
                    {
                        string key = (p.partInfo != null ? p.partInfo.name : p.name) + "#" + p.persistentId;
                        double fa = FarApi.PartArea(p);
                        rows.Append(head).Append(',').Append(p.partInfo != null ? p.partInfo.name : p.name).Append(',').Append(p.persistentId).Append(',')
                            .Append(p.skinTemperature.ToString("F1", inv)).Append(',').Append(p.temperature.ToString("F1", inv)).Append(',')
                            .Append((p.thermalConvectionFlux).ToString("F3", inv)).Append(',').Append(p.skinExposedArea.ToString("F3", inv)).Append(',')
                            .Append(p.exposedArea.ToString("F3", inv)).Append(',').Append(p.radiativeArea.ToString("F3", inv)).Append(',').Append(fa.ToString("F3", inv)).Append('\n');
                        if (!peakSkin.ContainsKey(key) || p.skinTemperature > peakSkin[key]) peakSkin[key] = p.skinTemperature;
                        if (!peakInt.ContainsKey(key) || p.temperature > peakInt[key]) peakInt[key] = p.temperature;
                        if (!partArea.ContainsKey(key)) partArea[key] = fa;
                        if (p.skinTemperature > vesselPeakSkin) vesselPeakSkin = p.skinTemperature;
                    }
                }
                return av.altitude < stopAlt || (av.altitude < startAlt && av.srfSpeed < stopSpeed);
            }, 1500, "entry to the stop altitude or speed", shot: false);
            Flight.WarpOff();
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "heating-" + (gearDown ? "gearDown" : "gearUp") + ".csv"), rows.ToString());
            ctx.Record("samples", samples);
            ctx.Record("vesselPeakSkinTemp", Math.Round(vesselPeakSkin, 1));
            ctx.Record("alive", !Flight.Dead);
            bool holdOk = holdSamples > 0 && holdOutside <= holdMaxOutside * holdSamples;
            if (holdSamples > 0)
            {
                double mean = aoaSum / holdSamples, sd = Math.Sqrt(Math.Max(0, aoaSq / holdSamples - mean * mean));
                ctx.Record("aoaMean", Math.Round(mean, 2)); ctx.Record("aoaSd", Math.Round(sd, 2)); ctx.Record("aoaMin", Math.Round(aoaMin, 1)); ctx.Record("aoaMax", Math.Round(aoaMax, 1));
                ctx.Record("aoaOutsideBandFraction", Math.Round((double)holdOutside / holdSamples, 3));
            }
            ctx.Record("holdOk", holdOk);
            foreach (var kv in peakSkin) { ctx.Record(kv.Key + ".peakSkin", Math.Round(kv.Value, 1)); ctx.Record(kv.Key + ".peakInt", Math.Round(peakInt[kv.Key], 1)); ctx.Record(kv.Key + ".farArea", Math.Round(partArea[kv.Key], 3)); }
            try { Mj.SmartAssOff(); } catch (UITestFailure) { }
            Assert.True(holdOk, $"attitude hold stayed within {holdBand} deg of {aoa} deg AoA below 60 km ({holdOutside} of {holdSamples} samples outside, min {aoaMin:F1} max {aoaMax:F1})");
        }

        [UITest("Mj_AscentToOrbit", Requires = "MechJeb", StartsAt = StartState.Flight, Craft = "UITest_Rocket", TimeoutSec = 1500)]
        public static IEnumerator Mj_AscentToOrbit(TestContext ctx)
        {
            double altKm = ctx.ParamDouble("orbitAltKm", 100);
            double inc = ctx.ParamDouble("inclination", 0);
            AscentType? type = null;
            if (ctx.Param("ascentType") != null) type = (AscentType)System.Enum.Parse(typeof(AscentType), ctx.Param("ascentType"), true);
            Assert.True(Mj.Present, "vessel has a MechJebCore (MechJebForAll or an MJ part)");

            ctx.Step("Engage ascent guidance");
            var s = Mj.Core.AscentSettings;
            if (type == AscentType.PSG)
            {
                // PSG flies to DesiredApoapsis too, and MJ only sets OptimizeStageFlag while its PSG settings window is drawn
                s.DesiredApoapsis.Val = altKm * 1000;
                s.OptimizeStageFlag = true;
            }
            if (ctx.Param("lastStage") != null) s.LastStage.Val = (int)ctx.ParamDouble("lastStage", -1);
            Mj.Ascent(altKm * 1000, inc, type);   // also stages the rocket off the pad
            yield return Wait.Until(() => Flight.Altitude > 1000, 180, "liftoff (altitude > 1 km)");
            if (ctx.Param("warp") != null) Flight.PhysicsWarp(int.Parse(ctx.Param("warp")));   // only once flying: rails warp on the pad locks staging
            if (ctx.ParamDouble("hyper", 0) > 1 && !Flight.HyperWarp(ctx.ParamDouble("hyper", 0), 1))
                Log.Warn("hyper-warp refused (TimeControl missing?), flying the ascent at 1x");
            yield return Snap.Shot(ctx, "liftoff");

            ctx.Step("Wait for orbit");
            yield return Mj.WaitAscentDone(altKm * 1000, tolM: ctx.ParamDouble("tolKm", 10) * 1000, timeoutSec: ctx.ParamDouble("ascentTimeout", 1200));
            Flight.HyperWarpOff();
            Flight.WarpOff();
            ctx.Record("apoapsisKm", System.Math.Round(Flight.Apoapsis / 1000, 1));
            ctx.Record("periapsisKm", System.Math.Round(Flight.Periapsis / 1000, 1));
            ctx.Record("inclination", System.Math.Round(Flight.Inclination, 2));
            yield return Snap.Shot(ctx, "orbit");
            Assert.True(Flight.Periapsis > 0, "periapsis above the surface");
            Assert.Near(inc, Flight.Inclination, 1.0, "inclination");
            Mj.AscentOff();
        }

        [UITest("Mj_LandAtLaunchSite", Requires = "MechJeb", StartsAt = StartState.Flight, Craft = "UITest_Rocket", TimeoutSec = 3600)]
        public static IEnumerator Mj_LandAtLaunchSite(TestContext ctx)
        {
            double altKm = ctx.ParamDouble("orbitAltKm", 100);
            double inc = ctx.ParamDouble("inclination", 0);
            Assert.True(Mj.Present, "vessel has a MechJebCore (MechJebForAll or an MJ part)");
            double lat = FlightGlobals.ActiveVessel.latitude;
            double lon = FlightGlobals.ActiveVessel.longitude;
            var body = FlightGlobals.ActiveVessel.mainBody;

            ctx.Step("Ascent");
            Mj.Ascent(altKm * 1000, inc);
            yield return Wait.Until(() => Flight.Altitude > 1000, 180, "liftoff (altitude > 1 km)");
            if (ctx.Param("warp") != null) Flight.PhysicsWarp(int.Parse(ctx.Param("warp")));
            yield return Mj.WaitAscentDone(altKm * 1000, timeoutSec: ctx.ParamDouble("ascentTimeout", 1200));
            Mj.AscentOff();

            ctx.Step("Landing guidance to the launch site");
            Mj.LandAt(body, lat, lon);
            yield return Mj.WaitLanded(ctx.ParamDouble("landingTimeout", 3000));
            Flight.WarpOff();
            double dist = Mj.DistanceTo(lat, lon);
            ctx.Record("distanceToTargetM", System.Math.Round(dist));
            yield return Snap.Shot(ctx, "landed");
            Assert.True(dist < ctx.ParamDouble("maxDistanceM", 5000), $"landed within {ctx.ParamDouble("maxDistanceM", 5000)} m of the target ({dist:F0} m)");
        }
    }
}
