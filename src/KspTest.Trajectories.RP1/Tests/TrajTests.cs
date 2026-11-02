using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using KspTest;
using KspTest.MechJeb;
using KspTest.RP1;

namespace KspTest.Tests.Trajectories.RP1
{
    /// <summary>
    /// Trajectories' prediction accuracy through a constant-angle-of-attack entry, with its force cache on and
    /// off. The vehicle holds one AoA with SmartASS and all four descent profile nodes are set to that same
    /// angle in AoA mode, so the profile's altitude blending is a no-op and the predictor is asked for exactly
    /// the entry being flown. A correct prediction would then sit still; how far the predicted impact point
    /// wanders as the entry progresses is the error the pilot feels as "the target keeps moving".
    ///
    /// Two things are measured at every sample:
    ///  - the predicted impact point, so its drift can be compared between the cached and uncached passes;
    ///  - the aerodynamic force both ways at the state actually being flown (AeroDynamicModel.GetForces, which
    ///    the prediction integrates, against ComputeForces, which calls FAR for that exact state). Their
    ///    difference is the cache's interpolation error, measured directly rather than inferred from the drift.
    ///    In the cache-off pass the two are the same call, so those columns read zero and act as the control.
    ///
    /// Params: craft, aoa, peKm, apKm, startAltKm, endAltKm, sampleSec, cache (on+off / on / off),
    /// physWarp, simUT, incDeg, propellant, flightTimeoutSec.
    /// </summary>
    public static class TrajTests
    {
        [UITest(Requires = "RP-1,MechJeb", StartsAt = StartState.Vab, Craft = "Mk2 SSTO Big", TimeoutSec = 6000)]
        public static IEnumerator Traj_CacheAB(TestContext ctx)
        {
            Ugui.DismissPopups();
            Assert.True(TrajApi.Available, "Trajectories is installed and its API resolved");
            ctx.Record("trajectoriesVersion", TrajApi.Version);

            string which = ctx.Param("cache", "on+off").ToLowerInvariant();
            if (which.Contains("on")) yield return RunPass(ctx, true);
            if (which.Contains("off")) yield return RunPass(ctx, false);
        }

        /// <summary>
        /// A whole RSS entry to the ground from a KCT reentry simulation (tanks empty by default: propellant=0), flying
        /// a Trajectories descent profile that Trajectories holds itself through SmartASS; every predicted impact is
        /// scored against where the vessel really comes down. Params: craft (Mk2 SSTO Big), profile (default
        /// 40/40/10/0h: the SSTO's entry angle, down to 10 deg through the low band, level with the horizon to
        /// finish), peKm, apKm, startAltKm, simUT, incDeg, propellant, physWarp, plus TrajProfileFlight's.
        /// </summary>
        [UITest(Requires = "RP-1,MechJeb", StartsAt = StartState.Vab, Craft = "Mk2 SSTO Big", TimeoutSec = 7200)]
        public static IEnumerator Traj_RP1_ProfileToGround(TestContext ctx) => RssProfileToGround(ctx, 0);

        /// <summary>
        /// Traj_RP1_ProfileToGround with the Descent page's "Adjust AoA to stay on target": at steerAtKm (default 100)
        /// the Target page's current-impact button sets the target and the checkbox goes on.
        /// </summary>
        [UITest(Requires = "RP-1,MechJeb", StartsAt = StartState.Vab, Craft = "Mk2 SSTO Big", TimeoutSec = 7200)]
        public static IEnumerator Traj_RP1_SteerToTarget(TestContext ctx) => RssProfileToGround(ctx, ctx.ParamDouble("steerAtKm", 100));

        /// <summary>
        /// An SSTO entry aimed at KSC and steered there. Starts the same reentry simulation, sets the Trajectories
        /// target targetWestKm (default 3) west of KSC on its latitude, then re-places the vessel on its entry orbit
        /// (as HyperEdit would: same apsides, descending through startAltKm) with the orbit turned until the predicted
        /// impact lands on the target: inclination just above KSC's latitude so the ground track tops out there
        /// heading about east (runway 09), argument of periapsis so the impact falls near that top, then node
        /// longitude and inclination by secant on the predicted miss. Then flies to the ground steering to the target.
        /// Params: aimTolKm (10), glideArcDeg (140), handoffAltKm (0 = fly to the ground; above it, stop there and
        /// leave the hold and steering on for a hand landing - run with -Keep), plus Traj_RP1_ProfileToGround's;
        /// profile defaults to 35/35/10/0h (at 40 deg the SSTO's range swung by 50-100 km per 2 deg; 5 deg low
        /// brought it in too fast).
        /// </summary>
        [UITest(Requires = "RP-1,MechJeb", StartsAt = StartState.Vab, Craft = "Mk2 SSTO Big", TimeoutSec = 7200)]
        public static IEnumerator Traj_RP1_KscApproach(TestContext ctx) =>
            KscEntry(ctx, new KscSpec { Craft = "Mk2 SSTO Big", Profile = "35/35/10/0h", PeKm = 70, TargetWestKm = 3, GlideArcDeg = 140, RcsOffBelowKm = 40 });

        /// <summary>
        /// A capsule (Mk1-3 Test: the Apollo-like pod on its heat shield) aimed at KSC itself and steered there by
        /// bank, as a capsule is flown. RO's descent mode shifts the center of mass toward the roof so the pod trims
        /// heat shield first with the air on its roof side, which in Trajectories' terms is -170 (nose below
        /// retrograde, roof down, as the prediction has it; the hold turns SmartASS's roof-up retrograde attitude
        /// over to match). The profile follows the trim the pod settled at in flight (about -158 near 100 km, which
        /// the blend from -142 at 140 km to -170 at 70 km passes through, -170 from 70 to about 33 km (a -165 low node
        /// fitted the transonic dip to -155 instead and cost 20 km of range), -172 low), since RCS cannot hold it
        /// off its trim and a profile it does not fly is a wrong prediction. Its lift points toward its roof, so down
        /// unbanked: the steering finds that from the model and banks from lift up, starting 60 deg off it, and is on
        /// before the aim so the aim is made with that bank.
        /// No parachute: it is the impact point that is measured. Params: comOffset (descent mode's CoM offset
        /// limit, 0.33 = the 10 deg trim of the reentry sweeps), plus Traj_RP1_KscApproach's; defaults Pe 35 km (45
        /// glided 4000 km on the edge of skipping), target 0 km west, glideArcDeg 40.
        /// </summary>
        [UITest(Requires = "RP-1,MechJeb", StartsAt = StartState.Vab, Craft = "Mk1-3 Test", TimeoutSec = 7200)]
        public static IEnumerator Traj_RP1_KscCapsule(TestContext ctx) =>
            KscEntry(ctx, new KscSpec { Craft = "Mk1-3 Test", Profile = "-142/-170/-170/-172", PeKm = 35, TargetWestKm = 0, GlideArcDeg = 40,
                                        ComOffset = 0.33, Attitude = "Retrograde", SteerDuringAim = true });

        /// <summary>
        /// The capsule's KSC entry flown open loop, to check the prediction on its own: no steering, the profile
        /// held and a fixed bank (bankDeg, default 0 = lift down for this pod; 120 = the steering's starting 60 deg
        /// off lift up), with the whole predicted path written at 130/110/90/70/50/30 km (prediction_&lt;km&gt;.csv) and
        /// the flown path every second (flown_path.csv), to see where prediction and flight part. Params as
        /// Traj_RP1_KscCapsule's, plus bankDeg and dumpAtKm ('+'-separated).
        /// </summary>
        [UITest(Requires = "RP-1,MechJeb", StartsAt = StartState.Vab, Craft = "Mk1-3 Test", TimeoutSec = 7200)]
        public static IEnumerator Traj_RP1_CapsuleOpenLoop(TestContext ctx) =>
            KscEntry(ctx, new KscSpec { Craft = "Mk1-3 Test", Profile = "-142/-170/-170/-172", PeKm = 35, TargetWestKm = 0, GlideArcDeg = 40,
                                        ComOffset = 0.33, Attitude = "Retrograde", OpenLoop = true });

        /// <summary>
        /// The capsule's KSC entry flown to a hand-set Trajectories capsule plan, with no steering: one trim angle
        /// (trimDeg, default the profile's high node) until the air sets it, the bank planBankDeg (how far the lift is
        /// off up, + right, default 60) and reversals at rev1Km and rev2Km (default 49 and none; 0 for none). Params
        /// as Traj_RP1_KscCapsule's.
        /// </summary>
        [UITest(Requires = "RP-1,MechJeb", StartsAt = StartState.Vab, Craft = "Mk1-3 Test", TimeoutSec = 7200)]
        public static IEnumerator Traj_RP1_CapsulePlan(TestContext ctx) =>
            KscEntry(ctx, new KscSpec { Craft = "Mk1-3 Test", Profile = "-142/-170/-170/-172", PeKm = 35, TargetWestKm = 0, GlideArcDeg = 40,
                                        ComOffset = 0.33, Attitude = "Retrograde", Plan = true });

        /// <summary>
        /// How much the predicted impact moves with time warp, with nothing else changing. The vessel (default the
        /// SSTO, Traj_RP1_KscApproach's entry; craft=Mk1-3 Test with comOffset for the capsule) is put on the KSC entry
        /// orbit at apoapsis (apKm, default 300 so the schedule ends well above the atmosphere) and held at its profile
        /// attitude with no steering. In orbit, on one Kepler arc, the impact should not move at all: 1x, physics warp
        /// 2x and 4x, rails warp 5x and 50x, back to 1x after each, then an explicit aerodynamic model rebuild. Then,
        /// with atmosphere=on (default), it coasts in and at probeAltKm (default 60) alternates 1x and physics warp.
        /// Every published prediction goes to warp_probe.csv with the warp rate, whether on rails, and which model
        /// instance made it (a new id is a rebuilt model).
        /// </summary>
        [UITest(Requires = "RP-1,MechJeb", StartsAt = StartState.Vab, Craft = "Mk2 SSTO Big", TimeoutSec = 7200)]
        public static IEnumerator Traj_RP1_WarpShift(TestContext ctx) =>
            KscEntry(ctx, new KscSpec { Craft = "Mk2 SSTO Big", Profile = "35/35/10/0h", PeKm = 70, ApKm = 300, TargetWestKm = 3, GlideArcDeg = 140,
                                        WarpProbe = true });

        // Samples every newly published prediction's impact while a warp schedule runs.
        private sealed class WarpLog
        {
            private readonly StringBuilder csv = new StringBuilder(
                "phase,realSec,missionTime,altKm,warpRate,rails,completed,impactLat,impactLon,eastKm,northKm,modelId,otherTorqueKNm,liveMs,peKm,apKm,rcs\n");
            private readonly double tLat, tLon, rKm;
            private int lastCount = -1;
            private readonly float start = UnityEngine.Time.realtimeSinceStartup;
            private static Type trajectoryType;
            internal string Phase = "";

            internal WarpLog(CelestialBody body, double tLat, double tLon)
            {
                this.tLat = tLat;
                this.tLon = tLon;
                rKm = body.Radius / 1000;
            }

            private static int ModelId()
            {
                if (trajectoryType == null)
                    foreach (var a in AssemblyLoader.loadedAssemblies)
                        if ((trajectoryType = a.assembly.GetType("Trajectories.Trajectory", false)) != null)
                            break;
                var f = trajectoryType?.GetField("aerodynamicModel_", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                object m = f?.GetValue(null);
                return m == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(m);
            }

            internal void Sample()
            {
                int n = TrajApi.CompletedPredictions;
                if (n == lastCount)
                    return;
                lastCount = n;
                var v = FlightGlobals.ActiveVessel;
                if (v == null || !TrajApi.ImpactLatLon(out double la, out double lo, out _))
                    return;
                double dLon = tLon - lo;
                while (dLon > 180) dLon -= 360;
                while (dLon < -180) dLon += 360;
                double east = -dLon * Math.PI / 180 * rKm * Math.Cos(tLat * Math.PI / 180);   // impact east of the target
                double north = (la - tLat) * Math.PI / 180 * rKm;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                csv.AppendLine(string.Join(",", Phase, (UnityEngine.Time.realtimeSinceStartup - start).ToString("F1", inv), v.missionTime.ToString("F1", inv),
                    (v.altitude / 1000).ToString("F2", inv), TimeWarp.CurrentRate.ToString("F2", inv), TimeWarp.fetch.Mode == TimeWarp.Modes.HIGH && TimeWarp.CurrentRateIndex > 0 ? "1" : "0",
                    n.ToString(inv), la.ToString("F5", inv), lo.ToString("F5", inv), east.ToString("F2", inv), north.ToString("F2", inv),
                    ModelId().ToString(inv), TrajApi.OtherPitchTorque.ToString("F3", inv), TrajApi.ComputationTimeMs.ToString("F1", inv),
                    (v.orbit.PeA / 1000).ToString("F4", inv), (v.orbit.ApA / 1000).ToString("F4", inv),
                    v.ActionGroups[KSPActionGroup.RCS] ? "1" : "0"));
            }

            // Samples for realSec real seconds.
            internal IEnumerator Hold(string phase, double realSec)
            {
                Phase = phase;
                Log.Info("warp probe: " + phase);
                float end = UnityEngine.Time.realtimeSinceStartup + (float)realSec;
                while (UnityEngine.Time.realtimeSinceStartup < end)
                {
                    Flight.AssertAlive();
                    Sample();
                    yield return null;
                }
            }

            internal void Write(TestContext ctx)
            {
                Directory.CreateDirectory(ctx.ResultsDir);
                File.WriteAllText(Path.Combine(ctx.ResultsDir, "warp_probe.csv"), csv.ToString());
            }
        }

        private static IEnumerator WaitBackTo1x(WarpLog log, string phase)
        {
            Flight.WarpOff();
            log.Phase = phase + "-settling";
            float end = UnityEngine.Time.realtimeSinceStartup + 60;
            while ((TimeWarp.CurrentRate > 1.001f || FlightGlobals.ActiveVessel.packed) && UnityEngine.Time.realtimeSinceStartup < end)
            {
                log.Sample();
                yield return null;
            }
        }

        private static IEnumerator WarpProbe(TestContext ctx, KscSpec spec, TrajProfileFlight.Plan plan, CelestialBody body, double tLat, double tLon,
                                             double startAltKm)
        {
            var log = new WarpLog(body, tLat, tLon);
            double hold = ctx.ParamDouble("holdSec", 20);
            // rcs=off: RCS off for the probe, to see whether RCS moving the vessel is what moves the impact
            if (!ctx.ParamBool("rcs", true))
                FlightGlobals.ActiveVessel.ActionGroups.SetGroup(KSPActionGroup.RCS, false);
            try
            {
                ctx.Step("Warp probe in orbit: 1x, physics 2x/4x, rails 5x/50x, model rebuild");
                yield return log.Hold("orbit-1x", hold);
                yield return log.Hold("orbit-1x-again", hold);
                foreach (double rate in new[] { 2.0, 4.0 })
                {
                    Flight.PhysicsWarpRate(rate);
                    yield return log.Hold($"orbit-phys{rate:F0}x", hold);
                    yield return WaitBackTo1x(log, $"orbit-phys{rate:F0}x");
                    yield return log.Hold($"orbit-after-phys{rate:F0}x", hold);
                }
                foreach (int index in new[] { 1, 3 })
                {
                    Flight.Warp(index);
                    string name = $"orbit-rails{TimeWarp.fetch.warpRates[index]:F0}x";
                    yield return log.Hold(name, index == 1 ? hold : hold / 4);
                    yield return WaitBackTo1x(log, name);
                    yield return log.Hold("orbit-after-" + name.Substring(6), hold);
                }
                TrajApi.InvalidateAerodynamicModel();
                yield return log.Hold("orbit-after-rebuild", hold);
                ctx.Record("probeOrbitEndAltKm", Math.Round(FlightGlobals.ActiveVessel.altitude / 1000, 1));

                if (!ctx.ParamBool("atmosphere", true))
                    yield break;
                double probeAltKm = ctx.ParamDouble("probeAltKm", 60);
                ctx.Step($"Coast in, then the warp probe at {probeAltKm} km");
                if (FlightGlobals.ActiveVessel.altitude > startAltKm * 1000 + 1000)
                {
                    Flight.Warp(3);
                    yield return Wait.Until(() => FlightGlobals.ActiveVessel.altitude < startAltKm * 1000 + 1000, 1800, "coast to the atmosphere", shot: false);
                    yield return WaitBackTo1x(log, "coast");
                }
                Flight.PointAtAoA(plan.Entry);
                yield return Wait.Seconds(1);
                Flight.PhysicsWarpRate(4);
                log.Phase = "descent-4x";
                float limit = UnityEngine.Time.realtimeSinceStartup + 3600;
                while (FlightGlobals.ActiveVessel.altitude > probeAltKm * 1000 && UnityEngine.Time.realtimeSinceStartup < limit)
                {
                    Flight.AssertAlive();
                    log.Sample();
                    yield return null;
                }
                yield return WaitBackTo1x(log, "descent");
                foreach (double rate in new[] { 1.0, 4.0, 1.0, 2.0, 1.0, 4.0, 1.0 })
                {
                    if (rate > 1) Flight.PhysicsWarpRate(rate);
                    yield return log.Hold($"atm-{rate:F0}x", hold);
                    if (rate > 1) yield return WaitBackTo1x(log, $"atm-{rate:F0}x");
                }
                ctx.Record("probeAtmEndAltKm", Math.Round(FlightGlobals.ActiveVessel.altitude / 1000, 1));
            }
            finally
            {
                Flight.WarpOff();
                log.Write(ctx);
            }
        }

        private sealed class KscSpec
        {
            public string Craft, Profile, Attitude = "Prograde";
            public double PeKm, TargetWestKm, GlideArcDeg;
            public double ApKm = 150;
            public bool WarpProbe;  // put on the entry orbit, then the warp probe instead of the entry (Traj_RP1_WarpShift)
            public double ComOffset = double.NaN;   // descent mode's CoM offset limit, for a capsule
            public bool SteerDuringAim;
            public bool OpenLoop;   // no steering: a fixed bank (bankDeg), and the predicted and flown paths written out
            public bool Plan;       // no steering: the capsule plan's bank and reversals (planBankDeg, rev1Km, rev2Km)
            // RCS off once the vessel descends below this altitude (0: never): an SSTO has the air for attitude down
            // there, and its RCS there only burns propellant (rcsOffBelowKm overrides).
            public double RcsOffBelowKm;
        }

        /// <summary>
        /// The descent profile's node altitudes, kept per body: this body's defaults (fractions of its atmosphere), then
        /// the home body's set to altCheckHomeKm (Earth's 140/35/25/1 by default) and this body's read again (no
        /// change), the saved text read back in (no change), this body's low node moved to altCheckLowKm, and what the
        /// vessel saves. This body's low node is put back after, the home body's left set, so the flight that follows
        /// (nodeAltKm) runs with another body's altitudes present.
        /// </summary>
        private static bool SetTrue(System.Reflection.FieldInfo f)
        {
            f.SetValue(null, true);
            return true;
        }

        private static void NodeAltitudeCheck(TestContext ctx, string k, CelestialBody body)
        {
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Type t = TrajApi.TryFindTypePublic("Trajectories.DescentProfile");
            var get = t?.GetMethod("NodeAltitudeM", any, null, new[] { typeof(CelestialBody), typeof(int) }, null);
            var set = t?.GetMethod("SetNodeAltitudeM", any);
            var load = t?.GetMethod("LoadAltitudes", any, null, new[] { typeof(string) }, null);
            var text = t?.GetProperty("NodeAltitudesText", any);
            var save = t?.GetMethod("Save", any, null, Type.EmptyTypes, null);
            Assert.True(get != null && set != null && load != null && text != null && save != null, "Trajectories keeps node altitudes per body");
            CelestialBody home = FlightGlobals.GetHomeBody();
            Func<CelestialBody, string> nodes = b => string.Join("/", new[] { 0, 1, 2, 3 }.Select(i =>
                (Convert.ToDouble(get.Invoke(null, new object[] { b, i })) / 1000).ToString("0.##", inv)));
            Func<CelestialBody, int, double, bool> setKm = (b, row, km) => (bool)set.Invoke(null, new object[] { b, row, km * 1000 });

            ctx.Record(k + "altDefaultKm_" + body.name, nodes(body));
            double[] homeKm = Array.ConvertAll((ctx.Param("altCheckHomeKm", "140/35/25/1") ?? "").Split('/'), s => double.Parse(s, inv));
            var homeSet = new string[4];
            for (int i = 3; i >= 0; --i)
                homeSet[i] = setKm(home, i, homeKm[i]) ? "ok" : "refused";
            ctx.Record(k + "altHomeSet_" + home.name, string.Join("/", homeSet) + " -> " + nodes(home));
            ctx.Record(k + "altAfterHomeKm_" + body.name, nodes(body));
            string saved = (string)text.GetValue(null, null);
            ctx.Record(k + "altText", saved);
            load.Invoke(null, new object[] { saved });
            ctx.Record(k + "altReloadedKm", home.name + " " + nodes(home) + ", " + body.name + " " + nodes(body));
            double lowKm = ctx.ParamDouble("altCheckLowKm", Math.Round(body.atmosphereDepth / 1000 * 0.2, 1));
            ctx.Record(k + "altSetLow_" + body.name, (setKm(body, 2, lowKm) ? "ok" : "refused") + " -> " + nodes(body) + " (home " + nodes(home) + ")");
            save.Invoke(null, null);
            var module = FlightGlobals.ActiveVessel.Parts.SelectMany(p => p.Modules.Cast<PartModule>())
                .FirstOrDefault(m => m.GetType().Name == "TrajectoriesVesselSettings");
            ctx.Record(k + "altVesselSaved", module == null ? "no module" : (string)module.GetType().GetField("NodeAltitudes")?.GetValue(module) ?? "no field");
            set.Invoke(null, new object[] { body, 2, double.NaN });
            ctx.Record(k + "altAfterCheckKm", home.name + " " + nodes(home) + ", " + body.name + " " + nodes(body));
        }

        private static IEnumerator KscEntry(TestContext ctx, KscSpec spec)
        {
            Ugui.DismissPopups();
            var plan = TrajProfileFlight.Setup(ctx, spec.Profile);
            string k = plan.K;
            string craft = ctx.Param("craft", spec.Craft);
            double physWarp = ctx.ParamDouble("physWarp", 4);
            double peKm = ctx.ParamDouble("peKm", spec.PeKm), apKm = ctx.ParamDouble("apKm", spec.ApKm), startAltKm = ctx.ParamDouble("startAltKm", 140);

            ctx.Step($"Open {craft} in the VAB");
            yield return Nav.EnsureState(ctx, StartState.Vab, ctx.CraftPath(craft));
            Ugui.DismissPopups();
            double propellant = ctx.ParamDouble("propellant", 0);
            ctx.Record(k + "propellantDirect", Rp1.ApplyPropellantLoadDirect(propellant));
            // avionics=Deep-Space: the procedural avionics switched to that config (Near-Earth loses control away from
            // home, e.g. body=Mars)
            string avionics = ctx.Param("avionics");
            if (!string.IsNullOrEmpty(avionics))
                ctx.Record(k + "avionicsParts", Rp1.SetAvionicsConfig(avionics) + " -> " + avionics);
            double comOffset = ctx.ParamDouble("comOffset", spec.ComOffset);
            if (!double.IsNaN(comOffset))
            {
                ctx.Record(k + "descentModeShifters", Rp1.SetDescentMode(true));
                ctx.Record(k + "comShiftersSet", Rp1.SetComOffsetLimit(comOffset));
            }

            ctx.Step("Simulate the entry");
            yield return Rp1.ConfigureSim(new SimSpec
            {
                // body=Mars (any body with an atmosphere; the target is at the same latitude and longitude there)
                Body = ctx.Param("body"),
                StartInOrbit = true, Mode = "Reentry",
                PeKm = peKm, Arrival = "Apoapsis", ApKm = apKm, EntryAltKm = startAltKm,
                IncDeg = 0, LanDeg = 0, ArgPeDeg = 0,
                // 8 h later than the other SSTO tests, so the approach to KSC is in daylight.
                UT = ctx.ParamDouble("simUT", 2019828800) > 0 ? ctx.ParamDouble("simUT", 2019828800) : (double?)null,
                Attitude = spec.Attitude, LogTelemetry = false, PropellantPct = propellant,
            });
            yield return Rp1.StartSim();
            Assert.Equal("Reentry", Rp1.SimMode, "SimOrbitMode");
            var v = FlightGlobals.ActiveVessel;
            CelestialBody body = v.mainBody;
            int partsStart = v.parts.Count;
            ctx.Record(k + "body", body.name);
            ctx.Record(k + "atmosphereKm", Math.Round(body.atmosphereDepth / 1000, 1));
            // the physics solver's iterations as the game has them (Unity's defaults and the root part's), and
            // solverIters=N: N position and velocity iterations on every part (SolverIterations keeps them on new parts)
            ctx.Record(k + "solverDefaults", string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Physics {0}/{1}, root rb {2}/{3}", UnityEngine.Physics.defaultSolverIterations, UnityEngine.Physics.defaultSolverVelocityIterations,
                v.rootPart.Rigidbody != null ? v.rootPart.Rigidbody.solverIterations : -1,
                v.rootPart.Rigidbody != null ? v.rootPart.Rigidbody.solverVelocityIterations : -1));
            // foreignAtStart=on: what else the vessel's physics is joined to right after the sim placed it (QueuedTorque.ForeignBodies)
            if (ctx.ParamBool("foreignAtStart", false))
                ctx.Record(k + "foreign_start", QueuedTorque.ForeignBodies(v));
            int solverIters = (int)ctx.ParamDouble("solverIters", 0);
            if (solverIters > 0)
            {
                int set = 0;
                foreach (Part part in v.parts)
                    if (part.Rigidbody != null)
                    {
                        part.Rigidbody.solverIterations = solverIters;
                        part.Rigidbody.solverVelocityIterations = solverIters;
                        ++set;
                    }
                ctx.Record(k + "solverItersSet", set + " parts at " + solverIters);
            }
            // altCheck=on: the descent profile's node altitudes on this body (NodeAltitudeCheck)
            if (ctx.ParamBool("altCheck", false))
                NodeAltitudeCheck(ctx, k, body);
            // runwayWaypoints=on: custom waypoints on the KSC runway and 0.5/1/5/10 km before it on a 3 deg glideslope
            // (Flight.AddRunwayWaypoints), for a hand landing after handoffAltKm.
            if (ctx.ParamBool("runwayWaypoints", false))
            {
                var added = new System.Collections.Generic.List<string>();
                double rwyHeading = Flight.AddRunwayWaypoints(new[] { 0.5, 1, 5, 10 }, 3.0, added);
                ctx.Record(k + "runwayHeadingDeg", Math.Round(rwyHeading, 1));
                for (int i = 0; i < added.Count; i++)
                    ctx.Record(k + "runwayWaypoint" + i, added[i]);
            }
            ctx.Record(k + "massT", Math.Round(v.GetTotalMass(), 3));
            ctx.Record(k + "gearUp", Ctrl.RetractGear());
            Ctrl.SetThrottle(0f);
            yield return Wait.Seconds(5);
            ctx.Record(k + "rcsThrusters", Ctrl.EnableRcs());
            // rcs=off: attitude by the wheels and surfaces only (RCS translates the vessel while holding attitude)
            bool rcsOff = !ctx.ParamBool("rcs", true);
            if (rcsOff)
                FlightGlobals.ActiveVessel.ActionGroups.SetGroup(KSPActionGroup.RCS, false);
            ctx.Record(k + "rcsOff", rcsOff);
            double rcsOffBelowKm = rcsOff ? 0 : ctx.ParamDouble("rcsOffBelowKm", spec.RcsOffBelowKm);
            UnityEngine.GameObject rcsCutoffObject = null;
            // Trajectories' own option where the build has it (DescentProfile.RcsOffBelowM: the prediction leaves the
            // RCS out below, and Trajectories switches it off there); otherwise the harness switches it off itself.
            bool rcsCutoffByTrajectories = TrajApi.SetStaticDouble("Trajectories.DescentProfile", "RcsOffBelowM",
                                                                   rcsOffBelowKm > 0 ? rcsOffBelowKm * 1000 : double.NaN);
            if (rcsOffBelowKm > 0 && !rcsCutoffByTrajectories)
            {
                rcsCutoffObject = new UnityEngine.GameObject("KspTest.RcsCutoff");
                rcsCutoffObject.AddComponent<RcsCutoff>().AltitudeM = rcsOffBelowKm * 1000;
            }
            ctx.Record(k + "rcsOffBelowKm", rcsOffBelowKm);
            ctx.Record(k + "rcsCutoffBy", rcsOffBelowKm > 0 ? (rcsCutoffByTrajectories ? "Trajectories" : "harness") : "none");
            // startProbeKm=125+115+100: predictions with the starting state taken different ways at each (StartStateProbe)
            UnityEngine.GameObject probeObject = null;
            string probeAt = ctx.Param("startProbeKm");
            if (!string.IsNullOrEmpty(probeAt))
            {
                probeObject = new UnityEngine.GameObject("KspTest.StartStateProbe");
                var probe = probeObject.AddComponent<StartStateProbe>();
                probe.Ctx = ctx;
                probe.K = k;
                probe.AltitudesKm = Array.ConvertAll(probeAt.Split('+'), s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture));
            }
            // biasSweepKm=130+100+80: predictions with the AoA bias swept over biasSweepDeg at each (BiasSweepProbe)
            UnityEngine.GameObject sweepObject = null;
            string sweepAt = ctx.Param("biasSweepKm");
            if (!string.IsNullOrEmpty(sweepAt))
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                sweepObject = new UnityEngine.GameObject("KspTest.BiasSweepProbe");
                var sweep = sweepObject.AddComponent<BiasSweepProbe>();
                sweep.Ctx = ctx;
                sweep.K = k;
                sweep.AltitudesKm = Array.ConvertAll(sweepAt.Split('+'), s => double.Parse(s, inv));
                sweep.BiasesDeg = Array.ConvertAll((ctx.Param("biasSweepDeg", "-10,-6,-3,-1,0,1,3,6,10") ?? "0").Split(','), s => double.Parse(s, inv));
            }
            FlightGlobals.ActiveVessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            Flight.IgnoreMaxTemperature(true);
            Flight.InfinitePropellant(true);

            // The target: targetWestKm west of KSC along its parallel, or targetLat/targetLon (deg) anywhere. On the
            // equator (targetLat=0) the orbit is inclined just above it, so the track there runs due east along a great
            // circle and the touchdown's along/across split has no heading to get wrong.
            double kscLat = SpaceCenter.Instance.Latitude, kscLon = SpaceCenter.Instance.Longitude;
            double westKm = ctx.ParamDouble("targetWestKm", spec.TargetWestKm);
            double tLat = ctx.ParamDouble("targetLat", kscLat);
            double tLon = ctx.Param("targetLon") != null
                ? ctx.ParamDouble("targetLon", kscLon)
                : kscLon - westKm * 1000 / (body.Radius * Math.Cos(kscLat * Math.PI / 180)) * 180 / Math.PI;
            Assert.True(TrajApi.SetTargetLatLon(body, tLat, tLon), "target set by latitude/longitude");
            ctx.Record(k + "kscLat", Math.Round(kscLat, 5));
            ctx.Record(k + "kscLon", Math.Round(kscLon, 5));

            yield return TrajProfileFlight.Engage(ctx, plan);
            // pin=on: no autopilot. Every physics frame the vessel is turned to the profile's angle, wings level, by
            // setting its angular velocity (no force, so nothing moves it off its path), so what flies it is the
            // aerodynamics, gravity and the physics step alone: two such flights at different warp levels differ only
            // by the step.
            bool pin = ctx.ParamBool("pin", false);
            UnityEngine.GameObject pinObject = null;
            if (pin)
            {
                TrajApi.HoldWithSmartASS = false;
                Mj.SmartAssOff();
                pinObject = new UnityEngine.GameObject("KspTest.AttitudePin");
                pinObject.AddComponent<AttitudePin>();
            }
            ctx.Record(k + "attitudePinned", pin);
            // wheelKNm=x: a reaction wheel of x kN m per axis added to the root part (no electric charge), a torque
            // source with no net force, to fly without RCS (rcs=off) but with the same control authority
            double wheelKNm = ctx.ParamDouble("wheelKNm", 0);
            if (wheelKNm > 0)
            {
                Part root = FlightGlobals.ActiveVessel.rootPart;
                var wheel = (ModuleReactionWheel)root.AddModule("ModuleReactionWheel", true);
                wheel.PitchTorque = wheel.YawTorque = wheel.RollTorque = (float)wheelKNm;
                wheel.resHandler.inputResources.Clear();
                wheel.OnStart(PartModule.StartState.Flying);
                wheel.wheelState = ModuleReactionWheel.WheelState.Active;
                GameEvents.onVesselWasModified.Fire(FlightGlobals.ActiveVessel);
                ctx.Record(k + "virtualWheelKNm", wheelKNm);
            }
            // trim=off: predictions with the control surfaces neutral (no trim solve), for a flight that does not
            // deflect them (pin=on)
            if (!ctx.ParamBool("trim", true))
                TrajApi.TrimControlSurfaces = false;
            ctx.Record(k + "predictionTrim", TrajApi.TrimControlSurfaces?.ToString() ?? "n/a");
            // clampAoA=off: predictions at the profile's angle even where the controls cannot hold it (builds with
            // AeroDynamicModel.ClampToHoldableAoA; on by default there)
            string clampAoA = ctx.Param("clampAoA");
            if (!string.IsNullOrEmpty(clampAoA))
                TrajApi.SetStaticBool("Trajectories.AeroDynamicModel", "ClampToHoldableAoA", !clampAoA.Equals("off", StringComparison.OrdinalIgnoreCase));
            // learnSide=off: predictions without the side force learned from the flight (AeroDynamicModel.LearnSideForce)
            string learnSide = ctx.Param("learnSide");
            if (!string.IsNullOrEmpty(learnSide))
                TrajApi.SetStaticBool("Trajectories.AeroDynamicModel", "LearnSideForce", !learnSide.Equals("off", StringComparison.OrdinalIgnoreCase));
            // yawTrim=off: predictions without the yaw trim solve (AeroDynamicModel.TrimYaw; on by default there)
            string yawTrim = ctx.Param("yawTrim");
            if (!string.IsNullOrEmpty(yawTrim))
                TrajApi.SetStaticBool("Trajectories.AeroDynamicModel", "TrimYaw", !yawTrim.Equals("off", StringComparison.OrdinalIgnoreCase));
            // logClamps=on: each holdable-angle clamp's state and pitch balance to KSP.log (AeroDynamicModel.LogClamps)
            if (ctx.ParamBool("logClamps", false))
                TrajApi.SetStaticBool("Trajectories.AeroDynamicModel", "LogClamps", true);
            double yawTrimNow = TrajApi.StaticDouble("Trajectories.AeroDynamicModel", "TrimYaw");
            ctx.Record(k + "yawTrim", double.IsNaN(yawTrimNow) ? "n/a" : yawTrimNow != 0 ? "on" : "off");
            double learnNow = TrajApi.StaticDouble("Trajectories.AeroDynamicModel", "LearnSideForce");
            ctx.Record(k + "learnSide", double.IsNaN(learnNow) ? "n/a" : learnNow != 0 ? "on" : "off");
            double clampNow = TrajApi.StaticDouble("Trajectories.AeroDynamicModel", "ClampToHoldableAoA");
            ctx.Record(k + "clampAoA", double.IsNaN(clampNow) ? "n/a" : clampNow != 0 ? "on" : "off");
            // hold=mechjeb: MechJeb's SmartASS alone at the entry angle it was set to (surface prograde + the entry
            // node's AoA), Trajectories' hold off, to tell a MechJeb problem from one in Trajectories' hold.
            bool mechJebOnly = (ctx.Param("hold", "trajectories") ?? "").Equals("mechjeb", StringComparison.OrdinalIgnoreCase);
            if (mechJebOnly && !pin)
                TrajApi.HoldWithSmartASS = false;
            bool harnessHold = (ctx.Param("hold", "trajectories") ?? "").Equals("harness", StringComparison.OrdinalIgnoreCase);
            ctx.Record(k + "holdBy", pin ? "pin" : mechJebOnly ? "MechJeb SmartASS" : harnessHold ? "harness (ProfileHold)" : "Trajectories");
            ctx.Record(k + "trajectoriesVersion", TrajApi.Version);
            // flyTrim=on: fly and predict the capsule's own trim angle, the profile only picking the branch. Off: FAR's
            // prediction API gives this pod a pitching moment 0.1-0.25 kN m off its flown one at Mach 23-27 (where the
            // real one is about zero), which on its flat moment curve puts the trim at -136 against a flown -170.
            bool flyTrim = ctx.ParamBool("flyTrim", false);
            ctx.Record(k + "flyTrimSet", TrajApi.SetStaticBool("Trajectories.DescentProfile", "FlyTrim", flyTrim));
            ctx.Record(k + "flyTrim", flyTrim);
            double trimMaxMach = ctx.ParamDouble("trimMaxMach", double.PositiveInfinity);
            TrajApi.SetStaticDouble("Trajectories.DescentProfile", "FlyTrimMaxMach", trimMaxMach);
            ctx.Record(k + "trimMaxMach", double.IsPositiveInfinity(trimMaxMach) ? (object)"none" : trimMaxMach);
            // fixedBankDeg=0: Trajectories' hold forces SmartASS's roll to that bank (it leaves the roll free otherwise)
            double fixedBank = spec.OpenLoop ? ctx.ParamDouble("bankDeg", 0) : ctx.ParamDouble("fixedBankDeg", double.NaN);
            ctx.Record(k + "fixedBankSet", TrajApi.SetStaticDouble("Trajectories.DescentProfile", "FixedBankDeg", fixedBank));
            ctx.Record(k + "fixedBankDeg", double.IsNaN(fixedBank) ? (object)"none" : fixedBank);
            // Trajectories' capsule plan, flown while the shifter is in Descent Mode (builds that have it): the one
            // angle predicted and held until the air sets it, and with Plan the bank and reversals flown unsteered.
            double trimDeg = ctx.ParamDouble("trimDeg", plan.High);
            ctx.Record(k + "capsulePlanTrimSet", TrajApi.SetStaticDouble("Trajectories.CapsulePlan", "TrimDeg", trimDeg));
            ctx.Record(k + "capsulePlanTrimDeg", trimDeg);
            if (spec.Plan)
            {
                double planBank = ctx.ParamDouble("planBankDeg", 60), rev1 = ctx.ParamDouble("rev1Km", 49), rev2 = ctx.ParamDouble("rev2Km", 0);
                TrajApi.SetStaticDouble("Trajectories.CapsulePlan", "BankDeg", planBank);
                TrajApi.SetStaticDouble("Trajectories.CapsulePlan", "Reversal1M", rev1 > 0 ? rev1 * 1000 : double.NaN);
                TrajApi.SetStaticDouble("Trajectories.CapsulePlan", "Reversal2M", rev2 > 0 ? rev2 * 1000 : double.NaN);
                ctx.Record(k + "planBankDeg", planBank);
                ctx.Record(k + "planRev1Km", rev1);
                ctx.Record(k + "planRev2Km", rev2);
            }
            // A capsule steers by bank from the start (60 deg off lift up), so the aim is made with the bank it will fly.
            if (spec.SteerDuringAim && !spec.OpenLoop)
                Assert.True(TrajApi.SetSteerToTarget(true), "steering on before the aim");
            // Aim with a fresh aerodynamic model and fly with the same one: resetting it after aiming moved the
            // prediction by 131 km, so the aim was made against a model the flight no longer had.
            Flight.PointAtAoA(plan.Entry);
            ctx.Record(k + "aeroModelReset", TrajApi.InvalidateAerodynamicModel());
            yield return Wait.Seconds(2);

            // Aim in three steps. 1) An orbit that goes over KSC: circular at the entry apoapsis, inclined just above
            // KSC's latitude so the ground track tops out there heading about east (runway 09), with the node found by
            // searching the track (Earth's rotation included) for the closest pass. 2) Slow down to the set
            // periapsis: the vessel goes on the entry orbit in that plane, at apoapsis. 3) Move where it slowed down
            // (the argument of periapsis) until the predicted impact is at the target; near the top of the track a timing
            // error is an east-west error, so this also takes out the Earth's rotation during the glide. A north-south
            // miss left over is taken out by inclination.
            // fixedOrbit=inc/LAN/argPe/M (deg, deg, deg, rad): no aim, straight onto that orbit, so two runs start
            // on the same one (the placement's UT is recorded as placeUT, for the Earth's rotation between runs).
            double inc, lan, argPe, M;
            double[] miss = new double[2];
            string fixedOrbit = ctx.Param("fixedOrbit");
            // aimWaitSec: how long each placement waits for predictions (real s, default 60).
            aimWaitSec = ctx.ParamDouble("aimWaitSec", 60);
            if (fixedOrbit != null)
            {
                double[] o = Array.ConvertAll(fixedOrbit.Split('/'), x => double.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture));
                inc = o[0]; lan = o[1]; argPe = o[2]; M = o[3];
                ctx.Step("Onto the fixed orbit");
                yield return PlaceAtAndMeasure(body, peKm, apKm, inc, lan, argPe, M, plan.Entry, tLat, tLon, miss);
                RecordTorqueSources(ctx, k);
                // halfStepCheck=on: the predicted impact here with Trajectories' half-step kicks on, off and on again
                // (PhysicsWarp.PredictHalfStep), recorded as predictedLat/Lon_<on|off|on2>, to set against the touchdown.
                if (ctx.ParamBool("halfStepCheck", false))
                {
                    ctx.Step("Predicted impact with and without the half-step kicks");
                    foreach (string mode in new[] { "on", "off", "on2" })
                    {
                        bool set = TrajApi.SetStaticBool("Trajectories.PhysicsWarp", "PredictHalfStep", mode != "off");
                        ctx.Record(k + "halfStepSet_" + mode, set);
                        yield return MeasureImpact(k + "predicted", mode, ctx);
                    }
                    TrajApi.SetStaticBool("Trajectories.PhysicsWarp", "PredictHalfStep", true);
                }
                // torqueVariantsKNm=a,b,...: one prediction each from here with the model's non-aerodynamic pitch torque
                // forced to that value (cache off, so every force is evaluated with it), recorded as
                // predictedLat/Lon_torque<a>; the seed is restored after.
                string torqueVariants = ctx.Param("torqueVariantsKNm");
                if (!string.IsNullOrEmpty(torqueVariants))
                {
                    ctx.Step("Predictions from here with the other pitch torque forced");
                    bool cacheWas = TrajApi.UseCache;
                    double seed = TrajApi.ModelDouble("SeedPitchTorque");
                    ctx.Record(k + "torqueVariantSeedKNm", Math.Round(seed, 2));
                    try
                    {
                        foreach (string tk in torqueVariants.Split(','))
                        {
                            double torque = double.Parse(tk.Trim(), System.Globalization.CultureInfo.InvariantCulture);
                            TrajApi.UseCache = false;
                            bool set = TrajApi.ForceOtherTorque(torque);
                            float t0 = UnityEngine.Time.realtimeSinceStartup;
                            bool ok = set && TrajApi.PredictNow();
                            string key = "torque" + tk.Trim();
                            ctx.Record(k + "predictMs_" + key, Math.Round(1000 * (UnityEngine.Time.realtimeSinceStartup - t0)));
                            ctx.Record(k + "otherTorqueUsed_" + key, Math.Round(TrajApi.OtherPitchTorque, 2));
                            if (ok && TrajApi.ImpactLatLon(out double la, out double lo, out _))
                            {
                                ctx.Record(k + "predictedLat_" + key, Math.Round(la, 5));
                                ctx.Record(k + "predictedLon_" + key, Math.Round(lo, 5));
                            }
                            else
                                ctx.Record(k + "predictedLat_" + key, set ? "no impact" : "no torque setter");
                            yield return null;
                        }
                    }
                    finally
                    {
                        TrajApi.UseCache = cacheWas;
                        if (!double.IsNaN(seed) && seed > 0)
                            TrajApi.ForceOtherTorque(seed);
                    }
                }
                // variants=A,B,C,D,H: one prediction each, run to completion from the state here: A as flown (force cache
                // on, 1 s step), B cache off, C a 0.25 s step, D both, H as A without the half-step gravity kicks at the
                // atmosphere boundary (PhysicsWarp.PredictHalfStep off). Each predicted impact is recorded as
                // predictedLat/Lon_var<X> and its path written to prediction_var<X>.csv, all against the one flight that
                // follows (a whole entry flown with the cache off is too slow to finish).
                string variants = ctx.Param("variants");
                if (!string.IsNullOrEmpty(variants))
                {
                    ctx.Step("Predictions from here with the force cache and the step varied");
                    bool cacheWas = TrajApi.UseCache;
                    double stepWas = TrajApi.IntegrationStepSize;
                    try
                    {
                        foreach (string name in variants.Split(','))
                        {
                            TrajApi.UseCache = name != "B" && name != "D";
                            TrajApi.IntegrationStepSize = name == "C" || name == "D" ? 0.25 : 1.0;
                            TrajApi.SetStaticBool("Trajectories.PhysicsWarp", "PredictHalfStep", name != "H");
                            float t0 = UnityEngine.Time.realtimeSinceStartup;
                            bool ok = TrajApi.PredictNow();
                            ctx.Record(k + "predictMs_var" + name, Math.Round(1000 * (UnityEngine.Time.realtimeSinceStartup - t0)));
                            if (ok && TrajApi.ImpactLatLon(out double la, out double lo, out _))
                            {
                                ctx.Record(k + "predictedLat_var" + name, Math.Round(la, 5));
                                ctx.Record(k + "predictedLon_var" + name, Math.Round(lo, 5));
                                Directory.CreateDirectory(ctx.ResultsDir);
                                TrajApi.DumpPrediction(Path.Combine(ctx.ResultsDir, $"prediction_var{name}.csv"));
                            }
                            else
                                ctx.Record(k + "predictedLat_var" + name, "no impact");
                        }
                    }
                    finally
                    {
                        TrajApi.UseCache = cacheWas;
                        TrajApi.IntegrationStepSize = stepWas;
                        TrajApi.SetStaticBool("Trajectories.PhysicsWarp", "PredictHalfStep", true);
                    }
                }
            }
            else
            {
            ctx.Step("Aim 1: an orbit over KSC");
            inc = Math.Abs(tLat) + ctx.ParamDouble("incMarginDeg", 0.3);
            double rAp = body.Radius + apKm * 1000;
            double now = Planetarium.GetUniversalTime();
            double circPeriod = 2 * Math.PI * Math.Sqrt(rAp * rAp * rAp / body.gravParameter);
            lan = 0;
            double bestKm = double.PositiveInfinity, uOver = 0;
            for (double l = 0; l < 360; l += 2)
            {
                double d = TrackPassKm(body, inc, rAp, l, now, circPeriod, tLat, tLon, out double u);
                if (d < bestKm) { bestKm = d; lan = l; uOver = u; }
            }
            for (double l = lan - 2; l <= lan + 2; l += 0.05)
            {
                double d = TrackPassKm(body, inc, rAp, l, now, circPeriod, tLat, tLon, out double u);
                if (d < bestKm) { bestKm = d; lan = Wrap360(l); uOver = u; }
            }
            ctx.Record(k + "aimOverKscKm", Math.Round(bestKm, 1));
            ctx.Record(k + "aimOverKscArgLatDeg", Math.Round(uOver, 2));

            ctx.Step("Aim 2: slow to the set periapsis");
            // Where on that orbit to slow down: the impact should come at uOver, after the coast from apoapsis down to
            // the atmosphere and the glide. glideArcDeg is a first guess (an SSTO at 35 deg from Pe 70 km glided about
            // 150 deg of arc); step 3 corrects it.
            double rPe = body.Radius + peKm * 1000;
            double ecc = (rAp - rPe) / (rAp + rPe);
            double nuTop = Flight.DescendingTrueAnomalyDeg(body, peKm * 1000, apKm * 1000, startAltKm * 1000);
            double uBurn = uOver - (nuTop - 180) - ctx.ParamDouble("glideArcDeg", spec.GlideArcDeg);
            argPe = Wrap360(uBurn - 180);   // apoapsis at the burn point
            M = Math.PI;                     // at apoapsis
            // aimLongKm: the unsteered impact is aimed that far east of the target (the glide ends heading about east),
            // a range reserve the steering takes off by raising the AoA, which shortens the glide at every altitude. With
            // none the Hera arrived short with nothing to give: below about 70 km its profile already flew the longest
            // glide, any AoA change shortened it, and physics warp 4x had cost it 40 km on the way down.
            double aimLongKm = ctx.ParamDouble("aimLongKm", 0);
            double aimLon = tLon + aimLongKm * 1000 / (body.Radius * Math.Cos(tLat * Math.PI / 180)) * 180 / Math.PI;
            ctx.Record(k + "aimLongKm", aimLongKm);
            Func<double, double, double, IEnumerator> place = (ic, ap, m) => PlaceAtAndMeasure(body, peKm, apKm, ic, lan, ap, m, plan.Entry, tLat, aimLon, miss);
            yield return place(inc, argPe, M);
            if (spec.WarpProbe)
            {
                yield return WarpProbe(ctx, spec, plan, body, tLat, tLon, startAltKm);
                yield break;
            }

            ctx.Step("Aim 3: move the slow-down along the orbit until the impact is at the target");
            // The vessel goes to a degree before the descending crossing of the entry altitude, so every placement
            // starts the entry at once, and the argument of periapsis (where on the orbit it slowed down) slides the
            // whole entry along the ground track: about 1 km of impact per km of track, and near the top of the track
            // a timing error is an east-west error too, so this takes out the Earth's rotation during the glide. The
            // mean anomaly and argument of periapsis are not one variable: at the same argPe + M an entry from
            // apoapsis and one from the atmosphere's edge are different entries (two such placements of a capsule
            // came down 3000 km apart). Secant on the east miss, keeping the best placement: successive predictions
            // near the top of the atmosphere scatter by tens of km, and chasing that walked an earlier aim from 6 km
            // out to 193 km.
            M = Flight.MeanAnomalyOf(nuTop - 1, ecc);
            yield return place(inc, argPe, M);
            double rKm = body.Radius / 1000, kmPerRad = rKm * Math.Cos(tLat * Math.PI / 180);
            double tol = ctx.ParamDouble("aimTolKm", 10);
            double bestArgPe = argPe, bestInc = inc, bestAbs = Math.Abs(miss[0]);
            // The best placement is measured again at the end, and when it does not repeat within the tolerance, aimed
            // again from there: a Mk1-3 pod at -160 read 0.4 km east on its first placement, which ended the search,
            // and 487 km on the same placement a moment later, and entered 437 km long.
            for (int round = 0; round < 3; ++round)
            {
            double prevArg = double.NaN, prevE = double.NaN;
            for (int it = 0; it < 12 && Math.Abs(miss[0]) > tol; ++it)
            {
                double e0 = miss[0];
                double arg = argPe * Math.PI / 180;
                // Moving periapsis east (a later slow-down) moves the impact east, so the step follows the east miss.
                double step = double.IsNaN(prevArg) || Math.Abs(e0 - prevE) < 1e-6
                    ? e0 / kmPerRad
                    : -e0 * (arg - prevArg) / (e0 - prevE);
                // No more than a quarter orbit at a time, and no more than twice what the miss says as arc on the
                // track (a secant through two scattered predictions can ask for far more).
                double cap = Math.Min(Math.PI / 2, Math.Max(0.002, 2 * Math.Abs(e0) / kmPerRad));
                step = Math.Max(-cap, Math.Min(cap, step));
                if (Math.Abs(step) < 1e-5)
                    break;
                prevArg = arg;
                prevE = e0;
                argPe = Wrap360((arg + step) * 180 / Math.PI);
                yield return place(inc, argPe, M);
                if (Math.Abs(miss[0]) < bestAbs)
                {
                    bestAbs = Math.Abs(miss[0]);
                    bestArgPe = argPe;
                }
            }
            // Back to the best placement found.
            argPe = bestArgPe;
            yield return place(inc, argPe, M);
            if (Math.Abs(miss[0]) <= tol)
                break;
            Log.Info($"aim: the best placement read {miss[0]:F1} km east again; aiming again from it");
            bestAbs = Math.Abs(miss[0]);
            }
            // North-south: inclination moves the top of the track.
            double bestN = Math.Abs(miss[1]);
            for (int it = 0; it < 4 && Math.Abs(miss[1]) > tol; ++it)
            {
                double n0 = miss[1];
                const double dInc = 0.1;
                yield return place(inc + dInc, argPe, M);
                double slope = (miss[1] - n0) / dInc;
                if (Math.Abs(slope) < 1e-6) break;
                inc = Math.Max(Math.Max(0.05, Math.Abs(tLat) - 1), Math.Min(Math.Abs(tLat) + 3, inc - Math.Max(-1, Math.Min(1, n0 / slope))));
                yield return place(inc, argPe, M);
                if (Math.Abs(miss[1]) < bestN)
                {
                    bestN = Math.Abs(miss[1]);
                    bestInc = inc;
                }
            }
            if (inc != bestInc)
            {
                inc = bestInc;
                yield return place(inc, argPe, M);
            }
            }
            ctx.Record(k + "placeUT", Math.Round(lastPlaceUT, 3));
            ctx.Record(k + "aimMissEastKm", Math.Round(miss[0], 2));
            ctx.Record(k + "aimMissNorthKm", Math.Round(miss[1], 2));
            ctx.Record(k + "orbit", $"Pe {peKm} Ap {apKm} inc {inc:F3} LAN {lan:F3} argPe {argPe:F3} M {M:F4}");

            // aoaSweep=altKm:speed,...: at each altitude and airspeed, the pitch input that trims each angle of attack
            // with the surfaces alone (no RCS or wheels), from the model's aerodynamic moment at inputs -1..1 (AoaSweep);
            // recorded as sweep_<km>. sweepOnly=on ends the test there.
            // farYaw=altKm:mach:aoaDeg:beta,beta,...: FAR's side force and yaw moment at each sideslip (deg), with the body
            // parts carrying the yaw (FarApi.YawBreakdown), as farYaw_<beta>; the surfaces' deflections as farYawSurfaces
            string farYaw = ctx.Param("farYaw");
            if (!string.IsNullOrEmpty(farYaw))
            {
                ctx.Step("FAR's yaw moment against sideslip");
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                string[] f = farYaw.Split(':');
                double km = double.Parse(f[0], inv), mach = double.Parse(f[1], inv), aoaDeg = double.Parse(f[2], inv);
                ctx.Record(k + "farYawSurfaces", FarApi.SurfaceStates(FlightGlobals.ActiveVessel));
                Directory.CreateDirectory(ctx.ResultsDir);
                File.WriteAllText(Path.Combine(ctx.ResultsDir, "far_geometry_bounds.csv"), FarApi.GeometryBoundsCsv(FlightGlobals.ActiveVessel));
                ctx.Record(k + "farVoxels", FarApi.VoxelDump(FlightGlobals.ActiveVessel, ctx.ResultsDir));
                foreach (string bs in f[3].Split(','))
                {
                    double beta = double.Parse(bs, inv);
                    ctx.Record(k + "farYaw_" + bs, FarApi.YawBreakdown(FlightGlobals.ActiveVessel, km * 1000, mach, aoaDeg, beta));
                    yield return null;
                }
            }
            // momentSweep=altKm:machFrom:machTo:machStep:aoaDeg;...: the model's moment against pitch input at each Mach
            // (MomentSweep), moment_sweep.csv, and the trim the model solves there, as momentSweep_<altKm>km_<aoa>deg
            string momentSweep = ctx.Param("momentSweep");
            if (!string.IsNullOrEmpty(momentSweep))
            {
                ctx.Step("Sweep the model's moment against pitch input");
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var csv = new System.Text.StringBuilder("altKm,mach,aoaDeg,input,momentKNm\n");
                foreach (string item in momentSweep.Split(';'))
                {
                    string[] f = item.Split(':');
                    double km = double.Parse(f[0], inv), from = double.Parse(f[1], inv), to = double.Parse(f[2], inv),
                           step = double.Parse(f[3], inv), aoaDeg = double.Parse(f[4], inv);
                    var trims = new System.Collections.Generic.List<string>();
                    for (double mach = from; mach <= to + 1e-9; mach += step)
                        trims.Add(MomentSweep(km * 1000, mach, aoaDeg, csv));
                    ctx.Record(k + "momentSweep_" + f[0] + "km_" + f[4] + "deg", string.Join(" ", trims.ToArray()));
                    yield return null;
                }
                Directory.CreateDirectory(ctx.ResultsDir);
                File.WriteAllText(Path.Combine(ctx.ResultsDir, "moment_sweep.csv"), csv.ToString());
            }
            string aoaSweep = ctx.Param("aoaSweep");
            if (!string.IsNullOrEmpty(aoaSweep))
            {
                ctx.Step("Sweep the angle of attack the surfaces can hold");
                foreach (string item in aoaSweep.Split(','))
                {
                    string[] kv = item.Split(':');
                    double km = double.Parse(kv[0], System.Globalization.CultureInfo.InvariantCulture);
                    double speed = double.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture);
                    ctx.Record(k + "sweep_" + kv[0] + "km_" + kv[1] + "ms", AoaSweep(km * 1000, speed, ctx.ParamDouble("sweepMaxDeg", 40), ctx.ParamDouble("sweepStepDeg", 2.5),
                        ctx.ParamBool("sweepRaw", false)));
                    yield return null;
                }
                // hullShots=pitchDeg:hdgDeg,...: the flight camera on the vessel from hullShotM (40 m) at each pitch and
                // heading, UI hidden, saved as hull_<pitch>_<hdg>.png in the results folder (negative pitch looks up from below)
                string hullShots = ctx.Param("hullShots");
                if (!string.IsNullOrEmpty(hullShots))
                {
                    if (MapView.MapIsEnabled)
                        MapView.ExitMapView();
                    GameEvents.onHideUI.Fire();
                    FlightCamera cam = FlightCamera.fetch;
                    float dist = (float)ctx.ParamDouble("hullShotM", 40);
                    cam.minPitch = -1.5f;
                    cam.maxPitch = 1.5f;
                    foreach (string item in hullShots.Split(','))
                    {
                        string[] ph = item.Split(':');
                        float pitch = float.Parse(ph[0], System.Globalization.CultureInfo.InvariantCulture);
                        float hdg = float.Parse(ph[1], System.Globalization.CultureInfo.InvariantCulture);
                        cam.SetDistanceImmediate(dist);
                        float until = UnityEngine.Time.realtimeSinceStartup + 1.5f;
                        while (UnityEngine.Time.realtimeSinceStartup < until)
                        {
                            cam.camPitch = pitch * UnityEngine.Mathf.Deg2Rad;
                            cam.camHdg = hdg * UnityEngine.Mathf.Deg2Rad;
                            yield return null;
                        }
                        UnityEngine.ScreenCapture.CaptureScreenshot(Path.Combine(ctx.ResultsDir, "hull_" + ph[0] + "_" + ph[1] + ".png"));
                        for (int i = 0; i < 5; ++i)
                            yield return null;
                    }
                    GameEvents.onShowUI.Fire();
                }
                if (ctx.ParamBool("sweepOnly", false))
                    yield break;
            }

            // Coast on rails to the top of the atmosphere, then face the profile attitude again: on rails the vessel
            // keeps its orientation while the velocity turns.
            if (FlightGlobals.ActiveVessel.altitude > startAltKm * 1000 + 1000)
            {
                Flight.Warp(3);
                yield return Wait.Until(() => FlightGlobals.ActiveVessel.altitude < startAltKm * 1000 + 1000, 1800, "coast to the atmosphere", shot: false);
                Flight.WarpOff();
                yield return Wait.Until(() => TimeWarp.CurrentRate <= 1.01, 30, "off rails", shot: false);
            }
            Flight.PointAtAoA(plan.Entry);
            yield return Wait.Seconds(1);
            if (physWarp > 1) Flight.PhysicsWarpRate(physWarp);
            // hyperWarp=rate: TimeControl's hyper-warp instead of physics warp, at physics accuracy hyperAccuracy
            // (default 1: the 1x step, more steps per frame); the rate it reached is recorded as game over real time.
            double hyperRate = ctx.ParamDouble("hyperWarp", 0);
            float hyperRealStart = 0f;
            double hyperGameStart = 0;
            if (hyperRate > 1)
            {
                Assert.True(Flight.HyperWarp(hyperRate, ctx.ParamDouble("hyperAccuracy", 1)), "TimeControl hyper-warp on");
                hyperRealStart = UnityEngine.Time.realtimeSinceStartup;
                hyperGameStart = Planetarium.GetUniversalTime();
                ctx.Record(k + "hyperWarpStepS", Math.Round(UnityEngine.Time.fixedDeltaTime, 4));
                ctx.Record(k + "hyperWarpTimeScale", UnityEngine.Time.timeScale);
            }
            // warpSchedule=rate@altKm,...: from then on the physics warp goes to each rate as the vessel descends past
            // its altitude (e.g. 1@50: back to 1x below 50 km), to see what a warp change mid-entry does; with
            // hyperWarp, TimeControl's attempted hyper-warp rate instead (physics accuracy unchanged).
            UnityEngine.GameObject scheduleObject = null;
            string schedule = ctx.Param("warpSchedule");
            if (!string.IsNullOrEmpty(schedule))
            {
                scheduleObject = new UnityEngine.GameObject("KspTest.WarpSchedule");
                var warpSchedule = scheduleObject.AddComponent<WarpSchedule>();
                warpSchedule.Hyper = hyperRate > 1;
                warpSchedule.Steps = Array.ConvertAll(schedule.Split(','), x =>
                {
                    string[] kv = x.Split('@');
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    return new[] { double.Parse(kv[0], inv), double.Parse(kv[1], inv) * 1000 };
                });
                ctx.Record(k + "warpSchedule", schedule);
            }
            // the predicted path at each of dumpAtKm (prediction_<km>.csv) and the flown path (flown_path.csv): always for
            // an open-loop entry, for the others when dumpAtKm is given
            string dumpAt = ctx.Param("dumpAtKm", spec.OpenLoop ? "130+110+90+70+50+30" : null);
            double[] dumps = string.IsNullOrEmpty(dumpAt)
                ? null
                : Array.ConvertAll(dumpAt.Split('+'), s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture));
            // stepLogSec=n: every physics step's control loop for n real seconds from here to steps.csv (StepLog), then
            // end the test there.
            double stepLogSec = ctx.ParamDouble("stepLogSec", 0);
            if (stepLogSec > 0)
            {
                ctx.Step($"Log every physics step for {stepLogSec} s");
                var stepLog = StepLog.Begin(stepLogSec);
                while (!stepLog.Done)
                {
                    Flight.AssertAlive();
                    yield return null;
                }
                Directory.CreateDirectory(ctx.ResultsDir);
                stepLog.Write(Path.Combine(ctx.ResultsDir, "steps.csv"));
                if (hyperRate > 1)
                    Flight.HyperWarpOff();
                yield break;
            }
            // frameSpikes=on (default): Trajectories' main-thread time per frame over the entry (FrameSpikes), recorded
            // as trajFrameMs* and frame_spikes.csv.
            bool frameSpikes = ctx.ParamBool("frameSpikes", true);
            // frameSpikesAlloc=on: also heap growth by method (alloc_*), which slows the prediction's hot paths
            if (frameSpikes)
                ctx.Record(k + "frameSpikesPatched", FrameSpikes.Start(ctx.ParamBool("frameSpikesAlloc", false)));
            // frameProfile=phases (default): where each frame's time goes - physics scripts, PhysX, Update, rendering,
            // present (FrameProfile), recorded as profile* and frame_profile.csv; methods: also every Update, FixedUpdate,
            // LateUpdate and OnGUI timed, by method and by assembly (slower); off
            // farPredictFlapShift=on/off: whether FAR's predictions move a flap's aerodynamic centre onto the wing ahead
            // (FARWingAerodynamicModel.PredictFlapACShift, on FAR forks that have it)
            string flapShift = ctx.Param("farPredictFlapShift");
            if (!string.IsNullOrEmpty(flapShift))
            {
                var field = TrajApi.TryFindTypePublic("ferram4.FARWingAerodynamicModel")?.GetField("PredictFlapACShift");
                field?.SetValue(null, !flapShift.Equals("off", StringComparison.OrdinalIgnoreCase));
                ctx.Record(k + "farPredictFlapShift", field != null ? field.GetValue(null) : "n/a");
            }
            // farControlCamber=on/off: whether a control surface behind a wing gets the pair's camber lift and its
            // aerodynamic centre shift, in flight and predictions (FARWingAerodynamicModel.ControlSurfaceCamber)
            string controlCamber = ctx.Param("farControlCamber");
            {
                var field = TrajApi.TryFindTypePublic("ferram4.FARWingAerodynamicModel")?.GetField("ControlSurfaceCamber");
                if (field != null && !string.IsNullOrEmpty(controlCamber))
                    field.SetValue(null, controlCamber.Equals("on", StringComparison.OrdinalIgnoreCase));
                ctx.Record(k + "farControlCamber", field != null ? field.GetValue(null) : "n/a");
            }
            string frameProfile = ctx.Param("frameProfile", "phases").ToLowerInvariant();
            if (frameProfile == "phases" || frameProfile == "methods")
                ctx.Record(k + "frameProfile", FrameProfile.Start(frameProfile == "methods"));
            // lowRender=on: VSync and anti-aliasing off for the entry (the window size is run-test's -Width/-Height)
            int vsyncWas = UnityEngine.QualitySettings.vSyncCount, antiAliasWas = UnityEngine.QualitySettings.antiAliasing;
            bool lowRender = ctx.ParamBool("lowRender", false);
            if (lowRender)
            {
                UnityEngine.QualitySettings.vSyncCount = 0;
                UnityEngine.QualitySettings.antiAliasing = 0;
                ctx.Record(k + "lowRender", $"vsync {vsyncWas} -> 0, anti-aliasing {antiAliasWas} -> 0, {UnityEngine.Screen.width}x{UnityEngine.Screen.height}");
            }
            // mapView=on: the entry flown in the map view, the vessel not drawn
            bool mapView = ctx.ParamBool("mapView", false);
            if (mapView && !MapView.MapIsEnabled)
                MapView.EnterMapView();
            // allocSurvey=on: the whole game's allocations by Update/FixedUpdate/LateUpdate/OnGUI (AllocSurvey), recorded as
            // survey_* and alloc_survey.csv; slows every frame a little
            bool allocSurvey = ctx.ParamBool("allocSurvey", false);
            // the collector's mode, every collection's frame (gc_frames.csv) and explicit GC.Collect calls; gcSliceMs
            // sets the incremental collector's time slice per frame
            GcWatch.Start(ctx, k, double.TryParse(ctx.Param("gcSliceMs", ""), System.Globalization.NumberStyles.Float,
                                                  System.Globalization.CultureInfo.InvariantCulture, out double gcSliceMs) ? gcSliceMs : double.NaN);
            if (allocSurvey)
                // allocSurveyDeep=Kerbalism,...: every method in those assemblies too
                ctx.Record(k + "allocSurveyPatched", AllocSurvey.Start(ctx.Param("allocSurveyDeep", "")?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)));
            // video=on: a time lapse of the map view looking straight down on the target from videoViewKm (default 500),
            // one screenshot every videoIntervalSec of real time (default 1), with videoLabel in frames.csv (VideoCapture)
            bool video = ctx.ParamBool("video", false);
            // momentAtKm=95,85,...: at each altitude on the way down, Flight.MomentBreakdown (FAR's predicted pitching
            // moment against the applied one, by voxel body part and wings), recorded as moment_<km>
            UnityEngine.GameObject momentObject = null;
            string momentAt = ctx.Param("momentAtKm");
            if (!string.IsNullOrEmpty(momentAt))
            {
                momentObject = new UnityEngine.GameObject("KspTest.MomentProbe");
                var mp = momentObject.AddComponent<MomentProbe>();
                mp.Ctx = ctx;
                mp.K = k;
                // momentLiveSweep=on: the model's AoA sweep at each of those altitudes too (sweepLive_<km>)
                mp.Sweep = ctx.ParamBool("momentLiveSweep", false);
                // momentPulse=on: a +2000 kN m pitch torque on the root for a second after each (pulse_<km>)
                mp.PulseTest = ctx.ParamBool("momentPulse", false);
                // momentPulseSeq=on: +2000 kN m on the root, the heaviest control surface and the heaviest part in turn, each
                // after a baseline (pulseSeq_<km>)
                mp.PulseSeq = ctx.ParamBool("momentPulseSeq", false);
                mp.SolverIters = (int)ctx.ParamDouble("solverIters", 0);
                // maxAngVel=N: every part's Rigidbody.maxAngularVelocity set to N rad/s (KSP: 50)
                mp.MaxAngVel = (float)ctx.ParamDouble("maxAngVel", 0);
                // farWingArcs=on: FAR's wing interaction overlay on (FARDebugValues.showWingInteractions), and screenshots of it at each
                mp.WingArcShots = ctx.ParamBool("farWingArcs", false);
                if (mp.WingArcShots)
                    ctx.Record(k + "farWingArcs", TrajApi.TryFindTypePublic("FerramAerospaceResearch.FARDebugValues")?.GetField("showWingInteractions") is System.Reflection.FieldInfo arcs && SetTrue(arcs));
                // momentRbWrites=on: everything written to the vessel's rigidbodies directly, by writer, over a second at each (rbWrites_<km>)
                mp.RbWritesOn = ctx.ParamBool("momentRbWrites", false);
                if (mp.RbWritesOn) RbWrites.Arm();
                if (mp.Sweep)
                {
                    WingLiveVsSim.Arm();
                    QueuedTorque.Arm();
                }
                var alts = new System.Collections.Generic.List<double>(Array.ConvertAll(momentAt.Split(','), x => double.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture)));
                alts.Sort((a, b) => b.CompareTo(a));
                mp.Alts = new System.Collections.Generic.Queue<double>(alts);
            }
            // traceAtKm=130,110,...: a trace of the prediction made as the vessel passes each altitude (TraceProbe),
            // trace_<km>.csv, to set against the flown samples
            UnityEngine.GameObject traceObject = null;
            TraceProbe traceProbe = null;
            string traceAt = ctx.Param("traceAtKm");
            if (!string.IsNullOrEmpty(traceAt))
            {
                Directory.CreateDirectory(ctx.ResultsDir);
                traceObject = new UnityEngine.GameObject("KspTest.TraceProbe");
                traceProbe = traceObject.AddComponent<TraceProbe>();
                traceProbe.Dir = ctx.ResultsDir;
                var talts = new System.Collections.Generic.List<double>(Array.ConvertAll(traceAt.Split(','), x => double.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture)));
                talts.Sort((a, b) => b.CompareTo(a));
                traceProbe.Alts = new System.Collections.Generic.Queue<double>(talts);
                TrajApi.SetStaticInt("Trajectories.Trajectory", "TraceEverySteps", (int)ctx.ParamDouble("traceEverySteps", 5));
            }
            // plotShotsKm=100,60,30,10,3: Trajectories' flight profile plot open, a screenshot at each altitude (PlotShotProbe)
            UnityEngine.GameObject plotObject = null;
            PlotShotProbe plotProbe = null;
            string plotAt = ctx.Param("plotShotsKm");
            if (!string.IsNullOrEmpty(plotAt))
            {
                Directory.CreateDirectory(ctx.ResultsDir);
                plotObject = new UnityEngine.GameObject("KspTest.PlotShotProbe");
                plotProbe = plotObject.AddComponent<PlotShotProbe>();
                plotProbe.Dir = ctx.ResultsDir;
                var palts = new System.Collections.Generic.List<double>(Array.ConvertAll(plotAt.Split(','), x => double.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture)));
                palts.Sort((a, b) => b.CompareTo(a));
                plotProbe.Alts = new System.Collections.Generic.Queue<double>(palts);
            }
            // yawProbe=80:20: sideslip, stick, body rates, aerodynamic and RCS moments and each control surface's state
            // between the two altitudes (km), yaw.csv and yaw_parts.txt (YawProbe)
            UnityEngine.GameObject yawObject = null;
            string yawAt = ctx.Param("yawProbe");
            if (!string.IsNullOrEmpty(yawAt))
            {
                Directory.CreateDirectory(ctx.ResultsDir);
                string[] tb = yawAt.Split(':');
                yawObject = new UnityEngine.GameObject("KspTest.YawProbe");
                YawProbe yp = yawObject.AddComponent<YawProbe>();
                yp.Dir = ctx.ResultsDir;
                yp.TopKm = double.Parse(tb[0], System.Globalization.CultureInfo.InvariantCulture);
                yp.BottomKm = tb.Length > 1 ? double.Parse(tb[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
                yp.EveryFrames = (int)ctx.ParamDouble("yawEveryFrames", 25);
            }
            // the RCS force the model adds at the trimmed pitch input (builds that have it), up the vessel at full input
            ctx.Record(k + "modelRcsUpPosKN", Math.Round(TrajApi.ModelDouble("RcsForceUpPositiveKN"), 3));
            ctx.Record(k + "modelRcsUpNegKN", Math.Round(TrajApi.ModelDouble("RcsForceUpNegativeKN"), 3));
            // rcsProbe=on: the RCS's net force along the velocity, by altitude band (RcsForceProbe), as rcs_<band>
            bool rcsProbe = ctx.ParamBool("rcsProbe", false);
            if (rcsProbe)
                RcsForceProbe.Begin(new double[] { 140, 100, 90, 82, 75, 68, 60, 50, 40, 30, 20, 10 });
            // the video keeps the Trajectories window and hides MechJeb's (restored after)
            var hiddenMj = new System.Collections.Generic.List<MuMech.DisplayModule>();
            if (video && Mj.Present)
                foreach (MuMech.DisplayModule dm in Mj.Core.GetComputerModules<MuMech.DisplayModule>())
                    if (dm.Enabled && dm.ShowInFlight)
                    {
                        dm.ShowInFlight = false;
                        hiddenMj.Add(dm);
                    }
            // mjSmoothDps=10: SmartASS "Smooth Control" (its target attitude slews at most that many deg/s);
            // mjVelKp=3: the BetterController's rate-loop gain (7.98 by default). Both persist in MechJeb's settings,
            // so they are put back after the flight.
            MuMech.MechJebModuleSmartASS mjSa = null;
            MuMech.EditableDouble mjVelKp = null;   // BetterController is internal: its VelKp by reflection
            bool mjSmoothWas = false;
            double mjDpsWas = double.NaN, mjVelKpWas = double.NaN;
            if (Mj.Present && Mj.Core != null)
            {
                double dps = ctx.ParamDouble("mjSmoothDps", double.NaN);
                if (!double.IsNaN(dps))
                {
                    mjSa = Mj.Core.GetComputerModule<MuMech.MechJebModuleSmartASS>();
                    mjSmoothWas = mjSa.smoothControl;
                    mjDpsWas = mjSa.degreesPerSecond.Val;
                    mjSa.smoothControl = true;
                    mjSa.degreesPerSecond.Val = dps;
                    ctx.Record(k + "mjSmoothDps", dps);
                }
                double velKp = ctx.ParamDouble("mjVelKp", double.NaN);
                var att = Mj.Core.GetComputerModule<MuMech.MechJebModuleAttitudeController>();
                object controller = att?.Controller;
                ctx.Record(k + "mjController", controller?.GetType().Name ?? "none");
                mjVelKp = controller?.GetType().GetField("VelKp", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                                                                     | System.Reflection.BindingFlags.NonPublic)?.GetValue(controller) as MuMech.EditableDouble;
                if (mjVelKp != null)
                {
                    mjVelKpWas = mjVelKp.Val;
                    if (!double.IsNaN(velKp))
                        mjVelKp.Val = velKp;
                    ctx.Record(k + "mjVelKp", mjVelKp.Val);
                }
            }
            // The MechJeb windows and settings changed above go back whatever happens in the flight (the runner disposes
            // a failed test's coroutines, which runs this finally): a run that ended early left SmartASS out of the
            // flight menu for good, MechJeb saving it hidden at the next quit.
            try
            {
                if (video)
                    VideoCapture.Begin(ctx.ResultsDir, body, tLat, tLon, ctx.ParamDouble("videoViewKm", 500),
                                       ctx.ParamDouble("videoIntervalSec", 1), ctx.Param("videoLabel", ""));
                yield return TrajProfileFlight.Fly(ctx, plan, 0, partsStart, physWarp, 60, 10, 5400, targetPreset: true,
                                                   steer: !spec.OpenLoop && !spec.Plan && !!ctx.ParamBool("steer", true),
                                                   dumpAtKm: dumps);
                if (video)
                {
                    // landingShotM=150,2000: the flight camera's view of where it came down from each distance (m),
                    // looking down at landingPitchDeg (30), taken paused at touchdown
                    float[] shotM = Array.ConvertAll((ctx.Param("landingShotM", "150,2000") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries),
                                                     x => float.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture));
                    if (shotM.Length > 0)
                        yield return VideoCapture.LandingShots(ctx.ParamDouble("landingPitchDeg", 30), shotM);
                    ctx.Record(k + "videoFrames", VideoCapture.End());
                }
                if (mjSa != null)
                    ctx.Record(k + "mjSmoothStillOn", mjSa.smoothControl);
            }
            finally
            {
                foreach (MuMech.DisplayModule dm in hiddenMj)
                    dm.ShowInFlight = true;
                if (mjSa != null)
                {
                    mjSa.smoothControl = mjSmoothWas;
                    mjSa.degreesPerSecond.Val = mjDpsWas;
                }
                if (mjVelKp != null && !double.IsNaN(mjVelKpWas))
                    mjVelKp.Val = mjVelKpWas;
            }
            if (momentObject != null)
                UnityEngine.Object.Destroy(momentObject);
            if (yawObject != null)
                UnityEngine.Object.Destroy(yawObject);
            if (plotObject != null)
            {
                ctx.Record(k + "plotShots", string.Join(" ", plotProbe.Taken.ToArray()));
                UnityEngine.Object.Destroy(plotObject);
            }
            if (traceObject != null)
            {
                ctx.Record(k + "traces", string.Join(" ", traceProbe.Requested.ToArray()));
                UnityEngine.Object.Destroy(traceObject);
            }
            if (rcsProbe)
                foreach (var kv in RcsForceProbe.End())
                    ctx.Record(k + "rcs_" + kv.Key, kv.Value);
            if (allocSurvey)
                AllocSurvey.Stop(ctx, k, frameSpikes ? FrameSpikes.GameAllocMB : double.NaN);
            if (frameSpikes)
                FrameSpikes.Stop(ctx, k);
            GcWatch.Stop(ctx, k);
            FrameProfile.Stop(ctx, k);
            if (lowRender)
            {
                UnityEngine.QualitySettings.vSyncCount = vsyncWas;
                UnityEngine.QualitySettings.antiAliasing = antiAliasWas;
            }
            if (mapView && MapView.MapIsEnabled)
                MapView.ExitMapView();
            if (hyperRate > 1)
            {
                double real = UnityEngine.Time.realtimeSinceStartup - hyperRealStart;
                ctx.Record(k + "hyperWarpAchievedRate", real > 0 ? Math.Round((Planetarium.GetUniversalTime() - hyperGameStart) / real, 2) : double.NaN);
                Flight.HyperWarpOff();
            }
            if (pinObject != null)
                UnityEngine.Object.Destroy(pinObject);
            if (scheduleObject != null)
                UnityEngine.Object.Destroy(scheduleObject);
            if (rcsCutoffObject != null)
                UnityEngine.Object.Destroy(rcsCutoffObject);
            if (probeObject != null)
                UnityEngine.Object.Destroy(probeObject);
            if (sweepObject != null)
                UnityEngine.Object.Destroy(sweepObject);
            // the plan as flown (with steering, as the steering left it) and the angle the air set
            ctx.Record(k + "planEndBankDeg", Math.Round(TrajApi.StaticDouble("Trajectories.CapsulePlan", "BankDeg"), 1));
            ctx.Record(k + "planEndRev1Km", Math.Round(TrajApi.StaticDouble("Trajectories.CapsulePlan", "Reversal1M") / 1000, 1));
            ctx.Record(k + "planEndRev2Km", Math.Round(TrajApi.StaticDouble("Trajectories.CapsulePlan", "Reversal2M") / 1000, 1));
            ctx.Record(k + "planFlownDeg", Math.Round(TrajApi.StaticDouble("Trajectories.CapsulePlan", "FlownDeg"), 1));
            TrajApi.SetStaticDouble("Trajectories.DescentProfile", "FixedBankDeg", double.NaN);
            ctx.Record(k + "trimSolves", TrajApi.StaticDouble("Trajectories.Trajectory", "TrimSolves"));
            TrajApi.SetStaticBool("Trajectories.DescentProfile", "FlyTrim", false);
            TrajApi.SetStaticDouble("Trajectories.DescentProfile", "FlyTrimMaxMach", double.PositiveInfinity);
        }

        /// <summary>
        /// Why the predicted impact walks from the top of the atmosphere down: an SSTO on the KSC entry orbit, placed at
        /// startAltKm (146) on the way down and held at the profile's attitude with no steering, is sampled every
        /// sampleSec (2) game seconds down to endAltKm (120). At each sample three predictions are run to completion in
        /// the same frame, from the same frozen state: A baseline (cache on, 1 s step), B cache off, C 0.25 s step;
        /// plus whether the prediction starts with a space or an atmospheric patch. At resetAtKm (133) the aerodynamic
        /// model is reset once. A vessel coasting through near-vacuum is on one Kepler arc, so a consistent predictor
        /// gives the same impact at every sample. Params: orbit (inc/LAN/argPe, the last KSC aim's by default), profile.
        /// </summary>
        [UITest(Requires = "RP-1,MechJeb", StartsAt = StartState.Vab, Craft = "Mk2 SSTO Big", TimeoutSec = 3600)]
        public static IEnumerator Traj_RP1_TopDrift(TestContext ctx)
        {
            Ugui.DismissPopups();
            var plan = TrajProfileFlight.Setup(ctx, "35/35/10/0h");
            string k = plan.K;
            double peKm = 70, apKm = 150;
            yield return Nav.EnsureState(ctx, StartState.Vab, ctx.CraftPath(ctx.Param("craft", "Mk2 SSTO Big")));
            Ugui.DismissPopups();
            ctx.Record("propellantDirect", Rp1.ApplyPropellantLoadDirect(0));
            yield return Rp1.ConfigureSim(new SimSpec
            {
                StartInOrbit = true, Mode = "Reentry", PeKm = peKm, Arrival = "Apoapsis", ApKm = apKm, EntryAltKm = 140,
                IncDeg = 0, LanDeg = 0, ArgPeDeg = 0, UT = 2019828800, Attitude = "Prograde", LogTelemetry = false, PropellantPct = 0,
            });
            yield return Rp1.StartSim();
            var v = FlightGlobals.ActiveVessel;
            CelestialBody body = v.mainBody;
            Ctrl.RetractGear();
            Ctrl.SetThrottle(0f);
            yield return Wait.Seconds(5);
            Ctrl.EnableRcs();
            v.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            Flight.IgnoreMaxTemperature(true);
            Flight.InfinitePropellant(true);
            yield return TrajProfileFlight.Engage(ctx, plan);

            double inc = ctx.ParamDouble("inc", 29.801), lan = ctx.ParamDouble("lan", 161.05), argPe = ctx.ParamDouble("argPe", 87.598);
            double startAltKm = ctx.ParamDouble("startAltKm", 146), endAltKm = ctx.ParamDouble("endAltKm", 120), resetAtKm = ctx.ParamDouble("resetAtKm", 133);
            double rPe = body.Radius + peKm * 1000, rAp = body.Radius + apKm * 1000, ecc = (rAp - rPe) / (rAp + rPe);
            double M = Flight.MeanAnomalyOf(Flight.DescendingTrueAnomalyDeg(body, peKm * 1000, apKm * 1000, startAltKm * 1000), ecc);
            Flight.SetOrbitAtMeanAnomaly(peKm * 1000, apKm * 1000, inc, lan, argPe, M);
            yield return null;
            Flight.PointAtAoA(plan.Entry);
            TrajApi.InvalidateAerodynamicModel();
            yield return Wait.Seconds(3);

            var csv = new StringBuilder("ut,altKm,variant,firstPatchAtmo,patches,impactLat,impactLon,shiftFromFirstKm,otherTorque,seedTorque,torqueLearned,lastTrim,qkPa\n");
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var first = new System.Collections.Generic.Dictionary<string, double[]>();
            bool resetDone = false;
            double sampleSec = ctx.ParamDouble("sampleSec", 2);
            // A baseline, B cache off, C 0.25 s step, E trim off and cache off (trim off with the cache on would read
            // cells the trimmed variants filled).
            string[] names = ctx.Param("variants", "A,B,C").Split(',');
            bool trimWas = TrajApi.TrimControlSurfaces ?? true;
            ctx.Step("Sample the prediction from the top of the atmosphere down");
            while (FlightGlobals.ActiveVessel.altitude > endAltKm * 1000)
            {
                v = FlightGlobals.ActiveVessel;
                if (!resetDone && v.altitude < resetAtKm * 1000)
                {
                    resetDone = true;
                    TrajApi.InvalidateAerodynamicModel();
                    csv.Append(Planetarium.GetUniversalTime().ToString("F1", inv)).Append(",").Append((v.altitude / 1000).ToString("F2", inv)).Append(",RESET,,,,,\n");
                }
                foreach (string name in names)
                {
                    TrajApi.UseCache = name != "B" && name != "E";
                    TrajApi.IntegrationStepSize = name == "C" ? 0.25 : 1.0;
                    TrajApi.TrimControlSurfaces = name == "E" ? false : trimWas;
                    bool ok = TrajApi.PredictNow();
                    TrajApi.FirstPatch(out bool atmo, out int count);
                    double la = double.NaN, lo = double.NaN, shift = double.NaN;
                    if (ok && TrajApi.ImpactLatLon(out la, out lo, out _))
                    {
                        if (!first.ContainsKey(name)) first[name] = new[] { la, lo };
                        shift = GreatCircleDeg(first[name][0], first[name][1], la, lo) * Math.PI / 180 * body.Radius / 1000;
                    }
                    csv.Append(Planetarium.GetUniversalTime().ToString("F1", inv)).Append(",").Append((v.altitude / 1000).ToString("F2", inv)).Append(",")
                       .Append(name).Append(",").Append(atmo ? 1 : 0).Append(",").Append(count).Append(",")
                       .Append(la.ToString("F4", inv)).Append(",").Append(lo.ToString("F4", inv)).Append(",").Append(shift.ToString("F1", inv)).Append(",")
                       .Append(TrajApi.OtherPitchTorque.ToString("F2", inv)).Append(",").Append(TrajApi.ModelDouble("SeedPitchTorque").ToString("F2", inv)).Append(",")
                       .Append(TrajApi.ModelBool("OtherPitchTorqueLearned") == true ? 1 : 0).Append(",").Append(TrajApi.ModelDouble("LastTrimInput").ToString("F4", inv)).Append(",")
                       .Append(v.dynamicPressurekPa.ToString("F5", inv)).Append("\n");
                    Log.Info($"topdrift {name}: alt {v.altitude / 1000:F2} km atmo {atmo} patches {count} impact {la:F3}/{lo:F3} shift {shift:F1} km");
                    // dumpPaths=1: the whole predicted path of each prediction that found an impact.
                    if (ctx.ParamDouble("dumpPaths", 0) > 0 && !double.IsNaN(la))
                        TrajApi.DumpPrediction(Path.Combine(ctx.ResultsDir, $"path_{name}_{v.altitude / 1000:F2}.csv"));
                }
                TrajApi.UseCache = true;
                TrajApi.IntegrationStepSize = 1.0;
                TrajApi.TrimControlSurfaces = trimWas;
                // The model's force at fixed reference states (trimmed, uncached): if it changes as the vessel crosses
                // into the atmosphere, the prediction depends on the vessel's live state rather than the predicted one.
                foreach (var refState in new[] { new[] { 7400.0, 100000.0 }, new[] { 5000.0, 60000.0 }, new[] { 1500.0, 30000.0 } })
                {
                    string refName = $"REF{refState[1] / 1000:F0}";
                    // The same state with the surfaces fixed (pitch input 0, no trim solve): FAR's own answer.
                    if (TrajApi.ForceAtPitchInput(v, plan.Entry, refState[0], refState[1], 0.0, out Vector3d f0, out double m0))
                        Log.Info($"topdrift {refName}p0: alt {v.altitude / 1000:F2} km |F| {f0.magnitude:F3} kN moment {m0:F3} kN m");
                    if (TrajApi.ComputeDirectAt(v, plan.Entry, refState[0], refState[1], out Vector3d f))
                    {
                        double drag = -Vector3d.Dot(f, v.srf_velocity.normalized);
                        csv.Append(Planetarium.GetUniversalTime().ToString("F1", inv)).Append(",").Append((v.altitude / 1000).ToString("F2", inv)).Append(",")
                           .Append(refName).Append(",,,").Append(f.magnitude.ToString("F4", inv)).Append(",").Append(drag.ToString("F4", inv)).Append(",,")
                           .Append(TrajApi.OtherPitchTorque.ToString("F2", inv)).Append(",,,").Append(TrajApi.ModelDouble("LastTrimInput").ToString("F4", inv)).Append(",")
                           .Append(v.dynamicPressurekPa.ToString("F5", inv)).Append("\n");
                        Log.Info($"topdrift {refName}: alt {v.altitude / 1000:F2} km |F| {f.magnitude:F3} kN drag {drag:F3} kN trim {TrajApi.ModelDouble("LastTrimInput"):F4}");
                    }
                }
                File.WriteAllText(Path.Combine(ctx.ResultsDir, "topdrift.csv"), csv.ToString());
                yield return Wait.Seconds(sampleSec);
            }
            TrajApi.HoldWithSmartASS = false;
        }

        /// <summary>
        /// Frames at Trajectories' space-to-atmosphere handoff: the vessel's own state (GetWorldPos3D - body,
        /// obt_velocity) against its KSP orbit's (SwapYZ of getRelativePositionAtUT / getOrbitalVelocityAtUT now), and
        /// against an Orbit rebuilt from those with UpdateFromStateVectors, as Trajectories' CreateOrbitFromState does,
        /// read back at the same UT and 60 s later against the original orbit. At the sim's start (140.9 km, below
        /// RSS Earth's 145 km inverse-rotation threshold) and again placed at 150 km.
        /// </summary>
        [UITest(Requires = "RP-1", StartsAt = StartState.Vab, Craft = "Mk2 SSTO Big", TimeoutSec = 900)]
        public static IEnumerator Traj_RP1_FrameCheck(TestContext ctx)
        {
            Ugui.DismissPopups();
            yield return Nav.EnsureState(ctx, StartState.Vab, ctx.CraftPath(ctx.Param("craft", "Mk2 SSTO Big")));
            Ugui.DismissPopups();
            yield return Rp1.ConfigureSim(new SimSpec
            {
                StartInOrbit = true, Mode = "Reentry", PeKm = 70, Arrival = "Apoapsis", ApKm = 150, EntryAltKm = 140,
                IncDeg = 0, LanDeg = 0, ArgPeDeg = 0, UT = 2019828800, Attitude = "Prograde", LogTelemetry = false, PropellantPct = 0,
            });
            yield return Rp1.StartSim();
            yield return Wait.Seconds(3);
            CheckFrames(ctx, "low.");
            Flight.SetOrbitAtMeanAnomaly(70000, 150000, 29.8, 161.05, 87.6, Math.PI);
            yield return Wait.Seconds(3);
            CheckFrames(ctx, "high.");
        }

        private static void CheckFrames(TestContext ctx, string k)
        {
            var v = FlightGlobals.ActiveVessel;
            CelestialBody body = v.mainBody;
            double ut = Planetarium.GetUniversalTime();
            Vector3d pos = v.GetWorldPos3D() - body.position;
            Vector3d vel = v.obt_velocity;
            Vector3d oPos = v.orbit.getRelativePositionAtUT(ut).xzy;
            Vector3d oVel = v.orbit.getOrbitalVelocityAtUT(ut).xzy;
            var rebuilt = new Orbit();
            rebuilt.UpdateFromStateVectors(oPos.xzy, oVel.xzy, body, ut);
            Vector3d rPos = rebuilt.getRelativePositionAtUT(ut).xzy, rVel = rebuilt.getOrbitalVelocityAtUT(ut).xzy;
            Vector3d rPos60 = rebuilt.getRelativePositionAtUT(ut + 60).xzy, oPos60 = v.orbit.getRelativePositionAtUT(ut + 60).xzy;
            Vector3d rotVel = body.getRFrmVel(body.position + pos);
            ctx.Record(k + "altKm", Math.Round(v.altitude / 1000, 2));
            ctx.Record(k + "inverseRotation", body.inverseRotation);
            ctx.Record(k + "vesselVsOrbitPosM", Math.Round((pos - oPos).magnitude, 1));
            ctx.Record(k + "vesselVsOrbitVelMs", Math.Round((vel - oVel).magnitude, 2));
            ctx.Record(k + "rotFrameVelMs", Math.Round(rotVel.magnitude, 1));
            ctx.Record(k + "vesselVelMinusOrbitVel_dotRot", Math.Round(Vector3d.Dot(vel - oVel, rotVel.normalized), 2));
            ctx.Record(k + "rebuiltVsOrbitPosM", Math.Round((rPos - oPos).magnitude, 1));
            ctx.Record(k + "rebuiltVsOrbitVelMs", Math.Round((rVel - oVel).magnitude, 2));
            ctx.Record(k + "rebuiltVsOrbitPos60sM", Math.Round((rPos60 - oPos60).magnitude, 1));
            ctx.Record(k + "orbitSpeed", Math.Round(oVel.magnitude, 1));
            ctx.Record(k + "srfSpeed", Math.Round(v.srf_velocity.magnitude, 1));
        }

        // Closest the ground track of a circular orbit comes to a point over the next 1.2 revolutions (km), with the
        // body turning underneath, and the argument of latitude (deg) there.
        private static double TrackPassKm(CelestialBody body, double inc, double r, double lan, double now, double period,
                                          double lat, double lon, out double argLatDeg)
        {
            var o = new Orbit(inc, 0, r, lan, 0, 0, now, body);
            double best = double.PositiveInfinity;
            argLatDeg = 0;
            const int n = 864;
            for (int i = 0; i <= n; ++i)
            {
                double t = now + 1.2 * period * i / n;
                Vector3d p = o.getPositionAtUT(t);
                double pLat = body.GetLatitude(p);
                double pLon = body.GetLongitude(p) - (t - now) / body.rotationPeriod * 360.0;
                double d = GreatCircleDeg(pLat, pLon, lat, lon) * Math.PI / 180 * body.Radius / 1000;
                if (d < best)
                {
                    best = d;
                    argLatDeg = 360.0 * 1.2 * i / n;
                }
            }
            return best;
        }

        // Places the vessel (turned to the profile's attitude, so the autopilot has nothing to slew and the force cache
        // nothing odd to learn), then takes the median impact of three fresh predictions, since near the top of the
        // atmosphere successive predictions can scatter.
        // Holds the active vessel at the descent profile's angle, wings level, every physics frame (KscEntry's pin=on).
        /// <summary>Runs Flight.MomentBreakdown as the vessel passes each altitude (km) in Alts, highest first.</summary>
        private sealed class MomentProbe : UnityEngine.MonoBehaviour
        {
            internal TestContext Ctx;
            internal string K;
            internal System.Collections.Generic.Queue<double> Alts;

            private string pendingPulse, pendingSeq, pendingRb;
            internal bool RbWritesOn;

            // solverIters: kept on every part each frame (the aim's placements put the vessel on rails and back)
            internal int SolverIters;
            // maxAngVel: every part's Rigidbody.maxAngularVelocity (rad/s; KSP's is 50), kept each frame
            internal float MaxAngVel;
            // wingArcShots: screenshots of FAR's wing interaction overlay at each altitude, from above and from the side
            internal bool WingArcShots;

            private System.Collections.IEnumerator ArcShots(string key)
            {
                FlightCamera cam = FlightCamera.fetch;
                Vessel v = FlightGlobals.ActiveVessel;
                if (cam == null || v == null) yield break;
                // the camera sized to the vessel, the UI hidden
                float size = 0;
                foreach (Part p in v.parts)
                    size = Math.Max(size, 2 * (p.transform.position - v.CoM).magnitude);
                GameEvents.onHideUI.Fire();
                try
                {
                    foreach (var (name, pitch, hdg, dist) in new[] { ("above", 1.35f, 0f, 1.6f), ("side", 0.12f, 1.5708f, 1.5f), ("rear", 0.5f, 3.1416f, 1.5f) })
                    {
                        cam.camPitch = pitch;
                        cam.camHdg = hdg;
                        cam.SetDistance(Math.Max(20f, dist * size));
                        for (int i = 0; i < 6; ++i) yield return new UnityEngine.WaitForEndOfFrame();
                        UnityEngine.ScreenCapture.CaptureScreenshot(Path.Combine(Ctx.ResultsDir, $"wingarcs_{key}km_{name}.png"));
                        yield return new UnityEngine.WaitForEndOfFrame();
                        yield return new UnityEngine.WaitForEndOfFrame();
                    }
                }
                finally
                {
                    GameEvents.onShowUI.Fire();
                }
            }

            private void Update()
            {
                Vessel v = FlightGlobals.ActiveVessel;
                if (MaxAngVel > 0 && v != null)
                    foreach (Part part in v.parts)
                        if (part.Rigidbody != null && part.Rigidbody.maxAngularVelocity != MaxAngVel)
                            part.Rigidbody.maxAngularVelocity = MaxAngVel;
                if (SolverIters > 0 && v != null)
                    foreach (Part part in v.parts)
                        if (part.Rigidbody != null && part.Rigidbody.solverVelocityIterations != SolverIters)
                        {
                            part.Rigidbody.solverIterations = SolverIters;
                            part.Rigidbody.solverVelocityIterations = SolverIters;
                        }
                if (pendingRb != null && RbWrites.WindowDone)
                {
                    Ctx.Record(K + "rbWrites_" + pendingRb, RbWrites.Report());
                    pendingRb = null;
                }
                if (pendingSeq != null && QueuedTorque.SequenceDone)
                {
                    Ctx.Record(K + "pulseSeq_" + pendingSeq, QueuedTorque.SequenceReport());
                    pendingSeq = null;
                }
                // a torque pulse started at the last altitude: its answer once done
                if (pendingPulse != null)
                {
                    double accel = QueuedTorque.PulseAccel();
                    if (!double.IsNaN(accel))
                    {
                        Ctx.Record(K + "pulse_" + pendingPulse, string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "+2000 kN m on the root for 50 steps: pitch accel {0:E2} rad/s2 (implied I {1:E2} kN m s2)", accel, 2000.0 / accel));
                        pendingPulse = null;
                    }
                }
                if (v == null || Alts == null || Alts.Count == 0 || v.altitude / 1000.0 > Alts.Peek())
                    return;
                double km = Alts.Dequeue();
                string key = km.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Ctx.Record(K + "moment_" + key, FarApi.MomentBreakdown(v));
                // the control surfaces as flight has them, against the pitch input and the model's trim at this state
                Ctx.Record(K + "surfaces_" + key, string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "input {0:F3} q {1:F1} kPa mach {2:F2} | {3}", v.ctrlState.pitch, v.dynamicPressurekPa, v.mach, FarApi.SurfaceStates(v)));
                // the model's moment against AoA and input here and now (AoaSweep, with its moments), to set against the
                // same sweep made at placement, before the surfaces had moved
                if (Sweep)
                {
                    Ctx.Record(K + "sweepLive_" + key, AoaSweep(v.altitude, v.srfSpeed, 4.0, 0.5, true));
                    Ctx.Record(K + "trimLive_" + key, TrimLive(v));
                    Ctx.Record(K + "wingsLiveSim_" + key, WingLiveVsSim.Compare(v));
                    Ctx.Record(K + "queued_" + key, QueuedTorque.Report());
                    Ctx.Record(K + "joints_" + key, WingLiveVsSim.SurfaceJoints(v));
                    Ctx.Record(K + "foreign_" + key, QueuedTorque.ForeignBodies(v));
                    Ctx.Record(K + "centres_" + key, QueuedTorque.CentreCheck(v));
                    if (WingArcShots)
                        StartCoroutine(ArcShots(key));
                    if (RbWritesOn)
                    {
                        RbWrites.Window(1.0);
                        pendingRb = key;
                    }
                    if (PulseTest)
                    {
                        QueuedTorque.Pulse(2000.0, 50);
                        pendingPulse = key;
                    }
                    if (PulseSeq)
                    {
                        // the same torque on the root, the heaviest control surface and the heaviest part, each after a
                        // baseline of nothing over as many steps: does the vessel answer a torque wherever it goes in?
                        Part root = v.rootPart;
                        Part surface = v.parts.Where(p => p.Rigidbody != null && p.Modules.Cast<PartModule>().Any(m => m.GetType().Name == "FARControllableSurface"))
                                              .OrderByDescending(p => p.Rigidbody.mass).FirstOrDefault();
                        Part heavy = v.parts.Where(p => p.Rigidbody != null).OrderByDescending(p => p.Rigidbody.mass).FirstOrDefault();
                        for (int rep = 0; rep < 2; ++rep)
                            foreach (var (label, target) in new[] { ("root", root), ("surface", surface), ("heavy", heavy) })
                            {
                                QueuedTorque.QueuePulse("base", root, 0, 10, 25);
                                QueuedTorque.QueuePulse(label, target, 2000.0, 10, 50);
                            }
                        pendingSeq = key;
                    }
                }
            }

            // The model's own trimmed evaluation (ComputeForces, TrimmedForce) at the flown AoA and half a degree either
            // side, the trim input each settles on, and what it trims with besides the surfaces.
            private static string TrimLive(Vessel v)
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var trajectory = TrajApi.TryFindTypePublic("Trajectories.Trajectory");
                object model = trajectory == null ? null : Reflect.GetStatic(trajectory, "aerodynamicModel_");
                if (model == null) return "no model";
                const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                var t = model.GetType();
                var compute = t.GetMethod("ComputeForces", all, null, new[] { typeof(double), typeof(Vector3d), typeof(Vector3d), typeof(double) }, null);
                var last = t.GetProperty("LastTrimInput", all);
                if (compute == null || last == null) return "no ComputeForces";
                object Member(object o, string name)
                {
                    for (Type c = o.GetType(); c != null; c = c.BaseType)
                    {
                        var f = c.GetField(name, all | System.Reflection.BindingFlags.DeclaredOnly);
                        if (f != null) return f.GetValue(o);
                        var pr = c.GetProperty(name, all | System.Reflection.BindingFlags.DeclaredOnly);
                        if (pr != null) return pr.GetValue(o, null);
                    }
                    return null;
                }
                // the angle the sampler and the prediction ask for here: the profile's
                double aoa = TrajApi.ProfileAoADeg(v);
                Vector3d vup = (v.CoMD - v.mainBody.position).normalized;
                var sb = new System.Text.StringBuilder();
                sb.AppendFormat(inv, "flown aoa {0:F2} input {1:F3};", aoa, v.ctrlState.pitch);
                foreach (double d in new[] { -0.5, 0.0, 0.5 })
                {
                    compute.Invoke(model, new object[] { v.altitude, (Vector3d)v.srf_velocity, vup, (aoa + d) * Math.PI / 180.0 });
                    sb.AppendFormat(inv, " {0:F2}:{1:F3}", aoa + d, Convert.ToDouble(last.GetValue(model, null)));
                }
                object sides = Member(model, "OtherSides");
                sb.AppendFormat(inv, "; other {0} sign {1} wheels {2} seed {3} rcs {4}", Member(model, "OtherPitchTorque"), Member(model, "authoritySign"),
                    sides != null ? Member(sides, "Wheels") : "?", sides != null ? Member(sides, "Seed") : "?", sides != null ? (Member(sides, "Rcs") != null ? "yes" : "no") : "?");
                return sb.ToString();
            }

            internal bool Sweep, PulseTest, PulseSeq;
        }

        private sealed class AttitudePin : UnityEngine.MonoBehaviour
        {
            private void FixedUpdate()
            {
                Vessel v = FlightGlobals.ActiveVessel;
                if (v == null || v.packed)
                    return;
                double aoa = TrajApi.ProfileAoADeg(v);
                if (!double.IsNaN(aoa))
                    Flight.SteerToAoA(aoa);
            }
        }

        // KscEntry's startProbeKm: the first time the descending vessel passes each altitude, predictions from that same
        // instant as Trajectories makes it, and with the density scale and/or force cache off. Each predicted impact
        // recorded as <k>probe<km>_<variant>Lat/Lon.
        private sealed class StartStateProbe : UnityEngine.MonoBehaviour
        {
            internal TestContext Ctx;
            internal string K;
            internal double[] AltitudesKm;
            private int next;

            // name, FARModel.UseGameDensity (the density scale to the predicted point), force cache
            private static readonly (string name, bool gameDensity, bool cache)[] Variants =
            {
                ("stock", true, true), ("noScale", false, true), ("noCache", true, false), ("noCacheNoScale", false, false),
            };

            private void Update()
            {
                Vessel v = FlightGlobals.ActiveVessel;
                if (v == null || v.packed || AltitudesKm == null || next >= AltitudesKm.Length
                    || v.altitude >= AltitudesKm[next] * 1000 || v.verticalSpeed >= 0)
                    return;
                string km = AltitudesKm[next].ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                ++next;
                // The two densities FARModel.DensityScale divides, at the vessel itself (their ratio should be 1 there),
                // and the game's: DensityAt(altitude) - what the forces were computed in - against StockAeroUtil at the
                // vessel's position; and which atmosphere FARModel thinks FAR predicts in.
                try
                {
                    var all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                              | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
                    Type trajType = null, farModelType = null;
                    foreach (var a in AssemblyLoader.loadedAssemblies)
                    {
                        trajType = trajType ?? a.assembly.GetType("Trajectories.Trajectory", false);
                        farModelType = farModelType ?? a.assembly.GetType("Trajectories.FARModel", false);
                    }
                    object model = trajType?.GetField("aerodynamicModel_", all)?.GetValue(null);
                    object predictsAt = farModelType?.GetMethod("PredictsAtPosition", all)?.Invoke(null, null);
                    object densityAt = model?.GetType().GetMethod("DensityAt", all)?.Invoke(model, new object[] { v.altitude });
                    Ctx.Record(K + $"probe{km}_predictsAtPosition", predictsAt ?? "n/a");
                    Ctx.Record(K + $"probe{km}_densityAt", densityAt != null ? ((double)densityAt).ToString("E6") : "n/a");
                    Ctx.Record(K + $"probe{km}_stockAtVessel", TrajApi.StockDensityAt(v.mainBody, v.GetWorldPos3D()).ToString("E6"));
                    Ctx.Record(K + $"probe{km}_atmDensity", v.atmDensity.ToString("E6"));
                }
                catch (Exception e)
                {
                    Ctx.Record(K + $"probe{km}_densityProbeError", e.GetType().Name + ": " + e.Message);
                }
                try
                {
                    bool cacheWas = TrajApi.UseCache;
                    foreach (var (name, gameDensity, cache) in Variants)
                    {
                        TrajApi.SetStaticBool("Trajectories.FARModel", "UseGameDensity", gameDensity);
                        TrajApi.UseCache = cache && cacheWas;
                        if (TrajApi.PredictNow() && TrajApi.ImpactLatLon(out double la, out double lo, out _))
                        {
                            Ctx.Record(K + $"probe{km}_{name}Lat", Math.Round(la, 5));
                            Ctx.Record(K + $"probe{km}_{name}Lon", Math.Round(lo, 5));
                        }
                    }
                    TrajApi.UseCache = cacheWas;
                    Ctx.Record(K + $"probe{km}_altKm", Math.Round(v.altitude / 1000, 2));
                }
                finally
                {
                    TrajApi.SetStaticBool("Trajectories.FARModel", "UseGameDensity", true);
                }
            }
        }

        // KscEntry's biasSweepKm: the first time the descending vessel passes each altitude, predictions from that same
        // instant with the AoA bias (DescentProfile.AoABiasRad, added to the profile all the way down) at each of
        // BiasesDeg, to see how the impact answers the bias the AoA steering steps. Each predicted impact recorded as
        // <k>sweep<km>_b<deg>Lat/Lon. Meant for steer=off, where nothing else sets the bias.
        private sealed class BiasSweepProbe : UnityEngine.MonoBehaviour
        {
            internal TestContext Ctx;
            internal string K;
            internal double[] AltitudesKm, BiasesDeg;
            private int next;

            private void Update()
            {
                Vessel v = FlightGlobals.ActiveVessel;
                if (v == null || v.packed || AltitudesKm == null || next >= AltitudesKm.Length
                    || v.altitude >= AltitudesKm[next] * 1000 || v.verticalSpeed >= 0)
                    return;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                string km = AltitudesKm[next].ToString("F0", inv);
                ++next;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    foreach (double b in BiasesDeg)
                    {
                        TrajApi.SetStaticDouble("Trajectories.DescentProfile", "AoABiasRad", b * Math.PI / 180);
                        string name = K + $"sweep{km}_b{b.ToString("0.##", inv)}";
                        if (TrajApi.PredictNow() && TrajApi.ImpactLatLon(out double la, out double lo, out _))
                        {
                            Ctx.Record(name + "Lat", Math.Round(la, 5));
                            Ctx.Record(name + "Lon", Math.Round(lo, 5));
                        }
                        else
                            Ctx.Record(name + "Lat", "none");
                    }
                    Ctx.Record(K + $"sweep{km}_altKm", Math.Round(v.altitude / 1000, 2));
                    Ctx.Record(K + $"sweep{km}_mach", Math.Round(v.mach, 2));
                }
                finally
                {
                    TrajApi.SetStaticDouble("Trajectories.DescentProfile", "AoABiasRad", 0.0);
                    Ctx.Record(K + $"sweep{km}_ms", watch.ElapsedMilliseconds);
                }
            }
        }

        // Switches RCS off once, the first time the descending active vessel is below AltitudeM (KscEntry's rcsOffBelowKm).
        private sealed class RcsCutoff : UnityEngine.MonoBehaviour
        {
            internal double AltitudeM;
            private bool done;

            private void Update()
            {
                Vessel v = FlightGlobals.ActiveVessel;
                if (done || v == null || v.altitude >= AltitudeM || v.verticalSpeed >= 0)
                    return;
                done = true;
                v.ActionGroups.SetGroup(KSPActionGroup.RCS, false);
                Log.Info($"RCS off below {AltitudeM / 1000:F0} km");
            }
        }

        // Sets the physics warp rate as the active vessel descends past each step's altitude (KscEntry's warpSchedule).
        private sealed class WarpSchedule : UnityEngine.MonoBehaviour
        {
            internal double[][] Steps;   // { rate, altitude m }
            internal bool Hyper;         // TimeControl's attempted hyper-warp rate instead of physics warp
            private int next;

            private void Update()
            {
                Vessel v = FlightGlobals.ActiveVessel;
                if (v == null || Steps == null || next >= Steps.Length || v.altitude >= Steps[next][1])
                    return;
                Log.Info($"warp schedule: {Steps[next][0]}x below {Steps[next][1] / 1000:F0} km");
                if (Hyper)
                    Flight.SetHyperWarpRate(Steps[next][0]);
                else if (Steps[next][0] > 1)
                    Flight.PhysicsWarpRate(Steps[next][0]);
                else
                    Flight.WarpOff();
                ++next;
            }
        }

        // Records the median impact of three fresh predictions, started after the call, as <prefix>Lat_<tag> / Lon_<tag>.
        private static IEnumerator MeasureImpact(string prefix, string tag, TestContext ctx)
        {
            int start = TrajApi.CompletedPredictions;
            yield return Wait.Until(() => TrajApi.CompletedPredictions >= start + 2, 60, "fresh predictions", shot: false);
            var lats = new System.Collections.Generic.List<double>();
            var lons = new System.Collections.Generic.List<double>();
            for (int i = 0; i < 3; ++i)
            {
                int seen = TrajApi.CompletedPredictions;
                yield return Wait.Until(() => TrajApi.CompletedPredictions > seen, 60, "another prediction", shot: false);
                if (TrajApi.ImpactLatLon(out double la, out double lo, out _))
                {
                    lats.Add(la);
                    lons.Add(lo);
                }
            }
            if (lats.Count == 0)
                yield break;
            lats.Sort();
            lons.Sort();
            ctx.Record(prefix + "Lat_" + tag, Math.Round(lats[lats.Count / 2], 5));
            ctx.Record(prefix + "Lon_" + tag, Math.Round(lons[lons.Count / 2], 5));
        }

        // When PlaceAtAndMeasure last put the vessel on its orbit (UT).
        private static double lastPlaceUT = double.NaN;

        // How long PlaceAtAndMeasure waits for predictions (real s; KscEntry's aimWaitSec, default 60).
        private static double aimWaitSec = 60;

        // At one altitude and airspeed, for each angle of attack 0..maxDeg: the pitch input at which Trajectories' model
        // (FAR) puts the aerodynamic pitching moment at zero with the surfaces alone - interpolated between the moments at
        // inputs -1, -0.5, 0, 0.5, 1 - or "<-1" / ">1" when no input in range does (the sign of what it would need). The
        // moment is the model's own (ComputeForcesAndMoment_Model, no RCS), in the frame Trajectories trims in.
        // "aoa:input aoa:input ... | free trim aoa" where the free trim is where the moment is zero at input 0.
        /// <summary>
        /// The model's pitching moment against pitch input (-1..1 by 0.05) at an altitude, Mach and angle of attack, on
        /// the main thread as the direct evaluation has it, appended to csv (altKm,mach,aoaDeg,input,momentKNm), and the
        /// trim the model's own solve gives there (ComputeForces, then LastTrimInput): returned as "mach:trim".
        /// </summary>
        private static string MomentSweep(double altitudeM, double mach, double aoaDeg, System.Text.StringBuilder csv)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var trajectory = TrajApi.TryFindTypePublic("Trajectories.Trajectory");
            object model = trajectory == null ? null : Reflect.GetStatic(trajectory, "aerodynamicModel_");
            if (model == null) return "no model";
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var m = model.GetType().GetMethod("ComputeForcesAndMoment_Model", any);
            var compute = model.GetType().GetMethod("ComputeForces", any);
            var sound = model.GetType().GetMethod("SpeedOfSoundAt", any);
            var lastTrim = model.GetType().GetProperty("LastTrimInput", any);
            var clamped = model.GetType().GetProperty("LastComputeClamped", any);
            UnityEngine.Transform t = FlightGlobals.ActiveVessel?.ReferenceTransform;
            if (m == null || compute == null || sound == null || t == null) return "no method";
            double speed = mach * Convert.ToDouble(sound.Invoke(model, new object[] { altitudeM }));
            Vector3d forward = ((Vector3d)t.up).normalized;
            Vector3d up = -((Vector3d)t.forward).normalized;
            Vector3d right = Vector3d.Cross(up, -forward).normalized;
            double rad = aoaDeg * Math.PI / 180.0;
            Vector3d air = (forward * Math.Cos(-rad) + up * Math.Sin(-rad)) * speed;
            for (int i = -20; i <= 20; ++i)
            {
                double input = i * 0.05;
                object[] args = { air, altitudeM, rad, input, right, 0.0 };
                m.Invoke(model, args);
                csv.Append(string.Format(inv, "{0:F2},{1:F3},{2:F2},{3:F2},{4:F3}\n", altitudeM / 1000, mach, aoaDeg, input, (double)args[5]));
            }
            compute.Invoke(model, new object[] { altitudeM, new Vector3d(speed, 0, 0), new Vector3d(0, 1, 0), rad });
            double trim = lastTrim == null ? double.NaN : Convert.ToDouble(lastTrim.GetValue(model, null));
            bool held = clamped != null && Convert.ToBoolean(clamped.GetValue(model, null));
            return mach.ToString("0.000", inv) + ":" + trim.ToString("+0.00;-0.00", inv) + (held ? "c" : "");
        }

        private static string AoaSweep(double altitudeM, double speed, double maxDeg, double stepDeg, bool raw = false)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var trajectory = TrajApi.TryFindTypePublic("Trajectories.Trajectory");
            object model = trajectory == null ? null : Reflect.GetStatic(trajectory, "aerodynamicModel_");
            if (model == null) return "no model";
            var m = model.GetType().GetMethod("ComputeForcesAndMoment_Model",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            UnityEngine.Transform t = FlightGlobals.ActiveVessel?.ReferenceTransform;
            if (m == null || t == null) return "no method";
            Vector3d forward = ((Vector3d)t.up).normalized;
            Vector3d up = -((Vector3d)t.forward).normalized;
            Vector3d right = Vector3d.Cross(up, -forward).normalized;
            double[] inputs = { -1.0, -0.5, 0.0, 0.5, 1.0 };
            double Moment(double aoaRad, double input)
            {
                Vector3d air = (forward * Math.Cos(-aoaRad) + up * Math.Sin(-aoaRad)) * speed;
                object[] args = { air, altitudeM, aoaRad, input, right, 0.0 };
                m.Invoke(model, args);
                return (double)args[5];
            }
            var sb = new System.Text.StringBuilder();
            double prevAoa = double.NaN, prevM0 = double.NaN, freeTrim = double.NaN;
            for (double a = 0; a <= maxDeg + 1e-9; a += stepDeg)
            {
                double rad = a * Math.PI / 180.0;
                double[] ms = Array.ConvertAll(inputs, p => Moment(rad, p));
                string cell = null;
                for (int i = 0; i + 1 < inputs.Length && cell == null; ++i)
                    if (ms[i] == 0.0 || ms[i] * ms[i + 1] < 0.0)
                        cell = (inputs[i] + (inputs[i + 1] - inputs[i]) * ms[i] / (ms[i] - ms[i + 1])).ToString("+0.00;-0.00", inv);
                if (cell == null)
                    // no zero in range: the side the input would have to go beyond (the end whose moment is smaller)
                    cell = Math.Abs(ms[0]) < Math.Abs(ms[4]) ? "<-1" : ">1";
                sb.Append(a.ToString("0.#", inv)).Append(':').Append(cell);
                // sweepRaw=on: the moment at no input and per unit input (kN m), the model's surface authority there
                if (raw)
                    sb.Append(string.Format(inv, "[{0:F0} {1:F0}/u]", ms[2], ms[3] - ms[1]));
                sb.Append(' ');
                double m0 = ms[2];
                if (!double.IsNaN(prevM0) && prevM0 * m0 < 0.0 && double.IsNaN(freeTrim))
                    freeTrim = prevAoa + (a - prevAoa) * prevM0 / (prevM0 - m0);
                prevAoa = a;
                prevM0 = m0;
            }
            sb.Append("| free trim ").Append(double.IsNaN(freeTrim) ? "none" : freeTrim.ToString("0.0", inv));
            return sb.ToString();
        }

        // The non-aerodynamic pitch torque's sources, against Trajectories' seed: the reaction wheels' pitch torque
        // (every ITorqueProvider that is neither RCS nor a lifting surface), and per RCS part type what one unit of
        // firing fraction really gives (maxFuelFlow x Isp(p) x G x ispMult x limiter, as ModuleRCS.CalculateThrust)
        // against the thrusterPower x Isp(p)/Isp(0) x limiter the model takes.
        private static void RecordTorqueSources(TestContext ctx, string k)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            double wheels = 0;
            var rcsTypes = new System.Collections.Generic.Dictionary<string, string>();
            var exhaust = typeof(ModuleRCS).GetField("exhaustVel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    if (m is ModuleRCS rcs)
                    {
                        if (rcsTypes.ContainsKey(p.partInfo.name)) continue;
                        double pAtm = p.staticPressureAtm;
                        double ispVac = rcs.atmosphereCurve.Evaluate(0f), ispP = rcs.atmosphereCurve.Evaluate((float)pAtm);
                        double real = rcs.maxFuelFlow * ispP * rcs.G * rcs.ispMult * rcs.thrustPercentage * 0.01;
                        double model = rcs.thrusterPower * (ispVac > 0 ? ispP / ispVac : 1) * rcs.thrustPercentage * 0.01;
                        double ev = exhaust != null ? Convert.ToDouble(exhaust.GetValue(rcs)) : double.NaN;
                        rcsTypes[p.partInfo.name] = string.Format(inv,
                            "thrusterPower {0:F4} limiter {1:F0} ispVac {2:F1} ispP {3:F1} ispMult {4:F3} maxFuelFlow {5:E4} exhaustVel {6:F1} " +
                            "realISP {7:F1} EPSILON {8:F3} thrusters {9} real/model {10:F4}",
                            rcs.thrusterPower, rcs.thrustPercentage, ispVac, ispP, rcs.ispMult, rcs.maxFuelFlow, ev, rcs.realISP,
                            rcs.EPSILON, rcs.thrusterTransforms?.Count ?? 0, model > 0 ? real / model : double.NaN);
                        continue;
                    }
                    if (!(m is ITorqueProvider tp) || m is ModuleLiftingSurface || m.GetType().Namespace?.StartsWith("ferram4") == true
                        || m.GetType().Namespace?.StartsWith("FerramAerospaceResearch") == true)
                        continue;
                    try
                    {
                        tp.GetPotentialTorque(out UnityEngine.Vector3 pos, out UnityEngine.Vector3 neg);
                        double pitch = 0.5 * (Math.Abs(pos.x) + Math.Abs(neg.x));
                        if (pitch > 0) ctx.Record(k + "torqueSource_" + p.partInfo.name + "_" + m.moduleName, Math.Round(pitch, 2));
                        wheels += pitch;
                    }
                    catch (Exception) { }
                }
            ctx.Record(k + "torqueWheelsKNm", Math.Round(wheels, 2));
            ctx.Record(k + "torqueSeedKNm", Math.Round(TrajApi.ModelDouble("SeedPitchTorque"), 2));
            // builds with a torque each way (OtherSides): the non-aerodynamic torque for a full positive / negative input
            ctx.Record(k + "torqueOtherPositiveKNm", Math.Round(TrajApi.ModelDouble("OtherPitchTorquePositive"), 2));
            ctx.Record(k + "torqueOtherNegativeKNm", Math.Round(TrajApi.ModelDouble("OtherPitchTorqueNegative"), 2));
            foreach (var kv in rcsTypes)
                ctx.Record(k + "rcsType_" + kv.Key, kv.Value);
        }

        private static IEnumerator PlaceAtAndMeasure(CelestialBody body, double peKm, double apKm, double inc, double lan, double argPe, double meanAnomaly,
                                                     double aoaDeg, double tLat, double tLon, double[] miss)
        {
            Flight.SetOrbitAtMeanAnomaly(peKm * 1000, apKm * 1000, inc, lan, argPe, meanAnomaly);
            lastPlaceUT = Planetarium.GetUniversalTime();
            yield return null;
            Flight.PointAtAoA(aoaDeg);
            int start = TrajApi.CompletedPredictions;
            float waitStart = UnityEngine.Time.realtimeSinceStartup;
            int frameStart = UnityEngine.Time.frameCount;
            double Counter(string name) => TrajApi.StaticDouble("Trajectories.Trajectory", name);
            double inc0 = Counter("IncrementCount"), steps0 = Counter("StepCount"), starved0 = Counter("StarvedStepCount");
            yield return Wait.Until(() => TrajApi.CompletedPredictions >= start + 2, aimWaitSec, "fresh predictions", shot: false);
            Log.Info($"aim: two fresh predictions in {UnityEngine.Time.realtimeSinceStartup - waitStart:F1} s, " +
                     $"{UnityEngine.Time.frameCount - frameStart} frames, {Counter("IncrementCount") - inc0} increments, " +
                     $"{Counter("StepCount") - steps0} steps, {Counter("StarvedStepCount") - starved0} starved");
            var lats = new System.Collections.Generic.List<double>();
            var lons = new System.Collections.Generic.List<double>();
            for (int i = 0; i < 3; ++i)
            {
                int seen = TrajApi.CompletedPredictions;
                if (i > 0)
                    yield return Wait.Until(() => TrajApi.CompletedPredictions > seen, aimWaitSec, "another prediction", shot: false);
                if (TrajApi.ImpactLatLon(out double la, out double lo, out _))
                {
                    lats.Add(la);
                    lons.Add(lo);
                }
            }
            miss[0] = miss[1] = double.NaN;
            if (lats.Count == 0)
                yield break;
            lats.Sort();
            lons.Sort();
            double iLat = lats[lats.Count / 2], iLon = lons[lons.Count / 2];
            double dLon = tLon - iLon;
            while (dLon > 180) dLon -= 360;
            while (dLon < -180) dLon += 360;
            double rKm = body.Radius / 1000;
            miss[0] = dLon * Math.PI / 180 * rKm * Math.Cos(tLat * Math.PI / 180);
            miss[1] = (tLat - iLat) * Math.PI / 180 * rKm;
            Log.Info($"aim: inc {inc:F3} LAN {lan:F3} argPe {argPe:F3} M {meanAnomaly:F4} -> impact {iLat:F3}/{iLon:F3}, miss E {miss[0]:F1} N {miss[1]:F1} km");
        }

        private static double GreatCircleDeg(double lat1, double lon1, double lat2, double lon2)
        {
            double d2r = Math.PI / 180;
            double c = Math.Sin(lat1 * d2r) * Math.Sin(lat2 * d2r) + Math.Cos(lat1 * d2r) * Math.Cos(lat2 * d2r) * Math.Cos((lon2 - lon1) * d2r);
            return Math.Acos(Math.Max(-1, Math.Min(1, c))) / d2r;
        }

        private static double Wrap360(double deg)
        {
            deg %= 360;
            return deg < 0 ? deg + 360 : deg;
        }

        private static IEnumerator RssProfileToGround(TestContext ctx, double steerAtKm)
        {
            Ugui.DismissPopups();
            var plan = TrajProfileFlight.Setup(ctx, "40/40/10/0h");
            string k = plan.K;
            string craft = ctx.Param("craft", "Mk2 SSTO Big");
            double physWarp = ctx.ParamDouble("physWarp", 4);

            ctx.Step($"Open {craft} in the VAB");
            yield return Nav.EnsureState(ctx, StartState.Vab, ctx.CraftPath(craft));
            Ugui.DismissPopups();
            double propellant = ctx.ParamDouble("propellant", 0);
            ctx.Record(k + "propellantDirect", Rp1.ApplyPropellantLoadDirect(propellant));

            ctx.Step("Simulate the entry");
            yield return Rp1.ConfigureSim(new SimSpec
            {
                StartInOrbit = true, Mode = "Reentry",
                PeKm = ctx.ParamDouble("peKm", 70), Arrival = "Apoapsis", ApKm = ctx.ParamDouble("apKm", 150),
                EntryAltKm = ctx.ParamDouble("startAltKm", 140),
                IncDeg = ctx.ParamDouble("incDeg", 0), LanDeg = 0, ArgPeDeg = 0,
                UT = ctx.ParamDouble("simUT", 2019800000) > 0 ? ctx.ParamDouble("simUT", 2019800000) : (double?)null,
                Attitude = "Prograde", LogTelemetry = false, PropellantPct = propellant,
            });
            yield return Rp1.StartSim();
            Assert.Equal("Reentry", Rp1.SimMode, "SimOrbitMode");

            var v0 = FlightGlobals.ActiveVessel;
            int partsStart = v0.parts.Count;
            ctx.Record(k + "spawnAltKm", Math.Round(v0.altitude / 1000, 1));
            ctx.Record(k + "massT", Math.Round(v0.GetTotalMass(), 3));
            // Same flight setup as the other reentry tests: gear up before FAR voxelizes, let the voxelization settle
            // before RCS starts slewing, no heat deaths, no propellant drain.
            ctx.Record(k + "gearUp", Ctrl.RetractGear());
            Ctrl.SetThrottle(0f);
            yield return Wait.Seconds(5);
            ctx.Record(k + "rcsThrusters", Ctrl.EnableRcs());
            FlightGlobals.ActiveVessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            Flight.IgnoreMaxTemperature(true);
            Flight.InfinitePropellant(true);

            yield return TrajProfileFlight.Engage(ctx, plan);
            if (physWarp > 1) Flight.PhysicsWarpRate(physWarp);
            yield return TrajProfileFlight.Fly(ctx, plan, steerAtKm, partsStart, physWarp, 60, 10, 5400);
        }

        private static IEnumerator RunPass(TestContext ctx, bool useCache)
        {
            string k = (useCache ? "cacheOn." : "cacheOff.");
            string craft = ctx.Param("craft", "Mk2 SSTO Big");
            double aoa = ctx.ParamDouble("aoa", 40);
            double endAltKm = ctx.ParamDouble("endAltKm", 25);
            double sampleSec = ctx.ParamDouble("sampleSec", 10);
            double physWarp = ctx.ParamDouble("physWarp", 4);
            double flightTimeout = ctx.ParamDouble("flightTimeoutSec", 2400);

            ctx.Step($"[cache {(useCache ? "on" : "off")}] Open {craft} in the VAB");
            yield return Nav.EnsureState(ctx, StartState.Vab, ctx.CraftPath(craft));
            Ugui.DismissPopups();

            double propellant = ctx.ParamDouble("propellant", 0);
            ctx.Record(k + "propellantDirect", Rp1.ApplyPropellantLoadDirect(propellant));

            ctx.Step($"[cache {(useCache ? "on" : "off")}] Simulate the entry");
            var spec = new SimSpec
            {
                StartInOrbit = true, Mode = "Reentry",
                PeKm = ctx.ParamDouble("peKm", 70), Arrival = "Apoapsis", ApKm = ctx.ParamDouble("apKm", 150),
                EntryAltKm = ctx.ParamDouble("startAltKm", 140),
                IncDeg = ctx.ParamDouble("incDeg", 0), LanDeg = 0, ArgPeDeg = 0,
                // A fixed start time so both passes fly the same entry over the same ground.
                UT = ctx.ParamDouble("simUT", 2019800000) > 0 ? ctx.ParamDouble("simUT", 2019800000) : (double?)null,
                Attitude = "Prograde", LogTelemetry = true, PropellantPct = propellant,
            };
            yield return Rp1.ConfigureSim(spec);
            yield return Rp1.StartSim();
            Assert.Equal("Reentry", Rp1.SimMode, "SimOrbitMode");

            var v0 = FlightGlobals.ActiveVessel;
            int partsStart = v0.parts.Count;
            ctx.Record(k + "spawnAltKm", Math.Round(v0.altitude / 1000, 1));

            // Same flight setup as the validated reentry tests: gear up before FAR voxelizes, let the
            // voxelization settle before RCS starts slewing, no heat deaths, no propellant drain.
            ctx.Record(k + "gearUp", Ctrl.RetractGear());
            Ctrl.SetThrottle(0f);
            yield return Wait.Seconds(5);
            ctx.Record(k + "rcsThrusters", Ctrl.EnableRcs());
            FlightGlobals.ActiveVessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            Flight.IgnoreMaxTemperature(true);
            Flight.InfinitePropellant(true);

            ctx.Step($"[cache {(useCache ? "on" : "off")}] Set a constant {aoa} deg profile and hold it");
            TrajEntry.ConfigureProfile(ctx, k, useCache, aoa);
            Mj.SmartAssSurfaceProgradeOffset(aoa, roll: 0, retrograde: false);
            if (physWarp > 1) Flight.PhysicsWarpRate(physWarp);

            yield return TrajEntry.Sample(ctx, k, aoa, sampleSec,
                new TrajEntry.EntryOptions { EndAltKm = endAltKm, SlowBelowAltKm = 90, SlowSpeed = 2000, CrossAltKm = ctx.ParamDouble("crossAltKm", 60) },
                flightTimeout, partsStart, physWarp);
            try { Mj.SmartAssOff(); } catch (UITestFailure) { }
        }

        /// <summary>
        /// Which axis of Trajectories' force cache carries its error. The cache is a 32 x 33 x 32 table over
        /// (speed, AoA, altitude) read by trilinear interpolation, so its nodes sit at fixed intervals:
        /// 10000/31 = 322.6 m/s, 360/32 = 11.25 deg, atmosphereDepth/31 = 4516 m on RSS Earth. Interpolating a
        /// curved function linearly between nodes is exact AT the nodes and worst in between, so if an axis is
        /// the culprit its error must fall to zero at that axis's node values and peak mid-cell.
        ///
        /// Both force paths take the velocity and position as arguments, so each axis is swept at a fixed
        /// reference state without flying there. The vehicle only has to hold a steady attitude, since that is
        /// the frame ComputeForces builds its fixed-AoA velocity in. Also times the uncached call, which is the
        /// FARAPI.CalculateVesselAeroForces the predictor makes four times per RK4 step.
        ///
        /// Params: craft, aoa/speed/altKm (the reference state), settleSec, timingCalls, physWarp.
        /// </summary>
        [UITest(Requires = "RP-1,MechJeb", StartsAt = StartState.Vab, Craft = "Mk2 SSTO Big", TimeoutSec = 2400)]
        public static IEnumerator Traj_CacheAxes(TestContext ctx)
        {
            Ugui.DismissPopups();
            Assert.True(TrajApi.Available, "Trajectories is installed and its API resolved");
            ctx.Record("trajectoriesVersion", TrajApi.Version);

            string craft = ctx.Param("craft", "Mk2 SSTO Big");
            double refAoA = ctx.ParamDouble("aoa", 40);
            double refSpeed = ctx.ParamDouble("speed", 5400);
            double refAlt = ctx.ParamDouble("altKm", 70) * 1000;

            yield return Nav.EnsureState(ctx, StartState.Vab, ctx.CraftPath(craft));
            Ugui.DismissPopups();
            ctx.Record("propellantDirect", Rp1.ApplyPropellantLoadDirect(ctx.ParamDouble("propellant", 0)));

            ctx.Step("Simulate an entry and settle at the reference attitude");
            yield return Rp1.ConfigureSim(new SimSpec
            {
                StartInOrbit = true, Mode = "Reentry",
                PeKm = ctx.ParamDouble("peKm", 70), Arrival = "Apoapsis", ApKm = ctx.ParamDouble("apKm", 150),
                EntryAltKm = ctx.ParamDouble("startAltKm", 105), IncDeg = 0, LanDeg = 0, ArgPeDeg = 0,
                UT = ctx.ParamDouble("simUT", 2019800000) > 0 ? ctx.ParamDouble("simUT", 2019800000) : (double?)null,
                Attitude = "Prograde", LogTelemetry = false, PropellantPct = ctx.ParamDouble("propellant", 0),
            });
            yield return Rp1.StartSim();
            ctx.Record("gearUp", Ctrl.RetractGear());
            Ctrl.SetThrottle(0f);
            yield return Wait.Seconds(5);
            ctx.Record("rcsThrusters", Ctrl.EnableRcs());
            FlightGlobals.ActiveVessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            Flight.IgnoreMaxTemperature(true);
            Flight.InfinitePropellant(true);

            // The cache must be on, or GetForces is just ComputeForces and every error reads zero.
            TrajApi.AlwaysUpdate = true;
            TrajApi.UseCache = true;
            TrajApi.ResetProfile(refAoA);
            Mj.SmartAssSurfaceProgradeOffset(refAoA, roll: 0, retrograde: false);
            double physWarp = ctx.ParamDouble("physWarp", 4);
            if (physWarp > 1) Flight.PhysicsWarpRate(physWarp);
            yield return Wait.Seconds(ctx.ParamDouble("settleSec", 60));
            if (physWarp > 1) Flight.WarpOff();
            yield return Wait.Seconds(2);

            var v = FlightGlobals.ActiveVessel;
            ctx.Record("aeroModel", TrajApi.ModelName);
            ctx.Record("sweepAtAltKm", Math.Round(v.altitude / 1000, 1));
            ctx.Record("sweepAtSpeed", Math.Round(v.srfSpeed));
            ctx.Record("sweepAtAoA", Math.Round(Flight.AoA, 2));
            // The node spacings the sweeps should show zeros at.
            ctx.Record("nodeSpacingSpeed", Math.Round(10000.0 / 31.0, 2));
            ctx.Record("nodeSpacingAoADeg", Math.Round(360.0 / 32.0, 3));
            ctx.Record("nodeSpacingAltM", Math.Round(v.mainBody.atmosphereDepth / 31.0, 1));

            ctx.Step("Time the uncached force call");
            int timingCalls = (int)ctx.ParamDouble("timingCalls", 2000);
            double usPerCall = TrajApi.TimeDirectCallUs(v, refAoA, timingCalls);
            // The same loop repeating one state, which FARVesselAero's lastSimResults memo short-circuits.
            // The predictor never repeats a state, so this is the memo's cost, not a solve's - recorded to
            // show the gap rather than to be mistaken for the real one.
            double usMemo = TrajApi.TimeDirectCallUs(v, refAoA, timingCalls, varyState: false);
            ctx.Record("directCallUs", Math.Round(usPerCall, 2));
            ctx.Record("directCallMemoHitUs", Math.Round(usMemo, 2));
            ctx.Record("directCallsTimed", timingCalls);
            // One RK4 step is four of them; a 1 s step over a ~1500 s entry is ~6000 calls.
            ctx.Record("estRecomputeMsAt1500s", Math.Round(usPerCall * 4 * 1500 / 1000.0, 1));
            Log.Info($"uncached force call: {usPerCall:F2} us; 4/step x 1500 steps = {usPerCall * 4 * 1500 / 1000.0:F0} ms per recompute");

            ctx.Step("Sweep each cache axis at the reference state");
            yield return Sweep(ctx, v, "aoa", refAoA, refSpeed, refAlt);
            yield return Sweep(ctx, v, "speed", refAoA, refSpeed, refAlt);
            yield return Sweep(ctx, v, "alt", refAoA, refSpeed, refAlt);
        }

        /// <summary>
        /// Whether FAR's force is a function of (speed, AoA, altitude) at all, which is what Trajectories'
        /// cache assumes by keying on exactly those three. FARWingAerodynamicModel.DetermineStall computes the
        /// wing's stall fraction from its own previous value, so the wings carry history.
        ///
        /// The probe is the same state evaluated twice in the same frame, differing only in what was evaluated
        /// immediately before it: a few calls at a high, stalling angle, or a few at zero. If the model were a
        /// function of its arguments the two must agree exactly. Whatever they differ by is the error any cache
        /// keyed on instantaneous state inherits, because a cell's value then depends on fill order.
        ///
        /// Params: aoa (held through the entry), probeAoA, primeHigh, primeLow, primeCalls, trials, speed, altKm.
        /// </summary>
        [UITest(Requires = "RP-1,MechJeb", StartsAt = StartState.Vab, Craft = "Mk2 SSTO Big", TimeoutSec = 2400)]
        public static IEnumerator Traj_StallPath(TestContext ctx)
        {
            Ugui.DismissPopups();
            Assert.True(TrajApi.Available, "Trajectories is installed and its API resolved");

            string craft = ctx.Param("craft", "Mk2 SSTO Big");
            double heldAoA = ctx.ParamDouble("aoa", 40);
            double probeAoA = ctx.ParamDouble("probeAoA", 20);
            double primeHigh = ctx.ParamDouble("primeHigh", 60);
            double primeLow = ctx.ParamDouble("primeLow", 0);
            int primeCalls = (int)ctx.ParamDouble("primeCalls", 3);
            int trials = (int)ctx.ParamDouble("trials", 8);
            double speed = ctx.ParamDouble("speed", 5161.29);
            double alt = ctx.ParamDouble("altKm", 67.74194) * 1000;

            yield return Nav.EnsureState(ctx, StartState.Vab, ctx.CraftPath(craft));
            Ugui.DismissPopups();
            ctx.Record("propellantDirect", Rp1.ApplyPropellantLoadDirect(ctx.ParamDouble("propellant", 0)));

            ctx.Step("Simulate an entry and settle");
            yield return Rp1.ConfigureSim(new SimSpec
            {
                StartInOrbit = true, Mode = "Reentry",
                PeKm = ctx.ParamDouble("peKm", 70), Arrival = "Apoapsis", ApKm = ctx.ParamDouble("apKm", 150),
                EntryAltKm = ctx.ParamDouble("startAltKm", 105), IncDeg = 0, LanDeg = 0, ArgPeDeg = 0,
                UT = ctx.ParamDouble("simUT", 2019800000) > 0 ? ctx.ParamDouble("simUT", 2019800000) : (double?)null,
                Attitude = "Prograde", LogTelemetry = false, PropellantPct = ctx.ParamDouble("propellant", 0),
            });
            yield return Rp1.StartSim();
            ctx.Record("gearUp", Ctrl.RetractGear());
            Ctrl.SetThrottle(0f);
            yield return Wait.Seconds(5);
            ctx.Record("rcsThrusters", Ctrl.EnableRcs());
            FlightGlobals.ActiveVessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            Flight.IgnoreMaxTemperature(true);
            Flight.InfinitePropellant(true);
            TrajApi.AlwaysUpdate = false;   // keep Trajectories' own predictions from moving the wings mid-probe
            Mj.SmartAssSurfaceProgradeOffset(heldAoA, roll: 0, retrograde: false);
            double physWarp = ctx.ParamDouble("physWarp", 4);
            if (physWarp > 1) Flight.PhysicsWarpRate(physWarp);
            yield return Wait.Seconds(ctx.ParamDouble("settleSec", 45));
            if (physWarp > 1) Flight.WarpOff();
            yield return Wait.Seconds(2);

            ctx.Step($"Probe {probeAoA} deg after priming at {primeHigh} deg and at {primeLow} deg");
            var v = FlightGlobals.ActiveVessel;
            var csv = new StringBuilder("trial,stallAfterHighPrime,dragAfterHighPrime,stallAfterLowPrime,dragAfterLowPrime,diffPct\n");
            double maxDiff = 0, sumDiff = 0;
            int n = 0;

            for (int t = 0; t < trials; t++)
            {
                double dHi = double.NaN, dLo = double.NaN, sHi = double.NaN, sLo = double.NaN;
                Vector3d velN = ((Vector3d)v.srf_velocity).normalized;

                for (int i = 0; i < primeCalls; i++) TrajApi.ComputeDirectAt(v, primeHigh, speed, alt, out _);
                if (TrajApi.ComputeDirectAt(v, probeAoA, speed, alt, out Vector3d fHi))
                { dHi = -Vector3d.Dot(fHi, velN); sHi = FarApi.WingStallMean(v); }

                for (int i = 0; i < primeCalls; i++) TrajApi.ComputeDirectAt(v, primeLow, speed, alt, out _);
                if (TrajApi.ComputeDirectAt(v, probeAoA, speed, alt, out Vector3d fLo))
                { dLo = -Vector3d.Dot(fLo, velN); sLo = FarApi.WingStallMean(v); }

                double diff = Math.Abs(dLo) > 1e-6 ? 100.0 * (dHi - dLo) / Math.Abs(dLo) : double.NaN;
                if (!double.IsNaN(diff)) { maxDiff = Math.Max(maxDiff, Math.Abs(diff)); sumDiff += Math.Abs(diff); n++; }
                csv.Append(string.Join(",", new[] { t.ToString(), sHi.ToString("F4"), dHi.ToString("F4"),
                    sLo.ToString("F4"), dLo.ToString("F4"), diff.ToString("F3") })).Append('\n');
                Log.Info($"stallPath trial {t}: afterHigh drag={dHi:F3} stall={sHi:F3}; afterLow drag={dLo:F3} stall={sLo:F3}; diff={diff:F2}%");
                yield return null;   // next frame, so the flight model runs in between
            }

            File.WriteAllText(Path.Combine(ctx.ResultsDir, "stall-path.csv"), csv.ToString());
            ctx.Record("heldAoA", heldAoA);
            ctx.Record("probeAoA", probeAoA);
            ctx.Record("trials", n);
            ctx.Record("maxDiffPct", Math.Round(maxDiff, 3));
            ctx.Record("meanDiffPct", n > 0 ? Math.Round(sumDiff / n, 3) : 0);
            ctx.Record("wingStallLive", Math.Round(FarApi.WingStallMean(v), 4));
            ctx.Record("vesselAoA", Math.Round(Flight.AoA, 2));
            Assert.True(n > 0, "the probe produced trials");

            // Phase 2: the same fixed (speed, AoA, altitude) probed as the vessel flies on. Nothing in the
            // probe's arguments changes, so a pure function of them would return a constant. FAR's gas
            // properties come from the vessel's current position and the clock (FARAtmosphere.GetGasProperties),
            // while the cache normalizes by a stock altitude-only density, so any drift here is error a cell
            // accumulates between being filled and being read.
            ctx.Step("Probe one fixed state as the vessel flies on");
            double driftSec = ctx.ParamDouble("driftSec", 10);
            double driftUntilKm = ctx.ParamDouble("driftUntilKm", 55);
            var dcsv = new StringBuilder("missionTime,ut,vesselAltKm,vesselSpeed,vesselLat,vesselLon,probeDragKN,driftPctFromFirst\n");
            double first = double.NaN, nextDrift = 0, maxDrift = 0;
            if (physWarp > 1) Flight.PhysicsWarpRate(physWarp);
            yield return Wait.Until(() =>
            {
                var av = FlightGlobals.ActiveVessel;
                if (av == null || Flight.Dead) return true;
                if (av.missionTime >= nextDrift)
                {
                    nextDrift = av.missionTime + driftSec;
                    if (TrajApi.ComputeDirectAt(av, probeAoA, speed, alt, out Vector3d f))
                    {
                        double d = -Vector3d.Dot(f, ((Vector3d)av.srf_velocity).normalized);
                        if (double.IsNaN(first)) first = d;
                        double pct = Math.Abs(first) > 1e-6 ? 100.0 * (d - first) / Math.Abs(first) : double.NaN;
                        if (!double.IsNaN(pct)) maxDrift = Math.Max(maxDrift, Math.Abs(pct));
                        dcsv.Append(string.Join(",", new[] { av.missionTime.ToString("F1"),
                            Planetarium.GetUniversalTime().ToString("F1"), (av.altitude / 1000).ToString("F2"),
                            av.srfSpeed.ToString("F1"), av.latitude.ToString("F4"), av.longitude.ToString("F4"),
                            d.ToString("F4"), pct.ToString("F3") })).Append('\n');
                    }
                }
                return av.altitude < driftUntilKm * 1000 || (av.altitude < 90000 && av.srfSpeed < 2000);
            }, ctx.ParamDouble("driftTimeoutSec", 1800), "fixed-state probe until the end altitude");
            if (physWarp > 1) Flight.WarpOff();
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "fixed-state-drift.csv"), dcsv.ToString());
            ctx.Record("driftMaxPct", Math.Round(maxDrift, 3));
        }

        /// <summary>
        /// The cache's error at the states an entry actually passes through, split into its parts (see
        /// TrajApi.Decompose): staleness (cached vs freshly interpolated), interpolation (fresh vs truth), the
        /// share of each axis, and a pack/unpack round trip that must reproduce the truth exactly. Everything is
        /// in the cache's own frame, so nothing here depends on how the test reads lift and drag off a vessel.
        /// Params: points ("altKm:speed+..."), aoa, craft, settleSec, physWarp.
        /// </summary>
        [UITest(Requires = "RP-1,MechJeb", StartsAt = StartState.Vab, Craft = "Mk2 SSTO Big", TimeoutSec = 2400)]
        public static IEnumerator Traj_Decompose(TestContext ctx)
        {
            Ugui.DismissPopups();
            Assert.True(TrajApi.Available, "Trajectories is installed and its API resolved");
            string craft = ctx.Param("craft", "Mk2 SSTO Big");
            double aoa = ctx.ParamDouble("aoa", 40);
            string points = ctx.Param("points", "100.37:7365+95.21:7336+90.10:7269+85.14:7136+80.97:6902+78.14:6550+74.85:6086+70.74:5445+67.02:4657+62.31:3750+57.24:2781");

            yield return Nav.EnsureState(ctx, StartState.Vab, ctx.CraftPath(craft));
            Ugui.DismissPopups();
            ctx.Record("propellantDirect", Rp1.ApplyPropellantLoadDirect(ctx.ParamDouble("propellant", 0)));
            yield return Rp1.ConfigureSim(new SimSpec
            {
                StartInOrbit = true, Mode = "Reentry",
                PeKm = ctx.ParamDouble("peKm", 70), Arrival = "Apoapsis", ApKm = ctx.ParamDouble("apKm", 150),
                EntryAltKm = ctx.ParamDouble("startAltKm", 105), IncDeg = 0, LanDeg = 0, ArgPeDeg = 0,
                UT = ctx.ParamDouble("simUT", 2019800000) > 0 ? ctx.ParamDouble("simUT", 2019800000) : (double?)null,
                Attitude = "Prograde", LogTelemetry = false, PropellantPct = ctx.ParamDouble("propellant", 0),
            });
            yield return Rp1.StartSim();
            Ctrl.RetractGear();
            Ctrl.SetThrottle(0f);
            yield return Wait.Seconds(5);
            Ctrl.EnableRcs();
            FlightGlobals.ActiveVessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            Flight.IgnoreMaxTemperature(true);
            Flight.InfinitePropellant(true);
            // lockSurfaces=on: every control surface held flat before any cache cell is filled, so the
            // deflection a cell was filled with and the one it is read with are the same by construction.
            bool lockSurfaces = ctx.Param("lockSurfaces", "off").ToLowerInvariant() == "on";
            if (lockSurfaces) ctx.Record("surfacesLocked", FarApi.SetMaxDeflect(FlightGlobals.ActiveVessel, 0));
            ctx.Record("deflectionAtFillStartDeg", Math.Round(FarApi.MeanAbsDeflection(FlightGlobals.ActiveVessel), 3));
            TrajApi.AlwaysUpdate = true;
            TrajApi.UseCache = true;
            TrajApi.ResetProfile(aoa);
            Mj.SmartAssSurfaceProgradeOffset(aoa, roll: 0, retrograde: false);
            yield return Wait.Seconds(ctx.ParamDouble("settleSec", 30));

            ctx.Step("Decompose the cache error at each flown state");
            var v = FlightGlobals.ActiveVessel;
            ctx.Record("deflectionAtReadDeg", Math.Round(FarApi.MeanAbsDeflection(v), 3));
            ctx.Record("vesselAoAAtRead", Math.Round(Flight.AoA, 2));
            var csv = new StringBuilder("altKm,speed,mach,machFrac,aoaFrac,altFrac," +
                "truthX,cachedX,freshX,truthY,cachedY,freshY," +
                "liftCachedPct,liftStalePct,liftInterpPct,liftAoAPct,liftMachPct,liftAltPct,liftRoundTripPct," +
                "dragCachedPct,dragStalePct,dragInterpPct,dragAoAPct,dragMachPct,dragAltPct,dragRoundTripPct\n");
            Func<double, double, double> pct = (x, t) => Math.Abs(t) > 1e-9 ? 100.0 * (x - t) / Math.Abs(t) : double.NaN;
            int n = 0;
            string geometry = "";
            foreach (string p in points.Split('+'))
            {
                var parts = p.Split(':');
                if (parts.Length != 2) continue;
                double altM = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) * 1000;
                double speed = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                var d = TrajApi.Decompose(v, aoa, speed, altM);
                Assert.NotNull(d, "Decompose ran (the reworked Trajectories build is installed)");
                geometry = d.Geometry;
                csv.Append(string.Join(",", new[]
                {
                    (altM / 1000).ToString("F2"), speed.ToString("F0"), d.Mach.ToString("F3"),
                    d.MachFrac.ToString("F3"), d.AoAFrac.ToString("F3"), d.AltFrac.ToString("F3"),
                    d.Truth.X.ToString("F4"), d.Cached.X.ToString("F4"), d.Fresh.X.ToString("F4"),
                    d.Truth.Y.ToString("F4"), d.Cached.Y.ToString("F4"), d.Fresh.Y.ToString("F4"),
                    pct(d.Cached.Y, d.Truth.Y).ToString("F3"), pct(d.Cached.Y, d.Fresh.Y).ToString("F3"),
                    pct(d.Fresh.Y, d.Truth.Y).ToString("F3"), pct(d.AoAOnly.Y, d.Truth.Y).ToString("F3"),
                    pct(d.MachOnly.Y, d.Truth.Y).ToString("F3"), pct(d.AltOnly.Y, d.Truth.Y).ToString("F3"),
                    pct(d.RoundTrip.Y, d.Truth.Y).ToString("F3"),
                    pct(d.Cached.X, d.Truth.X).ToString("F3"), pct(d.Cached.X, d.Fresh.X).ToString("F3"),
                    pct(d.Fresh.X, d.Truth.X).ToString("F3"), pct(d.AoAOnly.X, d.Truth.X).ToString("F3"),
                    pct(d.MachOnly.X, d.Truth.X).ToString("F3"), pct(d.AltOnly.X, d.Truth.X).ToString("F3"),
                    pct(d.RoundTrip.X, d.Truth.X).ToString("F3"),
                })).Append('\n');
                n++;
                yield return null;
            }
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "decompose.csv"), csv.ToString());
            ctx.Record("cacheGeometry", geometry);
            ctx.Record("points", n);
        }

        /// <summary>One axis of the cache swept at a fixed reference state, cached force against direct.</summary>
        private static IEnumerator Sweep(TestContext ctx, Vessel v, string axis, double refAoA, double refSpeed, double refAlt)
        {
            double from, to, step;
            switch (axis)
            {
                case "aoa":   from = 0;    to = 70;     step = 0.25; break;
                case "speed": from = 500;  to = 8000;   step = 25;   break;
                default:      from = 5000; to = 135000; step = 250;  break;
            }

            var csv = new StringBuilder(axis + ",aoaDeg,speedMS,altM,cachedDragKN,directDragKN,dragErrPct,cachedLiftKN,directLiftKN,liftErrPct\n");
            int n = 0, failed = 0;
            double maxErr = 0, sumErr = 0;
            for (double x = from; x <= to + 1e-9; x += step)
            {
                double aoa = axis == "aoa" ? x : refAoA;
                double speed = axis == "speed" ? x : refSpeed;
                double alt = axis == "alt" ? x : refAlt;
                if (!TrajApi.SampleForcesAt(v, aoa, speed, alt, out Vector3d cached, out Vector3d direct)) { failed++; continue; }

                Vector3d velN = ((Vector3d)v.srf_velocity).normalized;
                double dragC = -Vector3d.Dot(cached, velN), dragD = -Vector3d.Dot(direct, velN);
                double liftC = (cached - Vector3d.Dot(cached, velN) * velN).magnitude;
                double liftD = (direct - Vector3d.Dot(direct, velN) * velN).magnitude;
                double dErr = Math.Abs(dragD) > 0.01 ? 100.0 * (dragC - dragD) / Math.Abs(dragD) : double.NaN;
                double lErr = Math.Abs(liftD) > 0.01 ? 100.0 * (liftC - liftD) / Math.Abs(liftD) : double.NaN;
                if (!double.IsNaN(dErr)) { maxErr = Math.Max(maxErr, Math.Abs(dErr)); sumErr += Math.Abs(dErr); n++; }

                csv.Append(string.Join(",", new[] { x.ToString("F2"), aoa.ToString("F2"), speed.ToString("F1"), alt.ToString("F0"),
                    dragC.ToString("F4"), dragD.ToString("F4"), dErr.ToString("F3"),
                    liftC.ToString("F4"), liftD.ToString("F4"), lErr.ToString("F3") })).Append('\n');
            }
            File.WriteAllText(Path.Combine(ctx.ResultsDir, "axis-" + axis + ".csv"), csv.ToString());
            ctx.Record(axis + ".points", n);
            ctx.Record(axis + ".failed", failed);
            ctx.Record(axis + ".maxDragErrPct", Math.Round(maxErr, 2));
            ctx.Record(axis + ".meanDragErrPct", n > 0 ? Math.Round(sumErr / n, 2) : 0);
            Log.Info($"axis {axis}: {n} points, max |dragErr| {maxErr:F2}%, mean {(n > 0 ? sumErr / n : 0):F2}%");
            yield return null;
        }
    }
}
