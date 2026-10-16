using System;
using System.Collections;

namespace KspTest.MechJeb
{
    /// <summary>
    /// A whole entry to the ground flying a Trajectories descent profile with Trajectories itself holding it through
    /// SmartASS, optionally steering to a target set from the current impact on the way down. Shared by the stock
    /// (Aeris, orbit cheat) and RP-1 (SSTO, KCT simulation) tests, which only differ in how they get the vessel to
    /// the top of the atmosphere.
    ///
    /// Params read here: profile ("entry/high/low/final" deg, "h" after an angle for relative to the horizon, "s" after
    /// a number for an airspeed in m/s held by pitch - the node's speed mode),
    /// cache (on/off), trim, seed, rhoFix, crossAltKm, sampleSec, flightTimeoutSec.
    /// </summary>
    public static class TrajProfileFlight
    {
        public sealed class Plan
        {
            public string[] Profile;
            public bool[] Horizon;
            public double[] SpeedMs;   // speed mode's airspeed per node, NaN for an angle
            public double Entry, High, Low, Final;
            public bool UseCache;
            public string K;
        }

        /// <summary>Applies the Trajectories options the params ask for and parses the profile.</summary>
        public static Plan Setup(TestContext ctx, string defaultProfile)
        {
            Assert.True(TrajApi.Available, "Trajectories is installed and its API resolved");
            string rhoFix = ctx.Param("rhoFix");
            if (rhoFix != null) TrajApi.StockPositionDensity = rhoFix.Equals("on", StringComparison.OrdinalIgnoreCase);
            string trim = ctx.Param("trim");
            if (trim != null) TrajApi.TrimControlSurfaces = trim.Equals("on", StringComparison.OrdinalIgnoreCase);
            ctx.Record("trimControlSurfaces", TrajApi.TrimControlSurfaces.HasValue ? (object)TrajApi.TrimControlSurfaces.Value : "n/a");
            // seed=on/off: start the non-aerodynamic pitch torque from the wheels and RCS instead of waiting to learn it.
            string seed = ctx.Param("seed");
            if (seed != null) TrajApi.SeedOtherTorque = seed.Equals("on", StringComparison.OrdinalIgnoreCase);
            ctx.Record("seedOtherTorque", TrajApi.SeedOtherTorque.HasValue ? (object)TrajApi.SeedOtherTorque.Value : "n/a");
            // bgFill / bgPrefetch = on/off, bgWorkers = n: the force cache's background fill from FAR snapshots, on
            // builds that have it (explore/mt-predictions).
            string bgFill = ctx.Param("bgFill"), bgPrefetch = ctx.Param("bgPrefetch");
            if (bgFill != null) TrajApi.SetStaticBool("Trajectories.AeroForceCache", "BackgroundFill", bgFill.Equals("on", StringComparison.OrdinalIgnoreCase));
            if (bgPrefetch != null) TrajApi.SetStaticBool("Trajectories.AeroForceCache", "BackgroundPrefetch", bgPrefetch.Equals("on", StringComparison.OrdinalIgnoreCase));
            double bgWorkers = ctx.ParamDouble("bgWorkers", double.NaN);
            if (!double.IsNaN(bgWorkers))
                TrajApi.SetStaticInt("Trajectories.AeroForceCache", "MaxWorkers", (int)bgWorkers);
            // bgVerify = n: every nth fill context, compare a grid of cells filled on the main thread, by the same
            // bank-of-inputs trim from live evaluations, and from the context's snapshots (diagnostics).
            double bgVerify = ctx.ParamDouble("bgVerify", double.NaN);
            if (!double.IsNaN(bgVerify))
                TrajApi.SetStaticInt("Trajectories.AeroDynamicModel", "VerifyEveryNthContext", (int)bgVerify);
            bool? bg = TrajApi.StaticBool("Trajectories.AeroForceCache", "BackgroundFill");
            ctx.Record("backgroundFill", bg.HasValue ? (object)bg.Value : "n/a");
            bool? pf = TrajApi.StaticBool("Trajectories.AeroForceCache", "BackgroundPrefetch");
            ctx.Record("backgroundPrefetch", pf.HasValue ? (object)pf.Value : "n/a");
            double workers = TrajApi.StaticDouble("Trajectories.AeroForceCache", "MaxWorkers");
            ctx.Record("backgroundMaxWorkers", double.IsNaN(workers) ? (object)"n/a" : workers);

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string[] prof = (ctx.Param("profile", defaultProfile) ?? defaultProfile).Split('/');
            Assert.True(prof.Length == 4, "profile has four angles (entry/high/low/final)");
            bool[] speed = Array.ConvertAll(prof, s => s.Trim().EndsWith("s", StringComparison.OrdinalIgnoreCase));
            double[] num = Array.ConvertAll(prof, s => double.Parse(s.Trim().TrimEnd('h', 'H', 's', 'S'), inv));
            double[] ang = new double[4], speedMs = new double[4];
            for (int i = 0; i < 4; ++i)
            {
                ang[i] = speed[i] ? 0.0 : num[i];
                speedMs[i] = speed[i] ? num[i] : double.NaN;
            }
            bool useCache = !(ctx.Param("cache", "on") ?? "on").Equals("off", StringComparison.OrdinalIgnoreCase);
            return new Plan
            {
                Profile = prof,
                Horizon = Array.ConvertAll(prof, s => s.Trim().EndsWith("h", StringComparison.OrdinalIgnoreCase)),
                SpeedMs = speedMs,
                Entry = ang[0], High = ang[1], Low = ang[2], Final = ang[3],
                UseCache = useCache,
                K = useCache ? "cacheOn." : "cacheOff.",
            };
        }

        /// <summary>Sets the profile and switches on Trajectories' SmartASS hold of it.</summary>
        public static IEnumerator Engage(TestContext ctx, Plan p)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            ctx.Step($"Profile {string.Join("/", p.Profile)}, held by Trajectories through SmartASS");
            TrajEntry.ConfigureProfile(ctx, p.K, p.UseCache, p.Entry);
            TrajApi.SetProfileDeg(p.Entry, p.High, p.Low, p.Final, p.Horizon);
            if (p.SpeedMs != null && Array.Exists(p.SpeedMs, s => !double.IsNaN(s)))
                Assert.True(TrajApi.SetProfileSpeeds(p.SpeedMs), "Trajectories has the descent profile's speed mode");
            // nodeAltKm=entry/high/low/final (km, blank for the default): where each node's angle applies in full
            // (DescentProfile.SetNodeAltitudeM), set final first so a lower one is never refused for crossing it
            string nodeAlt = ctx.Param("nodeAltKm");
            if (!string.IsNullOrEmpty(nodeAlt))
            {
                string[] km = nodeAlt.Split('/');
                Assert.True(km.Length == 4, "nodeAltKm has four altitudes (entry/high/low/final)");
                var set = TrajApi.TryFindTypePublic("Trajectories.DescentProfile")?.GetMethod("SetNodeAltitudeM",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                Assert.True(set != null, "Trajectories has DescentProfile.SetNodeAltitudeM");
                CelestialBody body = FlightGlobals.ActiveVessel.mainBody;
                var done = new string[4];
                for (int i = 3; i >= 0; --i)
                {
                    double m = string.IsNullOrWhiteSpace(km[i]) ? double.NaN : double.Parse(km[i].Trim(), inv) * 1000.0;
                    bool ok = (bool)set.Invoke(null, new object[] { body, i, m });
                    done[i] = ok ? km[i].Trim() : km[i].Trim() + "(refused)";
                }
                ctx.Record(p.K + "nodeAltKm", string.Join("/", done));
            }
            ctx.Record(p.K + "profileModesAoA", string.Join("/", Array.ConvertAll(TrajApi.ProfileModesAoA, m => m ? "AoA" : "Horiz")));
            ctx.Record(p.K + "profileAnglesDeg", string.Join("/", Array.ConvertAll(TrajApi.ProfileAnglesDeg, d => Math.Round(d, 1).ToString(inv))));
            // SmartASS on surface prograde with the roll held level; from here Trajectories moves its pitch.
            Assert.True(Mj.Present, "vessel has a MechJebCore (MechJebForAll or an MJ part)");
            // Heat shield first: surface retrograde, turned over to roof down, as Trajectories' own hold will command.
            if (Math.Abs(p.Entry) > 90)
                Mj.SmartAssSurfaceProgradeOffset(p.Entry > 0 ? p.Entry - 180 : p.Entry + 180, roll: 180, retrograde: true);
            else
                Mj.SmartAssSurfaceProgradeOffset(p.Entry);
            // hold=harness: the harness flies the profile through SmartASS (ProfileHold), for builds without their own
            // hold such as upstream 2.4.5.4
            if ((ctx.Param("hold", "trajectories") ?? "").Equals("harness", StringComparison.OrdinalIgnoreCase))
            {
                TrajApi.HoldWithSmartASS = false;
                ProfileHold.Begin();
            }
            else
            {
                TrajApi.HoldWithSmartASS = true;
                Assert.True(TrajApi.HoldWithSmartASS == true, "Trajectories' SmartASS hold switched on (needs the build with it, and MechJeb)");
            }
            yield return Wait.Seconds(5);
        }

        /// <summary>
        /// From inside the atmosphere to the ground: at steerAtKm (0 = never) the Target page's current-impact button
        /// sets the target and the Descent page's steer-to-target checkbox goes on; then samples to touchdown and
        /// records where it came down against the target.
        /// </summary>
        public static IEnumerator Fly(TestContext ctx, Plan p, double steerAtKm, int partsStart, double physWarp,
                                      double crossAltKmDefault, double sampleSecDefault, double flightTimeoutDefault,
                                      bool targetPreset = false, bool steer = true, double[] dumpAtKm = null)
        {
            string k = p.K;
            var v = FlightGlobals.ActiveVessel;
            Vector3d target = Vector3d.zero, steerStart = Vector3d.zero;
            if (targetPreset)
            {
                // The target is already set (a place, not the current impact): steer to it from here, or with steer
                // off only measure against it.
                ctx.Step(steer ? "Steer to the preset target" : "Fly open loop against the preset target");
                Assert.True(TrajApi.TargetLatLonAlt.HasValue, "a target is set");
                target = TrajApi.TargetLatLonAlt.Value;
                ctx.Record(k + "targetLat", Math.Round(target.x, 5));
                ctx.Record(k + "targetLon", Math.Round(target.y, 5));
                ctx.Record(k + "targetSetAtKm", Math.Round(v.altitude / 1000, 1));
                steerStart = new Vector3d(v.latitude, v.longitude, 0);
                // a build without steering (upstream 2.4.5.4) has no checkbox, which is the same as off
                if (steer || TrajApi.SteerToTarget.HasValue)
                {
                    Assert.True(TrajApi.SetSteerToTarget(steer), "Descent page's steer-to-target checkbox");
                    Assert.True(TrajApi.SteerToTarget == steer, "steering to the target is " + (steer ? "on" : "off"));
                }
                steerAtKm = 1e9;   // for the touchdown records below
            }
            else if (steerAtKm > 0)
            {
                ctx.Step($"Target the current impact at {steerAtKm} km and steer to it");
                yield return Wait.Until(() => FlightGlobals.ActiveVessel.altitude < steerAtKm * 1000, 3600, $"below {steerAtKm} km", shot: false);
                Assert.True(TrajApi.SetTargetFromImpact(), "Target page's current-impact button");
                Assert.True(TrajApi.TargetLatLonAlt.HasValue, "a target is set");
                target = TrajApi.TargetLatLonAlt.Value;
                ctx.Record(k + "targetLat", Math.Round(target.x, 5));
                ctx.Record(k + "targetLon", Math.Round(target.y, 5));
                ctx.Record(k + "targetSetAtKm", Math.Round(v.altitude / 1000, 1));
                steerStart = new Vector3d(v.latitude, v.longitude, 0);
                Assert.True(TrajApi.SetSteerToTarget(true), "Descent page's steer-to-target checkbox");
                Assert.True(TrajApi.SteerToTarget == true, "steering to the target is on");
            }

            // handoffAltKm > 0: stop there for a hand landing, with the hold and steering left on for the pilot to
            // switch off (run with -Keep).
            double handoffKm = ctx.ParamDouble("handoffAltKm", 0);
            // crossAboveTarget=on: crossAltKm measured above the target's ground (as Trajectories measures its final
            // approach node), not above sea level
            double crossKm = ctx.ParamDouble("crossAltKm", crossAltKmDefault);
            Vector3d? targetLla = TrajApi.TargetLatLonAlt;
            if (targetLla.HasValue)
            {
                ctx.Record(p.K + "targetGroundKm", Math.Round(targetLla.Value.z / 1000, 3));
                if ((ctx.Param("crossAboveTarget", "off") ?? "off").Equals("on", StringComparison.OrdinalIgnoreCase))
                    crossKm += targetLla.Value.z / 1000;
            }
            var nodeHeight = TrajApi.TryFindTypePublic("Trajectories.DescentProfile")?.GetMethod("NodeHeightM",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                null, new[] { typeof(CelestialBody), typeof(int) }, null);
            var nodeAltitude = TrajApi.TryFindTypePublic("Trajectories.DescentProfile")?.GetMethod("NodeAltitudeM",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                null, new[] { typeof(CelestialBody), typeof(int) }, null);
            if (nodeHeight != null && nodeAltitude != null)
            {
                object[] a = { FlightGlobals.ActiveVessel.mainBody, 3 };
                ctx.Record(p.K + "finalNodeKm", string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.###} above the target, {1:0.###} above sea level",
                    Convert.ToDouble(nodeHeight.Invoke(null, a)) / 1000, Convert.ToDouble(nodeAltitude.Invoke(null, a)) / 1000));
            }
            ctx.Step(handoffKm > 0 ? $"Entry down to {handoffKm} km, then over to the pilot" : "Entry to the ground");
            yield return TrajEntry.Sample(ctx, k, p.Entry, ctx.ParamDouble("sampleSec", sampleSecDefault),
                new TrajEntry.EntryOptions
                {
                    ToGround = handoffKm <= 0, EndAltKm = handoffKm > 0 ? handoffKm : -1, SlowBelowAltKm = 0, SlowSpeed = 0,
                    CrossAltKm = crossKm,
                    TargetAoADeg = TrajApi.ProfileAoADeg,
                    DumpAtKm = dumpAtKm,
                },
                ctx.ParamDouble("flightTimeoutSec", flightTimeoutDefault), partsStart, physWarp);
            v = FlightGlobals.ActiveVessel;
            if (handoffKm > 0)
            {
                Flight.WarpOff();
                ctx.Record(k + "handoffAltKm", Math.Round(v.altitude / 1000, 2));
                ctx.Record(k + "handoffSpeedMs", Math.Round(v.srfSpeed, 0));
                ctx.Record(k + "handoffAlongMissKm", Math.Round(TrajApi.StaticDouble("Trajectories.TargetSteer", "AlongTrackMissM") / 1000, 2));
                ctx.Record(k + "handoffCrossMissKm", Math.Round(TrajApi.StaticDouble("Trajectories.TargetSteer", "CrossTrackMissM") / 1000, 2));
                ctx.Step("Yours: Trajectories' hold and steering are still on - switch them off to fly it in");
                yield break;
            }
            ctx.Record(k + "seedPitchTorqueKNm", Math.Round(TrajApi.ModelDouble("SeedPitchTorque"), 2));
            ctx.Record(k + "learnedPitchTorqueKNm", Math.Round(TrajApi.OtherPitchTorque, 2));
            // Force cache fill counters (NaN on builds without them): main-thread fills of empty and expired cells and
            // their time, and the background fill's.
            foreach (string c in new[] { "FillsEmpty", "FillsExpired", "FillMs", "BackgroundQueued", "BackgroundApplied", "BackgroundFailed", "BackgroundMs" })
                ctx.Record(k + "cache" + c, Math.Round(TrajApi.StaticDouble("Trajectories.AeroForceCache", c), 1));
            foreach (string c in new[] { "FillContextsCaptured", "FillContextsRefused", "VerifyCells" })
                ctx.Record(k + c, TrajApi.StaticDouble("Trajectories.AeroDynamicModel", c));
            foreach (string c in new[] { "VerifyInterpMeanPct", "VerifyInterpMaxPct", "VerifySnapMeanPct", "VerifySnapMaxPct", "VerifyTotalMeanPct" })
                ctx.Record(k + c, Math.Round(TrajApi.StaticDouble("Trajectories.AeroDynamicModel", c), 4));
            if (steerAtKm > 0)
            {
                // Where it came down against the target, split along and across the course from where steering began
                // to the target (the vessel has all but stopped by now, so its own velocity gives no direction):
                // long > 0 is beyond the target, right > 0 is right of the course.
                CelestialBody body = v.mainBody;
                Vector3d here = body.GetWorldSurfacePosition(v.latitude, v.longitude, 0) - body.position;
                Vector3d there = body.GetWorldSurfacePosition(target.x, target.y, 0) - body.position;
                Vector3d start = body.GetWorldSurfacePosition(steerStart.x, steerStart.y, 0) - body.position;
                Vector3d up = there.normalized;
                Vector3d course = Vector3d.Exclude(up, there - start).normalized;
                // Unity is left-handed: Cross(up, forward) is right.
                Vector3d right = Vector3d.Cross(up, course);
                Vector3d miss = Vector3d.Exclude(up, here - there);
                ctx.Record(k + "touchdownToTargetKm", Math.Round(miss.magnitude / 1000, 2));
                ctx.Record(k + "touchdownLongKm", Math.Round(Vector3d.Dot(miss, course) / 1000, 2));
                ctx.Record(k + "touchdownRightKm", Math.Round(Vector3d.Dot(miss, right) / 1000, 2));
                ctx.Record(k + "finalAoABiasDeg", Math.Round(TrajApi.StaticDouble("Trajectories.DescentProfile", "AoABiasRad") * 180 / Math.PI, 2));
                ctx.Record(k + "finalBankDeg", Math.Round(TrajApi.StaticDouble("Trajectories.DescentProfile", "BankRad") * 180 / Math.PI, 2));
                ctx.Record(k + "slopeMPerDeg", Math.Round(TrajApi.StaticDouble("Trajectories.TargetSteer", "SlopeMPerDeg"), 0));
                ctx.Record(k + "bankSlopeMPerDeg", Math.Round(TrajApi.StaticDouble("Trajectories.TargetSteer", "BankSlopeMPerDeg"), 0));
                ctx.Record(k + "steerMode", TrajApi.StaticString("Trajectories.TargetSteer", "Mode") ?? "n/a");
                TrajApi.SetSteerToTarget(false);
            }
            TrajApi.HoldWithSmartASS = false;
            ProfileHold.End();
            try { Mj.SmartAssOff(); } catch (UITestFailure) { }
        }
    }
}
