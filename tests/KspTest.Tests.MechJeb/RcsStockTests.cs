using System.Collections;
using KspTest;
using KspTest.MechJeb;

namespace KspTest.Tests.MechJeb
{
    public static class RcsStockTests
    {
        /// <summary>
        /// Rcs_RP1_OrbitHold on a stock install: attitude hold in orbit at high warp with the RcsInputPerStep patch on
        /// and off (RcsHold.Run: segments, hold, patch and the rest of its params). The stock Aeris 4A (craft) is
        /// launched to the runway and put on a circular Kerbin orbit (altKm, default 100: above the air; inc, default 0),
        /// RCS on, propellant infinite. wheels (default off): stock reaction wheels hold this craft with commands under
        /// RCS's 0.05 deadband, so RCS never fires; off, RCS is the only attitude control, as on a vessel whose wheels
        /// can't keep up (RO's).
        /// </summary>
        [UITest(Requires = "MechJeb", StartsAt = StartState.Flight, Craft = "Aeris 4A", CraftDir = "SPH", LaunchSite = "Runway", TimeoutSec = 7200)]
        public static IEnumerator Rcs_Stock_OrbitHold(TestContext ctx)
        {
            double altKm = ctx.ParamDouble("altKm", 100), inc = ctx.ParamDouble("inc", 0);
            var v = FlightGlobals.ActiveVessel;
            Ctrl.SetThrottle(0f);
            v.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            Flight.IgnoreMaxTemperature(true);
            Flight.InfinitePropellant(true);
            TrajApi.HoldWithSmartASS = false;

            ctx.Step("Into orbit");
            Flight.SetOrbitAtMeanAnomaly(altKm * 1000, altKm * 1000, inc, 0, 0, 0);
            yield return Wait.Until(() => FlightGlobals.ActiveVessel != null && !FlightGlobals.ActiveVessel.packed
                                          && FlightGlobals.ActiveVessel.altitude > altKm * 900, 120, "in orbit and off rails");
            v = FlightGlobals.ActiveVessel;
            ctx.Record("gearUp", Ctrl.RetractGear());
            ctx.Record("rcsThrusters", Ctrl.EnableRcs());
            int wheelsOff = 0;
            if (ctx.Param("wheels", "off").Equals("off", System.StringComparison.OrdinalIgnoreCase))
                foreach (Part p in v.parts)
                    foreach (ModuleReactionWheel w in p.FindModulesImplementing<ModuleReactionWheel>())
                    {
                        w.State = ModuleReactionWheel.WheelState.Disabled;
                        ++wheelsOff;
                    }
            ctx.Record("reactionWheelsDisabled", wheelsOff);
            Flight.PointAtAoA(0);
            yield return Wait.Seconds(2);
            yield return RcsHold.Run(ctx);
        }
    }
}
