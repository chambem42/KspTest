using System;
using System.Collections;
using KspTest;
using KspTest.MechJeb;
using UnityEngine;

namespace KspTest.Tests.MechJeb
{
    /// <summary>
    /// Traj_CacheAB on stock Kerbin: the same measurement as the RP-1 test (TrajEntry.Sample - predicted impact
    /// drift, cached against direct force, prediction refresh), with the entry set up the way Far_Heating_Flight
    /// does it since there is no KCT simulation here: launch the plane on the runway, put it on an entry orbit
    /// with the stock set-orbit cheat, hold a constant AoA on SmartASS and warp down to the atmosphere. Two
    /// passes, cache on then off. Params: craft (SPH, default Aeris 4A), aoa (default 20), peKm (35), apKm (85),
    /// endAltKm (25: the Aeris 4A holds 20 deg to about 24 km, then runs out of pitch authority), sampleSec (5), physWarp (4), cache (on+off / on / off), flightTimeoutSec (2400).
    /// </summary>
    public static class TrajStockTests
    {
        [UITest("Traj_CacheAB_Stock", Requires = "MechJeb", StartsAt = StartState.Flight, Craft = "Aeris 4A", CraftDir = "SPH", LaunchSite = "Runway", TimeoutSec = 6000)]
        public static IEnumerator Traj_CacheAB_Stock(TestContext ctx)
        {
            Assert.True(TrajApi.Available, "Trajectories is installed and its API resolved");
            ctx.Record("trajectoriesVersion", TrajApi.Version);
            // rhoFix=on/off: the stock model's position density correction (builds that have it). Unset leaves the
            // build's default.
            string rhoFix = ctx.Param("rhoFix");
            if (rhoFix != null) TrajApi.StockPositionDensity = rhoFix.Equals("on", StringComparison.OrdinalIgnoreCase);
            ctx.Record("stockPositionDensity", TrajApi.StockPositionDensity.HasValue ? (object)TrajApi.StockPositionDensity.Value : "n/a");
            // trim=on/off: evaluate control surfaces at the trim deflection per predicted state (builds that have it).
            string trim = ctx.Param("trim");
            if (trim != null) TrajApi.TrimControlSurfaces = trim.Equals("on", StringComparison.OrdinalIgnoreCase);
            ctx.Record("trimControlSurfaces", TrajApi.TrimControlSurfaces.HasValue ? (object)TrajApi.TrimControlSurfaces.Value : "n/a");
            string which = (ctx.Param("cache", "on+off") ?? "on+off").ToLowerInvariant();
            if (which.Contains("on")) yield return RunPass(ctx, true);
            if (which.Contains("off")) yield return RunPass(ctx, false);
        }

        /// <summary>
        /// A whole entry to the ground flying a varying descent profile, with Trajectories itself holding it: its
        /// "Hold AoA with MechJeb SmartASS" option keeps SmartASS's surface-prograde pitch on the profile's angle for
        /// the current altitude. Since the vehicle then flies what the prediction assumes all the way down, every
        /// predicted impact can be compared with where it really lands. Params: profile ("entry/high/low/final" deg,
        /// an "h" suffix for relative to the horizon; default 20/20/5/0h - the Aeris cannot hold 20 below ~24 km nor
        /// 10 subsonic, the high band blends down to the low node over 35-17.5 km on Kerbin, and the final approach
        /// wants a pitch, not an AoA), craft, peKm, apKm, cache (on/off), trim, seed, rhoFix, crossAltKm (default 30),
        /// sampleSec, physWarp, flightTimeoutSec.
        /// </summary>
        [UITest("Traj_ProfileToGround_Stock", Requires = "MechJeb", StartsAt = StartState.Flight, Craft = "Aeris 4A", CraftDir = "SPH", LaunchSite = "Runway", TimeoutSec = 6000)]
        public static IEnumerator Traj_ProfileToGround_Stock(TestContext ctx) => ProfileToGround(ctx, 0);

        /// <summary>
        /// Traj_ProfileToGround_Stock with the Descent page's "Adjust AoA to stay on target": at steerAtKm (default
        /// 60) the Target page's "current impact" button sets the target where the prediction then lands, the
        /// checkbox goes on, and the vessel flies to the ground. Records where it touched down against the target
        /// (along and across the track) and the AoA bias it took. Params as Traj_ProfileToGround_Stock, plus
        /// steerAtKm.
        /// </summary>
        [UITest("Traj_SteerToTarget_Stock", Requires = "MechJeb", StartsAt = StartState.Flight, Craft = "Aeris 4A", CraftDir = "SPH", LaunchSite = "Runway", TimeoutSec = 6000)]
        public static IEnumerator Traj_SteerToTarget_Stock(TestContext ctx) => ProfileToGround(ctx, ctx.ParamDouble("steerAtKm", 60));

        private static IEnumerator ProfileToGround(TestContext ctx, double steerAtKm)
        {
            var plan = TrajProfileFlight.Setup(ctx, "20/20/5/0h");
            string k = plan.K;
            double peKm = ctx.ParamDouble("peKm", 35), apKm = ctx.ParamDouble("apKm", 85);
            double physWarp = ctx.ParamDouble("physWarp", 4);

            ctx.Step("On the runway, then the entry orbit");
            yield return Nav.EnsureState(ctx, StartState.Flight, ctx.CraftPath(ctx.Param("craft", "Aeris 4A"), "SPH"), "Runway");
            Assert.True(Mj.Present, "vessel has a MechJebCore (MechJebForAll or an MJ part)");
            Flight.IgnoreMaxTemperature(true);
            Flight.InfinitePropellant(true);
            // fuel=empty (default) / full: a spaceplane comes home with its tanks dry, and flies very differently
            // (lighter, CoM moved) from the full craft as built. Monopropellant stays for the RCS.
            bool emptyTanks = !(ctx.Param("fuel", "empty") ?? "empty").Equals("full", StringComparison.OrdinalIgnoreCase);
            {
                var pv = FlightGlobals.ActiveVessel;
                ctx.Record(k + "massFullT", Math.Round(pv.GetTotalMass(), 3));
                if (emptyTanks)
                    foreach (Part p in pv.parts)
                        foreach (PartResource r in p.Resources)
                            if (r.resourceName == "LiquidFuel" || r.resourceName == "Oxidizer")
                                r.amount = 0;
                ctx.Record(k + "fuel", emptyTanks ? "empty" : "full");
            }
            yield return Wait.Seconds(1);
            ctx.Record(k + "massT", Math.Round(FlightGlobals.ActiveVessel.GetTotalMass(), 3));
            Flight.SetOrbit(peKm * 1000, apKm * 1000);
            yield return Wait.Until(() => FlightGlobals.ActiveVessel.altitude > apKm * 1000 * 0.9, 30, "vessel at the apoapsis");
            yield return Wait.Seconds(2);
            var v = FlightGlobals.ActiveVessel;
            int partsStart = v.parts.Count;
            ctx.Record(k + "gearUp", Ctrl.RetractGear());
            Ctrl.SetThrottle(0f);
            v.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            v.ActionGroups.SetGroup(KSPActionGroup.RCS, true);

            yield return TrajProfileFlight.Engage(ctx, plan);
            double atmoTop = v.mainBody.atmosphereDepth;
            Flight.Warp(3);
            yield return Wait.Until(() => FlightGlobals.ActiveVessel.altitude < atmoTop + 5000, 900, "approach the atmosphere", shot: false);
            Flight.WarpOff();
            yield return Wait.Until(() => TimeWarp.CurrentRate <= 1.01, 30, "off rails", shot: false);
            if (physWarp > 1) Flight.PhysicsWarpRate(physWarp);

            yield return TrajProfileFlight.Fly(ctx, plan, steerAtKm, partsStart, physWarp, 30, 5, 3000);
        }

        /// <summary>
        /// FAR's prediction atmosphere against the game's (builds with FARAPI.CalculateVesselAeroForcesAt). On the runway,
        /// the predicted lift at the vessel's latitude, at 0/90/180/270 deg of longitude away and at several altitudes:
        /// its ratio to the lift at the vessel's own longitude should follow the game's density ratio between the
        /// points (Trajectories' StockAeroUtil, the FlightIntegrator formula), at a speed low enough (liftSpeed, 40 m/s)
        /// that the few percent of temperature between points moves the lift coefficient by far less than that; and a
        /// quarter rotation later at the vessel's longitude, which should equal a quarter turn east now. Records FAR's
        /// public gas density (FARAtmosphere.GetGasProperties) against vessel.atmDensity and the game's: on a FAR whose
        /// default atmosphere is the altitude curve again (predictions alone take the game's) it is not asserted.
        /// Params: speed (150 m/s, for the quarter-turn check), liftSpeed (40 m/s), aoa (5 deg), alts ("100,10000,30000").
        /// </summary>
        [UITest("Far_PredictionAtmosphere_Flight", StartsAt = StartState.Flight, Craft = "Aeris 4A", CraftDir = "SPH", LaunchSite = "Runway", TimeoutSec = 600)]
        public static IEnumerator Far_PredictionAtmosphere_Flight(TestContext ctx)
        {
            yield return Wait.Seconds(10);   // FAR voxelizes the craft after load
            Assert.True(FarApi.HasPredictAt, "FAR has FARAPI.CalculateVesselAeroForcesAt");
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var v = FlightGlobals.ActiveVessel;
            CelestialBody body = v.mainBody;
            double now = Planetarium.GetUniversalTime();

            double farRho = FarApi.FarPredictionDensity(v);
            ctx.Record("farPublicRhoOverAtmDensity", Math.Round(farRho / v.atmDensity, 5));

            Vector3 vel = FarSim.BodyVelocity(v, ctx.ParamDouble("speed", 150), ctx.ParamDouble("aoa", 5), 0);
            Vector3 slow = FarSim.BodyVelocity(v, ctx.ParamDouble("liftSpeed", 40), ctx.ParamDouble("aoa", 5), 0);
            double lat = v.latitude, lon = v.longitude;
            double worstRho = 0, worstOld = 0, worstRepeat = 0, worstTurn = 0, worstLift = 0;
            foreach (string s in (ctx.Param("alts") ?? "100,10000,30000").Split(','))
            {
                double alt = double.Parse(s.Trim(), inv);
                double oldRho = body.GetDensity(body.GetPressure(alt), body.GetTemperature(alt));
                double lift0 = double.NaN, rho0 = double.NaN, slow0 = double.NaN;
                for (int dLon = 0; dLon <= 360; dLon += 90)
                {
                    Assert.True(FarApi.PredictAt(v, vel, lat, lon + dLon, alt, now, out double lift, out _), "CalculateVesselAeroForcesAt");
                    FarApi.PredictAt(v, slow, lat, lon + dLon, alt, now, out double slowLift, out _);
                    double game = TrajApi.StockDensityAt(body, body.GetWorldSurfacePosition(lat, lon + dLon, alt));
                    double far = FarApi.FarDensityAt(body, lat, lon + dLon, alt, now);
                    if (dLon == 0) { lift0 = lift; rho0 = game; slow0 = slowLift; }
                    worstRho = Math.Max(worstRho, Math.Abs(far / game - 1));
                    worstLift = Math.Max(worstLift, Math.Abs(slowLift / slow0 / (game / rho0) - 1));
                    worstOld = Math.Max(worstOld, Math.Abs(oldRho / game - 1));
                    if (dLon == 360) worstRepeat = Math.Max(worstRepeat, Math.Abs(lift / lift0 - 1));
                    ctx.Record($"alt{alt:F0}.dLon{dLon}", $"FAR public/game density {far / game:F5} (altitude curve {oldRho / game:F4}); lift x{lift / lift0:F4}, at {ctx.ParamDouble("liftSpeed", 40):F0} m/s x{slowLift / slow0:F5}, vs density x{game / rho0:F5}");
                }
                if (body.rotates && body.rotationPeriod != 0)
                {
                    FarApi.PredictAt(v, vel, lat, lon, alt, now + body.rotationPeriod / 4, out double later, out _);
                    FarApi.PredictAt(v, vel, lat, lon + 90, alt, now, out double east, out _);
                    worstTurn = Math.Max(worstTurn, Math.Abs(later / east - 1));
                }
            }
            ctx.Record("worstFarPublicVsGameDensityPct", Math.Round(worstRho * 100, 4));
            ctx.Record("worstSlowLiftVsDensityPct", Math.Round(worstLift * 100, 4));
            ctx.Record("worstOldAtmosphereErrPct", Math.Round(worstOld * 100, 3));
            // Not asserted: the first call at a new altitude differs from a repeat because downstream wings read their
            // upstream wing's lift slope, stall, sweep and Cd0 from its last calculation (FARWingInteraction), whatever
            // state that was at. A separate FAR issue from the atmosphere.
            ctx.Record("worstRepeatPct", Math.Round(worstRepeat * 100, 4));
            ctx.Record("worstQuarterTurnVsEastPct", Math.Round(worstTurn * 100, 4));
            Assert.True(worstLift < 0.002, $"FAR's predicted lift follows the game's density between points (worst {worstLift * 100:F3}%)");
            Assert.True(worstTurn < 1e-3, $"a quarter turn later matches 90 deg east now (worst {worstTurn * 100:F3}%)");
        }

        private static IEnumerator RunPass(TestContext ctx, bool useCache)
        {
            string k = useCache ? "cacheOn." : "cacheOff.";
            string pass = useCache ? "cache on" : "cache off";
            string craft = ctx.Param("craft", "Aeris 4A");
            double aoa = ctx.ParamDouble("aoa", 20);
            double peKm = ctx.ParamDouble("peKm", 35), apKm = ctx.ParamDouble("apKm", 85);
            double physWarp = ctx.ParamDouble("physWarp", 4);

            ctx.Step($"[{pass}] {craft} on the runway");
            yield return Nav.EnsureState(ctx, StartState.Flight, ctx.CraftPath(craft, "SPH"), "Runway");
            Assert.True(Mj.Present, "vessel has a MechJebCore (MechJebForAll or an MJ part)");
            Flight.IgnoreMaxTemperature(true);
            Flight.InfinitePropellant(true);

            ctx.Step($"[{pass}] Set the entry orbit");
            Flight.SetOrbit(peKm * 1000, apKm * 1000);
            yield return Wait.Until(() => FlightGlobals.ActiveVessel.altitude > apKm * 1000 * 0.9, 30, "vessel at the apoapsis");
            yield return Wait.Seconds(2);
            var v = FlightGlobals.ActiveVessel;
            int partsStart = v.parts.Count;
            ctx.Record(k + "apoapsisKm", Math.Round(v.orbit.ApA / 1000, 1));
            ctx.Record(k + "periapsisKm", Math.Round(v.orbit.PeA / 1000, 1));
            ctx.Record(k + "gearUp", Ctrl.RetractGear());
            Ctrl.SetThrottle(0f);
            v.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            v.ActionGroups.SetGroup(KSPActionGroup.RCS, true);

            ctx.Step($"[{pass}] Constant {aoa} deg profile, hold it, warp to the atmosphere");
            TrajEntry.ConfigureProfile(ctx, k, useCache, aoa);
            Mj.SmartAssSurfaceProgradeOffset(aoa);
            yield return Wait.Seconds(5);
            double atmoTop = v.mainBody.atmosphereDepth;
            Flight.Warp(3);
            yield return Wait.Until(() => FlightGlobals.ActiveVessel.altitude < atmoTop + 5000, 900, "approach the atmosphere", shot: false);
            Flight.WarpOff();
            yield return Wait.Until(() => TimeWarp.CurrentRate <= 1.01, 30, "off rails", shot: false);
            Mj.SmartAssSurfaceProgradeOffset(aoa);
            if (physWarp > 1) Flight.PhysicsWarpRate(physWarp);

            ctx.Step($"[{pass}] Entry");
            yield return TrajEntry.Sample(ctx, k, aoa, ctx.ParamDouble("sampleSec", 5),
                new TrajEntry.EntryOptions { EndAltKm = ctx.ParamDouble("endAltKm", 25), SlowBelowAltKm = 0, SlowSpeed = 0, CrossAltKm = ctx.ParamDouble("crossAltKm", 30) },
                ctx.ParamDouble("flightTimeoutSec", 2400), partsStart, physWarp);
            try { Mj.SmartAssOff(); } catch (UITestFailure) { }
        }
    }
}
